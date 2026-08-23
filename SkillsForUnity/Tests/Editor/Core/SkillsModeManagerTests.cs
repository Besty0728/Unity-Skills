using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 技能权限模式系统的单元测试。
    ///
    /// 覆盖三种操作模式（Approval / Auto / Bypass）、两条审批通道（Dialog / Panel）、
    /// NeverInSemi 自动判定、grant token 生命周期、EditorPrefs 持久化，
    /// 以及升级兼容规则（老安装 → Bypass）。
    ///
    /// 另外覆盖：
    /// - Allowlist 通道 (AddToAllowlist / RemoveFromAllowlist / ClearAllowlist / IsInAllowlist)
    /// - Allowlist 优先于 IsForbiddenInSemi
    /// - 单次有效 grant：TryGrant 不再永久写白名单
    /// - TryGrantAndReturnArgs (方案 B 一步执行) + ConsumeOneShotBypass
    /// - 老 GrantedSkills EditorPrefs → 新 AllowlistSkills 迁移幂等
    ///
    /// 权限相关的 EditorPrefs 在夹具开始时备份、结束时还原。
    /// "老安装"行为用测试专用 override 模拟，从而绝不改动无关的 UnitySkills EditorPrefs
    /// （语言、端口、日志等）。
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
        /// 只填 CheckAccess / IsForbiddenInSemi 会读的字段来构造 SkillInfo。
        /// 其余字段（Method、Parameters 等）故意留 null——模式管理器从不碰它们。
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

            // 普通 SemiAuto / FullAuto，理应直接放行。
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("safe", SkillMode.SemiAuto)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("normal")));

            // 平时会被 IsForbiddenInSemi 拦下的各种元数据组合——Bypass 模式完全跳过该检查。
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("del", op: SkillOperation.Delete)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("play", mayEnterPlayMode: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("reload", mayTriggerReload: true)));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("high_risk", risk: "high")));
            // 曾在 never-list 里的名字（scene_clear）：_explicitNeverList 移除后已不再被自动禁止，
            // 而 Bypass 下它和别的技能一样放行。
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
            SkillsModeManager.PanelApprovalRequired = false; // 与默认值相同，显式写出以示意图

            const string skillName = "smart_layout";
            const string args = "{\"target\":\"Cube\"}";

            var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(skillName, args);
            Assert.AreEqual(SkillsModeManager.ApprovalChannel.Dialog, channel);
            Assert.Greater(ttl, 0, "TTL should be a positive number of seconds");
            Assert.IsFalse(string.IsNullOrWhiteSpace(token), "Token must be non-empty");

            Assert.IsTrue(SkillsModeManager.TryGrant(skillName, token, args));

            // grant 不再永久写白名单。重新 CheckAccess（无 one-shot 重入）应再次 NeedsGrant。
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

            // 用户还没在面板上点 Approve，AI 就先把 token 回放了一次。
            Assert.AreEqual(GrantOutcome.PendingApproval,
                SkillsModeManager.TryGrantDetailed(skillName, token, args));
            CollectionAssert.DoesNotContain(SkillsModeManager.AllowlistSkills, skillName);

            // 该条目仍然活在面板的待批列表里。
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
            // Approve 不再永久写白名单，entry 保留等待后续 grant 触发一次性执行。
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

            // 从未签发过的 token。
            Assert.IsFalse(SkillsModeManager.TryGrant("any_skill", "bogus_token_xxx", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "bogus_token_xxx", "{}"));

            // 空串 / 纯空白 token。
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "", "{}"));
            Assert.AreEqual(GrantOutcome.Invalid,
                SkillsModeManager.TryGrantDetailed("any_skill", "   ", "{}"));

            // token 有效但 args 不匹配 → Invalid。
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

            // 直接查 EditorPrefs：确认 setter 真的写进了 PrefKeyMode。
            Assert.IsTrue(EditorPrefs.HasKey(PrefKeyMode));
            Assert.AreEqual("Auto", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Auto, SkillsModeManager.CurrentMode);

            // 切换模式是覆盖写新值，而非追加。
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            Assert.AreEqual("Approval", EditorPrefs.GetString(PrefKeyMode));
            Assert.AreEqual(SkillsOperatingMode.Approval, SkillsModeManager.CurrentMode);
        }

        [Test]
        public void IsForbiddenInSemi_CoversAllAutoJudgementBranches()
        {
            // 在 Approval / Auto 下必须被禁止的四种组合（纯由元数据判定）。
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

            // 不带任何高危标记的普通 SemiAuto / FullAuto 不得被禁止。
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_semi", SkillMode.SemiAuto)));
            Assert.IsFalse(SkillsModeManager.IsForbiddenInSemi(
                MakeSkill("plain_full", SkillMode.FullAuto)));

            // 组合标记的 Operation（Query|Modify）只要不含 Delete 就仍然放行。
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

            // 写入是异步的，必须强制 flush，ReadRecent 才看得到这一行。
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
            // getter 绝不能顺手把 PrefKeyMode 写进去——一旦写了，下次升级就没法重新判定默认值。
            Assert.IsFalse(EditorPrefs.HasKey(PrefKeyMode));
        }

        [Test]
        public void CurrentMode_FreshInstall_NoKeys_DefaultsToAuto()
        {
            // SetUp 之后不应残留任何 UnitySkills_* 键。
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

            // 空白 / null 入参一律无操作。
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

            // 默认拦截：RiskLevel="high" 由 metadata 判定为 NeverInSemi
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(MakeSkill("hot_skill", risk: "high")));

            // 加入 Allowlist 后被放行（Allowlist 优先于 IsForbiddenInSemi）
            Assert.IsTrue(SkillsModeManager.AddToAllowlist("hot_skill"));
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill("hot_skill", risk: "high")));

            // 同样适用于 Delete 操作判定的高危 skill
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

        /// <summary>
        /// 把内存里的白名单缓存字段强制置 null，使下一次公开访问重新走
        /// <c>EnsureAllowlistLoaded</c> → <c>MigrateLegacyGrantedToAllowlist</c>。
        /// 等价于模拟一次编辑器冷启动。
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
            // 1) 模拟老 install：写 legacy granted、清掉迁移标记和新 allowlist。
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

        [Test]
        public void AllowlistPresets_CodingAssist_IsNonEmptyDistinct_AndMergesBothGroups()
        {
            var pack = AllowlistPresets.CodingAssist;
            Assert.IsNotNull(pack);
            Assert.Greater(pack.Length, 0, "Coding Assist pack must not be empty");
            CollectionAssert.AllItemsAreNotNull(pack);

            // 无重复（忽略大小写）
            var distinct = pack.Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
            Assert.AreEqual(pack.Length, distinct.Length, "Coding Assist pack must have no duplicates");

            // CodingAssist == 组A + 组B
            CollectionAssert.AreEquivalent(
                AllowlistPresets.ScriptWrite.Concat(AllowlistPresets.InspectorSet).ToArray(),
                pack);
        }

        [Test]
        public void AllowlistPresets_ImportingPack_AllowsForbiddenAndGrantSkills_UnderApproval()
        {
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;

            // 组A（脚本写）模拟为 NeverInSemi：导入前 Forbidden
            var scriptWriteSample = MakeSkill(AllowlistPresets.ScriptWrite[0],
                mayTriggerReload: true, risk: "high");
            Assert.AreEqual(SkillsModeManager.AccessResult.Forbidden,
                SkillsModeManager.CheckAccess(scriptWriteSample),
                "Script-write skill must be forbidden before import");

            // 组B（Inspector 赋值）模拟为 FullAuto 非 forbidden：导入前 NeedsGrant
            var inspectorSample = MakeSkill(AllowlistPresets.InspectorSet[0],
                op: SkillOperation.Create);
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(inspectorSample),
                "Inspector-set skill must need grant before import");

            // 模拟"导入辅助代码编写包"：逐个加入 Allowlist
            foreach (var name in AllowlistPresets.CodingAssist)
                SkillsModeManager.AddToAllowlist(name);

            // 导入后：组A + 组B 全部放行（Allowlist 命中优先于 forbidden / grant）
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(scriptWriteSample),
                "Script-write skill must be allowed after import");
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(inspectorSample),
                "Inspector-set skill must be allowed after import");

            // 包内每一项都已在白名单
            foreach (var name in AllowlistPresets.CodingAssist)
                Assert.IsTrue(SkillsModeManager.IsInAllowlist(name),
                    "Pack member must be in allowlist after import: " + name);
        }
    }
}

// Producer:Betsy
