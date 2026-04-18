using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;

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
        private static string _prefix => $"{_prefixBase}{_port}/";
        
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
        private const double HeartbeatInterval = 10.0;
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
        
        // Keep Unicode readable instead of forcing escaped sequences.
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.Default
        };
        
        // Persistence keys for Domain Reload recovery (Project Scoped)
        private static string PrefKey(string key) => $"UnitySkills_{RegistryService.InstanceId}_{key}";
        
        private static string PREF_SERVER_SHOULD_RUN => PrefKey("ServerShouldRun");
        private static string PREF_AUTO_START => PrefKey("AutoStart");
        private static string PREF_TOTAL_PROCESSED => PrefKey("TotalProcessed");
        private static string PREF_LAST_PORT => PrefKey("LastPort");
        private static string PREF_CONSECUTIVE_FAILURES => PrefKey("ConsecutiveRestartFailures");
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
            catch
            {
                // Ignore write errors. The client may have already disconnected.
            }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        // Agent detection table - keyword to agent ID mapping
        private static readonly (string keyword, string agentId)[] _agentKeywords = new[]
        {
            ("claude", "ClaudeCode"), ("anthropic", "ClaudeCode"),
            ("codex", "Codex"), ("openai", "Codex"),
            ("gemini", "Gemini"), ("google", "Gemini"),
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

            // Priority 2: Detect from User-Agent via table lookup
            var ua = request.UserAgent ?? "";
            var uaLower = ua.ToLowerInvariant();

            foreach (var (keyword, agentId) in _agentKeywords)
            {
                if (uaLower.Contains(keyword))
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
                var skillCount = SkillRouter.GetManifest().Split('\n').Length;
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
                    job.ResponseJson = JsonConvert.SerializeObject(new { error = "Server stopped" }, _jsonSettings);
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
                catch { /* Ignore */ }
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
                        SendImmediateJsonResponse(context, request, 429, new
                        {
                            error = "Rate limit exceeded",
                            limit = MaxRequestsPerSecond,
                            suggestion = "Please slow down requests"
                        });
                        continue;
                    }

                    reservedPendingSlot = TryReservePendingSlot();
                    if (!reservedPendingSlot)
                    {
                        SendImmediateJsonResponse(context, request, 503, new
                        {
                            error = "Too many pending requests",
                            pendingLimit = MaxPendingRequests,
                            suggestion = "Please retry after current requests complete"
                        });
                        continue;
                    }
                    
                    if (request.HttpMethod == "POST" && request.ContentLength64 > 0)
                    {
                        if (request.ContentLength64 > MaxBodySizeBytes)
                        {
                            ReleasePendingSlot();
                            SendImmediateJsonResponse(context, request, 413, new
                            {
                                error = "Request body too large",
                                maxSizeBytes = MaxBodySizeBytes,
                                receivedBytes = request.ContentLength64
                            });
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
                                job.ResponseJson = JsonConvert.SerializeObject(new
                                {
                                    error = "Request queue is full",
                                    queueLimit = MaxQueuedRequests,
                                    suggestion = "Please retry after current requests complete"
                                }, _jsonSettings);
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
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = $"Gateway Timeout: Main thread did not respond within {RequestTimeoutMs / 1000} seconds",
                        diagnostics = new {
                            domainReloadPending = _domainReloadPending,
                            queuedRequests = QueuedRequests,
                            listenerAlive = _listenerThread?.IsAlive ?? false,
                            keepAliveAlive = _keepAliveThread?.IsAlive ?? false,
                        },
                        suggestion = _domainReloadPending
                            ? "Unity is reloading scripts. Wait a few seconds and retry."
                            : "Unity Editor may be paused, showing a modal dialog, or processing a long operation. " +
                              "Please check the Unity Editor window for any dialogs or errors.",
                        manualAction = "If the server is unresponsive, restart via: Window > UnitySkills > Start Server",
                        retryAfterSeconds = _domainReloadPending ? 5 : 10,
                        retryStrategy = "wait_and_retry"
                    }, _jsonSettings);
                }
                
                // Send HTTP response (thread-safe)
                SendResponse(job);
            }
            catch (Exception)
            {
                // Best effort - try to send error response
                try
                {
                    job.StatusCode = 500;
                    job.ResponseJson = JsonConvert.SerializeObject(new { error = "Internal server error" }, _jsonSettings);
                    SendResponse(job);
                }
                catch { }
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
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = ex.Message,
                        type = ex.GetType().Name
                    }, _jsonSettings);
                    SkillsLogger.LogWarning($"Job processing error: {ex.Message}");
                }
                finally
                {
                    job.IsProcessed = true;
                    job.CompletionSignal?.Set();
                    Interlocked.Increment(ref _totalRequestsProcessed);
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
            
            string path = job.Path.ToLower();
            
            // Health check
            if (path == "/" || path == "/health")
            {
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
            
            // Get skills manifest (with optional filtering)
            if (path == "/skills" && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = string.IsNullOrEmpty(job.QueryString)
                    ? SkillRouter.GetManifest()
                    : SkillRouter.GetFilteredManifest(job.QueryString);
                return;
            }

            if (path == "/skills/schema" && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetSchema();
                return;
            }

            // Skill recommendation by intent
            if (path == "/skills/recommend" && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetRecommendations(job.QueryString);
                return;
            }

            // Skill dependency chain
            if (path == "/skills/chain" && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetSkillChain(job.QueryString);
                return;
            }
            
            // Execute / DryRun / Plan skill
            if (path.StartsWith("/skill/") && job.HttpMethod == "POST")
            {
                if (_domainReloadPending || ServerAvailabilityHelper.IsCompilationInProgress())
                {
                    job.StatusCode = 503;
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = "Unity is compiling or reloading scripts",
                        diagnostics = new {
                            isCompiling = EditorApplication.isCompiling,
                            isUpdating = EditorApplication.isUpdating,
                            domainReloadPending = _domainReloadPending,
                        },
                        suggestion = "The REST server is temporarily unavailable during compilation. Wait a few seconds and retry.",
                        manualAction = "If this persists, check Unity Editor for compilation errors or stuck dialogs.",
                        retryAfterSeconds = _domainReloadPending ? 8 : 5,
                        retryStrategy = "wait_and_retry"
                    }, _jsonSettings);
                    return;
                }

                // Extract skill name (preserve original case) and validate
                string skillName = job.Path.Substring(7);
                if (skillName.Contains("/") || skillName.Contains("\\") || skillName.Contains(".."))
                {
                    job.StatusCode = 400;
                    job.ResponseJson = JsonConvert.SerializeObject(new { error = "Invalid skill name" }, _jsonSettings);
                    return;
                }

                var skillQs = SkillRouter.ParseQueryString(job.QueryString);
                SkillRouter.RequestMode mode = SkillRouter.RequestMode.Execute;
                if (skillQs.TryGetValue("mode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
                {
                    if (modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase))
                        mode = SkillRouter.RequestMode.DryRun;
                    else if (modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase))
                        mode = SkillRouter.RequestMode.Plan;
                }
                else if (skillQs.TryGetValue("dryRun", out var dryRunVal) && dryRunVal.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.DryRun;
                }

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
                            job.ResponseJson = SkillRouter.Execute(skillName, job.Body);
                            SkillsLogger.LogAgent(job.AgentId, skillName);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    job.StatusCode = 500;
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = ex.Message,
                        type = ex.GetType().Name,
                        skill = skillName,
                        suggestion = "If this error persists, check Unity console for details. " +
                                    "For 'Connection Refused' errors, Unity may be reloading scripts - wait 2-3 seconds and retry."
                    }, _jsonSettings);
                    SkillsLogger.LogWarning($"Skill '{skillName}' error: {ex.Message}");
                }
                return;
            }


            // Not found
            job.StatusCode = 404;
            job.ResponseJson = JsonConvert.SerializeObject(new {
                error = "Not found",
                endpoints = new[]
                {
                    "GET /skills",
                    "GET /skills/schema",
                    "GET /skills/recommend",
                    "GET /skills/chain",
                    "POST /skill/{name}",
                    "POST /skill/{name}?mode=dryRun",
                    "POST /skill/{name}?mode=plan",
                    "POST /skill/{name}?dryRun=true",
                    "GET /health"
                }
            }, _jsonSettings);
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

