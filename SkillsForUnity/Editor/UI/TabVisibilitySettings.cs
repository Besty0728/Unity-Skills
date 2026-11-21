using System;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Manages the user preferences for top-level tab visibility.
    /// Analytics tab visibility is also gated on SkillTelemetryService.Enabled.
    /// The evaluation tab is hidden by default and can be enabled manually under Tab Display in the settings drawer.
    /// </summary>
    public static class TabVisibilitySettings
    {
        private const string PrefKeyPrefix = "UnitySkills_TabVisible_";

        public static bool GetDefaultVisibility(string tabKey)
        {
            return !string.Equals(tabKey, "evaluation", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsTabVisible(string tabKey)
        {
            if (string.Equals(tabKey, "analytics", StringComparison.OrdinalIgnoreCase) && !SkillTelemetryService.Enabled)
                return false;

            return EditorPrefs.GetBool(PrefKeyPrefix + tabKey, GetDefaultVisibility(tabKey));
        }

        public static bool GetUserPreference(string tabKey)
        {
            return EditorPrefs.GetBool(PrefKeyPrefix + tabKey, GetDefaultVisibility(tabKey));
        }

        public static void SetUserPreference(string tabKey, bool visible)
        {
            EditorPrefs.SetBool(PrefKeyPrefix + tabKey, visible);
            OnChanged?.Invoke();
        }

        public static event Action OnChanged;

        public static void NotifyChanged()
        {
            OnChanged?.Invoke();
        }
    }
}

// Producer:Betsy
