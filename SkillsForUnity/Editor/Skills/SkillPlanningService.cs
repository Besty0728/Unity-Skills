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
        public static void ApplySemanticValidation(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return;

            ApplySemanticPlanner(skill, validation, null);
        }

        /// <summary>
        /// Returns steps/changes data embeddable in a DryRun response; returns null when the skill has no semantic planner.
        /// </summary>
        public static IDictionary<string, object> BuildPlanData(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
        {
            if (skill == null || validation == null)
                return null;

            var plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ApplySemanticPlanner(skill, validation, plan);

            // planLevel was never set to "semantic", meaning no planner matched
            if (!plan.ContainsKey("planLevel") || !"semantic".Equals(plan["planLevel"]?.ToString(), StringComparison.OrdinalIgnoreCase))
                return null;

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (plan.ContainsKey("steps"))
                result["steps"] = plan["steps"];
            if (plan.ContainsKey("changes"))
                result["changes"] = plan["changes"];
            return result.Count > 0 ? result : null;
        }

        /// <summary>
        /// The payload for <c>?mode=plan</c>. Returns a concrete dictionary rather than <c>object</c>, so callers
        /// can append their own envelope-level blocks (the surface profile's <c>authorization</c> preview) without
        /// reflection; key insertion order is serialization order, so appended blocks land after "note".
        /// </summary>
        public static Dictionary<string, object> BuildPlan(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation)
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
                ["unknownParams"] = validation?.UnknownParams.Count > 0 ? validation.UnknownParams.ToArray() : null,
                ["typeErrors"] = validation?.TypeErrors.Count > 0 ? validation.TypeErrors.ToArray() : null,
                ["semanticErrors"] = validation?.SemanticErrors.Count > 0 ? validation.SemanticErrors.ToArray() : null,
                ["missingPackages"] = validation?.MissingPackages.Count > 0 ? validation.MissingPackages.ToArray() : null,
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

            var entityId = GetStringArg(args, "entityId");
            if (!string.IsNullOrWhiteSpace(entityId))
                return $"entityId:{entityId}";

            return "(unspecified)";
        }

        // RequiresInput holds *semantic* tokens ("gameObject", "assetPath", "component"), not parameter names -
        // so SkillRouter.IsParameterRequired comparing by name almost never hits; before this check existed,
        // the 136 skills that can't act without a target all dryRun'd an empty body as valid:true.
        // Each token maps to the parameter names that satisfy it; a widely-used token is mapped only once five+
        // skills declare it; the rest keep old behavior instead of guessing. The exception: a mapping written
        // *together with* a specific skill's token (last two entries) - not a guess, but a mandate that ships with it.
        // Every candidate must be a name genuinely accepted by some skill declaring this token. The intersection
        // below silently drops the rest, so a name no skill accepts isn't a harmless fallback: it reads like a cover that doesn't exist.
        // Two names were removed for this: "materialPath" under material (none of the 16 declaring skills accept it,
        // the material asset path goes through the dual-purpose `path`), and "path" under assetPath.
        // "componentName" was kept because smart_reference_bind does accept it - exactly the case easiest to miss
        // by eye: "only one real consumer".
        // The rule is pinned by SkillMetadataGuardTests.RequiredInputGroups_NameOnlyRealParameters.
        private static readonly Dictionary<string, string[]> _requiredInputGroups =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["gameObject"] = new[] { "name", "path", "instanceId", "entityId" },
            // "A or B" token: the material setter can act on either a scene object or a material asset,
            // and both are passed in via the same `path` parameter - which is why the token is written as `path`
            // rather than "materialPath", which those skills don't accept at all.
            ["gameObject|path"] = new[] { "name", "path", "instanceId", "entityId" },
            ["assetPath"] = new[] { "assetPath" },
            ["component"] = new[] { "componentType", "componentName" },
            // The two entries below are each declared by only one skill, yet are still mapped: written alongside
            // that skill's token (2026-08-23) - not guesswork; without the mapping the token would read like a contract
            // that enforces nothing, exactly why these two skills used to dryRun an empty body as valid.
            // find_objects_by_name: `name` is the documented alias of `nameContains`.
            ["nameContains|name"] = new[] { "nameContains", "name" },
            // behavior_blackboard_list: accepts either a graph asset path *or* an agent locator. entityId belongs here
            // (this skill accepts instanceId + name + path, so the synthesized locator works for it) - omitting it would
            // stack another "provide one of ..." on top of an entityId that simply failed to resolve. find_objects_by_name
            // above omits entityId for the mirror-image reason: it has no instanceId, so the synthesized locator isn't
            // available, and a candidate accepted by no declaring skill reads like a cover that doesn't actually exist
            // (SkillMetadataGuardTests.RequiredInputGroups_NameOnlyRealParameters).
            ["gameObject|graphAssetPath"] = new[] { "name", "path", "instanceId", "entityId", "graphAssetPath" },
            // model_get_mesh_info: locate by scene GameObject (mesh on a MeshFilter/SkinnedMeshRenderer) or by
            // asset path (mesh imported from a model file) - the only compound token that was never registered
            // here, so an empty body used to dryRun as valid and fail only once GameObjectFinder.FindOrError ran
            // (SkillMetadataGuardTests.CompoundRequiredInputTokens_NameAKeyTheSkillAccepts now also checks that
            // every compound token is a registered key here, not just that each half names a real parameter).
            ["gameObject|assetPath"] = new[] { "name", "path", "instanceId", "entityId", "assetPath" },
            // dotween_pro_set_loops: two mutually independent halves, either one alone makes a complete request.
            // Same reasoning as the two entries above (written 2026-08-23 alongside this skill's token) - without the
            // mapping the token enforces nothing, and "neither passed" is exactly the call that used to succeed and
            // silently reset loops to the CLR default of 1. Note HasUsableArgument treats numeric 0 as unusable,
            // which is correct here: 0 isn't a loop count DOTween accepts either (skill explains more at execution time).
            ["loops|loopType"] = new[] { "loops", "loopType" },
            // 2026-09 cleanup of the 24 unregistered semantic-locator tokens named in
            // SkillMetadataGuardTests.RequiresInput_SingleTokensNameARealParameterOrGroupKey's (now near-empty)
            // allowlist. Each entry below is written alongside the specific skill(s) whose real parameter shape
            // it describes - not a guess. Simple, single-shape locators reuse the token name as-is; skills whose
            // locator shape collides with another skill using the same word (Cinemachine's mixing-camera and
            // state-driven-camera skills each juggle two *different* locators under one word) get their own
            // compound "A|B|..." key instead, exactly to avoid the vcamName-only trap documented on
            // ApplyRequiredInputGroups: reusing a group whose candidates the skill only partially accepts can
            // silently narrow "provide one of several" down to "provide exactly this one".
            ["vcam"] = new[] { "vcamName", "instanceId", "path", "entityId" },
            ["camera"] = new[] { "cameraName", "cameraInstanceId", "cameraPath" },
            ["source"] = new[] { "sourceName", "sourceInstanceId", "sourcePath" },
            ["sequencer"] = new[] { "sequencerName", "sequencerInstanceId", "sequencerPath", "sequencerEntityId" },
            ["splineContainer"] = new[] { "splineName", "splineInstanceId", "splinePath" },
            // cinemachine_set_spline's vcam half uses vcamName/vcamInstanceId/vcamPath, not the plain
            // vcamName/instanceId/path shape the "vcam" group above covers - reusing "vcam" here would leave only
            // {vcamName} in the intersection and reject a legitimate vcamInstanceId-only or vcamPath-only call.
            ["vcamName|vcamInstanceId|vcamPath"] = new[] { "vcamName", "vcamInstanceId", "vcamPath" },
            // cinemachine_mixing_camera_set_weight juggles two different locators (the mixer, and the child vcam
            // being weighted) under two different real parameter prefixes - neither matches "vcam"/"gameObject".
            ["mixerName|mixerInstanceId|mixerPath|mixerEntityId"] = new[] { "mixerName", "mixerInstanceId", "mixerPath", "mixerEntityId" },
            ["childName|childInstanceId|childPath|childEntityId"] = new[] { "childName", "childInstanceId", "childPath", "childEntityId" },
            // Shared by cinemachine_state_driven_camera_add_instruction (the parent state-driven camera) and, for
            // the child half, also by cinemachine_sequencer_add_instruction - both name their child camera locator
            // childCameraName/childInstanceId/childPath/childEntityId identically.
            ["cameraName|cameraInstanceId|cameraPath|cameraEntityId"] = new[] { "cameraName", "cameraInstanceId", "cameraPath", "cameraEntityId" },
            ["childCameraName|childInstanceId|childPath|childEntityId"] = new[] { "childCameraName", "childInstanceId", "childPath", "childEntityId" },
            // cinemachine_target_group_add_member/remove_member: "groupName..." locates the TargetGroup,
            // "targetName..." locates the member being added/removed - neither is the generic name/path/instanceId shape.
            ["groupName|groupInstanceId|groupPath"] = new[] { "groupName", "groupInstanceId", "groupPath" },
            ["targetName|targetInstanceId|targetPath"] = new[] { "targetName", "targetInstanceId", "targetPath" },
        };
    }
}

// Producer:Betsy
