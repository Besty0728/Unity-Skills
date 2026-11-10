using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// How long this editor session's domain reloads take, from beforeAssemblyReload to the next domain's first update.
    /// Mono's generic-sharing tables outlive every reload and are searched linearly, so reloads keep slowing down over a
    /// long session (measured: 1.6 s at the 10th reload, 17 s at the 320th) until the editor restarts; the plugin cannot
    /// reset them. restartRecommended flags a session whose recent reloads are several times slower than its first ones.
    /// The samples live in SessionState, which survives a reload but not a restart.
    /// </summary>
    [InitializeOnLoad]
    internal static class DomainReloadStats
    {
        internal const string StateSessionKey = "UnitySkills_DomainReloadStats";
        internal const string StartedSessionKey = "UnitySkills_DomainReloadStartedTicks";
        internal const int BaselineSamples = 5;
        internal const int RecentSamples = 3;
        internal const int MinReloadsForAdvice = 10;
        internal const double SlowFactor = 3;
        internal const long SlowFloorMs = 10000;
        private const long MaxPlausibleMs = 60L * 60L * 1000L;

        internal sealed class State
        {
            public long count;
            public List<long> first = new List<long>();
            public List<long> recent = new List<long>();
        }

        internal sealed class Snapshot
        {
            public long count;
            public long lastMs;
            public long recentMs;
            public long baselineMs;
            public bool restartRecommended;
        }

        private static State _state = new State();
        // Read by the HTTP thread's /health path; replaced whole on the main thread.
        private static volatile Snapshot _current = new Snapshot();

        internal static Snapshot Current => _current;

        static DomainReloadStats()
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

            try
            {
                _state = ParseState(SessionState.GetString(StateSessionKey, string.Empty));
                _current = Evaluate(_state);
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                if (SessionState.GetString(StartedSessionKey, string.Empty).Length > 0)
                    EditorApplication.update += OnFirstUpdate;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Domain reload timing is unavailable: {ex.Message}");
            }
        }

        private static void OnBeforeAssemblyReload() =>
            SessionState.SetString(StartedSessionKey, DateTime.UtcNow.Ticks.ToString());

        private static void OnFirstUpdate()
        {
            EditorApplication.update -= OnFirstUpdate;
            try
            {
                bool started = long.TryParse(SessionState.GetString(StartedSessionKey, string.Empty), out long startedTicks);
                SessionState.EraseString(StartedSessionKey);
                long ms = (DateTime.UtcNow.Ticks - startedTicks) / TimeSpan.TicksPerMillisecond;
                if (!started || ms < 0 || ms > MaxPlausibleMs) return;
                _state = Record(_state, ms);
                SessionState.SetString(StateSessionKey, JsonConvert.SerializeObject(_state));
                _current = Evaluate(_state);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Domain reload timing could not be recorded: {ex.Message}");
            }
        }

        /// <summary>The state after one more reload of <paramref name="ms"/>; the input is not changed.</summary>
        internal static State Record(State state, long ms)
        {
            var next = new State { count = state.count + 1, first = new List<long>(state.first), recent = new List<long>(state.recent) };
            if (next.first.Count < BaselineSamples) next.first.Add(ms);
            next.recent.Add(ms);
            if (next.recent.Count > RecentSamples) next.recent.RemoveRange(0, next.recent.Count - RecentSamples);
            return next;
        }

        internal static Snapshot Evaluate(State state)
        {
            long baseline = Median(state.first), recent = Median(state.recent);
            return new Snapshot
            {
                count = state.count,
                lastMs = state.recent.Count > 0 ? state.recent[state.recent.Count - 1] : 0,
                recentMs = recent,
                baselineMs = baseline,
                restartRecommended = state.count >= MinReloadsForAdvice && state.recent.Count >= RecentSamples && baseline > 0 &&
                                     recent >= Math.Max((long)(SlowFactor * baseline), SlowFloorMs)
            };
        }

        internal static State ParseState(string json)
        {
            if (string.IsNullOrEmpty(json)) return new State();
            try
            {
                var state = JsonConvert.DeserializeObject<State>(json);
                if (state == null || state.count < 0) return new State();
                state.first = state.first ?? new List<long>();
                state.recent = state.recent ?? new List<long>();
                return state;
            }
            catch (JsonException)
            {
                return new State();
            }
        }

        private static long Median(List<long> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}

// Producer:Betsy
