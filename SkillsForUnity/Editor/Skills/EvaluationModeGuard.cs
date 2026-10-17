using System;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// "Switch to Bypass, restore after the session" (contract 7.5). The operating mode is a per-project setting, and
    /// the restore key lives next to it in the same project's settings (<see cref="ProjectPrefs"/>), so no other project
    /// can see or act on it; once seen, the key records which session it waits for. The mode is put back only while it
    /// is still the Bypass the evaluation set, and a key whose mode the user changed by hand is just dropped. Checked
    /// from the Evaluation tab (creation, activation, finished commands), never from an InitializeOnLoad scan, so a
    /// normal editor start stays silent.
    /// </summary>
    internal static class EvaluationModeGuard
    {
        internal const string PrefKey = "UnitySkills_EvalRestoreMode";

        internal enum ArmResult { Armed, AlreadyBypass }

        internal enum CheckResult
        {
            /// <summary>No key.</summary>
            None,
            /// <summary>The key is kept: a session is active, or the bound session has not ended.</summary>
            Waiting,
            Restored,
            /// <summary>The user changed the mode meanwhile; only the key was removed.</summary>
            KeyCleared,
            /// <summary>The key was unreadable and has been removed.</summary>
            KeyDiscarded,
        }

        /// <summary>Where the key and the mode live; the real one is ProjectPrefs + SkillsModeManager, tests pass a fake.</summary>
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
            public string SessionId;
        }

        private sealed class EditorStore : IStore
        {
            // The key never existed as a machine-wide pref in a release, so it has no legacy fallback in ProjectPrefs.
            public string ReadKey() => ProjectPrefs.HasKey(PrefKey) ? ProjectPrefs.GetString(PrefKey, string.Empty) : null;
            public void WriteKey(string json) => ProjectPrefs.SetString(PrefKey, json);
            public void DeleteKey() => ProjectPrefs.DeleteKey(PrefKey);

            public SkillsOperatingMode Mode
            {
                get => SkillsModeManager.CurrentMode;
                set => SkillsModeManager.CurrentMode = value;
            }
        }

        internal static readonly IStore Editor = new EditorStore();

        /// <summary>Writes the key first, then switches; nothing is written when the mode already is Bypass.</summary>
        internal static ArmResult Arm(IStore store, DateTime nowUtc)
        {
            var mode = store.Mode;
            if (mode == SkillsOperatingMode.Bypass) return ArmResult.AlreadyBypass;
            store.WriteKey(Serialize(new RestoreKey
            {
                PreviousMode = mode.ToString().ToLowerInvariant(),
                ArmedAt = EvaluationState.FormatIso(nowUtc),
                SessionId = null,
            }));
            store.Mode = SkillsOperatingMode.Bypass;
            return ArmResult.Armed;
        }

        /// <summary>The key when it exists and parses; otherwise null.</summary>
        internal static RestoreKey Pending(IStore store) => Parse(store.ReadKey());

        /// <summary>
        /// Binds the key to a session started after arming, then restores when every condition of contract 7.5 holds:
        /// no session is active, and the bound session ended (or the user asked). <paramref name="projectRoot"/> locates
        /// the runner state the key is checked against.
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
                    SessionId = value.Value<string>("sessionId"),
                };
                if (value.Value<long?>("schema") != 1 ||
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
    }
}

// Producer:Betsy
