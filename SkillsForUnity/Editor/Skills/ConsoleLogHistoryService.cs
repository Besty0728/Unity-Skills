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

        private const string RelativeLogPath = "Library/UnitySkills/console_log_history.jsonl";
        private const string RelativeStatePath = "Library/UnitySkills/console_log_history_state.json";
        private const long FlushIntervalTicks = TimeSpan.TicksPerSecond;
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
        private static bool _persistenceFailed;
        private static long _nextFlushUtcTicks;

        static ConsoleLogHistoryService()
        {
            try
            {
                // An asset import worker loads this assembly against the same Library folder. Its records are not this
                // editor's console, and a second writer with its own seq counter tears lines and repeats seqs.
                if (LibraryStateFile.IsAssetImportWorker)
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
                // The rewrite below holds no rows at all, so no unreadable row survives it.
                _corruptRows = 0;
                _uncompactedCorruptRows = 0;
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
            // The callback does not touch SessionState; the durable JSONL seq remains the reload authority. A seq past
            // the persisted reservation extends it before any reader can see the record (rare: the flush keeps half a
            // block of headroom), so a kill can never lead to its reuse. CLR-only file I/O, safe on this thread.
            if (_seq > _reservedSeq && !_finalFlushDone)
                ReserveSeqLocked();
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
                bool stateReadable = LoadStateLocked();
                long lastSeq = 0;
                bool unreadableRowPending = false;
                bool readComplete = true;
                if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(_logPath))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            ConsoleLogHistoryRecord record;
                            try
                            {
                                record = JsonConvert.DeserializeObject<ConsoleLogHistoryRecord>(line, SkillsCommon.JsonSettings);
                            }
                            catch
                            {
                                record = null;
                            }

                            if (record == null || record.seq <= 0)
                            {
                                // A torn line (an append a kill cut short) hides a record whose seq lies before the next
                                // readable one. Keep every other line instead of treating the whole history as corrupt.
                                CountCorruptRowLocked();
                                unreadableRowPending = true;
                                continue;
                            }

                            if (record.seq <= lastSeq)
                            {
                                // A repeated or backwards seq means a second writer; windows reaching back to it are
                                // ambiguous. Keeping seqs unique and ascending is what cursors rely on.
                                CountCorruptRowLocked();
                                MarkIncompleteThroughLocked(lastSeq);
                                continue;
                            }

                            if (unreadableRowPending)
                            {
                                MarkIncompleteThroughLocked(record.seq - 1);
                                unreadableRowPending = false;
                            }

                            record.ApproximateBytes = ApproximateBytes(record);
                            Records.Add(record);
                            _bytes += record.ApproximateBytes;
                            lastSeq = record.seq;
                            if (!string.IsNullOrEmpty(record.nativeKey)) NativeKeys.Add(record.nativeKey);
                        }
                    }
                    catch
                    {
                        // A temporary file-system failure leaves the service live for new callbacks; the unread rest of
                        // the file is not in memory, so nothing before this point can be proven complete.
                        readComplete = false;
                    }
                }

                _seq = Math.Max(_seq, lastSeq);
                if (_reservedSeq > _seq || (!stateReadable && lastSeq > 0))
                {
                    // The last writer stopped without its final flush (a kill or a crash), or left no readable state:
                    // it may have handed out seqs that never reached the file. Resume above every one of them.
                    _seq = Math.Max(_reservedSeq, _seq + (stateReadable ? 0 : SeqReservationBlock));
                    MarkIncompleteThroughLocked(_seq);
                }
                if (unreadableRowPending || !readComplete)
                    MarkIncompleteThroughLocked(_seq);

                // Compaction rewrites the file from memory, so it drops unreadable rows, but only after a complete read.
                _needsCompact = TrimLocked() || (readComplete && _uncompactedCorruptRows > 0);
                _dirty = _needsCompact;
                _finalFlushDone = false;
                ReserveSeqLocked();
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
            FlushNow(final: false);
        }

        private static void FinalFlush() => FlushNow(final: true);

        private static void OnDomainUnload(object sender, EventArgs e) => FlushNow(final: true);

        /// <summary>
        /// Persists pending records, then the state. A final flush also releases the seq reservation: it is the last
        /// write of a domain whose history nobody reads any more, so the next domain continues exactly at its seq.
        /// </summary>
        private static void FlushNow(bool final)
        {
            lock (SyncRoot)
            {
                if (!_dirty && !final) return;
                try
                {
                    if (_dirty)
                    {
                        if (_needsCompact)
                        {
                            RewritePersistenceLocked();
                            _uncompactedCorruptRows = 0;
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
                }
            }
        }

        private static void RewritePersistenceLocked()
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            // Temp file + rename: a kill or a reload mid-compaction leaves the previous file, never a truncated one.
            LibraryStateFile.Write(_logPath, stream =>
            {
                using (var writer = new StreamWriter(stream, SkillsCommon.Utf8NoBom, 1 << 16))
                {
                    foreach (var record in Records)
                    {
                        writer.Write(JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings));
                        writer.Write(Environment.NewLine);
                    }
                }
            });
        }

        private static void AppendPendingPersistenceLocked()
        {
            if (PendingPersistence.Count == 0 || string.IsNullOrEmpty(_logPath)) return;
            var lines = new StringBuilder();
            foreach (var record in PendingPersistence)
                lines.Append(JsonConvert.SerializeObject(record, Formatting.None, SkillsCommon.JsonSettings)).Append(Environment.NewLine);
            LibraryStateFile.AppendLines(_logPath, SkillsCommon.Utf8NoBom.GetBytes(lines.ToString()));
        }

        /// <summary>Returns false when a state file exists but cannot be read.</summary>
        private static bool LoadStateLocked()
        {
            if (string.IsNullOrEmpty(_statePath) || !File.Exists(_statePath)) return true;
            try
            {
                var state = JsonConvert.DeserializeObject<PersistedState>(File.ReadAllText(_statePath), SkillsCommon.JsonSettings);
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
                return false;
            }
        }

        private static void WriteStateLocked()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            LibraryStateFile.WriteAllText(_statePath, JsonConvert.SerializeObject(new PersistedState
            {
                seq = _seq,
                dropped = _dropped,
                generation = _generation,
                reservedSeq = _reservedSeq,
                incompleteThroughSeq = _incompleteThroughSeq,
                // Rows still in the file are counted again by the next load; persist only those compaction removed.
                corruptRows = _corruptRows - _uncompactedCorruptRows,
            }, Formatting.None, SkillsCommon.JsonSettings));
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
            return new { oldestSeq = oldest, newestSeq = _seq, snapshotSeq = snapshot, retained = Records.Count, capacity = MaxEntries, maxBytes = MaxBytes, unknownTimeCount, nativeTime = "unknown", nativeMessageMayBeTruncated = true, fullTextGuaranteedFor = "managed", corruptRows = _corruptRows, incompleteThroughSeq = _incompleteThroughSeq, durabilityIssue = _persistenceFailed, editorLogPath = _editorLogPath, editorLogAvailable = !string.IsNullOrEmpty(_editorLogPath) && File.Exists(_editorLogPath), editorLogSource = "Editor.log is an external fallback; it is not imported into cursor history." };
        }

        /// <summary>
        /// A window is complete when every record after <paramref name="afterSeq"/> is still retained. Unreadable rows and
        /// unclean stops only affect windows reaching back to <see cref="_incompleteThroughSeq"/>; a persistence failure
        /// loses nothing that is still in memory and stays visible as coverage.durabilityIssue.
        /// </summary>
        private static bool IsCompleteLocked(long afterSeq, IEnumerable<ConsoleLogHistoryRecord> matching)
        {
            long oldest = Records.Count == 0 ? _seq + 1 : Records[0].seq;
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
                _dirty = false; _needsCompact = false;
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
                    _oldUncompactedCorruptRows = _uncompactedCorruptRows; _oldIncompleteThroughSeq = _incompleteThroughSeq; _oldReservedSeq = _reservedSeq; _oldFinalFlushDone = _finalFlushDone;
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
                    DeleteTestFiles();
                    _logPath = _oldPath; _statePath = _oldStatePath;
                    Records.Clear(); Records.AddRange(_oldRecords);
                    NativeKeys.Clear(); foreach (var key in _oldNativeKeys) NativeKeys.Add(key);
                    _seq = _oldSeq; _bytes = _oldBytes; _dropped = _oldDropped; _corruptRows = _oldCorruptRows; _generation = _oldGeneration;
                    _uncompactedCorruptRows = _oldUncompactedCorruptRows; _incompleteThroughSeq = _oldIncompleteThroughSeq; _reservedSeq = _oldReservedSeq; _finalFlushDone = _oldFinalFlushDone;
                    PendingPersistence.Clear(); PendingPersistence.AddRange(_oldPendingPersistence);
                    _captureStartSeq = _oldCaptureStartSeq; _capturing = _oldCapturing; _dirty = _oldDirty; _needsCompact = _oldNeedsCompact; _persistenceFailed = _oldPersistenceFailed; _nextFlushUtcTicks = _oldNextFlushUtcTicks;
                }
            }
        }

        internal static void FlushForTests() => FlushNow(final: false);

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
                _uncompactedCorruptRows = 0; _incompleteThroughSeq = 0; _reservedSeq = 0; _finalFlushDone = false;
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
