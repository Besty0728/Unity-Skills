using System;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Persistent advisory flag for the guide-mode hint surfaced on <c>/health</c>.
    /// When enabled, the server tells AI clients to prefer manual step guidance
    /// (via SKILL_GUIDE.md) over automated write skills for simple tasks.
    /// This is advisory only — there is no server-side enforcement.
    /// </summary>
    public static class SkillsGuideMode
    {
        private const string PrefKeyGuideMode = "UnitySkills_GuideMode";

        public static event Action OnChanged;

        /// <summary>
        /// Whether guide-mode hint is enabled. Persisted in EditorPrefs.
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKeyGuideMode, false);
            set
            {
                if (value == Enabled) return;
                EditorPrefs.SetBool(PrefKeyGuideMode, value);
                RaiseChanged();
            }
        }

        private static void RaiseChanged()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception ex) { SkillsLogger.LogWarning($"GuideMode OnChanged handler threw: {ex.Message}"); }
        }
    }
}
