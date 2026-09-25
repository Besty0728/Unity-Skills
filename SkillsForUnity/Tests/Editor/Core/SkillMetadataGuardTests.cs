using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

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
        /// Any skill whose implementation actually records an undo step, a workflow snapshot, or a
        /// direct asset/file write must declare <c>MutatesScene</c> or <c>MutatesAssets</c>.
        ///
        /// <para>Found by the 2026-09 audit: 194 skills called <c>Undo.Register*</c> /
        /// <c>WorkflowManager.Snapshot*</c> / <c>AssetDatabase.CreateAsset|SaveAssets</c> / etc. while
        /// declaring neither flag. That's not just a wire-truthfulness gap — it's exactly the hole in
        /// <see cref="SkillsSurfaceProfile"/>'s NoSceneAuthoring profile, which hides every write
        /// declaring <c>MutatesScene</c> regardless of category (see its rule 4): an under-declared
        /// skill sails straight through that gate while still mutating the scene.</para>
        ///
        /// <para>Scans package source directly via <see cref="SourceMask"/> rather than trusting
        /// reflection, since the bug under test is in the declaration itself, and the two could
        /// otherwise cross-check nothing. Method bodies are located by name via
        /// <c>DeclaringType.Name + ".cs"</c> — every *Skills.cs file's class name matches its file
        /// name in this codebase, and a #if/#else pair under the same name has all of its bodies
        /// unioned, so a real (non-stub) implementation containing a marker is still caught even when
        /// the stub branch (returning <c>NoXRI()</c> etc.) doesn't.</para>
        ///
        /// <para>The exemption list is for implementations that legitimately write but not to scene or
        /// asset content: three Console toggles and two QFramework settings that only touch EditorPrefs
        /// or an internal editor-window field (see each skill's own doc comment), and the two Workflow
        /// skills whose entire job is *recording* a snapshot of another object's current state into
        /// workflow history — bookkeeping, not a mutation of the object itself.</para>
        /// </summary>
        [Test]
        public void SkillsRecordingUndoOrSnapshots_DeclareMutatesSceneOrAssets()
        {
            var exemptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["console_set_pause_on_error"] = "writes ConsoleWindow's s_ConsoleFlags static field (EditorPrefs fallback) - an editor session toggle, not scene/asset content",
                ["console_set_collapse"] = "same as console_set_pause_on_error",
                ["console_set_clear_on_play"] = "same as console_set_pause_on_error",
                ["qframework_set_reskit_build_options"] = "writes EditorPrefs keys ResKitView itself reads, plus ResKitEditorAPI.SimulationMode which is EditorPrefs-backed inside QFramework - no scene/asset write",
                ["qframework_set_editor_locale"] = "writes QFramework's own EditorPrefs-backed LocaleKitEditor.IsCN - no scene/asset write",
                ["workflow_snapshot_object"] = "records the target's *current* state into workflow history for a later manual-change rollback; does not itself modify the target",
                ["workflow_snapshot_created"] = "same as workflow_snapshot_object",
            };

            var root = GetSkillsSourceRoot();
            Assert.That(Directory.Exists(root), Is.True, $"Skill source directory not found: {root}");

            var maskedByType = new Dictionary<string, string>(StringComparer.Ordinal);
            string GetMasked(string typeName)
            {
                if (maskedByType.TryGetValue(typeName, out var cached))
                    return cached;

                var path = Path.Combine(root, typeName + ".cs");
                var masked = File.Exists(path) ? SourceMask.Mask(File.ReadAllText(path)) : null;
                maskedByType[typeName] = masked;
                return masked;
            }

            var issues = new List<string>();
            var checkedCount = 0;

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered().OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (skill.MutatesScene || skill.MutatesAssets)
                    continue;
                if (exemptions.ContainsKey(skill.Name))
                    continue;

                var declaringType = skill.Method?.DeclaringType;
                if (declaringType == null)
                    continue;

                var masked = GetMasked(declaringType.Name);
                if (masked == null)
                    continue;

                var bodies = SourceMask.FindMethodBodies(masked, skill.Method.Name);
                if (bodies.Count == 0)
                    continue;

                checkedCount++;

                var hit = FirstWriteMarker(bodies);
                if (hit != null)
                {
                    issues.Add($"{skill.Name} ({declaringType.Name}.{skill.Method.Name}): body contains '{hit}' " +
                               "but declares neither MutatesScene nor MutatesAssets");
                }
            }

            // Recalibrated 2026-09-06: the 194-skill fix this test's own doc comment describes (skills that
            // write but didn't declare Mutates*) shrank the "not yet declaring" candidate pool from >600 down to
            // ~362 in the same pass that added this assertion - the threshold was checking a pre-fix world
            // against a post-fix number. 300 stays comfortably below today's real count while still catching a
            // scan that's actually broken (finds near-zero method bodies).
            Assert.That(checkedCount, Is.GreaterThan(300),
                $"Only matched {checkedCount} skill bodies in source - the source scan is likely broken, " +
                "and a green result here would be meaningless.");

            Assert.That(issues, Is.Empty,
                $"{issues.Count} skill(s) write to the scene or an asset without declaring it:\n" + string.Join("\n", issues));
        }

        /// <summary>Calls that write scene or asset state; a body containing one must declare MutatesScene or MutatesAssets.</summary>
        private static readonly string[] WriteMarkers =
        {
            "Undo.Register", "Undo.RecordObject", "Undo.AddComponent", "Undo.DestroyObject",
            "Undo.SetTransformParent", "WorkflowManager.Snapshot", "EditorSceneManager.MarkSceneDirty",
            "EditorUtility.SetDirty", "AssetDatabase.CreateAsset", "AssetDatabase.SaveAssets",
            "WriteImportSettingsIfDirty", "File.WriteAllText", "PrefabUtility.SaveAsPrefabAsset",
            ".SaveAndReimport",
        };

        private static string FirstWriteMarker(IEnumerable<string> maskedBodies)
        {
            var bodies = maskedBodies.ToList();
            return WriteMarkers.FirstOrDefault(marker => bodies.Any(body => body.IndexOf(marker, StringComparison.Ordinal) >= 0));
        }

        /// <summary>
        /// The canary for <see cref="SkillsRecordingUndoOrSnapshots_DeclareMutatesSceneOrAssets"/>: a real write call is
        /// found, while the same text inside a comment or a string literal is not.
        /// </summary>
        [Test]
        public void WriteMarkerScan_SeesCallsButNotCommentsOrStrings()
        {
            const string source =
                "public static class CanarySkills\n" +
                "{\n" +
                "    public static object CanaryWrite(GameObject go)\n" +
                "    {\n" +
                "        // Undo.Register would be a marker, but this is a comment\n" +
                "        var label = \"EditorUtility.SetDirty\";\n" +
                "        Undo.RecordObject(go, label);\n" +
                "        return null;\n" +
                "    }\n" +
                "\n" +
                "    public static object CanaryRead(GameObject go)\n" +
                "    {\n" +
                "        /* AssetDatabase.SaveAssets(); */\n" +
                "        return new { name = \"File.WriteAllText\" };\n" +
                "    }\n" +
                "}\n";
            var masked = SourceMask.Mask(source);

            Assert.That(FirstWriteMarker(SourceMask.FindMethodBodies(masked, "CanaryWrite")), Is.EqualTo("Undo.RecordObject"));
            Assert.That(FirstWriteMarker(SourceMask.FindMethodBodies(masked, "CanaryRead")), Is.Null,
                "Marker text inside comments and string literals must not count as a write.");
            Assert.That(SourceMask.FindMethodBodies(masked, "CanaryRead"), Has.Count.EqualTo(1), "The body must be found at all.");
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
        ///
        /// <para>Also asserts the whole compound string is itself a <c>_requiredInputGroups</c> key. Every part
        /// naming a real parameter is necessary but not sufficient: <see cref="SkillRouter"/>'s
        /// <c>IsParameterRequired</c> only ever compares a token to a parameter name by exact string equality, so
        /// it never matches a piped token; the *only* mechanism that can enforce a compound token is
        /// <see cref="SkillPlanningService"/>'s <c>ApplyRequiredInputGroups</c> looking the whole string up as a
        /// dictionary key. A token whose halves are individually valid but whose full string was never registered
        /// (<c>model_get_mesh_info</c>'s <c>"gameObject|assetPath"</c>) used to pass this test outright while
        /// enforcing nothing - an empty body dry-ran as valid and failed only once
        /// <c>GameObjectFinder.FindOrError</c> ran during execution.</para>
        /// </summary>
        [Test]
        public void CompoundRequiredInputTokens_NameAKeyTheSkillAccepts()
        {
            var groups = RequiredInputGroups();
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

                    if (!groups.ContainsKey(token))
                        offenders.Add($"{skill.Name}: compound token '{token}' is not a _requiredInputGroups key, " +
                                      "so ApplyRequiredInputGroups never enforces it");
                }
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// The gap between the two RequiresInput checks above: <see cref="RequiredInputGroups_NameOnlyRealParameters"/>
        /// only walks <c>_requiredInputGroups</c>'s own candidate lists, and
        /// <see cref="CompoundRequiredInputTokens_NameAKeyTheSkillAccepts"/> only looks at tokens
        /// containing '|'. A single, non-piped token that names neither a real parameter nor a
        /// <c>_requiredInputGroups</c> key falls through both - exactly what
        /// <c>asset_import</c>'s <c>"textureAsset"</c>/<c>"modelAsset"</c>/<c>"audioAsset"</c> tokens
        /// did: <see cref="SkillRouter"/>'s <c>IsParameterRequired</c> compares by literal parameter
        /// name, so it never matches these, and <see cref="SkillPlanningService"/>'s
        /// <c>ApplyRequiredInputGroups</c> looks the token up in <c>_requiredInputGroups</c> and
        /// silently continues when it's absent. Either way the token enforces nothing: the schema
        /// says nothing is required, an empty body dry-runs as <c>valid:true</c>, and the caller only
        /// finds out after execution has already started (fixed for asset_import's four skills in the
        /// same change that added this test).
        ///
        /// <para>The known-semantic-locator set below is this audit's other finding: the identical
        /// shape of bug recurred roughly 75 more times across Cinemachine / ProBuilder / Terrain /
        /// Timeline / Smart / Prefab / Animator / Audio / DOTween / Material / Physics / Scene / UI,
        /// all using a word describing *what kind* of target the skill needs (<c>vcam</c>,
        /// <c>proBuilderMesh</c>, <c>terrain</c>, <c>director</c>, ...) instead of a literal parameter
        /// name - the exact convention <c>"gameObject"</c> already gets, just never registered as a
        /// <c>_requiredInputGroups</c> key. Fixed in the same 2026-09 pass by either registering the
        /// word in <c>SkillPlanningService._requiredInputGroups</c> (with per-skill compound keys where
        /// a shared word covered two differently-shaped locators, e.g. Cinemachine's mixing-camera and
        /// state-driven-camera skills) or renaming the token on the affected skill to the real parameter
        /// name it always meant.</para>
        ///
        /// <para>Two tokens remain and are expected to stay here permanently: <c>"selection"</c>
        /// (Smart module) and <c>"selectedGameObjects"</c> (UI module) both name "the GameObjects
        /// currently selected in the Hierarchy" - state read from <c>UnityEditor.Selection</c> at
        /// request time, not carried by any JSON body parameter at all. No group entry can express this
        /// (a group's candidates must themselves be real accepted parameter names, per
        /// <see cref="RequiredInputGroups_NameOnlyRealParameters"/>, and none of these skills has one)
        /// so it is enforced directly instead, via <c>SkillPlanningService.AnalyzeRequiresEditorSelection</c>
        /// wired into the semantic-planner switch for each declaring skill - which is exactly why an empty
        /// body still correctly dry-runs as invalid for all eight of them despite the token itself being
        /// unenforceable through this mechanism.</para>
        /// </summary>
        [Test]
        public void RequiresInput_SingleTokensNameARealParameterOrGroupKey()
        {
            var knownSemanticLocatorTokens = new HashSet<string>(StringComparer.Ordinal)
            {
                "selection", "selectedGameObjects",
            };

            var groups = RequiredInputGroups();
            var offenders = new List<string>();

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered()
                         .Where(s => s.RequiresInput != null)
                         .OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                foreach (var token in skill.RequiresInput)
                {
                    if (string.IsNullOrEmpty(token) || token.IndexOf('|') >= 0)
                        continue; // compound tokens are CompoundRequiredInputTokens_NameAKeyTheSkillAccepts's job

                    if (SkillAcceptsParameter(skill, token))
                        continue;
                    if (groups.ContainsKey(token))
                        continue;
                    if (knownSemanticLocatorTokens.Contains(token))
                        continue;

                    offenders.Add($"{skill.Name}: token '{token}' names neither a parameter it accepts nor a " +
                                  "_requiredInputGroups key nor a recognized semantic-locator word");
                }
            }

            Assert.That(offenders, Is.Empty,
                $"{offenders.Count} RequiresInput token(s) enforce nothing - neither IsParameterRequired nor " +
                "ApplyRequiredInputGroups can ever match them, so an empty body dry-runs as valid and the " +
                $"caller only finds out after execution starts:\n{string.Join("\n", offenders)}");
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

        /// <summary>The same dual-path resolution as OutputsReturnContractTests.GetSkillsSourceRoot: in-project first, then the package cache.</summary>
        private static string GetSkillsSourceRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot != null)
            {
                var inProject = Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills");
                if (Directory.Exists(inProject))
                    return inProject;
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly)
                              ?? PackageInfo.FindForAssembly(typeof(SkillMetadataGuardTests).Assembly);
            if (packageInfo != null)
            {
                var inPackage = Path.Combine(packageInfo.resolvedPath, "Editor", "Skills");
                if (Directory.Exists(inPackage))
                    return inPackage;
            }

            return projectRoot != null
                ? Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills")
                : "SkillsForUnity/Editor/Skills";
        }

        /// <summary>
        /// A minimal string/comment-aware C# scanner used only by
        /// <see cref="SkillsRecordingUndoOrSnapshots_DeclareMutatesSceneOrAssets"/>. Blanks out line
        /// comments, block comments, and the contents of char/string literals (including
        /// interpolation holes, which stay live since they're real executable code), so brace/paren
        /// matching and marker-text search never trip over a comment or a string literal that happens
        /// to contain one of the marker substrings.
        /// </summary>
        private static class SourceMask
        {
            public static string Mask(string raw)
            {
                var n = raw.Length;
                var masked = raw.ToCharArray();
                // Mode stack: 'c' code, 'l' line comment, 'b' block comment, 's' string, 'x' char,
                // 'v' verbatim string, 'I' interpolated string, 'V' verbatim+interpolated string.
                // A '{' seen while in code mode pushes another 'c' frame (this doubles as the
                // interpolation-hole mechanism: a hole opened from 'I'/'V' is just a 'c' frame that
                // pops back to the string once its own brace nesting returns to zero).
                var stack = new List<char> { 'c' };
                var i = 0;
                while (i < n)
                {
                    var mode = stack[stack.Count - 1];
                    var ch = raw[i];

                    if (mode == 'c')
                    {
                        if (ch == '/' && i + 1 < n && raw[i + 1] == '/')
                        { masked[i] = ' '; masked[i + 1] = ' '; stack.Add('l'); i += 2; continue; }
                        if (ch == '/' && i + 1 < n && raw[i + 1] == '*')
                        { masked[i] = ' '; masked[i + 1] = ' '; stack.Add('b'); i += 2; continue; }
                        if (ch == '"')
                        {
                            var interp = i > 0 && raw[i - 1] == '$';
                            var verbatim = i > 0 && raw[i - 1] == '@';
                            if (!interp && i > 1 && raw[i - 2] == '$' && raw[i - 1] == '@') interp = true;
                            if (!verbatim && i > 1 && raw[i - 2] == '@' && raw[i - 1] == '$') verbatim = true;
                            stack.Add(interp && verbatim ? 'V' : interp ? 'I' : verbatim ? 'v' : 's');
                            masked[i] = ' '; i++; continue;
                        }
                        if (ch == '\'') { stack.Add('x'); masked[i] = ' '; i++; continue; }
                        if (ch == '{' || ch == '(') { stack.Add('c'); i++; continue; }
                        if (ch == '}' || ch == ')')
                        {
                            if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                            i++; continue;
                        }
                        i++; continue;
                    }

                    if (mode == 'l')
                    {
                        if (ch == '\n') { stack.RemoveAt(stack.Count - 1); i++; continue; }
                        masked[i] = ' '; i++; continue;
                    }

                    if (mode == 'b')
                    {
                        if (ch == '*' && i + 1 < n && raw[i + 1] == '/')
                        { masked[i] = ' '; masked[i + 1] = ' '; stack.RemoveAt(stack.Count - 1); i += 2; continue; }
                        masked[i] = ' '; i++; continue;
                    }

                    if (mode == 's')
                    {
                        if (ch == '\\') { masked[i] = ' '; if (i + 1 < n) masked[i + 1] = ' '; i += 2; continue; }
                        if (ch == '"') { masked[i] = ' '; stack.RemoveAt(stack.Count - 1); i++; continue; }
                        masked[i] = ' '; i++; continue;
                    }

                    if (mode == 'x')
                    {
                        if (ch == '\\') { masked[i] = ' '; if (i + 1 < n) masked[i + 1] = ' '; i += 2; continue; }
                        if (ch == '\'') { masked[i] = ' '; stack.RemoveAt(stack.Count - 1); i++; continue; }
                        masked[i] = ' '; i++; continue;
                    }

                    if (mode == 'v')
                    {
                        if (ch == '"')
                        {
                            if (i + 1 < n && raw[i + 1] == '"') { masked[i] = ' '; masked[i + 1] = ' '; i += 2; continue; }
                            masked[i] = ' '; stack.RemoveAt(stack.Count - 1); i++; continue;
                        }
                        masked[i] = ' '; i++; continue;
                    }

                    // 'I' or 'V': interpolated (optionally verbatim) string
                    {
                        var verbatim = mode == 'V';
                        if (verbatim && ch == '"' && i + 1 < n && raw[i + 1] == '"')
                        { masked[i] = ' '; masked[i + 1] = ' '; i += 2; continue; }
                        if (!verbatim && ch == '\\')
                        { masked[i] = ' '; if (i + 1 < n) masked[i + 1] = ' '; i += 2; continue; }
                        if (ch == '"') { masked[i] = ' '; stack.RemoveAt(stack.Count - 1); i++; continue; }
                        if (ch == '{')
                        {
                            if (i + 1 < n && raw[i + 1] == '{') { masked[i] = ' '; masked[i + 1] = ' '; i += 2; continue; }
                            stack.Add('c'); i++; continue; // interpolation hole: real code follows
                        }
                        if (ch == '}')
                        {
                            if (i + 1 < n && raw[i + 1] == '}') { masked[i] = ' '; masked[i + 1] = ' '; i += 2; continue; }
                            masked[i] = ' '; i++; continue;
                        }
                        masked[i] = ' '; i++; continue;
                    }
                }

                return new string(masked);
            }

            /// <summary>
            /// Every <c>(public|internal|private) static object &lt;methodName&gt;(...) { ... }</c>
            /// body found in already-masked text. Usually one match; more than one when a #if/#else
            /// pair declares the same name twice (both bodies are returned so a caller can check
            /// either one - e.g. a real implementation alongside a package-missing stub).
            /// </summary>
            public static List<string> FindMethodBodies(string masked, string methodName)
            {
                var bodies = new List<string>();
                var pattern = new Regex(@"(?:public|internal|private)\s+static\s+object\s+" + Regex.Escape(methodName) + @"\s*\(");
                foreach (Match match in pattern.Matches(masked))
                {
                    var parenOpen = masked.IndexOf('(', match.Index);
                    if (parenOpen < 0) continue;
                    var parenClose = MatchBracketFrom(masked, parenOpen, '(', ')');
                    if (parenClose < 0) continue;
                    var braceOpen = masked.IndexOf('{', parenClose);
                    if (braceOpen < 0) continue;
                    var braceClose = MatchBracketFrom(masked, braceOpen, '{', '}');
                    if (braceClose < 0) continue;
                    bodies.Add(masked.Substring(braceOpen, braceClose - braceOpen));
                }
                return bodies;
            }

            private static int MatchBracketFrom(string text, int openIndex, char open, char close)
            {
                var depth = 0;
                for (var i = openIndex; i < text.Length; i++)
                {
                    if (text[i] == open) depth++;
                    else if (text[i] == close)
                    {
                        depth--;
                        if (depth == 0) return i;
                    }
                }
                return -1;
            }
        }
    }
}

// Producer:Betsy
