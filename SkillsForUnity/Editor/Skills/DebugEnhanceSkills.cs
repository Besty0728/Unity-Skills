using UnityEngine;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Debug Enhancement Skills - Console control, error handling.
    /// </summary>
    public static class DebugEnhanceSkills
    {
        [UnitySkill("debug_log", "Write a message to the Unity console")]
        public static object DebugLog(string message, string type = "Log")
        {
            switch (type.ToLower())
            {
                case "warning":
                    Debug.LogWarning($"[UnitySkills] {message}");
                    break;
                case "error":
                    Debug.LogError($"[UnitySkills] {message}");
                    break;
                default:
                    Debug.Log($"[UnitySkills] {message}");
                    break;
            }
            return new { success = true, type, message };
        }

        [UnitySkill("editor_set_pause_on_error", "Enable or disable 'Error Pause' in Play mode")]
        public static object EditorSetPauseOnError(bool enabled = true)
        {
            // Access Console window flags via reflection
            var consoleType = System.Type.GetType("UnityEditor.ConsoleWindow, UnityEditor");
            if (consoleType == null)
                return new { success = false, error = "ConsoleWindow not found" };

            var flagField = consoleType.GetField("s_ConsoleFlags", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (flagField == null)
            {
                // Alternative: EditorPrefs
                EditorPrefs.SetBool("DeveloperMode_ErrorPause", enabled);
                return new { success = true, enabled, note = "Set via EditorPrefs" };
            }

            // Flag 256 = ErrorPause
            int flags = (int)flagField.GetValue(null);
            if (enabled)
                flags |= 256;
            else
                flags &= ~256;
            flagField.SetValue(null, flags);

            return new { success = true, enabled };
        }
    }
}
