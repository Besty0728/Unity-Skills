using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unit tests for the skill permission mode system.
    ///
    /// Covers the three operating modes (Approval / Auto / Bypass), the two approval channels
    /// (Dialog / Panel), automatic NeverInSemi determination, the grant token lifecycle,
    /// per-project persistence, and the upgrade compatibility rule (old install → Bypass).
    ///
    /// Also covers:
    /// - The Allowlist channel (AddToAllowlist / RemoveFromAllowlist / ClearAllowlist / IsInAllowlist)
    /// - Allowlist taking priority over IsForbiddenInSemi
    /// - Single-use grants: TryGrant no longer writes the allowlist permanently
    /// - TryGrantAndReturnArgs (Plan B: one-step execution) + ConsumeOneShotBypass
    /// - Idempotent migration from the old GrantedSkills key to the new AllowlistSkills key
    ///
    /// The settings live in a temp project's UserSettings/UnitySkills/settings.json for the whole fixture, so the
    /// real project and the machine-wide EditorPrefs are never touched; a dictionary stands in for the pre-3.0
    /// global EditorPrefs values. "Old install" behavior is simulated with a test-only override.
    /// </summary>
    [TestFixture]
    public class SkillsModeManagerTests
    {
        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        private const string RecoveryActiveSessionKey = "UnitySkills.Tests.PreferenceRecovery.Active";

        private string _settingsRoot;
        private Dictionary<string, object> _legacy;
        private ProjectPrefsTestScope _settings;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // File-backed, because ResetForTests' domain-reload recovery only covers a store that outlives a reload.
            _settingsRoot = Path.Combine(Path.GetTempPath(), "unityskills-mode-tests-" + Guid.NewGuid().ToString("N"));
            _legacy = new Dictionary<string, object>();
            _settings = ProjectPrefsTestScope.AtRoot(_settingsRoot, _legacy);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsModeManager.CompleteTestPreferenceRecovery();
            _settings.Dispose();
            if (Directory.Exists(_settingsRoot)) Directory.Delete(_settingsRoot, true);
        }

        [SetUp]
        public void SetUp()
        {
            _legacy.Clear();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsAuditLog.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsModeManager.InstallEraPrefsForTests = null;
            SkillsAuditLog.ResetForTests();
        }

        /// <summary>
        /// Constructs a SkillInfo populating only the fields CheckAccess / IsForbiddenInSemi
        /// read. The remaining fields (Method, Parameters, etc.) are deliberately left null — the
        /// mode manager never touches them.
        /// </summary>
        private static SkillRouter.SkillInfo MakeSkill(
            string name,
            SkillMode mode = SkillMode.FullAuto,
            SkillOperation op = SkillOperation.Modify,
            string risk = "low",
            bool mayEnterPlayMode = false,
            bool mayTriggerReload = false)
        {
            return new SkillRouter.SkillInfo
            {
                Name = name,
                Mode = mode,
                Operation = op,
                RiskLevel = risk,
                MayEnterPlayMode = mayEnterPlayMode,
                MayTriggerReload = mayTriggerReload,
            };
        }

        [Test]
        public void CheckAccess_BypassMode_AnySkill_AlwaysAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            // Ordinary SemiAuto / FullAuto should be allowed straight through.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("safe", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("normal")));

            // Various metadata combinations that would normally be blocked by IsForbiddenInSemi — Bypass mode skips that check entirely.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("del", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("play", mayEnterPlayMode: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("reload", mayTriggerReload: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("high_risk", risk: "high")));
            // A name that used to be on the never-list (scene_clear): after _explicitNeverList was
            // removed, it's no longer auto-forbidden, and under Bypass it's allowed just like any other skill.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("scene_clear")));
        }

        [Test]
        public void CheckAccess_AutoMode_SemiAutoAndFullAuto_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("semi_one", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("full_one", SkillMode.FullAuto)));
        }

        [Test]
        public void CheckAccess_AutoMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
        }

        [Test]
        public void CheckAccess_ApprovalMode_SemiAutoSkill_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("preview_thing", SkillMode.SemiAuto)));
        }

        [Test]
        public void CheckAccess_ApprovalMode_FullAutoUngranted_NeedsGrant()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill("smart_layout")));
        }

        [Test]
        public void Approval_DialogChannel_GrantIsOneShot_NotWrittenToAllowlist()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false; // Same as the default value; written explicitly to show intent

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Dialog, channel);
            Assert.Greater(ttl, 0, "TTL should be a positive number of seconds");
            Assert.IsFalse(string.IsNullOrWhiteSpace(token), "Token must be non-empty");

            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // A grant no longer permanently writes the allowlist. Re-checking access (with no one-shot re-entry) should return NeedsGrant again.
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        [Test]
        public void Approval_PanelChannel_GrantBeforeApprove_ReturnsPendingApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Panel, channel);

            // The user hasn't clicked Approve on the panel yet, but the AI already replayed the token once.
            Assert.AreEqual(GrantOutcome.PendingApproval,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);

            // The entry is still alive in the panel's pending list.
            var pending = SkillsModeManager.PeekPendingForTests(token);
            Assert.IsNotNull(pending);
            Assert.AreEqual(skillName, pending.SkillName);
            Assert.IsFalse(pending.ApprovedByPanel);
        }

        [Test]
        public void Approval_PanelChannel_ApproveKeepsEntry_GrantThenOneShot()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Approve(token));
            // Approve no longer permanently writes the allowlist; the entry is kept, waiting for a subsequent grant to trigger the one-shot execution.
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            var pendingAfterApprove = SkillsModeManager.PeekPendingForTests(token);
            Assert.IsNotNull(pendingAfterApprove, "Entry must be kept after Approve for AI re-grant.");
            Assert.IsTrue(pendingAfterApprove.ApprovedByPanel);

            // The AI's subsequent grant takes the Granted branch and consumes the entry; it doesn't write the allowlist.
            Assert.AreEqual(GrantOutcome.Granted,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token),
                "Entry must be consumed after Granted.");
        }

        [Test]
        public void Approval_PanelChannel_DenyThenGrant_ReturnsFalse()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Deny(token));

            Assert.IsFalse(SkillsModeManager.TryGrant(skillName, token, args));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token));
        }

        [Test]
        public void CheckAccess_ApprovalMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("hot_skill", risk: "high")));
        }

        [Test]
        public void TryGrant_InvalidToken_ReturnsFalseAndInvalid()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // A token that was never issued.
            Assert.IsFalse(SkillsModeManager.TryGrant("any_skill", "bogus_token_xxx", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "bogus_token_xxx", "{}"));

            // An empty string / whitespace-only token.
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "   ", "{}"));

            // A valid token but mismatched args → Invalid.
            const string skill = "smart_layout";
            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skill, "{\"a\":1}");
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skill, token, "{\"a\":2}"));
        }

        [Test]
        public void RemoveFromAllowlist_AfterAdd_CheckAccessReturnsNeedsGrant()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";

            Assert.IsTrue(SkillsModeManager.AddToAllowlist(skillName));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));

            Assert.IsTrue(SkillsModeManager.RemoveFromAllowlist(skillName));

            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        [Test]
        public void CurrentMode_Setter_PersistsToTheProjectSettings_AndGetterReadsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            // Directly checks the store and its file: confirms the setter really wrote PrefKeyMode.
            Assert.IsTrue(ProjectPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual("Auto", ProjectPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);

            // Switching modes overwrites the value rather than appending.
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            Assert.AreEqual("Approval", ProjectPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
            var file = JObject.Parse(File.ReadAllText(ProjectPrefs.SettingsFileFor(_settingsRoot)));
            Assert.AreEqual("Approval", file["values"]?[PrefKeyMode]?.ToString());
        }

        [Test]
        public void CurrentMode_LegacyGlobalValue_AppliesUntilThisProjectChoosesItsOwn()
        {
            _legacy[PrefKeyMode] = "Approval";
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);

            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
            Assert.AreEqual("Approval", _legacy[PrefKeyMode], "the global value is never written");
        }

        [Test]
        public void IsForbiddenInSemi_CoversAllAutoJudgementBranches()
        {
            // The four combinations that must be forbidden under Approval / Auto (judged purely by metadata).
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("del", op: SkillOperation.Delete)),
                "SkillOperation.Delete must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("enter_play", mayEnterPlayMode: true)),
                "MayEnterPlayMode must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("trigger_reload", mayTriggerReload: true)),
                "MayTriggerReload must be forbidden");
            Assert.IsTrue(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("hot", risk: "high")),
                "RiskLevel=\"high\" must be forbidden");

            // Ordinary SemiAuto / FullAuto with no high-risk markers must not be forbidden.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_semi", SkillMode.SemiAuto)));
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_full", SkillMode.FullAuto)));

            // A combined-flag Operation (Query|Modify) is still allowed as long as it doesn't include Delete.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("query_modify", op: SkillOperation.Query | SkillOperation.Modify)));
        }

        [Test]
        public void AuditLog_GrantEvent_AppendThenFlushSync_ReadRecentContainsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // Writing is asynchronous, so a flush must be forced before ReadRecent can see this line.
            SkillsAuditLog.FlushSync();
            var recent = SkillsAuditLog.ReadRecent(50);

            Assert.IsNotNull(recent);
            Assert.Greater(recent.Count, 0, "Audit log should contain at least one event");

            bool foundGrant = recent
                .OfType<JObject>()
                .Any(j => j["type"]?.ToString() == "grant"
                       && j["skill"]?.ToString() == skillName
                       && j["token"]?.ToString() == token);
            Assert.IsTrue(foundGrant,
                "Expected a 'grant' audit event for skill=" + skillName + " token=" + token);
        }

        [Test]
        public void CurrentMode_OldInstall_NoExplicitMode_DefaultsToBypass()
        {
            SkillsModeManager.ExistingInstallOverrideForTests = true;

            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
            // The getter must never write PrefKeyMode as a side effect — once it's written, the next upgrade can no longer re-determine the default value.
            Assert.IsFalse(ProjectPrefs.HasKey(PrefKeyMode));
        }

        [Test]
        public void CurrentMode_FreshInstall_NoKeys_DefaultsToAuto()
        {
            // No UnitySkills_* keys should remain after SetUp.
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);
            Assert.IsFalse(ProjectPrefs.HasKey(PrefKeyMode));
        }

        [Test]
        public void InstallEra_FreshMachine_LaterSettingsNeverFlipTheModeDefault()
        {
            // A machine without any pre-v1.9 key: the first evaluation decides "fresh" and stores it.
            var machine = new Dictionary<string, string>();
            SkillsModeManager.InstallEraPrefsForTests = machine;
            SkillsModeManager.ExistingInstallOverrideForTests = null;

            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);
            Assert.AreEqual("fresh", machine["UnitySkills_InstallEra"]);

            // The user then changes a machine-wide setting and a per-project one.
            machine["UnitySkills_Language"] = "English";
            machine["UnitySkills_LogLevel"] = "3";
            ProjectPrefs.SetInt("UnitySkills_PreferredPort", 8095);
            ProjectPrefs.SetBool("UnitySkills_TelemetryEnabled", false);

            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode,
                "a project that never chose a mode must not fall back to Bypass");
            Assert.IsFalse(SkillsModeManager.IsExistingInstallForDefaults());
            ProjectPrefs.DeleteKey("UnitySkills_SummaryAutoTruncate");
            SkillRouter.ResetSummaryPreferencesForTests();
            Assert.IsTrue(SkillRouter.SummaryAutoTruncate, "the one-shot truncation default stays the fresh one");
            Assert.IsFalse(ProjectPrefs.HasKey(PrefKeyMode), "the default is never written");
        }

        [Test]
        public void InstallEra_ExistingMachine_StaysExisting()
        {
            var machine = new Dictionary<string, string>
            {
                ["UnitySkills_PreferredPort"] = "8090",
            };
            SkillsModeManager.InstallEraPrefsForTests = machine;
            SkillsModeManager.ExistingInstallOverrideForTests = null;

            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
            Assert.AreEqual("existing", machine["UnitySkills_InstallEra"]);

            machine.Remove("UnitySkills_PreferredPort");
            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode, "the stored verdict is kept");
            Assert.IsTrue(SkillsModeManager.IsExistingInstallForDefaults());
        }

        [Test]
        public void InstallEra_StoredVerdict_WinsOverTheProbe_AndProjectValuesNeverCount()
        {
            var machine = new Dictionary<string, string>
            {
                ["UnitySkills_InstallEra"] = "fresh",
                ["UnitySkills_KeepAliveIntervalSeconds"] = "10",
            };
            SkillsModeManager.InstallEraPrefsForTests = machine;
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            Assert.IsFalse(SkillsModeManager.IsExistingInstallForDefaults());

            // Undecided machine whose only trace is a v3 per-project value: still a fresh install.
            machine.Clear();
            ProjectPrefs.SetBool("UnitySkills_RequireConfirmation", true);
            Assert.IsFalse(SkillsModeManager.IsExistingInstallForDefaults());
            Assert.AreEqual("fresh", machine["UnitySkills_InstallEra"]);
        }

        [Test]
        public void ResetForTests_DomainReloadRecovery_RestoresExplicitBypassMode()
        {
            SkillsModeManager.CompleteTestPreferenceRecovery();
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            SkillsModeManager.ResetForTests();
            Assert.IsFalse(ProjectPrefs.HasKey(PrefKeyMode));

            SkillsModeManager.RestorePreferencesAfterTestDomainReload();

            Assert.IsTrue(ProjectPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
        }

        [Test]
        public void ResetForTests_DomainReloadRecovery_NeverRestoresIntoAnotherSettingsStore()
        {
            SkillsModeManager.CompleteTestPreferenceRecovery();
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsModeManager.ResetForTests();
            Assert.IsTrue(SessionState.GetBool(RecoveryActiveSessionKey, false));

            var otherRoot = _settingsRoot + "-other";
            try
            {
                using (ProjectPrefsTestScope.AtRoot(otherRoot))
                {
                    // What the static constructor does after a reload, with a different project's store in use.
                    SkillsModeManager.RestorePreferencesAfterTestDomainReload();
                    Assert.IsFalse(ProjectPrefs.HasKey(PrefKeyMode), "values captured elsewhere are not written here");
                    Assert.IsFalse(SessionState.GetBool(RecoveryActiveSessionKey, false), "the recovery data is dropped");
                }
            }
            finally
            {
                if (Directory.Exists(otherRoot)) Directory.Delete(otherRoot, true);
            }
        }

        [Test]
        public void ResetForTests_InMemoryStore_IsNotCapturedForRecovery()
        {
            SkillsModeManager.CompleteTestPreferenceRecovery();
            using (ProjectPrefsTestScope.InMemory())
            {
                SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
                SkillsModeManager.ResetForTests();
                Assert.IsFalse(SessionState.GetBool(RecoveryActiveSessionKey, false),
                    "an in-memory store does not outlive a reload, so there is nothing to recover");
            }
        }

        [Test]
        public void Allowlist_AddRemoveClear_RoundTripsAndAudits()
        {
            Assert.IsFalse(SkillsModeManager.IsInAllowlist("alpha"));
            CollectionAssert.IsEmpty(SkillsModeManager.AllowlistSkills);

            Assert.IsTrue(SkillsModeManager.AddToAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist("alpha"));
            Assert.IsTrue(SkillsModeManager.IsInAllowlist("alpha"));
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("beta"));
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, SkillsModeManager.AllowlistSkills);

            Assert.IsTrue(SkillsModeManager.RemoveFromAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.RemoveFromAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.IsInAllowlist("alpha"));

            SkillsModeManager.ClearAllowlist();
            CollectionAssert.IsEmpty(SkillsModeManager.AllowlistSkills);

            // Blank / null inputs are always no-ops.
            Assert.IsFalse(SkillsModeManager.AddToAllowlist(""));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist("   "));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist(null));
            Assert.IsFalse(SkillsModeManager.RemoveFromAllowlist(null));
            Assert.IsFalse(SkillsModeManager.IsInAllowlist(null));
        }

        [Test]
        public void Allowlist_OverridesForbiddenInSemi_HighRiskSkillAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // Blocked by default: RiskLevel="high" is judged as NeverInSemi by metadata
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("hot_skill", risk: "high")));

            // Allowed after being added to the Allowlist (Allowlist takes priority over IsForbiddenInSemi)
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("hot_skill"));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("hot_skill", risk: "high")));

            // Also applies to a high-risk skill judged via the Delete operation
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("delete_thing"));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
        }

        [Test]
        public void TryGrantAndReturnArgs_OnGranted_ReturnsCachedArgsAndConsumesEntry()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;
            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\",\"value\":42}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            var (outcome, returnedName, returnedArgs) =
                SkillsModeManager.TryGrantAndReturnArgs(skillName, token, args);

            Assert.AreEqual(GrantOutcome.Granted, outcome);
            Assert.AreEqual(skillName, returnedName);
            Assert.AreEqual(args, returnedArgs, "Should return original cached argsJson verbatim");

            // The entry is consumed
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token));

            // A second call with the same token must be Invalid
            var (secondOutcome, _, _) =
                SkillsModeManager.TryGrantAndReturnArgs(skillName, token, args);
            Assert.AreEqual(GrantOutcome.Invalid, secondOutcome);
        }

        [Test]
        public void TryGrantAndReturnArgs_PanelChannelBeforeApprove_ReturnsPendingApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;
            const string skillName = "smart_layout";
            const string args = "{}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            var (outcome, returnedName, returnedArgs) =
                SkillsModeManager.TryGrantAndReturnArgs(skillName, token, args);
            Assert.AreEqual(GrantOutcome.PendingApproval, outcome);
            Assert.IsNull(returnedName);
            Assert.IsNull(returnedArgs);

            // The entry must be kept so it can be Approved later
            Assert.IsNotNull(SkillsModeManager.PeekPendingForTests(token));
        }

        [Test]
        public void OneShotBypass_AfterTryGrantAndReturnArgs_CheckAccessAllowedOnce()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            var (outcome, _, _) = SkillsModeManager.TryGrantAndReturnArgs(skillName, token, args);
            Assert.AreEqual(GrantOutcome.Granted, outcome);

            // The first CheckAccess hits the one-shot and is allowed
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));

            // The next CheckAccess has already consumed it, back to NeedsGrant
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        [Test]
        public void ConsumeOneShotBypass_NameMismatchOrEmpty_ReturnsFalse()
        {
            // Constructs empty state directly
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("anything"));

            // A name mismatch after a one-shot is set also doesn't consume it
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            var (token, _, _) = SkillsModeManager.IssueGrantRequest("alpha", "{}");
            SkillsModeManager.TryGrantAndReturnArgs("alpha", token, "{}");

            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("beta"));
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass(""));
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass(null));

            // A name match (case-insensitive) is what consumes it
            Assert.IsTrue(SkillsModeManager.ConsumeOneShotBypass("ALPHA"));
            // The next call must fail after being consumed
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("alpha"));
        }

        /// <summary>
        /// Force-nulls the in-memory allowlist cache field, so the next public access goes
        /// through <c>EnsureAllowlistLoaded</c> → <c>MigrateLegacyGrantedToAllowlist</c> again.
        /// Equivalent to simulating an editor cold start.
        /// </summary>
        private static void ForceAllowlistReload()
        {
            var field = typeof(SkillsModeManager).GetField("_allowlist",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "_allowlist field must exist for reload simulation");
            field.SetValue(null, null);
        }

        [Test]
        public void Migration_LegacyGrantedToAllowlist_MigratesEntriesAndSetsDoneFlag()
        {
            // 1) Simulate an old install: the machine-wide legacy granted list, no migration marker, no new allowlist.
            _legacy[PrefKeyLegacyGranted] = "[\"alpha\",\"beta\",\"gamma\"]";
            ProjectPrefs.DeleteKey(PrefKeyMigrationDone);
            ProjectPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            // 2) The first access triggers the migration
            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta", "gamma" }, snapshot);

            // 3) The migration-done marker has been written
            Assert.IsTrue(ProjectPrefs.GetBool(PrefKeyMigrationDone, false),
                "Migration must set the done flag after running");

            // 4) The legacy key is deliberately kept (as a rollback marker)
            Assert.IsTrue(_legacy.ContainsKey(PrefKeyLegacyGranted),
                "Legacy granted key must be preserved as rollback marker");

            // 5) The new allowlist has been persisted
            Assert.IsTrue(ProjectPrefs.HasKey(PrefKeyAllowlist),
                "Allowlist pref must be persisted after migration");

            // 6) An audit event has been written
            SkillsAuditLog.FlushSync();
            var recent = SkillsAuditLog.ReadRecent(100);
            bool sawMigration = recent
                .OfType<JObject>()
                .Any(j => j["type"]?.ToString() == "allowlist_migrated");
            Assert.IsTrue(sawMigration, "Expected 'allowlist_migrated' audit event after first migration");
        }

        [Test]
        public void Migration_RepeatLoad_IsIdempotent_NoDuplicateAuditEvent()
        {
            // First: run the migration
            _legacy[PrefKeyLegacyGranted] = "[\"alpha\"]";
            ProjectPrefs.DeleteKey(PrefKeyMigrationDone);
            ProjectPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();
            var _first = SkillsModeManager.AllowlistSkills;
            Assert.IsTrue(ProjectPrefs.GetBool(PrefKeyMigrationDone, false));

            // Clear the audit log, then "restart" once more (the done flag is still set)
            SkillsAuditLog.ResetForTests();
            ForceAllowlistReload();
            var snapshotAfterReload = SkillsModeManager.AllowlistSkills;

            // The content still comes from the persisted PrefKeyAllowlist; the legacy data isn't re-added
            CollectionAssert.AreEquivalent(new[] { "alpha" }, snapshotAfterReload);

            // Nor is the allowlist_migrated audit event fired again
            SkillsAuditLog.FlushSync();
            var recent = SkillsAuditLog.ReadRecent(100);
            bool sawMigration = recent
                .OfType<JObject>()
                .Any(j => j["type"]?.ToString() == "allowlist_migrated");
            Assert.IsFalse(sawMigration,
                "Migration must not re-run when PrefKeyMigrationDone is already true");
        }

        [Test]
        public void Migration_NoLegacyData_StillSetsDoneFlag_FreshInstall()
        {
            // Fresh install: no legacy data of any kind
            _legacy.Remove(PrefKeyLegacyGranted);
            ProjectPrefs.DeleteKey(PrefKeyLegacyGranted);
            ProjectPrefs.DeleteKey(PrefKeyMigrationDone);
            ProjectPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.IsEmpty(snapshot);
            Assert.IsTrue(ProjectPrefs.GetBool(PrefKeyMigrationDone, false),
                "Done flag must still be set on fresh install so future reads skip migration");
        }

        [Test]
        public void AllowlistPresets_CodingAssist_IsNonEmptyDistinct_AndMergesBothGroups()
        {
            var pack = AllowlistPresets.CodingAssist;
            Assert.IsNotNull(pack);
            Assert.Greater(pack.Length, 0, "Coding Assist pack must not be empty");
            CollectionAssert.AllItemsAreNotNull(pack);

            // No duplicates (case-insensitive)
            var distinct = pack.Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.AreEqual(pack.Length, distinct.Length, "Coding Assist pack must have no duplicates");

            // CodingAssist == group A + group B
            CollectionAssert.AreEquivalent(
                AllowlistPresets.ScriptWrite.Concat(AllowlistPresets.InspectorSet).ToArray(),
                pack);
        }

        [Test]
        public void AllowlistPresets_ImportingPack_AllowsForbiddenAndGrantSkills_UnderApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // Group A (script writes) simulated as NeverInSemi: Forbidden before import
            var scriptWriteSample = MakeSkill(AllowlistPresets.ScriptWrite[0],
                mayTriggerReload: true, risk: "high");
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(scriptWriteSample),
                "Script-write skill must be forbidden before import");

            // Group B (Inspector assignment) simulated as FullAuto and not forbidden: NeedsGrant before import
            var inspectorSample = MakeSkill(AllowlistPresets.InspectorSet[0],
                op: SkillOperation.Create);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(inspectorSample),
                "Inspector-set skill must need grant before import");

            // Simulate "importing the coding-assist pack": add each one to the Allowlist
            foreach (var name in AllowlistPresets.CodingAssist)
                SkillsModeManager.AddToAllowlist(name);

            // After import: both group A and group B are allowed (an Allowlist hit takes priority over forbidden / grant)
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(scriptWriteSample),
                "Script-write skill must be allowed after import");
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(inspectorSample),
                "Inspector-set skill must be allowed after import");

            // Every item in the pack is now in the allowlist
            foreach (var name in AllowlistPresets.CodingAssist)
                Assert.IsTrue(SkillsModeManager.IsInAllowlist(name),
                    "Pack member must be in allowlist after import: " + name);
        }
    }
}

// Producer:Betsy
