using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Append-only JSONL log of skill EXECUTION telemetry — the data source behind
    /// GET /analytics ("which skill was called how often, how slow, and how often did it fail").
    ///
    /// Deliberately separate from <see cref="SkillsAuditLog"/>: that log records permission
    /// events (grant/deny/allowlist), this one records every skill invocation outcome. They
    /// live in different files (<c>Library/UnitySkillsTelemetry.jsonl</c>) so a high-frequency
    /// execution stream never dilutes the permission audit trail.
    ///
    /// Structure mirrors SkillsAuditLog: writes are queued on the calling (main) thread and
    /// flushed asynchronously; files roll over at 1MB keeping up to 3 historical copies. All
    /// disk I/O is best-effort — a telemetry failure must never affect a business response.
    ///
    /// One JSONL line per call:
    /// <code>{"ts":"2026-07-09T...Z","skill":"gameobject_create","agent":"ClaudeCode",
    /// "mode":"execute","ok":true,"ms":12}</code>
    /// (<c>errorCode</c> is present only when <c>ok</c> is false.)
    /// </summary>
    public static class SkillTelemetryService
    {
        private const string LogFileName = "UnitySkillsTelemetry.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const string PrefEnabled = "UnitySkills_TelemetryEnabled";

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked guard
        private static string _cachedDir;
        private static string _cachedPath;

        // Aggregation cache: keep the serialized /analytics JSON per window for 30s so a burst
        // of polls doesn't re-read up to 4MB from disk on every request. Read/written only on
        // the main thread (the endpoint handler), but locked defensively.
        private const long AnalyticsCacheTtlTicks = 30L * TimeSpan.TicksPerSecond;
        private static readonly object _analyticsCacheLock = new object();
        private static readonly Dictionary<string, CachedAnalytics> _analyticsCache =
            new Dictionary<string, CachedAnalytics>(StringComparer.OrdinalIgnoreCase);

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
                "CONFIRMATION_REQUIRED", "COMPILING",
            };
        private static Dictionary<string, RecommendationHealth> _recommendationHealthCache;
        private static long _recommendationHealthCacheAtTicks;

        /// <summary>
        /// Master switch (EditorPrefs, default ON). When off, <see cref="Record"/> returns
        /// immediately. The getter reads EditorPrefs, so it must be called on the main thread —
        /// which every Record call site is (skill execution runs on the main thread).
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        /// <summary>
        /// Append one execution outcome. Non-blocking: the JSON line is queued and flushed on a
        /// thread-pool worker. Call from the main thread (reads the Enabled EditorPref and
        /// resolves the log path there so the flush worker never touches a Unity API).
        /// </summary>
        public static void Record(string skill, string agentId, string mode, bool ok, string errorCode, long durationMs)
        {
            try
            {
                if (!Enabled) return;
                // Resolve+cache the path here on the main thread so FlushPending (worker thread)
                // reuses the cached value instead of reading Application.dataPath off-thread.
                GetLogPath();
                _queue.Enqueue(BuildLine(skill, agentId, mode, ok, errorCode, durationMs));
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // Telemetry MUST NOT crash or slow the caller. Best-effort, swallow.
                SkillsLogger.LogWarning($"Telemetry enqueue failed: {ex.Message}");
            }
        }

        /// <summary>Resolve the telemetry log absolute path (cached after first call).</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// Build (or return a cached) /analytics response for the given window. The window is
        /// normalized to 1h|24h|7d|all (anything else → 24h). Result is cached 30s per window.
        /// Returns a fully serialized JSON string ready to write to the HTTP response.
        /// </summary>
        public static string BuildAnalyticsJson(string window)
        {
            window = NormalizeWindow(window);
            long now = DateTime.UtcNow.Ticks;

            lock (_analyticsCacheLock)
            {
                if (_analyticsCache.TryGetValue(window, out var cached) && now - cached.AtTicks < AnalyticsCacheTtlTicks)
                    return cached.Json;
            }

            string json;
            try
            {
                json = BuildAnalyticsJsonUncached(window);
            }
            catch (Exception ex)
            {
                // Aggregation is best-effort: on any failure return a well-formed empty report
                // rather than a 500. The endpoint stays usable and never blocks on a bad line.
                SkillsLogger.LogWarning($"Telemetry analytics build failed: {ex.Message}");
                json = JsonConvert.SerializeObject(BuildEmptyAnalytics(window, SafeEnabled()), SkillsCommon.JsonSettings);
            }

            lock (_analyticsCacheLock)
            {
                _analyticsCache[window] = new CachedAnalytics { Json = json, AtTicks = now };
            }
            return json;
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

        /// <summary>Internal: drain the queue synchronously on the calling thread (read consistency).</summary>
        internal static void FlushSync() => FlushPending();

        /// <summary>
        /// Delete telemetry records for an analytics window aligned with
        /// <see cref="BuildAnalyticsJson"/>: <c>1h</c> / <c>24h</c> / <c>7d</c> / <c>all</c>.
        /// <c>all</c> wipes every retained file; other windows remove only records with
        /// <c>ts &gt;= cutoff</c> (i.e. inside the window) and rewrite the primary log with
        /// the survivors. Always clears the analytics + recommendation caches so the next
        /// read is fresh. Best-effort — never throws to the caller.
        /// </summary>
        /// <returns>
        /// <c>{ success, window, removed, remaining }</c>, or
        /// <c>{ success:false, error }</c> on a hard failure.
        /// </returns>
        public static object DeleteWindow(string window)
        {
            try
            {
                window = NormalizeWindow(window);
                // Resolve the log path on the main thread before taking the write lock so the
                // flush worker never has to touch Application.dataPath off-thread later.
                GetLogPath();

                int removed;
                int remaining;
                lock (_writeLock)
                {
                    // Drain any in-flight queue entries under the same lock so a concurrent
                    // Record/flush cannot re-append lines we are about to drop.
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
                            // Unparseable timestamps are kept — we only delete what we can place
                            // confidently inside the window (matches BuildAnalyticsJsonUncached,
                            // which excludes unparseable lines from windowed aggregates).
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

        /// <summary>Internal: wipe the on-disk telemetry log (and rotated copies). Tests only.</summary>
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
        /// Delete primary + rotated telemetry files. Caller must hold <see cref="_writeLock"/>
        /// (or be single-threaded, as in tests).
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
        /// Rewrite the primary log with <paramref name="records"/> (chronological order) and
        /// drop every rotated copy so the retained set is exactly the survivors. Caller must
        /// hold <see cref="_writeLock"/>.
        /// </summary>
        private static void RewritePrimaryUnlocked(List<TelemetryRecord> records)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = _cachedPath ?? Path.Combine(dir, LogFileName);

            // Drop rotated files first so a crash mid-write leaves at most the new primary
            // (never a mix of old rotated + half-written primary that would double-count).
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
                    // Rebuild the JSONL line from the parsed record so we never re-emit a
                    // corrupt original line we managed to deserialize.
                    var payload = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["ts"] = r.Ts,
                        ["skill"] = r.Skill,
                        ["agent"] = r.Agent,
                        ["mode"] = r.Mode,
                        ["ok"] = r.Ok,
                    };
                    if (!r.Ok)
                        payload["errorCode"] = r.ErrorCode;
                    payload["ms"] = r.Ms;
                    writer.WriteLine(JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings));
                }
            }
        }

        /// <summary>
        /// Read every telemetry line without flushing (caller is expected to have flushed and
        /// hold <see cref="_writeLock"/>). Same chronological order as <see cref="ReadAll"/>.
        /// </summary>
        private static List<TelemetryRecord> ReadAllUnlocked()
        {
            var records = new List<TelemetryRecord>();
            for (int n = MaxRotatedFiles; n >= 1; n--)
                ReadFileInto(RotatedPath(n), records);
            ReadFileInto(GetLogPath(), records);
            return records;
        }

        // ===== write path =====

        private static string BuildLine(string skill, string agentId, string mode, bool ok, string errorCode, long durationMs)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["skill"] = skill,
                ["agent"] = agentId,
                ["mode"] = mode,
                ["ok"] = ok,
            };
            // Spec: omit errorCode entirely when ok=true; keep it (even if null) when ok=false.
            if (!ok)
                payload["errorCode"] = errorCode;
            payload["ms"] = durationMs;
            return JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings);
        }

        private static void ScheduleFlush()
        {
            // Coalesce many appends into a single flush task.
            if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;
            Task.Run(() =>
            {
                try { FlushPending(); }
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
        /// Drain the write queue onto disk. Caller must hold <see cref="_writeLock"/>
        /// (or be single-threaded, as in tests). Used both by the normal flush path and
        /// by <see cref="DeleteWindow"/> so a concurrent Record can't re-append lines
        /// that are about to be dropped.
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
                    while (_queue.TryDequeue(out var line))
                        writer.WriteLine(line);
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

                // Shift .2 -> .3, .1 -> .2, primary -> .1
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
        /// when accessed before the Unity Editor is ready (mirrors SkillsAuditLog).
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
            catch { /* Unity API not ready on this thread; fall through */ }

            try { return Application.persistentDataPath; }
            catch { return Path.GetTempPath(); }
        }

        // ===== read + aggregation path =====

        /// <summary>Parsed telemetry line. Field names bound to the JSONL keys.</summary>
        private sealed class TelemetryRecord
        {
            [JsonProperty("ts")] public string Ts;
            [JsonProperty("skill")] public string Skill;
            [JsonProperty("agent")] public string Agent;
            [JsonProperty("mode")] public string Mode;
            [JsonProperty("ok")] public bool Ok;
            [JsonProperty("errorCode")] public string ErrorCode;
            [JsonProperty("ms")] public long Ms;
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

        /// <summary>Per-skill running aggregate.</summary>
        private sealed class SkillAgg
        {
            public int Calls;
            public int Errors;
            public long TotalMs;
            public long MaxMs;
            public double AvgMs => Calls > 0 ? (double)TotalMs / Calls : 0.0;
            public double ErrorRate => Calls > 0 ? (double)Errors / Calls : 0.0;
        }

        /// <summary>Per-errorCode running aggregate.</summary>
        private sealed class ErrAgg
        {
            public int Count;
            public readonly Dictionary<string, int> SkillCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Read every telemetry line from the primary file plus the 3 rotated copies, oldest to
        /// newest, into memory. Unlike SkillsAuditLog.ReadRecent (tail-only) this is a full read —
        /// /analytics aggregates the whole retained window (≤4MB total). Flushes pending writes
        /// first so freshly recorded calls are visible.
        /// </summary>
        private static List<TelemetryRecord> ReadAll()
        {
            FlushSync();
            var records = new List<TelemetryRecord>();
            // Rotation shifts primary -> .1, so .3 is oldest and the primary is newest. Reading
            // in this order (each file top-to-bottom) yields global chronological order, which
            // "recentErrors" and firstTs/lastTs rely on.
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
                        catch { continue; } // skip a malformed line rather than failing the read
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

        private static string BuildAnalyticsJsonUncached(string window)
        {
            bool enabled = Enabled;
            var all = ReadAll();
            DateTime cutoff = WindowCutoffUtc(window);
            bool unbounded = string.Equals(window, "all", StringComparison.Ordinal);

            var perSkill = new Dictionary<string, SkillAgg>(StringComparer.Ordinal);
            var perErrorCode = new Dictionary<string, ErrAgg>(StringComparer.Ordinal);
            var perMode = new Dictionary<string, int>(StringComparer.Ordinal);
            var perAgent = new Dictionary<string, int>(StringComparer.Ordinal);
            var errorRecords = new List<TelemetryRecord>(); // chronological (read order)

            int totalCalls = 0, okCalls = 0, errorCalls = 0;
            string firstTs = null, lastTs = null;

            foreach (var r in all)
            {
                if (!unbounded)
                {
                    if (!DateTime.TryParse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        continue; // can't place it in the window → exclude
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

                string modeKey = string.IsNullOrEmpty(r.Mode) ? "(unknown)" : r.Mode;
                perMode.TryGetValue(modeKey, out var mc);
                perMode[modeKey] = mc + 1;

                string agentKey = string.IsNullOrEmpty(r.Agent) ? "(unknown)" : r.Agent;
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

            // Error-prone: only skills with a meaningful sample (calls>=5) rank by error rate.
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

            // Slowest: only skills with calls>=3 so a single outlier can't top the chart.
            var slowestSkills = perSkill
                .Where(kv => kv.Value.Calls >= 3)
                .OrderByDescending(kv => kv.Value.AvgMs)
                .ThenByDescending(kv => kv.Value.MaxMs)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(kv => new
                {
                    skill = kv.Key,
                    avgMs = (long)Math.Round(kv.Value.AvgMs),
                    maxMs = kv.Value.MaxMs,
                    calls = kv.Value.Calls,
                })
                .ToArray();

            var byAgent = perAgent
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new { agent = kv.Key, calls = kv.Value })
                .ToArray();

            // Most recent 10 errors, newest first.
            var recentSlice = errorRecords.Skip(Math.Max(0, errorRecords.Count - 10)).ToList();
            recentSlice.Reverse();
            var recentErrors = recentSlice
                .Select(r => new { ts = r.Ts, skill = r.Skill, errorCode = r.ErrorCode, mode = r.Mode })
                .ToArray();

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
            return JsonConvert.SerializeObject(response, SkillsCommon.JsonSettings);
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

        private static DateTime WindowCutoffUtc(string window)
        {
            var now = DateTime.UtcNow;
            switch (window)
            {
                case "1h": return now.AddHours(-1);
                case "7d": return now.AddDays(-7);
                case "all": return DateTime.MinValue;
                default: return now.AddHours(-24); // "24h"
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
