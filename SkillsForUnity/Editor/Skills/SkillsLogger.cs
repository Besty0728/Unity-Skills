using UnityEngine;
using UnityEditor;

namespace UnitySkills
{
    public enum LogLevel { Off = 0, Error = 1, Warning = 2, Info = 3, Agent = 4, Verbose = 5 }

    public static class SkillsLogger
    {
        private const string PrefKey = "UnitySkills_LogLevel";
        private static LogLevel? _level;

        /// <summary>
        /// Centralized version constant. Update this when releasing a new version.
        /// Referenced by SkillsHttpServer (/health), SkillRouter (/skills manifest), etc.
        /// </summary>
        public const string Version = "1.5.3";

        // Centralized log prefix constants with colors (Unity rich text)
        public const string PREFIX_INFO    = "<color=#4A9EFF>[UnitySkills]</color>";
        public const string PREFIX_SUCCESS = "<color=#5EE05E>[UnitySkills]</color>";
        public const string PREFIX_WARNING = "<color=#FFB347>[UnitySkills]</color>";
        public const string PREFIX_ERROR   = "<color=#FF6B6B>[UnitySkills]</color>";
        public const string PREFIX_SERVER  = "<color=#9B7EFF>[UnitySkills]</color>";
        public const string PREFIX_SKILL   = "<color=#5EC8E0>[UnitySkills]</color>";
        public const string PREFIX_WORKFLOW = "<color=#E091FF>[UnitySkills]</color>";

        public static LogLevel Level
        {
            get => _level ??= (LogLevel)EditorPrefs.GetInt(PrefKey, (int)LogLevel.Info);
            set { _level = value; EditorPrefs.SetInt(PrefKey, (int)value); }
        }

        public static void Log(string msg)
        {
            if (Level >= LogLevel.Info) Debug.Log($"[UnitySkills] {msg}");
        }

        public static void LogAgent(string agentId, string skillName)
        {
            if (Level >= LogLevel.Agent) Debug.Log($"[UnitySkills] <color=#5E9EE0>{agentId}</color> → {skillName}");
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

        /// <summary>
        /// Log a workflow-related message with the workflow prefix.
        /// </summary>
        public static void LogWorkflow(string msg)
        {
            if (Level >= LogLevel.Info) Debug.Log($"{PREFIX_WORKFLOW} {msg}");
        }

        /// <summary>
        /// Log a server-related message with the server prefix.
        /// </summary>
        public static void LogServer(string msg)
        {
            if (Level >= LogLevel.Info) Debug.Log($"{PREFIX_SERVER} {msg}");
        }

        /// <summary>
        /// Log a success message with the success prefix.
        /// </summary>
        public static void LogSuccess(string msg)
        {
            if (Level >= LogLevel.Info) Debug.Log($"{PREFIX_SUCCESS} {msg}");
        }
    }
}
