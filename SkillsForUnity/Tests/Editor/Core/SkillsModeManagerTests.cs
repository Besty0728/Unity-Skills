using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unit tests for the skill permission mode system.
    ///
    /// Covers the three operating modes (Approval / Auto / Bypass), the two approval channels
    /// (Dialog / Panel), automatic NeverInSemi determination, the grant token lifecycle,
    /// EditorPrefs persistence, and the upgrade compatibility rule (old install → Bypass).
    ///
    /// Also covers:
    /// - The Allowlist channel (AddToAllowlist / RemoveFromAllowlist / ClearAllowlist / IsInAllowlist)
    /// - Allowlist taking priority over IsForbiddenInSemi
    /// - Single-use grants: TryGrant no longer writes the allowlist permanently
    /// - TryGrantAndReturnArgs (Plan B: one-step execution) + ConsumeOneShotBypass
    /// - Idempotent migration from the old GrantedSkills EditorPrefs key to the new AllowlistSkills key
    ///
    /// Permission-related EditorPrefs are backed up at the start of the fixture and restored at
    /// the end. "Old install" behavior is simulated with a test-only override, so it never
    /// touches unrelated UnitySkills EditorPrefs (language, port, logging, etc.).
    /// </summary>
    [TestFixture]
    public class SkillsModeManagerTests
    {
        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        private bool _hadMode;
        private string _savedMode;
        private bool _hadPanelApproval;
        private bool _savedPanelApproval;
        private bool _hadAllowlist;
        private string _savedAllowlist;
        private bool _hadMigrationDone;
        private bool _savedMigrationDone;
        private bool _hadLegacyGranted;
        private string _savedLegacyGranted;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _hadMode = EditorPrefs.HasKey(PrefKeyMode);
            _savedMode = EditorPrefs.GetString(PrefKeyMode, string.Empty);
            _hadPanelApproval = EditorPrefs.HasKey(PrefKeyPanelApproval);
            _savedPanelApproval = EditorPrefs.GetBool(PrefKeyPanelApproval, false);
            _hadAllowlist = EditorPrefs.HasKey(PrefKeyAllowlist);
            _savedAllowlist = EditorPrefs.GetString(PrefKeyAllowlist, string.Empty);
            _hadMigrationDone = EditorPrefs.HasKey(PrefKeyMigrationDone);
            _savedMigrationDone = EditorPrefs.GetBool(PrefKeyMigrationDone, false);
            _hadLegacyGranted = EditorPrefs.HasKey(PrefKeyLegacyGranted);
            _savedLegacyGranted = EditorPrefs.GetString(PrefKeyLegacyGranted, string.Empty);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            RestoreString(PrefKeyMode, _hadMode, _savedMode);
            RestoreBool(PrefKeyPanelApproval, _hadPanelApproval, _savedPanelApproval);
            RestoreString(PrefKeyAllowlist, _hadAllowlist, _savedAllowlist);
            RestoreBool(PrefKeyMigrationDone, _hadMigrationDone, _savedMigrationDone);
            RestoreString(PrefKeyLegacyGranted, _hadLegacyGranted, _savedLegacyGranted);
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            ForceAllowlistReload();
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
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
        }

        private static void RestoreString(string key, bool existed, string value)
        {
            if (existed) EditorPrefs.SetString(key, value);
            else EditorPrefs.DeleteKey(key);
        }

        private static void RestoreBool(string key, bool existed, bool value)
        {
            if (existed) EditorPrefs.SetBool(key, value);
            else EditorPrefs.DeleteKey(key);
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
        public void CurrentMode_Setter_PersistsToEditorPrefs_AndGetterReadsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            // Directly checks EditorPrefs: confirms the setter really wrote to PrefKeyMode.
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual("Auto", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);

            // Switching modes overwrites the value rather than appending.
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            Assert.AreEqual("Approval", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
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
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        [Test]
        public void CurrentMode_FreshInstall_NoKeys_DefaultsToAuto()
        {
            // No UnitySkills_* keys should remain after SetUp.
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        [Test]
        public void ResetForTests_DomainReloadRecovery_RestoresExplicitBypassMode()
        {
            SkillsModeManager.CompleteTestPreferenceRecovery();
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            SkillsModeManager.ResetForTests();
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));

            SkillsModeManager.RestorePreferencesAfterTestDomainReload();

            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
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
            // 1) Simulate an old install: write legacy granted, clear the migration marker and the new allowlist.
            EditorPrefs.SetString(PrefKeyLegacyGranted, "[\"alpha\",\"beta\",\"gamma\"]");
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            // 2) The first access triggers the migration
            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta", "gamma" }, snapshot);

            // 3) The migration-done marker has been written
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false),
                "Migration must set the done flag after running");

            // 4) The legacy key is deliberately kept (as a rollback marker)
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyLegacyGranted),
                "Legacy granted key must be preserved as rollback marker");

            // 5) The new allowlist has been persisted
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyAllowlist),
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
            EditorPrefs.SetString(PrefKeyLegacyGranted, "[\"alpha\"]");
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();
            var _first = SkillsModeManager.AllowlistSkills;
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false));

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
            EditorPrefs.DeleteKey(PrefKeyLegacyGranted);
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.IsEmpty(snapshot);
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false),
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
