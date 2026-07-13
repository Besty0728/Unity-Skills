namespace UnitySkills
{
    /// <summary>
    /// 服务端三档操作模式，对齐 Claude Code permission modes。
    /// 由 Unity 面板控制，存储在 EditorPrefs（per-machine）。
    /// </summary>
    public enum SkillsOperatingMode
    {
        /// <summary>AI 必须询问用户取得授权后才能执行 FullAuto skill。注意：这并非出厂默认模式——新装默认 Auto、老装默认 Bypass，由 SkillsModeManager.CurrentMode 决策。</summary>
        Approval,
        /// <summary>AI 自动判断 — FullAuto skill 直接执行（写审计），仅 NeverInSemi 拦截。</summary>
        Auto,
        /// <summary>跳过审批 — 所有 skill 直接放行，仅 ConfirmationToken 仍生效。</summary>
        Bypass
    }

    /// <summary>
    /// Skill 在 [UnitySkill] 上标注的风险档位。
    /// NeverInSemi 不再手标，由 <see cref="SkillsModeManager.IsForbiddenInSemi"/> 自动判定。
    /// </summary>
    public enum SkillMode
    {
        /// <summary>明确低风险，三档模式下均直接执行。</summary>
        SemiAuto,
        /// <summary>默认；Approval 模式下需要用户授权才能执行。</summary>
        FullAuto
    }
}

// Producer:Betsy
