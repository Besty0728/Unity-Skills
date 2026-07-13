using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// In-memory event channel backing GET /events long-polling: editor-side sources publish
    /// events, HTTP-side waiters read them — turning the REST API from pull-only into
    /// "Unity can push".
    ///
    /// Threading contract (mirrors SkillsHttpServer's producer-consumer split):
    /// - Publish and all event-source callbacks run on the MAIN THREAD only (they serialize
    ///   the payload, append to the ring buffer, persist the seq via SessionState and set the
    ///   wakeup signal).
    /// - TryReadEventsAfter / GetCurrentSeq / ResetSignal / WaitSignal are safe on ThreadPool
    ///   threads: zero Unity API, zero SessionState — buffer access is guarded by a lock whose
    ///   critical section only appends/copies list entries (payload serialization happens
    ///   outside it).
    /// - Long-poll correctness relies on waiters re-scanning the buffer every 250ms; the
    ///   signal only reduces latency, so its multi-consumer Reset race is harmless.
    ///
    /// Persistence: only the seq counter survives a domain reload (SessionState), so cursors
    /// never move backwards; buffered events are lost with the old domain — clients detect the
    /// gap via oldestSeq/dropped and learn the compile outcome from server_restored.
    /// </summary>
    [InitializeOnLoad]
    public static class EventChannelService
    {
        private const int BufferCapacity = 500;
        private const string SessionKeySeq = "UnitySkills_EventChannelSeq";
        private const int MaxConsoleErrorsPerSecond = 20;
        private const int MaxConsoleMessageChars = 500;
        private const int MaxConsoleStackTraceLines = 3;

        private struct BufferedEvent
        {
            public long Seq;
            public string TypeName;
            public string ReadyJson;
        }

        // Ring buffer + seq counter, shared between the main thread (Publish) and ThreadPool
        // waiters (TryReadEventsAfter/GetCurrentSeq) — every access goes through _bufferLock.
        private static readonly object _bufferLock = new object();
        private static readonly Queue<BufferedEvent> _buffer = new Queue<BufferedEvent>(BufferCapacity + 1);
        private static long _seq;

        // Set by Publish (main thread), Reset/Wait by long-poll waiters (ThreadPool).
        private static readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);

        // console_error throttle state — main thread only (logMessageReceived, non-Threaded).
        private static long _consoleWindowStartTicks;
        private static int _consoleErrorsThisWindow;
        private static long _consoleDroppedSinceLast;
        // Guards against Publish-failure logging re-entering OnLogMessageReceived.
        private static bool _publishingConsoleError;

        static EventChannelService()
        {
            try
            {
                // Restore the seq counter so cursors held by clients across a domain reload
                // never observe a seq going backwards. C# guarantees this runs before the
                // first Publish (static constructor precedes any static member access).
                long.TryParse(SessionState.GetString(SessionKeySeq, "0"), out _seq);

                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
                Application.logMessageReceived += OnLogMessageReceived;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError("EventChannelService init failed: " + ex);
            }
        }

        /// <summary>
        /// Publishes one event to the channel. MAIN THREAD ONLY (serializes the payload,
        /// touches SessionState). <paramref name="type"/> must be a plain identifier
        /// (snake_case, no quotes/escapes) — it is embedded into JSON without escaping.
        /// </summary>
        public static void Publish(string type, object payload)
        {
            try
            {
                string payloadJson = payload == null
                    ? "{}"
                    : JsonConvert.SerializeObject(payload, SkillsCommon.JsonSettings);
                string tsUtc = DateTime.UtcNow.ToString("o");

                long seq;
                lock (_bufferLock)
                {
                    // seq assignment stays inside the lock so readers never observe a seq
                    // without its event in the buffer (the concat here is trivial; the
                    // expensive JsonConvert call is done above, outside the lock).
                    seq = ++_seq;
                    _buffer.Enqueue(new BufferedEvent
                    {
                        Seq = seq,
                        TypeName = type,
                        ReadyJson = string.Concat(
                            "{\"seq\":", seq.ToString(),
                            ",\"type\":\"", type,
                            "\",\"tsUtc\":\"", tsUtc,
                            "\",\"payload\":", payloadJson, "}"),
                    });
                    while (_buffer.Count > BufferCapacity)
                        _buffer.Dequeue();
                }

                _signal.Set();
                SessionState.SetString(SessionKeySeq, seq.ToString());
            }
            catch (Exception ex)
            {
                // LogWarning, never LogError: an Error here would re-enter the console_error
                // source (logMessageReceived) and risk recursion.
                SkillsLogger.LogWarning($"EventChannel publish failed for '{type}': {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes server_restored with a summary of the last compilation result. The
        /// compilation_finished event for a successful compile is published in the OLD domain
        /// and dies with the in-memory buffer on reload — a reconnecting client learns the
        /// compile outcome from this event instead. MAIN THREAD ONLY.
        /// </summary>
        internal static void PublishServerRestored(int port)
        {
            object lastCompilation = null;
            try
            {
                string json = CompilationResultService.GetLastCompilationJson();
                if (!string.IsNullOrEmpty(json))
                {
                    // DateParseHandling.None keeps finishedAtUtc as the raw ISO-8601 string
                    // (plain JObject.Parse would coerce it into a localized Date.ToString()).
                    JObject parsed;
                    using (var reader = new JsonTextReader(new System.IO.StringReader(json))
                           { DateParseHandling = DateParseHandling.None })
                    {
                        parsed = JObject.Load(reader);
                    }
                    lastCompilation = new
                    {
                        success = parsed["success"]?.ToObject<bool?>(),
                        errorCount = parsed["errorCount"]?.ToObject<int?>() ?? 0,
                        finishedAtUtc = parsed["finishedAtUtc"]?.ToString(),
                    };
                }
            }
            catch { /* summary is best-effort; the event itself must still go out */ }

            Publish("server_restored", new { port, lastCompilation });
        }

        /// <summary>
        /// Copies the ready-made JSON of every buffered event with seq &gt; <paramref name="since"/>
        /// (optionally filtered by type) into <paramref name="jsons"/>. Returns true when at
        /// least one event matched. SAFE OFF THE MAIN THREAD — zero Unity API.
        /// <paramref name="cursor"/> is the current max seq (scan upper bound: pass it back as
        /// the next 'since' even when type filtering skipped events). <paramref name="oldestSeq"/>
        /// is the seq of the oldest buffered event, or max+1 when the buffer is empty
        /// ("nothing older is available").
        /// </summary>
        public static bool TryReadEventsAfter(long since, string[] typeFilter,
            out List<string> jsons, out long cursor, out long oldestSeq)
        {
            jsons = new List<string>();
            lock (_bufferLock)
            {
                cursor = _seq;
                oldestSeq = _seq + 1;
                bool first = true;
                foreach (var e in _buffer)
                {
                    if (first)
                    {
                        oldestSeq = e.Seq;
                        first = false;
                    }
                    if (e.Seq <= since)
                        continue;
                    if (typeFilter != null && !MatchesTypeFilter(e.TypeName, typeFilter))
                        continue;
                    jsons.Add(e.ReadyJson);
                }
            }
            return jsons.Count > 0;
        }

        /// <summary>Current max seq — used as the default 'since' ("wait for new events only"). Thread-safe.</summary>
        public static long GetCurrentSeq()
        {
            lock (_bufferLock)
                return _seq;
        }

        /// <summary>
        /// Resets the wakeup signal. Call BEFORE scanning the buffer so a publish landing
        /// after the scan sets it again; races with other waiters are harmless because every
        /// waiter re-scans on a 250ms interval regardless. Thread-safe.
        /// </summary>
        public static void ResetSignal() => _signal.Reset();

        /// <summary>Blocks until a publish or the timeout, whichever first. Thread-safe.</summary>
        public static bool WaitSignal(int millisecondsTimeout) => _signal.Wait(millisecondsTimeout);

        private static bool MatchesTypeFilter(string typeName, string[] typeFilter)
        {
            foreach (var t in typeFilter)
            {
                if (string.Equals(typeName, t, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ===== Event sources (all callbacks arrive on the main thread) =====

        private static void OnBeforeAssemblyReload()
        {
            // Best-effort: the buffer dies with this domain moments later, but a waiter
            // already blocked in a long poll can still be signalled and deliver it.
            Publish("before_domain_reload", new { reason = "assembly_reload" });
        }

        private static void OnAfterAssemblyReload()
        {
            Publish("after_domain_reload", new { reason = "assembly_reload" });
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            Publish("playmode_changed", new { state = state.ToString() });
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;
            if (_publishingConsoleError)
                return;

            long nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - _consoleWindowStartTicks >= TimeSpan.TicksPerSecond)
            {
                _consoleWindowStartTicks = nowTicks;
                _consoleErrorsThisWindow = 0;
            }

            if (_consoleErrorsThisWindow >= MaxConsoleErrorsPerSecond)
            {
                _consoleDroppedSinceLast++;
                return;
            }
            _consoleErrorsThisWindow++;

            long dropped = _consoleDroppedSinceLast;
            _consoleDroppedSinceLast = 0;

            _publishingConsoleError = true;
            try
            {
                PlayCaptureService.RecordRuntimeError(message, stackTrace, type);
                Publish("console_error", new
                {
                    logType = type.ToString(),
                    message = Truncate(message, MaxConsoleMessageChars),
                    stackTrace = FirstLines(stackTrace, MaxConsoleStackTraceLines),
                    droppedSinceLast = dropped,
                });
            }
            finally
            {
                _publishingConsoleError = false;
            }
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars)
                return s;
            return s.Substring(0, maxChars);
        }

        private static string FirstLines(string s, int maxLines)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            int idx = -1;
            for (int i = 0; i < maxLines; i++)
            {
                idx = s.IndexOf('\n', idx + 1);
                if (idx < 0)
                    return s;
            }
            return s.Substring(0, idx);
        }
    }
}

// Producer:Betsy
