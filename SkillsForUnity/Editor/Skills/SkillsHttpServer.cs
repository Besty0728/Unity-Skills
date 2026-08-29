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
    /// Production-grade HTTP server for the UnitySkills REST API.
    ///
    /// Architecture: strict producer-consumer model
    /// - HTTP thread (producer): only responsible for receiving requests and enqueuing them, never calls any Unity API.
    /// - Main thread (consumer): handles all logic, including routing, rate limiting, and skill execution.
    ///
    /// Resilience capabilities:
    /// - Automatically restarts after a domain reload (script compilation)
    /// - Persists state via EditorPrefs
    /// - Graceful shutdown and recovery
    ///
    /// This is what achieves 100% thread safety with Unity's single-threaded architecture.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsHttpServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static Thread _keepAliveThread;
        private static volatile bool _isRunning;
        // volatile: read by the /health fast path on the HTTP thread.
        private static volatile int _port = 8090;
        private static readonly string _prefixBase = "http://localhost:";
        private static string _prefix = $"{_prefixBase}{_port}/";

        // Job queue — HTTP thread enqueues, main thread dequeues and processes.
        //
        // Two lanes, each strictly FIFO internally:
        // - light: read-only, millisecond-scale endpoints (liveness probe / progress polling), drained fully each
        //   frame regardless of the frame budget, so /health or /jobs/{id} polling never queues behind a slow skill.
        // - heavy: everything that executes a skill, builds the reflection cache, or writes state; bound by a per-frame count cap and a millisecond budget.
        //
        // Ordering across lanes is deliberately not guaranteed (the whole point of the split); callers needing order
        // must wait for the response before sending the next request — exactly what the Python client already does.
        //
        // ConcurrentQueue instead of Queue+lock: the only place relying on a lock for atomicity is Stop()'s drain,
        // which runs after the listener thread is joined, when there can no longer be concurrent producers.
        private static readonly ConcurrentQueue<RequestJob> _lightQueue = new ConcurrentQueue<RequestJob>();
        private static readonly ConcurrentQueue<RequestJob> _heavyQueue = new ConcurrentQueue<RequestJob>();
        // Mirror queue depth with Interlocked counters: ConcurrentQueue.Count requires walking segments,
        // and admission control plus /health need to read the depth on every incoming request.
        private static int _lightQueued = 0;
        private static int _heavyQueued = 0;
        private static bool _updateHooked = false;
        private static int _pendingRequests = 0;

        // Two gates on the heavy lane, checked before starting each job: a count cap bounds burstiness, a millisecond
        // budget bounds per-frame duration. A single skill can exceed the budget — it can't interrupt a running skill,
        // only refuse to start the next one — which is why the editor can still repaint under a long queue.
        private const int MaxHeavyJobsPerFrame = 20;
        private const double HeavyFrameBudgetSeconds = 0.012;

        private const int MaxRequestsPerSecond = 100;
        private const int MaxQueuedRequests = 200;
        private const int MaxPendingRequests = 300;
        private static readonly ConcurrentBag<RequestJob> _requestJobPool = new ConcurrentBag<RequestJob>();
        private static int _poolSize;

        // Admission-rate-limit on the listener thread, to keep the queue and threads from blowing up.
        private static int _admittedThisSecond = 0;
        private static long _lastAdmissionResetTicks = 0;
        
        // Keep-alive polling interval (ms) for checking pending jobs.
        private const int KeepAlivePollingMs = 50;

        // Interval for unconditionally waking the main thread; configurable.
        private const string PrefKeyKeepAliveInterval = "UnitySkills_KeepAliveIntervalSeconds";

        // Thread-safe cached copy of KeepAliveIntervalSeconds (EditorPrefs can only be read on the main thread)
        private static long _cachedKeepAliveIntervalTicks = 10L * TimeSpan.TicksPerSecond;

        /// <summary>
        /// Interval (seconds) at which the keep-alive thread forcibly wakes the main thread, even when there are no
        /// pending jobs. Keeps the watchdog and heartbeat running while Unity is unfocused. Defaults to 10 seconds, minimum 1 second.
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
        // Request processing timeout — cached for thread safety (EditorPrefs can only be read on the main thread)
        private static int _cachedTimeoutMs = 15 * 60 * 1000;
        private static int RequestTimeoutMs => _cachedTimeoutMs;
        internal static void RefreshTimeoutCache() => _cachedTimeoutMs = RequestTimeoutMinutes * 60 * 1000;
        private const int MaxBodySizeBytes = 10 * 1024 * 1024; // 10MB
        // Registry heartbeat interval (seconds)
        private const double HeartbeatInterval = 30.0;
        private static double _lastHeartbeatTime = 0;

        // Watchdog: periodically confirms the listener thread is alive, restarts it if not
        private const double WatchdogInterval = 15.0;
        private static double _lastWatchdogCheck = 0;

        // Fallback: recovers the server after a domain reload if delayCall never fires
        private const double SafetyNetInterval = 5.0;
        private static double _lastSafetyNetCheck = 0;

        // KeepAlive: unconditional wake interval (ticks; 5 seconds = 50_000_000 ticks)
        private static long _lastForceWakeTicks = 0;

        // Statistics
        private static long _totalRequestsProcessed = 0;
        private static long _totalRequestsReceived = 0;

        // Startup diagnostics: counts ProcessJobQueue ticks since Start() for self-check use
        private static volatile int _pjqTicksSinceStart = -1;

        // ===== Main-thread liveness mirror + /health snapshot =====
        //
        // Everything in this block is only ever written on the main thread, read by SendHealthFastPath on the HTTP
        // listener thread. It exists so GET /health can answer without going through the job queue: previously probes
        // got stuck behind a long-running skill, making "server is dead" and "Unity is busy" look identical to clients.

        // DateTime.UtcNow.Ticks from the most recent ProcessJobQueue frame. C# doesn't allow `volatile long`,
        // so it's accessed via Interlocked — which is likewise atomic on 32-bit builds.
        private static long _mainThreadTickUtc = 0;

        // Values that need to read Unity API / EditorPrefs, mirrored into plain static fields.
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
        // Before the first full refresh has landed, the fast path always bails out and /health falls back to the
        // main-thread queue instead of reporting placeholder values.
        private static volatile bool _snapReady;
        // Set from any thread by the SkillsModeManager.OnChanged / SkillsSurfaceProfile.OnChanged hooks, consumed on
        // the next main-thread frame. A flag rather than an in-place refresh is used so that every Unity API read
        // inside RefreshHealthSnapshot stays on the main thread, regardless of which thread raised the event.
        private static volatile bool _healthSnapshotDirty = true;
        private static bool _modeHookInstalled = false;

        // Floor on refreshing the "expensive half" of the snapshot when there's no OnChanged event at all. Catches
        // drift that events can't see: an expired grant TTL, or prefs changed directly, bypassing the manager.
        private const double HealthSnapshotInterval = 1.0;
        private static double _lastHealthSnapshot = 0;

        // ===== gzip response body cache (HTTP thread) =====
        //
        // Only used for GET /skills and GET /skills/schema — the only two response bodies large enough to be worth
        // compressing (summary ~143KB, full schema ~618KB). Keyed by ETag, a content hash, so entries self-invalidate:
        // content changes the key, and the old key is never requested again. Compression is pure CPU, never touches
        // the Unity API, so it's legitimate on the HTTP thread; the 618KB pass takes tens of ms, only on a cache miss.
        private const int GzipMinBytes = 4096;
        private const int MaxGzipCacheEntries = 32;
        private const long MaxGzipCacheBytes = 8L * 1024 * 1024;
        private static readonly ConcurrentDictionary<string, byte[]> _gzipCache =
            new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly object _gzipCacheLock = new object();
        private static long _gzipCacheBytes = 0;

        // Reuse SkillsCommon's JSON settings (single definition, no duplication)
        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;
        
        // Persistence key for domain-reload recovery (project-level scope) — lazily cached
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

        // Domain reload tracking
        // volatile: read by the HTTP thread (/health fast path) and by ThreadPool responders (timeout diagnostics),
        // only ever written on the main thread.
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
        /// Whether the server auto-starts. When true, it automatically restarts after a domain reload.
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
        /// Preferred server port. 0 = automatic (scans 8090-8100), otherwise uses the specified port.
        /// </summary>
        public static int PreferredPort
        {
            get => EditorPrefs.GetInt(PrefKeyPreferredPort, 0);
            set => EditorPrefs.SetInt(PrefKeyPreferredPort, value);
        }

        private const string PrefKeyRequestTimeout = "UnitySkills_RequestTimeoutMinutes";

        /// <summary>
        /// Request timeout (minutes). Defaults to 15 minutes, minimum 1 minute.
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
        /// A pending HTTP request job. Created by the HTTP thread, processed by the main thread.
        /// </summary>
        private class RequestJob
        {
            // Raw HTTP data (written by the HTTP thread)
            public HttpListenerContext Context;
            public string HttpMethod;
            public string Path;
            public string Body;
            public long EnqueueTimeTicks;
            public string RequestId;
            public string AgentId;
            public string QueryString;
            // Headers for conditional GET / content negotiation. A pure string read, so grabbed by the HTTP thread at enqueue time.
            public string IfNoneMatch;
            public string AcceptEncoding;

            // Processing result (written by the main thread)
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public int PoolReturned;
            // Content hash of ResponseJson for the two cacheable GET endpoints, null for every other endpoint.
            // It also doubles as both the ETag header and the gzip cache key.
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
                // Note: PoolReturned is maintained by ReturnRequestJob/Prepare, not managed by Reset
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
        /// Best-effort close for a context that the accept loop never handed off to a responder.
        /// Closing an already-closed response is a no-op; not closing it leaks the socket until the editor process exits.
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
        /// Lane classification for the two-lane queue. "light" = "read-only and millisecond-scale" — the liveness/progress
        /// polling an agent loops on during a long skill. Everything else is "heavy": execute, write state, build the reflection cache, or unbounded disk I/O.
        ///
        /// Every handler here has been individually verified; don't add an endpoint before re-reading its handler.
        /// - OPTIONS — straight to 204, has no handler at all.
        /// - GET /health, GET / — only the ?live=1 probe goes through the queue (everything else is answered on the
        ///   HTTP thread); that handler reads EditorPrefs and two compile flags.
        /// - GET /compile/status — two EditorApplication flags plus a cached SessionState string.
        /// - GET /jobs, /jobs/{id}[/logs|/progress] — BatchPersistence.ListJobs / GetJob just projects an
        ///   already-loaded in-memory list; the GET handler never reaches any write path.
        /// - GET /permission/status — reads mode, allowlist, and pending grants. The PendingGrantRequests getter
        ///   lazily sweeps expired grants, which is the only write in this lane: bounded by MaxLiveGrants, never
        ///   touches Unity, and only reclaims the caller's own expired tokens.
        ///
        /// The following are read-only but deliberately classified as heavy:
        /// - GET /analytics — has to aggregate telemetry JSONL from disk. Each window is cached for 30 seconds, but
        ///   the first call for a given window is unbounded I/O, which fails the "millisecond-scale" half of the test.
        /// - GET /skills/recommend — calls SkillRouter.Initialize() (a full reflection scan on a cold domain), then
        ///   scores every skill.
        /// - GET /skills, /skills/schema — anything that actually reaches the queue is, by definition, a cache
        ///   miss — i.e. exactly the request that has to build a several-hundred-KB manifest.
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
        /// Dequeues a job and keeps the mirrored depth counter in sync. Only decrements on a successful take, so a
        /// race with a concurrent producer that has already incremented but not yet enqueued leaves the count unchanged.
        /// </summary>
        private static bool TryDequeueJob(ConcurrentQueue<RequestJob> queue, ref int counter, out RequestJob job)
        {
            if (!queue.TryDequeue(out job))
                return false;

            Interlocked.Decrement(ref counter);
            return true;
        }

        /// <summary>
        /// Fails every job still queued on a lane with 503 SERVER_STOPPED, releasing its waiting responder. Safe to call
        /// without extra synchronization only because Stop() runs it after the listener thread is joined, when no producer can still be enqueuing.
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
        /// Parses an already-serialized error JSON string back into a JObject, so it can be emitted through
        /// SendImmediateJsonResponse without being double-encoded.
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
        /// Fast-path responder for cached GET /skills and /skills/schema. Runs on the HTTP listener thread — must
        /// never touch the Unity API or SkillsLogger (only headers, hashing, compression, socket writes). Attaches an
        /// ETag header, answers If-None-Match with an empty-body 304, and serves the cached gzip body when asked.
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
                // The same URL now has two possible response bodies (identity / gzip); without Vary,
                // an intermediate proxy might hand the gzip body to a client that never asked for compression.
                response.Headers.Add("Vary", "Accept-Encoding");

                // 304 is decided before compression: unchanged content should cost zero bytes and zero CPU, not a wasted gzip pass.
                if (IfNoneMatchSatisfied(request.Headers["If-None-Match"], etag))
                {
                    response.StatusCode = 304; // Not Modified — must not carry a response body
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
        /// Writes the body as gzip when the client supports it and a compressed body is available, else plain UTF-8.
        /// Shared by the HTTP-thread fast path and main-thread slow path, so content negotiation is identical between
        /// them. The caller must have already set the status code and content type.
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
        /// Returns true if the client lists gzip (or "*") in Accept-Encoding and hasn't disabled it with q=0.
        /// Deliberately kept minimal — it only gates two endpoints, and real clients (requests, curl, browsers)
        /// all just send plain "gzip, deflate".
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
                        continue; // explicitly rejected — keep scanning the next token
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the gzip body of <paramref name="json"/>, compressing and caching it on first use.
        /// Returns null (meaning "send as-is, uncompressed") when: the size is below <see cref="GzipMinBytes"/>,
        /// gzip fails to shrink the content, or on any failure — compression must never make a request fail.
        ///
        /// Pure CPU and string operations, safe on the HTTP thread. See the cache declaration for key/eviction rationale.
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
                    return null; // the overhead of one extra response header would exceed what compression saves

                using (var ms = new System.IO.MemoryStream(raw.Length / 4 + 256))
                {
                    using (var gz = new System.IO.Compression.GZipStream(
                        ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                    {
                        gz.Write(raw, 0, raw.Length);
                    }
                    // Must wait for the inner using to flush the gzip trailer before reading the length.
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
        /// Lenient If-None-Match comparison: tolerates quoted values, the W/ weak prefix, comma lists, and '*' wildcard.
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
        /// The part of the /health payload that comes from the Unity API or EditorPrefs.
        /// Two producers, one shape: <see cref="FromSnapshot"/> (HTTP thread, reads mirrored static fields) and
        /// <see cref="FromLive"/> (main thread, reads live). Sharing one struct and one builder is what keeps the
        /// fast path and <c>?live=1</c> from drifting into two different response shapes.
        /// </summary>
        private struct HealthVitals
        {
            public string UnityVersion;
            public string InstanceId;
            public string ProjectName;
            public string CurrentMode;
            public bool PanelApprovalRequired;
            // Wire value of the user-exposed surface tier ("full" / "guide" / "noSceneAuthoring").
            // The deprecated guideMode boolean is derived from this in BuildHealthJson rather than mirrored
            // separately, so the two can never disagree.
            public string SurfaceProfile;
            public int PendingCount;
            public int AllowlistCount;
            public bool AutoRestart;
            public int RequestTimeoutMinutes;
            public bool IsCompiling;
            public bool IsUpdating;

            /// <summary>HTTP-thread safe: reads only plain static fields, zero Unity API.</summary>
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

            /// <summary>Main thread only — reads the Unity API, EditorPrefs, and permission sets.</summary>
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
                    // Fully qualified: inside this nested type, the fields below would shadow the outer class's same-named members.
                    AutoRestart = SkillsHttpServer.AutoStart,
                    RequestTimeoutMinutes = SkillsHttpServer.RequestTimeoutMinutes,
                    IsCompiling = EditorApplication.isCompiling,
                    IsUpdating = EditorApplication.isUpdating,
                };
            }
        }

        /// <summary>
        /// Main thread only. Mirrors <see cref="HealthVitals"/> into the static fields read by the HTTP thread's /health path.
        ///
        /// full=false is the per-frame path, touching only two compilation flags — cheap reads, and the only metrics
        /// that genuinely change frame to frame. full=true also re-reads EditorPrefs and the permission sets
        /// (AllowlistSkills sorts/copies, PendingGrantRequests sweeps expired entries) — too wasteful at frame rate.
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
                // A stale snapshot is strictly better than breaking the editor's update loop; the next frame retries.
                // Either way, mainThreadIdleMs still reports a truthful value.
                SkillsLogger.LogVerbose($"Health snapshot refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Marks the "expensive half" of the health snapshot as needing a refresh on the next main-thread frame.
        /// Hooked onto <see cref="SkillsModeManager.OnChanged"/> and <see cref="SkillsSurfaceProfile.OnChanged"/>,
        /// so changes to mode / grants / allowlist / surface profile are reflected in /health immediately, instead
        /// of waiting out <see cref="HealthSnapshotInterval"/>.
        /// Setting a volatile flag (rather than refreshing in place) guarantees that no matter which thread raised
        /// the event, every Unity API read stays on the main thread.
        /// </summary>
        private static void OnPermissionStateChanged() => _healthSnapshotDirty = true;

        /// <summary>
        /// Serializes the /health payload. The caller supplies vitals; everything else is a plain static read safe on
        /// any thread, which is why this one method backs both the HTTP-thread fast path and the main thread's <c>?live=1</c> path.
        /// </summary>
        private static string BuildHealthJson(HealthVitals v, bool live)
        {
            long tick = Interlocked.Read(ref _mainThreadTickUtc);
            long idleMs = tick == 0
                ? -1L // the update loop hasn't ticked yet — age is unknown here, not zero
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
                // Deprecated alias for allowlistCount, kept for backward compatibility
                // (mirrors the `granted` / `counts.granted` aliases on /permission/status).
                // Can be removed in some future major version once external consumers have migrated.
                grantedCount = allowlistCount,
                // The slice of the skill surface currently exposed to the user. Authoritative: the agent cannot
                // change it, and any skill it hides answers SURFACE_EXCLUDED at execution time.
                surfaceProfile = v.SurfaceProfile,
                // Deprecated alias for surfaceProfile == "guide", kept for pre-2.7 clients that only understand a
                // boolean switch. Such clients would read noSceneAuthoring as false, i.e. "nothing is hidden" —
                // which is exactly why the hint field below needs to spell out the tier explicitly.
                guideMode = isGuide,
                // Only carries text when the tier isn't full: there's nothing to say in the full tier, and an
                // unconditional "prefer manual steps" hint (which is what this field used to always say) would
                // push the agent away from automation the user has actually already enabled.
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

                // ---- 2.3 additions (purely incremental, doesn't change the semantics of any existing field) ----
                port = _port,
                // Milliseconds since the last EditorApplication.update tick reached us. This is exactly what makes the
                // fast-path /health worthwhile: the server answers instantly while still telling you if the main thread is stuck.
                // A single-digit value means the editor is idle and healthy; several seconds means "alive but Unity is busy"
                // (a long skill, a modal dialog, importing) rather than "the server is dead".
                mainThreadIdleMs = idleMs,
                // Requests admitted but not yet answered (queue depth plus in-flight responders); the MaxPendingRequests admission cap.
                pendingRequests = Volatile.Read(ref _pendingRequests),
                // Depth of each of the two job-queue lanes; light is drained every frame.
                lightQueued,
                heavyQueued,
                domainReloadPending = _domainReloadPending,
                // True when this session's workflow history failed to load: rollback data is degraded, and
                // library cleanup stays paused until the history is cleared.
                workflowRecoveryMode = WorkflowManager.IsHistoryRecoveryMode,
                // false = answered on the HTTP thread from a snapshot up to ~1 second old.
                // true  = answered after a live read on the main thread (GET /health?live=1).
                live,
                note = "If you get 'Connection Refused', Unity may be reloading scripts. Wait 2-3 seconds and retry."
            }, _jsonSettings);
        }

        /// <summary>
        /// HTTP-thread responder for GET /health and GET /. Every value comes from a plain static field or the
        /// main-thread snapshot — zero Unity API, zero EditorPrefs, zero SkillsLogger — same contract as SendCachedGetResponse.
        ///
        /// The point is staying diagnosable under load: on the old "everything through the main thread" path, one
        /// long-running skill could hang the liveness probe itself, so callers couldn't tell "server is dead" from
        /// "Unity is busy". Now it answers instantly and mainThreadIdleMs says which; use GET /health?live=1 for a strictly live value.
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
        /// Returns true for GET /health?live=1 (or live=true) — an explicit opt-in to fall back to the main-thread
        /// queue, where every field is read live rather than pulled from a snapshot up to ~1 second old.
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

        // Agent recognition table: keyword -> agent ID mapping
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
        /// Recognizes the AI agent from the User-Agent or X-Agent-Id header.
        /// </summary>
        private static string DetectAgent(HttpListenerRequest request)
        {
            // Priority 1: explicit X-Agent-Id header
            var explicitId = request.Headers["X-Agent-Id"];
            if (!string.IsNullOrEmpty(explicitId))
                return explicitId;

            // Priority 2: table lookup against User-Agent (using OrdinalIgnoreCase to avoid ToLowerInvariant's allocation)
            var ua = request.UserAgent ?? "";

            foreach (var (keyword, agentId) in _agentKeywords)
            {
                if (ua.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return agentId;
            }

            // Unrecognized
            return string.IsNullOrEmpty(ua) ? "Unknown" : $"Unknown({ua.Substring(0, Math.Min(20, ua.Length))})";
        }

        /// <summary>
        /// Static constructor — invoked after every domain reload. This is the key to auto-recovery after script compilation.
        /// </summary>
        static SkillsHttpServer()
        {
            try
            {
                // Register editor lifecycle events
                EditorApplication.quitting += OnEditorQuitting;
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
                CompilationPipeline.compilationStarted += OnCompilationStarted;

                HookUpdateLoop();

                // Decide whether to auto-restart after a domain reload; deferred so Unity is fully initialized by then
                EditorApplication.delayCall += () => ScheduleDelayedCall(1.0, CheckAndRestoreServer);

                // Must be read only after the delayCall is hooked: PrefKey() drags in RegistryService's static init, and an
                // exception here would be swallowed by the outer catch, silently taking the recovery hooks above down with it.
                _editorLaunchPending = !SessionState.GetBool(PrefKey("EditorLaunchHandled"), false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitySkills] SkillsHttpServer init failed: " + ex);
            }
        }

        /// <summary>
        /// Called before script compilation — saves state.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            _domainReloadPending = true;

            // Critical fix: only write true while the server is actually running.
            // When _isRunning=false (a previous restart failed), don't overwrite — preserve the existing true intent.
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
                RegistryService.Unregister(); // temporary unregister
                // Actively close the HttpListener to release the port immediately
                _isRunning = false;
                try { _listener?.Stop(); } catch { }
                try { _listener?.Close(); } catch { }
                // Wait for the threads to exit, to ensure the port is fully released
                try { _listenerThread?.Join(2000); } catch { }
                try { _keepAliveThread?.Join(100); } catch { }
            }
        }

        /// <summary>
        /// Called after script compilation — restores state.
        /// </summary>
        private static void OnAfterAssemblyReload()
        {
            _domainReloadPending = false;

            // Restore the statistics that were in place before the reload
            var savedTotal = EditorPrefs.GetString(PREF_TOTAL_PROCESSED, "0");
            if (long.TryParse(savedTotal, out long parsed))
            {
                _totalRequestsProcessed = parsed;
            }
            // CheckAndRestoreServer is invoked via delayCall
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
        /// Called when the editor quits — clean shutdown.
        /// </summary>
        private static void OnEditorQuitting()
        {
            // Always clear on quit — don't want the next Unity session to auto-start
            EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
            EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            Stop();
        }

        // Retry counter for CheckAndRestoreServer
        private static int _restoreRetryCount = 0;
        private static bool _editorLaunchPending;
        private static bool _cliColdStartPending;
        private const int MaxRestoreRetries = 3;
        private static readonly double[] RestoreRetryDelays = { 1.0, 2.0, 4.0 }; // unit: seconds

        internal enum AutoStartReason
        {
            None,
            DomainReload,
            EditorLaunch,
            CliColdStart
        }

        /// <summary>
        /// Decides whether the server should be restored after a domain reload. Invoked via EditorApplication.delayCall
        /// to ensure Unity is ready by then. Retries up to 3 times with increasing delays (1s, 2s, 4s) if Start() fails.
        /// </summary>
        private static void CheckAndRestoreServer()
        {
            bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
            // batchmode is excluded: headless pipelines like `unity test` / `run` / `build` also run [InitializeOnLoad],
            // and if they grabbed 8090-8100 and advertised a short-lived instance to the global registry, it would steer
            // clients' multi-instance discovery to a process about to exit. CLI cold start uses the GUI path, so this doesn't apply.
            bool editorLaunchRequested = _editorLaunchPending && StartOnEditorLaunch && !Application.isBatchMode;
            // Unity CLI cold start (--args -unityskills-coldstart + already bound): force one launch this session,
            // ignoring the AutoStart/shouldRun preference; subsequent Domain Reloads go through the normal recovery path.
            _cliColdStartPending |= UnityCliService.ConsumeColdStartRequest();
            if (_cliColdStartPending && _restoreRetryCount == 0)
                SkillsLogger.Log("Unity CLI cold start detected — auto-starting server.");

            var reason = GetAutoStartReason(shouldRun && AutoStart, editorLaunchRequested, _cliColdStartPending);
            if (reason != AutoStartReason.None && !_isRunning)
            {
                bool domainReload = reason == AutoStartReason.DomainReload;
                int failures = domainReload ? EditorPrefs.GetInt(PREF_CONSECUTIVE_FAILURES, 0) : 0;

                // Decay: reset the counter if the last failure was more than 5 minutes ago
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
                    // Must also be cleared here: otherwise a pending "editor launch" intent would survive this
                    // early return and fire on some later reload — bypassing the circuit breaker we just tripped.
                    CompletePendingAutoStart(reason);
                    return;
                }

                int lastPort = EditorPrefs.GetInt(PREF_LAST_PORT, 0);
                int restorePort = (lastPort >= 8090 && lastPort <= 8100) ? lastPort : PreferredPort;
                SkillsLogger.Log($"Auto-starting server ({reason}, port={restorePort}, attempt {_restoreRetryCount + 1}/{MaxRestoreRetries + 1})...");
                Start(restorePort, fallbackToAuto: true);

                if (_isRunning)
                {
                    // Start succeeded (failures was already reset to zero inside Start())
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
                    // All retries exhausted for this round
                    _restoreRetryCount = 0;
                    CompletePendingAutoStart(reason);
                    if (domainReload)
                    {
                        EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, failures + 1);
                        EditorPrefs.SetString(PrefKey("LastFailTime"), EditorApplication.timeSinceStartup.ToString());
                        // The domain-reload path keeps the failure count: the user needs to know how close they are to the
                        // MaxConsecutiveFailures cap, otherwise there's no way to see the circuit breaker approaching while debugging.
                        SkillsLogger.LogError(
                            $"[UnitySkills] Server failed to restart (consecutive failures: {failures + 1}/{MaxConsecutiveFailures}). " +
                            "Will retry on next Domain Reload. Manual start: Window > UnitySkills > Start Server");
                    }
                    else
                    {
                        // EditorLaunch / CliColdStart only attempts once per session, so there's no cross-session count to report.
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
        /// Uses EditorApplication.update polling to implement a callback delayed by a given number of seconds.
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
                // Cache the keep-alive interval, for thread-safe reads by the KeepAliveLoop thread
                _cachedKeepAliveIntervalTicks = (long)KeepAliveIntervalSeconds * TimeSpan.TicksPerSecond;

                // Port probing: 8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;

                // Try the preferred port first if a valid one was given
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
                    // Auto mode: scan ports one by one
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
                            // Port is taken, try the next one
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

                // Persist state, for use in domain-reload recovery
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, true);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0); // Started successfully, clear the failure count

                // Register with the global registry
                RegistryService.Register(_port);

                // Populate the /health snapshot before the listener starts accepting, so the first probe takes the fast path
                // instead of falling back to the queue. The Register() call above must run first — instanceId/projectName come from it.
                RefreshHealthSnapshot(full: true);
                if (!_modeHookInstalled)
                {
                    SkillsModeManager.OnChanged += OnPermissionStateChanged;
                    SkillsSurfaceProfile.OnChanged += OnPermissionStateChanged;
                    _modeHookInstalled = true;
                }

                // Start the listener thread (producer — only enqueues, never touches the Unity API)
                _listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "UnitySkills-Listener" };
                _listenerThread.Start();

                // Start the keep-alive thread (forces Unity to keep updating while unfocused)
                _keepAliveThread = new Thread(KeepAliveLoop) { IsBackground = true, Name = "UnitySkills-KeepAlive" };
                _keepAliveThread.Start();

                // These calls are safe here because Start() is called from the main thread
                var skillCount = SkillRouter.SkillCount;
                SkillsLogger.Log($"REST Server started at {_prefix}");
                SkillsLogger.Log($"{skillCount} skills loaded | Instance: {RegistryService.InstanceId}");
                SkillsLogger.LogVerbose($"Domain Reload Recovery: ENABLED (AutoStart={AutoStart})");

                // Initialize the heartbeat timer, so it doesn't fire immediately during startup
                _lastHeartbeatTime = EditorApplication.timeSinceStartup;
                _lastWatchdogCheck = EditorApplication.timeSinceStartup;

                // Start the diagnostic counter used for self-test
                _pjqTicksSinceStart = 0;

                // Force an immediate update so ProcessJobQueue starts processing as soon as possible
                EditorApplication.QueuePlayerLoopUpdate();

                // Self-test: wait a bit for the update loop to settle before verifying reachability
                ScheduleDelayedCall(1.5, RunSelfTest);

                // Reconnection anchor for /events clients: carries the previous compilation summary,
                // since compilation_finished (the success one) disappears along with the old domain.
                EventChannelService.PublishServerRestored(_port);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to start: {ex.Message}");
                _isRunning = false;
                // Don't clear PREF_SERVER_SHOULD_RUN — preserve the restart intent so the next Reload tries again
            }
        }

        public static void Stop(bool permanent = false)
        {
            if (!_isRunning) return;
            _isRunning = false;

            // Clear the auto-restart flag on a permanent stop
            if (permanent)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            }

            // Unregister from the global registry
            RegistryService.Unregister();

            try { _listener?.Stop(); } catch { /* Best-effort cleanup on shutdown */ }
            try { _listener?.Close(); } catch { /* Best-effort cleanup on shutdown */ }

            // Wait for the threads to finish
            try { _listenerThread?.Join(2000); } catch { }
            try { _keepAliveThread?.Join(2000); } catch { }
            _listenerThread = null;
            _keepAliveThread = null;

            // The admission counter can't carry over a stop/restart: an in-flight responder might never reach its
            // own release logic, and a leftover count would eat into the next server instance's quota.
            // ReleasePendingSlot() clamps at 0, so a late release is still safe.
            Interlocked.Exchange(ref _pendingRequests, 0);

            // Notify every pending job that it ended in an error. This runs after joining the listener thread
            // above, so both lanes are already quiescent and no locking is needed.
            FailQueuedJobs(_lightQueue, ref _lightQueued);
            FailQueuedJobs(_heavyQueue, ref _heavyQueued);

            if (permanent)
                SkillsLogger.Log($"Server stopped (permanent)");
            else
                SkillsLogger.LogVerbose($"Server stopped (will auto-restart after reload)");
        }
        
        /// <summary>
        /// Permanently stops the server; it will no longer auto-restart.
        /// </summary>
        public static void StopPermanent()
        {
            Stop(permanent: true);
        }

        /// <summary>
        /// The keep-alive loop — forces Unity to keep updating while it's unfocused.
        /// Never calls any Unity API directly (goes through the thread-safe QueuePlayerLoopUpdate).
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
                        // Wake the Unity main thread in a thread-safe way
                        EditorApplication.QueuePlayerLoopUpdate();
                    }
                    else
                    {
                        // Also wake periodically when there are no pending jobs, so the watchdog and heartbeat can run
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
                    // On Unity 6000.3+, QueuePlayerLoopUpdate sometimes throws a harmless
                    // "SetSceneRepaintDirty can only be called from the main thread",
                    // even though the wake-up itself actually succeeded. Suppress the noise here;
                    // whether the queue actually got drained is verified by the main thread's ProcessJobQueue.
                    if (ex is UnityException && ex.Message != null && ex.Message.Contains("main thread"))
                        SkillsLogger.LogVerbose($"KeepAlive wake-up benign: {ex.Message.Split('\n')[0]}");
                    else
                        SkillsLogger.LogWarning($"KeepAlive iteration error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The HTTP listener loop (producer).
        /// Critical constraint: this method runs on a background thread, so no Unity API calls are allowed —
        /// it only enqueues raw request data for the main thread to process.
        ///
        /// Quota and socket lifecycle: everything after <see cref="TryReservePendingSlot"/> is wrapped in the same
        /// try/finally, so every exit path — including a client aborting an upload mid-read — releases the
        /// admission quota exactly once and closes the context. A quota leak is permanent: after leaking
        /// MaxPendingRequests times, every subsequent request turns into a 503 QUEUE_FULL until the next domain reload.
        ///
        /// Error backoff has two tiers: an accept (GetContext) failure is listener-level, given a long backoff left to
        /// the watchdog; a single request's failure must never tie up this one accept thread over one bad client.
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
                    Thread.Sleep(500); // Avoid a tight exception loop; the watchdog restarts if needed
                    continue;
                }
                catch (ObjectDisposedException) { break; } // Listener already disposed; the watchdog restarts it
                catch (Exception)
                {
                    if (!_isRunning) break;
                    Thread.Sleep(1000); // Back off on an unknown listener error; the watchdog steps in
                    continue;
                }

                string body = "";
                bool reservedPendingSlot = false;
                bool handedOffToResponder = false;
                RequestJob job = null;

                try
                {
                    // Grab the raw data immediately (never touch the Unity API)
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

                    // On a malformed request line, Mono's HttpListener gives a null Url, and every path below
                    // dereferences it, so reject it early with an actual response.
                    var url = request.Url;
                    if (url == null)
                    {
                        SendImmediateJsonResponse(context, request, 400, BuildErrorPayload(SkillErrorResponse.Build(
                            SkillErrorCode.NotFound,
                            "Malformed request URI",
                            retryStrategy: SkillErrorResponse.Abort)));
                        continue;
                    }

                    // Fast path: GET /skills, GET /skills/schema, and GET /health are answered directly on this
                    // HTTP thread using the cache/snapshot the main thread already built (zero Unity API — see
                    // SkillRouter.TryGetCachedGetResponse and SendHealthFastPath).
                    // A miss falls through to the regular main-thread queue, which populates the cache/snapshot for next time.
                    if (request.HttpMethod == "GET")
                    {
                        string fastPath = url.AbsolutePath;

                        // Long-polling: GET /events never goes through the main-thread queue. The accept loop only hands the context
                        // off to a ThreadPool waiter — it must never block here (this is the only accept thread). The responder
                        // releases the admission quota and closes the response on every exit path.
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

                        // Liveness probe: answered from the main-thread snapshot, so a busy/blocked main thread can no longer hang
                        // /health itself. Falls back to the queue before the first snapshot exists, or when the caller asks for ?live=1.
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

                        // An aborted upload throws IOException here — the finally block below prevents leaking the quota and socket.
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

                    // Enqueue for the main thread to process, sorted into one of two priority lanes. MaxQueuedRequests is still a
                    // single quota shared by both lanes — the admission cap is unchanged, only the service order differs.
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
                        // Increment before enqueuing: the count may briefly run high, but never goes negative from a consumer draining an uncounted item.
                        Interlocked.Increment(ref _lightQueued);
                        _lightQueue.Enqueue(job);
                    }
                    else
                    {
                        Interlocked.Increment(ref _heavyQueued);
                        _heavyQueue.Enqueue(job);
                    }

                    // Use an explicit state object to enqueue the responder, avoiding a closure-capture race.
                    var handoffJob = job;
                    job = null; // Ownership has passed to the queue; must not return the object to the pool even if QueueUserWorkItem throws
                    ThreadPool.QueueUserWorkItem(WaitAndRespondCallback, handoffJob);
                    handedOffToResponder = true;
                }
                catch (Exception ex)
                {
                    // A single request's failure (aborted upload, malformed body, ...). The finally block below returns the quota
                    // and socket, so this just needs to briefly yield — sleeping long here would stall the one accept thread over one bad client.
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
        /// Waits for a job to finish and sends the HTTP response. Runs on a ThreadPool thread — no Unity API calls allowed.
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
                // Wait for main-thread processing (with a timeout)
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
                
                // Send the HTTP response (thread-safe)
                SendResponse(job);
            }
            catch (Exception ex)
            {
                // Best effort — try to send an error response
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
        /// Sends the HTTP response. Thread-safe (never touches the Unity API).
        ///
        /// Only the two cacheable GET endpoints get job.ETag set (by <see cref="ApplyCacheableGetHeaders"/>);
        /// whether it's present decides whether the ETag/Vary headers and gzip negotiation are enabled here,
        /// so every other endpoint's behavior is unchanged from before.
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

                if (job.ETag != null)
                {
                    response.Headers.Add("ETag", $"\"{job.ETag}\"");
                    response.Headers.Add("Vary", "Accept-Encoding");
                }

                response.StatusCode = job.StatusCode;

                // By the time a 304 reaches here, ResponseJson has already been cleared, so this never falls into
                // the response-body branch and never carries a Content-Encoding.
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

        // ===== GET /events long polling =====

        private const int EventsDefaultTimeoutSeconds = 25;
        private const int EventsMinTimeoutSeconds = 1;
        private const int EventsMaxTimeoutSeconds = 55;
        private const int EventsPollIntervalMs = 250;

        /// <summary>Raw request data the accept loop hands off to the long-poll responder.</summary>
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
                // Client disconnected, or the listener died mid-poll — reconnection is already the intended
                // protocol; this must never be allowed to noisily kill the ThreadPool thread.
                // Throwing here means it happened before WriteEventsResponse, so no one has closed the response yet.
                CloseContextSafely(poll.Context);
            }
            finally
            {
                ReleasePendingSlot();
            }
        }

        /// <summary>
        /// Long-poll responder for GET /events. Runs entirely on a ThreadPool thread — zero Unity API, zero
        /// SessionState, no SkillsLogger (same constraints as SendCachedGetResponse). Loops "scan buffer → wait"
        /// until an event newer than 'since' shows up, it times out, or the server stops (reload) — writes the response directly.
        /// Correctness relies on the 250ms poll interval; the publish signal only reduces latency.
        /// Query params: since (default: current max seq, i.e. wait for new events only; 0 replays the buffer),
        /// timeout (seconds, default 25, clamped to 1-55), types (comma-separated filter).
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
                // Must Reset before scanning: a publish that lands after the scan will re-signal.
                // Another waiter's Reset could still swallow it, but the cost is only one extra 250ms poll
                // interval of waiting — it never affects correctness.
                EventChannelService.ResetSignal();

                if (EventChannelService.TryReadEventsAfter(since, typeFilter, out events, out cursor, out oldestSeq))
                    break;

                // The server is stopping (a domain reload is imminent): answer immediately with whatever's on
                // hand (i.e. empty), so the client reconnects instead of hanging.
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

            // since+1 is the first seq the client is missing; anything below oldestSeq has already been evicted
            // (ring buffer overflow) or lost to a domain reload.
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
        /// Writes the /events HTTP response. ThreadPool thread — only headers, encoding, and socket writes
        /// (the pure-string counterpart of SendCachedGetResponse/SendResponse).
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
        /// Main-thread job processor (consumer).
        /// Driven by EditorApplication.update — any Unity API call here is safe.
        /// </summary>
        private static void ProcessJobQueue()
        {
            // Main-thread liveness mirror, written before anything else this frame. The HTTP thread reports
            // /health.mainThreadIdleMs by subtracting "now" from it — exactly how a caller tells "server is dead" from
            // "Unity is busy". Writing it early means the next probe counts a long job in this tick as idle — the honest reading.
            Interlocked.Exchange(ref _mainThreadTickUtc, DateTime.UtcNow.Ticks);

            // Startup diagnostic counter (a cheap volatile increment, stops at 10000)
            var diagTick = _pjqTicksSinceStart;
            if (diagTick >= 0 && diagTick < 10000)
                _pjqTicksSinceStart = diagTick + 1;

            double frameStart = EditorApplication.timeSinceStartup;

            // /health snapshot: the cheap half refreshes every frame, the expensive half only on permission changes or when the 1-second floor expires.
            bool fullSnapshot = _healthSnapshotDirty || !_snapReady ||
                                frameStart - _lastHealthSnapshot >= HealthSnapshotInterval;
            if (fullSnapshot)
            {
                _healthSnapshotDirty = false;
                _lastHealthSnapshot = frameStart;
            }
            RefreshHealthSnapshot(fullSnapshot);

            // Lane 1 — light: drained entirely, not bound by the frame budget. These are the read-only, millisecond-scale
            // handlers (see IsLightRequest); starving them behind a slow skill is the failure this split prevents.
            while (TryDequeueJob(_lightQueue, ref _lightQueued, out var lightJob))
                RunJob(lightJob);

            // Lane 2 — heavy: two gates, a count cap and a wall-clock budget, both checked before starting each job.
            // A single skill legitimately running for several seconds is allowed; the budget can't interrupt it,
            // it can only refuse to start the next one — which is exactly why the editor can still repaint between bursts.
            int processed = 0;
            while (processed < MaxHeavyJobsPerFrame)
            {
                // The budget must never block the first heavy job of a frame. A busy light lane could legitimately eat the
                // whole 12ms, and letting that zero out the heavy lane would turn the priority split into starved skill execution.
                if (processed > 0 && EditorApplication.timeSinceStartup - frameStart >= HeavyFrameBudgetSeconds)
                    break;

                if (!TryDequeueJob(_heavyQueue, ref _heavyQueued, out var heavyJob))
                    break;

                RunJob(heavyJob);
                processed++;
            }

            // Work remains: request the next tick immediately, instead of waiting up to KeepAlivePollingMs for keep-alive to notice.
            if (Volatile.Read(ref _heavyQueued) > 0)
                EditorApplication.QueuePlayerLoopUpdate();

            double now = EditorApplication.timeSinceStartup;

            // Registry heartbeat
            if (_isRunning)
            {
                if (now - _lastHeartbeatTime > HeartbeatInterval)
                {
                    _lastHeartbeatTime = now;
                    RegistryService.Heartbeat(_port);
                }

                // Watchdog: restart the server if the listener thread has died
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

            // Fallback: recovers the server after a domain reload if delayCall never fires
            if (!_isRunning && !_domainReloadPending)
            {
                if (now - _lastSafetyNetCheck > SafetyNetInterval)
                {
                    _lastSafetyNetCheck = now;
                    bool shouldRun = EditorPrefs.GetBool(PREF_SERVER_SHOULD_RUN, false);
                    // Also covers editor-launch: on first startup shouldRun happens to be false (cleared on quit), and without
                    // this the new path would be the only auto-start path completely broken whenever delayCall doesn't fire.
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
        /// Runs a dequeued job to completion, and releases its waiting responder.
        /// Factored out of <see cref="ProcessJobQueue"/> so both lanes share exactly the same error handling and
        /// bookkeeping. Main thread only.
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
                // Only invalidate scene caches for requests that could have changed state (POST = skill execution)
                if (job.HttpMethod == "POST")
                    GameObjectFinder.InvalidateCache();
            }
        }

        /// <summary>
        /// Main-thread counterpart to GET /skills and GET /skills/schema, paired with the HTTP-thread fast path:
        /// stamps the freshly built body with an ETag — <see cref="SkillRouter.GetEtagForCachedGet"/> derives it from
        /// the same cache key the fast path uses — then collapses it to an empty-body 304 if If-None-Match matches.
        ///
        /// Only 200 responses get tagged. An error response body must never be handed to the client under a
        /// content hash, or the client will cache it.
        /// </summary>
        private static void ApplyCacheableGetHeaders(RequestJob job, string path)
        {
            if (job.StatusCode != 200)
                return;

            job.ETag = SkillRouter.GetEtagForCachedGet(path, job.QueryString, job.ResponseJson);
            if (job.ETag != null && IfNoneMatchSatisfied(job.IfNoneMatch, job.ETag))
            {
                job.StatusCode = 304; // Not Modified — must not carry a response body
                job.ResponseJson = null;
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

            // Health check. Only reached when the HTTP-thread fast path bails out: either the caller asked for ?live=1,
            // or the first snapshot hasn't been taken yet. Both share the same shape — BuildHealthJson is its sole source.
            if (path == "/" || string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                // The live read also refreshes the mirror, so the next fast-path probe picks up the latest values.
                RefreshHealthSnapshot(full: true);
                job.StatusCode = 200;
                job.ResponseJson = BuildHealthJson(HealthVitals.FromLive(), live: true);
                return;
            }

            // Compile-feedback loop closure — authoritatively answers "did the script I just changed compile?".
            // Goes through the main-thread path (same as /health) so it can read live editor state as well as the
            // last result, the latter of which survives the domain reload a successful compile triggers.
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

            // Execution telemetry aggregation — answers "which skills are being called / failing / slow".
            // Goes through the main-thread path (same as /health): reads the telemetry EditorPref and JSONL files.
            // Results are cached per window for 30 seconds inside SkillTelemetryService, to cap disk reads.
            if (string.Equals(path, "/analytics", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                var analyticsQs = SkillRouter.ParseQueryString(job.QueryString);
                string window = analyticsQs.TryGetValue("window", out var windowVal) ? windowVal : "24h";
                job.StatusCode = 200;
                job.ResponseJson = SkillTelemetryService.BuildAnalyticsJson(window);
                return;
            }

            // Fetches the skill manifest (optionally filtered).
            // A request only reaches the main thread when the HTTP-thread fast path missed, so this call is responsible
            // for building the cache. ApplyCacheableGetHeaders stamps it with the same ETag the fast path will use from
            // here on, so a client that keeps sending If-None-Match starts getting 304s from the next request onward.
            // The empty-query special case has been pushed down into SkillRouter: which tier a bare request should get
            // (/skills picks brief, /skills/schema picks full) is now the same decision shared with the HTTP-thread fast
            // path, so the two can never give different answers for the same URL.
            // A rejected ?category= / ?operation= value comes back as an error response body, and must never be treated
            // as a manifest: returning 200 would misreport it, and tagging it with an ETag would be far worse than ugly —
            // the client's next If-None-Match would hit and get a bodiless 304, i.e. the rejection vanishes and the query
            // looks accepted. Keeping bad spellings out of _etagCache also stops a run of typos evicting genuine entries.
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

            // Session constants (category/operation enums, reserved parameter names, the tracked-skills list)
            // plus the field defaults that ?wire=v2 omits. Cached and ETagged the same as the two endpoints above.
            if (string.Equals(path, "/skills/meta", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                job.StatusCode = 200;
                job.ResponseJson = SkillRouter.GetMeta();
                ApplyCacheableGetHeaders(job, path);
                return;
            }

            // Recommend skills by intent
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

            // Cross-skill aggregate execution (each step runs the full Execute pipeline)
            if (string.Equals(path, "/skills/batch", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                HandleSkillsBatchRequest(job);
                return;
            }

            // Job queries (a light GET, bypasses the skill router for high-frequency progress polling)
            if (job.HttpMethod == "GET" &&
                (string.Equals(path, "/jobs", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/jobs/", StringComparison.OrdinalIgnoreCase)))
            {
                HandleJobsRequest(job);
                return;
            }
            
            // Execute / DryRun / Plan a skill
            if (path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "POST")
            {
                if (RejectIfCompiling(job))
                    return;

                // Extract the skill name (preserving original casing) and validate it
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


            // Permission system: mode + grant tokens + audit log.
            if (path.StartsWith("/permission/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/permission", StringComparison.OrdinalIgnoreCase))
            {
                HandlePermissionRequest(job);
                return;
            }


            // No route matched
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
        /// Parses ?mode= / ?dryRun= from the already-parsed query string. Returns false when either parameter is
        /// present but its value can't be recognized (writes an INVALID_MODE error to the job) — the request must
        /// never execute in that case. Without this guard, an agent that misspells the mode (e.g. ?mode=dry_run,
        /// ?dryRun=1) would think it was previewing, while the server had already silently executed for real.
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
                    return true; // Explicit false = execute for real

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
        /// Parses ?diff= for POST /skill/{name}. A semantic sceneDiff is only meaningful for a real execution, so
        /// it's silently ignored under ?mode=dryRun / ?mode=plan (nothing executed, so there's nothing to diff).
        /// An unrecognized value is rejected with 400 (consistent with TryResolveRequestMode) rather than silently
        /// ignored, so an agent that misspells ?diff doesn't think it got a diff while the server quietly dropped it.
        /// Only returns false when the value is invalid (and writes a 400); every other case leaves captureDiff set.
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

            // diff only applies to a real execution; dryRun/plan previews have nothing to compare against.
            captureDiff = requested && mode == SkillRouter.RequestMode.Execute;
            return true;
        }

        /// <summary>
        /// Writes a 503 COMPILING response when Unity is compiling or a domain reload is pending; returns true if the
        /// request was rejected. Shared by POST /skill/{name} and POST /skills/batch (which misses the "/skill/" prefix check).
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

        // ===== Execution telemetry =====

        /// <summary>
        /// Records the result of a POST /skill/{name} call into <see cref="SkillTelemetryService"/>. Determines ok
        /// and extracts errorCode with a lightweight string probe (not JObject.Parse — this is the single-skill hot path).
        /// Fully isolated: a telemetry failure must never alter the business response already computed by the caller.
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
        /// Records the result of one step in /skills/batch. The batch loop already holds each step's parsed payload,
        /// so ok/errorCode are passed directly (no string probe needed). A null/blank skill name (malformed step) is
        /// recorded as "(malformed)". mode is batch_step or batch_step_dryRun, depending on the dryRun flag.
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
        /// Determines the skill outcome by scanning the raw JSON string — cheap enough for the hot path and tolerant
        /// of nested content. An error envelope (<c>"status":"error"</c>) counts as a failure, its <c>"errorCode"</c> is extracted.
        /// For a dryRun preview, a <c>"valid":false</c> verdict counts as a failure and reports DRYRUN_INVALID
        /// (a dryRun against an unknown skill returns an error envelope, caught by the first check).
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
        /// Extracts the value of the first <c>"errorCode":"..."</c> field. Returns null when the field is missing
        /// or is JSON null (<c>"errorCode":null</c> doesn't match the quoted probe pattern), so the telemetry row
        /// records a null errorCode rather than a wrong value.
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

        // ===== Cross-skill batch execution =====

        private const int MaxBatchSteps = 50;

        /// <summary>
        /// POST /skills/batch — executes multiple skills sequentially within a single main-thread job,
        /// saving one HTTP round trip and one main-thread wake-up per step.
        ///
        /// Request body: {"steps":[{"skill":"gameobject_create","args":{...}}, ...], "continueOnError":false}
        /// - Each step runs the full SkillRouter.Execute pipeline (permission gate, semantic validation, undo, audit),
        ///   identical to calling POST /skill/{name} individually; each step gets its own undo group, never merged.
        /// - Fails fast by default: the first failing step terminates the batch, remaining steps reported "skipped".
        ///   With continueOnError=true, a failing step is recorded and the batch continues. A grant-related response
        ///   (MODE_RESTRICTED / CONFIRMATION_REQUIRED) always interrupts regardless of continueOnError — these can't
        ///   be skipped; the full response (with the grant token) is returned so the caller can resume after granting.
        /// - Static $param slots: a request-body-level "params":{"name":value,...} object fills placeholder nodes in
        ///   a step's structured args. At any depth, an object whose only key is "$param" (e.g. {"$param":"height"}),
        ///   or shaped exactly {"$param":"name","default":X}, is replaced by params[name] when present, else its
        ///   "default", else that step fails SEMANTIC_INVALID (details.param names the missing slot).
        ///   $param is purely static and order-independent, resolved before $ref, and behaves the same under dryRun
        ///   and execute (a missing slot fails in dry-run too, exposing gaps before replay).
        ///   $param and $ref are mutually orthogonal: a step may use both, but a single node may only be one or the
        ///   other, never both ({"$param":..,"$ref":..} is SEMANTIC_INVALID). Any $ref left after substitution is
        ///   handled by the $ref stage below.
        /// - Cross-step references: at any depth within a step's structured args, an object whose only key is "$ref"
        ///   (e.g. {"$ref":"$0.instanceId"}) is substituted before that step executes. "$N" is the 0-based index of
        ///   an earlier, successful step; after the dot is a Newtonsoft SelectToken path into that step's unwrapped
        ///   result (bare "$0" = whole result, "$1.items[0].path" reaches into arrays).
        ///   An unresolvable reference (malformed / out-of-range / forward reference / referenced step didn't
        ///   succeed / path matches nothing) fails that step with SEMANTIC_INVALID, then falls through to the normal
        ///   fail-fast / continueOnError rules. References inside string-typed args are not resolved — only structured JSON args are scanned.
        /// - ?mode=dryRun validates every step but executes nothing, and never halts, so an agent can preview the
        ///   whole sequence in one call. $ref parameters carry no real value during a dry run: they're stripped from
        ///   the validation body and only get a structural check (index range, ordering, the referenced skill's
        ///   declared Outputs); such steps carry refsValidated and findings in validation.warnings. ?mode=plan isn't supported.
        /// - ?mode=transactional makes the whole batch all-or-nothing: an unknown skill, or a step whose skill
        ///   declares MayTriggerReload, is rejected up front with 400 (a reload clears the undo stack, breaking the
        ///   rollback promise), and continueOnError=true is rejected as self-contradictory. If any step fails —
        ///   including a grant interruption, whose token is still returned — every executed step is rolled back via
        ///   Undo.RevertAllDownToGroup and re-marked status:"rolled_back" (a MutatesAssets step gets
        ///   rollbackReliability:"partial": AssetDatabase disk writes aren't fully covered by the undo stack). The
        ///   response then reports status:"rolled_back" and rolledBack:true. transactional composes freely with $ref.
        /// - Both modes can equally be specified in the body ("mode":"dryRun"/"transactional", "dryRun":true). These
        ///   two keys are parsed independently, query string wins on conflict — see TryApplyBatchBodyMode — the
        ///   response echoes the mode that actually took effect ("mode":"dryRun"|"transactional"|"execute", plus the
        ///   legacy "dryRun" boolean), the only way for the caller to confirm which of the four spellings won.
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
                // The request body might only turn this into a preview at this point — the ?diff= parsed above
                // was based only on the mode in the query string, and a preview has nothing to compare against.
                captureDiff = false;
                // Nor is there anything to fence or roll back. This only happens when the two keys come from different
                // places (?mode=transactional plus body {"dryRun":true}), since one 'mode' value can't request both; if
                // transactional stayed on, ExecuteBatchCore would open an undo fence and roll back to it on the first
                // invalid step — touching the user's undo stack for a request that executed nothing.
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

            // The request-body-level "params" fills $param slots in step args (static, independent of mode).
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
        /// The sequential execution core behind POST /skills/batch: $param substitution, cross-step $ref resolution,
        /// then step-by-step runs of the full single-skill pipeline (SkillRouter.Execute — permission gate, undo,
        /// audit), with fail-fast / continueOnError / grant-interruption semantics and optional transactional rollback.
        /// The caller must pass an already-validated, non-empty steps array (transactional mode must also have run
        /// RejectTransactionalPrecheck). Returns the response body as a JObject ({status, executed, failed, results, ...}).
        /// </summary>
        internal static JObject ExecuteBatchCore(JArray steps, JObject batchParams, bool continueOnError,
            bool dryRun, bool transactional, string agentId, bool captureDiff = false)
        {
            int txStartGroup = -1;
            if (transactional)
            {
                // Plants a fence on the undo timeline for the whole batch. Each step still opens (and collapses)
                // its own undo group inside Execute; on failure, everything above this fence is rolled back at once.
                Undo.IncrementCurrentGroup();
                txStartGroup = Undo.GetCurrentGroup();
            }

            var results = new List<JObject>(steps.Count);
            // Each successful step's already-unwrapped result, for later steps to reference via $ref.
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

                // ---- Static $param substitution (resolved before $ref) ----
                // Purely static substitution, drawn from the request-body-level "params" object, so the result is identical
                // between dryRun and execute (the real value is present in both). Any $ref left after substitution is handled by the $ref stage below.
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

                // ---- Cross-step $ref references ----
                List<BatchRefNode> refNodes = null;          // for dryRun bookkeeping
                HashSet<string> strippedRefParams = null;    // dryRun: params already stripped from the validation body
                bool wholeArgsFromRef = false;               // dryRun: the args root node is itself a $ref
                List<string> refWarnings = null;             // dryRun: structural-check findings
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

                            // References carry no real value during a dry run. A parameter holding a $ref is
                            // removed from the validation body — leaving the placeholder object in would just
                            // produce TYPE_MISMATCH noise. The resulting MISSING_PARAM and semantic gaps are
                            // corrected uniformly after DryRun returns (see AdjustDryRunPayloadForRefs).
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
                        // Resolve on a deep copy using earlier steps' results; the original request body is never mutated.
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
                        // Every step shares the same POST job, so the per-request cache invalidation in ProcessJobQueue doesn't run
                        // between steps — without this line, a step couldn't find an object created earlier in the same batch.
                        // A ReadOnly step is side-effect-free by contract and can't stale the cache, so it's skipped; every other
                        // step — including one whose name doesn't resolve to a known skill — still triggers invalidation.
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
                    // $ref parameters have already been stripped from the validation body — the payload must be corrected before
                    // reading its 'valid' verdict (filter missingParams, downgrade semantic errors, attach refsValidated).
                    if (refNodes != null && refNodes.Count > 0)
                        AdjustDryRunPayloadForRefs(stepPayload, refNodes, strippedRefParams, wholeArgsFromRef, refWarnings);

                    // A DryRun response carries status:"dryRun" and valid:bool; an unknown skill returns status:"error".
                    // A validation failure never halts a dry-run batch.
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

                    // A grant-related response must never be skipped: the caller has to complete the grant/confirmation flow,
                    // so the batch stops here even if continueOnError=true. The full payload above carries the grant token.
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
                // the entry-level status field has already expressed success.
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
                // All-or-nothing: any failure (including a grant interruption — the failed step's entry still carries the
                // grant token as-is) rolls back every step executed since the batch fence, leaving no redo entries.
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
                    // AssetDatabase's disk writes aren't fully covered by the undo stack —
                    // mark this kind of rollback as partial rather than over-promising.
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
                // Echoes back the mode that actually took effect, not whichever single key requested it. ?mode= and
                // ?dryRun=/body "dryRun" are each parsed independently, either can come from the URL or payload (see
                // TryApplyBatchBodyMode), so "which of my four spellings won" can't be derived from the request alone —
                // a caller who thinks it sent a preview must be able to see that it actually got one.
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

        /// <summary>A $param name aggregated across the whole step sequence (for macro-library introspection).</summary>
        internal sealed class BatchParamDeclaration
        {
            public string Name;
            public bool HasDefault;      // every node referencing this name carries an inline default
            public JToken DefaultValue;  // the first inline default seen (for display only)
        }

        /// <summary>
        /// Aggregates the $param slots declared across the whole step sequence, keyed by name, in first-seen order.
        /// A name only counts as having a default if every node that references it carries an inline "default" —
        /// even a single bare {"$param":"x"} slot makes that value required.
        /// A malformed slot ($param name isn't a string) is skipped here, and reported per-step at execution time.
        /// Consistent with the execution stage, doesn't scan string-typed args.
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
        /// Parses ?mode= / ?dryRun= for /skills/batch. Batch accepts dryRun/transactional
        /// (a single-skill request accepts dryRun/plan — see TryResolveRequestMode, which keeps rejecting
        /// 'transactional' so its INVALID_MODE validValues stays accurate).
        /// Any unrecognized value returns false (and writes an error response).
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
                    return true; // Explicit false = execute for real

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
        /// The full set of top-level request-body keys POST /skills/batch recognizes, and the full set of query
        /// keys it reads (see TryResolveBatchRequestMode and TryResolveDiff).
        /// Anything else is rejected rather than ignored: a silently-dropped key is exactly what causes an agent
        /// to believe it requested a preview, or requested async execution, and got neither.
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
        /// Rejects an unrecognized query parameter on /skills/batch (400 UNKNOWN_PARAM). Returns true if rejected.
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
        /// Rejects an unrecognized top-level request-body key on /skills/batch (400 UNKNOWN_PARAM), consistent
        /// with what CollectUnknownParameters does for single-skill args.
        /// This runs before the 'steps' check, so a typo like "step" is reported as an "unknown key" itself,
        /// rather than as a missing 'steps'. Returns true if the request was rejected.
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
        /// Gives targeted hints for the handful of /skills/batch keys agents actually get wrong: the singular "step",
        /// promoting a step's own fields to the top level, treating transactional as a boolean, and runAsync — this
        /// endpoint never had that parameter, since it runs every step in the same main-thread job.
        /// Returns null when there's nothing specific to say.
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
        /// Reads a boolean that the client may have quoted. JSON true/false is accepted as-is, and the strings
        /// "true"/"false" are also parsed; anything else fails, so the caller can report TYPE_MISMATCH instead of
        /// silently falling back to a default.
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
        /// Applies the request-body-level "mode"/"dryRun" on top of what the query string already resolved.
        ///
        /// <para>"mode" and "dryRun" are two independent keys, each parsed independently, query string wins on
        /// conflict. In the past one "has the query already decided" flag gated both, so a URL's
        /// <c>?mode=transactional</c> would silently drop a body <c>{"dryRun":true}</c>, actually executing a batch
        /// the caller meant to preview — the worst possible failure for a preview, invisible in the response.</para>
        ///
        /// <para>Priority order: for the same key, the URL wins over the payload; within the same slot, "mode"
        /// wins over "dryRun" (on the query-string side, this is what TryResolveBatchRequestMode's early return
        /// already implements). Across the two keys, dryRun is monotonic — any surviving explicit <c>dryRun:true</c>
        /// makes the request a preview, and <c>dryRun:false</c> never cancels <c>mode:"dryRun"</c>, because "don't
        /// decide preview from this key" and "execute for real" aren't the same statement.
        /// Biasing toward preview is the only direction where the worst case is just a wasted call.</para>
        ///
        /// <para>Even a value that loses the priority contest is still validated, so a typo is never swallowed.
        /// Returns false (and writes a 400) on an invalid value or type.</para>
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

                // This key only belongs to the request body when the URL didn't write 'mode'.
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

                // Skip if a request-body-level 'mode' already spoke for this slot; that's priority within the
                // same slot, not a reason to ignore the URL's own dryRun key.
                if (!queryOwnsDryRun && !bodyModeApplied)
                    dryRun = dryRun || bodyDryRun;
            }

            return true;
        }

        /// <summary>
        /// Whether the query string carries a usable value for this key — the same "present and non-blank" test
        /// the ?mode= / ?dryRun= parsers use, so "?dryRun=" counts as "the caller didn't decide" in both places.
        /// </summary>
        private static bool HasQueryValue(Dictionary<string, string> qs, string key) =>
            qs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

        /// <summary>
        /// A transactional batch relies on the editor's undo stack to promise "all or nothing", so anything that
        /// would break that promise is rejected up front (400 SEMANTIC_INVALID) rather than failing mid-execution:
        /// unknown/malformed steps, a skill that might trigger a domain reload (wipes the undo stack), and
        /// continueOnError=true (a transaction is fail-fast by definition). Returns true if the batch was rejected.
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

        // ===== Static $param substitution (batch) =====

        /// <summary>
        /// A {"$param":"name"} / {"$param":"name","default":X} slot found within a step's args.
        /// ParamName is null when $param's value isn't a JSON string (reported as malformed at execution time).
        /// Unlike BatchRefNode, there's no TopLevelParam here: $param carries a real value under every mode, so
        /// nothing needs to be stripped from the dry-run validation body.
        /// </summary>
        private sealed class BatchParamNode
        {
            public JObject Node;
            public string ParamName;
            public bool HasDefault;
            public JToken DefaultValue;
        }

        /// <summary>
        /// An object node is a param node if and only if "$param" is its sole property (a bare slot), or it has
        /// exactly two properties, "$param" + "default" (a slot with a fallback). An object merely containing
        /// "$param" among other keys is payload data and is left alone — consistent with IsBatchRefNode.
        /// paramName is null when $param's value isn't a JSON string.
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
        /// Collects every $param slot at any depth within a step's args.
        /// When a node carries both "$param" and "$ref" (a node can only be one or the other, never both), it's
        /// recorded in paramRefConflict for the caller to reject with SEMANTIC_INVALID, and the search stops there.
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
        /// Resolves a single slot's value: if the batch's "params" object holds that name, it wins (case-sensitive),
        /// otherwise the node's inline "default" is used, otherwise that step fails with SEMANTIC_INVALID
        /// ("not provided and no default"). A $param name that isn't a string is judged malformed.
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

        // ===== Cross-step $ref references (batch) =====

        /// <summary>
        /// A {"$ref":"$N.path"} node found within a step's args. RefString is null when $ref's value isn't a JSON
        /// string (reported as malformed at execution time).
        /// TopLevelParam is the args property whose subtree contains this node; null when the node is itself the args root.
        /// </summary>
        private sealed class BatchRefNode
        {
            public JObject Node;
            public string RefString;
            public string TopLevelParam;
        }

        /// <summary>
        /// An object node is a reference if and only if "$ref" is its sole property;
        /// an object that merely contains "$ref" among many other keys is payload data and is left alone.
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
        /// Parses "$N", "$N.path", or "$N[…]" — N is the 0-based step index, and the rest is a Newtonsoft
        /// SelectToken path into that step's already-unwrapped result.
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
        /// Resolves a single reference against the already-executed steps' unwrapped results.
        /// Fails (with a structured reason) when: the reference is malformed, the index is out of range for this
        /// batch, it's a forward reference (N >= current step), the referenced step didn't succeed, or the SelectToken path matches nothing.
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
        /// Does a dry-run structural validation of a single reference (no real value to resolve yet): the index is
        /// in range and points to an earlier step, the referenced skill is known, and the path's first segment
        /// appears among the referenced skill's declared Outputs. Findings are only warnings — Outputs metadata may be incomplete, and a dry-run batch never halts.
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
                return; // No declared Outputs to compare against
            string firstSegment = FirstSelectTokenSegment(selectPath);
            if (firstSegment == null)
                return; // "[0]…" indexes into the result root — there's no name to validate

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
        /// Post-processes a step's DryRun payload after its $ref parameters are stripped from the validation body:
        /// drops the MISSING_PARAM entries stripping produced, downgrades that step's semanticErrors to warnings
        /// (the check ran without a reference value, so pass/fail is only a guess), recomputes 'valid' from what's
        /// left, and attaches refsValidated so the caller can see which parameters only got a structural check.
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
                return; // An error payload (unknown skill, etc.) — refsValidated is already attached, nothing more to correct

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
        /// Routes GET /jobs and GET /jobs/{id}[/logs] directly to BatchPersistence, bypassing the skill router.
        /// Designed for high-frequency progress polling: a caller pings GET /jobs/{id} every 200-500ms to get the latest snapshot.
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

        // ===== Permission system =====

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
                    // Deprecated alias: forwards to the allowlist/remove logic, response carries deprecated=true.
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
            // Field rename: `granted` → `allowlist`. The `granted` field is kept as a compatibility alias for one
            // version, and will be removed in the next minor version — clients should migrate to the `allowlist` field.
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                mode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                panelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                allowlist = allowlist,
                granted = allowlist, // deprecated alias — removed in the next minor version
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

            // The args field is optional — Approach B prefers the entry's cached original argsJson. When the body
            // carries args, it participates in hash validation under the existing rules; else the entry cache is read directly (TryPeekArgsJson).
            bool argsProvided = body.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var argsToken)
                                && argsToken != null && argsToken.Type != JTokenType.Null;
            string argsJson;
            if (argsProvided)
            {
                argsJson = ExtractArgsJson(body);
            }
            else
            {
                // Read the cached original argsJson directly from the entry — works for both zero-arg and parameterized
                // skills, so an AI calling grant only needs the token, matching "one-step execution" semantics. Falls back
                // to "{}" when the entry doesn't exist/has expired, so TryGrantAndReturnArgs below returns Invalid with a clear error.
                argsJson = SkillsModeManager.TryPeekArgsJson(token) ?? "{}";
            }

            // Note: HandlePermissionGrant is called by ProcessJobQueue on the main thread (EditorApplication.update), so
            // the ThreadStatic one-shot token set by TryGrantAndReturnArgs, and the subsequent SkillRouter.Execute, both
            // run on that same main thread — the thread-safety precondition holds, no extra dispatch needed.
            var (outcome, cachedSkill, cachedArgs) = SkillsModeManager.TryGrantAndReturnArgs(skill, token, argsJson);
            switch (outcome)
            {
                case GrantOutcome.Granted:
                {
                    // Approach B one-step execution: the one-shot token was already set on the current thread by
                    // TryGrantAndReturnArgs, and SkillRouter.Execute → CheckAccess consumes it immediately for a single pass.
                    //
                    // But the consumption point isn't guaranteed to be reached: Execute's four parameter-validation checks
                    // (UnknownParam / MissingParam / TypeMismatch / SemanticInvalid) all early-return before the permission
                    // gate, and any of those — or an exception caught here — would leave the token on the main thread, to be
                    // picked up by a later request for the same-named skill with different args. finally's unconditional clear covers every path.
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

                    // Try to inline execJson as a JSON object so callers upstream can read fields directly; falls back to a string on failure.
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

            // Deprecated alias: forwards to AllowlistRemove / ClearAllowlist. The response carries `deprecated: true`,
            // to help clients migrate to /permission/allowlist/remove.
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

        // ===== Allowlist endpoints =====

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
            // Strip _confirm and re-serialize, so the hash matches SkillRouter-side normalization.
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
                // 1. Reachability test with raw TCP and retries (completely bypasses the .NET HTTP client stack)
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
                        if (attempt > 1) Thread.Sleep(attempt * 1500); // Back off 3s, 4.5s

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
                                    // Before warning, retry localhost against other loopback addresses first.
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

                // 2. Port scan: report which ports in 8090-8100 are occupied
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
                    // Fall back to the known loopback addresses below.
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
