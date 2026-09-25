using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The dryRun and plan responses carry an "authorization" verdict built by BuildModeAuthorizationPreview, a
    /// hand-written copy of the execute path's ladder (surface exclusion first, then SkillsModeManager.CheckAccess).
    /// Nothing tied the copy to the original, so a new gate or a reordered step could leave the preview promising a
    /// call that execution refuses, or the reverse. This walks every surface profile, operating mode and allowlist state
    /// for a read-only skill, an ordinary write and a never-in-semi delete, and requires the preview to predict what
    /// Execute actually does. The confirmation gate and the dryRun policy are held off: the preview claims neither.
    /// </summary>
    [TestFixture]
    public class GatePreviewParityTests
    {
        private const string TargetName = "R6ParityTarget";
        private const string VictimName = "R6ParityVictim";

        private static readonly HashSet<string> GateCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            SkillErrorCode.ModeForbidden.ToWireString(), SkillErrorCode.ModeRestricted.ToWireString(),
            SkillErrorCode.SurfaceExcluded.ToWireString(), SkillErrorCode.ConfirmationRequired.ToWireString(),
            SkillErrorCode.DryRunRequired.ToWireString(),
        };

        private static readonly (string Skill, string Args)[] Probes =
        {
            ("gameobject_find", "{\"name\":\"" + TargetName + "\"}"),
            ("gameobject_set_active", "{\"name\":\"" + TargetName + "\",\"active\":true}"),
            ("gameobject_delete", "{\"name\":\"" + VictimName + "\"}"),
        };

        private ModePreferenceSnapshot _modePreferences;
        private SurfaceProfileKind _savedProfile;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _modePreferences = ModePreferenceSnapshot.Capture();

        [OneTimeTearDown]
        public void OneTimeTearDown() => _modePreferences.Restore();

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsModeManager.PanelApprovalRequired = false;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = false;
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            SkillsAuditLog.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
            DryRunPolicyService.OverrideForTests = null;
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void AuthorizationPreview_PredictsWhatExecuteDoes()
        {
            var mismatches = new List<string>();
            var verdictsSeen = new HashSet<string>(StringComparer.Ordinal);
            var cases = 0;

            foreach (SurfaceProfileKind profile in Enum.GetValues(typeof(SurfaceProfileKind)))
            foreach (SkillsOperatingMode mode in Enum.GetValues(typeof(SkillsOperatingMode)))
            foreach (var allowlisted in new[] { false, true })
            foreach (var (skill, args) in Probes)
            {
                SkillsSurfaceProfile.Current = profile;
                SkillsModeManager.CurrentMode = mode;
                if (allowlisted)
                    SkillsModeManager.AddToAllowlist(skill);
                else
                    SkillsModeManager.RemoveFromAllowlist(skill);
                FreshScene();

                var preview = JObject.Parse(SkillRouter.DryRun(skill, args))["authorization"] as JObject;
                var executed = JObject.Parse(SkillRouter.Execute(skill, args));
                SkillsModeManager.ClearOneShotBypass();
                cases++;

                var label = $"{profile}/{mode}/{(allowlisted ? "allowlisted" : "not allowlisted")}/{skill}";
                if (preview == null)
                {
                    mismatches.Add($"{label}: the dryRun response has no authorization block");
                    continue;
                }

                var allowed = preview.Value<bool>("allowed");
                var blockedBy = preview.Value<string>("blockedBy");
                var code = executed.Value<string>("status") == "error" ? executed.Value<string>("errorCode") : null;
                verdictsSeen.Add(allowed ? "allowed" : blockedBy);

                if (allowed && code != null && GateCodes.Contains(code))
                    mismatches.Add($"{label}: the preview allowed it, execute answered {code}");
                else if (!allowed && code != blockedBy)
                    mismatches.Add($"{label}: the preview said {blockedBy}, execute answered {code ?? "success"}");
            }

            var expectedCases = Enum.GetValues(typeof(SurfaceProfileKind)).Length *
                                Enum.GetValues(typeof(SkillsOperatingMode)).Length * 2 * Probes.Length;
            Assert.That(cases, Is.EqualTo(expectedCases));
            Assert.That(verdictsSeen, Is.SupersetOf(new[]
            {
                "allowed", SkillErrorCode.ModeForbidden.ToWireString(), SkillErrorCode.ModeRestricted.ToWireString(),
                SkillErrorCode.SurfaceExcluded.ToWireString(),
            }), "The matrix must reach every verdict the preview can give, or agreeing proves little.");
            Assert.That(mismatches, Is.Empty,
                "The authorization preview disagrees with execution:\n" + string.Join("\n", mismatches));
        }

        private static void FreshScene()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject(TargetName);
            new GameObject(VictimName);
            GameObjectFinder.InvalidateCache();
        }
    }
}

// Producer:Betsy
