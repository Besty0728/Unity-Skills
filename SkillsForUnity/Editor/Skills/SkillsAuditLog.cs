using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// An append-only JSONL audit log for the skill permission-mode system.
    ///
    /// Events are written to <c>Library/UnitySkillsAudit.jsonl</c> (stored per-project, not committed to
    /// Git). Writes queue on the calling thread and flush to disk asynchronously, so REST handlers never
    /// block on disk I/O. The file rotates at 1MB, keeping up to 3 historical copies
    /// (<c>UnitySkillsAudit.1.jsonl</c> / <c>.2.jsonl</c> / <c>.3.jsonl</c>).
    ///
    /// All three run modes (Approval / Auto / Bypass) write to the same log; this is the user's primary
    /// means of retracing "did the AI ask before doing X?"
    /// </summary>
    public static class SkillsAuditLog
    {
        private const string LogFileName = "UnitySkillsAudit.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const int ReadTailMaxBytes = 256 * 1024; // The /audit endpoint reads only the tail

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked guard
        private static string _cachedDir;
        private static string _cachedPath;

        /// <summary>
        /// Appends an event. Non-blocking: the JSON line is enqueued and flushed to disk by a thread-pool
        /// worker. Safe to call from any thread.
        /// </summary>
        public static void Append(string eventType, object data)
        {
            if (string.IsNullOrEmpty(eventType)) return;
            try
            {
                // Resolve and cache the path here (every current call site is on the main thread — see the
                // HandlePermissionGrant comment in SkillsHttpServer.cs), so the ThreadPool flush worker can
                // reuse the cached value instead of reading Application.dataPath off the main thread —
                // which would silently fall back to Path.GetTempPath() (see ResolveLibraryDir),
                // splitting this session's audit trail across two files.
                GetLogPath();
                var line = BuildLine(eventType, data);
                _queue.Enqueue(line);
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // The audit log must never bring down the caller; best-effort, and swallow the exception.
                SkillsLogger.LogWarning($"AuditLog enqueue failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads up to the most recent <paramref name="limit"/> records (newest first).
        /// Reads only the tail (roughly the last 256KB), so the time cost doesn't grow with file size.
        /// Returns parsed JObjects; serialization is left to the caller.
        /// </summary>
        public static IList<object> ReadRecent(int limit)
        {
            if (limit <= 0) limit = 100;
            // Flush pending items to disk first, so the read includes every record that's already been Appended.
            FlushSync();

            var path = GetLogPath();
            var results = new List<object>();
            if (!File.Exists(path)) return results;

            try
            {
                string tail;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long len = fs.Length;
                    long start = Math.Max(0, len - ReadTailMaxBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    using (var reader = new StreamReader(fs, new UTF8Encoding(false)))
                    {
                        // Discard the possibly-truncated first line when starting mid-line.
                        if (start > 0) reader.ReadLine();
                        tail = reader.ReadToEnd();
                    }
                }

                var lines = tail.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                int from = Math.Max(0, lines.Length - limit);
                for (int i = from; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    try
                    {
                        results.Add(Newtonsoft.Json.Linq.JObject.Parse(line));
                    }
                    catch
                    {
                        // Skip malformed lines; a single bad line shouldn't fail the whole read.
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"AuditLog read failed: {ex.Message}");
            }
            return results;
        }

        /// <summary>Resolves the audit log's absolute path (cached after the first call).</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// Deletes a single record from the primary log by the (ts, type) pair (the combination is
        /// effectively unique — ts is millisecond-precision UTC).
        /// Deliberately leaves rotated history files untouched and only rewrites the primary file, to
        /// avoid amplifying I/O or corrupting old logs.
        /// Returns the number of lines actually deleted (0 if not found, usually 1).
        /// Writes an <c>audit_deleted</c> tracer event after deleting, so the deletion itself is also
        /// audited — this is the key to the log serving as a trust anchor.
        /// </summary>
        public static int DeleteEntry(string ts, string type)
        {
            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(type)) return 0;
            FlushSync();
            int removed = RewritePrimary(line =>
            {
                Newtonsoft.Json.Linq.JObject obj;
                try { obj = Newtonsoft.Json.Linq.JObject.Parse(line); }
                catch { return true; } // Keep unparseable lines as-is
                var lineTs = obj["ts"]?.ToString();
                var lineType = obj["type"]?.ToString();
                bool match = string.Equals(lineTs, ts, StringComparison.Ordinal)
                          && string.Equals(lineType, type, StringComparison.Ordinal);
                return !match;
            });
            if (removed > 0)
                Append("audit_deleted", new { targetTs = ts, targetType = type, removed });
            return removed;
        }

        /// <summary>
        /// Clears the primary log and every rotated copy. Returns the total bytes deleted (an
        /// approximation, for toast display).
        /// Afterward writes an <c>audit_cleared</c> tracer event into the now-empty log, so the clear
        /// action itself leaves a trace.
        /// </summary>
        public static long ClearAll()
        {
            FlushSync();
            long bytesRemoved = 0;
            lock (_writeLock)
            {
                try
                {
                    var dir = _cachedDir ?? ResolveLibraryDir();
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "UnitySkillsAudit*.jsonl"))
                        {
                            try
                            {
                                var len = new FileInfo(f).Length;
                                File.Delete(f);
                                bytesRemoved += len;
                            }
                            catch (Exception ex)
                            {
                                SkillsLogger.LogWarning($"AuditLog ClearAll: failed to delete {f}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"AuditLog ClearAll failed: {ex.Message}");
                }
            }
            Append("audit_cleared", new { bytesRemoved });
            return bytesRemoved;
        }

        /// <summary>
        /// Internal: synchronously drains the queue on the calling thread.
        /// Used by <see cref="ReadRecent"/> and by tests, to guarantee writes are visible.
        /// </summary>
        internal static void FlushSync()
        {
            FlushPending();
        }

        /// <summary>Internal: clears the on-disk log and rotated copies; test-only use.</summary>
        internal static void ResetForTests()
        {
            FlushPending();
            try
            {
                var dir = ResolveLibraryDir();
                foreach (var f in Directory.EnumerateFiles(dir, "UnitySkillsAudit*.jsonl"))
                {
                    try { File.Delete(f); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }

        // ===== Internal implementation =====

        private static string BuildLine(string eventType, object data)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["type"] = eventType,
            };
            if (data != null)
            {
                // Flatten the data object into top-level fields, keeping the log grep-friendly.
                var token = Newtonsoft.Json.Linq.JToken.FromObject(data, JsonSerializer.Create(SkillsCommon.JsonSettings));
                if (token is Newtonsoft.Json.Linq.JObject obj)
                {
                    foreach (var prop in obj.Properties())
                    {
                        if (!payload.ContainsKey(prop.Name))
                            payload[prop.Name] = prop.Value;
                    }
                }
                else
                {
                    payload["data"] = token;
                }
            }
            return JsonConvert.SerializeObject(payload, Formatting.None, SkillsCommon.JsonSettings);
        }

        private static void ScheduleFlush()
        {
            // Coalesce multiple appends into a single flush task.
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
                try
                {
                    // ??= writes the resolved result back into _cachedDir (not just a local variable), so
                    // that even if Append's main-thread warmup gets bypassed and resolution happens here on
                    // a worker thread, later calls can reuse the same result instead of silently
                    // re-resolving every time (and possibly falling back to a different temp directory).
                    var dir = _cachedDir ??= ResolveLibraryDir();
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    var path = _cachedPath ?? Path.Combine(dir, LogFileName);

                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        while (_queue.TryDequeue(out var line))
                        {
                            writer.WriteLine(line);
                        }
                    }

                    RotateIfNeeded(path);
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"AuditLog flush failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Reads the primary log line by line, keeping only lines for which <paramref name="keep"/>
        /// returns true, and atomically rewrites the file (temp file + replace).
        /// Returns the number of lines deleted. Mutually exclusive with concurrent flushes via <c>_writeLock</c>.
        /// </summary>
        private static int RewritePrimary(Func<string, bool> keep)
        {
            int removed = 0;
            lock (_writeLock)
            {
                var path = GetLogPath();
                if (!File.Exists(path)) return 0;

                var tmp = path + ".tmp";
                try
                {
                    using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(src, new UTF8Encoding(false)))
                    using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(dst, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length == 0) continue;
                            if (keep(line)) writer.WriteLine(line);
                            else removed++;
                        }
                    }

                    // File.Replace(tmp, path, null) is the true atomic swap (leaves no backup file, since
                    // path is an existing JSONL log with its own rotated copies): there's no window where
                    // `path` is missing, unlike a Delete-then-Move, where a crash between the two calls
                    // would wipe out the primary log entirely.
                    // File.Replace requires the destination to already exist; RewritePrimary already returns
                    // early above when it doesn't, so this precondition holds, unless `path` was removed
                    // externally between that check and here (both are inside _writeLock, so it wouldn't be
                    // this code doing it) — in that extremely rare case, fall back to a plain move rather
                    // than discarding the already-rewritten content.
                    try
                    {
                        File.Replace(tmp, path, null);
                    }
                    catch (FileNotFoundException)
                    {
                        File.Move(tmp, path);
                    }
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"AuditLog RewritePrimary failed: {ex.Message}");
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                    return 0;
                }
            }
            return removed;
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxFileBytes) return;

                // Move in order: .2 -> .3, .1 -> .2, primary file -> .1
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
                SkillsLogger.LogWarning($"AuditLog rotate failed: {ex.Message}");
            }
        }

        private static string RotatedPath(int n)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            return Path.Combine(dir, $"UnitySkillsAudit.{n}.jsonl");
        }

        /// <summary>
        /// Returns <c>&lt;project&gt;/Library</c>. Falls back to <c>Application.persistentDataPath</c> when
        /// accessed before the Unity editor is ready (e.g. early static init on a worker thread).
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
    }
}

// Producer:Betsy
