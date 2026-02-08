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
        // Log prefix constants with colors (Unity rich text)
        private const string LOG_PREFIX = "<color=#4A9EFF>[UnitySkills]</color>";
        private const string LOG_SUCCESS = "<color=#5EE05E>[UnitySkills]</color>";
        private const string LOG_WARNING = "<color=#FFB347>[UnitySkills]</color>";
        private const string LOG_ERROR = "<color=#FF6B6B>[UnitySkills]</color>";
        private const string LOG_SERVER = "<color=#9B7EFF>[UnitySkills]</color>";
        private const string LOG_SKILL = "<color=#5EC8E0>[UnitySkills]</color>";
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
        private static double _lastRateLimitReset = 0;
        private const int MaxRequestsPerSecond = 100;
        
        // Keep-alive interval (ms)
        private const int KeepAliveIntervalMs = 50;
        // Heartbeat interval for registry (ms)
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
            
            // Result (set by Main thread)
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public ManualResetEventSlim CompletionSignal;
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
                Debug.Log($"{LOG_PREFIX} Domain Reload detected - server state saved (port {_port}), will auto-restart");
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
                Debug.Log($"{LOG_PREFIX} Compilation started - preparing for Domain Reload...");
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
                    Debug.Log($"{LOG_PREFIX} Auto-restoring server after Domain Reload...");
                    Start();
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

        public static void Start()
        {
            if (_isRunning)
            {
                Debug.Log($"{LOG_PREFIX} Server already running at {_prefix}");
                return;
            }

            try
            {
                HookUpdateLoop();

                // Port Hunting: 8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;

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

                if (!started)
                {
                    Debug.LogError($"{LOG_ERROR} Failed to find open port between {startPort} and {endPort}");
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
                Debug.Log($"{LOG_SERVER} REST Server started at <b>{_prefix}</b>");
                Debug.Log($"{LOG_SUCCESS} {skillCount} skills loaded | Instance: <b>{RegistryService.InstanceId}</b>");
                Debug.Log($"{LOG_PREFIX} Domain Reload Recovery: <color=#5EE05E>ENABLED</color> (AutoStart={AutoStart})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LOG_ERROR} Failed to start: {ex.Message}");
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

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

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
                Debug.Log($"{LOG_SERVER} Server stopped (permanent)");
            else
                Debug.Log($"{LOG_PREFIX} Server stopped (will auto-restart after reload)");
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
                        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                    }
                    
                    // Create job with raw data only - use DateTime (thread-safe) instead of Unity time
                    var job = new RequestJob
                    {
                        Context = context,
                        HttpMethod = request.HttpMethod,
                        Path = request.Url.AbsolutePath,
                        Body = body,
                        EnqueueTimeTicks = DateTime.UtcNow.Ticks,
                        StatusCode = 200,
                        ResponseJson = null,
                        IsProcessed = false,
                        CompletionSignal = new ManualResetEventSlim(false)
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
                // Wait up to 60 seconds for main thread to process
                bool completed = job.CompletionSignal.Wait(60000);
                
                if (!completed)
                {
                    job.StatusCode = 504;
                    job.ResponseJson = JsonConvert.SerializeObject(new {
                        error = "Gateway Timeout: Main thread did not respond within 60 seconds",
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
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                response.StatusCode = job.StatusCode;
                
                if (!string.IsNullOrEmpty(job.ResponseJson))
                {
                    response.ContentType = "application/json; charset=utf-8";
                    byte[] buffer = Encoding.UTF8.GetBytes(job.ResponseJson);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }
            catch { /* Ignore write errors */ }
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
                    Debug.LogWarning($"{LOG_WARNING} Job processing error: {ex.Message}");
                }
                finally
                {
                    job.IsProcessed = true;
                    job.CompletionSignal?.Set();
                    Interlocked.Increment(ref _totalRequestsProcessed);
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
                    version = "1.4.2",
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
                
                // Extract skill name (preserve original case)
                string skillName = job.Path.Substring(7);
                
                // Execute skill (safe - on main thread)
                try
                {
                    job.StatusCode = 200;
                    job.ResponseJson = SkillRouter.Execute(skillName, job.Body);
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
                    Debug.LogWarning($"{LOG_SKILL} Skill '<b>{skillName}</b>' error: {ex.Message}");
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
            // Use DateTime for thread-safe timing
            double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
            
            if (now - _lastRateLimitReset >= 1.0)
            {
                _requestsThisSecond = 0;
                _lastRateLimitReset = now;
            }
            
            _requestsThisSecond++;
            return _requestsThisSecond <= MaxRequestsPerSecond;
        }
    }
}

