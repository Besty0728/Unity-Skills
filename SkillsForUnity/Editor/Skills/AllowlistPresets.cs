using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// 预置 Allowlist 包：一组常用的「辅助代码编写」REST skill，供
    /// <see cref="AllowlistPickerWindow"/> 一键勾选导入。
    ///
    /// 收录原则——只收"加入 Allowlist 才有增量价值"的写操作：
    /// 纯读 / 查询 skill（SemiAuto，任何模式本就放行）与删除类（forbid，留给用户
    /// 显式追加）一律不收。具体生效模式见各组注释。
    /// </summary>
    public static class AllowlistPresets
    {
        /// <summary>
        /// 组 A · 脚本写。这些 skill 标了 <c>MayTriggerReload + RiskLevel="high"</c>，
        /// 被 <see cref="SkillsModeManager.IsForbiddenInSemi"/> 判为 NeverInSemi——在
        /// Auto / Approval 下都返回 <c>MODE_FORBIDDEN</c>，是唯一"非 Allowlist 不可"的编码刚需。
        /// </summary>
        public static readonly string[] ScriptWrite =
        {
            "script_create",
            "script_append",
            "script_replace",
            "script_rename",
            "script_move",
        };

        /// <summary>
        /// 组 B · Inspector 赋值。这些是 FullAuto（approvalBehavior=grant，非 forbidden）：
        /// 在 Auto 模式本就直接执行，加入 Allowlist 主要让 Approval 模式免去逐次 grant。
        /// </summary>
        public static readonly string[] InspectorSet =
        {
            "component_add",
            "component_set_property",
            "component_set_property_batch",
            "component_set_enabled",
        };

        /// <summary>
        /// 「辅助代码编写」预置包：组 A + 组 B 的合并列表（保持声明顺序，组内无重复）。
        /// AllowlistPickerWindow 的"勾选辅助代码编写包"按钮即导入此列表。
        /// </summary>
        public static readonly string[] CodingAssist =
            ScriptWrite.Concat(InspectorSet).ToArray();
    }
}

// Producer:Betsy
