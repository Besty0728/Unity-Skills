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
using UnitySkills.Internal;

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
        // A full history evicts on every add. Evicted rows stay in the file until this many have piled up, so the file
        // is rewritten once per quarter turnover instead of on every flush (issue #65: a 50,000-row rewrite each second).
        internal const int CompactEvictionCount = MaxEntries / 4;
        internal const long CompactEvictionBytes = MaxBytes / 4;
        // A reload parses only this much of the file's end; older rows are read when a reader first needs them.
        private const long TailReadBytes = 1L << 20;

        private const string RelativeLogPath = "Library/UnitySkills/console_log_history.jsonl";
        private const string RelativeStatePath = "Library/UnitySkills/console_log_history_state.json";
        private const long FlushIntervalTicks = TimeSpan.TicksPerSecond;
        private const int ReadChunkBytes = 1 << 16;
        // How far the persisted seq reservation runs ahead of the live counter. A kill can lose at most the records
        // since the last flush; the next load resumes above the reservation, so none of their seqs is handed out again.
        internal const long SeqReservationBlock = 10000;
        private const string SessionKeyId = "UnitySkills_ConsoleLogHistorySessionId";
        private const string SessionKeyCaptureStart = "UnitySkills_ConsoleLogHistoryCaptureStart";
        private const string SessionKeyCapturing = "UnitySkills_ConsoleLogHistoryCapturing";
        private static readonly object SyncRoot = new object();
        private static readonly List<ConsoleLogHistoryRecord> Records = new List<ConsoleLogHistoryRecord>();
        private static readonly List<ConsoleLogHistoryRecord> PendingPersistence = new List<ConsoleLogHistoryRecord>();
        private static readonly HashSet<string> NativeKeys = new HashSet<string>(StringComparer.Ordinal);
        // Retained managed records by type + "\u001f" + message, so a native import finds a managed twin without a scan.
        private static readonly Dictionary<string, int> ManagedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        // One serializer for every row a load reads, configured as JsonConvert.DeserializeObject configures its own per call.
        private static readonly JsonSerializer RowSerializer = CreateRowSerializer();
        private static string _sessionId;
        private static string _logPath;
        private static string _statePath;
        private static string _editorLogPath;
        private static long _seq;
        private static long _bytes;
        private static long _dropped;
        private static long _corruptRows;
        // Rows counted in _corruptRows that are still in the file; the next compaction drops them.
        private static long _uncompactedCorruptRows;
        // Records with seq at or below this may be missing (unreadable rows, an unclean stop). Persisted and monotonic.
        private static long _incompleteThroughSeq;
        // Persisted upper bound of every seq this writer has handed out; equal to _seq after a final flush.
        private static long _reservedSeq;
        // Set by the final flush of a reloading or quitting domain: later records are never served, so they reserve nothing.
        private static bool _finalFlushDone;
        private static long _generation;
        private static long _captureStartSeq;
        private static bool _capturing;
        private static bool _dirty;
        private static bool _needsCompact;
        // Evicted from memory since the last rewrite but still in the file; the next load trims them again.
        private static long _evictedSinceCompact;
        private static long _evictedBytesSinceCompact;
        private static bool _persistenceFailed;
        private static long _nextFlushUtcTicks;
        // Rows and bytes of the file exactly as this writer left it, persisted so the next load can read only its tail;
        // -1 rows while the file may hold anything else (unreadable rows, a failed write, a clear not yet rewritten).
        private static long _fileRows = -1;
        private static long _fileBytes;
        // The start of the file a tail-only load left unparsed; null once every row of the file has been read.
        private static UnparsedHead _head;
        private static long _tailBytes = TailReadBytes;
        private static long _parsedRows;

        static ConsoleLogHistoryService()
        {
            try
            {
                // An asset import worker loads this assembly against the same Library folder. Its records are not this
                // editor's console, and a second writer with its own seq counter tears lines and repeats seqs.
                if (ImportWorkerGate.IsWorkerProcess)
                    return;

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
                // Final flushes: beforeAssemblyReload and quitting end the served history; DomainUnload, the last
                // managed callback of a reloading domain, also persists what other reload handlers logged after them.
                AssemblyReloadEvents.beforeAssemblyReload += FinalFlush;
                EditorApplication.quitting += FinalFlush;
                AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;
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
            public long reservedSeq;
            public long incompleteThroughSeq;
            public long corruptRows;
            // What the file holds, so a load can read only its tail; null while it may differ, absent in older states.
            public long? fileRows;
            public long? fileBytes;
            // The file's oldest rows that are already evicted; the retained history starts right after them.
            public long? fileEvictedRows;
            public long? fileEvictedBytes;
        }

        /// <summary>
        /// Bytes [0, End) of the file, which a tail-only load did not parse. Its first rows are evicted ones the file keeps
        /// until compaction; from <see cref="RetainedOffset"/> on, <see cref="RetainedRows"/> rows still belong to the history.
        /// </summary>
        private sealed class UnparsedHead
        {
            internal long End;
            internal long RetainedOffset;
            internal long RetainedRows;
            // The first seq the tail load kept: every row of the head is older.
            internal long LoadedFromSeq;
            // Seq of the row at RetainedOffset; 0 until read.
            internal long OldestSeq;
            // A cached slice of the file, so evicting head rows one at a time does not reopen the file for each row.
            internal byte[] Chunk;
            internal long ChunkStart;
            internal int ChunkLength;
        }

        /// <summary>The load rules for one line at a time: unreadable and out-of-order rows are counted and mark the gap they hide.</summary>
        private sealed class RowReader
        {
            internal long LastSeq;
            internal bool UnreadableRowPending;
            // False once a line was skipped: the file then differs from anything this writer produces.
            internal bool Clean = true;

            internal ConsoleLogHistoryRecord Read(string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    Clean = false;
                    return null;
                }
                var record = ParseRow(line);
                if (record == null || record.seq <= 0)
                {
                    // A torn line (an append a kill cut short) hides a record whose seq lies before the next
                    // readable one. Keep every other line instead of treating the whole history as corrupt.
                    CountCorruptRowLocked();
                    UnreadableRowPending = true;
                    Clean = false;
                    return null;
                }

                if (record.seq <= LastSeq)
                {
                    // A repeated or backwards seq means a second writer; windows reaching back to it are
                    // ambiguous. Keeping seqs unique and ascending is what cursors rely on.
                    CountCorruptRowLocked();
                    MarkIncompleteThroughLocked(LastSeq);
                    Clean = false;
                    return null;
                }

                if (UnreadableRowPending)
                {
                    MarkIncompleteThroughLocked(record.seq - 1);
                    UnreadableRowPending = false;
                }
                LastSeq = record.seq;
                return record;
            }
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
                EnsureLoadedAfterLocked(_captureStartSeq);
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
                ManagedCounts.Clear();
                PendingPersistence.Clear();
                _head = null;
                // Until the rewrite below lands, the file still holds the cleared rows.
                _fileRows = -1;
                _bytes = 0;
                _dropped = 0;
                // The rewrite below holds no rows at all, so no unreadable row survives it.
                _corruptRows = 0;
                _uncompactedCorruptRows = 0;
                _evictedSinceCompact = 0;
                _evictedBytesSinceCompact = 0;
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
                // Native keys and managed twins of the rows only the file holds count as well.
                EnsureCompleteLocked();
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
                EnsureLoadedAfterLocked(afterSeq);
                long snapshot = throughSeq > 0 ? Math.Min(throughSeq, _seq) : _seq;
                long oldest = OldestSeqLocked();
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
                EnsureLoadedAfterLocked(0);
                long oldest = OldestSeqLocked();
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
                EnsureLoadedAfterLocked(afterSeq);
                dropped = _dropped;
                var matching = Records.Where(record => record.seq > afterSeq).ToList();
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

                // Coverage counts unknown-time rows over the whole history, not only the rows after afterSeq.
                EnsureLoadedAfterLocked(0);
                long oldest = OldestSeqLocked();
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
            // The callback does not touch SessionState; the durable JSONL seq remains the reload authority. A seq past
            // the persisted reservation extends it before any reader can see the record (rare: the flush keeps half a
            // block of headroom), so a kill can never lead to its reuse. CLR-only file I/O, safe on this thread.
            if (_seq > _reservedSeq && !_finalFlushDone)
                ReserveSeqLocked();
            record.ApproximateBytes = ApproximateBytes(record);
            AppendRecordLocked(record);
            // After a tail-only load the oldest rows are still in the file; evicting them reads their sizes from it.
            EvictOverCapLocked();
            if (persistAppend)
            {
                _dirty = true;
                PendingPersistence.Add(record);
            }
        }

        private static void LoadPersistedHistory()
        {
            lock (SyncRoot)
            {
                bool stateReadable = LoadStateLocked(out var state);
                var reader = new RowReader();
                bool readComplete = true;
                long lastSeq = 0;
                if (!TryLoadTailLocked(state, out lastSeq))
                {
                    readComplete = LoadAllLocked(reader);
                    lastSeq = reader.LastSeq;
                }

                _seq = Math.Max(_seq, lastSeq);
                if (_reservedSeq > _seq || (!stateReadable && lastSeq > 0))
                {
                    // The last writer stopped without its final flush (a kill or a crash), or left no readable state:
                    // it may have handed out seqs that never reached the file. Resume above every one of them.
                    _seq = Math.Max(_reservedSeq, _seq + (stateReadable ? 0 : SeqReservationBlock));
                    MarkIncompleteThroughLocked(_seq);
                }
                if (reader.UnreadableRowPending || !readComplete)
                    MarkIncompleteThroughLocked(_seq);

                // Compaction rewrites the file from memory, so it drops unreadable rows, but only after a complete read;
                // it also turns a file the state cannot describe back into one the next load reads only the tail of.
                bool trimmed = EvictOverCapLocked();
                _needsCompact = trimmed || (readComplete && (_uncompactedCorruptRows > 0 || _fileRows < 0));
                _dirty = _needsCompact;
                _finalFlushDone = false;
                ReserveSeqLocked();
            }
        }

        /// <summary>
        /// Loads only the rows in the last <see cref="_tailBytes"/> of the file, when the state shows the file is exactly what
        /// this writer left (same length, no unreadable rows). Older rows stay in the file until a reader needs them.
        /// </summary>
        private static bool TryLoadTailLocked(PersistedState state, out long lastSeq)
        {
            lastSeq = 0;
            if (state == null || !state.fileRows.HasValue || !state.fileBytes.HasValue || !state.fileEvictedRows.HasValue ||
                !state.fileEvictedBytes.HasValue || string.IsNullOrEmpty(_logPath)) return false;
            long fileRows = state.fileRows.Value, fileBytes = state.fileBytes.Value;
            long evictedRows = state.fileEvictedRows.Value, evictedBytes = state.fileEvictedBytes.Value;
            // A file within the tail is read whole, as every load did before.
            if (fileBytes <= _tailBytes || evictedRows < 0 || evictedRows > fileRows || evictedBytes < 0 || evictedBytes > fileBytes ||
                (evictedRows == 0) != (evictedBytes == 0)) return false;

            var tail = new List<ConsoleLogHistoryRecord>();
            long tailStart = -1;
            try
            {
                using (var stream = OpenLogForRead())
                {
                    // Another length means rows the state does not describe: a kill between an append and its state write.
                    if (stream.Length != fileBytes) return false;
                    if (evictedBytes > 0)
                    {
                        stream.Seek(evictedBytes - 1, SeekOrigin.Begin);
                        if (stream.ReadByte() != '\n') return false;
                    }
                    // Starting one byte early, the first line is the rest of the row the tail begins in, or the newline
                    // just before a tail that begins on a row boundary; either way it is not part of the tail.
                    bool ended = ForEachLine(stream, fileBytes - _tailBytes - 1, fileBytes, (line, offset, length) =>
                    {
                        if (tailStart < 0)
                        {
                            tailStart = offset + length;
                            return true;
                        }
                        var record = ParseRow(line);
                        long previousSeq = tail.Count == 0 ? 0 : tail[tail.Count - 1].seq;
                        // Anything unexpected falls back to the full load, which handles it as every load did before.
                        if (record == null || record.seq <= previousSeq || length != RowBytes(line)) return false;
                        record.ApproximateBytes = length;
                        tail.Add(record);
                        return true;
                    });
                    if (!ended) return false;
                }
            }
            catch
            {
                return false;
            }

            long headRows = fileRows - evictedRows - tail.Count;
            if (tail.Count == 0 || headRows < 0 || tailStart < evictedBytes || (headRows == 0) != (tailStart == evictedBytes)) return false;

            foreach (var record in tail)
            {
                AppendRecordLocked(record);
                if (!string.IsNullOrEmpty(record.nativeKey)) NativeKeys.Add(record.nativeKey);
            }
            _head = new UnparsedHead { End = tailStart, RetainedOffset = evictedBytes, RetainedRows = headRows, LoadedFromSeq = tail[0].seq };
            _fileRows = fileRows;
            _fileBytes = fileBytes;
            // The previous writer's evictions still in the file: a full load would trim and count exactly these.
            _dropped += evictedRows;
            _evictedSinceCompact = evictedRows;
            _evictedBytesSinceCompact = evictedBytes;
            lastSeq = tail[tail.Count - 1].seq;
            return true;
        }

        /// <summary>Reads every row, as every load did before tail loads. False when the file could not be read to its end.</summary>
        private static bool LoadAllLocked(RowReader reader)
        {
            long rows = 0, measured = 0, length = 0;
            bool readComplete = true;
            if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
            {
                try
                {
                    length = new FileInfo(_logPath).Length;
                    foreach (var line in File.ReadLines(_logPath))
                    {
                        long bytes = RowBytes(line);
                        measured += bytes;
                        var record = reader.Read(line);
                        if (record == null) continue;
                        record.ApproximateBytes = bytes;
                        AppendRecordLocked(record);
                        if (!string.IsNullOrEmpty(record.nativeKey)) NativeKeys.Add(record.nativeKey);
                        rows++;
                    }
                }
                catch
                {
                    // A temporary file-system failure leaves the service live for new callbacks; the unread rest of
                    // the file is not in memory, so nothing before this point can be proven complete.
                    readComplete = false;
                }
            }

            // Only rows this writer produced, each measured as it lies in the file, let the next load trust the counts.
            bool exact = readComplete && reader.Clean && measured == length;
            _fileRows = exact ? rows : -1;
            _fileBytes = exact ? length : 0;
            return readComplete;
        }

        /// <summary>Reads the unparsed head first when a reader needs rows after <paramref name="afterSeq"/> that only the file holds.</summary>
        private static void EnsureLoadedAfterLocked(long afterSeq)
        {
            if (_head != null && _head.RetainedRows > 0 && afterSeq + 1 < _head.LoadedFromSeq)
                EnsureCompleteLocked();
        }

        /// <summary>
        /// Parses the head a tail-only load skipped, once. Its retained rows go in front of the records in memory, which
        /// already hold every later row: rows appended since the load are in memory, and pending ones are not in the file.
        /// The rest of the head (evicted rows) only contributes native keys, as a full load's trimmed rows do.
        /// </summary>
        private static void EnsureCompleteLocked()
        {
            var head = _head;
            if (head == null) return;
            _head = null;
            var reader = new RowReader();
            var retained = new List<ConsoleLogHistoryRecord>();
            long retainedBytes = 0;
            bool read;
            try
            {
                using (var stream = OpenLogForRead())
                    read = ForEachLine(stream, 0, head.End, (line, offset, length) =>
                    {
                        var record = reader.Read(line);
                        if (record == null) return true;
                        if (!string.IsNullOrEmpty(record.nativeKey)) NativeKeys.Add(record.nativeKey);
                        if (offset >= head.RetainedOffset && record.seq < head.LoadedFromSeq)
                        {
                            record.ApproximateBytes = length;
                            retained.Add(record);
                            retainedBytes += length;
                        }
                        return true;
                    });
            }
            catch
            {
                read = false;
            }

            if (!read)
            {
                // As a load that could not read the file to its end: the rows only the file held are not served, and the
                // next load reads the whole file again instead of trusting the counts.
                AbandonHeadRowsLocked(head);
                _fileRows = -1;
                return;
            }

            Records.InsertRange(0, retained);
            foreach (var record in retained)
            {
                _bytes += record.ApproximateBytes;
                CountManagedLocked(record, 1);
            }
            if (!reader.Clean || retained.Count != head.RetainedRows || retainedBytes != head.End - head.RetainedOffset)
            {
                // The file is not what the state described: serve the readable rows and rewrite the file from memory.
                if (reader.UnreadableRowPending) MarkIncompleteThroughLocked(head.LoadedFromSeq - 1);
                _fileRows = -1;
                _needsCompact = true;
                _dirty = true;
                EvictOverCapLocked();
            }
            // The whole history is in memory now; like a full load, it compacts the evicted rows still in the file.
            if (_evictedSinceCompact > 0)
            {
                _needsCompact = true;
                _dirty = true;
            }
        }

        private static void CountCorruptRowLocked()
        {
            _corruptRows++;
            _uncompactedCorruptRows++;
        }

        private static void MarkIncompleteThroughLocked(long seq)
        {
            if (seq > _incompleteThroughSeq) _incompleteThroughSeq = seq;
        }

        /// <summary>Persists a reservation a block ahead of the live counter; on failure the next flush retries.</summary>
        private static void ReserveSeqLocked()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            _reservedSeq = _seq + SeqReservationBlock;
            try
            {
                WriteStateLocked();
            }
            catch
            {
                // No logging: this can run inside a log callback.
                _persistenceFailed = true;
                _dirty = true;
            }
        }

        private static void AppendRecordLocked(ConsoleLogHistoryRecord record)
        {
            Records.Add(record);
            _bytes += record.ApproximateBytes;
            CountManagedLocked(record, 1);
        }

        private static long RetainedRowsLocked() => Records.Count + (_head?.RetainedRows ?? 0);

        private static long RetainedBytesLocked() => _bytes + (_head == null || _head.RetainedRows == 0 ? 0 : _head.End - _head.RetainedOffset);

        private static bool EvictOverCapLocked()
        {
            bool evicted = false;
            while ((RetainedRowsLocked() > MaxEntries || RetainedBytesLocked() > MaxBytes) && EvictOldestLocked())
                evicted = true;
            return evicted;
        }

        /// <summary>
        /// Evicts the oldest retained row: from the unparsed head while it has any (its size read from the file, CLR-only
        /// I/O that is safe on the log callback thread), else from memory. Evicted rows stay in the file until compaction.
        /// </summary>
        private static bool EvictOldestLocked()
        {
            long bytes;
            var head = _head;
            if (head != null && head.RetainedRows > 0)
            {
                if (!TryReadHeadRowLocked(head, out _, out int length))
                {
                    AbandonHeadRowsLocked(head);
                    return true;
                }
                bytes = length;
                head.RetainedOffset += length;
                head.RetainedRows--;
                head.OldestSeq = 0;
            }
            else if (Records.Count > 0)
            {
                var removed = Records[0];
                Records.RemoveAt(0);
                _bytes -= removed.ApproximateBytes;
                CountManagedLocked(removed, -1);
                bytes = removed.ApproximateBytes;
            }
            else
            {
                return false;
            }
            _dropped++;
            _evictedSinceCompact++;
            _evictedBytesSinceCompact += bytes;
            return true;
        }

        /// <summary>The head's retained rows cannot be read: they leave the served history as if evicted, and windows reaching back to them are incomplete.</summary>
        private static void AbandonHeadRowsLocked(UnparsedHead head)
        {
            _dropped += head.RetainedRows;
            _evictedSinceCompact += head.RetainedRows;
            _evictedBytesSinceCompact += head.End - head.RetainedOffset;
            MarkIncompleteThroughLocked(head.LoadedFromSeq - 1);
            head.RetainedOffset = head.End;
            head.RetainedRows = 0;
            head.OldestSeq = 0;
        }

        /// <summary>The seq of the oldest retained row; after a tail-only load it is read from the head of the file.</summary>
        private static long OldestSeqLocked()
        {
            var head = _head;
            if (head != null && head.RetainedRows > 0)
            {
                if (head.OldestSeq > 0) return head.OldestSeq;
                if (TryReadHeadRowLocked(head, out int start, out int length))
                {
                    var record = ParseRow(DecodeLine(head.Chunk, start, length));
                    if (record != null && record.seq > 0 && record.seq < head.LoadedFromSeq)
                        return head.OldestSeq = record.seq;
                }
                AbandonHeadRowsLocked(head);
            }
            return Records.Count == 0 ? _seq + 1 : Records[0].seq;
        }

        /// <summary>Locates the row at the head's RetainedOffset in its cached chunk; false when the file cannot supply it.</summary>
        private static bool TryReadHeadRowLocked(UnparsedHead head, out int start, out int length)
        {
            start = 0;
            length = 0;
            try
            {
                int size = ReadChunkBytes;
                while (true)
                {
                    long offset = head.RetainedOffset;
                    if (head.Chunk == null || offset < head.ChunkStart || offset >= head.ChunkStart + head.ChunkLength)
                        ReadHeadChunk(head, offset, size);
                    int from = (int)(offset - head.ChunkStart);
                    int newline = Array.IndexOf(head.Chunk, (byte)'\n', from, head.ChunkLength - from);
                    if (newline >= 0)
                    {
                        start = from;
                        length = newline + 1 - from;
                        return true;
                    }
                    if (head.ChunkStart + head.ChunkLength >= head.End) return false;
                    // The row runs past the chunk: read again from its start, twice as far if it already began there.
                    if (head.ChunkStart == offset) size = (int)Math.Min(2L * Math.Max(size, head.ChunkLength), int.MaxValue);
                    head.Chunk = null;
                }
            }
            catch
            {
                // No logging: this can run inside a log callback.
                return false;
            }
        }

        private static void ReadHeadChunk(UnparsedHead head, long offset, int size)
        {
            int count = (int)Math.Min(size, head.End - offset);
            var chunk = new byte[count];
            using (var stream = OpenLogForRead())
            {
                stream.Seek(offset, SeekOrigin.Begin);
                for (int read = 0; read < count;)
                {
                    int n = stream.Read(chunk, read, count - read);
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }
            }
            head.Chunk = chunk;
            head.ChunkStart = offset;
            head.ChunkLength = count;
        }

        /// <summary>
        /// Calls <paramref name="row"/> with each newline-terminated line in [<paramref name="start"/>, <paramref name="end"/>)
        /// of the stream, with its offset and its byte length, newline included. False when <paramref name="row"/> stops the
        /// scan or the range does not end with a newline.
        /// </summary>
        private static bool ForEachLine(Stream stream, long start, long end, Func<string, long, int, bool> row)
        {
            var buffer = new byte[ReadChunkBytes];
            long bufferOffset = start;
            int filled = 0;
            stream.Seek(start, SeekOrigin.Begin);
            while (bufferOffset + filled < end)
            {
                // The carried-over start of a row fills the buffer: make room for the rest of it.
                if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
                int read = stream.Read(buffer, filled, (int)Math.Min(buffer.Length - filled, end - bufferOffset - filled));
                if (read <= 0) return false;
                int lineStart = 0;
                int scanFrom = filled;
                filled += read;
                int newline;
                while ((newline = Array.IndexOf(buffer, (byte)'\n', scanFrom, filled - scanFrom)) >= 0)
                {
                    int length = newline + 1 - lineStart;
                    if (!row(DecodeLine(buffer, lineStart, length), bufferOffset + lineStart, length)) return false;
                    lineStart = scanFrom = newline + 1;
                }
                if (lineStart > 0)
                {
                    Buffer.BlockCopy(buffer, lineStart, buffer, 0, filled - lineStart);
                    filled -= lineStart;
                    bufferOffset += lineStart;
                }
            }
            return filled == 0;
        }

        /// <summary>The text of a line whose <paramref name="length"/> includes its newline, without that newline or a CR before it.</summary>
        private static string DecodeLine(byte[] buffer, int start, int length)
        {
            int text = length - 1;
            if (text > 0 && buffer[start + text - 1] == '\r') text--;
            return SkillsCommon.Utf8NoBom.GetString(buffer, start, text);
        }

        private static FileStream OpenLogForRead()
        {
            return new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private static JsonSerializer CreateRowSerializer()
        {
            var serializer = JsonSerializer.CreateDefault(SkillsCommon.JsonSettings);
            // JsonConvert.DeserializeObject sets this too: content after the object (two rows glued together) is unreadable.
            serializer.CheckAdditionalContent = true;
            return serializer;
        }

        private static ConsoleLogHistoryRecord ParseRow(string line)
        {
            _parsedRows++;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(line)))
                    return RowSerializer.Deserialize<ConsoleLogHistoryRecord>(reader);
            }
            catch
            {
                return null;
            }
        }

        private static void FlushWhenDue()
        {
            if (_finalFlushDone)
            {
                // A final flush ran but this domain lives on; take the reservation up again before serving more seqs.
                lock (SyncRoot)
                {
                    _finalFlushDone = false;
                    ReserveSeqLocked();
                }
            }
            if (!_dirty || DateTime.UtcNow.Ticks < _nextFlushUtcTicks) return;
            FlushNow(final: false, deferCompaction: EditorApplication.isPlayingOrWillChangePlaymode);
        }

        private static void FinalFlush() => FlushNow(final: true);

        private static void OnDomainUnload(object sender, EventArgs e) => FlushNow(final: true);

        /// <summary>
        /// Persists pending records, then the state. A final flush also releases the seq reservation: it is the last
        /// write of a domain whose history nobody reads any more, so the next domain continues exactly at its seq.
        /// </summary>
        private static void FlushNow(bool final, bool deferCompaction = false)
        {
            lock (SyncRoot)
            {
                if (!_dirty && !final) return;
                try
                {
                    if (_dirty)
                    {
                        if (_needsCompact || CompactionDueLocked(deferCompaction))
                        {
                            // The rewrite is made from memory, so the rows a tail-only load left in the file are read first.
                            EnsureCompleteLocked();
                            RewritePersistenceLocked();
                            _uncompactedCorruptRows = 0;
                            _evictedSinceCompact = 0;
                            _evictedBytesSinceCompact = 0;
                        }
                        else
                        {
                            AppendPendingPersistenceLocked();
                        }
                        PendingPersistence.Clear();
                        _needsCompact = false;
                    }

                    if (final)
                    {
                        _reservedSeq = _seq;
                        _finalFlushDone = true;
                    }
                    else if (_reservedSeq < _seq + SeqReservationBlock / 2)
                    {
                        _reservedSeq = _seq + SeqReservationBlock;
                    }
                    WriteStateLocked();
                    _dirty = false;
                    _persistenceFailed = false;
                    _nextFlushUtcTicks = DateTime.UtcNow.Ticks + FlushIntervalTicks;
                }
                catch
                {
                    // A later editor tick retries. An append may have landed in part, so the retry rewrites the file
                    // from memory instead of appending the same records twice. Do not log from here because this can
                    // be entered while Unity itself is processing a log callback.
                    _persistenceFailed = true;
                    _needsCompact = true;
                    _dirty = true;
                    _fileRows = -1;
                }
            }
        }

        /// <summary>
        /// Whether enough evicted rows have piled up in the file to rewrite it. Play Mode (<paramref name="defer"/>) waits
        /// for a full turnover, which bounds the file at about twice the cap; the next load trims and compacts it anyway.
        /// </summary>
        private static bool CompactionDueLocked(bool defer)
        {
            if (_evictedSinceCompact >= MaxEntries || _evictedBytesSinceCompact >= MaxBytes) return true;
            return !defer && (_evictedSinceCompact >= CompactEvictionCount || _evictedBytesSinceCompact >= CompactEvictionBytes);
        }

        private static void RewritePersistenceLocked()
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            var sizes = new long[Records.Count];
            // Temp file + rename: a kill or a reload mid-compaction leaves the previous file, never a truncated one.
            LibraryStateFile.Write(_logPath, stream =>
            {
                using (var writer = new StreamWriter(stream, SkillsCommon.Utf8NoBom, 1 << 16))
                {
                    for (int i = 0; i < Records.Count; i++)
                    {
                        string line = JsonConvert.SerializeObject(Records[i], Formatting.None, SkillsCommon.JsonSettings);
                        writer.Write(line);
                        writer.Write(Environment.NewLine);
                        sizes[i] = RowBytes(line);
                    }
                }
            });
            // Rows read from an older file can serialize differently; the sizes now match the file a tail load walks.
            _bytes = 0;
            for (int i = 0; i < Records.Count; i++)
            {
                Records[i].ApproximateBytes = sizes[i];
                _bytes += sizes[i];
            }
            _fileRows = Records.Count;
            _fileBytes = _bytes;
        }

        private static void AppendPendingPersistenceLocked()
        {
            if (PendingPersistence.Count == 0 || string.IsNullOrEmpty(_logPath)) return;
            var lines = new StringBuilder();
            foreach (var record in PendingPersistence)
                lines.Append(JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings)).Append(Environment.NewLine);
            byte[] bytes = SkillsCommon.Utf8NoBom.GetBytes(lines.ToString());
            LibraryStateFile.AppendLines(_logPath, bytes);
            if (_fileRows < 0) return;
            _fileRows += PendingPersistence.Count;
            _fileBytes += bytes.Length;
        }

        /// <summary>Returns false when a state file exists but cannot be read.</summary>
        private static bool LoadStateLocked(out PersistedState state)
        {
            state = null;
            if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return true;
            try
            {
                state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(_statePath), SkillsCommon.JsonSettings);
                if (state == null) return false;
                _seq = Math.Max(_seq, state.seq);
                _dropped = Math.Max(0, state.dropped);
                _generation = Math.Max(0, state.generation);
                _reservedSeq = Math.Max(0, state.reservedSeq);
                _incompleteThroughSeq = Math.Max(_incompleteThroughSeq, state.incompleteThroughSeq);
                _corruptRows = Math.Max(0, state.corruptRows);
                return true;
            }
            catch
            {
                state = null;
                return false;
            }
        }

        private static void WriteStateLocked()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            bool exact = _fileRows >= 0;
            // Evictions come off the front of the file, then of the pending rows behind it, so the file holds at most its
            // own rows of them.
            bool evictionsInFile = _evictedSinceCompact <= _fileRows;
            LibraryStateFile.WriteAllText(_statePath, JsonConvert.SerializeObject(new PersistedState
            {
                seq = _seq,
                // Evicted rows still in the file are counted again when the next load trims them.
                dropped = Math.Max(0, _dropped - _evictedSinceCompact),
                generation = _generation,
                reservedSeq = _reservedSeq,
                incompleteThroughSeq = _incompleteThroughSeq,
                // Rows still in the file are counted again by the next load; persist only those compaction removed.
                corruptRows = _corruptRows - _uncompactedCorruptRows,
                fileRows = exact ? _fileRows : (long?)null,
                fileBytes = exact ? _fileBytes : (long?)null,
                fileEvictedRows = exact ? Math.Min(_evictedSinceCompact, _fileRows) : (long?)null,
                fileEvictedBytes = exact ? (evictionsInFile ? _evictedBytesSinceCompact : _fileBytes) : (long?)null,
            }, Formatting.None, SkillsCommon.JsonSettings));
        }

        private static bool HasManagedEquivalentLocked(string type, string message)
        {
            return type != null && ManagedCounts.ContainsKey(ManagedKey(type, message ?? string.Empty));
        }

        private static string ManagedKey(string type, string message) => string.Concat(type, "\u001f", message);

        /// <summary>Tracks the rows <see cref="HasManagedEquivalentLocked"/> can match: managed, with a type and a message.</summary>
        private static void CountManagedLocked(ConsoleLogHistoryRecord record, int delta)
        {
            if (record.source != "managed" || record.type == null || record.message == null) return;
            string key = ManagedKey(record.type, record.message);
            ManagedCounts.TryGetValue(key, out int count);
            count += delta;
            if (count > 0) ManagedCounts[key] = count;
            else ManagedCounts.Remove(key);
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
            return RowBytes(JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings));
        }

        /// <summary>A row's size in the file: its UTF-8 line plus the newline this writer ends it with.</summary>
        private static long RowBytes(string line)
        {
            return SkillsCommon.Utf8NoBom.GetByteCount(line) + Environment.NewLine.Length;
        }

        private static object CoverageLocked(long oldest, long snapshot, int unknownTimeCount)
        {
            return new { oldestSeq = oldest, newestSeq = _seq, snapshotSeq = snapshot, retained = RetainedRowsLocked(), capacity = MaxEntries, maxBytes = MaxBytes, unknownTimeCount, nativeTime = "unknown", nativeMessageMayBeTruncated = true, fullTextGuaranteedFor = "managed", corruptRows = _corruptRows, incompleteThroughSeq = _incompleteThroughSeq, durabilityIssue = _persistenceFailed, editorLogPath = _editorLogPath, editorLogAvailable = !string.IsNullOrEmpty(_editorLogPath) && File.Exists(_editorLogPath), editorLogSource = "Editor.log is an external fallback; it is not imported into cursor history." };
        }

        /// <summary>
        /// A window is complete when every record after <paramref name="afterSeq"/> is still retained. Unreadable rows and
        /// unclean stops only affect windows reaching back to <see cref="_incompleteThroughSeq"/>; a persistence failure
        /// loses nothing that is still in memory and stays visible as coverage.durabilityIssue.
        /// </summary>
        private static bool IsCompleteLocked(long afterSeq, IEnumerable<ConsoleLogHistoryRecord> matching)
        {
            long oldest = OldestSeqLocked();
            return afterSeq + 1 >= oldest && afterSeq >= _incompleteThroughSeq && !matching.Any(record => !record.timeKnown);
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
                _uncompactedCorruptRows = 0; _incompleteThroughSeq = 0; _reservedSeq = 0; _finalFlushDone = false; _persistenceFailed = false;
                _dirty = false; _needsCompact = false; _evictedSinceCompact = 0; _evictedBytesSinceCompact = 0;
                ManagedCounts.Clear(); _head = null; _fileRows = 0; _fileBytes = 0;
                DeleteTestFiles();
            }
        }

        private static void DeleteTestFiles()
        {
            foreach (var file in new[] { _logPath, _statePath })
            {
                if (string.IsNullOrEmpty(file)) continue;
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(file + LibraryStateFile.TempSuffix)) File.Delete(file + LibraryStateFile.TempSuffix);
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
            private readonly long _oldUncompactedCorruptRows;
            private readonly long _oldIncompleteThroughSeq;
            private readonly long _oldReservedSeq;
            private readonly bool _oldFinalFlushDone;
            private readonly long _oldGeneration;
            private readonly long _oldCaptureStartSeq;
            private readonly bool _oldCapturing;
            private readonly bool _oldDirty;
            private readonly bool _oldNeedsCompact;
            private readonly long _oldEvictedSinceCompact;
            private readonly long _oldEvictedBytesSinceCompact;
            private readonly bool _oldPersistenceFailed;
            private readonly List<ConsoleLogHistoryRecord> _oldPendingPersistence;
            private readonly long _oldNextFlushUtcTicks;
            private readonly Dictionary<string, int> _oldManagedCounts;
            private readonly UnparsedHead _oldHead;
            private readonly long _oldFileRows;
            private readonly long _oldFileBytes;
            private readonly long _oldTailBytes;

            internal TestScope(string path)
            {
                lock (SyncRoot)
                {
                    _oldPath = _logPath; _oldStatePath = _statePath;
                    _oldRecords = new List<ConsoleLogHistoryRecord>(Records);
                    _oldNativeKeys = new HashSet<string>(NativeKeys, StringComparer.Ordinal);
                    _oldManagedCounts = new Dictionary<string, int>(ManagedCounts, StringComparer.Ordinal);
                    _oldHead = _head; _oldFileRows = _fileRows; _oldFileBytes = _fileBytes; _oldTailBytes = _tailBytes;
                    _oldSeq = _seq; _oldBytes = _bytes; _oldDropped = _dropped; _oldCorruptRows = _corruptRows; _oldGeneration = _generation;
                    _oldUncompactedCorruptRows = _uncompactedCorruptRows; _oldIncompleteThroughSeq = _incompleteThroughSeq; _oldReservedSeq = _reservedSeq; _oldFinalFlushDone = _finalFlushDone;
                    _oldCaptureStartSeq = _captureStartSeq; _oldCapturing = _capturing; _oldDirty = _dirty; _oldNeedsCompact = _needsCompact; _oldPersistenceFailed = _persistenceFailed;
                    _oldEvictedSinceCompact = _evictedSinceCompact; _oldEvictedBytesSinceCompact = _evictedBytesSinceCompact;
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
                    DeleteTestFiles();
                    _logPath = _oldPath; _statePath = _oldStatePath;
                    Records.Clear(); Records.AddRange(_oldRecords);
                    NativeKeys.Clear(); foreach (var key in _oldNativeKeys) NativeKeys.Add(key);
                    ManagedCounts.Clear(); foreach (var pair in _oldManagedCounts) ManagedCounts.Add(pair.Key, pair.Value);
                    _head = _oldHead; _fileRows = _oldFileRows; _fileBytes = _oldFileBytes; _tailBytes = _oldTailBytes;
                    _seq = _oldSeq; _bytes = _oldBytes; _dropped = _oldDropped; _corruptRows = _oldCorruptRows; _generation = _oldGeneration;
                    _uncompactedCorruptRows = _oldUncompactedCorruptRows; _incompleteThroughSeq = _oldIncompleteThroughSeq; _reservedSeq = _oldReservedSeq; _finalFlushDone = _oldFinalFlushDone;
                    PendingPersistence.Clear(); PendingPersistence.AddRange(_oldPendingPersistence);
                    _captureStartSeq = _oldCaptureStartSeq; _capturing = _oldCapturing; _dirty = _oldDirty; _needsCompact = _oldNeedsCompact; _persistenceFailed = _oldPersistenceFailed; _nextFlushUtcTicks = _oldNextFlushUtcTicks;
                    _evictedSinceCompact = _oldEvictedSinceCompact; _evictedBytesSinceCompact = _oldEvictedBytesSinceCompact;
                }
            }
        }

        /// <summary>The periodic flush; <paramref name="inPlayMode"/> stands for EditorApplication.isPlayingOrWillChangePlaymode.</summary>
        internal static void FlushForTests(bool inPlayMode = false) => FlushNow(final: false, deferCompaction: inPlayMode);

        /// <summary>The flush beforeAssemblyReload runs; records added after it still reach the DomainUnload flush.</summary>
        internal static void FinalFlushForTests() => FlushNow(final: true);

        internal static void AppendCorruptTailForTests() => AppendRawForTests("{corrupt-tail" + Environment.NewLine);

        /// <summary>Flushes, then appends <paramref name="raw"/> verbatim (no newline added), like a torn or foreign write.</summary>
        internal static void AppendRawForTests(string raw)
        {
            FlushNow(final: false);
            lock (SyncRoot)
            {
                if (!string.IsNullOrEmpty(_logPath))
                    File.AppendAllText(_logPath, raw, new UTF8Encoding(false));
            }
        }

        /// <summary>
        /// Simulates a domain reload (<paramref name="clean"/>: the final flush runs first) or an editor kill (nothing
        /// is flushed, so pending records are lost) followed by the next load.
        /// </summary>
        internal static void ReloadForTests(bool clean = true)
        {
            if (clean) FlushNow(final: true);
            lock (SyncRoot)
            {
                Records.Clear(); NativeKeys.Clear(); PendingPersistence.Clear(); _seq = 0; _bytes = 0; _dropped = 0; _corruptRows = 0; _generation = 0; _dirty = false; _needsCompact = false; _persistenceFailed = false;
                _uncompactedCorruptRows = 0; _incompleteThroughSeq = 0; _reservedSeq = 0; _finalFlushDone = false; _evictedSinceCompact = 0; _evictedBytesSinceCompact = 0;
                ManagedCounts.Clear(); _head = null; _fileRows = -1; _fileBytes = 0;
                LoadPersistedHistory();
            }
        }

        /// <summary>Sets how much of the file's end a reload parses, so tests reach tail-only loads with small files.</summary>
        internal static void SetTailBytesForTests(long bytes)
        {
            lock (SyncRoot)
                _tailBytes = bytes;
        }

        /// <summary>Rows parsed from the file so far, by loads, on-demand completion and oldest-seq reads.</summary>
        internal static long ParsedRowsForTests
        {
            get { lock (SyncRoot) return _parsedRows; }
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
