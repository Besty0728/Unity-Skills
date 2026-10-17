using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// "Switch to Bypass, restore after the session" (contract 7.5). The operating mode is a machine-wide EditorPrefs
    /// value, so the restore key records which project armed it and, once seen, which session it waits for; the mode
    /// is put back only while it is still the Bypass the evaluation set, and a key whose mode the user changed by hand
    /// is just dropped. Checked from the Evaluation tab (creation, activation, finished commands), never from an
    /// InitializeOnLoad scan, so a normal editor start stays silent.
    /// </summary>
    internal static class EvaluationModeGuard
    {
        internal const string PrefKey = "UnitySkills_EvalRestoreMode";

        internal enum ArmResult { Armed, AlreadyBypass }

        internal enum CheckResult
        {
            /// <summary>No key, or it belongs to another project.</summary>
            None,
            /// <summary>The key is kept: a session is active, or the bound session has not ended.</summary>
            Waiting,
            Restored,
            /// <summary>The user changed the mode meanwhile; only the key was removed.</summary>
            KeyCleared,
            /// <summary>The key was unreadable and has been removed.</summary>
            KeyDiscarded,
        }

        /// <summary>Where the key and the mode live; the real one is EditorPrefs + SkillsModeManager, tests pass a fake.</summary>
        internal interface IStore
        {
            string ReadKey();
            void WriteKey(string json);
            void DeleteKey();
            SkillsOperatingMode Mode { get; set; }
        }

        internal sealed class RestoreKey
        {
            public string PreviousMode;
            public string ArmedAt;
            public string ProjectPath;
            public string SessionId;
        }

        private sealed class EditorStore : IStore
        {
            public string ReadKey() => EditorPrefs.HasKey(PrefKey) ? EditorPrefs.GetString(PrefKey, string.Empty) : null;
            public void WriteKey(string json) => EditorPrefs.SetString(PrefKey, json);
            public void DeleteKey() => EditorPrefs.DeleteKey(PrefKey);

            public SkillsOperatingMode Mode
            {
                get => SkillsModeManager.CurrentMode;
                set => SkillsModeManager.CurrentMode = value;
            }
        }

        internal static readonly IStore Editor = new EditorStore();

        /// <summary>Writes the key first, then switches; nothing is written when the mode already is Bypass.</summary>
        internal static ArmResult Arm(IStore store, string projectRoot, DateTime nowUtc)
        {
            var mode = store.Mode;
            if (mode == SkillsOperatingMode.Bypass) return ArmResult.AlreadyBypass;
            store.WriteKey(Serialize(new RestoreKey
            {
                PreviousMode = mode.ToString().ToLowerInvariant(),
                ArmedAt = EvaluationState.FormatIso(nowUtc),
                ProjectPath = projectRoot,
                SessionId = null,
            }));
            store.Mode = SkillsOperatingMode.Bypass;
            return ArmResult.Armed;
        }

        /// <summary>The key when it exists, parses and belongs to <paramref name="projectRoot"/>; otherwise null.</summary>
        internal static RestoreKey PendingFor(IStore store, string projectRoot)
        {
            var key = Parse(store.ReadKey());
            return key != null && SameProject(key.ProjectPath, projectRoot) ? key : null;
        }

        /// <summary>
        /// Binds the key to a session started after arming, then restores when every condition of contract 7.5 holds:
        /// the key belongs to this project, no session is active, and the bound session ended (or the user asked).
        /// </summary>
        internal static CheckResult Check(IStore store, string projectRoot, bool userForced)
        {
            var json = store.ReadKey();
            if (json == null) return CheckResult.None;
            var key = Parse(json);
            if (key == null)
            {
                store.DeleteKey();
                return CheckResult.KeyDiscarded;
            }
            if (!SameProject(key.ProjectPath, projectRoot)) return CheckResult.None;

            var current = EvaluationState.ReadCurrent(projectRoot, out _);
            if (key.SessionId == null && current != null && IsCreatedSince(projectRoot, current.SessionId, key.ArmedAt))
            {
                key.SessionId = current.SessionId;
                store.WriteKey(Serialize(key));
            }

            if (current != null && current.Status == "active") return CheckResult.Waiting;
            if (!userForced && !SessionEnded(projectRoot, key.SessionId)) return CheckResult.Waiting;

            if (store.Mode != SkillsOperatingMode.Bypass)
            {
                store.DeleteKey();
                return CheckResult.KeyCleared;
            }
            store.Mode = ParseMode(key.PreviousMode);
            store.DeleteKey();
            return CheckResult.Restored;
        }

        internal static SkillsOperatingMode ParseMode(string wire)
        {
            return TryParseMode(wire, out var mode) ? mode : SkillsOperatingMode.Auto;
        }

        /// <summary>Accepts the enum names only (any case), not numeric strings that Enum.TryParse would also take.</summary>
        private static bool TryParseMode(string wire, out SkillsOperatingMode mode)
        {
            mode = SkillsOperatingMode.Auto;
            return !string.IsNullOrEmpty(wire) && char.IsLetter(wire[0]) &&
                   Enum.TryParse(wire, true, out mode) && Enum.IsDefined(typeof(SkillsOperatingMode), mode);
        }

        internal static string Serialize(RestoreKey key)
        {
            return new JObject
            {
                ["schema"] = 1,
                ["previousMode"] = key.PreviousMode,
                ["armedAt"] = key.ArmedAt,
                ["projectPath"] = key.ProjectPath,
                ["sessionId"] = key.SessionId,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static RestoreKey Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var value = EvaluationState.ParseObject(json);
                var key = new RestoreKey
                {
                    PreviousMode = value.Value<string>("previousMode"),
                    ArmedAt = value.Value<string>("armedAt"),
                    ProjectPath = value.Value<string>("projectPath"),
                    SessionId = value.Value<string>("sessionId"),
                };
                if (value.Value<long?>("schema") != 1 || string.IsNullOrEmpty(key.ProjectPath) ||
                    EvaluationState.ParseIso(key.ArmedAt) == null || !TryParseMode(key.PreviousMode, out _))
                    return null;
                if (key.SessionId != null && !EvaluationState.IsSessionId(key.SessionId)) return null;
                return key;
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is InvalidCastException)
            {
                return null;
            }
        }

        private static bool IsCreatedSince(string projectRoot, string sessionId, string armedAt)
        {
            var session = EvaluationState.ReadSession(projectRoot, sessionId, out _);
            var created = EvaluationState.ParseIso(session?.CreatedAt);
            var armed = EvaluationState.ParseIso(armedAt);
            return created.HasValue && armed.HasValue && created.Value >= armed.Value;
        }

        private static bool SessionEnded(string projectRoot, string sessionId)
        {
            if (sessionId == null) return false;
            var status = EvaluationState.ReadSession(projectRoot, sessionId, out _)?.Status;
            return status == "completed" || status == "aborted";
        }

        internal static bool SameProject(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(NormalizePath(a), NormalizePath(b),
                UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }

        private static string NormalizePath(string path)
        {
            try { path = Path.GetFullPath(path); }
            catch (Exception) { /* compare as given */ }
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }
}

// Producer:Betsy
