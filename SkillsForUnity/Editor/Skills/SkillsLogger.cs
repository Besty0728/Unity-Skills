using UnityEngine;
using UnityEditor;

namespace UnitySkills
{
    public enum LogLevel { Off = 0, Error = 1, Warning = 2, Info = 3, Verbose = 4 }

    public static class SkillsLogger
    {
        private const string PrefKey = "UnitySkills_LogLevel";
        private static LogLevel? _level;

        public static LogLevel Level
        {
            get => _level ??= (LogLevel)EditorPrefs.GetInt(PrefKey, (int)LogLevel.Info);
            set { _level = value; EditorPrefs.SetInt(PrefKey, (int)value); }
        }

        public static void Log(string msg)
        {
            if (Level >= LogLevel.Info) Debug.Log($"[UnitySkills] {msg}");
        }

        public static void LogVerbose(string msg)
        {
            if (Level >= LogLevel.Verbose) Debug.Log($"[UnitySkills] {msg}");
        }

        public static void LogWarning(string msg)
        {
            if (Level >= LogLevel.Warning) Debug.LogWarning($"[UnitySkills] {msg}");
        }

        public static void LogError(string msg)
        {
            if (Level >= LogLevel.Error) Debug.LogError($"[UnitySkills] {msg}");
        }
    }
}
