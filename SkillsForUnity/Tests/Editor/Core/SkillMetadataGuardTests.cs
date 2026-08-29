using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// A registry-wide metadata guard, pinned at "zero violations".
    ///
    /// <para>The declarative metadata here isn't documentation — it's what the runtime actually acts on.
    /// <c>ReadOnly</c> decides whether a surface profile withdraws a given skill, so a write operation
    /// mis-tagged <c>ReadOnly=true</c> remains callable even under a profile designed specifically to withdraw
    /// it. <c>MutatesScene</c>/<c>MutatesAssets</c> decide what a profile withdraws, <c>TracksWorkflow</c>
    /// decides whether a call can be undone, and <c>RiskLevel</c> is what an agent reads before deciding
    /// whether to ask the user to confirm. Each of these is a gate, and a wrong declaration walks straight through it.</para>
    ///
    /// <para>The violation count is currently zero. Pinning it at zero is the whole point of this file: nobody
    /// introduces this kind of self-contradiction on purpose, so the useful moment to catch it is the commit
    /// that introduces it — not some release later, when it turns out a given profile was never actually hiding anything.</para>
    ///
    /// <para>No skill count is hardcoded here. The registry's size shifts with installed optional packages, so
    /// everything is derived at runtime; what's asserted is "the violation set is empty".</para>
    /// </summary>
    [TestFixture]
    public class SkillMetadataGuardTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            // ValidateMetadata audits the whole registry, while the snapshot helper used by the independently
            // derived checks below respects the surface profile. Pin it to full so both sides see the same
            // skill set; restore afterward, since this pref is global.
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
        }

        /// <summary>
        /// Audits its own ERROR tier, pinned at zero.
        ///
        /// <para>Only covers ERROR. The WARN tier is an aspirational pursuit — "Tags is empty", "Operation not
        /// set" — pinning that at zero too would turn every added skill into a burden, and the whole assertion
        /// would eventually get deleted. ERROR is reserved for self-contradictory declarations, which is a
        /// different kind of thing entirely: no reasonable codebase state could ever make it true.</para>
        /// </summary>
        [Test]
        public void ValidateMetadata_ReportsNoErrors()
        {
            var errors = SkillRouter.ValidateMetadata()
                .Where(issue => issue.StartsWith("[ERROR]", StringComparison.Ordinal))
                .OrderBy(issue => issue, StringComparer.Ordinal)
                .ToArray();

            Assert.That(errors, Is.Empty,
                $"{errors.Length} metadata contradiction(s). Each one defeats a runtime gate rather " +
                "than merely reading oddly — see SkillRouter.ValidateMetadata for what each implies:\n" +
                string.Join("\n", errors));
        }

        /// <summary>
        /// The impact declared by <c>X_batch</c> must be no less than that of <c>X</c>.
        ///
        /// <para>This deliberately re-derives the check rather than reading the conclusion from
        /// <c>ValidateMetadata</c>. If that rule were ever removed from the audit, both sides would pass —
        /// expected and actual would zero out together, and the assertion would stay green. The cost of this
        /// duplicate copy is having to update it in lockstep when the rule changes, and that break is exactly the alarm we want.</para>
        ///
        /// <para>An under-declared batch skill is the kind that sails through every gate its singular twin gets
        /// stopped by, while acting on N objects at once instead of one.</para>
        /// </summary>
        [Test]
        public void EveryBatchSkill_DeclaresAtLeastTheImpactOfItsSingularTwin()
        {
            const string suffix = "_batch";
            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered();
            var byName = registry.ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);

            var violations = new List<string>();
            int pairsChecked = 0;

            foreach (var batch in registry
                         .Where(s => s.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                var singularName = batch.Name.Substring(0, batch.Name.Length - suffix.Length);
                // Only recognizes strict X / X_batch pairs. Batch skills whose twin name is spelled differently
                // (material_set_colors_batch vs. material_set_color) are skipped rather than guessed at.
                if (!byName.TryGetValue(singularName, out var single))
                    continue;

                pairsChecked++;

                if (single.MutatesScene && !batch.MutatesScene)
                    violations.Add($"{batch.Name}: MutatesScene=false but {singularName} declares true");
                if (single.MutatesAssets && !batch.MutatesAssets)
                    violations.Add($"{batch.Name}: MutatesAssets=false but {singularName} declares true");
                if (single.TracksWorkflow && !batch.TracksWorkflow)
                    violations.Add($"{batch.Name}: TracksWorkflow=false but {singularName} declares true");
                if (RiskRank(batch.RiskLevel) < RiskRank(single.RiskLevel))
                    violations.Add($"{batch.Name}: RiskLevel='{batch.RiskLevel}' below {singularName}'s '{single.RiskLevel}'");
                if (single.ReadOnly != batch.ReadOnly)
                    violations.Add($"{batch.Name}: ReadOnly={batch.ReadOnly} but {singularName} declares {single.ReadOnly}");
            }

            Assert.That(pairsChecked, Is.GreaterThan(0),
                "No X / X_batch pairs found — the mirror check would be vacuous. Did the naming convention change?");
            Assert.That(violations, Is.Empty,
                $"{violations.Count} batch skill(s) declare less impact than the singular skill they " +
                "repeat N times over:\n" + string.Join("\n", violations));
        }

        /// <summary>low &lt; medium &lt; high; everything else ranks lowest, matching the attribute default.</summary>
        private static int RiskRank(string riskLevel)
        {
            if (string.Equals(riskLevel, "high", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(riskLevel, "medium", StringComparison.OrdinalIgnoreCase)) return 1;
            return 0;
        }

        /// <summary>
        /// The most load-bearing consequence of a mis-declared <c>ReadOnly</c>, restated independently of the
        /// audit. A surface profile never hides a read-only skill, so a write operation carrying
        /// <c>ReadOnly=true</c> is exactly the skill that survives under a profile designed to withdraw it —
        /// and it survives silently, because from the outside the profile still looks like it's filtering.
        /// </summary>
        [Test]
        public void NoReadOnlySkill_AlsoDeclaresItMutatesSomething()
        {
            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && (s.MutatesScene || s.MutatesAssets))
                .Select(s => $"{s.Name} (MutatesScene={s.MutatesScene}, MutatesAssets={s.MutatesAssets})")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "These skills claim to be read-only while declaring that they mutate. The surface " +
                "profile never hides a read-only skill, so each of these stays callable under a " +
                $"profile that exists to withdraw it:\n{string.Join("\n", contradictory)}");
        }

        [Test]
        public void NoReadOnlySkill_DeclaresAWriteOperation()
        {
            const SkillOperation writeOps = SkillOperation.Create | SkillOperation.Modify | SkillOperation.Delete;

            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && (s.Operation & writeOps) != 0)
                .Select(s => $"{s.Name} (Operation={s.Operation})")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "Read-only skills declaring a Create/Modify/Delete operation:\n" +
                string.Join("\n", contradictory));
        }

        [Test]
        public void NoReadOnlySkill_AlsoTracksWorkflow()
        {
            // TracksWorkflow means "this call gets snapshotted so it can be undone". A read-only skill has
            // nothing to undo, so both being true at once means one of the declarations is wrong — and if the
            // wrong one is ReadOnly, this skill is also dodging surface-profile filtering at the same time.
            var contradictory = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ReadOnly && s.TracksWorkflow)
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(contradictory, Is.Empty,
                "Read-only skills that also track workflow (nothing to roll back): " +
                string.Join(", ", contradictory));
        }

        /// <summary>
        /// Two skills that used to be declared <c>ReadOnly=true</c> while actually writing to disk.
        /// <c>scene_dependency_analyze</c> writes a markdown report (its own <c>savedTo</c> output names the
        /// file), and <c>scriptableobject_export_json</c> writes a JSON file. So neither of these can be hidden by any profile.
        ///
        /// <para>These are called out by name rather than left to the registry-wide scans above, because
        /// neither declares MutatesAssets nor a write-type Operation — no derived check could catch them if
        /// they regressed. This assertion is specifically about these two skills.</para>
        /// </summary>
        [TestCase("scene_dependency_analyze")]
        [TestCase("scriptableobject_export_json")]
        public void FileWritingAnalysisSkills_AreNotDeclaredReadOnly(string skill)
        {
            Assume.That(SkillRouter.TryGetSkill(skill, out var info), Is.True, $"{skill} is not registered.");

            Assert.That(info.ReadOnly, Is.False,
                $"{skill} writes a file to the project, so ReadOnly=true makes it unhideable by " +
                "every surface profile — the one property no profile can withdraw.");
        }

        /// <summary>
        /// <c>gameobject_get_info</c> is the skill an agent uses to learn everything about an object in one
        /// call, and <c>Outputs</c> is exactly what tells it "which follow-up calls it doesn't need to send".
        /// An under-declared Outputs means paying for an extra round trip for every key the response already carries but doesn't advertise.
        ///
        /// <para>The count is asserted alongside the names, so a "swap" can't sneak past: replace one key with
        /// another and the total is still 15.</para>
        /// </summary>
        [Test]
        public void GameObjectGetInfo_DeclaresAllFifteenOutputs()
        {
            Assume.That(SkillRouter.TryGetSkill("gameobject_get_info", out var info), Is.True);

            var expected = new[]
            {
                "name", "entityId", "instanceId", "path", "tag", "layer", "isActive",
                "position", "rotation", "scale", "parent", "parentPath", "childCount",
                "children", "components",
            };

            Assert.That(info.Outputs, Is.EquivalentTo(expected),
                "Outputs drifted from the response this skill actually returns:\n" +
                $"declared: {string.Join(", ", info.Outputs ?? Array.Empty<string>())}");
            Assert.That(info.Outputs.Length, Is.EqualTo(15));
            Assert.That(info.Outputs.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(info.Outputs.Length),
                "Duplicate output keys.");
        }

        /// <summary>
        /// Declared outputs must be unique everywhere. A duplicate entry is harmless at runtime and invisible
        /// during review, which is exactly how it survives — but it bloats every manifest that carries this
        /// record, and makes "count" meaningless as a completeness signal.
        /// </summary>
        [Test]
        public void NoSkill_DeclaresDuplicateOutputsOrTags()
        {
            var offenders = new List<string>();

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered()
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (skill.Outputs != null &&
                    skill.Outputs.Distinct(StringComparer.Ordinal).Count() != skill.Outputs.Length)
                {
                    offenders.Add($"{skill.Name}: duplicate Outputs [{string.Join(", ", skill.Outputs)}]");
                }

                if (skill.Tags != null &&
                    skill.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != skill.Tags.Length)
                {
                    offenders.Add($"{skill.Name}: duplicate Tags [{string.Join(", ", skill.Tags)}]");
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// An alias skill must mirror its target's impact declarations. An alias is the same code invoked under
        /// a second name, so once the declarations diverge, one of the two names ends up under the wrong
        /// gate's jurisdiction — and there's no way for an agent to tell which one.
        /// </summary>
        [TestCase("light_get_properties", "light_get_info")]
        public void AliasSkill_MirrorsItsTargetsImpactDeclarations(string alias, string target)
        {
            Assume.That(SkillRouter.TryGetSkill(alias, out var aliasInfo), Is.True, $"{alias} is not registered.");
            Assume.That(SkillRouter.TryGetSkill(target, out var targetInfo), Is.True, $"{target} is not registered.");

            Assert.That(aliasInfo.ReadOnly, Is.EqualTo(targetInfo.ReadOnly));
            Assert.That(aliasInfo.MutatesScene, Is.EqualTo(targetInfo.MutatesScene));
            Assert.That(aliasInfo.MutatesAssets, Is.EqualTo(targetInfo.MutatesAssets));
            Assert.That(aliasInfo.TracksWorkflow, Is.EqualTo(targetInfo.TracksWorkflow));
            Assert.That(aliasInfo.RiskLevel, Is.EqualTo(targetInfo.RiskLevel));
            Assert.That(aliasInfo.Category, Is.EqualTo(targetInfo.Category));
            Assert.That(aliasInfo.Outputs, Is.EqualTo(targetInfo.Outputs));
        }

        /// <summary>
        /// Every candidate name in <c>SkillPlanningService._requiredInputGroups</c> must be a parameter name
        /// that "at least one skill declaring that token actually accepts".
        ///
        /// <para>Group validation intersects the candidate names against a skill's own parameter set, so a name
        /// no skill accepts gets silently dropped — it never fails, never fires, and reads like coverage when
        /// there is none. Two such names were once shipped: "materialPath" (0 of the 16 skills declaring the
        /// material token accept it; what they actually take is the dual-purpose <c>path</c>), and "path" under
        /// the assetPath token. This test is also the reason "componentName" was kept during the same cleanup
        /// pass: exactly one skill (smart_reference_bind) accepts it, and removing it would have silently
        /// dropped that skill's target validation.</para>
        /// </summary>
        [Test]
        public void RequiredInputGroups_NameOnlyRealParameters()
        {
            var groups = RequiredInputGroups();
            Assume.That(groups, Is.Not.Null.And.Not.Empty,
                "SkillPlanningService._requiredInputGroups was renamed or emptied.");

            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered().ToArray();
            var offenders = new List<string>();
            foreach (var group in groups)
            {
                var declaring = registry
                    .Where(s => s.RequiresInput != null &&
                                s.RequiresInput.Any(t => string.Equals(t, group.Key, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                if (declaring.Length == 0)
                    continue;

                foreach (var candidate in group.Value)
                {
                    if (!declaring.Any(s => SkillAcceptsParameter(s, candidate)))
                    {
                        offenders.Add($"token '{group.Key}' offers '{candidate}', accepted by none of its " +
                                      $"{declaring.Length} declaring skill(s)");
                    }
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// The second half of an "A or B" style RequiresInput token must be a key the caller can actually send.
        /// <c>gameObject</c> is exempt, since it's the one purely semantic token in the word list (representing
        /// name/path/instanceId/entityId); skills whose sole locator vehicle is <c>items</c> are also exempt,
        /// since every <c>*_batch</c> skill's locator parameters live inside the array.
        ///
        /// <para>What this catches: <c>material_set_color</c> advertises "gameObject|materialPath" externally,
        /// yet rejects <c>materialPath</c> as an unknown parameter — and that name does exist on
        /// <c>material_assign</c> in the same module, so an agent generalizes it over and gets rejected for
        /// correctly reading the metadata.</para>
        /// </summary>
        [Test]
        public void CompoundRequiredInputTokens_NameAKeyTheSkillAccepts()
        {
            var offenders = new List<string>();

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered()
                         .Where(s => s.RequiresInput != null)
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (SkillAcceptsParameter(skill, "items"))
                    continue;

                foreach (var token in skill.RequiresInput)
                {
                    if (token == null || token.IndexOf('|') < 0)
                        continue;

                    foreach (var part in token.Split('|'))
                    {
                        if (string.Equals(part, "gameObject", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!SkillAcceptsParameter(skill, part))
                            offenders.Add($"{skill.Name}: token '{token}' names '{part}', which it does not accept");
                    }
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// Twelve skills caught by the 2026-08-23 live-machine smoke scan: they proceed to execute against an
        /// empty request body, then fail deep inside their own implementation. Each one needs an argument but
        /// doesn't declare <c>RequiresInput</c>, and their parameters are neither value types nor have a CLR
        /// default, so <c>IsParameterRequired</c> judges them all optional. The schema says "nothing is
        /// required", dryRun says <c>valid:true</c>, and the failure only shows up after execution.
        ///
        /// <para>Two assertions, because either one alone could be satisfied by a wrong fix. The token check
        /// catches the B3 trap — a token that names a key the skill doesn't accept enforces nothing, yet reads
        /// like coverage (a bare parameter name must be accepted; an "A|B" or semantic token must intersect
        /// non-emptily with the skill's parameters via <c>_requiredInputGroups</c>). The dryRun check catches
        /// the opposite mistake: metadata that looks correct but is actually unreachable, with an empty request
        /// body still validating as legal.</para>
        ///
        /// <para>Registration is asserted, not assumed. All twelve live in modules that compile whether or not
        /// the optional package is installed (package detection happens inside the method body), so a missing
        /// name here means the skill was renamed or removed, not that a package is missing.</para>
        /// </summary>
        [TestCase("batch_replace_material")]
        [TestCase("batch_set_render_layer")]
        [TestCase("behavior_blackboard_list")]
        [TestCase("decal_get_info")]
        [TestCase("find_objects_by_name")]
        [TestCase("netcode_get_network_object_info")]
        [TestCase("netcode_list_network_prefabs")]
        [TestCase("script_find_in_file")]
        [TestCase("shader_find")]
        [TestCase("smart_scene_query")]
        [TestCase("yooasset_get_build_settings")]
        [TestCase("yooasset_runtime_get_validation_result")]
        public void SkillsNeedingAnArgument_DeclareItAndRefuseAnEmptyBodyBeforeExecuting(string skillName)
        {
            Assert.That(SkillRouter.TryGetSkill(skillName, out var skill), Is.True,
                $"{skillName} is not registered.");

            Assert.That(skill.RequiresInput, Is.Not.Null.And.Not.Empty,
                $"{skillName} cannot do anything without an argument, so it must declare RequiresInput — " +
                "without it the schema advertises every parameter as optional and an empty body " +
                "executes into a failure the caller was told would not happen.");

            var groups = RequiredInputGroups();
            foreach (var token in skill.RequiresInput)
            {
                bool namesAnAcceptedKey = token.Split('|').Any(part => SkillAcceptsParameter(skill, part));
                bool resolvesThroughAGroup = groups.TryGetValue(token, out var candidates) &&
                                             candidates.Any(candidate => SkillAcceptsParameter(skill, candidate));

                Assert.That(namesAnAcceptedKey || resolvesThroughAGroup, Is.True,
                    $"{skillName}: RequiresInput token '{token}' neither names a parameter it accepts nor " +
                    "maps to a group whose candidates it accepts, so it enforces nothing and an agent " +
                    "reading it literally gets UNKNOWN_PARAM.");
            }

            var dry = JObject.Parse(SkillRouter.DryRun(skillName, "{}"));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                $"An empty body still dry-runs as valid for {skillName}: {dry["validation"]?.ToString(Formatting.None)}");
        }

        private static bool SkillAcceptsParameter(SkillRouter.SkillInfo skill, string parameterName)
        {
            if (skill.AllowedParameterSet != null)
                return skill.AllowedParameterSet.Contains(parameterName);

            return skill.ParameterNames != null &&
                   skill.ParameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// That private group-mapping table, read via reflection: it's the thing under test, so restating it
        /// here would just be testing a copy of itself.
        /// </summary>
        private static Dictionary<string, string[]> RequiredInputGroups()
        {
            var field = typeof(SkillPlanningService).GetField(
                "_requiredInputGroups", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "SkillPlanningService._requiredInputGroups was renamed.");
            return field.GetValue(null) as Dictionary<string, string[]>;
        }
    }
}

// Producer:Betsy
