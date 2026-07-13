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
    /// Append-only JSONL audit log for the Skill mode permission system (v1.9).
    ///
    /// Events are written to <c>Library/UnitySkillsAudit.jsonl</c> (per-project, not in Git).
    /// Writes are queued on the calling thread and flushed asynchronously so REST handlers
    /// never block on disk I/O. Files roll over at 1MB; up to 3 historical files are kept
    /// (<c>UnitySkillsAudit.1.jsonl</c> / <c>.2.jsonl</c> / <c>.3.jsonl</c>).
    ///
    /// All three operating modes (Approval / Auto / Bypass) write to the same log; this is
    /// the user's primary reverse-tracing tool for "did the AI ask before doing X?".
    /// </summary>
    public static class SkillsAuditLog
    {
        private const string LogFileName = "UnitySkillsAudit.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const int ReadTailMaxBytes = 256 * 1024; // /audit endpoint reads tail only

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked guard
        private static string _cachedDir;
        private static string _cachedPath;

        /// <summary>
        /// Append an event. Non-blocking: the JSON line is queued and flushed on a thread-pool
        /// worker. Safe to call from any thread.
        /// </summary>
        public static void Append(string eventType, object data)
        {
            if (string.IsNullOrEmpty(eventType)) return;
            try
            {
                var line = BuildLine(eventType, data);
                _queue.Enqueue(line);
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // Audit log MUST NOT crash the caller. Best-effort, swallow.
                SkillsLogger.LogWarning($"AuditLog enqueue failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Read up to <paramref name="limit"/> most-recent entries (newest first).
        /// Reads tail only (last ~256KB) so the call is bounded regardless of file size.
        /// Entries are returned as parsed JObjects; callers serialize as needed.
        /// </summary>
        public static IList<object> ReadRecent(int limit)
        {
            if (limit <= 0) limit = 100;
            // Flush pending entries so the read reflects everything that has been Append-ed.
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
                        // Discard partial first line if we started mid-line.
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
                        // Skip malformed lines rather than failing the whole read.
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"AuditLog read failed: {ex.Message}");
            }
            return results;
        }

        /// <summary>Resolve the audit log absolute path (cached after first call).</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// Delete a single entry from the primary log identified by the (ts, type) pair
        /// (which together are effectively unique — ts is millisecond-precision UTC).
        /// Rotated history files are intentionally untouched; only the primary file is
        /// rewritten so we don't accidentally bloat I/O or risk corrupting older logs.
        /// Returns the number of lines actually removed (0 if not found, typically 1).
        /// Writes an <c>audit_deleted</c> tracer event after the deletion so the act of
        /// deleting is itself audited — critical for keeping the log as a trust anchor.
        /// </summary>
        public static int DeleteEntry(string ts, string type)
        {
            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(type)) return 0;
            FlushSync();
            int removed = RewritePrimary(line =>
            {
                Newtonsoft.Json.Linq.JObject obj;
                try { obj = Newtonsoft.Json.Linq.JObject.Parse(line); }
                catch { return true; } // keep unparseable lines as-is
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
        /// Wipe the primary log AND every rotated copy. Returns total bytes removed
        /// (approximate, for the toast). Records an <c>audit_cleared</c> tracer event
        /// in the now-empty log so the wipe itself leaves a footprint.
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
        /// Internal: drain the queue synchronously on the calling thread.
        /// Used by <see cref="ReadRecent"/> and tests to guarantee write visibility.
        /// </summary>
        internal static void FlushSync()
        {
            FlushPending();
        }

        /// <summary>Internal: wipe the on-disk log (and rotated copies). Tests only.</summary>
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

        // ===== internals =====

        private static string BuildLine(string eventType, object data)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["type"] = eventType,
            };
            if (data != null)
            {
                // Flatten the data object as top-level fields so the log stays grep-friendly.
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
                try
                {
                    var dir = _cachedDir ?? ResolveLibraryDir();
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
        /// Read the primary log line-by-line, keep only the lines for which <paramref name="keep"/>
        /// returns true, and atomically rewrite the file (temp + replace). Returns the number
        /// of lines that were removed. Locked against concurrent flushes via <c>_writeLock</c>.
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

                    // File.Replace gives us an atomic swap on Windows + most POSIX FS.
                    // Fall back to Delete+Move when the destination doesn't exist (shouldn't
                    // happen here since we returned early above, but defensive).
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(tmp, path);
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
                SkillsLogger.LogWarning($"AuditLog rotate failed: {ex.Message}");
            }
        }

        private static string RotatedPath(int n)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            return Path.Combine(dir, $"UnitySkillsAudit.{n}.jsonl");
        }

        /// <summary>
        /// Returns <c>&lt;project&gt;/Library</c>. Falls back to <c>Application.persistentDataPath</c>
        /// when accessed before the Unity Editor is ready (e.g. early static init on a worker thread).
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
