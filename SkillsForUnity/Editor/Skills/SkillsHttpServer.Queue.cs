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
    public static partial class SkillsHttpServer
    {
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
            // True when the caller sent an explicit X-Agent-Id header -- that always wins, so RefineAgentId
            // never overwrites it with a process-chain guess.
            public bool AgentIdIsExplicit;
            // The client's TCP port at accept time, used to look up ClientProcessResolver's cache later. -1 when unavailable.
            public int RemotePort;
            public string QueryString;
            // Headers for conditional GET / content negotiation. A pure string read, so grabbed by the HTTP thread at enqueue time.
            public string IfNoneMatch;
            public string AcceptEncoding;
            // A status probe queued by a GET /jobs/{id}?wait= responder, not a client request: never counted in
            // totalProcessed, and its GET /jobs/{id} snapshot also carries the job's resultData once terminal.
            public bool IsInternalProbe;

            // Processing result (written by the main thread)
            public string ResponseJson;
            public int StatusCode;
            public bool IsProcessed;
            public int PoolReturned;
            // Content hash of ResponseJson for the two cacheable GET endpoints, null for every other endpoint.
            // It also doubles as both the ETag header and the gzip cache key.
            public string ETag;
            // Value of the Allow header on a 405 response; null everywhere else.
            public string AllowHeader;
            public ManualResetEventSlim CompletionSignal = new ManualResetEventSlim(false);

            public void Prepare(HttpListenerContext context, string httpMethod, string path, string body, string requestId, string agentId, string queryString = null, string ifNoneMatch = null, string acceptEncoding = null, bool agentIdIsExplicit = false, int remotePort = -1)
            {
                Context = context;
                HttpMethod = httpMethod;
                Path = path;
                Body = body;
                EnqueueTimeTicks = DateTime.UtcNow.Ticks;
                RequestId = requestId;
                AgentId = agentId;
                AgentIdIsExplicit = agentIdIsExplicit;
                RemotePort = remotePort;
                QueryString = queryString;
                IfNoneMatch = ifNoneMatch;
                AcceptEncoding = acceptEncoding;
                IsInternalProbe = false;
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                PoolReturned = 0;
                ETag = null;
                AllowHeader = null;
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
                AgentIdIsExplicit = false;
                RemotePort = -1;
                QueryString = null;
                IfNoneMatch = null;
                AcceptEncoding = null;
                IsInternalProbe = false;
                ResponseJson = null;
                StatusCode = 200;
                IsProcessed = false;
                ETag = null;
                AllowHeader = null;
                // Note: PoolReturned is maintained by ReturnRequestJob/Prepare, not managed by Reset
                CompletionSignal.Reset();
            }
        }

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
    }
}

// Producer:Betsy
