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
        private static void AnalyzeGameObjectCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var primitiveType = GetStringArg(args, "primitiveType");
            if (IsUnknownPrimitiveType(primitiveType))
                AddSemanticError(validation, "primitiveType", $"Unknown primitive type: {primitiveType}");

            var space = ReadTransformSpace(args, out var spaceError);
            if (spaceError != null)
                AddSemanticError(validation, "space", spaceError);

            string parentPath = null;
            var (hasParent, parentName, parentInstanceId, parentLocatorPath, parentEntityId) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
            if (hasParent)
            {
                var parent = ResolveLocator(parentName, parentInstanceId, parentLocatorPath, parentEntityId);
                if (parent.Error != null)
                {
                    AddSemanticError(validation, "parent", ExtractError(parent.Error));
                }
                else
                {
                    parentPath = parent.Path;
                    WarnIfPending(validation, "parent", parent);
                }
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentPath != null ? parentPath + "/" + name : name;
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create GameObject",
                            ["target"] = name,
                            ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType,
                            ["parent"] = parentPath ?? "(root)"
                        }
                    },
                    create: new List<object> { BuildCreatePrediction(args, name, predictedPath, primitiveType, space) });
            }
        }

        private static void AnalyzeGameObjectCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            // The error-free items seen so far, so a later item can be parented to one this same call creates.
            var batchCreated = new PendingObjectSet();
            var creates = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var name = GetStringArg(item, "name");
                if (Validate.Required(name, "name") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var primitiveType = GetStringArg(item, "primitiveType");
                if (IsUnknownPrimitiveType(primitiveType))
                    errors.Add($"Unknown primitive type: {primitiveType}");

                var space = ReadTransformSpace(item, out var spaceError);
                if (spaceError != null)
                    errors.Add(spaceError);

                string parentPath = null;
                var (hasParent, parentName, parentInstanceId, parentLocatorPath, parentEntityId) = ReadObjectLocator(item, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
                if (hasParent)
                {
                    var parent = ResolveLocator(parentName, parentInstanceId, parentLocatorPath, parentEntityId, batchCreated);
                    if (parent.Error != null)
                    {
                        errors.Add(ExtractError(parent.Error));
                    }
                    else
                    {
                        parentPath = parent.Path;
                        WarnIfPending(validation, "parent", parent, i);
                    }
                }

                ctx.ReportItemErrors(i, errors);

                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentPath != null ? parentPath + "/" + name : name;
                ctx.AddItemPlan(i,
                    name,
                    errors.Count == 0,
                    errors.ToArray(),
                    new Dictionary<string, object> { ["predictedPath"] = predictedPath });

                if (errors.Count == 0)
                {
                    creates.Add(BuildCreatePrediction(item, name, predictedPath, primitiveType, space));
                    batchCreated.Register(name, predictedPath);
                }
            }
            ctx.EmitPlan("Create GameObjects (batch)", create: creates);
        }

        // Same acceptance as the executor: only null/empty, "Empty" and "None" mean an empty GameObject.
        private static bool IsUnknownPrimitiveType(string primitiveType) =>
            !string.IsNullOrEmpty(primitiveType) &&
            !primitiveType.Equals("Empty", StringComparison.OrdinalIgnoreCase) &&
            !primitiveType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
            !Enum.TryParse<PrimitiveType>(primitiveType, true, out _);

        /// <summary>
        /// The <c>space</c> of gameobject_create/_batch, accepted exactly as the executor does: omitted or empty means
        /// "local" (the historic behavior), otherwise "local" or "world", case-insensitively. A bad value also yields the rejection message.
        /// </summary>
        private static string ReadTransformSpace(JObject args, out string error)
        {
            error = null;
            var raw = GetStringArg(args, "space");
            if (string.IsNullOrEmpty(raw) || raw.Equals("local", StringComparison.OrdinalIgnoreCase))
                return "local";
            if (raw.Equals("world", StringComparison.OrdinalIgnoreCase))
                return "world";
            error = $"Invalid value '{raw}' for parameter 'space'. Valid values: local, world";
            return raw;
        }

        /// <summary>
        /// The create[] entry of one new GameObject. <c>space</c> states how x/y/z and rotX/Y/Z are applied (relative to
        /// the parent, or world); scale is always local. Rotation and scale appear only when the call sets them.
        /// </summary>
        private static Dictionary<string, object> BuildCreatePrediction(JObject args, string name, string predictedPath, string primitiveType, string space)
        {
            var created = new Dictionary<string, object>
            {
                ["name"] = name,
                ["predictedPath"] = predictedPath,
                ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType,
                ["position"] = ReadVector(args, "x", "y", "z", 0f),
                ["space"] = space
            };
            if (HasAnyArg(args, "rotX", "rotY", "rotZ"))
                created["rotation"] = ReadVector(args, "rotX", "rotY", "rotZ", 0f);
            if (HasAnyArg(args, "scaleX", "scaleY", "scaleZ"))
                created["scale"] = ReadVector(args, "scaleX", "scaleY", "scaleZ", 1f);
            return created;
        }

        private static Dictionary<string, object> ReadVector(JObject args, string xKey, string yKey, string zKey, float fallback) =>
            new Dictionary<string, object>
            {
                ["x"] = GetFloatArg(args, xKey, fallback),
                ["y"] = GetFloatArg(args, yKey, fallback),
                ["z"] = GetFloatArg(args, zKey, fallback)
            };

        private static bool HasAnyArg(JObject args, params string[] keys) =>
            args != null && keys.Any(key => args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null);

        private static void AnalyzeGameObjectRename(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var newName = GetStringArg(args, "newName");
            AddErrorFromValidation(validation, Validate.Required(newName, "newName"), "newName");

            var target = ResolveTarget(args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }
            WarnIfPending(validation, "target", target);

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Rename GameObject",
                            ["target"] = target.Path,
                            ["newName"] = newName
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = target.Path,
                            ["oldName"] = target.Name,
                            ["newName"] = newName
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectRenameBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var modifies = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.GetItem(i);
                var errors = new List<string>();
                var newName = GetStringArg(item, "newName");
                if (Validate.Required(newName, "newName") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var target = ResolveTarget(item);
                if (target.Error != null)
                    errors.Add(ExtractError(target.Error));
                else
                    WarnIfPending(validation, "target", target, i);

                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    target.Error == null ? target.Path : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray(),
                    new Dictionary<string, object> { ["newName"] = newName });

                if (errors.Count == 0)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = target.Path,
                        ["oldName"] = target.Name,
                        ["newName"] = newName
                    });
                }
            }
            ctx.EmitPlan("Rename GameObjects (batch)", modify: modifies);
        }

        private static void AnalyzeGameObjectSetParent(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;

            var child = ResolveTarget(args, "childName", "childInstanceId", "childPath", "childEntityId");
            if (child.Error != null)
            {
                AddSemanticError(validation, "child", ExtractError(child.Error));
                return;
            }
            WarnIfPending(validation, "child", child);

            string parentPath = null;
            var (hasParent, parentName, parentInstanceId, parentLocatorPath, parentEntityId) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath", "parentEntityId");
            if (hasParent)
            {
                var parent = ResolveLocator(parentName, parentInstanceId, parentLocatorPath, parentEntityId);
                if (parent.Error != null)
                {
                    AddSemanticError(validation, "parent", ExtractError(parent.Error));
                }
                else
                {
                    parentPath = parent.Path;
                    WarnIfPending(validation, "parent", parent);
                }
            }

            if (plan != null)
            {
                var oldPath = child.Path;
                var newParentPath = parentPath ?? "(root)";
                var predictedPath = parentPath != null ? parentPath + "/" + child.Name : child.Name;

                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Set Parent",
                            ["target"] = oldPath,
                            ["newParent"] = newParentPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = oldPath,
                            ["oldPath"] = oldPath,
                            ["newPath"] = predictedPath,
                            ["newParent"] = newParentPath
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectSetParentBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            if (plan != null)
            {
                foreach (var token in ctx.Items)
                {
                    var itemObj = token as JObject;
                    var childName = itemObj != null ? GetStringArg(itemObj, "childName", "childPath") : null;
                    var parentName = itemObj != null ? GetStringArg(itemObj, "parentName", "parentPath") : null;
                    ctx.ItemPlans.Add(new Dictionary<string, object>
                    {
                        ["child"] = childName ?? "(unspecified)",
                        ["newParent"] = parentName ?? "(root)"
                    });
                }

                ctx.EmitPlan("Batch Set Parent", modify: new List<object>());
            }
        }

        private static void AnalyzeGameObjectDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var target = ResolveTarget(validation.Args);
            if (target.Error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(target.Error));
                return;
            }
            WarnIfPending(validation, "target", target);

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete GameObject",
                            ["target"] = target.Path
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = target.Path,
                            ["name"] = target.Name
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var ctx = TryBeginBatchAnalyze(validation, plan);
            if (ctx == null) return;

            var deletes = new List<object>();
            for (int i = 0; i < ctx.Items.Count; i++)
            {
                var token = ctx.Items[i];
                var item = token as JObject;
                if (item == null && token.Type == JTokenType.String)
                    item = new JObject { ["name"] = token.ToString() };
                item ??= new JObject();

                var errors = new List<string>();
                var target = ResolveTarget(item);
                if (target.Error != null)
                    errors.Add(ExtractError(target.Error));
                else
                    WarnIfPending(validation, "target", target, i);
                ctx.ReportItemErrors(i, errors);

                ctx.AddItemPlan(i,
                    target.Error == null ? target.Path : InferPrimaryTarget(item),
                    errors.Count == 0,
                    errors.ToArray());

                if (errors.Count == 0)
                {
                    deletes.Add(new Dictionary<string, object>
                    {
                        ["target"] = target.Path,
                        ["name"] = target.Name
                    });
                }
            }
            ctx.EmitPlan("Delete GameObjects (batch)", delete: deletes);
        }
    }
}

// Producer:Betsy
