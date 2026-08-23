using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 专项覆盖一次性授权放行 token 的加固。SkillRouter.Execute 有四处参数校验提前返回，位置正好夹在
    /// TryGrantAndReturnArgs（写入 ThreadStatic token）与 CheckAccess（消费它）之间，任何一处提前退出
    /// 若漏了 ClearOneShotBypass，token 就会泄漏到同一线程上的另一个无关请求里。
    /// 硬性 30 秒 deadline 是针对这点的第二道防线。
    ///
    /// 本夹具与 SkillsModeManagerTests.cs 互补，不重复它已有的授权/白名单/迁移覆盖。
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

        /// <summary>最小 SkillInfo：只填 CheckAccess / IsForbiddenInSemi 会读的字段。</summary>
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

            // 模拟调用方在授权与 CheckAccess 之间无条件清掉待用的一次性 token
            // （例如 SkillRouter.Execute 撞上参数校验提前返回）。
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

            // SkillsModeManager 没有可注入的时钟，而 deadline 是 ThreadStatic 字段——在同一线程上
            // 用反射把它推到过去，而不是在单测里真睡 30 多秒。
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

            // 换个 skill 名字来查权限，不得消费掉待用的一次性 token。
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill("unrelated_skill")));

            // 原 skill 的一次性授权仍然有效，且这一次之后即被消费掉。
            Assert.AreEqual(SkillsModeManager.AccessResult.Allowed,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
            Assert.AreEqual(SkillsModeManager.AccessResult.NeedsGrant,
                SkillsModeManager.CheckAccess(MakeSkill(skillName)));
        }
    }
}

// Producer:Betsy
