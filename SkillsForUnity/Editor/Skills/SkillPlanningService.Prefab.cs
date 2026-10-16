using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        // ==================================================================================
        // Prefab semantic planner
        // ==================================================================================

        private static void AnalyzePrefabAssetEditing(string name, SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var planned = new List<object>();
            try
            {
                var entries = new List<(string path, string skill, JObject args)>();
                if (name == "prefab_edit_batch")
                {
                    var steps = JArray.Parse(GetStringArg(validation.Args, "steps") ?? "[]");
                    UnitySkills.Internal.PrefabAssetEditor.ValidateSteps(steps);
                    foreach (JObject step in steps) entries.Add((GetStringArg(validation.Args, "prefabPath"), step.Value<string>("skill"), (JObject)step["args"]));
                }
                else if (name.EndsWith("_batch", StringComparison.Ordinal))
                {
                    if (!TryParseBatchItems(validation, out var items)) return;
                    if (items.Count > 50) { AddSemanticError(validation, "items", "At most 50 prefab batch items are allowed"); return; }
                    foreach (var token in items)
                    {
                        if (!(token is JObject item)) { AddSemanticError(validation, "items", "Each item must be an object"); return; }
                        var args = (JObject)item.DeepClone(); args.Remove("prefabPath");
                        entries.Add((item.Value<string>("prefabPath"), name.Substring(0, name.Length - 6), args));
                    }
                }
                else entries.Add((GetStringArg(validation.Args, "prefabPath"), name, (JObject)validation.Args.DeepClone()));

                var pendingAssets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    entry.args.Remove("prefabPath");
                    // Router-reserved controls are not operation arguments.
                    entry.args.Remove("verbose"); entry.args.Remove("offset");
                    try
                    {
                        UnitySkills.Internal.PrefabAssetEditor.RequireAsset(entry.path, true);
                        UnitySkills.Internal.PrefabAssetEditor.Preview(entry.path, entry.skill, entry.args);
                    }
                    catch (Exception ex)
                    {
                        if (name.EndsWith("_batch", StringComparison.Ordinal) && name != "prefab_edit_batch")
                            AddWarning(validation, $"Asset group '{entry.path}' may fail: {ex.Message}. Other asset groups will still execute.");
                        else if (pendingAssets.Contains(entry.path))
                            AddWarning(validation, "A later prefab operation depends on an earlier edit; its target and values will be validated on isolated contents at execution.");
                        else AddSemanticError(validation, "prefabPath", ex.Message);
                    }
                    planned.Add(new { action = entry.skill, prefabPath = entry.path, gameObjectPath = entry.args.Value<string>("gameObjectPath") ?? "" });
                    pendingAssets.Add(entry.path);
                }
            }
            catch (Exception ex) { AddSemanticError(validation, "prefabPath", ex.Message); }
            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan, planned, modify: planned);
            }
        }

        private static void AnalyzePrefabCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var savePath = GetStringArg(args, "savePath");
            AddErrorFromValidation(validation, Validate.Required(savePath, "savePath"), "savePath");
            AddErrorFromValidation(validation, Validate.SafePath(savePath, "savePath"), "savePath");

            var source = ResolveTarget(args);
            if (source.Error != null)
                AddSemanticError(validation, "gameObject", ExtractError(source.Error));
            else
                WarnIfPending(validation, "source", source);

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                if (!savePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    savePath += ".prefab";
                if (File.Exists(savePath))
                    AddWarning(validation, $"Prefab already exists at '{savePath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var goName = source.Error == null ? source.Name : GetStringArg(args, "name", "path");
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Prefab",
                            ["source"] = goName ?? "(unresolved)",
                            ["target"] = savePath ?? "(unresolved)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Prefab",
                            ["path"] = savePath,
                            ["sourceName"] = goName
                        }
                    });
            }
        }

        private static void AnalyzePrefabApply(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var target = ResolveTarget(args);
            var go = target.LiveObject;
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
            }
            else if (go != null)
            {
                if (!UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                    AddSemanticError(validation, "gameObject", $"'{go.name}' is not a prefab instance.");
            }
            else
            {
                WarnIfPending(validation, "target", target);
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var goName = target.Error == null ? target.Name : GetStringArg(args, "name", "path");
                string prefabPath = null;
                if (go != null && UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    var prefab = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefab != null)
                        prefabPath = AssetDatabase.GetAssetPath(prefab);
                }

                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Apply Prefab Overrides",
                            ["source"] = goName ?? "(unresolved)",
                            ["target"] = prefabPath ?? "(unresolved)"
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Prefab",
                            ["instanceName"] = goName,
                            ["prefabPath"] = prefabPath
                        }
                    });
            }
        }
    }
}

// Producer:Betsy
