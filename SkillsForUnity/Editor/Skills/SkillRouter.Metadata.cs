using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        /// <summary>
        /// Validates the metadata completeness and consistency of every discovered skill.
        /// Returns a set of diagnostic messages (prefixed with WARN/ERROR).
        /// </summary>
        public static List<string> ValidateMetadata()
        {
            Initialize();
            var issues = new List<string>();

            foreach (var s in _skills.Values)
            {
                if (s.Category == SkillCategory.Uncategorized)
                    issues.Add($"[WARN] {s.Name}: Category is Uncategorized");

                if (s.Operation == 0)
                    issues.Add($"[WARN] {s.Name}: Operation not specified");

                if (s.ReadOnly && s.TracksWorkflow)
                    issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with TracksWorkflow=true");

                // ReadOnly isn't just documentation, it's load-bearing: a surface profile never hides a read-only skill,
                // so a write operation mislabeled ReadOnly=true remains callable under a profile that exists specifically
                // to withdraw that kind of write. The three checks below are exactly the self-contradictions a mislabel would cause; they're ERROR rather than WARN,
                // because each one silently breaks a user-facing guarantee.
                if (s.ReadOnly)
                {
                    if (s.MutatesScene)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with MutatesScene=true (a read-only skill is never hidden by the surface profile)");

                    if (s.MutatesAssets)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with MutatesAssets=true (a read-only skill is never hidden by the surface profile)");

                    var writeOps = FormatOperation(s.Operation & (SkillOperation.Create | SkillOperation.Modify | SkillOperation.Delete));
                    if (writeOps != null)
                        issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with write Operation {string.Join("|", writeOps)}");
                }

                if (s.Tags == null || s.Tags.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Tags is empty");

                if (s.Outputs == null || s.Outputs.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Outputs is empty");

                if (s.Operation.HasFlag(SkillOperation.Delete) || s.Operation.HasFlag(SkillOperation.Modify))
                {
                    if (s.RequiresInput == null || s.RequiresInput.Length == 0)
                        issues.Add($"[WARN] {s.Name}: Delete/Modify operation but RequiresInput is empty");
                }

                if (s.MayEnterPlayMode && s.ReadOnly)
                    issues.Add($"[WARN] {s.Name}: MayEnterPlayMode=true but ReadOnly=true seems inconsistent");

                if (!s.SupportsDryRun && s.ReadOnly)
                    issues.Add($"[WARN] {s.Name}: SupportsDryRun=false but ReadOnly=true — read-only skills should support dry run");

                // RiskLevel is a free-form string, and RiskRank silently ranks any value it doesn't recognize as "low".
                // So a typo ("hgih") doesn't fail explicitly -- it demotes that skill to the lowest risk,
                // which is exactly the field an agent reads when deciding whether to confirm with the user,
                // and also the field AppendBatchMirrorIssues uses to compare a batch against its singular counterpart.
                // Every declaration shipped with the package today is valid; this check exists so the next one doesn't get miswritten in the direction of hiding risk.
                if (!IsKnownRiskLevel(s.RiskLevel))
                    issues.Add($"[WARN] {s.Name}: RiskLevel='{s.RiskLevel}' is not one of low/medium/high — it ranks as 'low'");
            }

            AppendBatchMirrorIssues(issues);

            return issues;
        }

        private const string BatchSkillSuffix = "_batch";

        /// <summary>
        /// Cross-skill rule: what <c>X_batch</c> declares must not have a smaller impact footprint than <c>X</c>.
        ///
        /// <para>A batch skill does the same work as the singular skill, N times over, so it can never mutate less, track less, or carry less risk.
        /// If the metadata says otherwise, the batch entry was written wrong, and the consequences aren't trivial:
        /// MutatesScene/MutatesAssets decide what a surface profile withdraws, TracksWorkflow decides whether this call
        /// can be undone, and RiskLevel is the field an agent reads before deciding whether to confirm with the user.
        /// An under-declared batch thus becomes the variant that slips through every gate meant to stop its singular twin --
        /// and it touches N objects instead of one.</para>
        ///
        /// <para>Only checks a strict <c>X</c>/<c>X_batch</c> name pairing. A singular twin spelled differently
        /// (material_set_colors_batch <-> material_set_color), or a batch skill with no twin at all,
        /// is skipped entirely, with no guessing.</para>
        /// </summary>
        private static void AppendBatchMirrorIssues(List<string> issues)
        {
            foreach (var batch in _skills.Values)
            {
                if (!batch.Name.EndsWith(BatchSkillSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var singularName = batch.Name.Substring(0, batch.Name.Length - BatchSkillSuffix.Length);
                if (!_skills.TryGetValue(singularName, out var single))
                    continue;

                if (single.MutatesScene && !batch.MutatesScene)
                    issues.Add($"[ERROR] {batch.Name}: MutatesScene=false but {singularName} declares MutatesScene=true");

                if (single.MutatesAssets && !batch.MutatesAssets)
                    issues.Add($"[ERROR] {batch.Name}: MutatesAssets=false but {singularName} declares MutatesAssets=true");

                if (single.TracksWorkflow && !batch.TracksWorkflow)
                    issues.Add($"[ERROR] {batch.Name}: TracksWorkflow=false but {singularName} declares TracksWorkflow=true");

                if (RiskRank(batch.RiskLevel) < RiskRank(single.RiskLevel))
                    issues.Add($"[ERROR] {batch.Name}: RiskLevel='{batch.RiskLevel}' is below {singularName}'s '{single.RiskLevel}'");
            }
        }

        /// <summary>
        /// low &lt; medium &lt; high. Everything else ranks the same as "low" -- that's not a fallback, it's a fact:
        /// <see cref="UnitySkillAttribute.RiskLevel"/>'s default value is "low",
        /// so an unrecognized or missing level genuinely is the lowest risk declaration available.
        /// </summary>
        private static int RiskRank(string riskLevel)
        {
            if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// Whether <paramref name="riskLevel"/> is a level <see cref="RiskRank"/> genuinely recognizes.
        /// Ranking an unknown string as "low" is correct runtime behavior, but staying silent about it isn't,
        /// so <see cref="ValidateMetadata"/> raises a WARN for it.
        /// </summary>
        private static bool IsKnownRiskLevel(string riskLevel) =>
            string.Equals(riskLevel, "low", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase);
    }
}

// Producer:Betsy
