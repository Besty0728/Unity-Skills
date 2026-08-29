namespace UnitySkills
{
    /// <summary>
    /// Server-side three-tier operating mode, aligned with Claude Code permission modes.
    /// Controlled by the Unity panel, stored in EditorPrefs (per-machine).
    /// </summary>
    public enum SkillsOperatingMode
    {
        /// <summary>The AI must ask the user for authorization before executing a FullAuto skill. Note: this is NOT the factory-default mode — new installs default to Auto, old installs default to Bypass, decided by SkillsModeManager.CurrentMode.</summary>
        Approval,
        /// <summary>AI decides automatically — FullAuto skills execute directly (write-audited), only NeverInSemi is blocked.</summary>
        Auto,
        /// <summary>Skip approval — all skills pass through directly, only ConfirmationToken still applies.</summary>
        Bypass
    }

    /// <summary>
    /// The risk tier a skill declares on [UnitySkill].
    /// NeverInSemi is no longer manually tagged; it's auto-determined by <see cref="SkillsModeManager.IsForbiddenInSemi"/>.
    /// </summary>
    public enum SkillMode
    {
        /// <summary>Explicitly low-risk, executes directly under all three tiers.</summary>
        SemiAuto,
        /// <summary>Default; requires user authorization to execute under Approval mode.</summary>
        FullAuto
    }
}

// Producer:Betsy
