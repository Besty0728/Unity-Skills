using System;
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
        
        // Rate limiting (processed on main thread only)
        private static int _requestsThisSecond = 0;
        private static long _lastRateLimitResetTicks = 0;
        private const int MaxRequestsPerSecond = 100;
        
        // Keep-alive interval (ms)
        private const int KeepAliveIntervalMs = 50;
        // Request processing timeout (ms)
        private const int RequestTimeoutMs = 60000;
        // Maximum allowed POST body size
        private const int MaxBodySizeBytes = 10 * 1024 * 1024; // 10MB
        // Heartbeat interval for registry (seconds)
        private const double HeartbeatInterval = 10.0;
        private static double _lastHeartbeatTime = 0;

        // Statistics
        private static long _totalRequestsProcessed = 0;
        private static long _totalRequestsReceived = 0;
        
        // JSON 序列化设置，禁用 Unicode 转义确保中文正确显示
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.Default
        };
        
        // Persistence keys for Domain Reload recovery (Project Scoped)
        private static string PrefKey(string key) => $"UnitySkills_{RegistryService.InstanceId}_{key}";
        
        private static string PREF_SERVER_SHOULD_RUN => PrefKey("ServerShouldRun");
        private static string PREF_AUTO_START => PrefKey("AutoStart");
        private static string PREF_TOTAL_PROCESSED => PrefKey("TotalProcessed");
        
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

            // Result (set by Main thread)
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public ManualResetEventSlim CompletionSignal;
        }

        // Request ID counter
        private static long _requestIdCounter = 0;

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
            EditorApplication.delayCall += CheckAndRestoreServer;
        }
        
        /// <summary>
        /// Called before scripts are compiled - save state.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            _domainReloadPending = true;

            // Persist the "should run" state before domain is destroyed
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, _isRunning);

            // Persist statistics
            EditorPrefs.SetString(PREF_TOTAL_PROCESSED, _totalRequestsProcessed.ToString());

            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Domain Reload detected - server state saved (port {_port}), will auto-restart");
                RegistryService.Unregister(); // Unregister temporarily
                // Don't call Stop() here - domain will be destroyed anyway
                // Just mark as not running to prevent errors
                _isRunning = false;
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
            Stop();
        }
        
        /// <summary>
        /// Check if server should be restored after Domain Reload.
        /// Called via EditorApplication.delayCall to ensure Unity is ready.
        /// </summary>
        private static void CheckAndRestoreServer()
        {
            bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
            bool autoStart = AutoStart;

            if (shouldRun && autoStart)
            {
                if (!_isRunning)
                {
                    SkillsLogger.Log($"Auto-restoring server after Domain Reload...");
                    Start(PreferredPort);
                }
            }
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

        public static void Start(int preferredPort = 0)
        {
            if (_isRunning)
            {
                SkillsLogger.LogVerbose($"Server already running at {_prefix}");
                return;
            }

            try
            {
                HookUpdateLoop();

                // Port Hunting: 8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;

                // If preferred port is specified and valid, try it first
                if (preferredPort >= startPort && preferredPort <= endPort)
                {
                    try
                    {
                        string prefix = $"{_prefixBase}{preferredPort}/";
                        _listener = new HttpListener();
                        _listener.Prefixes.Add(prefix);
                        _listener.Start();

                        _port = preferredPort;
                        started = true;
                    }
                    catch
                    {
                        try { _listener?.Close(); } catch { }
                        SkillsLogger.LogError($"Port {preferredPort} is in use. Try another port or use Auto.");
                        return;
                    }
                }
                else
                {
                    // Auto mode: scan ports
                    for (int p = startPort; p <= endPort; p++)
                    {
                        try
                        {
                            string prefix = $"{_prefixBase}{p}/";
                            _listener = new HttpListener();
                            _listener.Prefixes.Add(prefix);
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

                // Self-test: verify reachability after Start() returns
                EditorApplication.delayCall += RunSelfTest;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to start: {ex.Message}");
                _isRunning = false;
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
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
                    Thread.Sleep(KeepAliveIntervalMs);
                    
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
                    
                    if (request.HttpMethod == "POST" && request.ContentLength64 > 0)
                    {
                        if (request.ContentLength64 > MaxBodySizeBytes)
                        {
                            // Reject oversized request immediately
                            ManualResetEventSlim rejectSignal = null;
                            try
                            {
                                rejectSignal = new ManualResetEventSlim(true); // Already signaled
                                var rejectJob = new RequestJob
                                {
                                    Context = context,
                                    HttpMethod = request.HttpMethod,
                                    Path = request.Url.AbsolutePath,
                                    Body = "",
                                    EnqueueTimeTicks = DateTime.UtcNow.Ticks,
                                    RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                                    AgentId = DetectAgent(request),
                                    StatusCode = 413,
                                    ResponseJson = JsonConvert.SerializeObject(new {
                                        error = "Request body too large",
                                        maxSizeBytes = MaxBodySizeBytes,
                                        receivedBytes = request.ContentLength64
                                    }, _jsonSettings),
                                    IsProcessed = true,
                                    CompletionSignal = rejectSignal
                                };
                                ThreadPool.QueueUserWorkItem(_ => WaitAndRespond(rejectJob));
                                rejectSignal = null; // Ownership transferred
                            }
                            finally
                            {
                                rejectSignal?.Dispose();
                            }
                            continue;
                        }

                        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                    }
                    
                    ManualResetEventSlim signal = null;
                    try
                    {
                        signal = new ManualResetEventSlim(false);

                        // Create job with raw data only - use DateTime (thread-safe) instead of Unity time
                        var job = new RequestJob
                        {
                            Context = context,
                            HttpMethod = request.HttpMethod,
                            Path = request.Url.AbsolutePath,
                            Body = body,
                            EnqueueTimeTicks = DateTime.UtcNow.Ticks,
                            RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                            AgentId = DetectAgent(request),
                            StatusCode = 200,
                            ResponseJson = null,
                            IsProcessed = false,
                            CompletionSignal = signal
                        };

                        Interlocked.Increment(ref _totalRequestsReceived);

                        // Enqueue for main thread processing
                        lock (_queueLock)
                        {
                            _jobQueue.Enqueue(job);
                        }

                        // Wait for main thread to process (with timeout)
                        // This is thread-safe - just waiting on a signal
                        ThreadPool.QueueUserWorkItem(_ => WaitAndRespond(job));
                        signal = null; // Ownership transferred to WaitAndRespond
                    }
                    finally
                    {
                        signal?.Dispose(); // Only disposes if WaitAndRespond was never queued
                    }
                }
                catch (HttpListenerException) { /* Expected when stopping */ }
                catch (ObjectDisposedException) { /* Expected when stopping */ }
                catch (Exception)
                {
                    // Can't use Debug.Log here (not main thread safe for logging)
                    // Errors will be visible if Unity console is monitoring
                }
            }
        }
        
        /// <summary>
        /// Waits for job completion and sends HTTP response.
        /// Runs on ThreadPool thread - NO Unity API calls.
        /// </summary>
        private static void WaitAndRespond(RequestJob job)
        {
            try
            {
                // Wait for main thread to process (with timeout)
                bool completed = job.CompletionSignal.Wait(RequestTimeoutMs);
                
                if (!completed)
                {
                    job.StatusCode = 504;
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = $"Gateway Timeout: Main thread did not respond within {RequestTimeoutMs / 1000} seconds",
                        suggestion = "Unity Editor may be paused or showing a modal dialog"
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
                job.CompletionSignal?.Dispose();
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
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Agent-Id");
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
            
            // Heartbeat for Registry
            if (_isRunning)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastHeartbeatTime > HeartbeatInterval)
                {
                    _lastHeartbeatTime = now;
                    RegistryService.Heartbeat(_port);
                }
            }
        }

        /// <summary>
        /// Processes a single job. Runs on MAIN THREAD - all Unity API safe.
        /// </summary>
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
                    domainReloadRecovery = "enabled",
                    architecture = "Producer-Consumer (Thread-Safe)",
                    note = "If you get 'Connection Refused', Unity may be reloading scripts. Wait 2-3 seconds and retry."
                }, _jsonSettings);
                return;
            }
            
            // Get skills manifest
            if (path == "/skills" && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetManifest();
                return;
            }
            
            // Execute skill
            if (path.StartsWith("/skill/") && job.HttpMethod == "POST")
            {
                // Rate limiting (now safe - on main thread)
                if (!CheckRateLimit())
                {
                    job.StatusCode = 429;
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = "Rate limit exceeded",
                        limit = MaxRequestsPerSecond,
                        suggestion = "Please slow down requests"
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
                
                // Execute skill (safe - on main thread)
                try
                {
                    job.StatusCode = 200;
                    job.ResponseJson = SkillRouter.Execute(skillName, job.Body);
                    SkillsLogger.LogAgent(job.AgentId, skillName);
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
                endpoints = new[] { "GET /skills", "POST /skill/{name}", "GET /health" }
            }, _jsonSettings);
        }

        /// <summary>
        /// Rate limiting check. MUST be called from main thread only.
        /// Uses DateTime for consistent timing (NOT EditorApplication.timeSinceStartup).
        /// </summary>
        private static bool CheckRateLimit()
        {
            long now = DateTime.UtcNow.Ticks;

            if (now - _lastRateLimitResetTicks >= TimeSpan.TicksPerSecond)
            {
                _requestsThisSecond = 0;
                _lastRateLimitResetTicks = now;
            }

            _requestsThisSecond++;
            return _requestsThisSecond <= MaxRequestsPerSecond;
        }

        private static void RunSelfTest()
        {
            if (!_isRunning) return;
            int port = _port;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var hosts = new[] { "localhost", "127.0.0.1" };
                foreach (var host in hosts)
                {
                    string url = $"http://{host}:{port}/health";
                    try
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.Timeout = 3000;
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            if (resp.StatusCode == HttpStatusCode.OK)
                                SkillsLogger.LogSuccess($"[Self-Test] {url} -> OK");
                            else
                                SkillsLogger.LogWarning($"[Self-Test] {url} -> HTTP {(int)resp.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogError($"[Self-Test] {url} -> FAILED: {ex.Message}");
                        SkillsLogger.LogError($"[Self-Test] Check firewall/antivirus settings.");
                    }
                }
            });
        }
    }
}

