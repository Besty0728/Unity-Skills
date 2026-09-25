using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Snapshots every preference <see cref="SkillsModeManager.ResetForTests"/> deletes, and puts all of them back.
    /// A fixture that saved only the mode keys ended a test run with the user's allowlist deleted: ResetForTests
    /// removes it, and <see cref="SkillsModeManager.CompleteTestPreferenceRecovery"/> only clears the domain-reload
    /// recovery data. Capture in [OneTimeSetUp], restore in [OneTimeTearDown].
    /// </summary>
    internal sealed class ModePreferenceSnapshot
    {
        private static readonly string[] StringKeys =
        {
            "UnitySkills_OperatingMode", "UnitySkills_AllowlistSkills", "UnitySkills_GrantedSkills",
        };

        private static readonly string[] BoolKeys =
        {
            "UnitySkills_PanelApprovalRequired", "UnitySkills_AllowlistMigratedFromGranted",
        };

        private readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> _bools = new Dictionary<string, bool>();

        public static ModePreferenceSnapshot Capture()
        {
            var snapshot = new ModePreferenceSnapshot();
            foreach (var key in StringKeys)
            {
                if (EditorPrefs.HasKey(key))
                    snapshot._strings[key] = EditorPrefs.GetString(key);
            }

            foreach (var key in BoolKeys)
            {
                if (EditorPrefs.HasKey(key))
                    snapshot._bools[key] = EditorPrefs.GetBool(key);
            }

            return snapshot;
        }

        public void Restore()
        {
            foreach (var key in StringKeys)
            {
                if (_strings.TryGetValue(key, out var value)) EditorPrefs.SetString(key, value);
                else EditorPrefs.DeleteKey(key);
            }

            foreach (var key in BoolKeys)
            {
                if (_bools.TryGetValue(key, out var value)) EditorPrefs.SetBool(key, value);
                else EditorPrefs.DeleteKey(key);
            }

            // The in-memory allowlist was replaced by ResetForTests; drop it so the next read loads the restored pref.
            typeof(SkillsModeManager).GetField("_allowlist", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
            SkillsModeManager.CompleteTestPreferenceRecovery();
        }
    }
}

// Producer:Betsy
