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
    /// 技能"执行"遥测的追加式 JSONL 日志——GET /analytics（"某技能被调用多少次、多慢、失败率多高"）的数据源。
    ///
    /// 刻意与 <see cref="SkillsAuditLog"/> 分开：那份记录权限事件（授权/拒绝/白名单），这份记录每次技能调用的结果。
    /// 两者落在不同文件（本文件为 <c>Library/UnitySkillsTelemetry.jsonl</c>），使高频执行流不会稀释权限审计轨迹。
    ///
    /// 结构与 SkillsAuditLog 一致：写入在调用线程（主线程）排队、异步落盘；文件到 1MB 轮转，最多保留 3 份历史。
    /// 所有磁盘 I/O 均为尽力而为——遥测失败绝不能影响业务响应。
    ///
    /// 每次调用一行 JSONL：
    /// <code>{"ts":"2026-07-09T...Z","skill":"gameobject_create","agent":"ClaudeCode",
    /// "mode":"execute","ok":true,"ms":12}</code>
    /// （<c>errorCode</c> 仅在 <c>ok</c> 为 false 时出现。）
    /// </summary>
    public static class SkillTelemetryService
    {
        private const string LogFileName = "UnitySkillsTelemetry.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const string PrefEnabled = "UnitySkills_TelemetryEnabled";

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked 守卫
        private static string _cachedDir;
        private static string _cachedPath;

        // 聚合缓存：按 window 缓存序列化后的 /analytics JSON 30 秒，避免连续轮询时每次请求都从磁盘重读多达 4MB。
        // 只在主线程（端点处理器）读写，但仍保守加锁。
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
                "TARGET_NOT_FOUND", "MISSING_PACKAGE",
            };
        private static Dictionary<string, RecommendationHealth> _recommendationHealthCache;
        private static long _recommendationHealthCacheAtTicks;

        /// <summary>
        /// 总开关（EditorPrefs，默认开）。关闭时 <see cref="Record"/> 立即返回。
        /// getter 会读 EditorPrefs，故必须在主线程调用——所有 Record 调用点都满足（技能执行本就在主线程）。
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        /// <summary>
        /// 追加一条执行结果。非阻塞：JSON 行入队后由线程池 worker 落盘。
        /// 必须在主线程调用（在此读 Enabled 这个 EditorPref 并解析日志路径，使落盘 worker 永不触碰 Unity API）。
        /// </summary>
        public static void Record(string skill, string agentId, string mode, bool ok, string errorCode, long durationMs)
        {
            try
            {
                if (!Enabled) return;
                // 在主线程解析并缓存路径，好让 FlushPending（worker 线程）复用缓存值，
                // 而不是在非主线程读 Application.dataPath。
                GetLogPath();
                _queue.Enqueue(BuildLine(skill, agentId, mode, ok, errorCode, durationMs));
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // 遥测绝不能拖垮或拖慢调用方，尽力而为并吞掉异常。
                SkillsLogger.LogWarning($"Telemetry enqueue failed: {ex.Message}");
            }
        }

        /// <summary>解析遥测日志绝对路径（首次调用后缓存）。</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// 为给定 window 构建（或返回缓存的）/analytics 响应。window 归一为 1h|24h|7d|all（其余一律按 24h）。
        /// 结果按 window 缓存 30 秒。返回可直接写入 HTTP 响应的完整 JSON 字符串。
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
                // 聚合尽力而为：任何失败都返回格式正确的空报告而不是 500，端点始终可用，也不会被某一行坏数据卡住。
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

        /// <summary>内部：在调用线程上同步排空队列，保证读一致性。</summary>
        internal static void FlushSync() => FlushPending();

        /// <summary>
        /// 删除某个统计窗口内的遥测记录，窗口取值与 <see cref="BuildAnalyticsJson"/> 一致：
        /// <c>1h</c> / <c>24h</c> / <c>7d</c> / <c>all</c>。
        /// <c>all</c> 清空全部留存文件；其余窗口只删除 <c>ts &gt;= cutoff</c>（即落在窗口内）的记录，
        /// 并用幸存者重写主日志。总会清空 analytics 与 recommendation 缓存，保证下次读取是新的。
        /// 尽力而为——绝不向调用方抛异常。
        /// </summary>
        /// <returns>
        /// <c>{ success, window, removed, remaining }</c>；硬失败时返回 <c>{ success:false, error }</c>。
        /// </returns>
        public static object DeleteWindow(string window)
        {
            try
            {
                window = NormalizeWindow(window);
                // 取写锁前先在主线程解析日志路径，使落盘 worker 之后无需在非主线程碰 Application.dataPath。
                GetLogPath();

                int removed;
                int remaining;
                lock (_writeLock)
                {
                    // 在同一把锁内排空在途队列，使并发的 Record/落盘无法把我们即将删掉的行再追加回去。
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
                            // 无法解析的时间戳一律保留——只删能确信落在窗口内的记录
                            // （与 BuildAnalyticsJsonUncached 一致，那里也把无法解析的行排除在窗口聚合外）。
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

        /// <summary>内部：清除磁盘遥测日志及轮转副本，仅供测试使用。</summary>
        internal static void ResetForTests()
        {
            FlushPending();
            try
            {
                WipeAllFilesUnlocked();
            }
            catch { /* 忽略 */ }
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
        /// 删除主遥测文件与轮转副本。调用方必须持有 <see cref="_writeLock"/>（或如测试中那样为单线程）。
        /// </summary>
        private static void WipeAllFilesUnlocked()
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "UnitySkillsTelemetry*.jsonl"))
            {
                try { File.Delete(f); } catch { /* 忽略 */ }
            }
        }

        /// <summary>
        /// 用 <paramref name="records"/>（按时间顺序）重写主日志，并删除所有轮转副本，使留存集恰好是幸存记录。
        /// 调用方必须持有 <see cref="_writeLock"/>。
        /// </summary>
        private static void RewritePrimaryUnlocked(List<TelemetryRecord> records)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = _cachedPath ?? Path.Combine(dir, LogFileName);

            // 先删轮转文件，这样写入中途崩溃最多只剩新主文件，
            // 不会出现"旧轮转 + 写了一半的主文件"这种会重复计数的混合状态。
            for (int n = 1; n <= MaxRotatedFiles; n++)
            {
                var rotated = RotatedPath(n);
                if (File.Exists(rotated))
                {
                    try { File.Delete(rotated); } catch { /* 忽略 */ }
                }
            }

            if (records == null || records.Count == 0)
            {
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* 忽略 */ }
                }
                return;
            }

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs, SkillsCommon.Utf8NoBom))
            {
                foreach (var r in records)
                {
                    // 从解析后的记录重建 JSONL 行，避免把勉强反序列化成功的损坏原始行再吐出去。
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
        /// 不触发落盘地读取全部遥测行（调用方须已落盘并持有 <see cref="_writeLock"/>）。
        /// 时间顺序与 <see cref="ReadAll"/> 相同。
        /// </summary>
        private static List<TelemetryRecord> ReadAllUnlocked()
        {
            var records = new List<TelemetryRecord>();
            for (int n = MaxRotatedFiles; n >= 1; n--)
                ReadFileInto(RotatedPath(n), records);
            ReadFileInto(GetLogPath(), records);
            return records;
        }

        // ===== 写入路径 =====

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
            // 约定：ok=true 时完全省略 errorCode；ok=false 时保留该字段（即便值为 null）。
            if (!ok)
                payload["errorCode"] = errorCode;
            payload["ms"] = durationMs;
            return JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings);
        }

        private static void ScheduleFlush()
        {
            // 把多次追加合并成一次落盘任务。
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
        /// 把写队列排空到磁盘。调用方必须持有 <see cref="_writeLock"/>（或如测试中那样为单线程）。
        /// 常规落盘路径与 <see cref="DeleteWindow"/> 共用此方法，使并发的 Record 无法把即将被删的行再追加回去。
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

                // 依次搬移：.2 -> .3，.1 -> .2，主文件 -> .1
                for (int i = MaxRotatedFiles; i >= 1; i--)
                {
                    var src = i == 1 ? path : RotatedPath(i - 1);
                    var dst = RotatedPath(i);
                    if (File.Exists(dst))
                    {
                        try { File.Delete(dst); } catch { /* 忽略 */ }
                    }
                    if (File.Exists(src))
                    {
                        try { File.Move(src, dst); } catch { /* 忽略 */ }
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
        /// 返回 <c>&lt;project&gt;/Library</c>。在 Unity 编辑器尚未就绪时访问则回退到
        /// <c>Application.persistentDataPath</c>（与 SkillsAuditLog 一致）。
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
            catch { /* 本线程上 Unity API 未就绪，继续往下走 */ }

            try { return Application.persistentDataPath; }
            catch { return Path.GetTempPath(); }
        }

        // ===== 读取与聚合路径 =====

        /// <summary>解析后的遥测行，字段名绑定到 JSONL 的键。</summary>
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

        /// <summary>按技能累计的聚合量。</summary>
        private sealed class SkillAgg
        {
            public int Calls;
            public int Errors;
            public long TotalMs;
            public long MaxMs;

            // 仅统计成功调用的耗时。被拒调用（未知技能、校验失败、权限闸门）根本没进入技能体，
            // 其耗时说明不了该技能有多慢——"最慢"榜用这组数据而非上面的总计。
            public int OkCalls;
            public long OkTotalMs;
            public long OkMaxMs;

            public double AvgMs => Calls > 0 ? (double)TotalMs / Calls : 0.0;
            public double ErrorRate => Calls > 0 ? (double)Errors / Calls : 0.0;
            public double OkAvgMs => OkCalls > 0 ? (double)OkTotalMs / OkCalls : 0.0;
        }

        /// <summary>按 errorCode 累计的聚合量。</summary>
        private sealed class ErrAgg
        {
            public int Count;
            public readonly Dictionary<string, int> SkillCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// 把主文件与 3 份轮转副本中的全部遥测行按从旧到新读入内存。
        /// 与 SkillsAuditLog.ReadRecent（只读尾部）不同，这里是全量读取——/analytics 要聚合整个留存窗口（总计 ≤4MB）。
        /// 读取前先落盘待写项，使刚记录的调用可见。
        /// </summary>
        private static List<TelemetryRecord> ReadAll()
        {
            FlushSync();
            var records = new List<TelemetryRecord>();
            // 轮转把主文件搬到 .1，所以 .3 最旧、主文件最新。按此顺序（每个文件自上而下）读取即得全局时间顺序，
            // "recentErrors" 与 firstTs/lastTs 依赖这一点。
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
                        catch { continue; } // 跳过畸形行，不因单行失败而整次读取失败
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
            var errorRecords = new List<TelemetryRecord>(); // 按时间顺序（即读取顺序）

            int totalCalls = 0, okCalls = 0, errorCalls = 0;
            string firstTs = null, lastTs = null;

            foreach (var r in all)
            {
                if (!unbounded)
                {
                    if (!DateTime.TryParse(r.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        continue; // 无法定位到窗口内，排除
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

            // 易错榜：只有样本量足够（calls>=5）的技能才按错误率参与排名。
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

            // 最慢榜：只算成功调用，且成功次数 >=3，避免单个异常值霸榜。失败调用被排除，因为被拒
            // （未知技能、校验、权限闸门）的耗时计在路由层而非技能体上——算进来会让一个根本没执行过的
            // 名字被排成"慢技能"。
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

            // 最近 10 条错误，最新在前。
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
                default: return now.AddHours(-24); // "24h"（默认）
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
