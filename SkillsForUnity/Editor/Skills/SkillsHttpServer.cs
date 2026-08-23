using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills REST API 的生产级 HTTP 服务器。
    ///
    /// 架构：严格的生产者-消费者模型
    /// - HTTP 线程（生产者）：只负责接收请求并入队，绝不调用任何 Unity API。
    /// - 主线程（消费者）：处理全部逻辑，包括路由、限流与技能执行。
    ///
    /// 韧性能力：
    /// - 域重载（脚本编译）后自动重启
    /// - 通过 EditorPrefs 持久化状态
    /// - 优雅停机与恢复
    ///
    /// 这样才能与 Unity 的单线程架构做到 100% 线程安全。
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsHttpServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static Thread _keepAliveThread;
        private static volatile bool _isRunning;
        // volatile：会被 /health 快路径在 HTTP 线程上读取。
        private static volatile int _port = 8090;
        private static readonly string _prefixBase = "http://localhost:";
        private static string _prefix = $"{_prefixBase}{_port}/";

        // 作业队列——HTTP 线程入队，主线程出队并处理。
        //
        // 两条通道，各自内部严格 FIFO：
        // - light：只读、毫秒级的端点（存活探测 / 进度轮询）。每帧全部排空，且不受帧时间预算约束，
        //   使 /health 或 /jobs/{id} 轮询永不排在某个耗时数秒的技能后面。
        // - heavy：执行技能、构建反射缓存或写状态的一切请求。同时受每帧条数上限与毫秒预算约束。
        //
        // 刻意不保证跨通道的顺序（这正是拆分的目的）；需要顺序的调用方必须等到响应再发下一个请求，
        // Python 客户端本来就是这么做的。
        //
        // 用 ConcurrentQueue 而不是 Queue+lock：唯一靠锁保证原子性的地方是 Stop() 的排空，
        // 而那发生在监听线程 join 之后，那时不可能还有并发生产者。
        private static readonly ConcurrentQueue<RequestJob> _lightQueue = new ConcurrentQueue<RequestJob>();
        private static readonly ConcurrentQueue<RequestJob> _heavyQueue = new ConcurrentQueue<RequestJob>();
        // 用 Interlocked 计数器镜像队列深度：ConcurrentQueue.Count 需要遍历分段，
        // 而准入控制与 /health 每来一个请求都要读一次深度。
        private static int _lightQueued = 0;
        private static int _heavyQueued = 0;
        private static bool _updateHooked = false;
        private static int _pendingRequests = 0;

        // heavy 通道的两道闸门，每启动一个作业前都会评估。条数上限约束突发量，毫秒预算约束单帧耗时。
        // 单个技能自己就可能超出预算——预算无法打断正在跑的技能，只能拒绝再启动下一个，
        // 而这正是长队列下编辑器仍能重绘的原因。
        private const int MaxHeavyJobsPerFrame = 20;
        private const double HeavyFrameBudgetSeconds = 0.012;

        private const int MaxRequestsPerSecond = 100;
        private const int MaxQueuedRequests = 200;
        private const int MaxPendingRequests = 300;
        private static readonly ConcurrentBag<RequestJob> _requestJobPool = new ConcurrentBag<RequestJob>();
        private static int _poolSize;

        // 在监听线程上做准入限流，避免队列与线程爆掉。
        private static int _admittedThisSecond = 0;
        private static long _lastAdmissionResetTicks = 0;
        
        // 检查待处理作业的 keep-alive 轮询间隔（毫秒）。
        private const int KeepAlivePollingMs = 50;

        // 无条件唤醒主线程的间隔，可配置。
        private const string PrefKeyKeepAliveInterval = "UnitySkills_KeepAliveIntervalSeconds";

        // KeepAliveIntervalSeconds 的线程安全缓存副本（EditorPrefs 只能在主线程读）
        private static long _cachedKeepAliveIntervalTicks = 10L * TimeSpan.TicksPerSecond;

        /// <summary>
        /// keep-alive 线程强制唤醒主线程的间隔（秒），即使没有待处理作业也照样唤醒。
        /// 使 Unity 失焦时看门狗与心跳仍能运转。默认 10 秒，最小 1 秒。
        /// </summary>
        public static int KeepAliveIntervalSeconds
        {
            get => Mathf.Max(1, EditorPrefs.GetInt(PrefKeyKeepAliveInterval, 10));
            set
            {
                EditorPrefs.SetInt(PrefKeyKeepAliveInterval, Mathf.Max(1, value));
                _cachedKeepAliveIntervalTicks = (long)Mathf.Max(1, value) * TimeSpan.TicksPerSecond;
            }
        }
        // 请求处理超时——为线程安全而缓存（EditorPrefs 只能在主线程读）
        private static int _cachedTimeoutMs = 15 * 60 * 1000;
        private static int RequestTimeoutMs => _cachedTimeoutMs;
        internal static void RefreshTimeoutCache() => _cachedTimeoutMs = RequestTimeoutMinutes * 60 * 1000;
        private const int MaxBodySizeBytes = 10 * 1024 * 1024; // 10MB
        // 注册表心跳间隔（秒）
        private const double HeartbeatInterval = 30.0;
        private static double _lastHeartbeatTime = 0;

        // 看门狗：定期确认监听线程存活，否则重启
        private const double WatchdogInterval = 15.0;
        private static double _lastWatchdogCheck = 0;

        // 兜底：delayCall 没触发时，域重载后仍能恢复服务器
        private const double SafetyNetInterval = 5.0;
        private static double _lastSafetyNetCheck = 0;

        // KeepAlive：无条件唤醒间隔（ticks；5 秒 = 50_000_000 ticks）
        private static long _lastForceWakeTicks = 0;

        // 统计量
        private static long _totalRequestsProcessed = 0;
        private static long _totalRequestsReceived = 0;

        // 启动诊断：统计 Start() 之后 ProcessJobQueue 的 tick 次数，供自检使用
        private static volatile int _pjqTicksSinceStart = -1;

        // ===== 主线程存活镜像 + /health 快照 =====
        //
        // 本块中的一切只在主线程写入，由 SendHealthFastPath 在 HTTP 监听线程上读取。
        // 它的存在使 GET /health 不必进作业队列即可作答：过去探测会卡在正在跑的长技能后面，
        // 于是"服务器死了"和"Unity 正忙"在客户端看来完全一样。

        // 最近一次 ProcessJobQueue 帧的 DateTime.UtcNow.Ticks。C# 不允许 `volatile long`，
        // 故经 Interlocked 访问——在 32 位构建上同样是原子的。
        private static long _mainThreadTickUtc = 0;

        // 需要读 Unity API / EditorPrefs 的值，镜像进普通静态字段。
        private static volatile string _snapUnityVersion;
        private static volatile string _snapInstanceId;
        private static volatile string _snapProjectName;
        private static volatile string _snapCurrentMode;
        private static volatile bool _snapPanelApprovalRequired;
        private static volatile string _snapSurfaceProfile = SkillsSurfaceProfile.WireFull;
        private static volatile int _snapPendingCount;
        private static volatile int _snapAllowlistCount;
        private static volatile bool _snapAutoStart = true;
        private static volatile int _snapRequestTimeoutMinutes = 15;
        private static volatile bool _snapIsCompiling;
        private static volatile bool _snapIsUpdating;
        // 首次完整刷新落地之前，快路径一律放弃，/health 回落到主线程队列，
        // 而不是上报占位值。
        private static volatile bool _snapReady;
        // 由 SkillsModeManager.OnChanged / SkillsSurfaceProfile.OnChanged 钩子在任意线程置位，
        // 在下一个主线程帧被消费。用标志位而不是就地刷新，是为了让 RefreshHealthSnapshot 里
        // 所有 Unity API 读取都留在主线程，不管事件是谁抛的。
        private static volatile bool _healthSnapshotDirty = true;
        private static bool _modeHookInstalled = false;

        // 没有任何 OnChanged 时，快照"昂贵那一半"的刷新下限。用来捕捉事件看不到的漂移：
        // grant TTL 过期、绕过 manager 直接改的 prefs。
        private const double HealthSnapshotInterval = 1.0;
        private static double _lastHealthSnapshot = 0;

        // ===== gzip 响应体缓存（HTTP 线程） =====
        //
        // 仅用于 GET /skills 与 GET /skills/schema——只有这两个响应体大到值得压缩
        // （summary 约 143KB，完整 schema 约 618KB）。以 ETag 为键，而 ETag 是内容哈希，
        // 因此条目自失效：内容变了键就变，旧键再也不会被请求。压缩是纯 CPU、不碰 Unity API，
        // 所以放在 HTTP 线程上是合法的；618KB 那一趟耗时数十毫秒，且只在缓存未命中时才跑。
        private const int GzipMinBytes = 4096;
        private const int MaxGzipCacheEntries = 32;
        private const long MaxGzipCacheBytes = 8L * 1024 * 1024;
        private static readonly ConcurrentDictionary<string, byte[]> _gzipCache =
            new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly object _gzipCacheLock = new object();
        private static long _gzipCacheBytes = 0;

        // 复用 SkillsCommon 的 JSON 设置（单一定义，不重复）
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;
        
        // 域重载恢复用的持久化键（项目级作用域）——惰性缓存
        private static string PrefKey(string key) => $"UnitySkills_{RegistryService.InstanceId}_{key}";

        private static string _prefServerShouldRun;
        private static string _prefAutoStart;
        private static string _prefStartOnEditorLaunch;
        private static string _prefTotalProcessed;
        private static string _prefLastPort;
        private static string _prefConsecutiveFailures;
        private static string PREF_SERVER_SHOULD_RUN => _prefServerShouldRun ??= PrefKey("ServerShouldRun");
        private static string PREF_AUTO_START => _prefAutoStart ??= PrefKey("AutoStart");
        private static string PREF_START_ON_EDITOR_LAUNCH => _prefStartOnEditorLaunch ??= PrefKey("StartOnEditorLaunch");
        private static string PREF_TOTAL_PROCESSED => _prefTotalProcessed ??= PrefKey("TotalProcessed");
        private static string PREF_LAST_PORT => _prefLastPort ??= PrefKey("LastPort");
        private static string PREF_CONSECUTIVE_FAILURES => _prefConsecutiveFailures ??= PrefKey("ConsecutiveRestartFailures");
        private const int MaxConsecutiveFailures = 10;

        // 域重载跟踪
        // volatile：会被 HTTP 线程（/health 快路径）与 ThreadPool 应答器（超时诊断）读取，
        // 只在主线程写入。
        private static volatile bool _domainReloadPending = false;

        public static bool IsRunning => _isRunning;
        public static string Url => _prefix;
        public static int Port => _port;
        public static int QueuedRequests => Volatile.Read(ref _lightQueued) + Volatile.Read(ref _heavyQueued);
        public static long TotalProcessed => Interlocked.Read(ref _totalRequestsProcessed);

        public static void ResetStatistics()
        {
            Interlocked.Exchange(ref _totalRequestsProcessed, 0);
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, "0");
        }
        
        /// <summary>
        /// 服务器是否自动启动。为 true 时，域重载后会自动重启。
        /// </summary>
        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(PREF_AUTO_START, true);
            set => EditorPrefs.SetBool(PREF_AUTO_START, value);
        }

        public static bool StartOnEditorLaunch
        {
            get => EditorPrefs.GetBool(PREF_START_ON_EDITOR_LAUNCH, false);
            set => EditorPrefs.SetBool(PREF_START_ON_EDITOR_LAUNCH, value);
        }

        private const string PrefKeyPreferredPort = "UnitySkills_PreferredPort";

        /// <summary>
        /// 服务器首选端口。0 = 自动（扫描 8090-8100），否则使用指定端口。
        /// </summary>
        public static int PreferredPort
        {
            get => EditorPrefs.GetInt(PrefKeyPreferredPort, 0);
            set => EditorPrefs.SetInt(PrefKeyPreferredPort, value);
        }

        private const string PrefKeyRequestTimeout = "UnitySkills_RequestTimeoutMinutes";

        /// <summary>
        /// 请求超时（分钟）。默认 15 分钟，最小 1 分钟。
        /// </summary>
        public static int RequestTimeoutMinutes
        {
            get => Mathf.Max(1, EditorPrefs.GetInt(PrefKeyRequestTimeout, 15));
            set
            {
                EditorPrefs.SetInt(PrefKeyRequestTimeout, Mathf.Max(1, value));
                RefreshTimeoutCache();
            }
        }

        /// <summary>
        /// 一个待处理的 HTTP 请求作业。由 HTTP 线程创建，主线程处理。
        /// </summary>
        private class RequestJob
        {
            // 原始 HTTP 数据（由 HTTP 线程写入）
            public HttpListenerContext Context;
            public string HttpMethod;
            public string Path;
            public string Body;
            public long EnqueueTimeTicks;
            public string RequestId;
            public string AgentId;
            public string QueryString;
            // 条件 GET / 内容协商相关请求头。读请求头是纯字符串操作，故由 HTTP 线程在入队时抓取。
            public string IfNoneMatch;
            public string AcceptEncoding;

            // 处理结果（由主线程写入）
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public int PoolReturned;
            // 两个可缓存 GET 端点的 ResponseJson 内容哈希，其余端点为 null。
            // 它同时决定 ETag 头与 gzip 缓存键。
            public string ETag;
            public ManualResetEventSlim CompletionSignal = new ManualResetEventSlim(false);

            public void Prepare(HttpListenerContext context, string httpMethod, string path, string body, string requestId, string agentId, string queryString = null, string ifNoneMatch = null, string acceptEncoding = null)
            {
                Context = context;
                HttpMethod = httpMethod;
                Path = path;
                Body = body;
                EnqueueTimeTicks = DateTime.UtcNow.Ticks;
                RequestId = requestId;
                AgentId = agentId;
                QueryString = queryString;
                IfNoneMatch = ifNoneMatch;
                AcceptEncoding = acceptEncoding;
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                PoolReturned = 0;
                ETag = null;
                CompletionSignal.Reset();
            }

            public void Reset()
            {
                Context = null;
                HttpMethod = null;
                Path = null;
                Body = null;
                EnqueueTimeTicks = 0;
                RequestId = null;
                AgentId = null;
                QueryString = null;
                IfNoneMatch = null;
                AcceptEncoding = null;
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                ETag = null;
                // 注意：PoolReturned 由 ReturnRequestJob/Prepare 维护，不在 Reset 里管
                CompletionSignal.Reset();
            }
        }

        private static long _requestIdCounter = 0;

        private static bool TryReservePendingSlot()
        {
            int pending = Interlocked.Increment(ref _pendingRequests);
            if (pending <= MaxPendingRequests)
                return true;

            ReleasePendingSlot();
            return false;
        }

        private static void ReleasePendingSlot()
        {
            if (Interlocked.Decrement(ref _pendingRequests) < 0)
                Interlocked.Exchange(ref _pendingRequests, 0);
        }

        /// <summary>
        /// 对未被 accept 循环交给应答器的 context 做尽力关闭。
        /// 关闭一个已关闭的 response 是空操作；而不关则会把该 socket 泄漏到编辑器进程结束。
        /// </summary>
        private static void CloseContextSafely(HttpListenerContext context)
        {
            if (context == null) return;
            try { context.Response.Close(); } catch { /* already closed, or client is gone */ }
        }

        private static RequestJob RentRequestJob()
        {
            if (_requestJobPool.TryTake(out var job))
            {
                Interlocked.Decrement(ref _poolSize);
                return job;
            }

            return new RequestJob();
        }

        private static void ReturnRequestJob(RequestJob job)
        {
            if (job == null)
                return;

            if (Interlocked.Exchange(ref job.PoolReturned, 1) == 1)
                return;

            if (Interlocked.Increment(ref _poolSize) > MaxPendingRequests)
            {
                Interlocked.Decrement(ref _poolSize);
                job.CompletionSignal.Dispose();
                return;
            }
            job.Reset();
            _requestJobPool.Add(job);
        }

        private static bool CheckAdmissionRateLimit()
        {
            long now = DateTime.UtcNow.Ticks;

            if (now - _lastAdmissionResetTicks >= TimeSpan.TicksPerSecond)
            {
                _admittedThisSecond = 0;
                _lastAdmissionResetTicks = now;
            }

            _admittedThisSecond++;
            return _admittedThisSecond <= MaxRequestsPerSecond;
        }

        /// <summary>
        /// 双通道作业队列的通道判定。light 表示"只读且耗时在毫秒级"——即 agent 在长技能运行期间
        /// 循环发出的存活/进度轮询。其余一律为 heavy：执行技能、写状态、构建反射缓存或做无界磁盘操作的请求。
        ///
        /// 每个处理器都逐一核实过；未重读某端点的处理器之前，不要把它加进来。
        /// - OPTIONS——直接 204，压根没有处理器。
        /// - GET /health、GET /——只有 ?live=1 的探测会进队列（其余在 HTTP 线程上作答）；
        ///   该处理器读 EditorPrefs 与两个编译标志。
        /// - GET /compile/status——两个 EditorApplication 标志加一个缓存的 SessionState 字符串。
        /// - GET /jobs、/jobs/{id}[/logs|/progress]——BatchPersistence.ListJobs / GetJob 只是投影
        ///   已加载的内存列表；从 GET 处理器走不到任何写路径。
        /// - GET /permission/status——读模式、白名单与待处理授权。PendingGrantRequests 的 getter
        ///   会惰性清扫过期 grant，这是本通道里唯一的写：受 MaxLiveGrants 约束、不碰 Unity，
        ///   且只回收调用方自己的过期令牌。
        ///
        /// 以下虽为只读但刻意归为 heavy：
        /// - GET /analytics——要从磁盘聚合遥测 JSONL。每个 window 缓存 30 秒，但某 window 的首次调用
        ///   是无界 I/O，过不了"毫秒级"这一半条件。
        /// - GET /skills/recommend——会调 SkillRouter.Initialize()（冷域下的一次全量反射扫描），
        ///   然后给每个技能打分。
        /// - GET /skills、/skills/schema——能排到队列里的，按定义就是缓存未命中，
        ///   即那个要构建几百 KB 清单的请求。
        /// </summary>
        private static bool IsLightRequest(string httpMethod, string path)
        {
            if (string.Equals(httpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(path))
                return false;

            if (path == "/" ||
                string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/compile/status", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/permission/status", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase))
                return true;

            return path.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 出队一个作业，并同步维护镜像深度计数器。只有取成功时才自减，
        /// 因此与"已自增但尚未入队"的并发生产者擦肩而过时，计数保持不变。
        /// </summary>
        private static bool TryDequeueJob(ConcurrentQueue<RequestJob> queue, ref int counter, out RequestJob job)
        {
            if (!queue.TryDequeue(out job))
                return false;

            Interlocked.Decrement(ref counter);
            return true;
        }

        /// <summary>
        /// 把某条通道里仍在排队的作业全部以 503 SERVER_STOPPED 失败掉，并释放其等待中的应答器。
        /// 之所以可以不加同步地调用，仅因为 Stop() 是在监听线程 join 之后才执行它的，此时不可能还有生产者在入队。
        /// </summary>
        private static void FailQueuedJobs(ConcurrentQueue<RequestJob> queue, ref int counter)
        {
            while (TryDequeueJob(queue, ref counter, out var job))
            {
                job.StatusCode = 503;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.ServerStopped,
                    "Server stopped",
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                    retryAfterSeconds: 5);
                job.IsProcessed = true;
                job.CompletionSignal?.Set();
            }
        }

        /// <summary>
        /// 把已序列化的错误 JSON 字符串解析回 JObject，使其能经 SendImmediateJsonResponse
        /// 输出而不被二次编码。
        /// </summary>
        private static JObject BuildErrorPayload(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson))
                return new JObject();
            try { return JObject.Parse(rawJson); }
            catch { return new JObject { ["error"] = rawJson }; }
        }

        private static void SendImmediateJsonResponse(HttpListenerContext context, HttpListenerRequest request, int statusCode, object payload)
        {
            HttpListenerResponse response = null;
            try
            {
                response = context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", $"req_{Interlocked.Increment(ref _requestIdCounter):X8}");
                response.Headers.Add("X-Agent-Id", DetectAgent(request));
                response.StatusCode = statusCode;

                string responseJson = JsonConvert.SerializeObject(payload, _jsonSettings);
                byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"SendImmediateJsonResponse failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// 已缓存的 GET /skills 与 /skills/schema 的快路径应答器。运行在 HTTP 监听线程上——
        /// 绝不能触碰 Unity API 或 SkillsLogger（只做请求头、哈希、压缩与 socket 写）。
        /// 会附加 ETag 头，对 If-None-Match 以空体 304 应答，并在客户端要求时提供缓存的 gzip 响应体。
        /// </summary>
        private static void SendCachedGetResponse(HttpListenerContext context, HttpListenerRequest request, string json, string etag)
        {
            HttpListenerResponse response = null;
            try
            {
                response = context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", $"req_{Interlocked.Increment(ref _requestIdCounter):X8}");
                response.Headers.Add("X-Agent-Id", DetectAgent(request));
                response.Headers.Add("X-Fast-Path", "true");
                response.Headers.Add("ETag", $"\"{etag}\"");
                // 同一 URL 现在有两种可能的响应体（identity / gzip）；没有 Vary，
                // 中间代理可能把 gzip 体交给一个从未要求压缩的客户端。
                response.Headers.Add("Vary", "Accept-Encoding");

                // 304 在压缩之前判定：内容没变就该零字节、零 CPU，而不是白跑一趟 gzip。
                if (IfNoneMatchSatisfied(request.Headers["If-None-Match"], etag))
                {
                    response.StatusCode = 304; // Not Modified——不得携带响应体
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                WriteNegotiatedBody(response, json, etag, request.Headers["Accept-Encoding"]);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch { /* Never let fast-path errors kill the listener loop */ }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// 客户端声明支持且压缩体可用时以 gzip 写出响应体，否则写纯 UTF-8。
        /// HTTP 线程快路径与主线程慢路径共用此方法，使两者的内容协商完全一致。
        /// 调用方必须已设好状态码与 content type。
        /// </summary>
        private static void WriteNegotiatedBody(HttpListenerResponse response, string json, string etag, string acceptEncoding)
        {
            byte[] gzipped = etag != null && AcceptsGzip(acceptEncoding)
                ? GetOrBuildGzip(etag, json)
                : null;

            if (gzipped != null)
            {
                response.Headers.Add("Content-Encoding", "gzip");
                response.ContentLength64 = gzipped.Length;
                response.OutputStream.Write(gzipped, 0, gzipped.Length);
                return;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// 客户端在 Accept-Encoding 中列出了 gzip（或 "*"）且未用 q=0 禁用时返回 true。
        /// 刻意做得极简——它只把关两个端点，而真实客户端（requests、curl、浏览器）
        /// 发的都是朴素的 "gzip, deflate"。
        /// </summary>
        private static bool AcceptsGzip(string acceptEncoding)
        {
            if (string.IsNullOrEmpty(acceptEncoding))
                return false;

            foreach (var raw in acceptEncoding.Split(','))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;

                int semi = token.IndexOf(';');
                var coding = (semi >= 0 ? token.Substring(0, semi) : token).Trim();
                if (!coding.Equals("gzip", StringComparison.OrdinalIgnoreCase) && coding != "*")
                    continue;

                if (semi >= 0)
                {
                    var qPart = token.Substring(semi + 1).Trim();
                    if (qPart.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(qPart.Substring(2),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double q) && q <= 0)
                        continue; // 被显式拒绝——继续扫描下一个 token
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 返回 <paramref name="json"/> 的 gzip 体，首次使用时压缩并缓存。
        /// 以下情况返回 null（含义是"原样不压缩发送"）：体积小于 <see cref="GzipMinBytes"/>、
        /// gzip 压不小的内容、以及任何失败——压缩绝不能把一个请求搞失败。
        ///
        /// 纯 CPU 与字符串操作，在 HTTP 线程上安全。键与淘汰策略的理由见缓存声明处。
        /// </summary>
        private static byte[] GetOrBuildGzip(string etag, string json)
        {
            if (string.IsNullOrEmpty(etag) || string.IsNullOrEmpty(json))
                return null;

            if (_gzipCache.TryGetValue(etag, out var cached))
                return cached;

            byte[] compressed = null;
            try
            {
                byte[] raw = Encoding.UTF8.GetBytes(json);
                if (raw.Length < GzipMinBytes)
                    return null; // 帧头加一个额外响应头的开销会大于压缩省下的量

                using (var ms = new System.IO.MemoryStream(raw.Length / 4 + 256))
                {
                    using (var gz = new System.IO.Compression.GZipStream(
                        ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                    {
                        gz.Write(raw, 0, raw.Length);
                    }
                    // 必须等内层 using 把 gzip 尾部刷出后再读长度。
                    if (ms.Length < raw.Length)
                        compressed = ms.ToArray();
                }
            }
            catch
            {
                return null;
            }

            if (compressed == null)
                return null;

            lock (_gzipCacheLock)
            {
                if (_gzipCache.Count >= MaxGzipCacheEntries ||
                    _gzipCacheBytes + compressed.Length > MaxGzipCacheBytes)
                {
                    _gzipCache.Clear();
                    _gzipCacheBytes = 0;
                }
                if (_gzipCache.TryAdd(etag, compressed))
                    _gzipCacheBytes += compressed.Length;
            }
            return compressed;
        }

        /// <summary>
        /// 宽松的 If-None-Match 比较：容忍带引号的值、W/ 弱前缀、逗号分隔列表以及 '*' 通配。
        /// </summary>
        private static bool IfNoneMatchSatisfied(string ifNoneMatch, string etag)
        {
            if (string.IsNullOrEmpty(ifNoneMatch) || string.IsNullOrEmpty(etag))
                return false;

            foreach (var raw in ifNoneMatch.Split(','))
            {
                var candidate = raw.Trim();
                if (candidate == "*") return true;
                if (candidate.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate.Substring(2);
                candidate = candidate.Trim('"');
                if (string.Equals(candidate, etag, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ===== GET /health =====

        /// <summary>
        /// /health 载荷中来自 Unity API 或 EditorPrefs 的那部分。
        /// 两个生产者、同一形状：<see cref="FromSnapshot"/>（HTTP 线程，读镜像静态字段）与
        /// <see cref="FromLive"/>（主线程，真读）。把它们收进同一个结构体、由同一个构建器消费，
        /// 正是防止快路径与 <c>?live=1</c> 漂成两种不同响应形状的手段。
        /// </summary>
        private struct HealthVitals
        {
            public string UnityVersion;
            public string InstanceId;
            public string ProjectName;
            public string CurrentMode;
            public bool PanelApprovalRequired;
            // 用户暴露面档位的 wire 值（"full" / "guide" / "noSceneAuthoring"）。
            // 已弃用的 guideMode 布尔量在 BuildHealthJson 里由它派生，而不是单独镜像一份，
            // 这样两者永远不可能不一致。
            public string SurfaceProfile;
            public int PendingCount;
            public int AllowlistCount;
            public bool AutoRestart;
            public int RequestTimeoutMinutes;
            public bool IsCompiling;
            public bool IsUpdating;

            /// <summary>HTTP 线程安全：只读普通静态字段，零 Unity API。</summary>
            public static HealthVitals FromSnapshot() => new HealthVitals
            {
                UnityVersion = _snapUnityVersion,
                InstanceId = _snapInstanceId,
                ProjectName = _snapProjectName,
                CurrentMode = _snapCurrentMode,
                PanelApprovalRequired = _snapPanelApprovalRequired,
                SurfaceProfile = _snapSurfaceProfile,
                PendingCount = _snapPendingCount,
                AllowlistCount = _snapAllowlistCount,
                AutoRestart = _snapAutoStart,
                RequestTimeoutMinutes = _snapRequestTimeoutMinutes,
                IsCompiling = _snapIsCompiling,
                IsUpdating = _snapIsUpdating,
            };

            /// <summary>仅主线程——会读 Unity API、EditorPrefs 与权限集合。</summary>
            public static HealthVitals FromLive()
            {
                return new HealthVitals
                {
                    UnityVersion = Application.unityVersion,
                    InstanceId = RegistryService.InstanceId,
                    ProjectName = RegistryService.ProjectName,
                    CurrentMode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                    PanelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                    SurfaceProfile = SkillsSurfaceProfile.CurrentWire,
                    PendingCount = SkillsModeManager.PendingGrantRequests.Count,
                    AllowlistCount = SkillsModeManager.AllowlistSkills.Count,
                    // 加限定名：在此嵌套类型内，下面的字段名会遮蔽外层类的同名成员。
                    AutoRestart = SkillsHttpServer.AutoStart,
                    RequestTimeoutMinutes = SkillsHttpServer.RequestTimeoutMinutes,
                    IsCompiling = EditorApplication.isCompiling,
                    IsUpdating = EditorApplication.isUpdating,
                };
            }
        }

        /// <summary>
        /// 仅主线程。把 <see cref="HealthVitals"/> 镜像进 HTTP 线程 /health 路径所读的静态字段。
        ///
        /// full=false 是每帧路径，只碰两个编译标志——都是廉价属性读取，也是唯一真会逐帧变化的指标。
        /// full=true 还会重读 EditorPrefs 与权限集合（AllowlistSkills 要排序并复制，
        /// PendingGrantRequests 要清扫过期项），按编辑器帧率跑这些实在太浪费。
        /// </summary>
        private static void RefreshHealthSnapshot(bool full)
        {
            try
            {
                _snapIsCompiling = EditorApplication.isCompiling;
                _snapIsUpdating = EditorApplication.isUpdating;

                if (!full && _snapReady)
                    return;

                var vitals = HealthVitals.FromLive();
                _snapUnityVersion = vitals.UnityVersion;
                _snapInstanceId = vitals.InstanceId;
                _snapProjectName = vitals.ProjectName;
                _snapCurrentMode = vitals.CurrentMode;
                _snapPanelApprovalRequired = vitals.PanelApprovalRequired;
                _snapSurfaceProfile = vitals.SurfaceProfile;
                _snapPendingCount = vitals.PendingCount;
                _snapAllowlistCount = vitals.AllowlistCount;
                _snapAutoStart = vitals.AutoRestart;
                _snapRequestTimeoutMinutes = vitals.RequestTimeoutMinutes;
                _snapIsCompiling = vitals.IsCompiling;
                _snapIsUpdating = vitals.IsUpdating;
                _snapReady = true;
            }
            catch (Exception ex)
            {
                // 快照过期也严格优于把编辑器 update 循环搞坏；下一帧会重试。
                // 无论如何 mainThreadIdleMs 上报的都是真实值。
                SkillsLogger.LogVerbose($"Health snapshot refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 把健康快照"昂贵那一半"标记为需在下一个主线程帧刷新。挂在
        /// <see cref="SkillsModeManager.OnChanged"/> 与 <see cref="SkillsSurfaceProfile.OnChanged"/> 上，
        /// 使模式 / 授权 / 白名单 / 暴露面档位的变化立刻反映到 /health，
        /// 而不用干等 <see cref="HealthSnapshotInterval"/>。
        /// 置一个 volatile 标志（而非就地刷新），可保证无论事件由哪个线程抛出，
        /// 所有 Unity API 读取都留在主线程。
        /// </summary>
        private static void OnPermissionStateChanged() => _healthSnapshotDirty = true;

        /// <summary>
        /// 序列化 /health 载荷。调用方提供 vitals；此处其余内容都是任意线程都安全的普通静态读取，
        /// 因此这一个方法同时支撑 HTTP 线程快路径与主线程的 <c>?live=1</c> 路径。
        /// </summary>
        private static string BuildHealthJson(HealthVitals v, bool live)
        {
            long tick = Interlocked.Read(ref _mainThreadTickUtc);
            long idleMs = tick == 0
                ? -1L // update 循环还没 tick 过——此时年龄未知，不是零
                : Math.Max(0L, (DateTime.UtcNow.Ticks - tick) / TimeSpan.TicksPerMillisecond);

            int lightQueued = Volatile.Read(ref _lightQueued);
            int heavyQueued = Volatile.Read(ref _heavyQueued);
            int queued = lightQueued + heavyQueued;
            int allowlistCount = v.AllowlistCount;

            string profile = v.SurfaceProfile ?? SkillsSurfaceProfile.WireFull;
            bool isGuide = profile == SkillsSurfaceProfile.WireGuide;
            string surfaceProfileHint =
                isGuide ? "Guide profile: the write skills of GameObject / Component / Material / Scene (and the Sample primitives) are hidden and answer SURFACE_EXCLUDED. Read SKILL_GUIDE.md and instruct the user through the Editor steps; read-only skills there and every other module still work."
                : profile == SkillsSurfaceProfile.WireNoSceneAuthoring ? "noSceneAuthoring profile: scene-authoring write skills are hidden and answer SURFACE_EXCLUDED. Do the rest of the task normally; if it genuinely needs scene authoring, say so and let the user switch the profile back to full."
                : null;

            return JsonConvert.SerializeObject(new
            {
                status = "ok",
                service = "UnitySkills",
                version = SkillsLogger.Version,
                unityVersion = v.UnityVersion,
                instanceId = v.InstanceId,
                projectName = v.ProjectName,
                serverRunning = _isRunning,
                queuedRequests = queued,
                totalProcessed = Interlocked.Read(ref _totalRequestsProcessed),
                autoRestart = v.AutoRestart,
                requestTimeoutMinutes = v.RequestTimeoutMinutes,
                domainReloadRecovery = "enabled",
                architecture = "Producer-Consumer (Thread-Safe)",
                currentMode = v.CurrentMode,
                panelApprovalRequired = v.PanelApprovalRequired,
                pendingCount = v.PendingCount,
                allowlistCount,
                // allowlistCount 的已弃用别名，为向后兼容保留
                // （与 /permission/status 上的 `granted` / `counts.granted` 别名对应）。
                // 待外部消费方迁移完毕后，可在未来某个大版本移除。
                grantedCount = allowlistCount,
                // 用户当前暴露的技能面切片。具有权威性：agent 无法改动它，
                // 被它隐藏的技能在执行时一律以 SURFACE_EXCLUDED 作答。
                surfaceProfile = v.SurfaceProfile,
                // surfaceProfile == "guide" 的已弃用别名，为只认识布尔开关的 2.7 之前客户端保留。
                // 这类客户端会把 noSceneAuthoring 读成 false，即"什么都没隐藏"——
                // 这正是下面那条 hint 要把档位写清楚的原因。
                guideMode = isGuide,
                // 仅在档位不是 full 时才带文本：full 档下无话可说，
                // 而一条无条件的"优先手工步骤"提示（这个字段过去就是那样）
                // 会把 agent 推离用户实际已经开启的自动化。
                surfaceProfileHint = surfaceProfileHint,
                threads = new
                {
                    listenerAlive = _listenerThread?.IsAlive ?? false,
                    keepAliveAlive = _keepAliveThread?.IsAlive ?? false,
                },
                compilation = new
                {
                    isCompiling = v.IsCompiling,
                    isUpdating = v.IsUpdating,
                    domainReloadPending = _domainReloadPending,
                },
                queueStats = new
                {
                    queued,
                    totalReceived = Interlocked.Read(ref _totalRequestsReceived),
                },

                // ---- 2.3 新增（纯增量，未改动任何既有字段的语义） ----
                port = _port,
                // 距上一次 EditorApplication.update tick 抵达我们的毫秒数。正是这个字段让快路径
                // /health 变得有价值：服务器既能即时作答，又能告诉你主线程卡住了。
                // 个位数值代表编辑器空闲健康；数秒则意味着"活着但 Unity 正忙"
                // （长技能、模态对话框、导入），而不是"服务器死了"。
                mainThreadIdleMs = idleMs,
                // 已准入但尚未应答的请求数（队列深度加在途应答器），对应 MaxPendingRequests 准入上限。
                pendingRequests = Volatile.Read(ref _pendingRequests),
                // 双通道作业队列各自的深度；light 每帧排空。
                lightQueued,
                heavyQueued,
                domainReloadPending = _domainReloadPending,
                // 本次会话工作流历史加载失败时为 true：回滚数据已降级，
                // 且文件库清理会一直暂停，直到历史被清空。
                workflowRecoveryMode = WorkflowManager.IsHistoryRecoveryMode,
                // false = 在 HTTP 线程上用最多约 1 秒前的快照作答。
                // true  = 在主线程上实时读取后作答（GET /health?live=1）。
                live,
                note = "If you get 'Connection Refused', Unity may be reloading scripts. Wait 2-3 seconds and retry."
            }, _jsonSettings);
        }

        /// <summary>
        /// GET /health 与 GET / 的 HTTP 线程应答器。所有取值都来自普通静态字段或主线程快照，
        /// 因此零 Unity API、零 EditorPrefs、零 SkillsLogger——契约与 SendCachedGetResponse 相同。
        ///
        /// 目的是让高负载下仍可诊断：在旧的"只走主线程"路径上，单个长技能就能让存活探测本身挂住，
        /// 调用方分不清"服务器死了"和"Unity 正忙"。现在它立即作答，由 mainThreadIdleMs 说明是哪种。
        /// 需要严格实时值的调用方请用 GET /health?live=1。
        /// </summary>
        private static void SendHealthFastPath(HttpListenerContext context, HttpListenerRequest request)
        {
            HttpListenerResponse response = null;
            try
            {
                response = context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", $"req_{Interlocked.Increment(ref _requestIdCounter):X8}");
                response.Headers.Add("X-Agent-Id", DetectAgent(request));
                response.Headers.Add("X-Fast-Path", "true");
                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";

                byte[] buffer = Encoding.UTF8.GetBytes(BuildHealthJson(HealthVitals.FromSnapshot(), live: false));
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch { /* Never let fast-path errors kill the listener loop */ }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// GET /health?live=1（或 live=true）时返回 true——这是主动选择回到主线程队列，
        /// 所有字段都实时读取，而不是取用最多约 1 秒前的快照。
        /// </summary>
        private static bool WantsLiveHealth(string query)
        {
            if (string.IsNullOrEmpty(query))
                return false;

            var qs = SkillRouter.ParseQueryString(query);
            return qs.TryGetValue("live", out var value) &&
                   (value.Equals("1", StringComparison.Ordinal) ||
                    value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        // Agent 识别表：关键词 -> agent ID 映射
        private static readonly (string keyword, string agentId)[] _agentKeywords = new[]
        {
            ("claude", "ClaudeCode"), ("anthropic", "ClaudeCode"),
            ("codex", "Codex"), ("openai", "Codex"),
            ("cursor", "Cursor"),
            ("trae", "Trae"), ("bytedance", "Trae"),
            ("antigravity", "Antigravity"),
            ("opencode", "OpenCode"),
            ("kimi", "KimiCode"),
            ("windsurf", "Windsurf"), ("codeium", "Windsurf"),
            ("cline", "Cline"), ("roo", "Cline"),
            ("amazon", "AmazonQ"), ("aws", "AmazonQ"),
            ("python-requests", "Python"), ("python", "Python"),
            ("curl", "curl"),
        };

        /// <summary>
        /// 从 User-Agent 或 X-Agent-Id 请求头识别 AI Agent
        /// </summary>
        private static string DetectAgent(HttpListenerRequest request)
        {
            // 优先级 1：显式的 X-Agent-Id 请求头
            var explicitId = request.Headers["X-Agent-Id"];
            if (!string.IsNullOrEmpty(explicitId))
                return explicitId;

            // 优先级 2：查表从 User-Agent 识别（用 OrdinalIgnoreCase 避免 ToLowerInvariant 的分配）
            var ua = request.UserAgent ?? "";

            foreach (var (keyword, agentId) in _agentKeywords)
            {
                if (ua.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return agentId;
            }

            // 无法识别
            return string.IsNullOrEmpty(ua) ? "Unknown" : $"Unknown({ua.Substring(0, Math.Min(20, ua.Length))})";
        }

        /// <summary>
        /// 静态构造器——每次域重载后都会被调用。这是脚本编译后自动恢复的关键。
        /// </summary>
        static SkillsHttpServer()
        {
            try
            {
                // 注册编辑器生命周期事件
                EditorApplication.quitting += OnEditorQuitting;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
                CompilationPipeline.compilationStarted += OnCompilationStarted;

                HookUpdateLoop();

                // 判断域重载后是否应自动重启；用延迟调用以确保 Unity 已完全初始化
                EditorApplication.delayCall += () => ScheduleDelayedCall(1.0, CheckAndRestoreServer);

                // 必须在挂好 delayCall 之后再读：PrefKey() 会牵连 RegistryService 的静态初始化，
                // 此处若抛异常会被外层 catch 吞掉，连带把上面那套域重载恢复挂钩一起静默搞没。
                _editorLaunchPending = !SessionState.GetBool(PrefKey("EditorLaunchHandled"), false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] SkillsHttpServer init failed: " + ex);
            }
        }
        
        /// <summary>
        /// 脚本编译前调用——保存状态。
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            _domainReloadPending = true;

            // 关键修复：仅在服务器正在运行时写入 true
            // 当 _isRunning=false（前次重启失败），不覆写——保留已有的 true 意图
            if (_isRunning)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, true);
            }

            // 持久化统计量
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, _totalRequestsProcessed.ToString());

            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Domain Reload detected - server state saved (port {_port}), will auto-restart");
                EditorPrefs.SetInt(PREF_LAST_PORT, _port);
                RegistryService.Unregister(); // 临时注销
                // 主动关闭 HttpListener 以立即释放端口
                _isRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                // 等线程退出，确保端口彻底释放
                try { _listenerThread?.Join(2000); } catch { }
                try { _keepAliveThread?.Join(100); } catch { }
            }
        }
        
        /// <summary>
        /// 脚本编译后调用——恢复状态。
        /// </summary>
        private static void OnAfterAssemblyReload()
        {
            _domainReloadPending = false;
            
            // 恢复重载前的统计量
            var savedTotal = EditorPrefs.GetString(PREF_TOTAL_PROCESSED, "0");
            if (long.TryParse(savedTotal, out long parsed))
            {
                _totalRequestsProcessed = parsed;
            }
            // CheckAndRestoreServer 会经 delayCall 调用
        }
        
        /// <summary>
        /// 编译开始时调用。
        /// </summary>
        private static void OnCompilationStarted(object context)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Compilation started - preparing for Domain Reload...");
            }
        }
        
        /// <summary>
        /// 编辑器退出时调用——干净停机。
        /// </summary>
        private static void OnEditorQuitting()
        {
            // 退出时一律清除——不希望下次 Unity 会话自动启动
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
            EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            Stop();
        }
        
        // CheckAndRestoreServer 的重试计数
        private static int _restoreRetryCount = 0;
        private static bool _editorLaunchPending;
        private static bool _cliColdStartPending;
        private const int MaxRestoreRetries = 3;
        private static readonly double[] RestoreRetryDelays = { 1.0, 2.0, 4.0 }; // 单位：秒

        internal enum AutoStartReason
        {
            None,
            DomainReload,
            EditorLaunch,
            CliColdStart
        }

        /// <summary>
        /// 判断域重载后是否应恢复服务器。经 EditorApplication.delayCall 调用以确保 Unity 已就绪。
        /// Start() 失败时按递增延迟（1s、2s、4s）最多重试 3 次。
        /// </summary>
        private static void CheckAndRestoreServer()
        {
            bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
            // batchmode 排除：`unity test` / `run` / `build` 等无头流程同样跑 [InitializeOnLoad]，
            // 在那里抢占 8090-8100 并向全局注册表广告一个转瞬即逝的实例，会把客户端的多实例
            // 发现引到一个即将退出的进程上。CLI 冷启动走的是 GUI 启动，不受这条限制。
            bool editorLaunchRequested = _editorLaunchPending && StartOnEditorLaunch && !Application.isBatchMode;
            // Unity CLI 冷启动（--args -unityskills-coldstart + 已绑定）：本会话强制拉起一次，
            // 无视 AutoStart/shouldRun 偏好；后续 Domain Reload 走常规恢复路径。
            _cliColdStartPending |= UnityCliService.ConsumeColdStartRequest();
            if (_cliColdStartPending && _restoreRetryCount == 0)
                SkillsLogger.Log("Unity CLI cold start detected — auto-starting server.");

            var reason = GetAutoStartReason(shouldRun && AutoStart, editorLaunchRequested, _cliColdStartPending);
            if (reason != AutoStartReason.None && !_isRunning)
            {
                bool domainReload = reason == AutoStartReason.DomainReload;
                int failures = domainReload ? EditorPrefs.GetInt(PREF_CONSECUTIVE_FAILURES, 0) : 0;

                // 衰减：上次失败距今超过 5 分钟则重置计数
                if (failures > 0)
                {
                    string lastFailTimeKey = PrefKey("LastFailTime");
                    double lastFailTime = 0;
                    double.TryParse(EditorPrefs.GetString(lastFailTimeKey, "0"), out lastFailTime);
                    if (EditorApplication.timeSinceStartup - lastFailTime > 300)
                    {
                        failures = 0;
                        EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
                        SkillsLogger.LogVerbose("[UnitySkills] Consecutive failure counter reset (5 min decay)");
                    }
                }

                if (domainReload && failures >= MaxConsecutiveFailures)
                {
                    SkillsLogger.LogError(
                        $"[UnitySkills] Server restart abandoned after {failures} consecutive failures across Domain Reloads.\n" +
                        "Please restart manually: Window > UnitySkills > Start Server");
                    EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                    _restoreRetryCount = 0;
                    // 这里也要清：否则一个待处理的"编辑器启动"意图会越过这次提前返回活下来，
                    // 在后续某次重载时触发——绕过我们刚刚跳闸的熔断器。
                    CompletePendingAutoStart(reason);
                    return;
                }

                int lastPort = EditorPrefs.GetInt(PREF_LAST_PORT, 0);
                int restorePort = (lastPort >= 8090 && lastPort <= 8100) ? lastPort : PreferredPort;
                SkillsLogger.Log($"Auto-starting server ({reason}, port={restorePort}, attempt {_restoreRetryCount + 1}/{MaxRestoreRetries + 1})...");
                Start(restorePort, fallbackToAuto: true);

                if (_isRunning)
                {
                    // 启动成功（failures 已在 Start() 中清零）
                    _restoreRetryCount = 0;
                    CompletePendingAutoStart(reason);
                }
                else if (_restoreRetryCount < MaxRestoreRetries)
                {
                    double delay = RestoreRetryDelays[_restoreRetryCount];
                    _restoreRetryCount++;
                    ScheduleDelayedCall(delay, CheckAndRestoreServer);
                }
                else
                {
                    // 本轮所有重试耗尽
                    _restoreRetryCount = 0;
                    CompletePendingAutoStart(reason);
                    if (domainReload)
                    {
                        EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, failures + 1);
                        EditorPrefs.SetString(PrefKey("LastFailTime"), EditorApplication.timeSinceStartup.ToString());
                        // 域重载路径保留失败计数：用户需要知道离 MaxConsecutiveFailures 上限
                        // 还有多远，否则排查时看不出熔断即将触发。
                        SkillsLogger.LogError(
                            $"[UnitySkills] Server failed to restart (consecutive failures: {failures + 1}/{MaxConsecutiveFailures}). " +
                            "Will retry on next Domain Reload. Manual start: Window > UnitySkills > Start Server");
                    }
                    else
                    {
                        // EditorLaunch / CliColdStart 每会话只尝试一次，没有跨会话计数可报。
                        SkillsLogger.LogError(
                            $"[UnitySkills] Server auto-start failed ({reason}). Manual start: Window > UnitySkills > Start Server");
                    }
                }
            }
            else
            {
                _restoreRetryCount = 0;
                if (_editorLaunchPending && (!editorLaunchRequested || _isRunning))
                    CompletePendingAutoStart(AutoStartReason.EditorLaunch);
                if (_cliColdStartPending && _isRunning)
                    CompletePendingAutoStart(AutoStartReason.CliColdStart);
            }
        }

        internal static AutoStartReason GetAutoStartReason(bool restoreRequested, bool editorLaunchRequested, bool cliColdStart)
        {
            if (cliColdStart) return AutoStartReason.CliColdStart;
            if (editorLaunchRequested) return AutoStartReason.EditorLaunch;
            if (restoreRequested) return AutoStartReason.DomainReload;
            return AutoStartReason.None;
        }

        private static void CompletePendingAutoStart(AutoStartReason reason)
        {
            if (_editorLaunchPending)
            {
                SessionState.SetBool(PrefKey("EditorLaunchHandled"), true);
                _editorLaunchPending = false;
            }

            if (reason == AutoStartReason.CliColdStart)
            {
                _cliColdStartPending = false;
            }
        }

        /// <summary>
        /// 借 EditorApplication.update 轮询，实现延迟若干秒后回调。
        /// </summary>
        private static void ScheduleDelayedCall(double delaySeconds, Action callback)
        {
            double targetTime = EditorApplication.timeSinceStartup + delaySeconds;
            void Poll()
            {
                if (EditorApplication.timeSinceStartup >= targetTime)
                {
                    EditorApplication.update -= Poll;
                    callback();
                }
            }
            EditorApplication.update += Poll;
        }
        
        private static void HookUpdateLoop()
        {
            if (_updateHooked) return;
            EditorApplication.update += ProcessJobQueue;
            _updateHooked = true;
        }
        
        private static void UnhookUpdateLoop()
        {
            if (!_updateHooked) return;
            EditorApplication.update -= ProcessJobQueue;
            _updateHooked = false;
        }

        public static void Start(int preferredPort = 0, bool fallbackToAuto = false)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Server already running at {_prefix}");
                return;
            }

            try
            {
                HookUpdateLoop();
                RefreshTimeoutCache();
                // 缓存 keep-alive 间隔，供 KeepAliveLoop 线程安全读取
                _cachedKeepAliveIntervalTicks = (long)KeepAliveIntervalSeconds * TimeSpan.TicksPerSecond;

                // 端口探测：8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;

                // 指定了合法的首选端口时先试它
                if (preferredPort >= startPort && preferredPort <= endPort)
                {
                    try
                    {
                        _listener = new HttpListener();
                        _listener.Prefixes.Add($"{_prefixBase}{preferredPort}/");
                        _listener.Prefixes.Add($"http://127.0.0.1:{preferredPort}/");
                        _listener.Start();

                        _port = preferredPort;
                        _prefix = $"{_prefixBase}{_port}/";
                        started = true;
                    }
                    catch
                    {
                        try { _listener?.Close(); } catch { }
                        if (!fallbackToAuto)
                        {
                            SkillsLogger.LogError($"Port {preferredPort} is in use. Try another port or use Auto.");
                            return;
                        }
                        SkillsLogger.LogVerbose($"Port {preferredPort} is in use, falling back to auto-scan...");
                    }
                }

                if (!started)
                {
                    // 自动模式：逐个扫描端口
                    for (int p = startPort; p <= endPort; p++)
                    {
                        try
                        {
                            _listener = new HttpListener();
                            _listener.Prefixes.Add($"{_prefixBase}{p}/");
                            _listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                            _listener.Start();

                            _port = p;
                            _prefix = $"{_prefixBase}{_port}/";
                            started = true;
                            break;
                        }
                        catch
                        {
                            // 端口被占，试下一个
                            try { _listener?.Close(); } catch { }
                        }
                    }
                }

                if (!started)
                {
                    SkillsLogger.LogError($"Failed to find open port between {startPort} and {endPort}");
                    return;
                }

                _isRunning = true;

                // 持久化状态，供域重载恢复使用
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, true);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0); // 成功启动，清除失败计数

                // 注册到全局注册表
                RegistryService.Register(_port);

                // 在监听器开始 accept 之前先填好 /health 快照，使第一次探测就走快路径而不回落到队列。
                // 上面的 Register() 必须先执行——instanceId/projectName 来自它。
                RefreshHealthSnapshot(full: true);
                if (!_modeHookInstalled)
                {
                    SkillsModeManager.OnChanged += OnPermissionStateChanged;
                    SkillsSurfaceProfile.OnChanged += OnPermissionStateChanged;
                    _modeHookInstalled = true;
                }

                // 启动监听线程（生产者——只入队，不碰 Unity API）
                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "UnitySkills-Listener" };
                _listenerThread.Start();

                // 启动 keep-alive 线程（失焦时强制 Unity 继续 update）
                _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true, Name = "UnitySkills-KeepAlive" };
                _keepAliveThread.Start();

                // 这些调用在此安全，因为 Start() 是从主线程调用的
                var skillCount = SkillRouter.SkillCount;
                SkillsLogger.Log($"REST Server started at {_prefix}");
                SkillsLogger.Log($"{skillCount} skills loaded | Instance: {RegistryService.InstanceId}");
                SkillsLogger.LogVerbose($"Domain Reload Recovery: ENABLED (AutoStart={AutoStart})");

                // 初始化心跳计时器，避免启动过程中立刻发出第一次心跳
                _lastHeartbeatTime = EditorApplication.timeSinceStartup;
                _lastWatchdogCheck = EditorApplication.timeSinceStartup;

                // 启动自检用的诊断计数器
                _pjqTicksSinceStart = 0;

                // 强制立即 update 一次，让 ProcessJobQueue 尽快开始处理
                EditorApplication.QueuePlayerLoopUpdate();

                // 自检：稍等一会儿让 update 循环稳定后再验证可达性
                ScheduleDelayedCall(1.5, RunSelfTest);

                // /events 客户端的重连锚点：携带上一次编译摘要，
                // 因为 compilation_finished（成功那条）会随旧域一起消失。
                EventChannelService.PublishServerRestored(_port);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to start: {ex.Message}");
                _isRunning = false;
                // 不清除 PREF_SERVER_SHOULD_RUN — 保留重启意图，下次 Reload 继续尝试
            }
        }

        public static void Stop(bool permanent = false)
        {
            if (!_isRunning) return;
            _isRunning = false;

            // 永久停止时清掉自动重启标志
            if (permanent)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            }

            // 从全局注册表注销
            RegistryService.Unregister();

            try { _listener?.Stop(); } catch { /* Best-effort cleanup on shutdown */ }
            try { _listener?.Close(); } catch { /* Best-effort cleanup on shutdown */ }

            // 等线程结束
            try { _listenerThread?.Join(2000); } catch { }
            try { _keepAliveThread?.Join(2000); } catch { }
            _listenerThread = null;
            _keepAliveThread = null;

            // 准入计数器不能跨越一次停止/重启：仍在途的应答器可能永远跑不到自己的释放逻辑，
            // 残留计数会吃掉下一个服务器实例的配额。ReleasePendingSlot() 会在 0 处夹住，
            // 所以迟到的释放仍然安全。
            Interlocked.Exchange(ref _pendingRequests, 0);

            // 通知所有待处理作业以错误收场。此处在上面 join 监听线程之后执行，
            // 两条通道都已静默，无需加锁。
            FailQueuedJobs(_lightQueue, ref _lightQueued);
            FailQueuedJobs(_heavyQueue, ref _heavyQueued);

            if (permanent)
                SkillsLogger.Log($"Server stopped (permanent)");
            else
                SkillsLogger.LogVerbose($"Server stopped (will auto-restart after reload)");
        }
        
        /// <summary>
        /// 永久停止服务器，不再自动重启。
        /// </summary>
        public static void StopPermanent()
        {
            Stop(permanent: true);
        }
        
        /// <summary>
        /// keep-alive 循环——Unity 失焦时强制其继续 update。
        /// 不直接调用任何 Unity API（走线程安全的 QueuePlayerLoopUpdate）。
        /// </summary>
        private static void KeepAliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(KeepAlivePollingMs);
                    
                    bool hasPendingJobs = QueuedRequests > 0;

                    if (hasPendingJobs)
                    {
                        // 线程安全地唤醒 Unity 主线程
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                    else
                    {
                        // 没有待处理作业时也定期唤醒，好让看门狗与心跳能跑起来
                        long nowTicks = DateTime.UtcNow.Ticks;
                        long intervalTicks = _cachedKeepAliveIntervalTicks;
                        if (nowTicks - _lastForceWakeTicks > intervalTicks)
                        {
                            _lastForceWakeTicks = nowTicks;
                            EditorApplication.QueuePlayerLoopUpdate();
                        }
                    }
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    // Unity 6000.3+ 的 QueuePlayerLoopUpdate 有时会冒出一句无害的
                    // "SetSceneRepaintDirty can only be called from the main thread"，
                    // 而唤醒本身其实成功了。此处压掉噪音；
                    // 队列是否被排空由主线程的 ProcessJobQueue 验证。
                    if (ex is UnityException && ex.Message != null && ex.Message.Contains("main thread"))
                        SkillsLogger.LogVerbose($"KeepAlive wake-up benign: {ex.Message.Split('\n')[0]}");
                    else
                        SkillsLogger.LogWarning($"KeepAlive iteration error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// HTTP 监听循环（生产者）。
        /// 关键约束：本方法跑在后台线程上，不允许任何 Unity API 调用，
        /// 只把原始请求数据入队交给主线程处理。
        ///
        /// 配额与 socket 的生命周期：<see cref="TryReservePendingSlot"/> 之后的一切都包在同一个
        /// try/finally 里，使每条退出路径——包括读取请求体时客户端中途中止上传——
        /// 都恰好释放一次准入配额并关闭 context。配额泄漏是永久性的：
        /// 泄漏 MaxPendingRequests 次之后，后续每个请求都会变成 503 QUEUE_FULL，直到下次域重载。
        ///
        /// 错误退避分两级：accept（GetContext）失败属于监听器级别，保留长退避交给看门狗；
        /// 而单个请求的失败绝不能为一个坏客户端拖住这唯一的 accept 线程。
        /// </summary>
        private static void ListenLoop()
        {
            while (_isRunning)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    if (!_isRunning) break;
                    Thread.Sleep(500); // 避免异常紧循环；必要时由看门狗重启
                    continue;
                }
                catch (ObjectDisposedException) { break; } // 监听器已销毁；由看门狗重启
                catch (Exception)
                {
                    if (!_isRunning) break;
                    Thread.Sleep(1000); // 未知监听器错误时退避；由看门狗介入
                    continue;
                }

                string body = "";
                bool reservedPendingSlot = false;
                bool handedOffToResponder = false;
                RequestJob job = null;

                try
                {
                    // 立即抓取原始数据（不碰 Unity API）
                    var request = context.Request;

                    if (!CheckAdmissionRateLimit())
                    {
                        SendImmediateJsonResponse(context, request, 429, BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.RateLimit,
                            "Rate limit exceeded",
                            details: new { limit = MaxRequestsPerSecond },
                            retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                            retryAfterSeconds: 1)));
                        continue;
                    }

                    reservedPendingSlot = TryReservePendingSlot();
                    if (!reservedPendingSlot)
                    {
                        SendImmediateJsonResponse(context, request, 503, BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.QueueFull,
                            "Too many pending requests",
                            details: new { pendingLimit = MaxPendingRequests },
                            retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                            retryAfterSeconds: 2)));
                        continue;
                    }

                    // 请求行畸形时 Mono 的 HttpListener 会给出 null 的 Url，
                    // 而下面每条路径都会解引用它，所以提前用一个真实响应拒掉。
                    var url = request.Url;
                    if (url == null)
                    {
                        SendImmediateJsonResponse(context, request, 400, BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.NotFound,
                            "Malformed request URI",
                            retryStrategy: SkillErrorResponse.Abort)));
                        continue;
                    }

                    // 快路径：GET /skills、GET /skills/schema 与 GET /health 直接在本 HTTP 线程上
                    // 用主线程构建好的缓存/快照作答（零 Unity API——见
                    // SkillRouter.TryGetCachedGetResponse 与 SendHealthFastPath）。
                    // 未命中则落到常规主线程队列，由它为下次填好缓存/快照。
                    if (request.HttpMethod == "GET")
                    {
                        string fastPath = url.AbsolutePath;

                        // 长轮询：GET /events 从不进主线程队列。accept 循环只把 context 交给
                        // 一个 ThreadPool 等待者——它绝不能在此阻塞（这是唯一的 accept 线程）。
                        // 应答器会在每条退出路径上释放准入配额并关闭 response。
                        if (string.Equals(fastPath, "/events", StringComparison.OrdinalIgnoreCase))
                        {
                            var pollState = new EventsPollState
                            {
                                Context = context,
                                RawQuery = url.Query,
                                RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                                AgentId = DetectAgent(request),
                            };
                            ThreadPool.QueueUserWorkItem(EventsLongPollCallback, pollState);
                            handedOffToResponder = true;
                            continue;
                        }

                        // 存活探测：用主线程快照作答，使繁忙或阻塞的主线程再也不能让 /health 本身挂住。
                        // 首个快照尚未生成时，以及调用方用 ?live=1 要求实时值时，一律放弃快路径（落回队列）。
                        if ((fastPath == "/" || string.Equals(fastPath, "/health", StringComparison.OrdinalIgnoreCase)) &&
                            _snapReady && !WantsLiveHealth(url.Query))
                        {
                            SendHealthFastPath(context, request);
                            continue;
                        }

                        if ((string.Equals(fastPath, "/skills", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(fastPath, "/skills/schema", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(fastPath, "/skills/meta", StringComparison.OrdinalIgnoreCase)) &&
                            SkillRouter.TryGetCachedGetResponse(fastPath, url.Query, out var cachedJson, out var cachedEtag))
                        {
                            SendCachedGetResponse(context, request, cachedJson, cachedEtag);
                            continue;
                        }
                    }

                    if (request.HttpMethod == "POST" && request.ContentLength64 > 0)
                    {
                        if (request.ContentLength64 > MaxBodySizeBytes)
                        {
                            SendImmediateJsonResponse(context, request, 413, BuildErrorPayload(SkillErrorResponse.Build(
                                SkillErrorCode.BodyTooLarge,
                                "Request body too large",
                                details: new { maxSizeBytes = MaxBodySizeBytes, receivedBytes = request.ContentLength64 },
                                retryStrategy: SkillErrorResponse.Abort)));
                            continue;
                        }

                        // 上传被中止会在此抛 IOException——靠下面的 finally 才不会泄漏配额与 socket。
                        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                    }

                    job = RentRequestJob();
                    job.Prepare(
                        context,
                        request.HttpMethod,
                        url.AbsolutePath,
                        body,
                        $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                        DetectAgent(request),
                        url.Query,
                        request.Headers["If-None-Match"],
                        request.Headers["Accept-Encoding"]);

                    Interlocked.Increment(ref _totalRequestsReceived);

                    // 入队交主线程处理，按两条优先级通道分流。
                    // MaxQueuedRequests 仍是两条通道共享的单一配额，准入上限不变，只是服务顺序不同。
                    if (QueuedRequests >= MaxQueuedRequests)
                    {
                        job.StatusCode = 503;
                        job.ResponseJson = SkillErrorResponse.Build(
                            SkillErrorCode.QueueFull,
                            "Request queue is full",
                            details: new { queueLimit = MaxQueuedRequests },
                            retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                            retryAfterSeconds: 2);
                        job.IsProcessed = true;
                        job.CompletionSignal.Set();
                    }
                    else if (IsLightRequest(job.HttpMethod, job.Path))
                    {
                        // 先自增再入队：计数可能短暂偏高，但绝不会因消费者排走一个我们还没计数的项而变负。
                        Interlocked.Increment(ref _lightQueued);
                        _lightQueue.Enqueue(job);
                    }
                    else
                    {
                        Interlocked.Increment(ref _heavyQueued);
                        _heavyQueue.Enqueue(job);
                    }

                    // 用显式 state 对象排入应答器，避免闭包捕获竞态。
                    var handoffJob = job;
                    job = null; // 所有权已交给队列；即便 QueueUserWorkItem 抛异常也不得归还对象池
                    ThreadPool.QueueUserWorkItem(WaitAndRespondCallback, handoffJob);
                    handedOffToResponder = true;
                }
                catch (Exception ex)
                {
                    // 单个请求的失败（上传中止、请求体畸形……）。下面的 finally 会归还配额与 socket，
                    // 所以这里只需短暂让出——在此长睡会为一个坏客户端停住唯一的 accept 线程。
                    if (!_isRunning) break;
                    SkillsLogger.LogVerbose($"Request dropped: {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(50);
                }
                finally
                {
                    if (reservedPendingSlot && !handedOffToResponder)
                        ReleasePendingSlot();
                    if (job != null)
                        ReturnRequestJob(job);
                    if (!handedOffToResponder)
                        CloseContextSafely(context);
                }
            }
        }
        
        /// <summary>
        /// 等待作业完成并发送 HTTP 响应。跑在 ThreadPool 线程上——不允许任何 Unity API 调用。
        /// </summary>
        private static void WaitAndRespondCallback(object state)
        {
            if (state is RequestJob job)
            {
                WaitAndRespond(job);
                return;
            }

            SkillsLogger.LogWarning("WaitAndRespond callback received invalid state.");
        }

        private static void WaitAndRespond(RequestJob job)
        {
            if (job == null)
            {
                SkillsLogger.LogWarning("WaitAndRespond received a null request job.");
                return;
            }

            bool completed = false;
            try
            {
                // 等主线程处理（带超时）
                completed = job.CompletionSignal.Wait(RequestTimeoutMs);
                
                if (!completed)
                {
                    job.StatusCode = 504;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.Timeout,
                        $"Gateway Timeout: Main thread did not respond within {RequestTimeoutMs / 1000} seconds",
                        details: new {
                            domainReloadPending = _domainReloadPending,
                            queuedRequests = QueuedRequests,
                            listenerAlive = _listenerThread?.IsAlive ?? false,
                            keepAliveAlive = _keepAliveThread?.IsAlive ?? false,
                            suggestion = _domainReloadPending
                                ? "Unity is reloading scripts. Wait a few seconds and retry."
                                : "Unity Editor may be paused, showing a modal dialog, or processing a long operation.",
                            manualAction = "If unresponsive, restart via: Window > UnitySkills > Start Server",
                        },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                        retryAfterSeconds: _domainReloadPending ? 5 : 10);
                }
                
                // 发送 HTTP 响应（线程安全）
                SendResponse(job);
            }
            catch (Exception ex)
            {
                // 尽力而为——尝试发送错误响应
                try
                {
                    job.StatusCode = 500;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.Internal,
                        "Internal server error",
                        retryStrategy: SkillErrorResponse.Abort);
                    SendResponse(job);
                }
                catch (Exception ex2)
                {
                    SkillsLogger.LogError($"Fallback response failed: primary={ex.Message}, fallback={ex2.Message}");
                }
            }
            finally
            {
                ReleasePendingSlot();
                ReturnRequestJob(job);
            }
        }
        
        /// <summary>
        /// 发送 HTTP 响应。线程安全（不碰 Unity API）。
        ///
        /// 只有两个可缓存 GET 端点会被设上 job.ETag（由 <see cref="ApplyCacheableGetHeaders"/> 设置）；
        /// 它是否存在决定此处是否启用 ETag/Vary 头与 gzip 协商，
        /// 因此其余所有端点的行为与此前完全一致。
        /// </summary>
        private static void SendResponse(RequestJob job)
        {
            HttpListenerResponse response = null;
            try
            {
                response = job.Context.Response;

                // CORS 响应头
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", job.RequestId);
                response.Headers.Add("X-Agent-Id", job.AgentId);

                if (job.ETag != null)
                {
                    response.Headers.Add("ETag", $"\"{job.ETag}\"");
                    response.Headers.Add("Vary", "Accept-Encoding");
                }

                response.StatusCode = job.StatusCode;

                // 304 到达此处时 ResponseJson 已被清空，因此永不会走到响应体分支，
                // 也永不会带上 Content-Encoding。
                if (!string.IsNullOrEmpty(job.ResponseJson))
                {
                    response.ContentType = "application/json; charset=utf-8";
                    WriteNegotiatedBody(response, job.ResponseJson, job.ETag, job.AcceptEncoding);
                }
            }
            catch { /* Ignore write errors - client may have disconnected */ }
            finally
            {
                try { response?.Close(); } catch { /* Best-effort cleanup */ }
            }
        }

        // ===== GET /events 长轮询 =====

        private const int EventsDefaultTimeoutSeconds = 25;
        private const int EventsMinTimeoutSeconds = 1;
        private const int EventsMaxTimeoutSeconds = 55;
        private const int EventsPollIntervalMs = 250;

        /// <summary>由 accept 循环交给长轮询应答器的原始请求数据。</summary>
        private sealed class EventsPollState
        {
            public HttpListenerContext Context;
            public string RawQuery;
            public string RequestId;
            public string AgentId;
        }

        private static void EventsLongPollCallback(object state)
        {
            if (!(state is EventsPollState poll))
                return;

            try
            {
                RespondEventsLongPoll(poll);
            }
            catch
            {
                // 客户端断开或监听器在轮询中途死掉——重连本就是既定协议；
                // 绝不能让它把 ThreadPool 线程吵闹地搞死。
                // 在 WriteEventsResponse 之前抛出，意味着此时还没人关闭 response。
                CloseContextSafely(poll.Context);
            }
            finally
            {
                ReleasePendingSlot();
            }
        }

        /// <summary>
        /// GET /events 的长轮询应答器。完全跑在 ThreadPool 线程上——零 Unity API、零 SessionState、
        /// 不用 SkillsLogger（约束与 SendCachedGetResponse 相同）。循环"扫描缓冲 → 等待"，
        /// 直到出现比 'since' 更新的事件、超时、或服务器停止（域重载）——然后直接写出响应。
        /// 正确性依赖那 250ms 的轮询；发布信号只是降低延迟。
        /// 查询参数：since（默认取当前最大 seq，即只等新事件；传 0 则回放缓冲）、
        /// timeout（秒，默认 25，夹在 1-55）、types（逗号分隔的过滤器）。
        /// </summary>
        private static void RespondEventsLongPoll(EventsPollState poll)
        {
            var qs = SkillRouter.ParseQueryString(poll.RawQuery);

            long since;
            if (qs.TryGetValue("since", out var sinceRaw))
            {
                if (!long.TryParse(sinceRaw, out since) || since < 0)
                {
                    WriteEventsResponse(poll, 400, SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        $"Invalid 'since' value '{sinceRaw}' — expected a non-negative integer sequence number.",
                        details: new
                        {
                            received = sinceRaw,
                            hint = "Pass the 'cursor' from a previous /events response, 'since=0' to replay the whole buffer, or omit 'since' to wait for new events only.",
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry));
                    return;
                }
            }
            else
            {
                since = EventChannelService.GetCurrentSeq();
            }

            int timeoutSeconds;
            if (qs.TryGetValue("timeout", out var timeoutRaw))
            {
                if (!int.TryParse(timeoutRaw, out timeoutSeconds))
                {
                    WriteEventsResponse(poll, 400, SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        $"Invalid 'timeout' value '{timeoutRaw}' — expected whole seconds.",
                        details: new { received = timeoutRaw, validRange = $"{EventsMinTimeoutSeconds}-{EventsMaxTimeoutSeconds}" },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry));
                    return;
                }
                timeoutSeconds = Math.Max(EventsMinTimeoutSeconds, Math.Min(EventsMaxTimeoutSeconds, timeoutSeconds));
            }
            else
            {
                timeoutSeconds = EventsDefaultTimeoutSeconds;
            }

            string[] typeFilter = null;
            if (qs.TryGetValue("types", out var typesRaw) && !string.IsNullOrWhiteSpace(typesRaw))
            {
                typeFilter = typesRaw.Split(',')
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .ToArray();
                if (typeFilter.Length == 0)
                    typeFilter = null;
            }

            long deadlineTicks = DateTime.UtcNow.Ticks + timeoutSeconds * TimeSpan.TicksPerSecond;
            List<string> events;
            long cursor, oldestSeq;
            bool timedOut = false;

            while (true)
            {
                // 必须在扫描之前 Reset：扫描之后落地的发布会重新置起信号。
                // 另一个等待者的 Reset 仍可能吞掉它，但代价只是多等一个 250ms 轮询间隔，
                // 绝不影响正确性。
                EventChannelService.ResetSignal();

                if (EventChannelService.TryReadEventsAfter(since, typeFilter, out events, out cursor, out oldestSeq))
                    break;

                // 服务器正在停止（域重载在即）：立刻用手上的内容（即空）作答，
                // 让客户端去重连而不是干挂着。
                if (!_isRunning)
                {
                    timedOut = true;
                    break;
                }

                long remainingTicks = deadlineTicks - DateTime.UtcNow.Ticks;
                if (remainingTicks <= 0)
                {
                    timedOut = true;
                    break;
                }

                int waitMs = (int)Math.Min(EventsPollIntervalMs, remainingTicks / TimeSpan.TicksPerMillisecond + 1);
                EventChannelService.WaitSignal(waitMs);
            }

            // since+1 是客户端缺失的第一个 seq；低于 oldestSeq 的都已被淘汰（环形缓冲溢出）
            // 或随域重载丢失。
            bool dropped = since + 1 < oldestSeq;

            var sb = new StringBuilder(128 + events.Count * 256);
            sb.Append("{\"status\":\"ok\",\"events\":[");
            for (int i = 0; i < events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(events[i]);
            }
            sb.Append("],\"cursor\":").Append(cursor)
              .Append(",\"oldestSeq\":").Append(oldestSeq)
              .Append(",\"dropped\":").Append(dropped ? "true" : "false")
              .Append(",\"timedOut\":").Append(timedOut ? "true" : "false")
              .Append('}');

            WriteEventsResponse(poll, 200, sb.ToString());
        }

        /// <summary>
        /// 写出 /events 的 HTTP 响应。ThreadPool 线程——只做请求头、编码与 socket 写
        /// （SendCachedGetResponse/SendResponse 的纯字符串同类方法）。
        /// </summary>
        private static void WriteEventsResponse(EventsPollState poll, int statusCode, string json)
        {
            HttpListenerResponse response = null;
            try
            {
                response = poll.Context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", poll.RequestId);
                response.Headers.Add("X-Agent-Id", poll.AgentId);
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch { /* Never let long-poll write errors bubble */ }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        /// <summary>
        /// 主线程作业处理器（消费者）。
        /// 经 EditorApplication.update 驱动——此处调用任何 Unity API 都是安全的。
        /// </summary>
        private static void ProcessJobQueue()
        {
            // 主线程存活镜像，在本帧其他任何事情之前写入。HTTP 线程用"现在"减去它来上报
            // /health.mainThreadIdleMs，这正是调用方区分"服务器死了"与"Unity 正忙"的依据。
            // 提前写入意味着本次 tick 内的长作业会被下一个探测者计入空闲时长——这才是诚实的读数。
            Interlocked.Exchange(ref _mainThreadTickUtc, DateTime.UtcNow.Ticks);

            // 启动诊断计数器（轻量 volatile 自增，到 10000 停止）
            var diagTick = _pjqTicksSinceStart;
            if (diagTick >= 0 && diagTick < 10000)
                _pjqTicksSinceStart = diagTick + 1;

            double frameStart = EditorApplication.timeSinceStartup;

            // /health 快照：便宜那一半每帧刷，昂贵那一半只在权限状态变化或 1 秒下限到期时刷。
            bool fullSnapshot = _healthSnapshotDirty || !_snapReady ||
                                frameStart - _lastHealthSnapshot >= HealthSnapshotInterval;
            if (fullSnapshot)
            {
                _healthSnapshotDirty = false;
                _lastHealthSnapshot = frameStart;
            }
            RefreshHealthSnapshot(fullSnapshot);

            // 通道 1——light：全部排空，且不受帧预算约束。它们是只读的毫秒级处理器（见 IsLightRequest）；
            // 让它们饿死在慢技能后面正是这次拆分要防的故障，而给它们设上限等于把同一问题缩小规模后重新引入。
            while (TryDequeueJob(_lightQueue, ref _lightQueued, out var lightJob))
                RunJob(lightJob);

            // 通道 2——heavy：两道闸门，条数上限与墙上时钟预算，二者都在启动每个作业之前检查。
            // 单个技能合理地跑上数秒是允许的；预算无法打断它，只能拒绝再启动下一个，
            // 而这正是突发之间编辑器仍能重绘的原因。
            int processed = 0;
            while (processed < MaxHeavyJobsPerFrame)
            {
                // 预算绝不阻挡一帧中的第一个 heavy 作业。繁忙的 light 通道完全可能合理地吃掉整个 12ms，
                // 若让这种情况把 heavy 通道清零，优先级拆分就变成了技能执行被饿死。
                if (processed > 0 && EditorApplication.timeSinceStartup - frameStart >= HeavyFrameBudgetSeconds)
                    break;

                if (!TryDequeueJob(_heavyQueue, ref _heavyQueued, out var heavyJob))
                    break;

                RunJob(heavyJob);
                processed++;
            }

            // 还有剩余工作：立刻请求下一个 tick，而不是等最多 KeepAlivePollingMs 让 keep-alive 线程发现。
            if (Volatile.Read(ref _heavyQueued) > 0)
                EditorApplication.QueuePlayerLoopUpdate();

            double now = EditorApplication.timeSinceStartup;

            // 注册表心跳
            if (_isRunning)
            {
                if (now - _lastHeartbeatTime > HeartbeatInterval)
                {
                    _lastHeartbeatTime = now;
                    RegistryService.Heartbeat(_port);
                }

                // 看门狗：监听线程已死则重启服务器
                if (now - _lastWatchdogCheck > WatchdogInterval)
                {
                    _lastWatchdogCheck = now;
                    bool listenerDead = _listenerThread == null || !_listenerThread.IsAlive;
                    bool listenerNotListening = _listener == null || !_listener.IsListening;

                    if (listenerDead || listenerNotListening)
                    {
                        SkillsLogger.LogWarning($"Watchdog: server unhealthy (threadAlive={!listenerDead}, listening={!listenerNotListening}), restarting...");
                        int port = _port;
                        Stop();
                        Start(port, fallbackToAuto: true);
                    }
                    else
                    {
                        bool keepAliveDead = _keepAliveThread == null || !_keepAliveThread.IsAlive;
                        if (keepAliveDead)
                        {
                            SkillsLogger.LogWarning("Watchdog: keep-alive thread died, restarting...");
                            _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true, Name = "UnitySkills-KeepAlive" };
                            _keepAliveThread.Start();
                        }
                    }
                }
            }

            // 兜底：delayCall 没触发时，域重载后仍能恢复服务器
            if (!_isRunning && !_domainReloadPending)
            {
                if (now - _lastSafetyNetCheck > SafetyNetInterval)
                {
                    _lastSafetyNetCheck = now;
                    bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
                    // 也兜住 editor-launch：首次启动时 shouldRun 恰好是 false（退出时被清），
                    // 否则新路径会是唯一一条 delayCall 不触发就彻底失效的自启路径。
                    bool editorLaunchRequested = _editorLaunchPending && StartOnEditorLaunch && !Application.isBatchMode;
                    if ((shouldRun && AutoStart) || editorLaunchRequested)
                    {
                        int failures = EditorPrefs.GetInt(PREF_CONSECUTIVE_FAILURES, 0);
                        if (failures < MaxConsecutiveFailures)
                        {
                            SkillsLogger.Log("[SafetyNet] Server should be running but isn't — attempting recovery...");
                            int lastPort = EditorPrefs.GetInt(PREF_LAST_PORT, 0);
                            int restorePort = (lastPort >= 8090 && lastPort <= 8100) ? lastPort : PreferredPort;
                            Start(restorePort, fallbackToAuto: true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 把一个已出队的作业跑到完成，并释放其等待中的应答器。
        /// 从 <see cref="ProcessJobQueue"/> 中抽出，使两条通道共享完全相同的错误处理与账目维护。
        /// 仅主线程。
        /// </summary>
        private static void RunJob(RequestJob job)
        {
            try
            {
                ProcessJob(job);
            }
            catch (Exception ex)
            {
                job.StatusCode = 500;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.Internal,
                    ex.Message,
                    details: new { type = ex.GetType().Name },
                    retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
                SkillsLogger.LogWarning($"Job processing error: {ex.Message}");
            }
            finally
            {
                job.IsProcessed = true;
                job.CompletionSignal?.Set();
                Interlocked.Increment(ref _totalRequestsProcessed);
                // 只在请求可能改动过状态时失效场景缓存（POST = 技能执行）
                if (job.HttpMethod == "POST")
                    GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// GET /skills 与 GET /skills/schema 在主线程侧的对应实现，与 HTTP 线程快路径配对：
        /// 给刚构建好的响应体打上 ETag——<see cref="SkillRouter.GetEtagForCachedGet"/> 是从快路径
        /// 所用的同一个缓存键派生出它的——随后在调用方的 If-None-Match 已匹配时把响应压成空体 304。
        ///
        /// 只给 200 的响应体打标签。绝不能把错误响应体挂在一个内容哈希下交给客户端，
        /// 否则客户端会把它缓存起来。
        /// </summary>
        private static void ApplyCacheableGetHeaders(RequestJob job, string path)
        {
            if (job.StatusCode != 200)
                return;

            job.ETag = SkillRouter.GetEtagForCachedGet(path, job.QueryString, job.ResponseJson);
            if (job.ETag != null && IfNoneMatchSatisfied(job.IfNoneMatch, job.ETag))
            {
                job.StatusCode = 304; // Not Modified——不得携带响应体
                job.ResponseJson = null;
            }
        }

        private static void ProcessJob(RequestJob job)
        {
            // 处理 OPTIONS（CORS 预检）
            if (job.HttpMethod == "OPTIONS")
            {
                job.StatusCode = 204;
                job.ResponseJson = "";
                return;
            }
            
            string path = job.Path;

            // 健康检查。只有 HTTP 线程快路径放弃时才会走到这里：要么调用方要求了 ?live=1，
            // 要么首个快照尚未拍下。两种情况载荷形状相同——BuildHealthJson 是该形状的唯一来源。
            if (path == "/" || string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                // 实时读取同时也刷新了镜像，使下一次快路径探测拿到的是最新值。
                RefreshHealthSnapshot(full: true);
                job.StatusCode = 200;
                job.ResponseJson = BuildHealthJson(HealthVitals.FromLive(), live: true);
                return;
            }

            // 编译反馈闭环——权威地回答"我上次改的脚本编译过了吗"。
            // 走主线程路径（与 /health 相同），以便读取实时编辑器状态与上次结果，
            // 后者能撑过一次成功编译所触发的域重载。
            if (string.Equals(path, "/compile/status", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                string lastCompilation = CompilationResultService.GetLastCompilationJson();
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new {
                    status = "ok",
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    domainReloadPending = _domainReloadPending,
                    lastCompilation = lastCompilation != null ? (object)new JRaw(lastCompilation) : null
                }, _jsonSettings);
                return;
            }

            // 执行遥测聚合——回答"哪些技能被调用 / 在失败 / 很慢"。
            // 走主线程路径（与 /health 相同）：读遥测 EditorPref 与 JSONL 文件。
            // 结果在 SkillTelemetryService 内按 window 缓存 30 秒，以限制磁盘读取量。
            if (string.Equals(path, "/analytics", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                var analyticsQs = SkillRouter.ParseQueryString(job.QueryString);
                string window = analyticsQs.TryGetValue("window", out var windowVal) ? windowVal : "24h";
                job.StatusCode = 200;
                job.ResponseJson = SkillTelemetryService.BuildAnalyticsJson(window);
                return;
            }

            // 取技能清单（可带过滤）。
            // 请求只有在 HTTP 线程快路径未命中时才会到主线程，也就是说，这一次调用负责构建缓存。
            // ApplyCacheableGetHeaders 给它打上快路径此后要用的同一个 ETag，
            // 于是持续发 If-None-Match 的客户端从下一个请求起就能拿到 304。
            // 空查询的特例已下沉到 SkillRouter：裸请求该选哪个面（/skills 选 brief，
            // /skills/schema 选 full）如今是与 HTTP 线程快路径共享的同一个决策，
            // 两者不可能对同一 URL 给出不同答案。
            // 被拒的 ?category= / ?operation= 值会以错误响应体返回，绝不能当作清单处理：
            // 用 200 会误报，而给它打 ETag 比不美观严重得多——客户端下一次 If-None-Match 会命中并拿到
            // 无响应体的 304，等于拒绝消失、查询看起来被接受了。把这些错误拼写挡在 _etagCache 之外，
            // 同时也避免一串拼写错误把真正的条目挤出缓存。
            if (string.Equals(path, "/skills", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.ResponseJson = SkillRouter.GetFilteredManifest(job.QueryString, out bool manifestRejected);
                job.StatusCode = manifestRejected ? 400 : 200;
                if (!manifestRejected)
                    ApplyCacheableGetHeaders(job, path);
                return;
            }

            if (string.Equals(path, "/skills/schema", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.ResponseJson = SkillRouter.GetFilteredSchema(job.QueryString, out bool schemaRejected);
                job.StatusCode = schemaRejected ? 400 : 200;
                if (!schemaRejected)
                    ApplyCacheableGetHeaders(job, path);
                return;
            }

            // 会话常量（category/operation 枚举、保留参数名、被跟踪技能列表）
            // 以及 ?wire=v2 会省略的字段默认值。与上面两个端点一样有缓存与 ETag。
            if (string.Equals(path, "/skills/meta", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetMeta();
                ApplyCacheableGetHeaders(job, path);
                return;
            }

            // 按意图推荐技能
            if (string.Equals(path, "/skills/recommend", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetRecommendations(job.QueryString);
                return;
            }

            // 技能依赖链
            if (string.Equals(path, "/skills/chain", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetSkillChain(job.QueryString);
                return;
            }

            // 跨技能聚合执行（每步都跑完整的 Execute 流水线）
            if (string.Equals(path, "/skills/batch", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                HandleSkillsBatchRequest(job);
                return;
            }

            // 作业查询（轻量 GET，为高频进度轮询绕过 skill router）
            if (job.HttpMethod == "GET" &&
                (string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase)))
            {
                HandleJobsRequest(job);
                return;
            }
            
            // 执行 / DryRun / Plan 技能
            if (path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                if (RejectIfCompiling(job))
                    return;

                // 取出技能名（保留原始大小写）并校验
                string skillName = job.Path.Substring(7);
                if (skillName.Contains("/") || skillName.Contains("\\") || skillName.Contains(".."))
                {
                    job.StatusCode = 400;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.InvalidSkillName,
                        "Invalid skill name",
                        details: new { received = skillName },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    return;
                }

                var skillQs = SkillRouter.ParseQueryString(job.QueryString);
                if (!TryResolveRequestMode(job, skillQs, skillName, out var mode))
                    return;
                if (!TryResolveDiff(job, skillQs, skillName, mode, out var captureDiff))
                    return;

                var skillSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    job.StatusCode = 200;
                    switch (mode)
                    {
                        case SkillRouter.RequestMode.DryRun:
                            job.ResponseJson = SkillRouter.DryRun(skillName, job.Body);
                            break;
                        case SkillRouter.RequestMode.Plan:
                            job.ResponseJson = SkillRouter.Plan(skillName, job.Body);
                            break;
                        default:
                            job.ResponseJson = SkillRouter.Execute(skillName, job.Body, captureDiff);
                            SkillsLogger.LogAgent(job.AgentId, skillName);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    job.StatusCode = 500;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.Internal,
                        ex.Message,
                        skill: skillName,
                        details: new { type = ex.GetType().Name },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                        retryAfterSeconds: 3);
                    SkillsLogger.LogWarning($"Skill '{skillName}' error: {ex.Message}");
                }
                skillSw.Stop();
                RecordSkillTelemetry(mode, skillName, job.AgentId, job.ResponseJson, skillSw.ElapsedMilliseconds);
                return;
            }


            // 权限系统：模式 + 授权令牌 + 审计日志。
            if (path.StartsWith("/permission/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/permission", StringComparison.OrdinalIgnoreCase))
            {
                HandlePermissionRequest(job);
                return;
            }


            // 未匹配任何路由
            job.StatusCode = 404;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.NotFound,
                "Not found",
                details: new {
                    endpoints = new[]
                    {
                        "GET /skills",
                        "GET /skills?full=1",
                        "GET /skills/schema",
                        "GET /skills/meta",
                        "GET /skills/recommend",
                        "GET /skills/chain",
                        "POST /skills/batch",
                        "POST /skills/batch?mode=dryRun|transactional",
                        "POST /skill/{name}",
                        "POST /skill/{name}?mode=dryRun",
                        "POST /skill/{name}?mode=plan",
                        "POST /skill/{name}?dryRun=true",
                        "GET /jobs",
                        "GET /jobs/{id}",
                        "GET /jobs/{id}/progress",
                        "GET /jobs/{id}/logs",
                        "GET /health",
                        "GET /compile/status",
                        "GET /events",
                        "GET /analytics",
                        "GET /permission/status",
                        "POST /permission/grant",
                        "POST /permission/approve",
                        "POST /permission/deny",
                        "GET /permission/allowlist",
                        "POST /permission/allowlist/add",
                        "POST /permission/allowlist/remove",
                        "POST /permission/revoke",
                        "GET /permission/audit"
                    }
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// 从已解析的查询串中解析 ?mode= / ?dryRun=。任一参数出现但取值无法识别时返回 false
        /// （并向该作业写入 INVALID_MODE 错误响应）——此种情况下绝不能执行该请求。
        /// 没有这道守卫，把模式拼错的 agent（如 ?mode=dry_run、?dryRun=1）会以为自己在预览，
        /// 而服务器已经悄悄真执行了。
        /// </summary>
        private static bool TryResolveRequestMode(RequestJob job, Dictionary<string, string> qs, string skillName, out SkillRouter.RequestMode mode)
        {
            mode = SkillRouter.RequestMode.Execute;

            if (qs.TryGetValue("mode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
            {
                if (modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.DryRun;
                    return true;
                }
                if (modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.Plan;
                    return true;
                }

                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Unknown mode '{modeValue}' — request was NOT executed.",
                    skill: skillName,
                    details: new
                    {
                        received = modeValue,
                        validValues = new[] { "dryRun", "plan" },
                        hint = "Use '?mode=dryRun' to validate without executing, '?mode=plan' for an execution plan, or omit '?mode=' entirely to execute for real.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            if (qs.TryGetValue("dryRun", out var dryRunVal) && !string.IsNullOrWhiteSpace(dryRunVal))
            {
                if (dryRunVal.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.DryRun;
                    return true;
                }
                if (dryRunVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return true; // 显式 false = 真正执行

                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Invalid dryRun value '{dryRunVal}' — request was NOT executed.",
                    skill: skillName,
                    details: new
                    {
                        received = dryRunVal,
                        validValues = new[] { "true", "false" },
                        hint = "Use '?dryRun=true' (or '?mode=dryRun') to validate without executing; omit the parameter to execute for real.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 为 POST /skill/{name} 解析 ?diff=。语义化的 sceneDiff 只对真执行有意义，
        /// 因此在 ?mode=dryRun / ?mode=plan 下被静默忽略（什么都没执行，也就无从对比）。
        /// 无法识别的取值以 400 拒绝（与 TryResolveRequestMode 一致）而非静默忽略，
        /// 使把 ?diff 拼错的 agent 不会以为自己要到了 diff 而服务器悄悄省略了它。
        /// 仅在取值非法时返回 false（并写出 400）；其余情况会设好 captureDiff。
        /// </summary>
        private static bool TryResolveDiff(RequestJob job, Dictionary<string, string> qs, string skillName, SkillRouter.RequestMode mode, out bool captureDiff)
        {
            captureDiff = false;

            if (!qs.TryGetValue("diff", out var diffValue) || string.IsNullOrWhiteSpace(diffValue))
                return true;

            bool requested;
            if (diffValue.Equals("1", StringComparison.OrdinalIgnoreCase) || diffValue.Equals("true", StringComparison.OrdinalIgnoreCase))
                requested = true;
            else if (diffValue.Equals("0", StringComparison.OrdinalIgnoreCase) || diffValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                requested = false;
            else
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Invalid diff value '{diffValue}' — request was NOT executed.",
                    skill: skillName,
                    details: new
                    {
                        received = diffValue,
                        validValues = new[] { "1", "true", "0", "false" },
                        hint = "Use '?diff=1' (or '?diff=true') to attach a semantic sceneDiff to the success response; omit it or use '?diff=0' for none. Ignored under ?mode=dryRun/plan.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            // diff 只适用于真执行；dryRun/plan 预览没有可对比的对象。
            captureDiff = requested && mode == SkillRouter.RequestMode.Execute;
            return true;
        }

        /// <summary>
        /// Unity 正在编译或有域重载待处理时写出 503 COMPILING 响应，请求被拒则返回 true。
        /// 由 POST /skill/{name} 与 POST /skills/batch 共用（后者匹配不上 "/skill/" 前缀检查）。
        /// </summary>
        private static bool RejectIfCompiling(RequestJob job)
        {
            if (!_domainReloadPending && !ServerAvailabilityHelper.IsCompilationInProgress())
                return false;

            job.StatusCode = 503;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.Compiling,
                "Unity is compiling or reloading scripts",
                details: new {
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating,
                    domainReloadPending = _domainReloadPending,
                    suggestion = "The REST server is temporarily unavailable during compilation. Wait a few seconds and retry.",
                    manualAction = "If this persists, check Unity Editor for compilation errors or stuck dialogs.",
                },
                retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                retryAfterSeconds: _domainReloadPending ? 8 : 5);
            return true;
        }

        // ===== 执行遥测 =====

        /// <summary>
        /// 把一次 POST /skill/{name} 的结果记入 <see cref="SkillTelemetryService"/>。
        /// 用轻量字符串探测（不用 JObject.Parse——这里是单技能热路径）判定 ok 并提取 errorCode。
        /// 完全隔离：遥测失败绝不会改动调用方已算好的业务响应。
        /// </summary>
        private static void RecordSkillTelemetry(SkillRouter.RequestMode mode, string skillName, string agentId, string responseJson, long durationMs)
        {
            try
            {
                string modeStr = mode == SkillRouter.RequestMode.DryRun ? "dryRun"
                               : mode == SkillRouter.RequestMode.Plan ? "plan"
                               : "execute";
                ProbeOutcome(responseJson, mode == SkillRouter.RequestMode.DryRun, out bool ok, out string errorCode);
                SkillTelemetryService.Record(skillName, agentId, modeStr, ok, errorCode, durationMs);
            }
            catch { /* telemetry is best-effort — never surface to the caller */ }
        }

        /// <summary>
        /// 记录 /skills/batch 中一步的结果。批处理循环已持有每步解析好的载荷，
        /// 故 ok/errorCode 直接传入（无需字符串探测）。技能名为 null 或空白（畸形步骤）时记为 "(malformed)"。
        /// mode 按 dryRun 标志取 batch_step 或 batch_step_dryRun。
        /// </summary>
        private static void RecordBatchStep(string skillName, string agentId, bool dryRun, bool ok, string errorCode, long durationMs)
        {
            try
            {
                SkillTelemetryService.Record(
                    string.IsNullOrWhiteSpace(skillName) ? "(malformed)" : skillName,
                    agentId,
                    dryRun ? "batch_step_dryRun" : "batch_step",
                    ok, errorCode, durationMs);
            }
            catch { /* telemetry is best-effort */ }
        }

        /// <summary>
        /// 通过扫描原始 JSON 字符串来判定技能响应——足够便宜可用于热路径，且能容忍嵌套内容。
        /// 错误信封（<c>"status":"error"</c>）判为失败，并从中提取 <c>"errorCode"</c>。
        /// 对 dryRun 预览而言，<c>"valid":false</c> 的结论判为失败并上报 DRYRUN_INVALID
        /// （未知技能的 dryRun 会返回错误信封，被第一道检查捕获）。
        /// </summary>
        private static void ProbeOutcome(string json, bool isDryRun, out bool ok, out string errorCode)
        {
            ok = true;
            errorCode = null;
            if (string.IsNullOrEmpty(json))
                return;

            if (json.IndexOf("\"status\":\"error\"", StringComparison.Ordinal) >= 0)
            {
                ok = false;
                errorCode = ExtractErrorCode(json);
                return;
            }

            if (isDryRun && json.IndexOf("\"valid\":false", StringComparison.Ordinal) >= 0)
            {
                ok = false;
                errorCode = "DRYRUN_INVALID";
            }
        }

        /// <summary>
        /// 提取第一个 <c>"errorCode":"..."</c> 字段的值。字段缺失或为 JSON null 时返回 null
        /// （<c>"errorCode":null</c> 匹配不上带引号的探测模式），
        /// 于是遥测行记下的是 null errorCode，而不是一个错的值。
        /// </summary>
        private static string ExtractErrorCode(string json)
        {
            const string key = "\"errorCode\":\"";
            int idx = json.IndexOf(key, StringComparison.Ordinal);
            if (idx < 0) return null;
            int start = idx + key.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        // ===== 跨技能批量执行 =====

        private const int MaxBatchSteps = 50;

        /// <summary>
        /// POST /skills/batch——在同一个主线程作业内顺序执行多个技能，
        /// 每一步省下一次 HTTP 往返与一次主线程唤醒。
        ///
        /// 请求体：{"steps":[{"skill":"gameobject_create","args":{...}}, ...], "continueOnError":false}
        /// - 每一步都跑完整的 SkillRouter.Execute 流水线（权限闸门、语义校验、undo、审计），
        ///   与单独调用 POST /skill/{name} 完全一致；每一步各有自己的 undo 组，不合并。
        /// - 默认快速失败：第一个失败的步骤即终止批处理，其余步骤上报为 "skipped"。
        ///   continueOnError=true 时失败步骤被记录、批处理继续。授权类响应
        ///   （MODE_RESTRICTED / CONFIRMATION_REQUIRED）无论 continueOnError 如何一律中断——
        ///   它们不可被跳过；该步骤的完整响应（含授权令牌）会被返回，使调用方能走完授权流程
        ///   并重新提交剩余步骤。
        /// - 静态 $param 槽位：请求体层级的 "params":{"name":value,...} 对象用于填充某步结构化 args
        ///   中的占位节点。任意深度上，唯一键为 "$param" 的对象（如 {"$param":"height"}），
        ///   或恰好形如 {"$param":"name","default":X} 的对象，会被替换为 params[name]（存在时），
        ///   否则取其 "default"，再否则该步以 SEMANTIC_INVALID 失败（details.param 指出缺失的槽位）。
        ///   $param 是纯静态替换、不依赖步骤顺序，故在 $ref 之前解析，且 dryRun 与 execute 下行为一致
        ///   （真实值总是存在——槽位缺失在 dry-run 里同样失败，能在回放前暴露缺口）。
        ///   $param 与 $ref 相互正交：一个步骤可以同时用两者，但单个节点只能是其中之一，绝不可两者兼有
        ///   （{"$param":..,"$ref":..} 会被判 SEMANTIC_INVALID）。替换后仍剩下的 $ref 交由下面的 $ref 阶段处理。
        /// - 跨步骤引用：某步结构化 args 内任意深度上，唯一键为 "$ref" 的对象
        ///   （如 {"$ref":"$0.instanceId"}）会在该步执行前被替换。"$N" 是某个更早且成功步骤的 0 基下标；
        ///   点号之后的部分是指向该步已解包结果的 Newtonsoft SelectToken 路径
        ///   （单独的 "$0" 表示整个结果，"$1.items[0].path" 可深入数组）。
        ///   无法解析的引用（格式错误 / 下标越界 / 前向引用 / 被引步骤未成功 / 路径无匹配）
        ///   会让该步以 SEMANTIC_INVALID 失败，随后按常规的快速失败 / continueOnError 规则处理。
        ///   字符串类型 args 内部的引用不会被解析——只扫描结构化 JSON args。
        /// - ?mode=dryRun 校验每一步但不执行任何东西，且永不中断，使 agent 能一次调用预览整个序列。
        ///   dry-run 中 $ref 参数不携带真实值：它们会被从校验体中剥除，只做结构性检查
        ///   （下标范围、顺序、被引技能声明的 Outputs）；此类步骤在 validation.warnings 中
        ///   带上 refsValidated 与 findings。不支持 ?mode=plan。
        /// - ?mode=transactional 让整批要么全成要么全无：未知技能，以及所属技能声明了 MayTriggerReload
        ///   的步骤，都会被前置以 400 拒绝（域重载会清空编辑器撤销栈，回滚承诺将无法兑现），
        ///   continueOnError=true 也因自相矛盾被拒。任一步骤失败时——包括授权中断，其授权令牌仍会返回——
        ///   所有已执行步骤都经 Undo.RevertAllDownToGroup 回退，并被重新标记为 status:"rolled_back"
        ///   （MutatesAssets 技能的步骤会带上 rollbackReliability:"partial"：
        ///   AssetDatabase 的磁盘写入并未被撤销栈完全覆盖）。响应随后报 status:"rolled_back" 与 rolledBack:true。
        ///   transactional 模式可与 $ref 引用自由组合。
        /// - 两种模式同样可以在请求体中指定（"mode":"dryRun"/"transactional"、"dryRun":true）。
        ///   这两个键各自独立解析且查询串优先——见 TryApplyBatchBodyMode——
        ///   响应会回显实际生效的模式（"mode":"dryRun"|"transactional"|"execute"，
        ///   以及历史遗留的 "dryRun" 布尔量），这是调用方确认四种写法里哪个胜出的唯一途径。
        /// </summary>
        private static void HandleSkillsBatchRequest(RequestJob job)
        {
            if (RejectIfCompiling(job))
                return;

            var qs = SkillRouter.ParseQueryString(job.QueryString);
            if (RejectUnknownBatchQueryParams(job, qs))
                return;
            if (!TryResolveBatchRequestMode(job, qs, out bool dryRun, out bool transactional))
                return;
            var batchMode = dryRun ? SkillRouter.RequestMode.DryRun : SkillRouter.RequestMode.Execute;
            if (!TryResolveDiff(job, qs, "/skills/batch", batchMode, out bool captureDiff))
                return;

            if (!TryParseBody(job, out var body)) return;

            if (RejectUnknownBatchBodyKeys(job, body))
                return;
            if (!TryApplyBatchBodyMode(job, body, qs, ref dryRun, ref transactional))
                return;
            if (dryRun)
            {
                // 请求体可能到此刻才把它变成预览——上面解析 ?diff= 时依据的还只是查询串里的模式，
                // 而预览没有可对比的对象。
                captureDiff = false;
                // 也没有需要围栏或回滚的东西。只有两个键来自不同位置时才会走到这里
                // （?mode=transactional 加请求体 {"dryRun":true}），因为单个 'mode' 值不可能同时要求两者；
                // 若继续开着 transactional，ExecuteBatchCore 会开一道 undo 围栏，
                // 然后在第一个非法步骤处回退到它——为一个什么都没执行的请求去动用户的撤销栈。
                transactional = false;
            }

            if (!(body.TryGetValue("steps", StringComparison.OrdinalIgnoreCase, out var stepsToken) && stepsToken is JArray steps) || steps.Count == 0)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "'steps' must be a non-empty array of {skill, args} objects.",
                    details: new
                    {
                        example = new
                        {
                            steps = new object[] { new { skill = "gameobject_create", args = new { name = "Cube" } } },
                            continueOnError = false,
                        },
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            if (steps.Count > MaxBatchSteps)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.SemanticInvalid,
                    $"Too many steps: {steps.Count} (max {MaxBatchSteps}). Split into multiple /skills/batch calls.",
                    details: new { received = steps.Count, max = MaxBatchSteps },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool continueOnError = false;
            if (body.TryGetValue("continueOnError", StringComparison.OrdinalIgnoreCase, out var coeToken)
                && coeToken != null && coeToken.Type != JTokenType.Null
                && !TryReadBatchBool(coeToken, out continueOnError))
            {
                WriteBatchTypeMismatch(job, "continueOnError", coeToken,
                    "Use JSON true/false; the strings \"true\"/\"false\" are accepted too. Until 2.7 any other type was silently read as false, so a batch the caller believed would continue past failures actually stopped at the first one.");
                return;
            }

            // 请求体层级的 "params" 用于填充步骤 args 里的 $param 槽位（静态，与模式无关）。
            JObject batchParams = null;
            if (body.TryGetValue("params", StringComparison.OrdinalIgnoreCase, out var paramsToken) && paramsToken is JObject paramsObj)
                batchParams = paramsObj;

            if (transactional && RejectTransactionalPrecheck(job, steps, continueOnError))
                return;

            var response = ExecuteBatchCore(steps, batchParams, continueOnError, dryRun, transactional, job.AgentId, captureDiff);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(response, _jsonSettings);
        }

        /// <summary>
        /// POST /skills/batch 背后的顺序执行核心：$param 替换、跨步 $ref 解析，
        /// 然后逐步跑完整的单技能流水线（SkillRouter.Execute——权限闸门、undo、审计），
        /// 并带上快速失败 / continueOnError / 授权中断语义以及可选的事务回滚。
        /// 调用方必须传入已校验的非空 steps 数组（transactional 模式还须先跑 RejectTransactionalPrecheck）。
        /// 以 JObject 返回响应体（{status, executed, failed, results, ...}）。
        /// </summary>
        internal static JObject ExecuteBatchCore(JArray steps, JObject batchParams, bool continueOnError,
            bool dryRun, bool transactional, string agentId, bool captureDiff = false)
        {
            int txStartGroup = -1;
            if (transactional)
            {
                // 在撤销时间线上为整批立一道围栏。每一步在 Execute 内部仍会开启（并折叠）自己的 undo 组；
                // 失败时把这道围栏之上的一切一次性回退。
                Undo.IncrementCurrentGroup();
                txStartGroup = Undo.GetCurrentGroup();
            }

            var results = new List<JObject>(steps.Count);
            // 每个成功步骤的已解包结果，供后续步骤通过 $ref 引用。
            var stepResults = new JToken[steps.Count];
            int executedCount = 0;
            int failedCount = 0;
            bool halted = false;
            var batchDiff = captureDiff && !dryRun ? SkillSceneDiff.CreateBatchCapture() : null;

            for (int i = 0; i < steps.Count; i++)
            {
                string stepSkillName = GetBatchStepSkillName(steps[i]);

                if (halted)
                {
                    results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "skipped" });
                    continue;
                }

                var stepSw = System.Diagnostics.Stopwatch.StartNew();

                if (!(steps[i] is JObject step) || string.IsNullOrWhiteSpace(stepSkillName))
                {
                    failedCount++;
                    results.Add(new JObject
                    {
                        ["index"] = i,
                        ["skill"] = stepSkillName,
                        ["status"] = "error",
                        ["error"] = BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.MissingParam,
                            $"steps[{i}] must be an object with a non-empty 'skill' field.",
                            retryStrategy: SkillErrorResponse.RetryFixAndRetry)),
                    });
                    if (!continueOnError && !dryRun) halted = true;
                    RecordBatchStep(stepSkillName, agentId, dryRun, false, "MISSING_PARAM", stepSw.ElapsedMilliseconds);
                    continue;
                }

                string argsJson = "{}";
                JToken argsToken = null;
                if (step.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var rawArgs) &&
                    rawArgs != null && rawArgs.Type != JTokenType.Null)
                {
                    argsToken = rawArgs;
                    argsJson = rawArgs.Type == JTokenType.String
                        ? rawArgs.ToString()
                        : rawArgs.ToString(Formatting.None);
                }

                // ---- 静态 $param 替换（在 $ref 之前解析） ----
                // 纯静态替换，取自请求体层级的 "params" 对象，因此 dryRun 与 execute 的解析结果一致
                // （两种情况下真实值都存在）。替换后 args 里仍剩的 $ref 交由下面的 $ref 阶段处理。
                if (argsToken is JContainer)
                {
                    var paramNodes = FindBatchParamNodes(argsToken, out var paramRefConflict);
                    if (paramRefConflict != null || paramNodes.Count > 0)
                    {
                        string paramErrorJson = null;
                        if (paramRefConflict != null)
                        {
                            paramErrorJson = SkillErrorResponse.Build(
                                SkillErrorCode.SemanticInvalid,
                                $"steps[{i}]: an args node may be $param or $ref, not both — {paramRefConflict.ToString(Formatting.None)}",
                                skill: stepSkillName,
                                details: new { node = paramRefConflict.ToString(Formatting.None), reason = "a single node cannot mix $param and $ref" },
                                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                        }
                        else
                        {
                            // 在深拷贝上替换；原始请求体永不被改动。
                            var paramClone = argsToken.DeepClone();
                            foreach (var paramNode in FindBatchParamNodes(paramClone, out _))
                            {
                                if (!TryResolveBatchParam(paramNode, batchParams, out var value, out var reason))
                                {
                                    paramErrorJson = SkillErrorResponse.Build(
                                        SkillErrorCode.SemanticInvalid,
                                        $"steps[{i}]: cannot resolve $param '{paramNode.ParamName ?? "(non-string)"}' — {reason}",
                                        skill: stepSkillName,
                                        details: new { param = paramNode.ParamName, reason },
                                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                                    break;
                                }
                                var replacement = (value ?? JValue.CreateNull()).DeepClone();
                                if (ReferenceEquals(paramNode.Node, paramClone)) paramClone = replacement;
                                else paramNode.Node.Replace(replacement);
                            }
                            if (paramErrorJson == null)
                            {
                                // 把替换后的 args 送入下面的 $ref 阶段。
                                argsToken = paramClone;
                                argsJson = paramClone.ToString(Formatting.None);
                            }
                        }

                        if (paramErrorJson != null)
                        {
                            failedCount++;
                            results.Add(new JObject
                            {
                                ["index"] = i,
                                ["skill"] = stepSkillName,
                                ["status"] = "error",
                                ["error"] = BuildErrorPayload(paramErrorJson),
                            });
                            if (!continueOnError && !dryRun) halted = true;
                            RecordBatchStep(stepSkillName, agentId, dryRun, false, "SEMANTIC_INVALID", stepSw.ElapsedMilliseconds);
                            continue;
                        }
                    }
                }

                // ---- 跨步骤 $ref 引用 ----
                List<BatchRefNode> refNodes = null;          // dryRun 记账用
                HashSet<string> strippedRefParams = null;    // dryRun：已从校验体中剥除的参数
                bool wholeArgsFromRef = false;               // dryRun：args 根节点本身就是一个 $ref
                List<string> refWarnings = null;             // dryRun：结构性检查发现
                if (argsToken is JContainer)
                {
                    if (dryRun)
                    {
                        refNodes = FindBatchRefNodes(argsToken);
                        if (refNodes.Count > 0)
                        {
                            refWarnings = new List<string>();
                            foreach (var refNode in refNodes)
                                ValidateBatchRefStructural(refNode.RefString, i, steps, refWarnings);

                            // dry-run 期间引用不携带真实值。持有 $ref 的参数会被从校验体中移除——
                            // 把占位对象留在里面只会产生 TYPE_MISMATCH 噪音。由此带来的
                            // MISSING_PARAM 与语义缺口在 DryRun 返回后统一校正
                            // （见 AdjustDryRunPayloadForRefs）。
                            strippedRefParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var refNode in refNodes)
                            {
                                if (refNode.TopLevelParam == null) wholeArgsFromRef = true;
                                else strippedRefParams.Add(refNode.TopLevelParam);
                            }
                            if (wholeArgsFromRef || !(argsToken is JObject argsObj))
                            {
                                argsJson = "{}";
                            }
                            else
                            {
                                var strippedArgs = (JObject)argsObj.DeepClone();
                                foreach (var refNode in refNodes)
                                {
                                    if (refNode.TopLevelParam != null)
                                        strippedArgs.Remove(refNode.TopLevelParam);
                                }
                                argsJson = strippedArgs.ToString(Formatting.None);
                            }
                        }
                    }
                    else
                    {
                        // 在深拷贝上依据先前步骤的结果解析；原始请求体永不被改动。
                        var argsClone = argsToken.DeepClone();
                        var cloneRefs = FindBatchRefNodes(argsClone);
                        if (cloneRefs.Count > 0)
                        {
                            string refErrorJson = null;
                            foreach (var refNode in cloneRefs)
                            {
                                if (!TryResolveBatchRef(refNode.RefString, stepResults, i, steps.Count,
                                        out var resolved, out var reason, out var referencedStep))
                                {
                                    refErrorJson = SkillErrorResponse.Build(
                                        SkillErrorCode.SemanticInvalid,
                                        $"steps[{i}]: cannot resolve $ref '{refNode.RefString ?? "(non-string)"}' — {reason}",
                                        skill: stepSkillName,
                                        details: new { @ref = refNode.RefString, referencedStep, reason },
                                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                                    break;
                                }
                                var replacement = resolved.DeepClone();
                                if (ReferenceEquals(refNode.Node, argsClone)) argsClone = replacement;
                                else refNode.Node.Replace(replacement);
                            }
                            if (refErrorJson != null)
                            {
                                failedCount++;
                                results.Add(new JObject
                                {
                                    ["index"] = i,
                                    ["skill"] = stepSkillName,
                                    ["status"] = "error",
                                    ["error"] = BuildErrorPayload(refErrorJson),
                                });
                                if (!continueOnError) halted = true;
                                RecordBatchStep(stepSkillName, agentId, dryRun, false, "SEMANTIC_INVALID", stepSw.ElapsedMilliseconds);
                                continue;
                            }
                            argsJson = argsClone.ToString(Formatting.None);
                        }
                    }
                }

                string stepJson;
                try
                {
                    if (dryRun)
                    {
                        stepJson = SkillRouter.DryRun(stepSkillName, argsJson);
                    }
                    else
                    {
                        if (batchDiff != null && SkillRouter.TryGetSkill(stepSkillName, out var diffSkill) && !diffSkill.ReadOnly)
                        {
                            try { SkillSceneDiff.CaptureBatchStepBefore(batchDiff, JObject.Parse(argsJson)); }
                            catch { batchDiff.HadWritableSteps = true; }
                        }
                        stepJson = SkillRouter.Execute(stepSkillName, argsJson);
                        // 所有步骤共用同一个 POST 作业，因此 ProcessJobQueue 里的逐请求缓存失效
                        // 不会在步骤之间执行——没有这句，某一步就找不到同批中更早步骤创建的对象。
                        // ReadOnly 步骤按契约无副作用，不可能让缓存过期，故跳过；
                        // 其余每一步——包括名字解析不到已知技能的那种——仍会触发失效。
                        if (!SkillRouter.TryGetSkill(stepSkillName, out var stepSkill) || !stepSkill.ReadOnly)
                            GameObjectFinder.InvalidateCache();
                        SkillsLogger.LogAgent(agentId, $"{stepSkillName} (batch {i + 1}/{steps.Count})");
                    }
                }
                catch (Exception ex)
                {
                    stepJson = SkillErrorResponse.Build(
                        SkillErrorCode.Internal,
                        ex.Message,
                        skill: stepSkillName,
                        details: new { type = ex.GetType().Name },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
                    SkillsLogger.LogWarning($"Batch step {i} '{stepSkillName}' error: {ex.Message}");
                }

                JObject stepPayload;
                try { stepPayload = JObject.Parse(stepJson); }
                catch { stepPayload = new JObject { ["status"] = "error", ["error"] = stepJson }; }

                string stepStatus = stepPayload["status"]?.ToString();

                if (dryRun)
                {
                    // $ref 参数已从校验体中剥除——必须在读取其 'valid' 结论之前先校正载荷
                    // （过滤 missingParams、降级语义错误、附上 refsValidated）。
                    if (refNodes != null && refNodes.Count > 0)
                        AdjustDryRunPayloadForRefs(stepPayload, refNodes, strippedRefParams, wholeArgsFromRef, refWarnings);

                    // DryRun 响应带 status:"dryRun" 与 valid:bool；未知技能返回 status:"error"。
                    // 校验失败绝不会中止 dry-run 批处理。
                    bool stepValid = string.Equals(stepStatus, "dryRun", StringComparison.OrdinalIgnoreCase) &&
                        stepPayload["valid"]?.Type == JTokenType.Boolean && stepPayload["valid"].ToObject<bool>();
                    if (stepValid)
                    {
                        executedCount++;
                        results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "success", ["result"] = stepPayload });
                    }
                    else
                    {
                        failedCount++;
                        results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "error", ["error"] = stepPayload });
                    }
                    RecordBatchStep(stepSkillName, agentId, dryRun, stepValid, stepValid ? null : "DRYRUN_INVALID", stepSw.ElapsedMilliseconds);
                    continue;
                }

                if (string.Equals(stepStatus, "error", StringComparison.OrdinalIgnoreCase))
                {
                    failedCount++;
                    results.Add(new JObject { ["index"] = i, ["skill"] = stepSkillName, ["status"] = "error", ["error"] = stepPayload });

                    // 授权类响应绝不可跳过：调用方必须走完授权/确认流程，
                    // 所以即便 continueOnError=true，批处理也在此停下。上面的完整载荷携带授权令牌。
                    string errorCode = stepPayload["errorCode"]?.ToString();
                    bool authorizationRequired =
                        string.Equals(errorCode, "MODE_RESTRICTED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(errorCode, "CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase);

                    if (authorizationRequired || !continueOnError)
                        halted = true;
                    RecordBatchStep(stepSkillName, agentId, dryRun, false, errorCode, stepSw.ElapsedMilliseconds);
                    continue;
                }

                // status:"success"（或任何非错误形状）——解包内层结果；
                // 条目层级的 status 字段已表达了成功。
                executedCount++;
                var unwrappedResult = stepPayload.TryGetValue("result", out var innerResult) ? innerResult : stepPayload;
                stepResults[i] = unwrappedResult;
                if (batchDiff != null)
                    SkillSceneDiff.TrackBatchStepResult(batchDiff, unwrappedResult);
                results.Add(new JObject
                {
                    ["index"] = i,
                    ["skill"] = stepSkillName,
                    ["status"] = "success",
                    ["result"] = unwrappedResult,
                });
                RecordBatchStep(stepSkillName, agentId, dryRun, true, null, stepSw.ElapsedMilliseconds);
            }

            bool rolledBack = false;
            if (transactional && failedCount > 0)
            {
                // 全成或全无：任何失败（含授权中断——上面失败步骤的条目仍原样携带授权令牌）
                // 都会回退自批处理围栏以来执行的每一步，且不留下重做条目。
                Undo.RevertAllDownToGroup(txStartGroup);
                GameObjectFinder.InvalidateCache();
                rolledBack = true;

                int revertedSteps = 0;
                foreach (var entry in results)
                {
                    if (!string.Equals(entry["status"]?.ToString(), "success", StringComparison.Ordinal))
                        continue;
                    entry["status"] = "rolled_back";
                    revertedSteps++;
                    // AssetDatabase 的磁盘写入并未被撤销栈完全覆盖——
                    // 把这类回滚标为 partial，而不是过度承诺。
                    string entrySkill = entry["skill"]?.ToString();
                    if (!string.IsNullOrEmpty(entrySkill) &&
                        SkillRouter.TryGetSkill(entrySkill, out var entryInfo) && entryInfo.MutatesAssets)
                    {
                        entry["rollbackReliability"] = "partial";
                    }
                }
                SkillsLogger.Log($"Transactional batch rolled back {revertedSteps} executed step(s) after a failed step (undo group {txStartGroup}).");
            }

            var response = new JObject
            {
                ["status"] = failedCount == 0 ? "completed" : (transactional ? "rolled_back" : "partial"),
                // 回显的是实际生效的模式，而不是某一个键要求的模式。?mode= 与 ?dryRun=/请求体 "dryRun"
                // 各自独立解析，且都可能来自 URL 或载荷（见 TryApplyBatchBodyMode），
                // 所以"我这四种写法里到底哪个胜出"是调用方光看自己的请求推不出来的——
                // 而一个以为自己发了预览的调用方，必须能看见它确实拿到了预览。
                ["mode"] = dryRun ? "dryRun" : (transactional ? "transactional" : "execute"),
                ["dryRun"] = dryRun,
            };
            if (transactional)
            {
                response["transactional"] = true;
                response["rolledBack"] = rolledBack;
            }
            response["executed"] = executedCount;
            response["failed"] = failedCount;
            response["results"] = new JArray(results);
            if (batchDiff != null)
                response["sceneDiff"] = SkillSceneDiff.BuildBatch(batchDiff);
            return response;
        }

        /// <summary>跨整个步骤序列聚合出的一个 $param 名（供宏库自省用）。</summary>
        internal sealed class BatchParamDeclaration
        {
            public string Name;
            public bool HasDefault;      // 引用该名字的每个节点都带内联 default
            public JToken DefaultValue;  // 见到的第一个内联 default（仅用于展示）
        }

        /// <summary>
        /// 聚合整个步骤序列中声明的 $param 槽位，按名字为键、以首次出现顺序排列。
        /// 只有引用某名字的每一个节点都带内联 "default" 时，该名字才算有默认值——
        /// 哪怕只有一个裸的 {"$param":"x"} 槽位，该值就是必填的。
        /// 畸形槽位（$param 名不是字符串）在此跳过，由执行阶段逐步骤上报。
        /// 与执行阶段一致，不扫描字符串类型的 args。
        /// </summary>
        internal static List<BatchParamDeclaration> CollectBatchParamDeclarations(JArray steps)
        {
            var byName = new Dictionary<string, BatchParamDeclaration>(StringComparer.Ordinal);
            var ordered = new List<BatchParamDeclaration>();
            if (steps == null)
                return ordered;

            foreach (var step in steps)
            {
                if (!(step is JObject stepObj)
                    || !stepObj.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var args)
                    || !(args is JContainer))
                    continue;

                foreach (var node in FindBatchParamNodes(args, out _))
                {
                    if (node.ParamName == null)
                        continue;
                    if (!byName.TryGetValue(node.ParamName, out var decl))
                    {
                        decl = new BatchParamDeclaration
                        {
                            Name = node.ParamName,
                            HasDefault = node.HasDefault,
                            DefaultValue = node.DefaultValue,
                        };
                        byName[node.ParamName] = decl;
                        ordered.Add(decl);
                    }
                    else if (!node.HasDefault)
                    {
                        decl.HasDefault = false;
                    }
                    else if (decl.DefaultValue == null)
                    {
                        decl.DefaultValue = node.DefaultValue;
                    }
                }
            }
            return ordered;
        }

        private static string GetBatchStepSkillName(JToken stepToken)
        {
            if (stepToken is JObject step &&
                step.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var skillToken) &&
                skillToken != null && skillToken.Type != JTokenType.Null)
            {
                return skillToken.ToString();
            }
            return null;
        }

        /// <summary>
        /// 为 /skills/batch 解析 ?mode= / ?dryRun=。批处理接受 dryRun/transactional
        /// （单技能请求接受的是 dryRun/plan——见 TryResolveRequestMode，它继续拒绝 'transactional'，
        /// 以保证其 INVALID_MODE 的 validValues 准确）。
        /// 任何无法识别的取值都返回 false（并写出错误响应）。
        /// </summary>
        private static bool TryResolveBatchRequestMode(RequestJob job, Dictionary<string, string> qs, out bool dryRun, out bool transactional)
        {
            dryRun = false;
            transactional = false;

            if (qs.TryGetValue("mode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
            {
                if (modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                    return true;
                }
                if (modeValue.Equals("transactional", StringComparison.OrdinalIgnoreCase))
                {
                    transactional = true;
                    return true;
                }

                bool isPlan = modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase);
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    isPlan
                        ? "Batch supports '?mode=dryRun' (validates every step without executing) and '?mode=transactional' (all-or-nothing with rollback); 'plan' is not available for /skills/batch."
                        : $"Unknown mode '{modeValue}' — request was NOT executed.",
                    skill: "skills_batch",
                    details: new
                    {
                        received = modeValue,
                        validValues = new[] { "dryRun", "transactional" },
                        hint = "Use '?mode=dryRun' to validate without executing, '?mode=transactional' for all-or-nothing execution with rollback, or omit '?mode=' entirely to execute fail-fast.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            if (qs.TryGetValue("dryRun", out var dryRunVal) && !string.IsNullOrWhiteSpace(dryRunVal))
            {
                if (dryRunVal.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                    return true;
                }
                if (dryRunVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return true; // 显式 false = 真正执行

                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Invalid dryRun value '{dryRunVal}' — request was NOT executed.",
                    skill: "skills_batch",
                    details: new
                    {
                        received = dryRunVal,
                        validValues = new[] { "true", "false" },
                        hint = "Use '?dryRun=true' (or '?mode=dryRun') to validate without executing; omit the parameter to execute for real.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            return true;
        }

        /// <summary>
        /// POST /skills/batch 认识的顶层请求体键的完整集合，以及它会读取的查询键的完整集合
        /// （见 TryResolveBatchRequestMode 与 TryResolveDiff）。
        /// 其余一律拒绝而非忽略：被静默丢掉的键，正是 agent 以为自己要了预览、或要了异步执行，
        /// 结果两样都没拿到的成因。
        /// </summary>
        private static readonly string[] BatchBodyParams = { "steps", "params", "continueOnError", "dryRun", "mode" };
        private static readonly string[] BatchQueryParams = { "mode", "dryRun", "diff" };

        private static bool IsKnownBatchParam(string[] allowed, string name)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 拒绝 /skills/batch 上无法识别的查询参数（400 UNKNOWN_PARAM）。请求被拒则返回 true。
        /// </summary>
        private static bool RejectUnknownBatchQueryParams(RequestJob job, Dictionary<string, string> qs)
        {
            var unknown = new List<object>();
            foreach (var key in qs.Keys)
            {
                if (IsKnownBatchParam(BatchQueryParams, key))
                    continue;

                var entry = new Dictionary<string, object> { ["parameter"] = key };
                var hint = BatchParamHint(key);
                if (hint != null)
                    entry["hint"] = hint;
                unknown.Add(entry);
            }

            if (unknown.Count == 0)
                return false;

            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.UnknownParam,
                "Unknown query parameter(s) on POST /skills/batch — the batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    unknownParams = unknown,
                    allowedParams = BatchQueryParams,
                    location = "queryString",
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }

        /// <summary>
        /// 拒绝 /skills/batch 上无法识别的顶层请求体键（400 UNKNOWN_PARAM），
        /// 与 CollectUnknownParameters 对单技能 args 的做法一致。
        /// 它在 'steps' 检查之前执行，使像 "step" 这样的拼写错误被报成"未知键"本身，
        /// 而不是报成缺少 'steps'。请求被拒则返回 true。
        /// </summary>
        private static bool RejectUnknownBatchBodyKeys(RequestJob job, JObject body)
        {
            var unknown = new List<object>();
            foreach (var property in body.Properties())
            {
                if (IsKnownBatchParam(BatchBodyParams, property.Name))
                    continue;

                var entry = new Dictionary<string, object> { ["parameter"] = property.Name };
                var hint = BatchParamHint(property.Name);
                if (hint != null)
                    entry["hint"] = hint;
                unknown.Add(entry);
            }

            if (unknown.Count == 0)
                return false;

            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.UnknownParam,
                "Unknown top-level field(s) in the /skills/batch body — the batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    unknownParams = unknown,
                    allowedParams = BatchBodyParams,
                    location = "body",
                    hint = "Per-step fields ('skill', 'args') live inside each element of 'steps', not at the top level.",
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }

        /// <summary>
        /// 针对 agent 实践中真正会写错的那几个 /skills/batch 键给出定向提示：
        /// 单数形式的 "step"、把单个步骤自己的字段提到顶层、把 transactional 当布尔量，
        /// 以及 runAsync——本端点从来没有这个参数，因为它把每一步都放在同一个主线程作业里跑。
        /// 没有具体可说的内容时返回 null。
        /// </summary>
        private static string BatchParamHint(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "step":
                    return "Did you mean 'steps'? It takes an array of {skill, args} objects.";
                case "skill":
                case "args":
                    return "'skill' and 'args' belong to an element of 'steps', not to the top level: {\"steps\":[{\"skill\":\"...\",\"args\":{...}}]}.";
                case "transactional":
                    return "All-or-nothing execution is a mode, not a flag: use '?mode=transactional' (or body \"mode\":\"transactional\").";
                case "runasync":
                case "async":
                    return "POST /skills/batch is always synchronous — it runs every step in one main-thread job and returns all results. For a long-running background batch use the batch_execute skill's 'runAsync' parameter (POST /skill/batch_execute) and poll job_status / GET /jobs/{id}.";
                case "continueonfailure":
                case "ignoreerrors":
                    return "Did you mean 'continueOnError'?";
                case "diff":
                    return "'diff' is a query parameter, not a body field: POST /skills/batch?diff=1.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 读取一个客户端可能加了引号的布尔量。JSON 的 true/false 原样接受，
        /// 字符串 "true"/"false" 也会解析；其余一律失败，使调用方能上报 TYPE_MISMATCH
        /// 而不是静默取默认值。
        /// </summary>
        private static bool TryReadBatchBool(JToken token, out bool value)
        {
            value = false;
            if (token == null)
                return false;
            if (token.Type == JTokenType.Boolean)
            {
                value = token.ToObject<bool>();
                return true;
            }
            if (token.Type == JTokenType.String)
                return bool.TryParse(token.ToString().Trim(), out value);
            return false;
        }

        private static void WriteBatchTypeMismatch(RequestJob job, string parameter, JToken token, string hint)
        {
            string receivedType = token.Type.ToString().ToLowerInvariant();
            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.TypeMismatch,
                $"'{parameter}' must be a boolean — received {receivedType}. The batch was NOT executed.",
                skill: "skills_batch",
                details: new
                {
                    parameter,
                    expectedType = "boolean",
                    receivedType,
                    received = token is JContainer ? token.ToString(Formatting.None) : token.ToString(),
                    hint,
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// 在查询串已解析出的结果之上，应用请求体层级的 "mode"/"dryRun"。
        ///
        /// <para>"mode" 与 "dryRun" 是两个独立的键，各自独立解析、各自查询串优先。
        /// 过去用同一个"查询串是否已作决定"的标志同时把关两者，结果 URL 里的
        /// <c>?mode=transactional</c> 会一声不响地丢掉请求体中的 <c>{"dryRun":true}</c>，
        /// 把调用方要求预览的那批真执行了——对预览而言这是最糟的失败，且在响应里完全看不出来。</para>
        ///
        /// <para>优先级依次为：同一个键上，URL 胜过载荷；同一位置内，"mode" 胜过 "dryRun"
        /// （查询串一侧就是靠 TryResolveBatchRequestMode 的提前返回实现这一点的）。
        /// 跨两个键时，dryRun 是单调的——任何存活下来的显式 <c>dryRun:true</c> 都使该请求成为预览，
        /// 而 <c>dryRun:false</c> 永远不会取消 <c>mode:"dryRun"</c>，
        /// 因为"别从这个键判定为预览"和"真执行"不是同一句话。
        /// 偏向预览是唯一一个最坏情况仅为浪费一次调用的方向。</para>
        ///
        /// <para>即便在优先级竞争中落败，取值仍然照样校验，所以拼写错误绝不会被吞掉。
        /// 取值或类型非法时返回 false（并写出 400）。</para>
        /// </summary>
        private static bool TryApplyBatchBodyMode(RequestJob job, JObject body, Dictionary<string, string> qs,
            ref bool dryRun, ref bool transactional)
        {
            bool queryOwnsMode = HasQueryValue(qs, "mode");
            bool queryOwnsDryRun = HasQueryValue(qs, "dryRun");
            bool bodyModeApplied = false;

            if (body.TryGetValue("mode", StringComparison.OrdinalIgnoreCase, out var modeToken)
                && modeToken != null && modeToken.Type != JTokenType.Null)
            {
                if (modeToken.Type != JTokenType.String)
                {
                    string receivedType = modeToken.Type.ToString().ToLowerInvariant();
                    job.StatusCode = 400;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.TypeMismatch,
                        $"Body 'mode' must be a string — received {receivedType}. The batch was NOT executed.",
                        skill: "skills_batch",
                        details: new
                        {
                            parameter = "mode",
                            expectedType = "string",
                            receivedType,
                            validValues = new[] { "dryRun", "transactional" },
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    return false;
                }

                string modeValue = modeToken.ToString().Trim();
                bool bodyDryRunMode = modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase);
                bool bodyTransactional = modeValue.Equals("transactional", StringComparison.OrdinalIgnoreCase);
                if (!bodyDryRunMode && !bodyTransactional)
                {
                    bool isPlan = modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase);
                    job.StatusCode = 400;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.InvalidMode,
                        isPlan
                            ? "Batch supports mode 'dryRun' (validates every step without executing) and 'transactional' (all-or-nothing with rollback); 'plan' is not available for /skills/batch."
                            : $"Unknown mode '{modeValue}' — the batch was NOT executed.",
                        skill: "skills_batch",
                        details: new
                        {
                            received = modeValue,
                            validValues = new[] { "dryRun", "transactional" },
                            location = "body",
                            hint = "Set body \"mode\":\"dryRun\" to validate without executing, \"transactional\" for all-or-nothing execution with rollback, or omit it to execute fail-fast.",
                        },
                        retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                    return false;
                }

                // 只有 URL 没写 'mode' 时，这个键才归请求体所有。
                if (!queryOwnsMode)
                {
                    bodyModeApplied = true;
                    transactional = bodyTransactional;
                    dryRun = dryRun || bodyDryRunMode;
                }
            }

            if (body.TryGetValue("dryRun", StringComparison.OrdinalIgnoreCase, out var dryRunToken)
                && dryRunToken != null && dryRunToken.Type != JTokenType.Null)
            {
                if (!TryReadBatchBool(dryRunToken, out bool bodyDryRun))
                {
                    WriteBatchTypeMismatch(job, "dryRun", dryRunToken,
                        "Use JSON true/false (or the strings \"true\"/\"false\") in the body, or '?dryRun=true' / '?mode=dryRun' in the query string.");
                    return false;
                }

                // 请求体层级已有 'mode' 为该位置发过声时跳过；那属于同一位置内的优先级，
                // 不构成忽略 URL 自身 dryRun 键的理由。
                if (!queryOwnsDryRun && !bodyModeApplied)
                    dryRun = dryRun || bodyDryRun;
            }

            return true;
        }

        /// <summary>
        /// 查询串是否为该键携带了可用取值——与 ?mode= / ?dryRun= 解析器所用的
        /// "存在且非空白"判定相同，因此 "?dryRun=" 在两处都算作"调用方没作决定"。
        /// </summary>
        private static bool HasQueryValue(Dictionary<string, string> qs, string key) =>
            qs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// 事务批处理靠编辑器撤销栈承诺"全成或全无"，因此任何会破坏该承诺的东西都被前置拒绝
        /// （400 SEMANTIC_INVALID），而不是等到执行途中才失败：未知/畸形步骤、
        /// 可能触发域重载的技能（重载会清空撤销栈），以及 continueOnError=true
        /// （事务按定义就是快速失败）。整批被拒则返回 true。
        /// </summary>
        private static bool RejectTransactionalPrecheck(RequestJob job, JArray steps, bool continueOnError)
        {
            if (continueOnError)
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.SemanticInvalid,
                    "'continueOnError=true' conflicts with '?mode=transactional': a transaction is all-or-nothing, so execution can never continue past a failed step. Remove one of the two.",
                    skill: "skills_batch",
                    details: new { mode = "transactional", continueOnError = true },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return true;
            }

            var violations = new List<(int step, string skill, string reason)>();
            for (int i = 0; i < steps.Count; i++)
            {
                string name = GetBatchStepSkillName(steps[i]);
                string reason = null;
                if (string.IsNullOrWhiteSpace(name))
                    reason = "step is not an object with a non-empty 'skill' field";
                else if (!SkillRouter.TryGetSkill(name, out var info))
                    reason = "unknown skill";
                else if (info.MayTriggerReload)
                    reason = "the skill declares MayTriggerReload — a domain reload wipes the editor undo stack, so the transactional rollback promise cannot be kept";

                if (reason != null)
                    violations.Add((i, name, reason));
            }

            if (violations.Count == 0)
                return false;

            var first = violations[0];
            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.SemanticInvalid,
                $"Transactional batch rejected before execution: steps[{first.step}] ('{first.skill ?? "?"}') — {first.reason}." +
                (violations.Count > 1 ? $" {violations.Count - 1} more violation(s) listed in details." : string.Empty),
                skill: "skills_batch",
                details: new
                {
                    mode = "transactional",
                    violations = violations.Select(v => new { v.step, v.skill, v.reason }).ToArray(),
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return true;
        }

        // ===== 静态 $param 替换（批处理） =====

        /// <summary>
        /// 在某步 args 内找到的 {"$param":"name"} / {"$param":"name","default":X} 槽位。
        /// 当 $param 的值不是 JSON 字符串时 ParamName 为 null（在解析阶段报为畸形）。
        /// 与 BatchRefNode 不同，这里没有 TopLevelParam：$param 在任何模式下都携带真实值，
        /// 因此不会从 dry-run 校验体中剥除任何东西。
        /// </summary>
        private sealed class BatchParamNode
        {
            public JObject Node;
            public string ParamName;
            public bool HasDefault;
            public JToken DefaultValue;
        }

        /// <summary>
        /// 一个对象节点是参数节点，当且仅当 "$param" 是它唯一的属性（裸槽位），
        /// 或它恰好只有 "$param" + "default" 两个属性（带兜底的槽位）。
        /// 仅仅在众多键中含有 "$param" 的对象属于载荷数据，保持不动——与 IsBatchRefNode 一致。
        /// 当 $param 的值不是 JSON 字符串时 paramName 为 null。
        /// </summary>
        private static bool IsBatchParamNode(JObject obj, out string paramName, out bool hasDefault, out JToken defaultValue)
        {
            paramName = null;
            hasDefault = false;
            defaultValue = null;

            if (obj.Count == 1)
            {
                var prop = (JProperty)obj.First;
                if (!string.Equals(prop.Name, "$param", StringComparison.Ordinal))
                    return false;
                paramName = prop.Value?.Type == JTokenType.String ? prop.Value.ToString() : null;
                return true;
            }

            if (obj.Count == 2)
            {
                JProperty paramProp = null, defaultProp = null;
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "$param", StringComparison.Ordinal)) paramProp = prop;
                    else if (string.Equals(prop.Name, "default", StringComparison.Ordinal)) defaultProp = prop;
                }
                if (paramProp == null || defaultProp == null)
                    return false;
                paramName = paramProp.Value?.Type == JTokenType.String ? paramProp.Value.ToString() : null;
                hasDefault = true;
                defaultValue = defaultProp.Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 收集某步 args 中任意深度上的每一个 $param 槽位。
        /// 若遇到同时带 "$param" 与 "$ref" 的节点（一个节点只能是其中之一，绝不可两者兼有），
        /// 把它记入 paramRefConflict 供调用方以 SEMANTIC_INVALID 拒绝，并在该节点处停止搜索。
        /// </summary>
        private static List<BatchParamNode> FindBatchParamNodes(JToken argsRoot, out JObject paramRefConflict)
        {
            var found = new List<BatchParamNode>();
            paramRefConflict = null;
            CollectBatchParamNodes(argsRoot, found, ref paramRefConflict);
            return found;
        }

        private static void CollectBatchParamNodes(JToken token, List<BatchParamNode> found, ref JObject paramRefConflict)
        {
            if (paramRefConflict != null)
                return;

            if (token is JObject obj)
            {
                bool hasParam = false, hasRef = false;
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "$param", StringComparison.Ordinal)) hasParam = true;
                    else if (string.Equals(prop.Name, "$ref", StringComparison.Ordinal)) hasRef = true;
                }
                if (hasParam && hasRef)
                {
                    paramRefConflict = obj;
                    return;
                }
                if (hasParam && IsBatchParamNode(obj, out var paramName, out var hasDefault, out var defaultValue))
                {
                    found.Add(new BatchParamNode
                    {
                        Node = obj,
                        ParamName = paramName,
                        HasDefault = hasDefault,
                        DefaultValue = defaultValue,
                    });
                    return;
                }
                foreach (var prop in obj.Properties())
                    CollectBatchParamNodes(prop.Value, found, ref paramRefConflict);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    CollectBatchParamNodes(item, found, ref paramRefConflict);
            }
        }

        /// <summary>
        /// 解析单个槽位的值：批处理的 "params" 对象持有该名字时以它为准（区分大小写），
        /// 否则取节点的内联 "default"，再否则该步以 SEMANTIC_INVALID 失败（"未提供且无默认值"）。
        /// $param 名不是字符串则判为畸形。
        /// </summary>
        private static bool TryResolveBatchParam(BatchParamNode node, JObject batchParams, out JToken value, out string reason)
        {
            value = null;
            reason = null;

            if (node.ParamName == null)
            {
                reason = "the $param value must be a string naming a batch parameter";
                return false;
            }
            if (batchParams != null && batchParams.TryGetValue(node.ParamName, StringComparison.Ordinal, out var provided))
            {
                value = provided;
                return true;
            }
            if (node.HasDefault)
            {
                value = node.DefaultValue ?? JValue.CreateNull();
                return true;
            }
            reason = "not provided and no default";
            return false;
        }

        // ===== 跨步骤 $ref 引用（批处理） =====

        /// <summary>
        /// 在某步 args 内找到的 {"$ref":"$N.path"} 节点。当 $ref 的值不是 JSON 字符串时
        /// RefString 为 null（在解析阶段报为畸形）。
        /// TopLevelParam 是子树中包含该节点的那个 args 属性；节点本身就是 args 根时为 null。
        /// </summary>
        private sealed class BatchRefNode
        {
            public JObject Node;
            public string RefString;
            public string TopLevelParam;
        }

        /// <summary>
        /// 一个对象节点是引用，当且仅当 "$ref" 是它唯一的属性；
        /// 仅仅在众多键中含有 "$ref" 的对象属于载荷数据，保持不动。
        /// </summary>
        private static bool IsBatchRefNode(JObject obj, out string refString)
        {
            refString = null;
            if (obj.Count != 1)
                return false;
            var prop = (JProperty)obj.First;
            if (!string.Equals(prop.Name, "$ref", StringComparison.Ordinal))
                return false;
            refString = prop.Value?.Type == JTokenType.String ? prop.Value.ToString() : null;
            return true;
        }

        private static List<BatchRefNode> FindBatchRefNodes(JToken argsRoot)
        {
            var found = new List<BatchRefNode>();
            CollectBatchRefNodes(argsRoot, argsRoot, found);
            return found;
        }

        private static void CollectBatchRefNodes(JToken token, JToken root, List<BatchRefNode> found)
        {
            if (token is JObject obj)
            {
                if (IsBatchRefNode(obj, out var refString))
                {
                    found.Add(new BatchRefNode
                    {
                        Node = obj,
                        RefString = refString,
                        TopLevelParam = GetTopLevelParamName(obj, root),
                    });
                    return;
                }
                foreach (var prop in obj.Properties())
                    CollectBatchRefNodes(prop.Value, root, found);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    CollectBatchRefNodes(item, root, found);
            }
        }

        private static string GetTopLevelParamName(JToken node, JToken root)
        {
            JToken cur = node;
            while (cur != null && !ReferenceEquals(cur, root) && !ReferenceEquals(cur.Parent, root))
                cur = cur.Parent;
            return cur is JProperty prop ? prop.Name : null;
        }

        /// <summary>
        /// 解析 "$N"、"$N.path" 或 "$N[…]"——N 是 0 基的步骤下标，
        /// 其余部分是指向该步已解包结果的 Newtonsoft SelectToken 路径。
        /// </summary>
        private static bool TryParseBatchRef(string refString, out int stepIndex, out string selectPath, out string parseError)
        {
            stepIndex = -1;
            selectPath = null;
            parseError = null;

            if (string.IsNullOrEmpty(refString) || refString[0] != '$')
            {
                parseError = "the $ref value must be a string like \"$0\", \"$0.instanceId\" or \"$1.items[0].path\"";
                return false;
            }

            int i = 1;
            while (i < refString.Length && char.IsDigit(refString[i]))
                i++;
            if (i == 1 || !int.TryParse(refString.Substring(1, i - 1), out stepIndex))
            {
                stepIndex = -1;
                parseError = "no step index after '$' (expected \"$N\" with N = 0-based index of an earlier step)";
                return false;
            }

            if (i == refString.Length)
                return true; // "$N"——整个已解包结果

            char next = refString[i];
            if (next == '.')
            {
                selectPath = refString.Substring(i + 1);
                if (selectPath.Length > 0)
                    return true;
                parseError = "empty path after '.'";
                return false;
            }
            if (next == '[')
            {
                selectPath = refString.Substring(i);
                return true;
            }

            parseError = $"unexpected character '{next}' after the step index";
            return false;
        }

        /// <summary>
        /// 依据已执行步骤的已解包结果解析单个引用。
        /// 以下情况失败（并给出结构化原因）：引用格式错误、下标超出本批范围、
        /// 前向引用（N >= 当前步）、被引步骤未成功完成，或 SelectToken 路径无匹配。
        /// </summary>
        private static bool TryResolveBatchRef(string refString, JToken[] stepResults, int currentIndex, int stepCount,
            out JToken resolved, out string reason, out int referencedStep)
        {
            resolved = null;
            reason = null;

            if (!TryParseBatchRef(refString, out referencedStep, out var selectPath, out var parseError))
            {
                reason = parseError;
                return false;
            }

            if (referencedStep >= stepCount)
            {
                reason = $"step index {referencedStep} is out of range (batch has {stepCount} steps)";
                return false;
            }
            if (referencedStep >= currentIndex)
            {
                reason = $"forward reference — steps[{referencedStep}] does not run before steps[{currentIndex}]; $refs may only point to earlier steps";
                return false;
            }
            if (stepResults[referencedStep] == null)
            {
                reason = $"steps[{referencedStep}] did not complete successfully, so its result is not available";
                return false;
            }

            if (selectPath == null)
            {
                resolved = stepResults[referencedStep];
                return true;
            }

            try
            {
                resolved = stepResults[referencedStep].SelectToken(selectPath, errorWhenNoMatch: false);
            }
            catch (Exception ex)
            {
                reason = $"invalid SelectToken path '{selectPath}': {ex.Message}";
                return false;
            }
            if (resolved == null)
            {
                reason = $"path '{selectPath}' matched nothing in the result of steps[{referencedStep}]";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 对单个引用做 dry-run 结构性校验（此时没有真实值可解析）：
        /// 下标在范围内且指向更早的步骤、被引技能已知、路径首段出现在被引技能声明的 Outputs 中。
        /// 所有发现都只是警告——Outputs 元数据可能不完整，而 dry-run 批处理从不中止。
        /// </summary>
        private static void ValidateBatchRefStructural(string refString, int currentIndex, JArray steps, List<string> warnings)
        {
            string label = $"$ref '{refString ?? "(non-string)"}'";
            if (!TryParseBatchRef(refString, out var refStep, out var selectPath, out var parseError))
            {
                warnings.Add($"{label}: malformed ({parseError}) — this step will fail at execution.");
                return;
            }
            if (refStep >= steps.Count)
            {
                warnings.Add($"{label}: step index {refStep} is out of range (batch has {steps.Count} steps) — this step will fail at execution.");
                return;
            }
            if (refStep >= currentIndex)
            {
                warnings.Add($"{label}: forward reference (steps[{refStep}] does not run before steps[{currentIndex}]) — this step will fail at execution.");
                return;
            }

            string refSkillName = GetBatchStepSkillName(steps[refStep]);
            if (string.IsNullOrWhiteSpace(refSkillName) || !SkillRouter.TryGetSkill(refSkillName, out var refSkill))
            {
                warnings.Add($"{label}: referenced steps[{refStep}] has no known skill ('{refSkillName}') — this step will fail at execution.");
                return;
            }

            if (selectPath == null)
                return;
            var outputs = SkillRouter.GetEffectiveOutputs(refSkill);
            if (outputs == null || outputs.Length == 0)
                return; // 没有声明 Outputs 可供比对
            string firstSegment = FirstSelectTokenSegment(selectPath);
            if (firstSegment == null)
                return; // "[0]…" 是对结果根做下标访问，没有名字可校验

            foreach (var output in outputs)
            {
                if (string.Equals(output, firstSegment, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            warnings.Add($"{label}: field '{firstSegment}' is not among the declared outputs of '{refSkillName}' [{string.Join(", ", outputs)}] — declared Outputs may be incomplete, so this is only a warning; verify at execution.");
        }

        private static string FirstSelectTokenSegment(string selectPath)
        {
            if (string.IsNullOrEmpty(selectPath) || selectPath[0] == '[')
                return null;
            int cut = selectPath.IndexOfAny(new[] { '.', '[' });
            return cut < 0 ? selectPath : selectPath.Substring(0, cut);
        }

        /// <summary>
        /// 在某步的 $ref 参数被从校验体剥除之后，对其 DryRun 载荷做后处理：
        /// 丢掉因剥除而产生的 MISSING_PARAM 条目、把该步的 semanticErrors 降级为警告
        /// （语义检查是在没有引用值的情况下跑的，无论判过还是判不过都只是猜测）、
        /// 按剩下的内容重算 'valid'，并附上 refsValidated 让调用方看清哪些参数只受了结构性检查。
        /// </summary>
        private static void AdjustDryRunPayloadForRefs(JObject stepPayload, List<BatchRefNode> refNodes,
            HashSet<string> strippedParams, bool wholeArgsFromRef, List<string> refWarnings)
        {
            var refsValidated = new JArray();
            foreach (var refNode in refNodes)
            {
                refsValidated.Add(new JObject
                {
                    ["param"] = refNode.TopLevelParam ?? "(args)",
                    ["ref"] = refNode.RefString,
                    ["structural"] = true,
                });
            }
            stepPayload["refsValidated"] = refsValidated;

            if (!(stepPayload["validation"] is JObject validation))
                return; // 错误载荷（未知技能等）——已附上 refsValidated，无需再校正

            var addedWarnings = new List<string>(refWarnings);

            if (validation["missingParams"] is JArray missing && missing.Count > 0)
            {
                for (int m = missing.Count - 1; m >= 0; m--)
                {
                    string param = missing[m]?.ToString();
                    if (wholeArgsFromRef || (param != null && strippedParams.Contains(param)))
                        missing.RemoveAt(m);
                }
                if (missing.Count == 0)
                    validation["missingParams"] = null;
            }

            if (validation["semanticErrors"] is JArray semantic && semantic.Count > 0)
            {
                foreach (var item in semantic)
                    addedWarnings.Add($"semantic check not confirmable while '$ref' params are unresolved (structural-only): {item.ToString(Formatting.None)}");
                validation["semanticErrors"] = null;
            }

            if (addedWarnings.Count > 0)
            {
                if (!(validation["warnings"] is JArray warningsArr))
                {
                    warningsArr = new JArray();
                    validation["warnings"] = warningsArr;
                }
                foreach (var warning in addedWarnings)
                    warningsArr.Add(warning);
            }

            stepPayload["valid"] =
                IsNullOrEmptyJArray(validation["missingParams"]) &&
                IsNullOrEmptyJArray(validation["unknownParams"]) &&
                IsNullOrEmptyJArray(validation["typeErrors"]) &&
                IsNullOrEmptyJArray(validation["semanticErrors"]);
        }

        private static bool IsNullOrEmptyJArray(JToken token) => !(token is JArray arr) || arr.Count == 0;

        /// <summary>
        /// 把 GET /jobs 与 GET /jobs/{id}[/logs] 直接路由到 BatchPersistence，不经过 skill router。
        /// 专为高频进度轮询设计：调用方每 200-500 毫秒 ping 一次 GET /jobs/{id} 即可拿到最新快照。
        /// </summary>
        private static void HandleJobsRequest(RequestJob job)
        {
            string path = job.Path ?? string.Empty;
            var qs = SkillRouter.ParseQueryString(job.QueryString);

            // GET /jobs  → 列表
            if (string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/jobs/", StringComparison.OrdinalIgnoreCase))
            {
                int limit = 50;
                if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                    limit = Mathf.Clamp(lp, 1, 100);

                var jobs = BatchPersistence.ListJobs(limit);
                var projected = new System.Collections.Generic.List<object>(jobs.Length);
                foreach (var r in jobs)
                {
                    projected.Add(new
                    {
                        jobId = r.jobId,
                        kind = r.kind,
                        status = r.status,
                        progress = r.progress,
                        currentStage = r.currentStage,
                        startedAt = r.startedAt,
                        updatedAt = r.updatedAt,
                        resultSummary = r.resultSummary,
                        error = r.error,
                    });
                }

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    count = projected.Count,
                    jobs = projected,
                }, _jsonSettings);
                return;
            }

            // GET /jobs/{id}[/logs]
            const string prefix = "/jobs/";
            string remainder = path.Substring(prefix.Length).TrimEnd('/');
            string jobId;
            string subResource = null;
            int slashIdx = remainder.IndexOf('/');
            if (slashIdx >= 0)
            {
                jobId = remainder.Substring(0, slashIdx);
                subResource = remainder.Substring(slashIdx + 1);
            }
            else
            {
                jobId = remainder;
            }

            if (string.IsNullOrEmpty(jobId))
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing job id in path",
                    details: new { example = "/jobs/{id}" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            var record = BatchPersistence.GetJob(jobId);
            if (record == null)
            {
                job.StatusCode = 404;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.NotFound,
                    $"Job not found: {jobId}",
                    details: new { jobId },
                    retryStrategy: SkillErrorResponse.Abort);
                return;
            }

            if (string.Equals(subResource, "progress", StringComparison.OrdinalIgnoreCase))
            {
                int offset = 0;
                if (qs.TryGetValue("offset", out var off) && int.TryParse(off, out var offp))
                    offset = Math.Max(0, offp);

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(
                    AsyncJobService.BuildProgressSnapshot(record, offset),
                    _jsonSettings);
                return;
            }

            if (string.Equals(subResource, "logs", StringComparison.OrdinalIgnoreCase))
            {
                int limit = 100;
                if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                    limit = Mathf.Clamp(lp, 1, 500);

                var logs = record.logs ?? new System.Collections.Generic.List<BatchJobLogEntry>();
                int skip = Math.Max(0, logs.Count - limit);
                var sliced = logs.Skip(skip)
                    .Select(e => new
                    {
                        timestamp = e.timestamp,
                        level = e.level,
                        stage = e.stage,
                        message = e.message,
                        code = e.code,
                    })
                    .ToArray();

                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    jobId = record.jobId,
                    count = sliced.Length,
                    totalCount = logs.Count,
                    logs = sliced,
                }, _jsonSettings);
                return;
            }

            // GET /jobs/{id}（默认——完整状态快照）
            int recentCount = 10;
            if (qs.TryGetValue("recentCount", out var rc) && int.TryParse(rc, out var rcp))
                recentCount = Mathf.Clamp(rcp, 1, 200);
            var recentEvents = record.progressEvents == null
                ? Array.Empty<object>()
                : record.progressEvents
                    .Skip(Math.Max(0, record.progressEvents.Count - recentCount))
                    .Select(e => new
                    {
                        timestamp = e.timestamp,
                        progress = e.progress,
                        stage = e.stage,
                        description = e.description,
                    }).ToArray();

            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                jobId = record.jobId,
                kind = record.kind,
                status = record.status,
                progress = record.progress,
                currentStage = record.currentStage,
                progressStage = record.progressStage,
                startedAt = record.startedAt,
                updatedAt = record.updatedAt,
                processedItems = record.processedItems,
                totalItems = record.totalItems,
                resultSummary = record.resultSummary,
                error = record.error,
                warnings = record.warnings,
                reportId = record.reportId,
                relatedWorkflowId = record.relatedWorkflowId,
                canCancel = record.canCancel,
                recentProgress = recentEvents,
                terminal = IsTerminalStatus(record.status),
            }, _jsonSettings);
        }

        // ===== 权限系统 =====

        private static void HandlePermissionRequest(RequestJob job)
        {
            string path = job.Path ?? string.Empty;

            if (string.Equals(path, "/permission/status", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionStatus(job);
                return;
            }

            if (string.Equals(path, "/permission/audit", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionAudit(job);
                return;
            }

            if (string.Equals(path, "/permission/allowlist", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionAllowlistList(job);
                return;
            }

            if (job.HttpMethod == "POST")
            {
                if (string.Equals(path, "/permission/grant", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionGrant(job);
                    return;
                }
                if (string.Equals(path, "/permission/approve", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionApprove(job);
                    return;
                }
                if (string.Equals(path, "/permission/deny", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionDeny(job);
                    return;
                }
                if (string.Equals(path, "/permission/allowlist/add", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionAllowlistAdd(job);
                    return;
                }
                if (string.Equals(path, "/permission/allowlist/remove", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionAllowlistRemove(job);
                    return;
                }
                if (string.Equals(path, "/permission/revoke", StringComparison.OrdinalIgnoreCase))
                {
                    // 已弃用别名：转发到 allowlist/remove 逻辑，响应中带 deprecated=true。
                    HandlePermissionRevoke(job);
                    return;
                }
            }

            job.StatusCode = 404;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.NotFound,
                "Permission endpoint not found",
                details: new
                {
                    endpoints = new[]
                    {
                        "GET /permission/status",
                        "POST /permission/grant",
                        "POST /permission/approve",
                        "POST /permission/deny",
                        "GET /permission/allowlist",
                        "POST /permission/allowlist/add",
                        "POST /permission/allowlist/remove",
                        "POST /permission/revoke",
                        "GET /permission/audit"
                    }
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        private static void HandlePermissionStatus(RequestJob job)
        {
            var qs = SkillRouter.ParseQueryString(job.QueryString);
            string focusToken = qs.TryGetValue("token", out var tokenVal) ? tokenVal : null;

            var pending = SkillsModeManager.PendingGrantRequests;
            var allowlist = SkillsModeManager.AllowlistSkills;

            object focusEntry = null;
            if (!string.IsNullOrEmpty(focusToken))
            {
                var match = pending.FirstOrDefault(p => string.Equals(p.Token, focusToken, StringComparison.Ordinal));
                if (match != null)
                {
                    focusEntry = new
                    {
                        token = match.Token,
                        skill = match.SkillName,
                        argsSummary = match.ArgsSummary,
                        channel = match.Channel,
                        approvedByPanel = match.ApprovedByPanel,
                        expiresAtUtc = match.ExpiresAtUtc.ToString("o"),
                        ttlSeconds = Math.Max(0, (int)(match.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
                    };
                }
            }

            job.StatusCode = 200;
            // 字段重命名：`granted` → `allowlist`。`granted` 字段作为兼容别名保留一个版本，
            // 下个 minor 版本会移除——客户端应迁移到 `allowlist` 字段。
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                mode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                panelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                allowlist = allowlist,
                granted = allowlist, // 已弃用别名——下个小版本移除
                pending = pending.Select(p => new
                {
                    token = p.Token,
                    skill = p.SkillName,
                    argsSummary = p.ArgsSummary,
                    channel = p.Channel,
                    approvedByPanel = p.ApprovedByPanel,
                    expiresAtUtc = p.ExpiresAtUtc.ToString("o"),
                    ttlSeconds = Math.Max(0, (int)(p.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
                }).ToArray(),
                focus = focusEntry,
                counts = new
                {
                    allowlist = allowlist.Count,
                    granted = allowlist.Count, // 已弃用别名
                    pending = pending.Count,
                },
                deprecated = new
                {
                    granted = "Use 'allowlist' instead. The 'granted' field will be removed in a future minor version.",
                },
            }, _jsonSettings);
        }

        private static void HandlePermissionGrant(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var sToken) ? sToken?.ToString() : null;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var tToken) ? tToken?.ToString() : null;

            if (string.IsNullOrWhiteSpace(skill) || string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Both 'skill' and 'token' are required.",
                    details: new { required = new[] { "skill", "token" }, optional = new[] { "args" } },
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            // args 字段可选——方案 B 优先用 entry 缓存的原 argsJson。
            // body 携带 args 时按现有规则参与哈希校验；未携带时直接读 entry 缓存（TryPeekArgsJson）。
            bool argsProvided = body.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var argsToken)
                                && argsToken != null && argsToken.Type != JTokenType.Null;
            string argsJson;
            if (argsProvided)
            {
                argsJson = ExtractArgsJson(body);
            }
            else
            {
                // 直接从 entry 取缓存的原 argsJson —— 既对零参 skill 工作，也对带参 skill 工作，
                // 让 AI 调 grant 时只需提供 token，符合"一步执行"语义。
                // entry 不存在/过期时回退 "{}"，让下方 TryGrantAndReturnArgs 返回 Invalid 给出明确错误。
                argsJson = SkillsModeManager.TryPeekArgsJson(token) ?? "{}";
            }

            // 注意：HandlePermissionGrant 由 ProcessJobQueue 在主线程 (EditorApplication.update) 调用，
            // 所以 TryGrantAndReturnArgs 设置的 ThreadStatic one-shot 令牌、以及后续的 SkillRouter.Execute
            // 都在同一个主线程内执行——线程安全前提成立，无需额外 dispatch。
            var (outcome, cachedSkill, cachedArgs) = SkillsModeManager.TryGrantAndReturnArgs(skill, token, argsJson);
            switch (outcome)
            {
                case GrantOutcome.Granted:
                {
                    // 方案 B 一步执行：one-shot 令牌已由 TryGrantAndReturnArgs 设置在当前线程，
                    // SkillRouter.Execute → CheckAccess 会立刻消费该令牌、单次放行。
                    //
                    // 但消费点不是必经之路：Execute 的四道参数校验（UnknownParam / MissingParam /
                    // TypeMismatch / SemanticInvalid）都在权限门之前早退，任何一道早退——以及这里
                    // catch 到的异常——都会让令牌留在主线程上，被后续同名 skill 的请求带着别的参数
                    // 命中。finally 无条件清除是唯一能覆盖全部路径的位置。
                    string execJson;
                    try
                    {
                        execJson = SkillRouter.Execute(cachedSkill, cachedArgs);
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"grant_executed failed for '{cachedSkill}': {ex.Message}");
                        execJson = SkillErrorResponse.Build(
                            SkillErrorCode.Internal,
                            ex.Message,
                            skill: cachedSkill,
                            details: new { type = ex.GetType().Name },
                            retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
                    }
                    finally
                    {
                        SkillsModeManager.ClearOneShotBypass();
                    }

                    SkillsAuditLog.Append("grant_executed", new { skill = cachedSkill, token });

                    // 尝试把 execJson 内联为 JSON 对象，方便上层直接读字段；失败兜底为字符串。
                    object resultPayload;
                    try { resultPayload = JObject.Parse(execJson); }
                    catch
                    {
                        try { resultPayload = JToken.Parse(execJson); }
                        catch { resultPayload = execJson; }
                    }

                    job.StatusCode = 200;
                    job.ResponseJson = JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        skill = cachedSkill,
                        executed = true,
                        result = resultPayload,
                    }, _jsonSettings);
                    return;
                }
                case GrantOutcome.PendingApproval:
                    job.StatusCode = 200;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.GrantPendingApproval,
                        "Token is valid but waiting for panel approval.",
                        skill: skill,
                        details: new
                        {
                            hint = "Tell the user to click Approve on the Unity panel; then POST /permission/grant again to trigger one-step execution.",
                        },
                        retryStrategy: SkillErrorResponse.RetryAskUserAndGrant,
                        extra: new Dictionary<string, object> { ["ok"] = false, ["reason"] = "GRANT_PENDING_APPROVAL" });
                    return;
                default:
                    WritePermissionError(job, 400, SkillErrorCode.InvalidToken,
                        "Grant token is invalid, expired, or does not match (skill, args).",
                        skill: skill,
                        details: new { suggestion = "Re-trigger the skill to obtain a fresh MODE_RESTRICTED token bound to your current args." },
                        retry: SkillErrorResponse.RetryAskUserAndGrant);
                    return;
            }
        }

        private static void HandlePermissionApprove(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var t) ? t?.ToString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam, "'token' is required.", retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            bool ok = SkillsModeManager.Approve(token);
            job.StatusCode = ok ? 200 : 404;
            job.ResponseJson = JsonConvert.SerializeObject(new { ok, token }, _jsonSettings);
        }

        private static void HandlePermissionDeny(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var t) ? t?.ToString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam, "'token' is required.", retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            bool ok = SkillsModeManager.Deny(token);
            job.StatusCode = ok ? 200 : 404;
            job.ResponseJson = JsonConvert.SerializeObject(new { ok, token }, _jsonSettings);
        }

        private static void HandlePermissionRevoke(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            bool all = body.TryGetValue("all", StringComparison.OrdinalIgnoreCase, out var allToken)
                && allToken.Type == JTokenType.Boolean && allToken.ToObject<bool>();

            // 已弃用别名：转发到 AllowlistRemove / ClearAllowlist。响应带 `deprecated: true`，
            // 便于客户端迁移到 /permission/allowlist/remove。
            if (all)
            {
                int before = SkillsModeManager.AllowlistSkills.Count;
                SkillsModeManager.ClearAllowlist();
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    revoked = before,
                    allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                    deprecated = true,
                    deprecationHint = "Use POST /permission/allowlist/remove with {all:true} instead.",
                }, _jsonSettings);
                return;
            }

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Provide either 'skill' or 'all:true'.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool removed = SkillsModeManager.RemoveFromAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                revoked = removed ? 1 : 0,
                skill,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                deprecated = true,
                deprecationHint = "Use POST /permission/allowlist/remove with {skill:'<name>'} instead.",
            }, _jsonSettings);
        }

        // ===== 白名单端点 =====

        private static void HandlePermissionAllowlistList(RequestJob job)
        {
            var allowlist = SkillsModeManager.AllowlistSkills;
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                allowlist = allowlist,
                count = allowlist.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAllowlistAdd(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "'skill' is required.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            if (!SkillRouter.HasSkill(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.SkillNotFound,
                    $"Unknown skill: {skill}",
                    details: new { skill, hint = "Use GET /skills to list registered skill names." },
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool added = SkillsModeManager.AddToAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                skill,
                added,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAllowlistRemove(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            bool all = body.TryGetValue("all", StringComparison.OrdinalIgnoreCase, out var allToken)
                && allToken.Type == JTokenType.Boolean && allToken.ToObject<bool>();

            if (all)
            {
                int before = SkillsModeManager.AllowlistSkills.Count;
                SkillsModeManager.ClearAllowlist();
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    removed = before > 0,
                    removedCount = before,
                    allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                }, _jsonSettings);
                return;
            }

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Provide either 'skill' or 'all:true'.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool removed = SkillsModeManager.RemoveFromAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                skill,
                removed,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAudit(RequestJob job)
        {
            var qs = SkillRouter.ParseQueryString(job.QueryString);
            int limit = 100;
            if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                limit = Mathf.Clamp(lp, 1, 1000);

            var entries = SkillsAuditLog.ReadRecent(limit);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                count = entries.Count,
                limit,
                entries,
                path = SkillsAuditLog.GetLogPath(),
            }, _jsonSettings);
        }

        private static bool TryParseBody(RequestJob job, out JObject body)
        {
            body = null;
            try
            {
                body = string.IsNullOrWhiteSpace(job.Body) ? new JObject() : JObject.Parse(job.Body);
                return true;
            }
            catch (Exception ex)
            {
                WritePermissionError(job, 400, SkillErrorCode.InvalidJson,
                    $"Invalid JSON body: {ex.Message}",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }
        }

        private static string ExtractArgsJson(JObject body)
        {
            if (body == null) return string.Empty;
            if (!body.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var argsToken))
                return string.Empty;
            if (argsToken == null || argsToken.Type == JTokenType.Null) return string.Empty;
            if (argsToken.Type == JTokenType.String) return argsToken.ToString();
            // 去掉 _confirm 后重新序列化，使哈希与 SkillRouter 侧的归一化一致。
            if (argsToken is JObject obj)
            {
                var clone = (JObject)obj.DeepClone();
                clone.Remove("_confirm");
                return clone.ToString(Formatting.None);
            }
            return argsToken.ToString(Formatting.None);
        }

        private static void WritePermissionError(
            RequestJob job, int statusCode, SkillErrorCode code, string message,
            string skill = null, object details = null, string retry = null)
        {
            job.StatusCode = statusCode;
            job.ResponseJson = SkillErrorResponse.Build(code, message, skill: skill, details: details, retryStrategy: retry);
        }

        private static bool IsTerminalStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
        }

        private static void RunSelfTest()
        {
            if (!_isRunning) return;
            int port = _port;
            int pjqTicks = _pjqTicksSinceStart;
            SkillsLogger.Log($"[Self-Test] Starting (ProcessJobQueue ticks={pjqTicks}, listener={_listener?.IsListening})");

            ThreadPool.QueueUserWorkItem(_ =>
            {
                // 1. 用裸 TCP 做带重试的可达性测试（完全绕开 .NET HTTP 客户端栈）
                var hosts = new[] { "localhost", "127.0.0.1" };
                foreach (var host in hosts)
                {
                    if (!_isRunning) return;

                    string url = $"http://{host}:{port}/health";
                    bool success = false;
                    string lastError = null;
                    var connectAddresses = GetSelfTestAddresses(host);

                    for (int attempt = 1; attempt <= 3 && !success && _isRunning; attempt++)
                    {
                        if (attempt > 1) Thread.Sleep(attempt * 1500); // 退避 3 秒、4.5 秒

                        foreach (var address in connectAddresses)
                        {
                            if (!_isRunning)
                                return;

                            try
                            {
                                if (!TryReadSelfTestResponse(address, host, port, out string response, out string error))
                                {
                                    lastError = error;
                                    continue;
                                }

                                if (response.Contains("200") && response.Contains("\"status\""))
                                {
                                    SkillsLogger.LogSuccess($"[Self-Test] {url} -> OK");
                                    success = true;
                                    break;
                                }
                                else if (response.Length > 0)
                                {
                                    var firstLine = response.Split('\n')[0].Trim();
                                    // 打警告之前，先在其他回环地址上重试 localhost。
                                    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
                                        firstLine.IndexOf("400", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        lastError = $"{firstLine} via {address}";
                                        continue;
                                    }

                                    SkillsLogger.LogWarning($"[Self-Test] {url} -> {firstLine}");
                                    success = true;
                                    break;
                                }
                                else
                                {
                                    lastError = $"Empty response via {address}";
                                }
                            }
                            catch (Exception ex)
                            {
                                lastError = $"{ex.InnerException?.Message ?? ex.Message} via {address}";
                            }
                        }
                    }

                    if (!success)
                    {
                        SkillsLogger.LogWarning($"[Self-Test] {url} -> FAILED after 3 attempts: {lastError}");
                        SkillsLogger.LogWarning($"[Self-Test] Main thread may be busy (PJQ ticks={_pjqTicksSinceStart}). External clients can connect once editor is responsive.");
                    }
                }

                // 2. 端口扫描：报告 8090-8100 中被占用的端口
                var occupied = new List<string>();
                for (int p = 8090; p <= 8100; p++)
                {
                    if (p == port) continue;
                    try
                    {
                        using (var tcp = new System.Net.Sockets.TcpClient())
                        {
                            var ar = tcp.BeginConnect("127.0.0.1", p, null, null);
                            if (ar.AsyncWaitHandle.WaitOne(500))
                            {
                                tcp.EndConnect(ar);
                                occupied.Add(p.ToString());
                            }
                        }
                    }
                    catch { /* Connection refused = port is free */ }
                }
                if (occupied.Count > 0)
                    SkillsLogger.LogWarning($"[Self-Test] Occupied ports (8090-8100): {string.Join(", ", occupied)}");
            });
        }

        private static List<IPAddress> GetSelfTestAddresses(string host)
        {
            var addresses = new List<IPAddress>();

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var address in Dns.GetHostAddresses(host))
                    {
                        if (IPAddress.IsLoopback(address) && !addresses.Contains(address))
                            addresses.Add(address);
                    }
                }
                catch
                {
                    // 退回到下面这些已知的回环地址。
                }

                addresses.Sort((left, right) =>
                {
                    int leftRank = left.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1;
                    int rightRank = right.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1;
                    return leftRank.CompareTo(rightRank);
                });

                if (!addresses.Contains(IPAddress.Loopback))
                    addresses.Insert(0, IPAddress.Loopback);
                if (!addresses.Contains(IPAddress.IPv6Loopback))
                    addresses.Add(IPAddress.IPv6Loopback);

                return addresses;
            }

            if (IPAddress.TryParse(host, out var parsedAddress))
            {
                addresses.Add(parsedAddress);
                return addresses;
            }

            foreach (var address in Dns.GetHostAddresses(host))
            {
                if (!addresses.Contains(address))
                    addresses.Add(address);
            }

            return addresses;
        }

        private static bool TryReadSelfTestResponse(IPAddress address, string hostHeader, int port, out string response, out string error)
        {
            response = null;
            error = null;

            using (var tcp = new System.Net.Sockets.TcpClient(address.AddressFamily))
            {
                var ar = tcp.BeginConnect(address, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(3000))
                {
                    tcp.Close();
                    error = "TCP connect timed out";
                    return false;
                }

                tcp.EndConnect(ar);
                tcp.ReceiveTimeout = 5000;
                tcp.SendTimeout = 2000;

                var stream = tcp.GetStream();
                var httpReq =
                    $"GET /health HTTP/1.1\r\n" +
                    $"Host: {hostHeader}:{port}\r\n" +
                    "User-Agent: UnitySkills-SelfTest\r\n" +
                    "Accept: application/json\r\n" +
                    "Connection: close\r\n\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(httpReq);
                stream.Write(reqBytes, 0, reqBytes.Length);

                var sb = new StringBuilder();
                var buf = new byte[4096];
                int read;
                while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                    sb.Append(Encoding.UTF8.GetString(buf, 0, read));

                response = sb.ToString();
                return true;
            }
        }
    }
}

// Producer:Betsy
