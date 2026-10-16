using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// A bounded, durable history of managed Unity log callbacks. The threaded callback deliberately
    /// uses only CLR APIs: Unity objects and editor APIs are not safe to touch from worker threads.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleLogHistoryService
    {
        internal const int MaxEntries = 50000;
        internal const long MaxBytes = 64L * 1024L * 1024L;
        internal const int DefaultReadLimit = 100;
        internal const int MaxReadLimit = 1000;

        private const string RelativeLogPath = "Library/UnitySkills/console_log_history.jsonl";
        private const string RelativeStatePath = "Library/UnitySkills/console_log_history_state.json";
        private const long FlushIntervalTicks = TimeSpan.TicksPerSecond;
        private const string SessionKeyId = "UnitySkills_ConsoleLogHistorySessionId";
        private const string SessionKeyCaptureStart = "UnitySkills_ConsoleLogHistoryCaptureStart";
        private const string SessionKeyCapturing = "UnitySkills_ConsoleLogHistoryCapturing";
        private static readonly object SyncRoot = new object();
        private static readonly List<ConsoleLogHistoryRecord> Records = new List<ConsoleLogHistoryRecord>();
        private static readonly List<ConsoleLogHistoryRecord> PendingPersistence = new List<ConsoleLogHistoryRecord>();
        private static readonly HashSet<string> NativeKeys = new HashSet<string>(StringComparer.Ordinal);
        private static string _sessionId;
        private static string _logPath;
        private static string _statePath;
        private static string _editorLogPath;
        private static long _seq;
        private static long _bytes;
        private static long _dropped;
        private static long _corruptRows;
        private static long _generation;
        private static long _captureStartSeq;
        private static bool _capturing;
        private static bool _dirty;
        private static bool _needsCompact;
        private static bool _persistenceFailed;
        private static long _nextFlushUtcTicks;

        static ConsoleLogHistoryService()
        {
            try
            {
                // This constructor is run by Unity on the editor thread. The callback below must not
                // resolve this path or access Application/Editor APIs after it is registered.
                _logPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativeLogPath));
                _statePath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", RelativeStatePath));
                _editorLogPath = Application.consoleLogPath;
                LoadPersistedHistory();
                _sessionId = SessionState.GetString(SessionKeyId, string.Empty);
                if (string.IsNullOrEmpty(_sessionId))
                {
                    _sessionId = Guid.NewGuid().ToString("N");
                    SessionState.SetString(SessionKeyId, _sessionId);
                }
                long.TryParse(SessionState.GetString(SessionKeyCaptureStart, "0"), out _captureStartSeq);
                _capturing = SessionState.GetBool(SessionKeyCapturing, false);
                Application.logMessageReceivedThreaded += OnLogMessageReceivedThreaded;
                EditorApplication.update += FlushWhenDue;
                AssemblyReloadEvents.beforeAssemblyReload += FlushNow;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Console log history initialization failed: " + ex.Message);
            }
        }

        internal sealed class ConsoleLogHistoryRecord
        {
            public long seq;
            public string type;
            public string message;
            public string stackTrace;
            public string utc;
            public bool timeKnown;
            public string sessionId;
            public string source;
            [JsonProperty] internal string nativeKey;
            internal long ApproximateBytes;
        }

        private sealed class PersistedState
        {
            public long seq;
            public long dropped;
            public long generation;
        }

        internal sealed class ConsoleLogHistoryWindow
        {
            public List<ConsoleLogHistoryRecord> logs;
            public long cursor;
            public long snapshotSeq;
            public long totalMatched;
            public bool hasMore;
            public bool complete;
            public long dropped;
            public object coverage;
        }

        internal sealed class ConsoleLogHistoryPage
        {
            public List<object> logs;
            public int count;
            public string cursor;
            public string nextCursor;
            public long totalMatched;
            public bool hasMore;
            public bool complete;
            public long dropped;
            public object coverage;
            internal string errorParameter;
        }

        internal static long GetCurrentSeq()
        {
            lock (SyncRoot)
                return _seq;
        }

        internal static void StartCaptureSession()
        {
            lock (SyncRoot)
            {
                _capturing = true;
                _captureStartSeq = _seq;
                SessionState.SetBool(SessionKeyCapturing, true);
                SessionState.SetString(SessionKeyCaptureStart, _captureStartSeq.ToString(CultureInfo.InvariantCulture));
            }
        }

        internal static int StopCaptureSession()
        {
            lock (SyncRoot)
            {
                _capturing = false;
                SessionState.SetBool(SessionKeyCapturing, false);
                return Records.Count(record => record.seq > _captureStartSeq);
            }
        }

        internal static bool IsCapturing
        {
            get { lock (SyncRoot) return _capturing; }
        }

        internal static long CaptureStartSeq
        {
            get { lock (SyncRoot) return _captureStartSeq; }
        }

        internal static void Clear()
        {
            lock (SyncRoot)
            {
                Records.Clear();
                NativeKeys.Clear();
                PendingPersistence.Clear();
                _bytes = 0;
                _dropped = 0;
                _generation++;
                _captureStartSeq = _seq;
                SessionState.SetString(SessionKeyCaptureStart, _captureStartSeq.ToString(CultureInfo.InvariantCulture));
                _dirty = true;
                _needsCompact = true;
            }
        }

        /// <summary>Imports old native Console entries once. They have no trustworthy timestamp, so utc stays null.</summary>
        internal static void ImportNativeEntries(IEnumerable<DebugSkills.LogEntryInfo> entries)
        {
            if (entries == null) return;
            lock (SyncRoot)
            {
                foreach (var entry in entries.Reverse())
                {
                    if (entry == null) continue;
                    string key = NativeKey(entry.type, entry.message, entry.file, entry.line);
                    if (NativeKeys.Contains(key) || HasManagedEquivalentLocked(entry.type, entry.message))
                        continue;

                    NativeKeys.Add(key);
                    AddLocked(new ConsoleLogHistoryRecord
                    {
                        type = entry.type ?? "Log",
                        message = entry.message ?? string.Empty,
                        stackTrace = null,
                        utc = null,
                        timeKnown = false,
                        sessionId = "native",
                        source = "native",
                        nativeKey = key,
                    }, persistAppend: true);
                }
            }
        }

        /// <summary>
        /// Returns raw records in ascending seq order after a caller's saved cursor. Runtime jobs use
        /// this rather than refreshing the Console, so their evidence is tied to one fixed snapshot.
        /// </summary>
        internal static ConsoleLogHistoryWindow ReadWindow(long afterSeq, long throughSeq = 0,
            int limit = MaxReadLimit, bool includeStackTrace = true)
        {
            limit = Math.Max(1, Math.Min(MaxReadLimit, limit));
            lock (SyncRoot)
            {
                long snapshot = throughSeq > 0 ? Math.Min(throughSeq, _seq) : _seq;
                long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
                var matching = Records.Where(record => record.seq > afterSeq && record.seq <= snapshot).ToList();
                bool hasMore = matching.Count > limit;
                var returned = matching.Take(limit).Select(record => CopyRecord(record, includeStackTrace)).ToList();
                return new ConsoleLogHistoryWindow
                {
                    logs = returned,
                    cursor = returned.Count > 0 ? returned[returned.Count - 1].seq : afterSeq,
                    snapshotSeq = snapshot,
                    totalMatched = matching.Count,
                    hasMore = hasMore,
                    complete = IsCompleteLocked(afterSeq, matching),
                    dropped = _dropped,
                    coverage = CoverageLocked(oldest, snapshot, matching.Count(record => !record.timeKnown))
                };
            }
        }

        internal static ConsoleLogHistoryWindow GetWindow(string fromUtc, string toUtc, int limit = MaxReadLimit,
            bool includeStackTrace = true)
        {
            DateTime? from = ParseUtcOrNull(fromUtc);
            DateTime? to = ParseUtcOrNull(toUtc);
            limit = Math.Max(1, Math.Min(MaxReadLimit, limit));
            lock (SyncRoot)
            {
                long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
                var matching = FilterByUtc(Records, from, to).ToList();
                bool hasMore = matching.Count > limit;
                var returned = matching.Take(limit).Select(record => CopyRecord(record, includeStackTrace)).ToList();
                return new ConsoleLogHistoryWindow
                {
                    logs = returned,
                    cursor = returned.Count > 0 ? returned[returned.Count - 1].seq : _seq,
                    snapshotSeq = _seq,
                    totalMatched = matching.Count,
                    hasMore = hasMore,
                    complete = IsCompleteLocked(0, Records),
                    dropped = _dropped,
                    coverage = CoverageLocked(oldest, _seq, Records.Count(record => !record.timeKnown))
                };
            }
        }

        internal static List<ConsoleLogHistoryRecord> SnapshotAll(bool includeStackTrace, out long dropped, out bool complete, long afterSeq = 0)
        {
            lock (SyncRoot)
            {
                dropped = _dropped;
                var matching = Records.Where(record => record.seq > afterSeq).ToList();
                long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
                complete = IsCompleteLocked(afterSeq, matching);
                return matching.Select(record => CopyRecord(record, includeStackTrace)).ToList();
            }
        }

        internal static ConsoleLogHistoryPage ReadPage(int targetMask, string filter, int limit, bool collapse,
            string fromUtc, string toUtc, string groupId, string cursor, bool includeStackTrace, long afterSeq = 0)
        {
            limit = Math.Max(1, Math.Min(MaxReadLimit, limit));
            if (!TryParseCursor(cursor, out var cursorGeneration, out var snapshot, out var offset))
                return ErrorPage("cursor");
            DateTime? from = ParseUtcOrNull(fromUtc);
            DateTime? to = ParseUtcOrNull(toUtc);
            lock (SyncRoot)
            {
                if (!string.IsNullOrEmpty(cursor) && cursorGeneration != _generation)
                    return ErrorPage("cursor");
                if (string.IsNullOrEmpty(cursor))
                {
                    snapshot = _seq;
                    offset = 0;
                }
                else
                {
                    snapshot = Math.Min(snapshot, _seq);
                }

                long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
                var matching = Records
                    .Where(record => record.seq > afterSeq && record.seq <= snapshot)
                    .Where(record => MatchesMask(record.type, targetMask))
                    .Where(record => MatchesText(record, filter))
                    .Where(record => MatchesUtc(record, from, to))
                    .Where(record => string.IsNullOrEmpty(groupId) || string.Equals(GroupId(record), groupId, StringComparison.Ordinal))
                    .OrderByDescending(record => record.seq)
                    .ToList();

                long totalMatched = matching.LongCount();

                List<object> displayed;
                long nextOffset;
                bool hasMore;
                if (collapse)
                {
                    var groups = matching.GroupBy(GroupId).Select(group => new
                    {
                        groupId = group.Key,
                        type = group.First().type,
                        message = group.First().message,
                        stackTrace = includeStackTrace ? group.First().stackTrace : null,
                        count = group.All(record => record.source == "managed") ? (int?)group.Count() : null,
                        countKnown = group.All(record => record.source == "managed"),
                        firstUtc = group.Where(record => record.timeKnown).OrderBy(record => record.utc).Select(record => record.utc).FirstOrDefault(),
                        lastUtc = group.Where(record => record.timeKnown).OrderByDescending(record => record.utc).Select(record => record.utc).FirstOrDefault(),
                        firstSeq = group.Min(record => record.seq),
                        lastSeq = group.Max(record => record.seq),
                        timeKnown = group.All(record => record.timeKnown),
                        source = group.All(record => record.source == "managed") ? "managed" : "mixed",
                    }).OrderByDescending(group => group.lastSeq).ToList();
                    var page = groups.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit).ToList();
                    displayed = page.Cast<object>().ToList();
                    nextOffset = offset + page.Count;
                    hasMore = nextOffset < groups.Count;
                }
                else
                {
                    var page = matching.Skip((int)Math.Min(offset, int.MaxValue)).Take(limit).ToList();
                    displayed = page.Select(record => (object)CopyRecord(record, includeStackTrace)).ToList();
                    nextOffset = offset + page.Count;
                    hasMore = nextOffset < matching.Count;
                }

                return new ConsoleLogHistoryPage
                {
                    logs = displayed,
                    count = displayed.Count,
                    cursor = MakeCursor(_generation, snapshot, offset),
                    nextCursor = hasMore ? MakeCursor(_generation, snapshot, nextOffset) : null,
                    totalMatched = totalMatched,
                    hasMore = hasMore,
                    complete = IsCompleteLocked(afterSeq, matching),
                    dropped = _dropped,
                    coverage = CoverageLocked(oldest, snapshot, Records.Count(record => !record.timeKnown))
                };
            }
        }

        private static ConsoleLogHistoryPage ErrorPage(string parameter)
        {
            return new ConsoleLogHistoryPage { logs = new List<object>(), count = 0, complete = false, errorParameter = parameter, coverage = new { error = "Invalid " + parameter } };
        }

        private static void OnLogMessageReceivedThreaded(string message, string stackTrace, LogType type)
        {
            // No Unity API, SessionState, SkillsLogger, or other editor-affine operation is permitted here.
            try
            {
                lock (SyncRoot)
                {
                    AddLocked(new ConsoleLogHistoryRecord
                    {
                        type = type.ToString(),
                        message = message ?? string.Empty,
                        stackTrace = stackTrace ?? string.Empty,
                        utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        timeKnown = true,
                        sessionId = _sessionId,
                        source = "managed",
                    }, persistAppend: true);
                }
            }
            catch
            {
                // Logging an error here would recursively invoke this callback.
            }
        }

        private static void AddLocked(ConsoleLogHistoryRecord record, bool persistAppend)
        {
            record.seq = ++_seq;
            // The callback does not touch SessionState; the durable JSONL seq remains the reload authority.
            record.ApproximateBytes = ApproximateBytes(record);
            Records.Add(record);
            _bytes += record.ApproximateBytes;
            bool evicted = false;
            while (Records.Count > MaxEntries || _bytes > MaxBytes)
            {
                var removed = Records[0];
                Records.RemoveAt(0);
                _bytes -= removed.ApproximateBytes;
                _dropped++;
                evicted = true;
            }
            if (persistAppend)
            {
                _dirty = true;
                PendingPersistence.Add(record);
                _needsCompact |= evicted;
            }
        }

        private static void LoadPersistedHistory()
        {
            lock (SyncRoot)
            {
                LoadStateLocked();
                if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;
                try
                {
                    foreach (var line in File.ReadLines(_logPath))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var record = JsonConvert.DeserializeObject<ConsoleLogHistoryRecord>(line, SkillsCommon.JsonSettings);
                            if (record == null || record.seq <= 0) continue;
                            record.ApproximateBytes = ApproximateBytes(record);
                            Records.Add(record);
                            _bytes += record.ApproximateBytes;
                            _seq = Math.Max(_seq, record.seq);
                            if (!string.IsNullOrEmpty(record.nativeKey)) NativeKeys.Add(record.nativeKey);
                        }
                        catch
                        {
                            // A torn final JSONL line is expected after an interrupted editor exit. Keep
                            // every earlier line instead of treating the whole durable history as corrupt.
                            _corruptRows++;
                        }
                    }
                }
                catch { /* A temporary file-system failure leaves the service live for new callbacks. */ }
                _needsCompact = TrimLocked();
                _dirty = _needsCompact;
            }
        }

        private static bool TrimLocked()
        {
            bool changed = false;
            while (Records.Count > MaxEntries || _bytes > MaxBytes)
            {
                var removed = Records[0];
                Records.RemoveAt(0);
                _bytes -= removed.ApproximateBytes;
                _dropped++;
                changed = true;
            }
            return changed;
        }

        private static void FlushWhenDue()
        {
            if (!_dirty || DateTime.UtcNow.Ticks < _nextFlushUtcTicks) return;
            FlushNow();
        }

        private static void FlushNow()
        {
            lock (SyncRoot)
            {
                if (!_dirty) return;
                try
                {
                    if (_needsCompact)
                    {
                        RewritePersistenceLocked();
                    }
                    else
                    {
                        AppendPendingPersistenceLocked();
                    }
                    WriteStateLocked();
                    PendingPersistence.Clear();
                    _needsCompact = false;
                    _dirty = false;
                    _persistenceFailed = false;
                    _nextFlushUtcTicks = DateTime.UtcNow.Ticks + FlushIntervalTicks;
                }
                catch
                {
                    // A later editor tick retries. Do not log from here because this can be entered
                    // while Unity itself is processing a log callback.
                    _persistenceFailed = true;
                }
            }
        }

        private static void RewritePersistenceLocked()
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            string directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllLines(_logPath, Records.Select(record => JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings)), new UTF8Encoding(false));
        }

        private static void AppendPendingPersistenceLocked()
        {
            if (PendingPersistence.Count == 0 || string.IsNullOrEmpty(_logPath)) return;
            string directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllLines(_logPath, PendingPersistence.Select(record => JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings)), new UTF8Encoding(false));
        }

        private static void LoadStateLocked()
        {
            if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return;
            try
            {
                var state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(_statePath), SkillsCommon.JsonSettings);
                if (state == null) return;
                _seq = Math.Max(_seq, state.seq);
                _dropped = Math.Max(0, state.dropped);
                _generation = Math.Max(0, state.generation);
            }
            catch { }
        }

        private static void WriteStateLocked()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            string directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_statePath, JsonConvert.SerializeObject(new PersistedState { seq = _seq, dropped = _dropped, generation = _generation }, Formatting.None, SkillsCommon.JsonSettings), new UTF8Encoding(false));
        }

        private static bool HasManagedEquivalentLocked(string type, string message)
        {
            return Records.Any(record => record.source == "managed" && string.Equals(record.type, type, StringComparison.Ordinal) &&
                string.Equals(record.message, message ?? string.Empty, StringComparison.Ordinal));
        }

        private static ConsoleLogHistoryRecord CopyRecord(ConsoleLogHistoryRecord record, bool includeStackTrace)
        {
            return new ConsoleLogHistoryRecord
            {
                seq = record.seq, type = record.type, message = record.message,
                stackTrace = includeStackTrace ? record.stackTrace : null,
                utc = record.utc, timeKnown = record.timeKnown, sessionId = record.sessionId, source = record.source
            };
        }

        private static IEnumerable<ConsoleLogHistoryRecord> FilterByUtc(IEnumerable<ConsoleLogHistoryRecord> source, DateTime? from, DateTime? to)
        {
            return source.Where(record => MatchesUtc(record, from, to));
        }

        private static bool MatchesUtc(ConsoleLogHistoryRecord record, DateTime? from, DateTime? to)
        {
            if (!from.HasValue && !to.HasValue) return true;
            if (!record.timeKnown || !DateTime.TryParse(record.utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)) return false;
            return (!from.HasValue || utc >= from.Value) && (!to.HasValue || utc < to.Value);
        }

        private static bool MatchesText(ConsoleLogHistoryRecord record, string filter)
        {
            return string.IsNullOrEmpty(filter) || (record.message ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (record.stackTrace ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesMask(string type, int mask)
        {
            if (string.Equals(type, "Warning", StringComparison.OrdinalIgnoreCase)) return (mask & DebugSkills.WarningModeMask) != 0;
            if (string.Equals(type, "Error", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Exception", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Assert", StringComparison.OrdinalIgnoreCase)) return (mask & DebugSkills.ErrorModeMask) != 0;
            return (mask & DebugSkills.LogModeMask) != 0;
        }

        private static string GroupId(ConsoleLogHistoryRecord record)
        {
            string text = (record.type ?? string.Empty) + "\u001f" + (record.message ?? string.Empty) + "\u001f" + (record.stackTrace ?? string.Empty);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string NativeKey(string type, string message, string file, int line)
        {
            return string.Concat(type ?? string.Empty, "\u001f", message ?? string.Empty, "\u001f", file ?? string.Empty, "\u001f", line.ToString(CultureInfo.InvariantCulture));
        }

        private static long ApproximateBytes(ConsoleLogHistoryRecord record)
        {
            return Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings)) + Environment.NewLine.Length;
        }

        private static object CoverageLocked(long oldest, long snapshot, int unknownTimeCount)
        {
            return new { oldestSeq = oldest, newestSeq = _seq, snapshotSeq = snapshot, retained = Records.Count, capacity = MaxEntries, maxBytes = MaxBytes, unknownTimeCount, nativeTime = "unknown", nativeMessageMayBeTruncated = true, fullTextGuaranteedFor = "managed", corruptRows = _corruptRows, durabilityIssue = _persistenceFailed, editorLogPath = _editorLogPath, editorLogAvailable = !string.IsNullOrEmpty(_editorLogPath) && File.Exists(_editorLogPath), editorLogSource = "Editor.log is an external fallback; it is not imported into cursor history." };
        }

        private static bool IsCompleteLocked(long afterSeq, IEnumerable<ConsoleLogHistoryRecord> matching)
        {
            long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
            return afterSeq + 1 >= oldest && _corruptRows == 0 && !_persistenceFailed && !matching.Any(record => !record.timeKnown);
        }

        private static DateTime? ParseUtcOrNull(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : (DateTime?)null;
        }

        private static bool TryParseCursor(string cursor, out long generation, out long snapshot, out long offset)
        {
            generation = 0; snapshot = 0; offset = 0;
            if (string.IsNullOrEmpty(cursor)) return true;
            var parts = cursor.Split(':');
            return parts.Length == 4 && parts[0] == "v1" && long.TryParse(parts[1], out generation) &&
                long.TryParse(parts[2], out snapshot) && long.TryParse(parts[3], out offset) && snapshot >= 0 && offset >= 0;
        }

        private static string MakeCursor(long generation, long snapshot, long offset)
        {
            return string.Concat("v1:", generation.ToString(CultureInfo.InvariantCulture), ":", snapshot.ToString(CultureInfo.InvariantCulture), ":", offset.ToString(CultureInfo.InvariantCulture));
        }

        internal static void ResetForTests(string path = null)
        {
            lock (SyncRoot)
            {
                _logPath = path ?? _logPath;
                _statePath = string.IsNullOrEmpty(_logPath) ? null : _logPath + ".state";
                Records.Clear(); NativeKeys.Clear(); PendingPersistence.Clear(); _seq = 0; _bytes = 0; _dropped = 0; _corruptRows = 0; _generation = 0; _captureStartSeq = 0; _capturing = false;
                _dirty = false; _needsCompact = false;
                if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath)) File.Delete(_logPath);
                if (!string.IsNullOrEmpty(_statePath) && File.Exists(_statePath)) File.Delete(_statePath);
            }
        }

        internal static IDisposable BeginTestScope(string path)
        {
            return new TestScope(path);
        }

        private sealed class TestScope : IDisposable
        {
            private readonly string _oldPath;
            private readonly string _oldStatePath;
            private readonly List<ConsoleLogHistoryRecord> _oldRecords;
            private readonly HashSet<string> _oldNativeKeys;
            private readonly long _oldSeq;
            private readonly long _oldBytes;
            private readonly long _oldDropped;
            private readonly long _oldCorruptRows;
            private readonly long _oldGeneration;
            private readonly long _oldCaptureStartSeq;
            private readonly bool _oldCapturing;
            private readonly bool _oldDirty;
            private readonly bool _oldNeedsCompact;
            private readonly bool _oldPersistenceFailed;
            private readonly List<ConsoleLogHistoryRecord> _oldPendingPersistence;
            private readonly long _oldNextFlushUtcTicks;

            internal TestScope(string path)
            {
                lock (SyncRoot)
                {
                    _oldPath = _logPath; _oldStatePath = _statePath;
                    _oldRecords = new List<ConsoleLogHistoryRecord>(Records);
                    _oldNativeKeys = new HashSet<string>(NativeKeys, StringComparer.Ordinal);
                    _oldSeq = _seq; _oldBytes = _bytes; _oldDropped = _dropped; _oldCorruptRows = _corruptRows; _oldGeneration = _generation;
                    _oldCaptureStartSeq = _captureStartSeq; _oldCapturing = _capturing; _oldDirty = _dirty; _oldNeedsCompact = _needsCompact; _oldPersistenceFailed = _persistenceFailed;
                    _oldPendingPersistence = new List<ConsoleLogHistoryRecord>(PendingPersistence); _oldNextFlushUtcTicks = _nextFlushUtcTicks;
                }
                ResetForTests(path);
            }

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    // This scope only ever owns the temporary path installed by ResetForTests.
                    // Delete it before restoring the live history path, so editor tests cannot
                    // accumulate artifacts under the system temp directory.
                    if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath)) File.Delete(_logPath);
                    if (!string.IsNullOrEmpty(_statePath) && File.Exists(_statePath)) File.Delete(_statePath);
                    _logPath = _oldPath; _statePath = _oldStatePath;
                    Records.Clear(); Records.AddRange(_oldRecords);
                    NativeKeys.Clear(); foreach (var key in _oldNativeKeys) NativeKeys.Add(key);
                    _seq = _oldSeq; _bytes = _oldBytes; _dropped = _oldDropped; _corruptRows = _oldCorruptRows; _generation = _oldGeneration;
                    PendingPersistence.Clear(); PendingPersistence.AddRange(_oldPendingPersistence);
                    _captureStartSeq = _oldCaptureStartSeq; _capturing = _oldCapturing; _dirty = _oldDirty; _needsCompact = _oldNeedsCompact; _persistenceFailed = _oldPersistenceFailed; _nextFlushUtcTicks = _oldNextFlushUtcTicks;
                }
            }
        }

        internal static void FlushForTests() => FlushNow();

        internal static void AppendCorruptTailForTests()
        {
            FlushNow();
            lock (SyncRoot)
            {
                if (!string.IsNullOrEmpty(_logPath))
                    File.AppendAllText(_logPath, "{corrupt-tail" + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        internal static void ReloadForTests()
        {
            FlushNow();
            lock (SyncRoot)
            {
                Records.Clear(); NativeKeys.Clear(); PendingPersistence.Clear(); _seq = 0; _bytes = 0; _dropped = 0; _corruptRows = 0; _generation = 0; _dirty = false; _needsCompact = false; _persistenceFailed = false;
                LoadPersistedHistory();
            }
        }

        internal static void RecordForTests(string message, string stackTrace = "", LogType type = LogType.Log, DateTime? utc = null)
        {
            lock (SyncRoot)
            {
                AddLocked(new ConsoleLogHistoryRecord { type = type.ToString(), message = message ?? string.Empty, stackTrace = stackTrace ?? string.Empty,
                    utc = (utc ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), timeKnown = true, sessionId = _sessionId, source = "managed" }, true);
            }
        }
    }
}

// Producer:Betsy
