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
    internal static class SkillPlanningService
    {
        public static void EnrichDryRun(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return;

            ApplySemanticPlanner(skill, validation, null);
        }

        /// <summary>
        /// Returns steps/changes data for inclusion in DryRun responses.
        /// Returns null if no semantic planner exists for this skill.
        /// </summary>
        public static IDictionary<string, object> BuildPlanData(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return null;

            var plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ApplySemanticPlanner(skill, validation, plan);

            // If planLevel was not set to "semantic", no planner matched
            if (!plan.ContainsKey("planLevel") || !"semantic".Equals(plan["planLevel"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                return null;

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (plan.ContainsKey("steps"))
                result["steps"] = plan["steps"];
            if (plan.ContainsKey("changes"))
                result["changes"] = plan["changes"];
            return result.Count > 0 ? result : null;
        }

        public static object BuildPlan(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            var plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = "plan",
                ["valid"] = validation?.Valid ?? false,
                ["planLevel"] = "generic",
                ["skill"] = BuildSkillDescriptor(skill),
                ["parameters"] = validation?.ParameterDetails?.ToArray() ?? Array.Empty<object>(),
                ["validation"] = BuildValidation(validation),
                ["summary"] = BuildSummary(skill, validation, null),
                ["steps"] = BuildGenericSteps(skill, validation?.Args).ToArray(),
                ["changes"] = BuildGenericChanges(skill, validation?.Args),
                ["workflow"] = BuildWorkflow(skill),
                ["note"] = "No execution performed"
            };

            var serverAvailability = BuildServerAvailability(validation?.Args);
            if (serverAvailability != null)
                plan["serverAvailability"] = serverAvailability;

            ApplySemanticPlanner(skill, validation, plan);

            plan["valid"] = validation?.Valid ?? false;
            plan["validation"] = BuildValidation(validation);
            plan["summary"] = BuildSummary(skill, validation, plan);
            return plan;
        }

        private static Dictionary<string, object> BuildSkillDescriptor(SkillRouter.SkillInfo skill)
        {
            return new Dictionary<string, object>
            {
                ["name"] = skill.Name,
                ["description"] = skill.Description,
                ["category"] = skill.Category != SkillCategory.Uncategorized ? skill.Category.ToString() : null,
                ["operation"] = SkillRouter.FormatOperationForPlanning(skill.Operation),
                ["tags"] = skill.Tags,
                ["outputs"] = skill.Outputs,
                ["requiresInput"] = skill.RequiresInput,
                ["readOnly"] = skill.ReadOnly,
                ["tracksWorkflow"] = skill.TracksWorkflow,
                ["mutatesScene"] = skill.MutatesScene,
                ["mutatesAssets"] = skill.MutatesAssets,
                ["mayTriggerReload"] = skill.MayTriggerReload,
                ["mayEnterPlayMode"] = skill.MayEnterPlayMode,
                ["supportsDryRun"] = skill.SupportsDryRun,
                ["riskLevel"] = skill.RiskLevel,
                ["requiresPackages"] = skill.RequiresPackages
            };
        }

        private static Dictionary<string, object> BuildValidation(SkillRouter.ParameterValidationResult validation)
        {
            return new Dictionary<string, object>
            {
                ["missingParams"] = validation?.MissingParams.Count > 0 ? validation.MissingParams.ToArray() : null,
                ["typeErrors"] = validation?.TypeErrors.Count > 0 ? validation.TypeErrors.ToArray() : null,
                ["semanticErrors"] = validation?.SemanticErrors.Count > 0 ? validation.SemanticErrors.ToArray() : null,
                ["warnings"] = validation?.Warnings.Count > 0 ? validation.Warnings.ToArray() : null
            };
        }

        private static Dictionary<string, object> BuildSummary(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation,
            IDictionary<string, object> plan)
        {
            var summary = new Dictionary<string, object>
            {
                ["canExecute"] = validation?.Valid ?? false,
                ["requiresConfirmation"] = RequiresConfirmation(skill),
                ["message"] = BuildSummaryMessage(skill, validation)
            };

            if (plan != null && plan.TryGetValue("changes", out var changesObj) && changesObj is IDictionary<string, object> changes)
            {
                summary["changeCounts"] = new Dictionary<string, object>
                {
                    ["create"] = CountEntries(changes, "create"),
                    ["modify"] = CountEntries(changes, "modify"),
                    ["delete"] = CountEntries(changes, "delete")
                };
            }

            return summary;
        }

        private static int CountEntries(IDictionary<string, object> changes, string key)
        {
            if (!changes.TryGetValue(key, out var value) || value == null)
                return 0;

            if (value is Array arr)
                return arr.Length;

            if (value is IEnumerable<object> enumerable)
                return enumerable.Count();

            return 0;
        }

        private static string BuildSummaryMessage(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (validation == null)
                return $"Plan generated for {skill.Name}.";

            if (!validation.Valid)
                return $"Plan found blocking issues for {skill.Name}. Fix validation errors before executing.";

            if (skill.ReadOnly)
                return $"{skill.Name} is read-only. Execution is safe and does not require workflow rollback.";

            if (skill.TracksWorkflow)
                return $"{skill.Name} can execute with workflow tracking and task-level rollback support.";

            return $"{skill.Name} is ready to execute. Review predicted changes before applying.";
        }

        private static bool RequiresConfirmation(SkillRouter.SkillInfo skill)
        {
            return skill.Operation.HasFlag(SkillOperation.Delete)
                || (skill.Tags?.Any(t => string.Equals(t, "batch", StringComparison.OrdinalIgnoreCase)) ?? false)
                || skill.Category == SkillCategory.Asset
                || skill.Category == SkillCategory.Cleaner;
        }

        private static List<object> BuildGenericSteps(SkillRouter.SkillInfo skill, JObject args)
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["index"] = 1,
                    ["skill"] = skill.Name,
                    ["action"] = DescribeAction(skill.Operation),
                    ["target"] = InferPrimaryTarget(args),
                    ["note"] = "Generic plan derived from skill metadata."
                }
            };
        }

        private static Dictionary<string, object> BuildGenericChanges(SkillRouter.SkillInfo skill, JObject args)
        {
            var create = new List<object>();
            var modify = new List<object>();
            var delete = new List<object>();
            var target = InferPrimaryTarget(args);

            var entry = new Dictionary<string, object>
            {
                ["target"] = target,
                ["category"] = skill.Category.ToString(),
                ["operation"] = DescribeAction(skill.Operation)
            };

            if (skill.Operation.HasFlag(SkillOperation.Create))
                create.Add(entry);
            if (skill.Operation.HasFlag(SkillOperation.Modify))
                modify.Add(entry);
            if (skill.Operation.HasFlag(SkillOperation.Delete))
                delete.Add(entry);

            return new Dictionary<string, object>
            {
                ["create"] = create.ToArray(),
                ["modify"] = modify.ToArray(),
                ["delete"] = delete.ToArray()
            };
        }

        private static Dictionary<string, object> BuildWorkflow(SkillRouter.SkillInfo skill)
        {
            return new Dictionary<string, object>
            {
                ["tracksWorkflow"] = skill.TracksWorkflow,
                ["rollbackScope"] = skill.TracksWorkflow ? "task" : "none",
                ["predictedSnapshots"] = skill.TracksWorkflow
                    ? new[]
                    {
                        skill.Operation.HasFlag(SkillOperation.Create) ? "createdObjects" : null,
                        skill.Operation.HasFlag(SkillOperation.Modify) ? "modifiedTargets" : null,
                        skill.Operation.HasFlag(SkillOperation.Delete) ? "deletedTargets" : null
                    }.Where(x => x != null).ToArray()
                    : Array.Empty<string>()
            };
        }

        private static Dictionary<string, object> BuildServerAvailability(JObject args)
        {
            if (args == null)
                return null;

            foreach (var path in GetCandidatePaths(args))
            {
                if (ServerAvailabilityHelper.AffectsScriptDomain(path))
                {
                    return ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                        $"Plan detected a script-domain affecting path: {path}. Execution may briefly disconnect the REST server.",
                        alwaysInclude: true,
                        retryAfterSeconds: 5);
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidatePaths(JObject args)
        {
            var keys = new[] { "assetPath", "destinationPath", "sourcePath", "savePath", "materialPath" };
            foreach (var key in keys)
            {
                if (args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                {
                    var value = token.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                        yield return value;
                }
            }

            var itemsJson = GetStringArg(args, "items");
            if (string.IsNullOrWhiteSpace(itemsJson))
                yield break;

            JArray items;
            try
            {
                items = JArray.Parse(itemsJson);
            }
            catch
            {
                yield break;
            }

            foreach (var token in items.OfType<JObject>())
            {
                foreach (var itemKey in new[] { "assetPath", "destinationPath", "sourcePath", "path", "materialPath" })
                {
                    if (token.TryGetValue(itemKey, StringComparison.OrdinalIgnoreCase, out var itemValue) && itemValue.Type != JTokenType.Null)
                    {
                        var value = itemValue.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            yield return value;
                    }
                }
            }
        }

        private static string DescribeAction(SkillOperation operation)
        {
            var actions = new List<string>();
            if (operation.HasFlag(SkillOperation.Create)) actions.Add("Create");
            if (operation.HasFlag(SkillOperation.Modify)) actions.Add("Modify");
            if (operation.HasFlag(SkillOperation.Delete)) actions.Add("Delete");
            if (operation.HasFlag(SkillOperation.Query)) actions.Add("Query");
            if (operation.HasFlag(SkillOperation.Analyze)) actions.Add("Analyze");
            if (operation.HasFlag(SkillOperation.Execute)) actions.Add("Execute");
            return actions.Count > 0 ? string.Join("/", actions) : "Execute";
        }

        private static string InferPrimaryTarget(JObject args)
        {
            if (args == null)
                return "(unspecified)";

            var target = GetStringArg(args, "newName", "name", "path", "assetPath", "destinationPath", "sourcePath", "materialPath", "componentType");
            if (!string.IsNullOrWhiteSpace(target))
                return target;

            if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken) &&
                idToken.Type != JTokenType.Null &&
                idToken.ToObject<int>() != 0)
            {
                return $"instanceId:{idToken.ToObject<int>()}";
            }

            return "(unspecified)";
        }

        private static void ApplySemanticPlanner(
            SkillRouter.SkillInfo skill,
            SkillRouter.ParameterValidationResult validation,
            IDictionary<string, object> plan)
        {
            switch (skill.Name)
            {
                case "gameobject_create":
                    AnalyzeGameObjectCreate(validation, plan);
                    break;
                case "gameobject_create_batch":
                    AnalyzeGameObjectCreateBatch(validation, plan);
                    break;
                case "gameobject_rename":
                    AnalyzeGameObjectRename(validation, plan);
                    break;
                case "gameobject_rename_batch":
                    AnalyzeGameObjectRenameBatch(validation, plan);
                    break;
                case "gameobject_delete":
                    AnalyzeGameObjectDelete(validation, plan);
                    break;
                case "gameobject_delete_batch":
                    AnalyzeGameObjectDeleteBatch(validation, plan);
                    break;
                case "gameobject_set_parent":
                    AnalyzeGameObjectSetParent(validation, plan);
                    break;
                case "gameobject_set_parent_batch":
                    AnalyzeGameObjectSetParentBatch(validation, plan);
                    break;
                case "component_add":
                    AnalyzeComponentAdd(validation, plan);
                    break;
                case "component_add_batch":
                    AnalyzeComponentAddBatch(validation, plan);
                    break;
                case "component_remove":
                    AnalyzeComponentRemove(validation, plan);
                    break;
                case "component_remove_batch":
                    AnalyzeComponentRemoveBatch(validation, plan);
                    break;
                case "component_set_property":
                    AnalyzeComponentSetProperty(validation, plan);
                    break;
                case "component_set_property_batch":
                    AnalyzeComponentSetPropertyBatch(validation, plan);
                    break;
                case "material_create":
                    AnalyzeMaterialCreate(validation, plan);
                    break;
                case "material_create_batch":
                    AnalyzeMaterialCreateBatch(validation, plan);
                    break;
                case "material_assign":
                    AnalyzeMaterialAssign(validation, plan);
                    break;
                case "material_assign_batch":
                    AnalyzeMaterialAssignBatch(validation, plan);
                    break;
                case "asset_import":
                    AnalyzeAssetImport(validation, plan);
                    break;
                case "asset_import_batch":
                    AnalyzeAssetImportBatch(validation, plan);
                    break;
                case "asset_delete":
                    AnalyzeAssetDelete(validation, plan);
                    break;
                case "asset_delete_batch":
                    AnalyzeAssetDeleteBatch(validation, plan);
                    break;
                case "asset_move":
                    AnalyzeAssetMove(validation, plan);
                    break;
                case "asset_move_batch":
                    AnalyzeAssetMoveBatch(validation, plan);
                    break;
                case "scene_create":
                    AnalyzeSceneCreate(validation, plan);
                    break;
                case "scene_save":
                    AnalyzeSceneSave(validation, plan);
                    break;
                case "scene_load":
                    AnalyzeSceneLoad(validation, plan);
                    break;
                case "prefab_create":
                    AnalyzePrefabCreate(validation, plan);
                    break;
                case "prefab_apply":
                    AnalyzePrefabApply(validation, plan);
                    break;
                case "script_create":
                    AnalyzeScriptCreate(validation, plan);
                    break;
            }
        }

        private static void AnalyzeGameObjectCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var primitiveType = GetStringArg(args, "primitiveType");
            if (!string.IsNullOrWhiteSpace(primitiveType) &&
                !primitiveType.Equals("Empty", StringComparison.OrdinalIgnoreCase) &&
                !primitiveType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                !Enum.TryParse<PrimitiveType>(primitiveType, true, out _))
            {
                AddSemanticError(validation, "primitiveType", $"Unknown primitive type: {primitiveType}");
            }

            GameObject parentGo = null;
            var (hasParent, parentName, parentInstanceId, parentPath) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath");
            if (hasParent)
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath);
                if (parentErr != null)
                    AddSemanticError(validation, "parent", ExtractError(parentErr));
                else
                    parentGo = found;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentGo != null ? GameObjectFinder.GetPath(parentGo) + "/" + name : name;
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
                            ["parent"] = parentGo != null ? GameObjectFinder.GetPath(parentGo) : "(root)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["predictedPath"] = predictedPath,
                            ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType,
                            ["position"] = new Dictionary<string, object>
                            {
                                ["x"] = GetFloatArg(args, "x"),
                                ["y"] = GetFloatArg(args, "y"),
                                ["z"] = GetFloatArg(args, "z")
                            }
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var itemPlans = new List<object>();
            var creates = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var errors = new List<string>();
                var name = GetStringArg(item, "name");
                if (Validate.Required(name, "name") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var primitiveType = GetStringArg(item, "primitiveType");
                if (!string.IsNullOrWhiteSpace(primitiveType) &&
                    !primitiveType.Equals("Empty", StringComparison.OrdinalIgnoreCase) &&
                    !primitiveType.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                    !Enum.TryParse<PrimitiveType>(primitiveType, true, out _))
                {
                    errors.Add($"Unknown primitive type: {primitiveType}");
                }

                string parentPath = "(root)";
                var (hasParent, parentName, parentInstanceId, parentLocatorPath) = ReadObjectLocator(item, "parentName", "parentInstanceId", "parentPath");
                if (hasParent)
                {
                    var (parentGo, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentLocatorPath);
                    if (parentErr != null)
                        errors.Add(ExtractError(parentErr));
                    else
                        parentPath = GameObjectFinder.GetPath(parentGo);
                }

                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));

                var predictedPath = string.IsNullOrWhiteSpace(name)
                    ? "(unresolved)"
                    : parentPath == "(root)" ? name : parentPath + "/" + name;
                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = name,
                    ["valid"] = errors.Count == 0,
                    ["predictedPath"] = predictedPath,
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["predictedPath"] = predictedPath,
                        ["primitiveType"] = string.IsNullOrWhiteSpace(primitiveType) ? "Empty" : primitiveType
                    });
                }
            }

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
                            ["action"] = "Create GameObjects (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    create: creates,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectRename(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var newName = GetStringArg(args, "newName");
            AddErrorFromValidation(validation, Validate.Required(newName, "newName"), "newName");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

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
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["newName"] = newName
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["oldName"] = go.name,
                            ["newName"] = newName
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectRenameBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var modifies = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var errors = new List<string>();
                var newName = GetStringArg(item, "newName");
                if (Validate.Required(newName, "newName") is object nameErr)
                    errors.Add(ExtractError(nameErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = errors.Count == 0,
                    ["newName"] = newName,
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0 && go != null)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["oldName"] = go.name,
                        ["newName"] = newName
                    });
                }
            }

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
                            ["action"] = "Rename GameObjects (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    modify: modifies,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectSetParent(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;

            var (child, childError) = ResolveGameObject(args, "childName", "childInstanceId", "childPath");
            if (childError != null)
            {
                AddSemanticError(validation, "child", ExtractError(childError));
                return;
            }

            GameObject parentGo = null;
            var (hasParent, parentName, parentInstanceId, parentPath) = ReadObjectLocator(args, "parentName", "parentInstanceId", "parentPath");
            if (hasParent)
            {
                var (found, parentErr) = GameObjectFinder.FindOrError(parentName, parentInstanceId, parentPath);
                if (parentErr != null)
                    AddSemanticError(validation, "parent", ExtractError(parentErr));
                else
                    parentGo = found;
            }

            if (plan != null)
            {
                var oldPath = GameObjectFinder.GetPath(child);
                var newParentPath = parentGo != null ? GameObjectFinder.GetPath(parentGo) : "(root)";
                var predictedPath = parentGo != null ? newParentPath + "/" + child.name : child.name;

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
            if (!TryParseBatchItems(validation, out var items))
                return;

            if (plan != null)
            {
                var itemPlans = new List<object>();
                foreach (var item in items)
                {
                    var itemObj = item as JObject;
                    var childName = itemObj != null ? GetStringArg(itemObj, "childName", "childPath") : null;
                    var parentName = itemObj != null ? GetStringArg(itemObj, "parentName", "parentPath") : null;
                    itemPlans.Add(new Dictionary<string, object>
                    {
                        ["child"] = childName ?? "(unspecified)",
                        ["newParent"] = parentName ?? "(root)"
                    });
                }

                MarkSemantic(plan);
                SetPlanDetails(
                    plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Batch Set Parent",
                            ["targetCount"] = items.Count
                        }
                    },
                    modify: new List<object>(),
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var (go, error) = ResolveGameObject(validation.Args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

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
                            ["target"] = GameObjectFinder.GetPath(go)
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["name"] = go.name
                        }
                    });
            }
        }

        private static void AnalyzeGameObjectDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var deletes = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var token = items[i];
                var item = token as JObject;
                if (item == null && token.Type == JTokenType.String)
                    item = new JObject { ["name"] = token.ToString() };
                item ??= new JObject();

                var errors = new List<string>();
                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));
                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = errors.Count == 0,
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0 && go != null)
                {
                    deletes.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["name"] = go.name
                    });
                }
            }

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
                            ["action"] = "Delete GameObjects (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    delete: deletes,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeComponentAdd(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            bool alreadyExists = go.GetComponent(type) != null && !AllowsMultiple(type);
            if (alreadyExists)
                AddWarning(validation, $"Component {type.Name} already exists on {go.name}; execution will be a no-op warning.");

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
                            ["action"] = "Add Component",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName
                        }
                    },
                    create: alreadyExists
                        ? new List<object>()
                        : new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["target"] = GameObjectFinder.GetPath(go),
                                ["component"] = type.Name,
                                ["fullTypeName"] = type.FullName
                            }
                        });
            }
        }

        private static void AnalyzeComponentAddBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var creates = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var errors = new List<string>();
                var warnings = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                Type type = null;
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else if (go.GetComponent(type) != null && !AllowsMultiple(type))
                        warnings.Add($"Component {type.Name} already exists on {go.name}");
                }

                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));
                foreach (var warning in warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = errors.Count == 0,
                    ["warnings"] = warnings.ToArray(),
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0 && type != null && warnings.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = type.Name,
                        ["fullTypeName"] = type.FullName
                    });
                }
            }

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
                            ["action"] = "Add Components (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    create: creates,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeComponentRemove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            var components = go.GetComponents(type);
            if (components.Length == 0)
            {
                AddSemanticError(validation, "component", $"Component not found on {go.name}: {componentType}");
                return;
            }

            int componentIndex = GetIntArg(args, "componentIndex");
            if (componentIndex >= components.Length)
            {
                AddSemanticError(validation, "componentIndex", $"Component index {componentIndex} out of range. Found {components.Length} components of type {componentType}");
                return;
            }

            var requiredBy = GetRequiredByComponents(go, type);
            if (requiredBy.Any())
            {
                AddSemanticError(validation, "component", $"Cannot remove {componentType} - required by: {string.Join(", ", requiredBy)}");
                return;
            }

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
                            ["action"] = "Remove Component",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName,
                            ["componentIndex"] = componentIndex
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["component"] = type.Name,
                            ["componentIndex"] = componentIndex
                        }
                    });
            }
        }

        private static void AnalyzeComponentRemoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var deletes = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var errors = new List<string>();

                var componentType = GetStringArg(item, "componentType");
                if (Validate.Required(componentType, "componentType") is object typeErr)
                    errors.Add(ExtractError(typeErr));

                var (go, error) = ResolveGameObject(item);
                if (error != null)
                    errors.Add(ExtractError(error));

                Type type = null;
                int count = 0;
                if (errors.Count == 0)
                {
                    type = ComponentSkills.FindComponentType(componentType);
                    if (type == null)
                        errors.Add($"Component type not found: {componentType}");
                    else
                    {
                        var components = go.GetComponents(type);
                        count = components.Length;
                        if (count == 0)
                            errors.Add($"Component not found: {componentType}");
                        else if (GetRequiredByComponents(go, type).Any())
                            errors.Add($"Cannot remove {componentType} because another component requires it");
                    }
                }

                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = errors.Count == 0,
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0 && type != null)
                {
                    deletes.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = type.Name,
                        ["count"] = count
                    });
                }
            }

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
                            ["action"] = "Remove Components (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    delete: deletes,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeComponentSetProperty(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var componentType = GetStringArg(args, "componentType");
            var propertyName = GetStringArg(args, "propertyName");
            AddErrorFromValidation(validation, Validate.Required(componentType, "componentType"), "componentType");
            AddErrorFromValidation(validation, Validate.Required(propertyName, "propertyName"), "propertyName");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null)
            {
                AddSemanticError(validation, "componentType", $"Component type not found: {componentType}");
                return;
            }

            var comp = go.GetComponent(type);
            if (comp == null)
            {
                AddSemanticError(validation, "component", $"Component not found: {componentType}");
                return;
            }

            var (prop, field) = FindMember(type, propertyName);
            if (prop == null && field == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            var targetType = prop?.PropertyType ?? field?.FieldType;
            if (targetType == null)
            {
                AddSemanticError(validation, "propertyName", $"Property/field not found: {propertyName}");
                return;
            }

            if (prop != null && !prop.CanWrite)
            {
                AddSemanticError(validation, "propertyName", $"Property {propertyName} is read-only");
                return;
            }

            TryValidateComponentAssignment(args, propertyName, targetType, validation);

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
                            ["action"] = "Set Component Property",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["componentType"] = type.FullName,
                            ["propertyName"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["component"] = type.Name,
                            ["property"] = propertyName,
                            ["valueType"] = targetType.Name
                        }
                    });
            }
        }

        private static void AnalyzeComponentSetPropertyBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var modifies = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var itemValidation = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeComponentSetProperty(itemValidation, null);

                foreach (var semanticError in itemValidation.SemanticErrors)
                {
                    AddSemanticError(validation, $"items[{i}]", ExtractSemanticMessage(semanticError));
                }
                foreach (var warning in itemValidation.Warnings)
                {
                    AddWarning(validation, $"items[{i}]: {warning}");
                }

                var (go, _) = ResolveGameObject(item);
                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = itemValidation.SemanticErrors.Count == 0,
                    ["errors"] = itemValidation.SemanticErrors.Select(ExtractSemanticMessage).ToArray()
                });

                if (itemValidation.SemanticErrors.Count == 0 && go != null)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["component"] = GetStringArg(item, "componentType"),
                        ["property"] = GetStringArg(item, "propertyName")
                    });
                }
            }

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
                            ["action"] = "Set Component Properties (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    modify: modifies,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeMaterialCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var name = GetStringArg(args, "name");
            AddErrorFromValidation(validation, Validate.Required(name, "name"), "name");

            var savePath = GetStringArg(args, "savePath");
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object saveErr)
                AddSemanticError(validation, "savePath", ExtractError(saveErr));

            var resolvedShaderName = ResolveShaderName(GetStringArg(args, "shaderName"), validation);
            string resolvedPath = null;
            if (!string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name))
                resolvedPath = ResolveMaterialSavePath(savePath, name);
            else if (string.IsNullOrEmpty(savePath))
                AddWarning(validation, "Material will be created in memory only because savePath is omitted.");

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
                            ["action"] = "Create Material",
                            ["target"] = name,
                            ["shader"] = resolvedShaderName,
                            ["savePath"] = resolvedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["name"] = name,
                            ["shader"] = resolvedShaderName,
                            ["path"] = resolvedPath,
                            ["renderPipeline"] = ProjectSkills.DetectRenderPipeline().ToString(),
                            ["persistent"] = !string.IsNullOrEmpty(resolvedPath)
                        }
                    });
            }
        }

        private static void AnalyzeMaterialCreateBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var creates = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var itemValidation = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialCreate(itemValidation, null);

                var name = GetStringArg(item, "name");
                var savePath = GetStringArg(item, "savePath");
                var resolvedShader = ResolveShaderName(GetStringArg(item, "shaderName"), itemValidation);

                foreach (var semanticError in itemValidation.SemanticErrors)
                    AddSemanticError(validation, $"items[{i}]", ExtractSemanticMessage(semanticError));
                foreach (var warning in itemValidation.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = name,
                    ["valid"] = itemValidation.SemanticErrors.Count == 0,
                    ["errors"] = itemValidation.SemanticErrors.Select(ExtractSemanticMessage).ToArray(),
                    ["warnings"] = itemValidation.Warnings.ToArray()
                });

                if (itemValidation.SemanticErrors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["name"] = name,
                        ["shader"] = resolvedShader,
                        ["path"] = !string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name) ? ResolveMaterialSavePath(savePath, name) : null
                    });
                }
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Materials (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    create: creates,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeMaterialAssign(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var materialPath = GetStringArg(args, "materialPath");
            AddErrorFromValidation(validation, Validate.Required(materialPath, "materialPath"), "materialPath");

            var (go, error) = ResolveGameObject(args);
            if (error != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(error));
                return;
            }

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                AddSemanticError(validation, "renderer", "No Renderer component found");
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                AddSemanticError(validation, "materialPath", $"Material not found: {materialPath}");
                return;
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Assign Material",
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["materialPath"] = materialPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = GameObjectFinder.GetPath(go),
                            ["material"] = materialPath,
                            ["rendererType"] = renderer.GetType().Name
                        }
                    });
            }
        }

        private static void AnalyzeMaterialAssignBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var modifies = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var itemValidation = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeMaterialAssign(itemValidation, null);
                foreach (var semanticError in itemValidation.SemanticErrors)
                    AddSemanticError(validation, $"items[{i}]", ExtractSemanticMessage(semanticError));

                var (go, _) = ResolveGameObject(item);
                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = go != null ? GameObjectFinder.GetPath(go) : InferPrimaryTarget(item),
                    ["valid"] = itemValidation.SemanticErrors.Count == 0,
                    ["errors"] = itemValidation.SemanticErrors.Select(ExtractSemanticMessage).ToArray()
                });

                if (itemValidation.SemanticErrors.Count == 0 && go != null)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["target"] = GameObjectFinder.GetPath(go),
                        ["material"] = GetStringArg(item, "materialPath")
                    });
                }
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Assign Materials (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    modify: modifies,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeAssetImport(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath) && !File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                AddSemanticError(validation, "sourcePath", $"Source not found: {sourcePath}");
            if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(sourcePath))
                AddSemanticError(validation, "sourcePath", $"Source path must be a file, not a directory: {sourcePath}");
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));

            if (plan != null)
            {
                MarkSemantic(plan);
                var dir = string.IsNullOrEmpty(destinationPath) ? null : Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Import Asset",
                            ["target"] = destinationPath,
                            ["sourcePath"] = sourcePath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["sourcePath"] = sourcePath,
                            ["destinationPath"] = destinationPath,
                            ["createsDirectory"] = !string.IsNullOrEmpty(dir) && !Directory.Exists(dir)
                        }
                    });
            }
        }

        private static void AnalyzeAssetImportBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var creates = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var itemValidation = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetImport(itemValidation, null);
                foreach (var semanticError in itemValidation.SemanticErrors)
                    AddSemanticError(validation, $"items[{i}]", ExtractSemanticMessage(semanticError));

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = GetStringArg(item, "destinationPath") ?? GetStringArg(item, "sourcePath"),
                    ["valid"] = itemValidation.SemanticErrors.Count == 0,
                    ["errors"] = itemValidation.SemanticErrors.Select(ExtractSemanticMessage).ToArray()
                });

                if (itemValidation.SemanticErrors.Count == 0)
                {
                    creates.Add(new Dictionary<string, object>
                    {
                        ["sourcePath"] = GetStringArg(item, "sourcePath"),
                        ["destinationPath"] = GetStringArg(item, "destinationPath")
                    });
                }
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Import Assets (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    create: creates,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeAssetDelete(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var assetPath = GetStringArg(validation.Args, "assetPath");
            AddErrorFromValidation(validation, Validate.Required(assetPath, "assetPath"), "assetPath");
            if (!string.IsNullOrEmpty(assetPath) && Validate.SafePath(assetPath, "assetPath", isDelete: true) is object pathErr)
                AddSemanticError(validation, "assetPath", ExtractError(pathErr));
            if (!string.IsNullOrEmpty(assetPath) && !File.Exists(assetPath) && !Directory.Exists(assetPath))
                AddSemanticError(validation, "assetPath", $"Asset not found: {assetPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete Asset",
                            ["target"] = assetPath
                        }
                    },
                    delete: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["target"] = assetPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetDeleteBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var deletes = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var path = GetStringArg(item, "path");
                var errors = new List<string>();
                if (Validate.Required(path, "path") is object pathRequired)
                    errors.Add(ExtractError(pathRequired));
                else
                {
                    if (Validate.SafePath(path, "path", isDelete: true) is object pathErr)
                        errors.Add(ExtractError(pathErr));
                    if (!File.Exists(path) && !Directory.Exists(path))
                        errors.Add($"Asset not found: {path}");
                }

                if (errors.Count > 0)
                    AddSemanticError(validation, $"items[{i}]", string.Join("; ", errors));
                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = path,
                    ["valid"] = errors.Count == 0,
                    ["errors"] = errors.ToArray()
                });

                if (errors.Count == 0)
                    deletes.Add(new Dictionary<string, object> { ["target"] = path });
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Delete Assets (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    delete: deletes,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        private static void AnalyzeAssetMove(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var sourcePath = GetStringArg(args, "sourcePath");
            var destinationPath = GetStringArg(args, "destinationPath");
            AddErrorFromValidation(validation, Validate.Required(sourcePath, "sourcePath"), "sourcePath");
            AddErrorFromValidation(validation, Validate.Required(destinationPath, "destinationPath"), "destinationPath");

            if (!string.IsNullOrEmpty(sourcePath) && Validate.SafePath(sourcePath, "sourcePath") is object srcErr)
                AddSemanticError(validation, "sourcePath", ExtractError(srcErr));
            if (!string.IsNullOrEmpty(destinationPath) && Validate.SafePath(destinationPath, "destinationPath") is object dstErr)
                AddSemanticError(validation, "destinationPath", ExtractError(dstErr));
            if (!string.IsNullOrEmpty(sourcePath) && !File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                AddSemanticError(validation, "sourcePath", $"Asset not found: {sourcePath}");
            if (!string.IsNullOrEmpty(destinationPath) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath) != null)
                AddWarning(validation, $"Destination already exists and may cause move failure: {destinationPath}");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Move Asset",
                            ["target"] = sourcePath,
                            ["destinationPath"] = destinationPath
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["from"] = sourcePath,
                            ["to"] = destinationPath
                        }
                    });
            }
        }

        private static void AnalyzeAssetMoveBatch(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return;

            var modifies = new List<object>();
            var itemPlans = new List<object>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i] as JObject ?? new JObject();
                var itemValidation = new SkillRouter.ParameterValidationResult { Args = item };
                AnalyzeAssetMove(itemValidation, null);
                foreach (var semanticError in itemValidation.SemanticErrors)
                    AddSemanticError(validation, $"items[{i}]", ExtractSemanticMessage(semanticError));
                foreach (var warning in itemValidation.Warnings)
                    AddWarning(validation, $"items[{i}]: {warning}");

                itemPlans.Add(new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["target"] = GetStringArg(item, "sourcePath"),
                    ["valid"] = itemValidation.SemanticErrors.Count == 0,
                    ["errors"] = itemValidation.SemanticErrors.Select(ExtractSemanticMessage).ToArray()
                });

                if (itemValidation.SemanticErrors.Count == 0)
                {
                    modifies.Add(new Dictionary<string, object>
                    {
                        ["from"] = GetStringArg(item, "sourcePath"),
                        ["to"] = GetStringArg(item, "destinationPath")
                    });
                }
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Move Assets (batch)",
                            ["target"] = $"{items.Count} items"
                        }
                    },
                    modify: modifies,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = items.Count,
                            ["items"] = itemPlans.ToArray()
                        }
                    });
            }
        }

        // ==================================================================================
        // Scene Semantic Planners
        // ==================================================================================

        private static void AnalyzeSceneCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");
            AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    scenePath += ".unity";

                if (File.Exists(scenePath))
                    AddWarning(validation, $"Scene already exists at '{scenePath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create Scene",
                            ["target"] = scenePath ?? "(unresolved)"
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = scenePath
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a new scene may briefly interrupt the connection.",
                    alwaysInclude: false, retryAfterSeconds: 3);
            }
        }

        private static void AnalyzeSceneSave(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath))
                AddErrorFromValidation(validation, Validate.SafePath(scenePath, "scenePath"), "scenePath");

            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var targetPath = !string.IsNullOrWhiteSpace(scenePath) ? scenePath : activeScene.path;

            if (string.IsNullOrWhiteSpace(targetPath))
                AddWarning(validation, "Scene has no path yet. A Save As dialog may appear or a default path will be used.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Save Scene",
                            ["target"] = targetPath ?? activeScene.name
                        }
                    },
                    modify: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Scene",
                            ["path"] = targetPath,
                            ["sceneName"] = activeScene.name,
                            ["isDirty"] = activeScene.isDirty
                        }
                    });
            }
        }

        private static void AnalyzeSceneLoad(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scenePath = GetStringArg(args, "scenePath");
            AddErrorFromValidation(validation, Validate.Required(scenePath, "scenePath"), "scenePath");

            if (!string.IsNullOrWhiteSpace(scenePath) && !File.Exists(scenePath))
                AddSemanticError(validation, "scenePath", $"Scene file not found: {scenePath}");

            var additive = args?["additive"]?.Value<bool>() ?? false;
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            if (!additive && activeScene.isDirty)
                AddWarning(validation, $"Active scene '{activeScene.name}' has unsaved changes that will be lost.");

            if (plan != null)
            {
                MarkSemantic(plan);
                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = additive ? "Load Scene (Additive)" : "Load Scene",
                            ["target"] = scenePath ?? "(unresolved)",
                            ["additive"] = additive
                        }
                    },
                    modify: additive ? null : new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "ActiveScene",
                            ["from"] = activeScene.path,
                            ["to"] = scenePath
                        }
                    });

                if (!additive)
                {
                    plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                        "Loading a scene (non-additive) replaces the current scene and may briefly interrupt the connection.",
                        alwaysInclude: false, retryAfterSeconds: 3);
                }
            }
        }

        // ==================================================================================
        // Prefab Semantic Planners
        // ==================================================================================

        private static void AnalyzePrefabCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var savePath = GetStringArg(args, "savePath");
            AddErrorFromValidation(validation, Validate.Required(savePath, "savePath"), "savePath");
            AddErrorFromValidation(validation, Validate.SafePath(savePath, "savePath"), "savePath");

            var (go, goErr) = ResolveGameObject(args);
            if (goErr != null)
                AddSemanticError(validation, "gameObject", ExtractError(goErr));

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
                var goName = go != null ? go.name : GetStringArg(args, "name", "path");
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
            var (go, goErr) = ResolveGameObject(args);
            if (goErr != null)
            {
                AddSemanticError(validation, "gameObject", ExtractError(goErr));
            }
            else if (go != null)
            {
                if (!UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go))
                    AddSemanticError(validation, "gameObject", $"'{go.name}' is not a prefab instance.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var goName = go != null ? go.name : GetStringArg(args, "name", "path");
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

        // ==================================================================================
        // Script Semantic Planners
        // ==================================================================================

        private static void AnalyzeScriptCreate(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            var args = validation.Args;
            var scriptName = GetStringArg(args, "scriptName", "name");
            var folder = GetStringArg(args, "folder") ?? "Assets/Scripts";

            if (string.IsNullOrWhiteSpace(scriptName))
                AddSemanticError(validation, "scriptName", "scriptName or name is required.");

            if (!string.IsNullOrWhiteSpace(scriptName))
            {
                // Validate C# class name
                if (!System.Text.RegularExpressions.Regex.IsMatch(scriptName, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    AddSemanticError(validation, "scriptName", $"'{scriptName}' is not a valid C# class name.");

                var predictedPath = Path.Combine(folder, scriptName + ".cs").Replace('\\', '/');
                if (File.Exists(predictedPath))
                    AddWarning(validation, $"Script already exists at '{predictedPath}' and will be overwritten.");
            }

            if (plan != null)
            {
                MarkSemantic(plan);
                var predictedPath = !string.IsNullOrWhiteSpace(scriptName)
                    ? Path.Combine(folder, scriptName + ".cs").Replace('\\', '/')
                    : "(unresolved)";

                SetPlanDetails(plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = "Create C# Script",
                            ["target"] = predictedPath
                        }
                    },
                    create: new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["type"] = "Script",
                            ["path"] = predictedPath,
                            ["className"] = scriptName
                        }
                    });

                plan["serverAvailability"] = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                    "Creating a C# script triggers compilation and Domain Reload. The REST server will be briefly unavailable.",
                    alwaysInclude: true, retryAfterSeconds: 10);
            }
        }

        private static void TryValidateComponentAssignment(
            JObject args,
            string propertyName,
            Type targetType,
            SkillRouter.ParameterValidationResult validation)
        {
            var assetPath = GetStringArg(args, "assetPath");
            var referencePath = GetStringArg(args, "referencePath");
            var referenceName = GetStringArg(args, "referenceName");
            var value = GetStringArg(args, "value");

            try
            {
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath(assetPath, targetType);
                    if (asset == null)
                        AddSemanticError(validation, propertyName, $"Asset not found or type mismatch: '{assetPath}' (expected {targetType.Name})");
                    return;
                }

                if (!string.IsNullOrEmpty(referencePath) || !string.IsNullOrEmpty(referenceName))
                {
                    var resolved = ResolveSceneReference(targetType, referencePath, referenceName);
                    if (resolved == null)
                        AddSemanticError(validation, propertyName, $"Could not resolve reference for {propertyName}. Target: path='{referencePath}', name='{referenceName}'");
                    return;
                }

                ComponentSkills.ConvertValue(value, targetType);
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, propertyName, ex.Message);
            }
        }

        private static (GameObject go, object error) ResolveGameObject(JObject args)
        {
            var (_, name, instanceId, path) = ReadObjectLocator(args, "name", "instanceId", "path");
            return GameObjectFinder.FindOrError(name, instanceId, path);
        }

        private static (GameObject go, object error) ResolveGameObject(JObject args, string nameKey, string instanceIdKey, string pathKey)
        {
            var (_, name, instanceId, path) = ReadObjectLocator(args, nameKey, instanceIdKey, pathKey);
            return GameObjectFinder.FindOrError(name, instanceId, path);
        }

        private static (bool hasLocator, string name, int instanceId, string path) ReadObjectLocator(
            JObject args,
            string nameKey,
            string instanceIdKey,
            string pathKey)
        {
            var name = GetStringArg(args, nameKey);
            var path = GetStringArg(args, pathKey);
            int instanceId = GetIntArg(args, instanceIdKey);
            bool hasLocator = !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(path) || instanceId != 0;
            return (hasLocator, name, instanceId, path);
        }

        private static string ResolveShaderName(string shaderName, SkillRouter.ParameterValidationResult validation)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                shaderName = ProjectSkills.GetDefaultShaderName();

            var shader = Shader.Find(shaderName);
            if (shader != null)
                return shaderName;

            var pipeline = ProjectSkills.DetectRenderPipeline();
            var fallbackShaders = pipeline switch
            {
                ProjectSkills.RenderPipelineType.URP => new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard" },
                ProjectSkills.RenderPipelineType.HDRP => new[] { "HDRP/Lit", "Standard" },
                _ => new[] { "Standard", "Mobile/Diffuse", "Unlit/Color" }
            };

            foreach (var fallback in fallbackShaders)
            {
                shader = Shader.Find(fallback);
                if (shader != null)
                {
                    AddWarning(validation, $"Shader '{shaderName}' not found. Planner fell back to '{fallback}'.");
                    return fallback;
                }
            }

            AddSemanticError(validation, "shaderName", $"Shader not found: {shaderName}");
            return shaderName;
        }

        private static string ResolveMaterialSavePath(string savePath, string materialName)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;

            if (!savePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                savePath = "Assets/" + savePath;

            if (Directory.Exists(savePath) || !Path.HasExtension(savePath))
            {
                string fileName = string.IsNullOrEmpty(materialName) ? "NewMaterial" : materialName;
                savePath = Path.Combine(savePath, fileName + ".mat").Replace("\\", "/");
            }
            else if (!savePath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                savePath = savePath + ".mat";
            }

            return savePath;
        }

        private static bool TryParseBatchItems(SkillRouter.ParameterValidationResult validation, out JArray items)
        {
            items = null;
            if (validation?.Args == null)
                return false;

            var itemsJson = GetStringArg(validation.Args, "items");
            if (Validate.RequiredJsonArray(itemsJson, "items") is object requiredErr)
            {
                AddSemanticError(validation, "items", ExtractError(requiredErr));
                return false;
            }

            try
            {
                items = JArray.Parse(itemsJson);
                if (items.Count == 0)
                {
                    AddSemanticError(validation, "items", "items must be a non-empty array");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, "items", $"Failed to parse items JSON: {ex.Message}");
                return false;
            }
        }

        private static bool AllowsMultiple(Type type)
        {
            try
            {
                return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
            }
            catch
            {
                return true;
            }
        }

        private static string[] GetRequiredByComponents(GameObject go, Type targetType)
        {
            try
            {
                return go.GetComponents<Component>()
                    .Where(c => c != null && c.GetType() != targetType)
                    .Where(c => c.GetType().GetCustomAttributes(typeof(RequireComponent), true)
                        .OfType<RequireComponent>()
                        .Any(r => r.m_Type0 == targetType || r.m_Type1 == targetType || r.m_Type2 == targetType))
                    .Select(c => c.GetType().Name)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static (PropertyInfo prop, FieldInfo field) FindMember(Type type, string memberName)
        {
            if (type == null || string.IsNullOrEmpty(memberName))
                return (null, null);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var prop = type.GetProperty(memberName, flags);
            var field = type.GetField(memberName, flags);
            return (prop, field);
        }

        private static object ResolveSceneReference(Type targetType, string referencePath, string referenceName)
        {
            var (go, error) = GameObjectFinder.FindOrError(referenceName, 0, referencePath);
            if (error != null || go == null)
                return null;

            if (targetType == typeof(GameObject))
                return go;
            if (targetType == typeof(Transform))
                return go.transform;
            if (typeof(Component).IsAssignableFrom(targetType))
                return go.GetComponent(targetType);
            return null;
        }

        private static void SetPlanDetails(
            IDictionary<string, object> plan,
            List<object> steps,
            List<object> create = null,
            List<object> modify = null,
            List<object> delete = null,
            IDictionary<string, object> extra = null)
        {
            if (plan == null)
                return;

            plan["steps"] = steps?.ToArray() ?? Array.Empty<object>();
            plan["changes"] = new Dictionary<string, object>
            {
                ["create"] = (create ?? new List<object>()).ToArray(),
                ["modify"] = (modify ?? new List<object>()).ToArray(),
                ["delete"] = (delete ?? new List<object>()).ToArray()
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                    plan[pair.Key] = pair.Value;
            }
        }

        private static void MarkSemantic(IDictionary<string, object> plan)
        {
            if (plan != null)
                plan["planLevel"] = "semantic";
        }

        private static string GetStringArg(JObject args, params string[] keys)
        {
            if (args == null)
                return null;

            foreach (var key in keys)
            {
                if (args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                    return token.ToString();
            }
            return null;
        }

        private static int GetIntArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<int>(); } catch { }
            }
            return 0;
        }

        private static float GetFloatArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<float>(); } catch { }
            }
            return 0f;
        }

        private static void AddErrorFromValidation(SkillRouter.ParameterValidationResult validation, object result, string field)
        {
            if (result == null)
                return;
            AddSemanticError(validation, field, ExtractError(result));
        }

        private static void AddSemanticError(SkillRouter.ParameterValidationResult validation, string field, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            validation.SemanticErrors.Add(new Dictionary<string, object>
            {
                ["field"] = field,
                ["error"] = message
            });
        }

        private static void AddWarning(SkillRouter.ParameterValidationResult validation, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            validation.Warnings.Add(message);
        }

        private static string ExtractError(object result)
        {
            return SkillResultHelper.TryGetError(result, out var errorText) ? errorText : result?.ToString() ?? "Unknown error";
        }

        private static string ExtractSemanticMessage(object semanticError)
        {
            if (SkillResultHelper.TryGetMemberValue(semanticError, "error", out var value) && value != null)
                return value.ToString();
            return semanticError?.ToString() ?? "Unknown semantic error";
        }
    }
}
