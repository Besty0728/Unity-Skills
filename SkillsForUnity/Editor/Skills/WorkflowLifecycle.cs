using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// The editor's busy spans (compiles and domain reloads), kept for the idle gap so that waiting on the editor is not
    /// counted as an agent's idle time. A span that is open across a domain reload is closed by the next domain's first load.
    /// The spans live in SessionState, which survives a domain reload but not an editor restart.
    /// </summary>
    [InitializeOnLoad]
    internal static class WorkflowLifecycle
    {
        internal const string SpansSessionKey = "UnitySkills_WorkflowBusySpans";
        internal const string OpenSinceSessionKey = "UnitySkills_WorkflowBusyOpenSince";
        internal const int MaxSpans = 32;

        private static readonly List<(long start, long end)> Spans = new List<(long start, long end)>();
        private static long _openSinceMs;

        // Set by ReplaceSpansForTests: the state lives in memory only, so tests never change the editor's real spans.
        private static bool _detached;

        static WorkflowLifecycle()
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

            try
            {
                LoadFromSessionState();
                CloseOpenSpan();
                SaveToSessionState();
                CompilationPipeline.compilationStarted += OnBusyStarted;
                CompilationPipeline.compilationFinished += OnBusyFinished;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow busy spans are unavailable: {ex.Message}");
            }
        }

        /// <summary>The recent busy spans as [start, end] in Unix ms; an open span counts as busy until now.</summary>
        internal static IReadOnlyList<(long start, long end)> BusySpans
        {
            get
            {
                var spans = new List<(long start, long end)>(Spans);
                if (_openSinceMs > 0)
                    spans.Add((_openSinceMs, NowUnixMs()));
                return spans;
            }
        }

        private static void OnBusyStarted(object context)
        {
            if (_openSinceMs == 0)
                _openSinceMs = NowUnixMs();
            SaveToSessionState();
        }

        private static void OnBusyFinished(object context)
        {
            CloseOpenSpan();
            SaveToSessionState();
        }

        private static void OnBeforeAssemblyReload()
        {
            // The reload itself is busy time: it stays open until the next domain closes it.
            if (_openSinceMs == 0)
                _openSinceMs = NowUnixMs();
            SaveToSessionState();
        }

        private static void OnAfterAssemblyReload()
        {
            CloseOpenSpan();
            SaveToSessionState();
        }

        private static void CloseOpenSpan()
        {
            if (_openSinceMs == 0)
                return;

            long end = NowUnixMs();
            if (end >= _openSinceMs)
                Spans.Add((_openSinceMs, end));
            if (Spans.Count > MaxSpans)
                Spans.RemoveRange(0, Spans.Count - MaxSpans);
            _openSinceMs = 0;
        }

        private static void LoadFromSessionState()
        {
            Spans.Clear();
            _openSinceMs = 0;
            try
            {
                var stored = JsonConvert.DeserializeObject<List<long[]>>(SessionState.GetString(SpansSessionKey, string.Empty));
                if (stored != null)
                {
                    foreach (var span in stored.Where(s => s != null && s.Length == 2))
                        Spans.Add((span[0], span[1]));
                }
                long.TryParse(SessionState.GetString(OpenSinceSessionKey, string.Empty), out _openSinceMs);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow busy spans could not be restored: {ex.Message}");
            }
        }

        private static void SaveToSessionState()
        {
            if (_detached)
                return;
            WriteSessionState();
        }

        private static void WriteSessionState()
        {
            try
            {
                SessionState.SetString(SpansSessionKey,
                    JsonConvert.SerializeObject(Spans.Select(span => new[] { span.start, span.end }).ToList()));
                SessionState.SetString(OpenSinceSessionKey, _openSinceMs.ToString());
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"Workflow busy spans could not be saved: {ex.Message}");
            }
        }

        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Test seam: replaces the spans and the open span's start in memory, and writes nothing to SessionState until <see cref="ResetForTests"/>.</summary>
        internal static void ReplaceSpansForTests(IEnumerable<(long start, long end)> spans, long openSinceMs = 0)
        {
            _detached = true;
            Spans.Clear();
            Spans.AddRange(spans);
            _openSinceMs = openSinceMs;
        }

        /// <summary>Test seam: writes the current state to SessionState, which the editor and a reload read back.</summary>
        internal static void PersistForTests() => WriteSessionState();

        /// <summary>Test seam: forgets the in-memory state and reads it back from SessionState, as a domain reload does.</summary>
        internal static void ResetForTests()
        {
            _detached = false;
            LoadFromSessionState();
        }
    }
}

// Producer:Betsy
