using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnitySkills.Internal;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Append-only JSONL log of skill "execution" telemetry — the data source for GET /analytics
    /// ("how many times was a skill called, how slow, how high a failure rate").
    ///
    /// Deliberately separate from <see cref="SkillsAuditLog"/>: that one records permission
    /// events (authorized/denied/allowlist); this one records the outcome of every skill call.
    /// The two land in different files (this one is <c>Library/UnitySkillsTelemetry.jsonl</c>),
    /// so the high-frequency execution stream doesn't dilute the permission audit trail.
    ///
    /// Structure mirrors SkillsAuditLog: writes are queued on the calling thread (main thread)
    /// and flushed to disk asynchronously; the file rotates at 1MB, keeping up to 3 historical
    /// copies. All disk I/O is best-effort — a telemetry failure must never affect the business response.
    ///
    /// One JSONL line per call:
    /// <code>{"ts":"2026-07-09T...Z","skill":"gameobject_create","agent":"ClaudeCode",
    /// "mode":"execute","ok":true,"ms":12}</code>
    /// (<c>errorCode</c> only appears when <c>ok</c> is false; so do the optional <c>err</c> and <c>target</c>
    /// fields, which carry a path-redacted error message and argument summary.)
    /// </summary>
    public static class SkillTelemetryService
    {
        private const string LogFileName = "UnitySkillsTelemetry.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const string PrefEnabled = "UnitySkills_TelemetryEnabled";

        // Delaying the flush by this much lets ClientProcessResolver's background worker (8ms coalesce + a
        // ~30-60ms lsof/proc/syscall round) land its result before the agent field is finally decided at
        // flush time -- see PendingRecord/BuildLine below. Most skills finish in single-digit ms, so binding
        // the agent at *enqueue* time (the old behavior) almost always raced the resolver and lost.
        private const int FlushDelayMs = 200;

        private static readonly ConcurrentQueue<PendingRecord> _queue = new ConcurrentQueue<PendingRecord>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked guard
        private static string _cachedDir;
        private static string _cachedPath;

        /// <summary>
        /// Everything needed to build a JSONL line, minus the final agent identity -- that's resolved from
        /// <see cref="ClientProcessResolver"/> at flush time (see <see cref="BuildLine"/>), not at enqueue
        /// time, since flush happens well after the resolver has had a chance to finish.
        /// </summary>
        internal sealed class PendingRecord
        {
            public string Ts;
            public string Skill;
            public string FallbackAgentId;
            public int RemotePort;
            public bool AgentIdIsExplicit;
            public string Mode;
            public bool Ok;
            public string ErrorCode;
            public string Error;
            public string Target;
            public long DurationMs;
            public long? WorkflowMs;
        }

        private const int MaxErrorChars = 160;
        private const int MaxTargetChars = 120;

        // Aggregation cache: caches the serialized /analytics JSON per window for 30 seconds, so
        // continuous polling doesn't reread up to 4MB from disk on every request.
        // Read/written only on the main thread (endpoint handler), but locked conservatively anyway.
        private const long AnalyticsCacheTtlTicks = 30L * TimeSpan.TicksPerSecond;
        private static readonly object _analyticsCacheLock = new object();
        private static readonly Dictionary<string, CachedAnalytics> _analyticsCache =
            new Dictionary<string, CachedAnalytics>(StringComparer.Ordinal);

        private struct CachedAnalytics
        {
            public string Json;
            public long AtTicks;
        }

        internal sealed class RecommendationHealth
        {
            public int Calls;
            public int Errors;
            public long AvgMs;
            public double ErrorRate;
            public int Penalty;
            public string[] Warnings = Array.Empty<string>();
        }

        private static readonly HashSet<string> RecommendationIgnoredErrorCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "UNKNOWN_SKILL", "UNKNOWN_PARAM", "MISSING_PARAM", "TYPE_MISMATCH",
                "INVALID_JSON", "SEMANTIC_INVALID", "INVALID_MODE", "MODE_RESTRICTED",
                "CONFIRMATION_REQUIRED", "DRYRUN_REQUIRED", "COMPILING",
                "TARGET_NOT_FOUND", "MISSING_PACKAGE",
            };
        private static Dictionary<string, RecommendationHealth> _recommendationHealthCache;
        private static long _recommendationHealthCacheAtTicks;

        public static event Action OnChanged;

        /// <summary>
        /// Master switch, per project (<see cref="ProjectPrefs"/>, on by default). When off, <see cref="Record"/> returns
        /// immediately. The getter reads the settings store (and possibly EditorPrefs for the pre-3.0 global value), so it
        /// must be called on the main thread — every Record call site satisfies this (skill execution already runs on the
        /// main thread).
        /// </summary>
        public static bool Enabled
        {
            get => ProjectPrefs.GetBool(PrefEnabled, true);
            set
            {
                if (ProjectPrefs.GetBool(PrefEnabled, true) == value) return;
                ProjectPrefs.SetBool(PrefEnabled, value);
                OnChanged?.Invoke();
            }
        }

        /// <summary>
        /// Appends one execution result. Non-blocking: the record is enqueued and flushed to disk (after a
        /// short delay -- see <see cref="FlushDelayMs"/>) by a thread-pool worker. Must be called on the main
        /// thread (this is where the Enabled EditorPref is read and the log path resolved, so the flush
        /// worker never touches Unity APIs).
        ///
        /// <paramref name="remotePort"/>/<paramref name="agentIdIsExplicit"/> mirror SkillsHttpServer's
        /// RequestJob fields: when the caller has an explicit X-Agent-Id header, <paramref name="agentId"/> is
        /// used as-is and no late resolution is attempted. Otherwise <paramref name="agentId"/> is only the
        /// *fallback* (whatever header/User-Agent guess was available at call time) -- BuildLine re-checks
        /// ClientProcessResolver's cache at flush time and prefers that if it has since resolved.
        ///
        /// <paramref name="error"/>/<paramref name="target"/> are written only for failed (<paramref name="ok"/>=false)
        /// lines. Callers pass them already path-redacted and truncated (see <see cref="SanitizeError"/>/<see cref="SanitizeTarget"/>).
        ///
        /// <paramref name="workflowMs"/> is the workflow history bookkeeping time of a tracked write (written as wfMs when present).
        /// </summary>
        public static void Record(string skill, string agentId, string mode, bool ok, string errorCode, long durationMs, int remotePort = -1, bool agentIdIsExplicit = false, string error = null, string target = null, long? workflowMs = null)
        {
            try
            {
                if (!Enabled) return;
                // Resolve and cache the path on the main thread, so FlushPending (a worker
                // thread) can reuse the cached value instead of reading Application.dataPath off the main thread.
                GetLogPath();
                _queue.Enqueue(new PendingRecord
                {
                    Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Skill = skill,
                    FallbackAgentId = agentId,
                    RemotePort = remotePort,
                    AgentIdIsExplicit = agentIdIsExplicit,
                    Mode = mode,
                    Ok = ok,
                    ErrorCode = errorCode,
                    Error = error,
                    Target = target,
                    DurationMs = durationMs,
                    WorkflowMs = workflowMs,
                });
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // Telemetry must never drag down or slow the caller; best-effort and swallow the exception.
                SkillsLogger.LogWarning($"Telemetry enqueue failed: {ex.Message}");
            }
        }

        /// <summary>Resolves the absolute telemetry log path (cached after the first call).</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// Builds (or returns a cached) /analytics response for the given window. window is
        /// normalized to 1h|24h|7d|all (anything else falls back to 24h).
        /// Results are cached per window for 30 seconds. Returns a complete JSON string ready to
        /// write directly into the HTTP response.
        /// </summary>
        public static string BuildAnalyticsJson(string window, string agent = null)
        {
            window = NormalizeWindow(window);
            string agentFilter = NormalizeAgentFilter(agent);
            // Filtered and unfiltered reports must never share a cache entry.
            string cacheKey = agentFilter == null ? window : window + "\n" + agentFilter;
            long now = DateTime.UtcNow.Ticks;

            lock (_analyticsCacheLock)
            {
                if (_analyticsCache.TryGetValue(cacheKey, out var cached) && now - cached.AtTicks < AnalyticsCacheTtlTicks)
                    return cached.Json;
            }

            string json;
            try
            {
                json = BuildAnalyticsJsonUncached(window, agentFilter);
            }
            catch (Exception ex)
            {
                // Aggregation is best-effort: any failure returns a well-formed empty report
                // instead of a 500, so the endpoint stays available and isn't jammed by one bad line.
                SkillsLogger.LogWarning($"Telemetry analytics build failed: {ex.Message}");
                json = JsonConvert.SerializeObject(BuildEmptyAnalytics(window, SafeEnabled()), SkillsCommon.JsonSettings);
            }

            lock (_analyticsCacheLock)
            {
                _analyticsCache[cacheKey] = new CachedAnalytics { Json = json, AtTicks = now };
            }
            return json;
        }

        /// <summary>
        /// Canonical form of the <c>?agent=</c> filter: trimmed, then folded with ClientProcessResolver's alias map
        /// (<c>claude-code</c> becomes <c>ClaudeCode</c>). Returns null when no filter was given.
        /// </summary>
        internal static string NormalizeAgentFilter(string agent)
        {
            var trimmed = agent?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : ClientProcessResolver.CanonicalizeExplicitAgentId(trimmed);
        }

        /// <summary>Failure detail: the message of a failed envelope, or null when it has none.</summary>
        internal static string ErrorMessageOf(JToken failure)
        {
            var message = (failure as JObject)?["error"];
            return message?.Type == JTokenType.String ? message.ToString() : null;
        }

        /// <summary>
        /// Reads the top-level <c>errorCode</c> and <c>error</c> of a failed response. Nested fields (for
        /// example an errorCode inside <c>details</c>) are never read. Unparseable input yields nulls.
        /// </summary>
        internal static void ReadFailureEnvelope(string json, out string errorCode, out string message)
        {
            errorCode = null;
            message = null;
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var envelope = JObject.Parse(json);
                var code = envelope["errorCode"];
                errorCode = code?.Type == JTokenType.String ? code.ToString() : null;
                message = ErrorMessageOf(envelope);
            }
            catch (JsonException)
            {
                // Not a JSON object: there is no structured failure detail to record.
            }
        }

        /// <summary>Path-redacted error message for a failure line, cut to the telemetry limit. Main thread only.</summary>
        internal static string SanitizeError(string message) =>
            SanitizeError(message, RegistryService.ProjectPath, UserHomeFolder());

        internal static string SanitizeError(string message, string projectRoot, string homeRoot) =>
            RedactAndCut(message, MaxErrorChars, projectRoot, homeRoot);

        /// <summary>
        /// Path-redacted, short argument summary for a failure line. Main thread only.
        /// </summary>
        internal static string SanitizeTarget(string argsJson) =>
            SanitizeTarget(argsJson, RegistryService.ProjectPath, UserHomeFolder());

        /// <summary>
        /// Redacts before SummarizeArgs truncates: a long home path cut at 32 characters would otherwise leave
        /// a fragment of the user name that no longer matches the root. String values are redacted in the parsed
        /// tree, the summary is taken from that, and the summary gets one more pass for SummarizeArgs' raw-text fallback.
        /// </summary>
        internal static string SanitizeTarget(string argsJson, string projectRoot, string homeRoot)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return null;

            string summarySource;
            try
            {
                var token = JToken.Parse(argsJson);
                RedactStringValues(token, projectRoot, homeRoot);
                summarySource = token.ToString(Formatting.None);
            }
            catch (JsonException)
            {
                summarySource = argsJson;
            }

            var summary = RedactPaths(SkillsModeManager.SummarizeArgs(summarySource), projectRoot, homeRoot);
            return CutToLimit(summary, MaxTargetChars);
        }

        private static string UserHomeFolder() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static string RedactAndCut(string text, int maxChars, string projectRoot, string homeRoot)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return CutToLimit(RedactPaths(text, projectRoot, homeRoot), maxChars);
        }

        private static string CutToLimit(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text)) return null;
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        private static void RedactStringValues(JToken token, string projectRoot, string homeRoot)
        {
            switch (token)
            {
                case JObject obj:
                    foreach (var prop in obj.Properties())
                        RedactStringValues(prop.Value, projectRoot, homeRoot);
                    break;
                case JArray array:
                    foreach (var item in array)
                        RedactStringValues(item, projectRoot, homeRoot);
                    break;
                case JValue value when value.Type == JTokenType.String:
                    value.Value = RedactPaths((string)value.Value, projectRoot, homeRoot);
                    break;
            }
        }

        /// <summary>
        /// Replaces <paramref name="projectRoot"/> with <c>&lt;project&gt;</c>, then <paramref name="homeRoot"/> with <c>~</c>.
        /// Both separator spellings are matched. The project root goes first, since it normally sits inside home.
        /// </summary>
        internal static string RedactPaths(string text, string projectRoot, string homeRoot)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = ReplacePathRoot(text, projectRoot, "<project>");
            return ReplacePathRoot(text, homeRoot, "~");
        }

        private static string ReplacePathRoot(string text, string root, string replacement)
        {
            root = root?.TrimEnd('/', '\\');
            if (string.IsNullOrEmpty(root)) return text;
            text = ReplaceAtPathBoundary(text, root.Replace('\\', '/'), replacement);
            return ReplaceAtPathBoundary(text, root.Replace('/', '\\'), replacement);
        }

        /// <summary>Replaces occurrences that are not followed by more name characters, so <c>/Users/bet</c> never eats <c>/Users/betsy</c>.</summary>
        private static string ReplaceAtPathBoundary(string text, string root, string replacement)
        {
            var sb = new StringBuilder(text.Length);
            int pos = 0;
            while (true)
            {
                int hit = text.IndexOf(root, pos, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) break;
                int end = hit + root.Length;
                bool continuesName = end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-' || text[end] == '.');
                sb.Append(text, pos, (continuesName ? end : hit) - pos);
                if (!continuesName) sb.Append(replacement);
                pos = end;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }

        internal static IReadOnlyDictionary<string, RecommendationHealth> GetRecommendationHealth()
        {
            if (!SafeEnabled())
                return new Dictionary<string, RecommendationHealth>(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow.Ticks;
            lock (_analyticsCacheLock)
            {
                if (_recommendationHealthCache != null &&
                    now - _recommendationHealthCacheAtTicks < AnalyticsCacheTtlTicks)
                    return _recommendationHealthCache;
            }

            Dictionary<string, RecommendationHealth> result;
            try { result = BuildRecommendationHealth(ReadAll(), DateTime.UtcNow.AddDays(-7)); }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Telemetry recommendation health failed: {ex.Message}");
                result = new Dictionary<string, RecommendationHealth>(StringComparer.OrdinalIgnoreCase);
            }

            lock (_analyticsCacheLock)
            {
                _recommendationHealthCache = result;
                _recommendationHealthCacheAtTicks = now;
            }
            return result;
        }

        /// <summary>Internal: synchronously drains the queue on the calling thread, to guarantee read consistency.</summary>
        internal static void FlushSync() => FlushPending();

        /// <summary>
        /// The delayed flush (see <see cref="FlushDelayMs"/>) is a thread-pool task that does not survive its domain, so
        /// whatever is still queued when a domain reload starts or the editor quits is written out here. Logs nothing.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterLifecycleFlush()
        {
            if (ImportWorkerGate.IsWorkerProcess) return;

            AssemblyReloadEvents.beforeAssemblyReload += FlushSync;
            EditorApplication.quitting += FlushSync;
        }

        /// <summary>
        /// Deletes telemetry records within a statistics window, using the same window values as
        /// <see cref="BuildAnalyticsJson"/>: <c>1h</c> / <c>24h</c> / <c>7d</c> / <c>all</c>.
        /// <c>all</c> wipes every retained file; other windows only delete records with
        /// <c>ts &gt;= cutoff</c> (i.e. falling within the window), and rewrite the primary log
        /// from the survivors. Always clears the analytics and recommendation caches, so the next
        /// read is fresh. Best-effort — never throws to the caller.
        /// </summary>
        /// <returns>
        /// <c>{ success, window, removed, remaining }</c>; on a hard failure returns <c>{ success:false, error }</c>.
        /// </returns>
        public static object DeleteWindow(string window)
        {
            try
            {
                window = NormalizeWindow(window);
                // Resolve the log path on the main thread before taking the write lock, so the
                // flush worker afterward never touches Application.dataPath off the main thread.
                GetLogPath();

                int removed;
                int remaining;
                lock (_writeLock)
                {
                    // Drain the in-flight queue inside the same lock, so a concurrent
                    // Record/flush can't re-append lines we're about to delete.
                    FlushPendingUnlocked();
                    var all = ReadAllUnlocked();
                    if (string.Equals(window, "all", StringComparison.Ordinal))
                    {
                        removed = all.Count;
                        remaining = 0;
                        WipeAllFilesUnlocked();
                    }
                    else
                    {
                        DateTime cutoff = WindowCutoffUtc(window);
                        var keep = new List<TelemetryRecord>(all.Count);
                        removed = 0;
                        foreach (var r in all)
                        {
                            // Timestamps that can't be parsed are always kept — only records
                            // confidently within the window get deleted (consistent with
                            // BuildAnalyticsJsonUncached, which also excludes unparseable lines from the window aggregation).
                            if (DateTime.TryParse(r.Ts, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var dt) && dt >= cutoff)
                            {
                                removed++;
                                continue;
                            }
                            keep.Add(r);
                        }
                        remaining = keep.Count;
                        RewritePrimaryUnlocked(keep);
                    }
                }

                InvalidateCaches();
                return new { success = true, window, removed, remaining };
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Telemetry DeleteWindow failed: {ex.Message}");
                return new { success = false, error = ex.Message };
            }
        }

        /// <summary>Internal: clears the on-disk telemetry log and its rotated copies; for test use only.</summary>
        internal static void ResetForTests()
        {
            FlushPending();
            try
            {
                WipeAllFilesUnlocked();
            }
            catch { /* ignore */ }
            InvalidateCaches();
        }

        private static void InvalidateCaches()
        {
            lock (_analyticsCacheLock)
            {
                _analyticsCache.Clear();
                _recommendationHealthCache = null;
                _recommendationHealthCacheAtTicks = 0;
            }
        }

        /// <summary>
        /// Deletes the primary telemetry file and its rotated copies. The caller must hold
        /// <see cref="_writeLock"/> (or be single-threaded, as in tests).
        /// </summary>
        private static void WipeAllFilesUnlocked()
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "UnitySkillsTelemetry*.jsonl"))
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Rewrites the primary log from <paramref name="records"/> (in time order) and deletes
        /// all rotated copies, so the retained set is exactly the surviving records.
        /// The caller must hold <see cref="_writeLock"/>.
        /// </summary>
        private static void RewritePrimaryUnlocked(List<TelemetryRecord> records)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = _cachedPath ?? Path.Combine(dir, LogFileName);

            // Delete the rotated files first, so a crash mid-write leaves at most a new primary
            // file — never a mixed state of "old rotations + a half-written primary file" that
            // would double-count records.
            for (int n = 1; n <= MaxRotatedFiles; n++)
            {
                var rotated = RotatedPath(n);
                if (File.Exists(rotated))
                {
                    try { File.Delete(rotated); } catch { /* ignore */ }
                }
            }

            if (records == null || records.Count == 0)
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
                return;
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, SkillsCommon.Utf8NoBom))
            {
                foreach (var r in records)
                {
                    // Rebuild the JSONL line from the parsed record, instead of re-emitting a raw
                    // line that only barely managed to deserialize despite being corrupted.
                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["ts"] = r.Ts,
                        ["skill"] = r.Skill,
                        ["agent"] = r.Agent,
                        ["mode"] = r.Mode,
                        ["ok"] = r.Ok,
                    };
                    if (!r.Ok)
                    {
                        payload["errorCode"] = r.ErrorCode;
                        if (!string.IsNullOrEmpty(r.Err)) payload["err"] = r.Err;
                        if (!string.IsNullOrEmpty(r.Target)) payload["target"] = r.Target;
                    }
                    payload["ms"] = r.Ms;
                    if (r.WfMs.HasValue) payload["wfMs"] = r.WfMs.Value;
                    writer.WriteLine(JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings));
                }
            }
        }

        /// <summary>
        /// Reads all telemetry lines without triggering a flush (the caller must already have
        /// flushed and must hold <see cref="_writeLock"/>).
        /// Time order matches <see cref="ReadAll"/>.
        /// </summary>
        private static List<TelemetryRecord> ReadAllUnlocked()
        {
            var records = new List<TelemetryRecord>();
            for (int n = MaxRotatedFiles; n >= 1; n--)
                ReadFileInto(RotatedPath(n), records);
            ReadFileInto(GetLogPath(), records);
            return records;
        }

        // ===== Write path =====

        internal static string BuildLine(PendingRecord record)
        {
            // Late-bound agent identity: resolved right now (at flush time), not when Record() was called.
            // ClientProcessResolver.TryGetAgentId is a pure, thread-safe dictionary lookup -- safe to call from
            // this worker thread, and it never touches any Unity API.
            string agentId = record.FallbackAgentId;
            if (!record.AgentIdIsExplicit && record.RemotePort > 0 &&
                ClientProcessResolver.TryGetAgentId(record.RemotePort, out var resolved))
            {
                agentId = resolved;
            }

            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ts"] = record.Ts,
                ["skill"] = record.Skill,
                ["agent"] = agentId,
                ["mode"] = record.Mode,
                ["ok"] = record.Ok,
            };
            // Convention: errorCode is omitted entirely when ok=true; kept (even if the value is null) when ok=false.
            // err/target follow the same rule, but are omitted when the caller had nothing to record.
            if (!record.Ok)
            {
                payload["errorCode"] = record.ErrorCode;
                if (!string.IsNullOrEmpty(record.Error)) payload["err"] = record.Error;
                if (!string.IsNullOrEmpty(record.Target)) payload["target"] = record.Target;
            }
            payload["ms"] = record.DurationMs;
            if (record.WorkflowMs.HasValue) payload["wfMs"] = record.WorkflowMs.Value;
            return JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings);
        }

        private static void ScheduleFlush()
        {
            // Coalesce multiple appends into a single flush task, and delay it (see FlushDelayMs) so the
            // agent field gets a fair chance to resolve before BuildLine runs. FlushSync (used by
            // ReadAll/DeleteWindow -- anything that needs read-your-writes *now*) bypasses this delay
            // entirely and just uses whatever's resolved (or not) at that instant; it must never wait.
            if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(FlushDelayMs).ConfigureAwait(false);
                    FlushPending();
                }
                finally { Interlocked.Exchange(ref _flushScheduled, 0); }
            });
        }

        private static void FlushPending()
        {
            if (_queue.IsEmpty) return;
            lock (_writeLock)
            {
                FlushPendingUnlocked();
            }
        }

        /// <summary>
        /// Drains the write queue to disk. The caller must hold <see cref="_writeLock"/> (or be
        /// single-threaded, as in tests). The regular flush path shares this method with
        /// <see cref="DeleteWindow"/>, so a concurrent Record can't re-append lines that are about to be deleted.
        /// </summary>
        private static void FlushPendingUnlocked()
        {
            if (_queue.IsEmpty) return;
            try
            {
                var dir = _cachedDir ?? ResolveLibraryDir();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var path = _cachedPath ?? Path.Combine(dir, LogFileName);

                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(fs, SkillsCommon.Utf8NoBom))
                {
                    while (_queue.TryDequeue(out var record))
                        writer.WriteLine(BuildLine(record));
                }

                RotateIfNeeded(path);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Telemetry flush failed: {ex.Message}");
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxFileBytes) return;

                // Shift each file in turn: .2 -> .3, .1 -> .2, primary file -> .1
                for (int i = MaxRotatedFiles; i >= 1; i--)
                {
                    var src = i == 1 ? path : RotatedPath(i - 1);
                    var dst = RotatedPath(i);
                    if (File.Exists(dst))
                    {
                        try { File.Delete(dst); } catch { /* ignore */ }
                    }
                    if (File.Exists(src))
                    {
                        try { File.Move(src, dst); } catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Telemetry rotate failed: {ex.Message}");
            }
        }

        private static string RotatedPath(int n)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            return Path.Combine(dir, $"UnitySkillsTelemetry.{n}.jsonl");
        }

        /// <summary>
        /// Returns <c>&lt;project&gt;/Library</c>. Falls back to <c>Application.persistentDataPath</c>
        /// (matching SkillsAuditLog) if accessed before the Unity editor is ready.
        /// </summary>
        private static string ResolveLibraryDir()
        {
            try
            {
                var dataPath = Application.dataPath;
                if (!string.IsNullOrEmpty(dataPath))
                {
                    var projectRoot = Path.GetFullPath(Path.Combine(dataPath, ".."));
                    return Path.Combine(projectRoot, "Library");
                }
            }
            catch { /* Unity APIs aren't ready on this thread yet; keep going */ }

            try { return Application.persistentDataPath; }
            catch { return Path.GetTempPath(); }
        }

        // ===== Read and aggregation path =====

        /// <summary>A parsed telemetry line, with field names bound to the JSONL keys.</summary>
        internal sealed class TelemetryRecord
        {
            [JsonProperty("ts")] public string Ts;
            [JsonProperty("skill")] public string Skill;
            [JsonProperty("agent")] public string Agent;
            [JsonProperty("mode")] public string Mode;
            [JsonProperty("ok")] public bool Ok;
            [JsonProperty("errorCode")] public string ErrorCode;
            [JsonProperty("err")] public string Err;
            [JsonProperty("target")] public string Target;
            [JsonProperty("ms")] public long Ms;
            [JsonProperty("wfMs")] public long? WfMs;
        }

        private static Dictionary<string, RecommendationHealth> BuildRecommendationHealth(
            IEnumerable<TelemetryRecord> records, DateTime cutoffUtc)
        {
            var aggregates = new Dictionary<string, SkillAgg>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records ?? Enumerable.Empty<TelemetryRecord>())
            {
                if (record == null || string.IsNullOrWhiteSpace(record.Skill) ||
                    !(string.Equals(record.Mode, "execute", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(record.Mode, "batch_step", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (!DateTime.TryParse(record.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp) ||
                    timestamp < cutoffUtc)
                    continue;
                if (!record.Ok && !string.IsNullOrWhiteSpace(record.ErrorCode) &&
                    RecommendationIgnoredErrorCodes.Contains(record.ErrorCode))
                    continue;

                if (!aggregates.TryGetValue(record.Skill, out var aggregate))
                    aggregates[record.Skill] = aggregate = new SkillAgg();
                aggregate.Calls++;
                aggregate.TotalMs += Math.Max(0, record.Ms);
                aggregate.MaxMs = Math.Max(aggregate.MaxMs, record.Ms);
                if (!record.Ok) aggregate.Errors++;
            }

            var result = new Dictionary<string, RecommendationHealth>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in aggregates)
            {
                var aggregate = pair.Value;
                result[pair.Key] = CalculateRecommendationHealth(aggregate.Calls, aggregate.Errors, aggregate.TotalMs);
            }
            return result;
        }

        internal static RecommendationHealth CalculateRecommendationHealth(int calls, int errors, long totalMs)
        {
            calls = Math.Max(0, calls);
            errors = Math.Max(0, Math.Min(errors, calls));
            var rate = calls > 0 ? (double)errors / calls : 0.0;
            var avgMs = calls > 0 ? (double)Math.Max(0, totalMs) / calls : 0.0;
            var penalty = calls < 5 ? 0 : rate >= 0.75 ? 3 : rate >= 0.50 ? 2 : rate >= 0.25 ? 1 : 0;
            var warnings = new List<string>();
            if (penalty > 0)
                warnings.Add($"Local 7d telemetry: {errors}/{calls} valid calls failed ({rate:P0}); ranking reduced by {penalty}.");
            if (calls >= 3 && avgMs >= 2000)
                warnings.Add($"Local 7d telemetry: average execution time is {avgMs / 1000.0:F1}s across {calls} valid calls.");
            return new RecommendationHealth
            {
                Calls = calls,
                Errors = errors,
                AvgMs = (long)Math.Round(avgMs),
                ErrorRate = Math.Round(rate, 4),
                Penalty = penalty,
                Warnings = warnings.ToArray(),
            };
        }

        /// <summary>Per-skill running aggregates.</summary>
        private sealed class SkillAgg
        {
            public int Calls;
            public int Errors;
            public long TotalMs;
            public long MaxMs;

            // Only successful calls' durations are tracked. A rejected call (unknown skill,
            // validation failure, permission gate) never entered the skill body at all, so its
            // duration says nothing about how slow the skill is — the "slowest" leaderboard uses
            // this data instead of the totals above.
            public int OkCalls;
            public long OkTotalMs;
            public long OkMaxMs;

            public double AvgMs => Calls > 0 ? (double)TotalMs / Calls : 0.0;
            public double ErrorRate => Calls > 0 ? (double)Errors / Calls : 0.0;
            public double OkAvgMs => OkCalls > 0 ? (double)OkTotalMs / OkCalls : 0.0;
        }

        /// <summary>Per-errorCode running aggregates.</summary>
        private sealed class ErrAgg
        {
            public int Count;
            public readonly Dictionary<string, int> SkillCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Reads all telemetry lines from the primary file and its 3 rotated copies into memory,
        /// oldest to newest.
        /// Unlike SkillsAuditLog.ReadRecent (which only reads the tail), this is a full read —
        /// /analytics has to aggregate the entire retention window (≤4MB total).
        /// Flushes pending writes first, so a just-recorded call is visible.
        /// </summary>
        private static List<TelemetryRecord> ReadAll()
        {
            FlushSync();
            var records = new List<TelemetryRecord>();
            // Rotation moves the primary file to .1, so .3 is oldest and the primary file is
            // newest. Reading in this order (top to bottom within each file) gives global time
            // order, which "recentErrors" and firstTs/lastTs depend on.
            for (int n = MaxRotatedFiles; n >= 1; n--)
                ReadFileInto(RotatedPath(n), records);
            ReadFileInto(GetLogPath(), records);
            return records;
        }

        private static void ReadFileInto(string path, List<TelemetryRecord> into)
        {
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs, SkillsCommon.Utf8NoBom))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        TelemetryRecord rec;
                        try { rec = JsonConvert.DeserializeObject<TelemetryRecord>(line); }
                        catch { continue; } // Skip malformed lines rather than failing the whole read over one bad line
                        if (rec != null && !string.IsNullOrEmpty(rec.Ts))
                            into.Add(rec);
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Telemetry read failed ({Path.GetFileName(path)}): {ex.Message}");
            }
        }

        private static string BuildAnalyticsJsonUncached(string window, string agentFilter) =>
            AggregateAnalytics(ReadAll(), window, agentFilter, DateTime.UtcNow);

        /// <summary>
        /// Aggregates telemetry records into the /analytics report. Seam for tests: <paramref name="records"/> and
        /// <paramref name="nowUtc"/> replace ReadAll() and DateTime.UtcNow. <paramref name="agent"/> is canonicalized
        /// here (see NormalizeAgentFilter); when set, only matching records count and the report echoes it as <c>agent</c>.
        /// </summary>
        internal static string AggregateAnalytics(IEnumerable<TelemetryRecord> records, string window, string agent, DateTime nowUtc)
        {
            bool enabled = Enabled;
            string agentFilter = NormalizeAgentFilter(agent);
            DateTime cutoff = WindowCutoffUtc(window, nowUtc);
            bool unbounded = string.Equals(window, "all", StringComparison.Ordinal);

            var perSkill = new Dictionary<string, SkillAgg>(StringComparer.Ordinal);
            var perErrorCode = new Dictionary<string, ErrAgg>(StringComparer.Ordinal);
            var perMode = new Dictionary<string, int>(StringComparer.Ordinal);
            var perAgent = new Dictionary<string, int>(StringComparer.Ordinal);
            var errorRecords = new List<TelemetryRecord>(); // In time order (i.e. read order)

            int totalCalls = 0, okCalls = 0, errorCalls = 0;
            string firstTs = null, lastTs = null;

            foreach (var r in records)
            {
                if (agentFilter != null && !string.Equals(AgentLabel(r.Agent), agentFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!unbounded)
                {
                    if (!DateTime.TryParse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        continue; // Can't be placed within the window, so excluded
                    if (dt < cutoff) continue;
                }

                totalCalls++;
                if (r.Ok) okCalls++; else errorCalls++;

                if (firstTs == null || string.CompareOrdinal(r.Ts, firstTs) < 0) firstTs = r.Ts;
                if (lastTs == null || string.CompareOrdinal(r.Ts, lastTs) > 0) lastTs = r.Ts;

                string skillKey = string.IsNullOrEmpty(r.Skill) ? "(unknown)" : r.Skill;
                if (!perSkill.TryGetValue(skillKey, out var sa)) { sa = new SkillAgg(); perSkill[skillKey] = sa; }
                sa.Calls++;
                sa.TotalMs += r.Ms;
                if (r.Ms > sa.MaxMs) sa.MaxMs = r.Ms;
                if (!r.Ok) sa.Errors++;
                else
                {
                    sa.OkCalls++;
                    sa.OkTotalMs += r.Ms;
                    if (r.Ms > sa.OkMaxMs) sa.OkMaxMs = r.Ms;
                }

                string modeKey = string.IsNullOrEmpty(r.Mode) ? "(unknown)" : r.Mode;
                perMode.TryGetValue(modeKey, out var mc);
                perMode[modeKey] = mc + 1;

                string agentKey = AgentLabel(r.Agent);
                perAgent.TryGetValue(agentKey, out var ac);
                perAgent[agentKey] = ac + 1;

                if (!r.Ok)
                {
                    errorRecords.Add(r);
                    if (!string.IsNullOrEmpty(r.ErrorCode))
                    {
                        if (!perErrorCode.TryGetValue(r.ErrorCode, out var ea)) { ea = new ErrAgg(); perErrorCode[r.ErrorCode] = ea; }
                        ea.Count++;
                        ea.SkillCounts.TryGetValue(skillKey, out var scv);
                        ea.SkillCounts[skillKey] = scv + 1;
                    }
                }
            }

            double errorRate = totalCalls > 0 ? Math.Round((double)errorCalls / totalCalls, 4) : 0.0;

            var topSkills = perSkill
                .OrderByDescending(kv => kv.Value.Calls)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kv => new
                {
                    skill = kv.Key,
                    calls = kv.Value.Calls,
                    errorRate = Math.Round(kv.Value.ErrorRate, 4),
                    avgMs = (long)Math.Round(kv.Value.AvgMs),
                })
                .ToArray();

            var errorCodes = perErrorCode
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new
                {
                    code = kv.Key,
                    count = kv.Value.Count,
                    topSkills = kv.Value.SkillCounts
                        .OrderByDescending(s => s.Value)
                        .ThenBy(s => s.Key, StringComparer.Ordinal)
                        .Take(3)
                        .Select(s => s.Key)
                        .ToArray(),
                })
                .ToArray();

            // Error-prone leaderboard: only skills with a large enough sample size (calls>=5) rank by error rate.
            var errorProneSkills = perSkill
                .Where(kv => kv.Value.Calls >= 5 && kv.Value.Errors > 0)
                .OrderByDescending(kv => kv.Value.ErrorRate)
                .ThenByDescending(kv => kv.Value.Calls)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kv => new
                {
                    skill = kv.Key,
                    calls = kv.Value.Calls,
                    errors = kv.Value.Errors,
                    errorRate = Math.Round(kv.Value.ErrorRate, 4),
                })
                .ToArray();

            // Slowest leaderboard: only counts successful calls, and only with >=3 successes, to
            // avoid a single outlier dominating the list. Failed calls are excluded because the
            // duration of a rejected call (unknown skill, validation, permission gate) is charged
            // to the router layer rather than the skill body — including it would rank a name
            // that never actually executed as a "slow skill".
            var slowestSkills = perSkill
                .Where(kv => kv.Value.OkCalls >= 3)
                .OrderByDescending(kv => kv.Value.OkAvgMs)
                .ThenByDescending(kv => kv.Value.OkMaxMs)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kv => new
                {
                    skill = kv.Key,
                    avgMs = (long)Math.Round(kv.Value.OkAvgMs),
                    maxMs = kv.Value.OkMaxMs,
                    calls = kv.Value.OkCalls,
                })
                .ToArray();

            var byAgent = perAgent
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { agent = kv.Key, calls = kv.Value })
                .ToArray();

            // The 10 most recent errors, newest first.
            var recentSlice = errorRecords.Skip(Math.Max(0, errorRecords.Count - 10)).ToList();
            recentSlice.Reverse();
            var recentErrors = recentSlice.Select(BuildRecentErrorEntry).ToArray();

            var response = new
            {
                status = "ok",
                window,
                telemetryEnabled = enabled,
                summary = new
                {
                    totalCalls,
                    okCalls,
                    errorCalls,
                    errorRate,
                    uniqueSkills = perSkill.Count,
                    firstTs,
                    lastTs,
                },
                topSkills,
                errorCodes,
                errorProneSkills,
                slowestSkills,
                byMode = perMode,
                byAgent,
                recentErrors,
            };
            string json = JsonConvert.SerializeObject(response, SkillsCommon.JsonSettings);
            if (agentFilter == null) return json;

            // Filtered reports echo the canonical filter right after "window"; unfiltered output keeps its exact shape.
            var echoed = JObject.Parse(json);
            echoed.Property("window").AddAfterSelf(new JProperty("agent", agentFilter));
            return JsonConvert.SerializeObject(echoed, SkillsCommon.JsonSettings);
        }

        /// <summary>Label used for an agent in byAgent and in the agent filter: missing agents are "(unknown)".</summary>
        private static string AgentLabel(string agent) => string.IsNullOrEmpty(agent) ? "(unknown)" : agent;

        /// <summary>
        /// One recentErrors entry. err and target are added only when present, so older lines produce the same shape.
        /// </summary>
        private static JObject BuildRecentErrorEntry(TelemetryRecord r)
        {
            var entry = new JObject
            {
                ["ts"] = r.Ts,
                ["skill"] = r.Skill,
                ["errorCode"] = r.ErrorCode,
                ["mode"] = r.Mode,
            };
            if (!string.IsNullOrEmpty(r.Err)) entry["err"] = r.Err;
            if (!string.IsNullOrEmpty(r.Target)) entry["target"] = r.Target;
            return entry;
        }

        private static object BuildEmptyAnalytics(string window, bool enabled) => new
        {
            status = "ok",
            window,
            telemetryEnabled = enabled,
            summary = new
            {
                totalCalls = 0,
                okCalls = 0,
                errorCalls = 0,
                errorRate = 0.0,
                uniqueSkills = 0,
                firstTs = (string)null,
                lastTs = (string)null,
            },
            topSkills = Array.Empty<object>(),
            errorCodes = Array.Empty<object>(),
            errorProneSkills = Array.Empty<object>(),
            slowestSkills = Array.Empty<object>(),
            byMode = new Dictionary<string, int>(),
            byAgent = Array.Empty<object>(),
            recentErrors = Array.Empty<object>(),
        };

        private static string NormalizeWindow(string window)
        {
            if (string.IsNullOrEmpty(window)) return "24h";
            switch (window.ToLowerInvariant())
            {
                case "1h": return "1h";
                case "24h": return "24h";
                case "7d": return "7d";
                case "all": return "all";
                default: return "24h";
            }
        }

        private static DateTime WindowCutoffUtc(string window) => WindowCutoffUtc(window, DateTime.UtcNow);

        private static DateTime WindowCutoffUtc(string window, DateTime now)
        {
            switch (window)
            {
                case "1h": return now.AddHours(-1);
                case "7d": return now.AddDays(-7);
                case "all": return DateTime.MinValue;
                default: return now.AddHours(-24); // "24h" (default)
            }
        }

        private static bool SafeEnabled()
        {
            try { return Enabled; }
            catch { return true; }
        }
    }
}

// Producer:Betsy
