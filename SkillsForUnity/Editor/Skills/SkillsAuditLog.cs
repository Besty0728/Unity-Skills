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
    /// Skill 模式权限系统的追加式 JSONL 审计日志。
    ///
    /// 事件写入 <c>Library/UnitySkillsAudit.jsonl</c>（按项目存放，不入 Git）。写入在调用线程排队、
    /// 异步落盘，使 REST 处理器不会阻塞在磁盘 I/O 上。文件到 1MB 轮转，最多保留 3 份历史
    /// （<c>UnitySkillsAudit.1.jsonl</c> / <c>.2.jsonl</c> / <c>.3.jsonl</c>）。
    ///
    /// 三种运行模式（Approval / Auto / Bypass）都写同一份日志；这是用户回溯"AI 做 X 之前问过没有"的主要手段。
    /// </summary>
    public static class SkillsAuditLog
    {
        private const string LogFileName = "UnitySkillsAudit.jsonl";
        private const long MaxFileBytes = 1024L * 1024L; // 1MB
        private const int MaxRotatedFiles = 3;
        private const int ReadTailMaxBytes = 256 * 1024; // /audit 端点只读尾部

        private static readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private static readonly object _writeLock = new object();
        private static int _flushScheduled; // Interlocked 守卫
        private static string _cachedDir;
        private static string _cachedPath;

        /// <summary>
        /// 追加一条事件。非阻塞：JSON 行入队后由线程池 worker 落盘。任意线程调用均安全。
        /// </summary>
        public static void Append(string eventType, object data)
        {
            if (string.IsNullOrEmpty(eventType)) return;
            try
            {
                // 在此解析并缓存路径（当前所有调用点都在主线程——见 SkillsHttpServer.cs 的
                // HandlePermissionGrant 注释），好让 ThreadPool 落盘 worker 复用缓存值，而不是在非主线程
                // 读 Application.dataPath——那样会静默回退到 Path.GetTempPath()（见 ResolveLibraryDir），
                // 把本次会话的审计轨迹劈成两个文件。
                GetLogPath();
                var line = BuildLine(eventType, data);
                _queue.Enqueue(line);
                ScheduleFlush();
            }
            catch (Exception ex)
            {
                // 审计日志绝不能拖垮调用方，尽力而为并吞掉异常。
                SkillsLogger.LogWarning($"AuditLog enqueue failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取最近至多 <paramref name="limit"/> 条记录（最新在前）。
        /// 只读尾部（末尾约 256KB），使耗时不随文件大小增长。
        /// 返回已解析的 JObject，序列化由调用方自行处理。
        /// </summary>
        public static IList<object> ReadRecent(int limit)
        {
            if (limit <= 0) limit = 100;
            // 先落盘待写项，保证读到的内容包含所有已 Append 的记录。
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
                        // 从行中间开始时丢弃残缺的首行。
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
                        // 跳过畸形行，不因单行失败而整次读取失败。
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"AuditLog read failed: {ex.Message}");
            }
            return results;
        }

        /// <summary>解析审计日志绝对路径（首次调用后缓存）。</summary>
        public static string GetLogPath()
        {
            if (_cachedPath != null) return _cachedPath;
            _cachedDir = ResolveLibraryDir();
            _cachedPath = Path.Combine(_cachedDir, LogFileName);
            return _cachedPath;
        }

        /// <summary>
        /// 按 (ts, type) 二元组从主日志中删除单条记录（两者合起来实际唯一——ts 为毫秒精度 UTC）。
        /// 刻意不动轮转历史文件，只重写主文件，以免放大 I/O 或损坏旧日志。
        /// 返回实际删除的行数（未找到为 0，通常为 1）。
        /// 删除后写入 <c>audit_deleted</c> 示踪事件，让"删除"这一动作本身也被审计——这是日志作为信任锚点的关键。
        /// </summary>
        public static int DeleteEntry(string ts, string type)
        {
            if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(type)) return 0;
            FlushSync();
            int removed = RewritePrimary(line =>
            {
                Newtonsoft.Json.Linq.JObject obj;
                try { obj = Newtonsoft.Json.Linq.JObject.Parse(line); }
                catch { return true; } // 无法解析的行原样保留
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
        /// 清空主日志及全部轮转副本。返回删除的总字节数（近似值，供 toast 显示）。
        /// 随后在已清空的日志里写入 <c>audit_cleared</c> 示踪事件，使清空动作本身留痕。
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
        /// 内部：在调用线程上同步排空队列。
        /// 由 <see cref="ReadRecent"/> 和测试使用，以保证写入可见。
        /// </summary>
        internal static void FlushSync()
        {
            FlushPending();
        }

        /// <summary>内部：清除磁盘日志及轮转副本，仅供测试使用。</summary>
        internal static void ResetForTests()
        {
            FlushPending();
            try
            {
                var dir = ResolveLibraryDir();
                foreach (var f in Directory.EnumerateFiles(dir, "UnitySkillsAudit*.jsonl"))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }
            }
            catch { /* 忽略 */ }
        }

        // ===== 内部实现 =====

        private static string BuildLine(string eventType, object data)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                ["type"] = eventType,
            };
            if (data != null)
            {
                // 把 data 对象摊平成顶层字段，保持日志对 grep 友好。
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
                try
                {
                    // ??= 会把解析结果写回 _cachedDir（而非仅存局部变量），这样即使 Append 的主线程预热
                    // 被绕过、此处在 worker 线程解析，后续调用也能复用同一结果，而不是每次静默重解析
                    // （并可能回退到不同的临时目录）。
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
        /// 逐行读取主日志，只保留 <paramref name="keep"/> 返回 true 的行，并原子重写文件（临时文件 + 替换）。
        /// 返回被删除的行数。通过 <c>_writeLock</c> 与并发落盘互斥。
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

                    // File.Replace(tmp, path, null) 才是真正的原子交换（不留备份文件，因为 path 是已有
                    // 轮转副本的 JSONL 日志）：不存在 `path` 缺失的时间窗，不像 Delete 再 Move 那样，
                    // 两次调用之间崩溃会彻底丢掉主日志。
                    // File.Replace 要求目标已存在；RewritePrimary 上面已在不存在时提前返回，故此前提成立，
                    // 除非 `path` 在那次检查与此处之间被外部移除（两处都在 _writeLock 内，不会是本代码所为）
                    // ——这种极罕见情形下退回普通 move，而不是丢弃已重写的内容。
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
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略 */ }
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
                SkillsLogger.LogWarning($"AuditLog rotate failed: {ex.Message}");
            }
        }

        private static string RotatedPath(int n)
        {
            var dir = _cachedDir ?? ResolveLibraryDir();
            return Path.Combine(dir, $"UnitySkillsAudit.{n}.jsonl");
        }

        /// <summary>
        /// 返回 <c>&lt;project&gt;/Library</c>。在 Unity 编辑器尚未就绪时访问（如 worker 线程上的早期静态初始化）
        /// 则回退到 <c>Application.persistentDataPath</c>。
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
    }
}

// Producer:Betsy
