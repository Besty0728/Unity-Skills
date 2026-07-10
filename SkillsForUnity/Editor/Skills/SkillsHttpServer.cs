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
    /// Production-grade HTTP server for UnitySkills REST API.
    ///
    /// Architecture: Strict Producer-Consumer Pattern
    /// - HTTP Thread (Producer): ONLY receives requests and enqueues them. NO Unity API calls.
    /// - Main Thread (Consumer): Processes ALL logic including routing, rate limiting, and skill execution.
    ///
    /// Resilience Features:
    /// - Auto-restart after Domain Reload (script compilation)
    /// - Persistent state via EditorPrefs
    /// - Graceful shutdown and recovery
    ///
    /// This ensures 100% thread safety with Unity's single-threaded architecture.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsHttpServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static Thread _keepAliveThread;
        private static volatile bool _isRunning;
        private static int _port = 8090;
        private static readonly string _prefixBase = "http://localhost:";
        private static string _prefix = $"{_prefixBase}{_port}/";
        
        // Job queue - HTTP thread enqueues, Main thread dequeues and processes
        private static readonly Queue<RequestJob> _jobQueue = new Queue<RequestJob>();
        private static readonly object _queueLock = new object();
        private static bool _updateHooked = false;
        private static int _pendingRequests = 0;
        
        private const int MaxRequestsPerSecond = 100;
        private const int MaxQueuedRequests = 200;
        private const int MaxPendingRequests = 300;
        private static readonly ConcurrentBag<RequestJob> _requestJobPool = new ConcurrentBag<RequestJob>();
        private static int _poolSize;

        // Admission limiting on the listener thread to avoid queue and thread blowups.
        private static int _admittedThisSecond = 0;
        private static long _lastAdmissionResetTicks = 0;
        
        // Keep-alive polling interval (ms) for checking pending jobs.
        private const int KeepAlivePollingMs = 50;

        // Configurable interval for unconditional main-thread wakeup.
        private const string PrefKeyKeepAliveInterval = "UnitySkills_KeepAliveIntervalSeconds";

        // Thread-safe cached value for KeepAliveIntervalSeconds (EditorPrefs is main-thread only)
        private static long _cachedKeepAliveIntervalTicks = 10L * TimeSpan.TicksPerSecond;

        /// <summary>
        /// How often (seconds) the keep-alive thread forces a main-thread wakeup,
        /// even when there are no pending jobs. Keeps watchdog and heartbeat alive
        /// while Unity is unfocused. Default 10s, minimum 1s.
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
        // Request processing timeout - cached for thread safety (EditorPrefs is main-thread only)
        private static int _cachedTimeoutMs = 15 * 60 * 1000;
        private static int RequestTimeoutMs => _cachedTimeoutMs;
        internal static void RefreshTimeoutCache() => _cachedTimeoutMs = RequestTimeoutMinutes * 60 * 1000;
        // Maximum allowed POST body size
        private const int MaxBodySizeBytes = 10 * 1024 * 1024; // 10MB
        // Heartbeat interval for registry (seconds)
        private const double HeartbeatInterval = 30.0;
        private static double _lastHeartbeatTime = 0;

        // Watchdog: periodically verify listener thread is alive and restart if not
        private const double WatchdogInterval = 15.0;
        private static double _lastWatchdogCheck = 0;

        // Safety net: recover server after Domain Reload if delayCall failed to fire
        private const double SafetyNetInterval = 5.0;
        private static double _lastSafetyNetCheck = 0;

        // KeepAlive: unconditional wakeup interval (ticks; 5s = 50_000_000 ticks)
        private static long _lastForceWakeTicks = 0;

        // Statistics
        private static long _totalRequestsProcessed = 0;
        private static long _totalRequestsReceived = 0;

        // Startup diagnostic: counts ProcessJobQueue ticks since Start() for self-test diagnostics
        private static volatile int _pjqTicksSinceStart = -1;
        
        // Shared JSON settings from SkillsCommon (single definition, no duplication)
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;
        
        // Persistence keys for Domain Reload recovery (Project Scoped) — lazy-cached
        private static string PrefKey(string key) => $"UnitySkills_{RegistryService.InstanceId}_{key}";

        private static string _prefServerShouldRun;
        private static string _prefAutoStart;
        private static string _prefTotalProcessed;
        private static string _prefLastPort;
        private static string _prefConsecutiveFailures;
        private static string PREF_SERVER_SHOULD_RUN => _prefServerShouldRun ??= PrefKey("ServerShouldRun");
        private static string PREF_AUTO_START => _prefAutoStart ??= PrefKey("AutoStart");
        private static string PREF_TOTAL_PROCESSED => _prefTotalProcessed ??= PrefKey("TotalProcessed");
        private static string PREF_LAST_PORT => _prefLastPort ??= PrefKey("LastPort");
        private static string PREF_CONSECUTIVE_FAILURES => _prefConsecutiveFailures ??= PrefKey("ConsecutiveRestartFailures");
        private const int MaxConsecutiveFailures = 10;

        // Domain Reload tracking
        private static bool _domainReloadPending = false;

        public static bool IsRunning => _isRunning;
        public static string Url => _prefix;
        public static int Port => _port;
        public static int QueuedRequests { get { lock (_queueLock) { return _jobQueue.Count; } } }
        public static long TotalProcessed => _totalRequestsProcessed;

        public static void ResetStatistics()
        {
            _totalRequestsProcessed = 0;
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, "0");
        }
        
        /// <summary>
        /// Gets or sets whether the server should auto-start.
        /// When true, server will automatically restart after Domain Reload.
        /// </summary>
        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(PREF_AUTO_START, true);
            set => EditorPrefs.SetBool(PREF_AUTO_START, value);
        }

        private const string PrefKeyPreferredPort = "UnitySkills_PreferredPort";

        /// <summary>
        /// Gets or sets the preferred port for the server.
        /// 0 = Auto (scan 8090-8100), otherwise use specified port.
        /// </summary>
        public static int PreferredPort
        {
            get => EditorPrefs.GetInt(PrefKeyPreferredPort, 0);
            set => EditorPrefs.SetInt(PrefKeyPreferredPort, value);
        }

        private const string PrefKeyRequestTimeout = "UnitySkills_RequestTimeoutMinutes";

        /// <summary>
        /// Gets or sets the request timeout in minutes.
        /// Default 15 minutes. Minimum 1 minute.
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
        /// Represents a pending HTTP request job.
        /// Created by HTTP thread, processed by Main thread.
        /// </summary>
        private class RequestJob
        {
            // Raw HTTP data (set by HTTP thread)
            public HttpListenerContext Context;
            public string HttpMethod;
            public string Path;
            public string Body;
            public long EnqueueTimeTicks;
            public string RequestId;
            public string AgentId;
            public string QueryString;

            // Result (set by Main thread)
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public int PoolReturned;
            public ManualResetEventSlim CompletionSignal = new ManualResetEventSlim(false);

            public void Prepare(HttpListenerContext context, string httpMethod, string path, string body, string requestId, string agentId, string queryString = null)
            {
                Context = context;
                HttpMethod = httpMethod;
                Path = path;
                Body = body;
                EnqueueTimeTicks = DateTime.UtcNow.Ticks;
                RequestId = requestId;
                AgentId = agentId;
                QueryString = queryString;
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                PoolReturned = 0;
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
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                // Note: PoolReturned is managed by ReturnRequestJob/Prepare, not Reset
                CompletionSignal.Reset();
            }
        }

        // Request ID counter
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
        /// Parse a pre-serialized error JSON string back into a JObject so it can be
        /// passed through SendImmediateJsonResponse without double-encoding.
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
        /// Fast-path responder for cached GET /skills and /skills/schema. Runs ON THE HTTP
        /// LISTENER THREAD — must never touch Unity APIs or SkillsLogger (headers, hashing and
        /// socket writes only). Adds an ETag header and honors If-None-Match with an
        /// empty-body 304 reply.
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

                if (IfNoneMatchSatisfied(request.Headers["If-None-Match"], etag))
                {
                    response.StatusCode = 304; // Not Modified — must not carry a body
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                byte[] buffer = Encoding.UTF8.GetBytes(json);
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
        /// Loose If-None-Match comparison: tolerates quoted values, W/ weak prefixes,
        /// comma-separated lists and the '*' wildcard.
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

        // Agent detection table - keyword to agent ID mapping
        private static readonly (string keyword, string agentId)[] _agentKeywords = new[]
        {
            ("claude", "ClaudeCode"), ("anthropic", "ClaudeCode"),
            ("codex", "Codex"), ("openai", "Codex"),
            ("cursor", "Cursor"),
            ("trae", "Trae"), ("bytedance", "Trae"),
            ("antigravity", "Antigravity"),
            ("windsurf", "Windsurf"), ("codeium", "Windsurf"),
            ("cline", "Cline"), ("roo", "Cline"),
            ("amazon", "AmazonQ"), ("aws", "AmazonQ"),
            ("python-requests", "Python"), ("python", "Python"),
            ("curl", "curl"),
        };

        /// <summary>
        /// Detect AI Agent from User-Agent or X-Agent-Id header
        /// </summary>
        private static string DetectAgent(HttpListenerRequest request)
        {
            // Priority 1: Explicit X-Agent-Id header
            var explicitId = request.Headers["X-Agent-Id"];
            if (!string.IsNullOrEmpty(explicitId))
                return explicitId;

            // Priority 2: Detect from User-Agent via table lookup (OrdinalIgnoreCase avoids ToLowerInvariant allocation)
            var ua = request.UserAgent ?? "";

            foreach (var (keyword, agentId) in _agentKeywords)
            {
                if (ua.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return agentId;
            }

            // Unknown
            return string.IsNullOrEmpty(ua) ? "Unknown" : $"Unknown({ua.Substring(0, Math.Min(20, ua.Length))})";
        }

        /// <summary>
        /// Static constructor - called after every Domain Reload.
        /// This is the key to auto-recovery after script compilation.
        /// </summary>
        static SkillsHttpServer()
        {
            try
            {
                // Register for editor lifecycle events
                EditorApplication.quitting += OnEditorQuitting;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
                CompilationPipeline.compilationStarted += OnCompilationStarted;

                HookUpdateLoop();

                // Check if we should auto-restart after Domain Reload
                // Use delayed call to ensure Unity is fully initialized
                EditorApplication.delayCall += () => ScheduleDelayedCall(1.0, CheckAndRestoreServer);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] SkillsHttpServer init failed: " + ex);
            }
        }
        
        /// <summary>
        /// Called before scripts are compiled - save state.
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

            // Persist statistics
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, _totalRequestsProcessed.ToString());

            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Domain Reload detected - server state saved (port {_port}), will auto-restart");
                EditorPrefs.SetInt(PREF_LAST_PORT, _port);
                RegistryService.Unregister(); // Unregister temporarily
                // Actively close HttpListener to release port immediately
                _isRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                // Wait for threads to exit so port is fully released
                try { _listenerThread?.Join(2000); } catch { }
                try { _keepAliveThread?.Join(100); } catch { }
            }
        }
        
        /// <summary>
        /// Called after scripts are compiled - restore state.
        /// </summary>
        private static void OnAfterAssemblyReload()
        {
            _domainReloadPending = false;
            
            // Restore statistics from before reload
            var savedTotal = EditorPrefs.GetString(PREF_TOTAL_PROCESSED, "0");
            if (long.TryParse(savedTotal, out long parsed))
            {
                _totalRequestsProcessed = parsed;
            }
            // CheckAndRestoreServer will be called via delayCall
        }
        
        /// <summary>
        /// Called when compilation starts.
        /// </summary>
        private static void OnCompilationStarted(object context)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Compilation started - preparing for Domain Reload...");
            }
        }
        
        /// <summary>
        /// Called when editor is quitting - clean shutdown.
        /// </summary>
        private static void OnEditorQuitting()
        {
            // Always clear on quit - we don't want auto-start on next Unity session
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
            EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            Stop();
        }
        
        // Retry counter for CheckAndRestoreServer
        private static int _restoreRetryCount = 0;
        private const int MaxRestoreRetries = 3;
        private static readonly double[] RestoreRetryDelays = { 1.0, 2.0, 4.0 }; // seconds

        /// <summary>
        /// Check if server should be restored after Domain Reload.
        /// Called via EditorApplication.delayCall to ensure Unity is ready.
        /// Retries up to 3 times with increasing delays (1s, 2s, 4s) if Start() fails.
        /// </summary>
        private static void CheckAndRestoreServer()
        {
            bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
            bool autoStart = AutoStart;

            if (shouldRun && autoStart && !_isRunning)
            {
                int failures = EditorPrefs.GetInt(PREF_CONSECUTIVE_FAILURES, 0);

                // Decay: if last failure was more than 5 minutes ago, reset counter
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

                if (failures >= MaxConsecutiveFailures)
                {
                    SkillsLogger.LogError(
                        $"[UnitySkills] Server restart abandoned after {failures} consecutive failures across Domain Reloads.\n" +
                        "Please restart manually: Window > UnitySkills > Start Server");
                    EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                    _restoreRetryCount = 0;
                    return;
                }

                int lastPort = EditorPrefs.GetInt(PREF_LAST_PORT, 0);
                int restorePort = (lastPort >= 8090 && lastPort <= 8100) ? lastPort : PreferredPort;
                SkillsLogger.Log($"Auto-restoring server after Domain Reload (port={restorePort}, attempt {_restoreRetryCount + 1}/{MaxRestoreRetries + 1}, consecutive failures={failures})...");
                Start(restorePort, fallbackToAuto: true);

                if (_isRunning)
                {
                    // 启动成功（failures 已在 Start() 中清零）
                    _restoreRetryCount = 0;
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
                    EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, failures + 1);
                    EditorPrefs.SetString(PrefKey("LastFailTime"), EditorApplication.timeSinceStartup.ToString());
                    SkillsLogger.LogError(
                        $"[UnitySkills] Server failed to restart (consecutive failures: {failures + 1}/{MaxConsecutiveFailures}). " +
                        "Will retry on next Domain Reload. Manual start: Window > UnitySkills > Start Server");
                }
            }
            else
            {
                _restoreRetryCount = 0;
            }
        }

        /// <summary>
        /// Schedule a callback after a real delay in seconds using EditorApplication.update polling.
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
                // Cache keep-alive interval for thread-safe access from KeepAliveLoop
                _cachedKeepAliveIntervalTicks = (long)KeepAliveIntervalSeconds * TimeSpan.TicksPerSecond;

                // Port Hunting: 8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;

                // If preferred port is specified and valid, try it first
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
                    // Auto mode: scan ports
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
                            // Port occupied, try next
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

                // Persist state for Domain Reload recovery
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, true);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0); // 成功启动，清除失败计数

                // Register to global registry
                RegistryService.Register(_port);

                // Start listener thread (Producer - ONLY enqueues, no Unity API)
                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "UnitySkills-Listener" };
                _listenerThread.Start();

                // Start keep-alive thread (forces Unity to update when not focused)
                _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true, Name = "UnitySkills-KeepAlive" };
                _keepAliveThread.Start();

                // These calls are safe here because Start() is called from Main thread
                var skillCount = SkillRouter.SkillCount;
                SkillsLogger.Log($"REST Server started at {_prefix}");
                SkillsLogger.Log($"{skillCount} skills loaded | Instance: {RegistryService.InstanceId}");
                SkillsLogger.LogVerbose($"Domain Reload Recovery: ENABLED (AutoStart={AutoStart})");

                // Initialize heartbeat timer so the first heartbeat doesn't fire immediately during startup
                _lastHeartbeatTime = EditorApplication.timeSinceStartup;
                _lastWatchdogCheck = EditorApplication.timeSinceStartup;

                // Start diagnostic counter for self-test
                _pjqTicksSinceStart = 0;

                // Force an immediate update so ProcessJobQueue starts processing as soon as possible
                EditorApplication.QueuePlayerLoopUpdate();

                // Self-test: verify reachability after a short delay to let the update loop stabilize
                ScheduleDelayedCall(1.5, RunSelfTest);

                // Reconnection anchor for /events clients: carries the last compilation
                // summary because compilation_finished (success) dies with the old domain.
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

            // If permanent stop, clear the auto-restart flag
            if (permanent)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            }

            // Unregister from global registry
            RegistryService.Unregister();

            try { _listener?.Stop(); } catch { /* Best-effort cleanup on shutdown */ }
            try { _listener?.Close(); } catch { /* Best-effort cleanup on shutdown */ }

            // Wait for threads to finish
            try { _listenerThread?.Join(2000); } catch { }
            try { _keepAliveThread?.Join(2000); } catch { }
            _listenerThread = null;
            _keepAliveThread = null;

            // Signal all pending jobs to complete with error
            lock (_queueLock)
            {
                while (_jobQueue.Count > 0)
                {
                    var job = _jobQueue.Dequeue();
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

            if (permanent)
                SkillsLogger.Log($"Server stopped (permanent)");
            else
                SkillsLogger.LogVerbose($"Server stopped (will auto-restart after reload)");
        }
        
        /// <summary>
        /// Stop server permanently without auto-restart.
        /// </summary>
        public static void StopPermanent()
        {
            Stop(permanent: true);
        }
        
        /// <summary>
        /// Keep-alive loop - forces Unity to update when not focused.
        /// Does NOT call any Unity API directly (uses thread-safe QueuePlayerLoopUpdate).
        /// </summary>
        private static void KeepAliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(KeepAlivePollingMs);
                    
                    bool hasPendingJobs;
                    lock (_queueLock)
                    {
                        hasPendingJobs = _jobQueue.Count > 0;
                    }

                    if (hasPendingJobs)
                    {
                        // Thread-safe call to wake up Unity's main thread
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                    else
                    {
                        // No pending jobs: still wake up periodically so watchdog and heartbeat can run
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
                    // Unity 6000.3+ QueuePlayerLoopUpdate may surface a benign
                    // "SetSceneRepaintDirty can only be called from the main thread"
                    // even though the wake-up itself succeeds. Silence the noise;
                    // the queue drain is verified by main-thread ProcessJobQueue.
                    if (ex is UnityException && ex.Message != null && ex.Message.Contains("main thread"))
                        SkillsLogger.LogVerbose($"KeepAlive wake-up benign: {ex.Message.Split('\n')[0]}");
                    else
                        SkillsLogger.LogWarning($"KeepAlive iteration error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// HTTP Listener loop (Producer).
        /// CRITICAL: This runs on a background thread. NO Unity API calls allowed.
        /// Only enqueues raw request data for main thread processing.
        /// </summary>
        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    
                    // Immediately capture raw data (no Unity API)
                    var request = context.Request;
                    string body = "";
                    bool reservedPendingSlot = false;
                    bool handedOffToResponder = false;

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

                    // Fast path: GET /skills and GET /skills/schema are answered directly on this
                    // HTTP thread when SkillRouter's string caches are already built (zero Unity
                    // API — see SkillRouter.TryGetCachedGetResponse). A cache miss falls through
                    // to the normal main-thread queue, which builds the cache for next time.
                    // /health deliberately stays on the main-thread path: clients probe it to
                    // detect main-thread liveness and compilation state.
                    if (request.HttpMethod == "GET")
                    {
                        string fastPath = request.Url.AbsolutePath;

                        // Long-poll: GET /events never enters the main-thread queue. The accept
                        // loop only hands the context to a ThreadPool waiter — it must NEVER
                        // block here (this is the sole accept thread). The responder releases
                        // the pending slot and closes the response on every exit path.
                        if (string.Equals(fastPath, "/events", StringComparison.OrdinalIgnoreCase))
                        {
                            var pollState = new EventsPollState
                            {
                                Context = context,
                                RawQuery = request.Url.Query,
                                RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                                AgentId = DetectAgent(request),
                            };
                            try
                            {
                                ThreadPool.QueueUserWorkItem(EventsLongPollCallback, pollState);
                                handedOffToResponder = true;
                            }
                            finally
                            {
                                if (!handedOffToResponder)
                                    ReleasePendingSlot();
                            }
                            continue;
                        }

                        if ((string.Equals(fastPath, "/skills", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(fastPath, "/skills/schema", StringComparison.OrdinalIgnoreCase)) &&
                            SkillRouter.TryGetCachedGetResponse(fastPath, request.Url.Query, out var cachedJson, out var cachedEtag))
                        {
                            ReleasePendingSlot();
                            reservedPendingSlot = false;
                            SendCachedGetResponse(context, request, cachedJson, cachedEtag);
                            continue;
                        }
                    }

                    if (request.HttpMethod == "POST" && request.ContentLength64 > 0)
                    {
                        if (request.ContentLength64 > MaxBodySizeBytes)
                        {
                            ReleasePendingSlot();
                            SendImmediateJsonResponse(context, request, 413, BuildErrorPayload(SkillErrorResponse.Build(
                                SkillErrorCode.BodyTooLarge,
                                "Request body too large",
                                details: new { maxSizeBytes = MaxBodySizeBytes, receivedBytes = request.ContentLength64 },
                                retryStrategy: SkillErrorResponse.Abort)));
                            continue;
                        }

                        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                    }
                    
                    RequestJob job = null;
                    try
                    {
                        job = RentRequestJob();
                        job.Prepare(
                            context,
                            request.HttpMethod,
                            request.Url.AbsolutePath,
                            body,
                            $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                            DetectAgent(request),
                            request.Url.Query);

                        Interlocked.Increment(ref _totalRequestsReceived);

                        // Enqueue for main thread processing
                        lock (_queueLock)
                        {
                            if (_jobQueue.Count >= MaxQueuedRequests)
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
                            else
                            {
                                _jobQueue.Enqueue(job);
                            }
                        }

                        // Queue the responder with an explicit state object to avoid closure-capture races.
                        ThreadPool.QueueUserWorkItem(WaitAndRespondCallback, job);
                        handedOffToResponder = true;
                        job = null; // Prevent finally from returning to pool (ownership transferred to WaitAndRespond)
                    }
                    finally
                    {
                        if (reservedPendingSlot && !handedOffToResponder)
                            ReleasePendingSlot();
                        if (job != null)
                            ReturnRequestJob(job);
                    }
                }
                catch (HttpListenerException)
                {
                    if (!_isRunning) break;
                    Thread.Sleep(500); // avoid tight exception loop; watchdog will restart if needed
                }
                catch (ObjectDisposedException) { break; } // listener destroyed; watchdog will restart
                catch (Exception)
                {
                    if (!_isRunning) break;
                    Thread.Sleep(1000); // back off on unknown error; watchdog will intervene
                }
            }
        }
        
        /// <summary>
        /// Waits for job completion and sends HTTP response.
        /// Runs on ThreadPool thread - NO Unity API calls.
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
                // Wait for main thread to process (with timeout)
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
                
                // Send HTTP response (thread-safe)
                SendResponse(job);
            }
            catch (Exception ex)
            {
                // Best effort - try to send error response
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
        /// Sends HTTP response. Thread-safe (no Unity API).
        /// </summary>
        private static void SendResponse(RequestJob job)
        {
            HttpListenerResponse response = null;
            try
            {
                response = job.Context.Response;

                // CORS headers
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", job.RequestId);
                response.Headers.Add("X-Agent-Id", job.AgentId);

                response.StatusCode = job.StatusCode;
                
                if (!string.IsNullOrEmpty(job.ResponseJson))
                {
                    response.ContentType = "application/json; charset=utf-8";
                    byte[] buffer = Encoding.UTF8.GetBytes(job.ResponseJson);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }
            catch { /* Ignore write errors - client may have disconnected */ }
            finally
            {
                try { response?.Close(); } catch { /* Best-effort cleanup */ }
            }
        }

        // ===== GET /events long-polling (v2.1) =====

        private const int EventsDefaultTimeoutSeconds = 25;
        private const int EventsMinTimeoutSeconds = 1;
        private const int EventsMaxTimeoutSeconds = 55;
        private const int EventsPollIntervalMs = 250;

        /// <summary>Raw request data handed from the accept loop to the long-poll responder.</summary>
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
                // Client disconnected or the listener died mid-poll — reconnecting is the
                // established protocol; never let this kill the ThreadPool thread noisily.
            }
            finally
            {
                ReleasePendingSlot();
            }
        }

        /// <summary>
        /// GET /events long-poll responder. Runs ENTIRELY on a ThreadPool thread — zero
        /// Unity API, zero SessionState, no SkillsLogger (same constraints as
        /// SendCachedGetResponse). Loops "scan buffer → wait" until events newer than
        /// 'since' show up, the timeout elapses, or the server stops (domain reload) —
        /// then writes the response directly. Correctness relies on the 250ms poll; the
        /// publish signal only reduces latency.
        /// Query: since (default = current max seq → wait for new events only; 0 = replay
        /// buffer), timeout (seconds, default 25, clamp 1-55), types (comma-separated filter).
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
                // Reset BEFORE scanning: a publish landing after the scan re-sets the signal.
                // Another waiter's Reset can still swallow it, which merely costs one 250ms
                // poll interval — never correctness.
                EventChannelService.ResetSignal();

                if (EventChannelService.TryReadEventsAfter(since, typeFilter, out events, out cursor, out oldestSeq))
                    break;

                // Server stopping (domain reload imminent): answer instantly with what we
                // have (nothing) so the client can reconnect instead of hanging.
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

            // since+1 is the first seq the client is missing; anything below oldestSeq was
            // evicted (ring overflow) or lost to a domain reload.
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
        /// Writes the /events HTTP response. ThreadPool thread — headers, encoding and
        /// socket writes only (pure-string sibling of SendCachedGetResponse/SendResponse).
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
        /// Main thread job processor (Consumer).
        /// Runs via EditorApplication.update - ALL Unity API calls are safe here.
        /// </summary>
        private static void ProcessJobQueue()
        {
            // Startup diagnostic counter (lightweight volatile increment, stops at 10000)
            var diagTick = _pjqTicksSinceStart;
            if (diagTick >= 0 && diagTick < 10000)
                _pjqTicksSinceStart = diagTick + 1;

            int processed = 0;
            const int maxPerFrame = 20; // Process more per frame for high throughput
            
            while (processed < maxPerFrame)
            {
                RequestJob job = null;
                
                lock (_queueLock)
                {
                    if (_jobQueue.Count > 0)
                    {
                        job = _jobQueue.Dequeue();
                    }
                }
                
                if (job == null) break;
                
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
                    // Only invalidate scene cache when request may have mutated state (POST = skill execution)
                    if (job.HttpMethod == "POST")
                        GameObjectFinder.InvalidateCache();
                }

                processed++;
            }

            double now = EditorApplication.timeSinceStartup;

            // Heartbeat for Registry
            if (_isRunning)
            {
                if (now - _lastHeartbeatTime > HeartbeatInterval)
                {
                    _lastHeartbeatTime = now;
                    RegistryService.Heartbeat(_port);
                }

                // Watchdog: restart server if listener thread has died
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

            // Safety net: recover server after Domain Reload if delayCall failed to fire
            if (!_isRunning && !_domainReloadPending)
            {
                if (now - _lastSafetyNetCheck > SafetyNetInterval)
                {
                    _lastSafetyNetCheck = now;
                    bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
                    if (shouldRun && AutoStart)
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
        private static void ProcessJob(RequestJob job)
        {
            // Handle OPTIONS (CORS preflight)
            if (job.HttpMethod == "OPTIONS")
            {
                job.StatusCode = 204;
                job.ResponseJson = "";
                return;
            }
            
            string path = job.Path;

            // Health check
            if (path == "/" || string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                int pendingCount = SkillsModeManager.PendingGrantRequests.Count;
                int allowlistCount = SkillsModeManager.AllowlistSkills.Count;
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new {
                    status = "ok",
                    service = "UnitySkills",
                    version = SkillsLogger.Version,
                    unityVersion = Application.unityVersion,
                    instanceId = RegistryService.InstanceId,
                    projectName = RegistryService.ProjectName,
                    serverRunning = _isRunning,
                    queuedRequests = QueuedRequests,
                    totalProcessed = _totalRequestsProcessed,
                    autoRestart = AutoStart,
                    requestTimeoutMinutes = RequestTimeoutMinutes,
                    domainReloadRecovery = "enabled",
                    architecture = "Producer-Consumer (Thread-Safe)",
                    currentMode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                    panelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                    pendingCount,
                    allowlistCount,
                    // Deprecated alias for allowlistCount, kept for v1.9.x backward compatibility
                    // (mirrors the `granted` / `counts.granted` aliases on /permission/status).
                    // Safe to remove in a future major version once external consumers migrate.
                    grantedCount = allowlistCount,
                    threads = new {
                        listenerAlive = _listenerThread?.IsAlive ?? false,
                        keepAliveAlive = _keepAliveThread?.IsAlive ?? false,
                    },
                    compilation = new {
                        isCompiling = EditorApplication.isCompiling,
                        isUpdating = EditorApplication.isUpdating,
                        domainReloadPending = _domainReloadPending,
                    },
                    queueStats = new {
                        queued = QueuedRequests,
                        totalReceived = _totalRequestsReceived,
                    },
                    note = "If you get 'Connection Refused', Unity may be reloading scripts. Wait 2-3 seconds and retry."
                }, _jsonSettings);
                return;
            }

            // Compilation feedback loop — authoritative "did my last script edit compile?" query.
            // Runs on the main-thread path (like /health) so it can read live editor state and the
            // last result, which survives the domain reload a successful compile triggers.
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

            // Execution telemetry aggregation — "which skills are called / failing / slow?".
            // Main-thread path (like /health): reads the telemetry EditorPref + the JSONL files.
            // Results are cached 30s per window inside SkillTelemetryService to bound disk reads.
            if (string.Equals(path, "/analytics", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                var analyticsQs = SkillRouter.ParseQueryString(job.QueryString);
                string window = analyticsQs.TryGetValue("window", out var windowVal) ? windowVal : "24h";
                job.StatusCode = 200;
                job.ResponseJson = SkillTelemetryService.BuildAnalyticsJson(window);
                return;
            }

            // Get skills manifest (with optional filtering)
            if (string.Equals(path, "/skills", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = string.IsNullOrEmpty(job.QueryString)
                    ? SkillRouter.GetManifest()
                    : SkillRouter.GetFilteredManifest(job.QueryString);
                return;
            }

            if (string.Equals(path, "/skills/schema", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = string.IsNullOrEmpty(job.QueryString)
                    ? SkillRouter.GetSchema()
                    : SkillRouter.GetFilteredSchema(job.QueryString);
                return;
            }

            // Skill recommendation by intent
            if (string.Equals(path, "/skills/recommend", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetRecommendations(job.QueryString);
                return;
            }

            // Skill dependency chain
            if (string.Equals(path, "/skills/chain", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetSkillChain(job.QueryString);
                return;
            }

            // Cross-skill aggregated execution (each step runs the full Execute pipeline)
            if (string.Equals(path, "/skills/batch", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                HandleSkillsBatchRequest(job);
                return;
            }

            // Job query (lightweight GET, bypasses skill router for high-frequency progress polling)
            if (job.HttpMethod == "GET" &&
                (string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase)))
            {
                HandleJobsRequest(job);
                return;
            }
            
            // Execute / DryRun / Plan skill
            if (path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                if (RejectIfCompiling(job))
                    return;

                // Extract skill name (preserve original case) and validate
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


            // Permission system (v1.9): mode + grant token + audit log.
            if (path.StartsWith("/permission/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/permission", StringComparison.OrdinalIgnoreCase))
            {
                HandlePermissionRequest(job);
                return;
            }


            // Not found
            job.StatusCode = 404;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.NotFound,
                "Not found",
                details: new {
                    endpoints = new[]
                    {
                        "GET /skills",
                        "GET /skills/schema",
                        "GET /skills/recommend",
                        "GET /skills/chain",
                        "POST /skills/batch",
                        "POST /skill/{name}",
                        "POST /skill/{name}?mode=dryRun",
                        "POST /skill/{name}?mode=plan",
                        "POST /skill/{name}?dryRun=true",
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
        /// Resolves ?mode= / ?dryRun= from the parsed query string. Returns false (and writes an
        /// INVALID_MODE error response to the job) when either parameter is present with an
        /// unrecognized value — the request must NOT be executed in that case. Without this
        /// guard, an agent that misspells the mode (e.g. ?mode=dry_run, ?dryRun=1) believes it
        /// is previewing while the server silently executes for real.
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
                    return true; // explicit false = execute for real

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
        /// Resolves ?diff= for POST /skill/{name}. A semantic sceneDiff is only meaningful for a
        /// real execution, so it is silently ignored under ?mode=dryRun / ?mode=plan (nothing is
        /// executed, nothing to diff). An unrecognized value is rejected with 400 (mirroring
        /// TryResolveRequestMode) rather than silently ignored, so an agent that misspells ?diff
        /// never believes it requested a diff while the server quietly omits it. Returns false
        /// (and writes a 400) only on an invalid value; captureDiff is set otherwise.
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

            // Diff only applies to a real execution; a dryRun/plan preview has nothing to diff.
            captureDiff = requested && mode == SkillRouter.RequestMode.Execute;
            return true;
        }

        /// <summary>
        /// Writes a 503 COMPILING response when Unity is compiling or a Domain Reload is
        /// pending. Returns true when the request was rejected. Shared by POST /skill/{name}
        /// and POST /skills/batch (the latter does not match the "/skill/" prefix check).
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

        // ===== Execution telemetry (v2.1) =====

        /// <summary>
        /// Records one POST /skill/{name} outcome to <see cref="SkillTelemetryService"/>. Uses a
        /// lightweight string probe (not JObject.Parse — this is the hot single-skill path) to
        /// classify ok + extract errorCode. Fully isolated: a telemetry failure never changes the
        /// business response the caller already computed.
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
        /// Records one /skills/batch step outcome. The batch loop already holds each step's parsed
        /// payload, so ok/errorCode are passed in directly (no string probing). A null/blank skill
        /// name (a malformed step) is logged as "(malformed)". mode is batch_step /
        /// batch_step_dryRun per the dryRun flag.
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
        /// Classifies a skill response by scanning the raw JSON string — cheap enough for the hot
        /// path and tolerant of nested content. An error envelope (<c>"status":"error"</c>) is a
        /// failure and its <c>"errorCode"</c> is extracted. For a dryRun preview, a
        /// <c>"valid":false</c> verdict is a failure reported as DRYRUN_INVALID (an unknown-skill
        /// dryRun comes back as an error envelope and is caught by the first check).
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
        /// Extracts the value of the first <c>"errorCode":"..."</c> field. Returns null when the
        /// field is absent or is JSON null (<c>"errorCode":null</c> does not match the quoted
        /// probe), so the telemetry line records a null errorCode rather than a wrong one.
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

        // ===== Cross-skill batch execution (v2.1) =====

        private const int MaxBatchSteps = 50;

        /// <summary>
        /// POST /skills/batch — executes several skills sequentially inside one main-thread job,
        /// saving one HTTP round-trip + main-thread wakeup per step.
        ///
        /// Body: {"steps":[{"skill":"gameobject_create","args":{...}}, ...], "continueOnError":false}
        /// - Each step runs the FULL SkillRouter.Execute pipeline (permission gate, semantic
        ///   validation, undo, audit) exactly like a standalone POST /skill/{name}; each step
        ///   gets its own undo group (no merging).
        /// - Default is fail-fast: the first failed step stops the batch and the remaining steps
        ///   are reported as "skipped". With continueOnError=true failed steps are recorded and
        ///   the batch continues. Authorization-type responses (MODE_RESTRICTED /
        ///   CONFIRMATION_REQUIRED) ALWAYS interrupt regardless of continueOnError — they cannot
        ///   be skipped; the step's full response (incl. grant token) is returned so the caller
        ///   can complete the grant flow and re-submit the remaining steps.
        /// - Static $param slots: a body-level "params":{"name":value,...} object fills
        ///   placeholder nodes in a step's structured args. Any object whose ONLY key is "$param"
        ///   (e.g. {"$param":"height"}), or exactly {"$param":"name","default":X}, at any depth,
        ///   is replaced by params[name] when present, else its "default", else the step fails
        ///   SEMANTIC_INVALID (details.param names the missing slot). $param is a pure static
        ///   substitution with no step-order dependency, so it is resolved BEFORE $ref and
        ///   identically in dryRun and execute (real values always exist — a missing slot fails a
        ///   dry-run too, surfacing gaps before replay). $param and $ref are orthogonal: a step
        ///   may use both, but a single node is one or the other, never both
        ///   ({"$param":..,"$ref":..} is rejected SEMANTIC_INVALID). Any $ref left after
        ///   substitution goes through the $ref stage below.
        /// - Inter-step references (v2): inside a step's structured args, any object whose ONLY
        ///   key is "$ref" (e.g. {"$ref":"$0.instanceId"}), at any depth, is replaced before that
        ///   step executes. "$N" is the 0-based index of an EARLIER successful step; the part
        ///   after the dot is a Newtonsoft SelectToken path into that step's unwrapped result
        ///   ("$0" alone = the whole result, "$1.items[0].path" digs into arrays). Unresolvable
        ///   refs (malformed / index out of range / forward reference / referenced step not
        ///   successful / path with no match) fail the step with SEMANTIC_INVALID and then follow
        ///   the normal fail-fast / continueOnError rules. Refs inside string-typed args are NOT
        ///   resolved — only structured JSON args are scanned.
        /// - ?mode=dryRun validates every step without executing anything and never interrupts,
        ///   so an agent can preview the whole sequence in one call. $ref params carry no real
        ///   value in a dry-run: they are stripped from the validation body and checked
        ///   structurally only (index range, ordering, referenced skill's declared Outputs); such
        ///   steps carry refsValidated + findings in validation.warnings. ?mode=plan is not
        ///   supported.
        /// - ?mode=transactional makes the batch all-or-nothing: unknown skills and steps whose
        ///   skill declares MayTriggerReload are rejected up front with 400 (a domain reload
        ///   wipes the editor undo stack, so the rollback promise could not be kept), and
        ///   continueOnError=true is rejected as contradictory. When any step fails — including
        ///   authorization interrupts, whose grant token is still returned — every already
        ///   executed step is reverted via Undo.RevertAllDownToGroup and re-labelled
        ///   status:"rolled_back" (steps of MutatesAssets skills get
        ///   rollbackReliability:"partial": AssetDatabase disk writes are not fully covered by
        ///   the undo stack). The response then reports status:"rolled_back" + rolledBack:true.
        ///   Transactional mode composes freely with $ref references.
        /// </summary>
        private static void HandleSkillsBatchRequest(RequestJob job)
        {
            if (RejectIfCompiling(job))
                return;

            var qs = SkillRouter.ParseQueryString(job.QueryString);
            if (!TryResolveBatchRequestMode(job, qs, out bool dryRun, out bool transactional))
                return;

            if (!TryParseBody(job, out var body)) return;

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

            bool continueOnError = body.TryGetValue("continueOnError", StringComparison.OrdinalIgnoreCase, out var coeToken)
                && coeToken.Type == JTokenType.Boolean && coeToken.ToObject<bool>();

            // Body-level "params" fills $param slots inside step args (static, mode-agnostic).
            JObject batchParams = null;
            if (body.TryGetValue("params", StringComparison.OrdinalIgnoreCase, out var paramsToken) && paramsToken is JObject paramsObj)
                batchParams = paramsObj;

            if (transactional && RejectTransactionalPrecheck(job, steps, continueOnError))
                return;

            var response = ExecuteBatchCore(steps, batchParams, continueOnError, dryRun, transactional, job.AgentId);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(response, _jsonSettings);
        }

        /// <summary>
        /// Core sequential executor behind POST /skills/batch and macro_run: $param substitution,
        /// inter-step $ref resolution, then the FULL single-skill pipeline per step
        /// (SkillRouter.Execute — permission gate, undo, audit), with fail-fast /
        /// continueOnError / authorization-interrupt semantics and optional transactional
        /// rollback. Callers must pass a validated non-empty steps array (and, for
        /// transactional, run RejectTransactionalPrecheck first). Returns the response body
        /// ({status, executed, failed, results, ...}) as a JObject.
        /// </summary>
        internal static JObject ExecuteBatchCore(JArray steps, JObject batchParams, bool continueOnError,
            bool dryRun, bool transactional, string agentId)
        {
            int txStartGroup = -1;
            if (transactional)
            {
                // Fence the whole batch in the undo timeline. Each step still opens (and
                // collapses) its own undo group inside Execute; on failure everything above
                // this fence is reverted in one shot.
                Undo.IncrementCurrentGroup();
                txStartGroup = Undo.GetCurrentGroup();
            }

            var results = new List<JObject>(steps.Count);
            // Unwrapped result of each successful step, resolvable by later steps via $ref.
            var stepResults = new JToken[steps.Count];
            int executedCount = 0;
            int failedCount = 0;
            bool halted = false;

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

                // ---- Static $param substitution (v2, resolved before $ref) ----
                // Pure static replacement from the body-level "params" object, so dryRun and
                // execute resolve it identically (real values exist either way). Any $ref left
                // in the substituted args is handled by the $ref stage below.
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
                            // Substitute on a deep copy; the original request body is never mutated.
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
                                // Feed the substituted args into the $ref stage below.
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

                // ---- Inter-step $ref references (v2) ----
                List<BatchRefNode> refNodes = null;          // dryRun bookkeeping
                HashSet<string> strippedRefParams = null;    // dryRun: params removed from the validation body
                bool wholeArgsFromRef = false;               // dryRun: the args root itself is a $ref
                List<string> refWarnings = null;             // dryRun: structural findings
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

                            // References carry no real value during a dry-run. Params holding a
                            // $ref are removed from the validation body — leaving the placeholder
                            // objects in would only produce TYPE_MISMATCH noise. The resulting
                            // MISSING_PARAM / semantic gaps are reconciled after DryRun returns
                            // (see AdjustDryRunPayloadForRefs).
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
                        // Resolve against earlier step results on a deep copy; the original
                        // request body is never mutated.
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
                        // Batch steps use the single-arg Execute overload (captureDiff:false):
                        // ?diff=1 is a per-skill feature and is not plumbed through /skills/batch
                        // (v1 boundary — batch has no sceneDiff).
                        stepJson = SkillRouter.Execute(stepSkillName, argsJson);
                        // Steps share one POST job, so the per-request invalidation in
                        // ProcessJobQueue never runs between them — without this, a step
                        // can't find objects created by an earlier step in the same batch.
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
                    // $ref params were stripped from the validation body — reconcile the payload
                    // (missingParams filter, semantic downgrade, refsValidated) BEFORE reading
                    // its 'valid' verdict.
                    if (refNodes != null && refNodes.Count > 0)
                        AdjustDryRunPayloadForRefs(stepPayload, refNodes, strippedRefParams, wholeArgsFromRef, refWarnings);

                    // DryRun responses carry status:"dryRun" + valid:bool; unknown skills come
                    // back as status:"error". Validation failures never halt a dry-run batch.
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

                    // Authorization-type responses can never be skipped: the caller must handle
                    // the grant/confirmation flow, so the batch stops here even with
                    // continueOnError=true. The full payload above carries the grant token.
                    string errorCode = stepPayload["errorCode"]?.ToString();
                    bool authorizationRequired =
                        string.Equals(errorCode, "MODE_RESTRICTED", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(errorCode, "CONFIRMATION_REQUIRED", StringComparison.OrdinalIgnoreCase);

                    if (authorizationRequired || !continueOnError)
                        halted = true;
                    RecordBatchStep(stepSkillName, agentId, dryRun, false, errorCode, stepSw.ElapsedMilliseconds);
                    continue;
                }

                // status:"success" (or any non-error shape) — unwrap the inner result;
                // the entry-level status field already expresses success.
                executedCount++;
                var unwrappedResult = stepPayload.TryGetValue("result", out var innerResult) ? innerResult : stepPayload;
                stepResults[i] = unwrappedResult;
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
                // All-or-nothing: any failure (incl. authorization interrupts — the failed
                // step's entry above still carries the grant token untouched) reverts every
                // step executed since the batch fence, without leaving redo entries.
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
                    // AssetDatabase disk writes are not fully covered by the undo stack —
                    // flag those rollbacks as partial instead of over-promising.
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
            return response;
        }

        /// <summary>One $param name aggregated across a step sequence (macro-library introspection).</summary>
        internal sealed class BatchParamDeclaration
        {
            public string Name;
            public bool HasDefault;      // every node referencing this name carries an inline default
            public JToken DefaultValue;  // first inline default seen (display only)
        }

        /// <summary>
        /// Aggregates the $param slots declared across a whole step sequence, keyed by name and in
        /// first-occurrence order. A name counts as having a default only when EVERY node
        /// referencing it carries an inline "default" — a single bare {"$param":"x"} slot makes the
        /// value mandatory. Malformed slots (non-string $param name) are skipped here; execution
        /// reports them per step. String-typed args are not scanned, mirroring execution.
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
        /// Resolves ?mode= / ?dryRun= for /skills/batch. Batch accepts dryRun/transactional
        /// (single-skill requests accept dryRun/plan instead — see TryResolveRequestMode, which
        /// keeps rejecting 'transactional' so its INVALID_MODE validValues stay accurate).
        /// Returns false (and writes the error response) on any unrecognized value.
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
                    return true; // explicit false = execute for real

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
        /// Transactional batches promise all-or-nothing via the editor undo stack, so anything
        /// that would break that promise is rejected up front (400 SEMANTIC_INVALID) instead of
        /// failing mid-flight: unknown/malformed steps, skills that may trigger a domain reload
        /// (a reload wipes the undo stack), and continueOnError=true (a transaction is
        /// fail-fast by definition). Returns true when the batch was rejected.
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

        // ===== Static $param substitution (batch v2) =====

        /// <summary>
        /// A {"$param":"name"} / {"$param":"name","default":X} slot found inside a step's args.
        /// ParamName is null when the $param value is not a JSON string (reported as malformed at
        /// resolve time). Unlike BatchRefNode there is no TopLevelParam: $param carries a real
        /// value in every mode, so nothing is stripped from the dry-run validation body.
        /// </summary>
        private sealed class BatchParamNode
        {
            public JObject Node;
            public string ParamName;
            public bool HasDefault;
            public JToken DefaultValue;
        }

        /// <summary>
        /// An object node is a parameter node iff "$param" is its ONLY property (a bare slot), or
        /// its EXACTLY two properties are "$param" + "default" (a slot with a fallback). Objects
        /// that merely contain "$param" among other keys are payload data and stay untouched —
        /// mirrors IsBatchRefNode. paramName is null when the $param value is not a JSON string.
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
        /// Collects every $param slot in a step's args (any depth). paramRefConflict is set to the
        /// first node carrying BOTH "$param" and "$ref" (a node is one or the other, never both)
        /// so the caller can reject it SEMANTIC_INVALID; the search stops at that node.
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
        /// Resolves one slot's value: the batch "params" object wins when it holds the name
        /// (case-sensitive), else the node's inline "default", else the step fails
        /// SEMANTIC_INVALID ("not provided and no default"). A non-string $param name is malformed.
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

        // ===== Inter-step $ref references (batch v2) =====

        /// <summary>
        /// A {"$ref":"$N.path"} node found inside a step's args. RefString is null when the
        /// $ref value is not a JSON string (reported as malformed at resolve time).
        /// TopLevelParam is the args property whose subtree contains the node, or null when
        /// the node IS the args root.
        /// </summary>
        private sealed class BatchRefNode
        {
            public JObject Node;
            public string RefString;
            public string TopLevelParam;
        }

        /// <summary>
        /// An object node is a reference iff "$ref" is its ONLY property; objects that merely
        /// contain a "$ref" key among others are payload data and stay untouched.
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
        /// Parses "$N", "$N.path" or "$N[…]" — N is the 0-based step index; the remainder is a
        /// Newtonsoft SelectToken path into that step's unwrapped result.
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
                return true; // "$N" — the whole unwrapped result

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
        /// Resolves one reference against the unwrapped results of already-executed steps.
        /// Fails (with a structured reason) on: malformed ref, index outside the batch,
        /// forward reference (N >= current step), referenced step not completed successfully,
        /// or a SelectToken path with no match.
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
        /// Dry-run structural validation of one reference (no real values exist to resolve):
        /// index in range and pointing at an earlier step, referenced skill known, first path
        /// segment present in the referenced skill's declared Outputs. All findings are
        /// warnings — Outputs metadata may be incomplete, and a dry-run batch never halts.
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
                return; // no declared outputs to check against
            string firstSegment = FirstSelectTokenSegment(selectPath);
            if (firstSegment == null)
                return; // "[0]…" indexes into the result root; nothing to name-check

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
        /// Post-processes a step's DryRun payload after its $ref params were stripped from the
        /// validation body: drops MISSING_PARAM entries caused by the stripping, downgrades the
        /// step's semanticErrors to warnings (semantic checks ran without the reference values,
        /// so both pass and fail verdicts would be guesses), recomputes 'valid' from what is
        /// left, and attaches refsValidated so callers see which params got structural-only
        /// treatment.
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
                return; // error payload (unknown skill etc.) — refsValidated attached, nothing to adjust

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
        /// Routes GET /jobs and GET /jobs/{id}[/logs] to BatchPersistence without going
        /// through the skill router. Designed for high-frequency progress polling: the
        /// caller pings GET /jobs/{id} every 200-500 ms and gets a fresh snapshot.
        /// </summary>
        private static void HandleJobsRequest(RequestJob job)
        {
            string path = job.Path ?? string.Empty;
            var qs = SkillRouter.ParseQueryString(job.QueryString);

            // GET /jobs  → list
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

            // GET /jobs/{id} (default — full status snapshot)
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

        // ===== Permission system (v1.9) =====

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
                    // Deprecated alias: forwards to allowlist/remove logic, response includes deprecated=true.
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
            // v1.9 字段重命名：`granted` → `allowlist`。`granted` 字段作为兼容别名保留一个版本，
            // 下个 minor 版本会移除——客户端应迁移到 `allowlist` 字段。
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                mode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                panelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                allowlist = allowlist,
                granted = allowlist, // deprecated alias — remove in next minor
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
                    granted = allowlist.Count, // deprecated alias
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

            // Deprecated alias: forwards to AllowlistRemove / ClearAllowlist. Response carries
            // `deprecated: true` so clients can migrate to /permission/allowlist/remove.
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

        // ===== Allowlist endpoints (v1.9 改版) =====

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
            // Re-serialize without _confirm so hashing matches the SkillRouter-side normalization.
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
                // 1. Reachability test with retry using raw TCP (bypasses .NET HTTP client stack entirely)
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
                        if (attempt > 1) Thread.Sleep(attempt * 1500); // 3s, 4.5s backoff

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
                                    // Retry localhost on other loopback addresses before logging a warning.
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

                // 2. Port scan: report occupied ports in 8090-8100
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
                    // Fall back to known loopback addresses below.
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

