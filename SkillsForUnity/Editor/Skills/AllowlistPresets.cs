using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Preset Allowlist packages: a set of commonly-used "辅助代码编写" (coding-assist) REST skills, for
    /// <see cref="AllowlistPickerWindow"/> to check and import with one click.
    ///
    /// Inclusion principle — only include write operations where "adding them to the Allowlist has incremental value":
    /// pure read/query skills (SemiAuto, already permitted in every mode) and delete-type skills (forbid, left for the user
    /// to add explicitly) are never included. See each group's comment for the exact mode this applies to.
    /// </summary>
    public static class AllowlistPresets
    {
        /// <summary>
        /// Group A · script writes. These skills are marked <c>MayTriggerReload + RiskLevel="high"</c> and are
        /// judged NeverInSemi by <see cref="SkillsModeManager.IsForbiddenInSemi"/> — under
        /// both Auto and Approval they return <c>MODE_FORBIDDEN</c>, making this the one genuine "must be in the Allowlist" hard requirement for coding.
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
        /// Group B · Inspector assignment. These are FullAuto (approvalBehavior=grant, not forbidden):
        /// they already execute directly under Auto mode; adding them to the Allowlist mainly lets Approval mode skip granting them one by one.
        /// </summary>
        public static readonly string[] InspectorSet =
        {
            "component_add",
            "component_set_property",
            "component_set_property_batch",
            "component_set_enabled",
        };

        /// <summary>
        /// The "辅助代码编写" (coding-assist) preset package: the merged list of Group A + Group B (declaration order preserved, no duplicates within a group).
        /// AllowlistPickerWindow's "勾选辅助代码编写包" button imports exactly this list.
        /// </summary>
        public static readonly string[] CodingAssist =
            ScriptWrite.Concat(InspectorSet).ToArray();
    }
}

// Producer:Betsy
