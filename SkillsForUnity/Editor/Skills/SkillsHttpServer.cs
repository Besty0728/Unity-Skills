using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Embedded HTTP server for UnitySkills REST API.
    /// Supports high-frequency calls and background execution when Unity is not focused.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsHttpServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static Thread _keepAliveThread;
        private static bool _isRunning;
        private static string _prefix = "http://localhost:8090/";
        
        // Request queue for main thread execution
        private static readonly Queue<PendingRequest> _requestQueue = new Queue<PendingRequest>();
        private static readonly object _queueLock = new object();
        private static bool _updateHooked = false;
        
        // Rate limiting
        private static int _requestsThisSecond = 0;
        private static double _lastRateLimitReset = 0;
        private const int MaxRequestsPerSecond = 100;
        
        // Keep-alive interval (ms) - forces Unity to process requests even when not focused
        private const int KeepAliveIntervalMs = 50;

        public static bool IsRunning => _isRunning;
        public static string Url => _prefix;
        public static int QueuedRequests { get { lock (_queueLock) { return _requestQueue.Count; } } }

        private class PendingRequest
        {
            public string SkillName;
            public string Args;
            public ManualResetEvent WaitHandle;
            public string Result;
            public double QueueTime;
        }

        static SkillsHttpServer()
        {
            EditorApplication.quitting += Stop;
            // Hook into update loop immediately for background execution support
            HookUpdateLoop();
        }
        
        private static void HookUpdateLoop()
        {
            if (_updateHooked) return;
            EditorApplication.update += ProcessRequestQueue;
            _updateHooked = true;
        }
        
        private static void UnhookUpdateLoop()
        {
            if (!_updateHooked) return;
            EditorApplication.update -= ProcessRequestQueue;
            _updateHooked = false;
        }

        public static void Start()
        {
            if (_isRunning)
            {
                Debug.Log("[UnitySkills] Server already running at " + _prefix);
                return;
            }

            try
            {
                // Ensure update hook is active
                HookUpdateLoop();
                
                _listener = new HttpListener();
                _listener.Prefixes.Add(_prefix);
                _listener.Start();
                _isRunning = true;

                // Start listener thread
                _listenerThread = new Thread(ListenLoop) { IsBackground = true };
                _listenerThread.Start();
                
                // Start keep-alive thread to ensure Unity processes requests when not focused
                _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true };
                _keepAliveThread.Start();

                Debug.Log($"[UnitySkills] REST Server started at {_prefix}");
                Debug.Log($"[UnitySkills] {SkillRouter.GetManifest().Split('\n').Length} skills available");
                Debug.Log($"[UnitySkills] Background execution: ENABLED (works when Unity is not focused)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnitySkills] Failed to start: {ex.Message}");
                _isRunning = false;
            }
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _listener?.Stop();
            _listener?.Close();
            
            // Clear pending requests
            lock (_queueLock)
            {
                while (_requestQueue.Count > 0)
                {
                    var req = _requestQueue.Dequeue();
                    req.Result = JsonConvert.SerializeObject(new { error = "Server stopped" });
                    req.WaitHandle.Set();
                }
            }
            
            Debug.Log("[UnitySkills] Server stopped");
        }
        
        /// <summary>
        /// Keep-alive loop that forces Unity Editor to update even when not focused.
        /// This ensures requests are processed in the background.
        /// </summary>
        private static void KeepAliveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    Thread.Sleep(KeepAliveIntervalMs);
                    
                    // Only force repaint if there are pending requests
                    bool hasPendingRequests;
                    lock (_queueLock)
                    {
                        hasPendingRequests = _requestQueue.Count > 0;
                    }
                    
                    if (hasPendingRequests)
                    {
                        // Force Unity to update even when not focused
                        // This is thread-safe and will trigger ProcessRequestQueue on the main thread
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Ignore errors in keep-alive loop
                }
            }
        }

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) { /* Expected when stopping */ }
                catch (ObjectDisposedException) { /* Expected when stopping */ }
                catch (Exception ex)
                {
                    if (_isRunning) Debug.LogError($"[UnitySkills] Listen error: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Process queued requests on the main thread.
        /// This runs via EditorApplication.update which works even when Unity is not focused
        /// (when triggered by QueuePlayerLoopUpdate from keep-alive thread).
        /// </summary>
        private static void ProcessRequestQueue()
        {
            // Process multiple requests per frame for high-frequency support
            int processed = 0;
            const int maxPerFrame = 10;
            
            while (processed < maxPerFrame)
            {
                PendingRequest request = null;
                
                lock (_queueLock)
                {
                    if (_requestQueue.Count > 0)
                    {
                        request = _requestQueue.Dequeue();
                    }
                }
                
                if (request == null) break;
                
                try
                {
                    request.Result = SkillRouter.Execute(request.SkillName, request.Args);
                }
                catch (Exception ex)
                {
                    request.Result = JsonConvert.SerializeObject(new {
                        error = ex.Message,
                        type = ex.GetType().Name,
                        skill = request.SkillName
                    });
                    Debug.LogWarning($"[UnitySkills] Skill '{request.SkillName}' error: {ex.Message}");
                }
                finally
                {
                    request.WaitHandle.Set();
                }
                
                processed++;
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            HttpListenerResponse response = null;
            string result = null;

            try
            {
                var request = context.Request;
                response = context.Response;

                // Set CORS headers early
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 204;
                    SafeCloseResponse(response);
                    return;
                }

                var path = request.Url.AbsolutePath.ToLower();

                if (path == "/skills" && request.HttpMethod == "GET")
                {
                    result = SkillRouter.GetManifest();
                }
                else if (path.StartsWith("/skill/") && request.HttpMethod == "POST")
                {
                    // Rate limiting check
                    if (!CheckRateLimit())
                    {
                        response.StatusCode = 429;
                        result = JsonConvert.SerializeObject(new { 
                            error = "Rate limit exceeded", 
                            limit = MaxRequestsPerSecond,
                            suggestion = "Please slow down requests"
                        });
                    }
                    else
                    {
                        var skillName = request.Url.AbsolutePath.Substring(7); // Keep original case
                        string body;
                        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                        result = ExecuteOnMainThread(skillName, body);
                    }
                }
                else if (path == "/" || path == "/health")
                {
                    try { SkillRouter.Initialize(); } catch { /* Ignore init errors on health check */ }
                    result = JsonConvert.SerializeObject(new { 
                        status = "ok", 
                        service = "UnitySkills", 
                        version = "1.3.0",
                        serverRunning = _isRunning,
                        queuedRequests = QueuedRequests,
                        backgroundExecution = true
                    });
                }
                else
                {
                    response.StatusCode = 404;
                    result = JsonConvert.SerializeObject(new { error = "Not found", endpoints = new[] { "GET /skills", "POST /skill/{name}", "GET /health" } });
                }
            }
            catch (Exception ex)
            {
                // Catch-all to prevent server crash
                try
                {
                    if (response != null)
                        response.StatusCode = 500;
                    result = JsonConvert.SerializeObject(new { error = ex.Message, type = ex.GetType().Name });
                    Debug.LogWarning($"[UnitySkills] Request error: {ex.Message}");
                }
                catch { /* Ignore errors during error handling */ }
            }

            // Send response safely
            try
            {
                if (response != null && result != null)
                {
                    response.ContentType = "application/json";
                    var buffer = Encoding.UTF8.GetBytes(result);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Response write error: {ex.Message}");
            }
            finally
            {
                SafeCloseResponse(response);
            }
        }
        
        private static bool CheckRateLimit()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRateLimitReset >= 1.0)
            {
                _requestsThisSecond = 0;
                _lastRateLimitReset = now;
            }
            
            _requestsThisSecond++;
            return _requestsThisSecond <= MaxRequestsPerSecond;
        }
        
        private static void SafeCloseResponse(HttpListenerResponse response)
        {
            try { response?.Close(); } catch { /* Ignore close errors */ }
        }

        private static string ExecuteOnMainThread(string skillName, string args)
        {
            var request = new PendingRequest
            {
                SkillName = skillName,
                Args = args,
                WaitHandle = new ManualResetEvent(false),
                Result = null,
                QueueTime = EditorApplication.timeSinceStartup
            };
            
            // Add to queue
            lock (_queueLock)
            {
                _requestQueue.Enqueue(request);
            }

            // Wait with timeout (60s for long operations)
            // The keep-alive thread will force Unity to process even when not focused
            bool completed = request.WaitHandle.WaitOne(60000);
            
            if (!completed)
            {
                Debug.LogWarning($"[UnitySkills] Skill '{skillName}' timed out after 60 seconds. Unity might be paused or busy.");
                return JsonConvert.SerializeObject(new { 
                    error = "Timeout: Operation took longer than 60 seconds", 
                    skill = skillName,
                    suggestion = "Ensure Unity Editor is responsive. The editor might be in play mode pause or showing a modal dialog.",
                    queuedRequests = QueuedRequests
                });
            }
            
            return request.Result ?? JsonConvert.SerializeObject(new { error = "Unknown error", skill = skillName });
        }
    }
}
