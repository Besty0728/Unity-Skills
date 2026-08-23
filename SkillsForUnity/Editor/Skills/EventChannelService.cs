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
    /// 支撑 GET /events 长轮询的内存事件通道：编辑器侧事件源发布，HTTP 侧等待者读取，
    /// 让 REST API 从纯拉取变成"Unity 也能推"。
    ///
    /// 线程契约（与 SkillsHttpServer 的生产者-消费者划分一致）：
    /// - Publish 及所有事件源回调只在主线程执行（序列化载荷、追加环形缓冲、
    ///   通过 SessionState 持久化 seq、置唤醒信号）。
    /// - TryReadEventsAfter / GetCurrentSeq / ResetSignal / WaitSignal 可在线程池线程上安全调用：
    ///   不碰任何 Unity API，也不碰 SessionState；缓冲访问由锁保护，
    ///   临界区内只做列表的追加 / 复制（载荷序列化在锁外完成）。
    /// - 长轮询的正确性靠等待者每 250ms 重扫一次缓冲；信号只用来降低延迟，
    ///   因此多消费者下 Reset 的竞态无害。
    ///
    /// 持久化：只有 seq 计数器跨域重载存活（SessionState），保证游标不会倒退；
    /// 缓冲中的事件随旧域一起丢失——客户端通过 oldestSeq/dropped 察觉断档，
    /// 并从 server_restored 事件得知编译结果。
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

        // 环形缓冲与 seq 计数器由主线程（Publish）和线程池等待者
        // （TryReadEventsAfter/GetCurrentSeq）共享，一切访问都必须经过 _bufferLock。
        private static readonly object _bufferLock = new object();
        private static readonly Queue<BufferedEvent> _buffer = new Queue<BufferedEvent>(BufferCapacity + 1);
        private static long _seq;

        // 由 Publish（主线程）置位，由长轮询等待者（线程池）Reset / Wait。
        private static readonly ManualResetEventSlim _signal = new ManualResetEventSlim(false);

        // console_error 的限流状态，仅主线程访问（logMessageReceived 非 Threaded 版本）。
        private static long _consoleWindowStartTicks;
        private static int _consoleErrorsThisWindow;
        private static long _consoleDroppedSinceLast;
        // 防止 Publish 失败时打的日志再次进入 OnLogMessageReceived 造成递归。
        private static bool _publishingConsoleError;

        static EventChannelService()
        {
            try
            {
                // 恢复 seq 计数器，使客户端跨域重载持有的游标不会看到 seq 倒退。
                // C# 保证静态构造先于任何静态成员访问执行，因此必定早于第一次 Publish。
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
        /// 向通道发布一个事件。仅限主线程（要序列化载荷、要碰 SessionState）。
        /// <paramref name="type"/> 必须是纯标识符（snake_case，不含引号或转义）——
        /// 它会被不加转义地直接嵌进 JSON。
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
                    // seq 的分配必须留在锁内，读者才不会看到某个 seq 而缓冲里还没有对应事件。
                    // 这里的字符串拼接很轻；昂贵的 JsonConvert 已在锁外完成。
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
                // 只能 LogWarning，绝不能 LogError：Error 会重新进入 console_error 事件源
                // （logMessageReceived），有递归风险。
                SkillsLogger.LogWarning($"EventChannel publish failed for '{type}': {ex.Message}");
            }
        }

        /// <summary>
        /// 发布 server_restored，附带上次编译结果摘要。编译成功时的 compilation_finished
        /// 事件是在旧域里发布的，会随内存缓冲一起在重载中消失——重连的客户端改从这个事件
        /// 得知编译结果。仅限主线程。
        /// </summary>
        internal static void PublishServerRestored(int port)
        {
            object lastCompilation = null;
            try
            {
                string json = CompilationResultService.GetLastCompilationJson();
                if (!string.IsNullOrEmpty(json))
                {
                    // DateParseHandling.None 保住 finishedAtUtc 的原始 ISO-8601 字符串；
                    // 直接用 JObject.Parse 会把它强转成本地化的 Date.ToString()。
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
        /// 把缓冲中 seq &gt; <paramref name="since"/> 的事件（可按类型过滤）已备好的 JSON
        /// 复制进 <paramref name="jsons"/>，至少命中一条时返回 true。
        /// 可在非主线程安全调用——不碰任何 Unity API。
        /// <paramref name="cursor"/> 是当前最大 seq（扫描上界：即便类型过滤跳过了事件，
        /// 也应把它作为下次的 since 传回）。<paramref name="oldestSeq"/> 是缓冲中最老事件的 seq，
        /// 缓冲为空时取 max+1，表示"再老的都没有了"。
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

        /// <summary>当前最大 seq，用作默认的 since（即"只等新事件"）。线程安全。</summary>
        public static long GetCurrentSeq()
        {
            lock (_bufferLock)
                return _seq;
        }

        /// <summary>
        /// 重置唤醒信号。必须在扫描缓冲之前调用，这样扫描之后到来的发布能重新置位；
        /// 与其他等待者的竞态无害，因为每个等待者无论如何都会以 250ms 间隔重扫。线程安全。
        /// </summary>
        public static void ResetSignal() => _signal.Reset();

        /// <summary>阻塞直到有事件发布或超时，以先到者为准。线程安全。</summary>
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

        // ===== 事件源（所有回调都在主线程到达） =====

        private static void OnBeforeAssemblyReload()
        {
            // 尽力而为：缓冲马上就随本域一起消失，但已经阻塞在长轮询里的等待者
            // 仍可能被唤醒并把这条事件送出去。
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
