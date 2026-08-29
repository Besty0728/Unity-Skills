using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Dedicated coverage for the hardening of the one-shot grant-bypass token. SkillRouter.Execute has
    /// four early-return parameter validation points, positioned right between TryGrantAndReturnArgs
    /// (which writes the ThreadStatic token) and CheckAccess (which consumes it); if any one of those early exits misses ClearOneShotBypass, the token leaks into another unrelated request on the same thread.
    /// The hard 30-second deadline is the second line of defense against exactly this.
    ///
    /// This fixture complements SkillsModeManagerTests.cs and doesn't duplicate the grant/allowlist/migration coverage it already has.
    /// </summary>
    [TestFixture]
    public class SkillsModeManagerOneShotTests
    {
        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";

        private bool _hadMode;
        private string _savedMode;
        private bool _hadPanelApproval;
        private bool _savedPanelApproval;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _hadMode = EditorPrefs.HasKey(PrefKeyMode);
            _savedMode = EditorPrefs.GetString(PrefKeyMode, string.Empty);
            _hadPanelApproval = EditorPrefs.HasKey(PrefKeyPanelApproval);
            _savedPanelApproval = EditorPrefs.GetBool(PrefKeyPanelApproval, false);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_hadMode) EditorPrefs.SetString(PrefKeyMode, _savedMode);
            else EditorPrefs.DeleteKey(PrefKeyMode);
            if (_hadPanelApproval) EditorPrefs.SetBool(PrefKeyPanelApproval, _savedPanelApproval);
            else EditorPrefs.DeleteKey(PrefKeyPanelApproval);
            SkillsModeManager.CompleteTestPreferenceRecovery();
        }

        [SetUp]
        public void SetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsAuditLog.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
        }

        /// <summary>A minimal SkillInfo: only fills the fields CheckAccess / IsForbiddenInSemi read.</summary>
        private static SkillRouter.SkillInfo MakeSkill(string name, SkillMode mode = SkillMode.FullAuto)
        {
            return new SkillRouter.SkillInfo
            {
                Name = name,
                Mode = mode,
                Operation = SkillOperation.Modify,
                RiskLevel = "low",
                MayEnterPlayMode = false,
                MayTriggerReload = false,
            };
        }

        [Test]
        public void ClearOneShotBypass_AfterGrant_CheckAccessNoLongerAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;
            const string skillName = "one_shot_clear_probe";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, "{}");
            var (outcome, _, _) = SkillsModeManager.TryGrantAndReturnArgs(skillName, token, "{}");
            Assert.AreEqual(GrantOutcome.Granted, outcome);

            // Simulates a caller unconditionally clearing the pending one-shot token between the
            // grant and CheckAccess (e.g. SkillRouter.Execute hitting an early-return parameter validation).
            SkillsModeManager.ClearOneShotBypass();

            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)),
                "A cleared one-shot token must not allow the skill through.");
        }

        [Test]
        public void ConsumeOneShotBypass_TokenPastThirtySecondDeadline_IsDiscardedNotConsumed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;
            const string skillName = "one_shot_expiry_probe";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, "{}");
            var (outcome, _, _) = SkillsModeManager.TryGrantAndReturnArgs(skillName, token, "{}");
            Assert.AreEqual(GrantOutcome.Granted, outcome);

            // SkillsModeManager has no injectable clock, and the deadline is a ThreadStatic field -- push it into the past via reflection on the same thread, rather than actually
            // sleeping 30-odd seconds in a unit test.
            var deadlineField = typeof(SkillsModeManager).GetField("_oneShotDeadlineUtc",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(deadlineField, "_oneShotDeadlineUtc field must exist for expiry simulation");
            deadlineField.SetValue(null, DateTime.UtcNow.AddSeconds(-1));

            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass(skillName),
                "A token past its 30s deadline must be discarded rather than consumed.");
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)),
                "An expired one-shot must fall back to requiring a fresh grant.");
        }

        [Test]
        public void CheckAccess_OneShotSurvivesMismatchedName_ThenAllowsCorrectSkillExactlyOnce()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;
            const string skillName = "one_shot_mismatch_probe";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, "{}");
            var (outcome, _, _) = SkillsModeManager.TryGrantAndReturnArgs(skillName, token, "{}");
            Assert.AreEqual(GrantOutcome.Granted, outcome);

            // Checking permission under a different skill name must not consume the pending one-shot token.
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill("unrelated_skill")));

            // The original skill's one-shot grant is still valid, and gets consumed right after this call.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }
    }
}

// Producer:Betsy
