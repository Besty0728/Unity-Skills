using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        /// <summary>
        /// The semantic planner of the particle write skills. Validation is ParticleSkills' own Prepare path (the one
        /// execution runs), so dryRun and execution reach the same verdict; this side only resolves locators, which is
        /// where objects an earlier step of a /skills/batch dry run will create become nameable.
        /// </summary>
        private static void AnalyzeParticleSkill(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (validation?.Args == null)
                return;

            // A nested configJson object arrives already parsed, and parsing kept only the last of two equal keys; the raw
            // body still shows them. A JSON-string configJson is checked by the codec itself.
            foreach (var duplicate in ParticleSkills.DuplicateKeyPaths(validation.RawJson))
                AddSemanticError(validation, duplicate, $"Duplicate key at {duplicate}: every key may appear once (a JSON parser keeps only the last one). Merge them.");

            var outcome = ParticleSkills.Plan(skill.Name, validation.Args, ResolveParticleTarget);
            foreach (var package in outcome.MissingPackages)
            {
                if (!validation.MissingPackages.Contains(package))
                    validation.MissingPackages.Add(package);
            }
            foreach (var error in outcome.Errors)
                AddSemanticError(validation, error.Key, error.Value);
            foreach (var warning in outcome.Warnings)
                AddWarning(validation, warning);

            if (plan == null || !outcome.Semantic)
                return;
            MarkSemantic(plan);
            SetPlanDetails(plan, outcome.Steps, outcome.Create, outcome.Modify, outcome.Delete,
                outcome.Extra.Count > 0 ? outcome.Extra : null);
        }

        /// <summary>
        /// Resolves the locator keys with <paramref name="prefix"/> ("" = name/instanceId/path/entityId,
        /// "parent" = parentName/parentInstanceId/parentPath/parentEntityId) the way every planner does.
        /// </summary>
        private static ParticleSkills.PlanTarget ResolveParticleTarget(JObject args, string prefix)
        {
            string Key(string suffix) => string.IsNullOrEmpty(prefix) ? suffix : prefix + char.ToUpperInvariant(suffix[0]) + suffix.Substring(1);

            var (hasLocator, name, instanceId, path, entityId) = ReadObjectLocator(args, Key("name"), Key("instanceId"), Key("path"), Key("entityId"));
            if (!hasLocator)
                return ParticleSkills.PlanTarget.None;

            var resolution = ResolveLocator(name, instanceId, path, entityId);
            if (resolution.Error != null)
                return new ParticleSkills.PlanTarget { Kind = ParticleSkills.PlanTargetKind.Failed, Error = resolution.Error };
            if (resolution.IsPending)
                return new ParticleSkills.PlanTarget { Kind = ParticleSkills.PlanTargetKind.Pending, PendingPath = resolution.PendingPath };
            return new ParticleSkills.PlanTarget { Kind = ParticleSkills.PlanTargetKind.Live, Live = resolution.LiveObject };
        }
    }
}

// Producer:Betsy
