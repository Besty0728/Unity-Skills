using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unit tests for the v1.9 Skill mode permission system (plan section 11).
    ///
    /// Covers three operating modes (Approval / Auto / Bypass), two approval channels
    /// (Dialog / Panel), auto NeverInSemi judgement, grant token lifecycle, EditorPrefs
    /// persistence and the upgrade-compat rule (existing install → Bypass).
    ///
    /// v1.9 改版后新增覆盖（工作包 A）：
    /// - Allowlist 通道 (AddToAllowlist / RemoveFromAllowlist / ClearAllowlist / IsInAllowlist)
    /// - Allowlist 优先于 IsForbiddenInSemi
    /// - 单次有效 grant：TryGrant 不再永久写白名单
    /// - TryGrantAndReturnArgs (方案 B 一步执行) + ConsumeOneShotBypass
    /// - 老 GrantedSkills EditorPrefs → 新 AllowlistSkills 迁移幂等
    ///
    /// Side-effects: every test SetUp wipes UnitySkills_* EditorPrefs and resets the
    /// in-memory grant table + on-disk audit log. Legacy install marker keys are
    /// cleared but NOT restored — if a developer ran the production package on this
    /// machine before, the OneTimeSetUp warning lists the keys that get wiped.
    /// </summary>
    [TestFixture]
    public class SkillsModeManagerTests
    {
        // Pre-v1.9 EditorPrefs keys that mark an "existing install" (plan section 10
        // / SkillsModeManager.IsExistingInstall). Presence of any of these flips the
        // default mode from Approval (fresh install) to Bypass (upgrade-compat).
        private static readonly string[] LegacyInstallKeys =
        {
            "UnitySkills_RequireConfirmation",
            "UnitySkills_PreferredPort",
            "UnitySkills_LogLevel",
            "UnitySkills_Language",
            "UnitySkills_RequestTimeoutMinutes",
            "UnitySkills_KeepAliveIntervalSeconds",
            "UnitySkills_AutoInstallPackagesOnStartup",
        };

        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var existing = LegacyInstallKeys.Where(EditorPrefs.HasKey).ToList();
            if (existing.Count > 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[SkillsModeManagerTests] Legacy UnitySkills_* prefs detected. " +
                    "Tests in this fixture clear them and they will NOT be restored: "
                    + string.Join(", ", existing));
            }
        }

        [SetUp]
        public void SetUp()
        {
            // Force IsExistingInstall() == false so the default mode getter returns
            // Approval unless a test explicitly opts back into "old install" state.
            foreach (var k in LegacyInstallKeys) EditorPrefs.DeleteKey(k);
            SkillsModeManager.ResetForTests();
            SkillsAuditLog.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var k in LegacyInstallKeys) EditorPrefs.DeleteKey(k);
            SkillsModeManager.ResetForTests();
            SkillsAuditLog.ResetForTests();
        }

        // ─────────────────────────────────────────────────────────────────
        //  helpers
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a SkillInfo with only the fields CheckAccess / IsForbiddenInSemi read.
        /// All other fields (Method, Parameters, etc.) are intentionally null because
        /// the mode manager never touches them.
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

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #1 — Bypass mode allows everything
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_BypassMode_AnySkill_AlwaysAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            // Plain SemiAuto / FullAuto — trivially allowed.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("safe", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("normal")));

            // Every metadata flavour that IsForbiddenInSemi would normally trip on —
            // Bypass mode bypasses the check entirely.
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("del", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("play", mayEnterPlayMode: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("reload", mayTriggerReload: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("high_risk", risk: "high")));
            // explicit "never in semi" list (e.g. scene_clear)
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("scene_clear")));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #2 — Auto mode allows Semi & Full
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_AutoMode_SemiAutoAndFullAuto_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("semi_one", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("full_one", SkillMode.FullAuto)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #3 — Auto mode still blocks auto-judged NeverInSemi
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_AutoMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #4 — Approval + SemiAuto bypasses the grant gate
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_SemiAutoSkill_Allowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("preview_thing", SkillMode.SemiAuto)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #5 — Approval + FullAuto without grant → NeedsGrant
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_FullAutoUngranted_NeedsGrant()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill("smart_layout")));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #6 — Approval + Dialog grant：单次有效，不进白名单
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_DialogChannel_GrantIsOneShot_NotWrittenToAllowlist()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false; // explicit, default

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Dialog, channel);
            Assert.Greater(ttl, 0, "TTL should be a positive number of seconds");
            Assert.IsFalse(string.IsNullOrWhiteSpace(token), "Token must be non-empty");

            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // v1.9 改版：grant 不再永久写白名单。重新 CheckAccess（无 one-shot 重入）应再次 NeedsGrant。
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #7 — Approval + Panel ON + no approval yet → PendingApproval
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_GrantBeforeApprove_ReturnsPendingApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Panel, channel);

            // AI re-plays the token before the user clicks Approve on the panel.
            Assert.AreEqual(GrantOutcome.PendingApproval,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);

            // The entry is still alive in the panel pending list.
            var pending = SkillsModeManager.PeekPendingForTests(token);
            Assert.IsNotNull(pending);
            Assert.AreEqual(skillName, pending.SkillName);
            Assert.IsFalse(pending.ApprovedByPanel);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #8 — Approval + Panel Approve → entry 保留、单次有效
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_ApproveKeepsEntry_GrantThenOneShot()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Approve(token));
            // v1.9 改版：Approve 不再永久写白名单，entry 保留等待后续 grant 触发一次性执行。
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            var pendingAfterApprove = SkillsModeManager.PeekPendingForTests(token);
            Assert.IsNotNull(pendingAfterApprove, "Entry must be kept after Approve for AI re-grant.");
            Assert.IsTrue(pendingAfterApprove.ApprovedByPanel);

            // AI 后续 grant 走 Granted 分支并消费 entry；不写白名单。
            Assert.AreEqual(GrantOutcome.Granted,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token),
                "Entry must be consumed after Granted.");
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #9 — Panel Deny + grant → false
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Approval_PanelChannel_DenyThenGrant_ReturnsFalse()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = true;

            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);

            Assert.IsTrue(SkillsModeManager.Deny(token));

            // Token entry is gone, grant must fail and the skill stays out of allowlist.
            Assert.IsFalse(SkillsModeManager.TryGrant(skillName, token, args));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #10 — Approval + NeverInSemi → Forbidden (no grant route)
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CheckAccess_ApprovalMode_NeverInSemiSkill_Forbidden()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // v1.9.x removed the historical _explicitNeverList (scene_clear/scene_new/
            // batch_apply) — see SkillsModeManager.cs:89-94. Forbidden now means a
            // metadata flag is set (Operation=Delete / MayEnterPlayMode / MayTriggerReload
            // / RiskLevel=high). Cover two distinct flavours to exercise the OR-branch.
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("delete_thing", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("reload_scene", mayTriggerReload: true)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #11 — invalid token paths
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void TryGrant_InvalidToken_ReturnsFalseAndInvalid()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // Never-issued token.
            Assert.IsFalse(SkillsModeManager.TryGrant("any_skill", "bogus_token_xxx", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "bogus_token_xxx", "{}"));

            // Empty / whitespace token.
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "   ", "{}"));

            // Valid token but mismatched args → Invalid.
            const string skill = "smart_layout";
            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skill, "{\"a\":1}");
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed(skill, token, "{\"a\":2}"));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #12 — Allowlist + remove → 回到 NeedsGrant
        // ═════════════════════════════════════════════════════════════════

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

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #13 — CurrentMode persists via EditorPrefs
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_Setter_PersistsToEditorPrefs_AndGetterReadsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;

            // Direct EditorPrefs verification — the setter persisted under PrefKeyMode.
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual("Auto", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);

            // Switching writes the new value (overwrite, not append).
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            Assert.AreEqual("Approval", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #14 — IsForbiddenInSemi auto judgement
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void IsForbiddenInSemi_CoversAllAutoJudgementBranches()
        {
            // v1.9.x is metadata-only: four flavours that MUST be forbidden in
            // Approval / Auto. The historical _explicitNeverList fallback was
            // removed (see SkillsModeManager.cs:89-94) — no name-based check.
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

            // A plain skill with NO forbidden metadata must NOT be forbidden,
            // even if its name resembles a historical never-list entry.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("scene_clear")),
                "Name-only matching is intentionally not a forbidden trigger anymore");

            // Plain SemiAuto / FullAuto without any flag must NOT be forbidden.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_semi", SkillMode.SemiAuto)));
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_full", SkillMode.FullAuto)));

            // Combined-flags Operation (Query|Modify) without Delete remains allowed.
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("query_modify", op: SkillOperation.Query | SkillOperation.Modify)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #15 — Audit log records grant events
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void AuditLog_GrantEvent_AppendThenFlushSync_ReadRecentContainsIt()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{\"x\":1}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // Append is async; force a flush so ReadRecent sees the line.
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

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #16 — Old install (legacy pref exists) → Bypass default
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_OldInstall_NoExplicitMode_DefaultsToBypass()
        {
            // SetUp already cleared every legacy + mode key. Plant just one legacy
            // marker — IsExistingInstall uses HasKey only, value doesn't matter.
            EditorPrefs.SetInt("UnitySkills_PreferredPort", 12345);

            Assert.AreEqual(SkillsOperatingMode.Bypass, SkillsModeManager.CurrentMode);
            // Getter must NOT write PrefKeyMode as a side effect — that would prevent
            // the next upgrade from re-evaluating the default.
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #17 — Fresh install (no legacy, no explicit) → Approval
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void CurrentMode_FreshInstall_NoKeys_DefaultsToApproval()
        {
            // SetUp left zero UnitySkills_* keys behind.
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #18 — Allowlist API: add / remove / clear / IsInAllowlist
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Allowlist_AddRemoveClear_RoundTripsAndAudits()
        {
            Assert.IsFalse(SkillsModeManager.IsInAllowlist("alpha"));
            CollectionAssert.IsEmpty(SkillsModeManager.AllowlistSkills);

            // Add new → true; Add same → false; query reflects.
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist("alpha"));
            Assert.IsTrue(SkillsModeManager.IsInAllowlist("alpha"));
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("beta"));
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta" }, SkillsModeManager.AllowlistSkills);

            // Remove existing → true; remove missing → false.
            Assert.IsTrue(SkillsModeManager.RemoveFromAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.RemoveFromAllowlist("alpha"));
            Assert.IsFalse(SkillsModeManager.IsInAllowlist("alpha"));

            // Clear → empty.
            SkillsModeManager.ClearAllowlist();
            CollectionAssert.IsEmpty(SkillsModeManager.AllowlistSkills);

            // Whitespace/null inputs are no-ops.
            Assert.IsFalse(SkillsModeManager.AddToAllowlist(""));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist("   "));
            Assert.IsFalse(SkillsModeManager.AddToAllowlist(null));
            Assert.IsFalse(SkillsModeManager.RemoveFromAllowlist(null));
            Assert.IsFalse(SkillsModeManager.IsInAllowlist(null));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #19 — Allowlist overrides IsForbiddenInSemi
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void Allowlist_OverridesForbiddenInSemi_HighRiskSkillAllowed()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // v1.9.x is metadata-only — use a metadata-forbidden skill instead of
            // the now-defunct explicit never-list name ("scene_clear"). See
            // SkillsModeManager.cs:89-94 for the rationale on removing the list.
            const string skill = "delete_thing";

            // 默认拦截（metadata flag → Forbidden）
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill(skill, op: SkillOperation.Delete)));

            // 加入 Allowlist 后被放行（Allowlist 优先于 IsForbiddenInSemi）
            Assert.IsTrue(SkillsModeManager.AddToAllowlist(skill));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skill, op: SkillOperation.Delete)));

            // 高危 mayTriggerReload 也同样可被 Allowlist 放行
            const string reloadSkill = "reload_scene";
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill(reloadSkill, mayTriggerReload: true)));
            Assert.IsTrue(SkillsModeManager.AddToAllowlist(reloadSkill));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(reloadSkill, mayTriggerReload: true)));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #20 — TryGrantAndReturnArgs（方案 B 一步执行）
        // ═════════════════════════════════════════════════════════════════

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

            // entry 被消费
            Assert.IsNull(SkillsModeManager.PeekPendingForTests(token));

            // 二次调用同 token 必须 Invalid
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

            // entry 必须保留以便后续 Approve
            Assert.IsNotNull(SkillsModeManager.PeekPendingForTests(token));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #21 — One-shot bypass：grant 方案 B 让 CheckAccess 单次放行
        // ═════════════════════════════════════════════════════════════════

        [Test]
        public void OneShotBypass_AfterTryGrantAndReturnArgs_CheckAccessAllowedOnce()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            const string skillName = "smart_layout";
            const string args = "{}";

            var (token, _, _) = SkillsModeManager.IssueGrantRequest(skillName, args);
            var (outcome, _, _) = SkillsModeManager.TryGrantAndReturnArgs(skillName, token, args);
            Assert.AreEqual(GrantOutcome.Granted, outcome);

            // 第一次 CheckAccess 命中 one-shot，被放行
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));

            // 再次 CheckAccess 已经消费完，回到 NeedsGrant
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }

        [Test]
        public void ConsumeOneShotBypass_NameMismatchOrEmpty_ReturnsFalse()
        {
            // 直接构造空状态
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("anything"));

            // 设置 one-shot 后名字不匹配也不消费
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            var (token, _, _) = SkillsModeManager.IssueGrantRequest("alpha", "{}");
            SkillsModeManager.TryGrantAndReturnArgs("alpha", token, "{}");

            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("beta"));
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass(""));
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass(null));

            // 名字匹配（大小写无关）才消费
            Assert.IsTrue(SkillsModeManager.ConsumeOneShotBypass("ALPHA"));
            // 消费后下一次必失败
            Assert.IsFalse(SkillsModeManager.ConsumeOneShotBypass("alpha"));
        }

        // ═════════════════════════════════════════════════════════════════
        //  Test matrix #22 — EditorPrefs 迁移：legacy granted → allowlist，幂等
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Force the in-memory allowlist cache field to null so the next public access
        /// re-runs <c>EnsureAllowlistLoaded</c> → <c>MigrateLegacyGrantedToAllowlist</c>.
        /// Mirrors what a fresh editor launch would do.
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
            // 1) 模拟老 v1.9 install：写 legacy granted、清掉迁移标记和新 allowlist。
            EditorPrefs.SetString(PrefKeyLegacyGranted, "[\"alpha\",\"beta\",\"gamma\"]");
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            // 2) 首次访问触发迁移
            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.AreEquivalent(new[] { "alpha", "beta", "gamma" }, snapshot);

            // 3) 迁移完成标记已写入
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false),
                "Migration must set the done flag after running");

            // 4) Legacy key 故意保留（回滚标记）
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyLegacyGranted),
                "Legacy granted key must be preserved as rollback marker");

            // 5) 新 allowlist 已持久化
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyAllowlist),
                "Allowlist pref must be persisted after migration");

            // 6) 审计事件已写入
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
            // 第一次：跑迁移
            EditorPrefs.SetString(PrefKeyLegacyGranted, "[\"alpha\"]");
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();
            var _first = SkillsModeManager.AllowlistSkills;
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false));

            // 清审计后，再"重启"一次（done flag 仍在）
            SkillsAuditLog.ResetForTests();
            ForceAllowlistReload();
            var snapshotAfterReload = SkillsModeManager.AllowlistSkills;

            // 内容仍来自持久化的 PrefKeyAllowlist，不重复加 legacy 的数据
            CollectionAssert.AreEquivalent(new[] { "alpha" }, snapshotAfterReload);

            // 也不重复发 allowlist_migrated 审计事件
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
            // Fresh install：没有任何 legacy 数据
            EditorPrefs.DeleteKey(PrefKeyLegacyGranted);
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            ForceAllowlistReload();

            var snapshot = SkillsModeManager.AllowlistSkills;
            CollectionAssert.IsEmpty(snapshot);
            Assert.IsTrue(EditorPrefs.GetBool(PrefKeyMigrationDone, false),
                "Done flag must still be set on fresh install so future reads skip migration");
        }
    }
}
