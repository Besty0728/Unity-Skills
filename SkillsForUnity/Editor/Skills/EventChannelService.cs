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
    /// In-memory event channel backing the GET /events long-poll: editor-side event sources publish,
    /// HTTP-side waiters read, turning the REST API from pure-pull into "Unity can also push".
    ///
    /// Threading contract (consistent with SkillsHttpServer's producer-consumer split):
    /// - Publish and all event-source callbacks run only on the main thread (serializing the payload,
    ///   appending to the ring buffer, persisting seq via SessionState, and setting the wake signal).
    /// - TryReadEventsAfter / GetCurrentSeq / ResetSignal / WaitSignal are safe to call on thread-pool
    ///   threads: they touch no Unity API and no SessionState; buffer access is protected by a lock,
    ///   and the critical section only does list append/copy (payload serialization happens outside the lock).
    /// - Long-poll correctness relies on each waiter re-scanning the buffer every 250ms; the signal
    ///   only reduces latency, so a Reset race under multiple consumers is harmless.
    ///
    /// Persistence: only the seq counter survives a domain reload (via SessionState), guaranteeing the
    /// cursor never goes backwards; events in the buffer are lost along with the old domain -- clients
    /// detect the gap via oldestSeq/dropped and learn the compile result from the server_restored event.
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

        // The ring buffer and seq counter are shared between the main thread (Publish) and thread-pool
        // waiters (TryReadEventsAfter/GetCurrentSeq); every access must go through _bufferLock.
        private static readonly object _bufferLock = new object();
        private static readonly Queue<BufferedEvent> _buffer = new Queue<BufferedEvent>(BufferCapacity + 1);
        private static long _seq;

        // Set by Publish (main thread); Reset / Wait by long-poll waiters (thread pool).
        private static readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);

        // Rate-limit state for console_error, main-thread-only access (non-Threaded logMessageReceived).
        private static long _consoleWindowStartTicks;
        private static int _consoleErrorsThisWindow;
        private static long _consoleDroppedSinceLast;
        // Prevents a log emitted by a failed Publish from re-entering OnLogMessageReceived and recursing.
        private static bool _publishingConsoleError;

        static EventChannelService()
        {
            try
            {
                // Restores the seq counter so a client's cursor across a domain reload never sees seq go
                // backwards. C# guarantees the static constructor runs before any static member access, so this always happens before the first Publish.
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
        /// Publishes an event to the channel. Main thread only (needs to serialize the payload and
        /// touch SessionState). <paramref name="type"/> must be a plain identifier (snake_case, no
        /// quotes or escaping) -- it gets embedded directly into the JSON with no escaping.
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
                    // seq assignment must stay inside the lock, or a reader could see a seq whose matching
                    // event isn't in the buffer yet. The string concatenation here is cheap; the expensive JsonConvert call already ran outside the lock.
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
                // Must use LogWarning, never LogError: an Error would re-enter the console_error
                // event source (logMessageReceived), risking recursion.
                SkillsLogger.LogWarning($"EventChannel publish failed for '{type}': {ex.Message}");
            }
        }

        /// <summary>
        /// Publishes server_restored, with a summary of the last compile result attached. The
        /// compilation_finished event for a successful compile was published in the old domain and disappears
        /// along with the in-memory buffer on reload -- reconnecting clients instead learn the compile result from this event. Main thread only.
        /// </summary>
        internal static void PublishServerRestored(int port)
        {
            object lastCompilation = null;
            try
            {
                string json = CompilationResultService.GetLastCompilationJson();
                if (!string.IsNullOrEmpty(json))
                {
                    // DateParseHandling.None preserves finishedAtUtc's original ISO-8601 string;
                    // calling JObject.Parse directly would force it into a localized Date.ToString().
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
        /// Copies the ready-made JSON for buffered events with seq &gt; <paramref name="since"/>
        /// (optionally filtered by type) into <paramref name="jsons"/>, returning true if at least one
        /// matched. Safe to call off the main thread -- touches no Unity API.
        /// <paramref name="cursor"/> is the current max seq (the scan's upper bound: even if type
        /// filtering skipped events, it should still be passed back as the next call's since).
        /// <paramref name="oldestSeq"/> is the seq of the oldest event in the buffer; when empty this is max+1, meaning "nothing older is available".
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

        /// <summary>Current max seq, used as the default since (i.e. "only wait for new events"). Thread-safe.</summary>
        public static long GetCurrentSeq()
        {
            lock (_bufferLock)
                return _seq;
        }

        /// <summary>
        /// Resets the wake signal. Must be called before scanning the buffer, so a publish that arrives
        /// after the scan can re-set it; harmless to race with other waiters since every waiter re-scans every 250ms regardless. Thread-safe.
        /// </summary>
        public static void ResetSignal() => _signal.Reset();

        /// <summary>Blocks until an event is published or the timeout elapses, whichever comes first. Thread-safe.</summary>
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
            // Best-effort: the buffer disappears along with this domain almost immediately, but a
            // waiter already blocked in the long-poll may still be woken and get this event sent out.
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
