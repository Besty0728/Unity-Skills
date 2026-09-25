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
