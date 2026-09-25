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

                    // Expected-instance guard: a caller that names the Editor it means (expectInstance / expectProject as a
                    // query parameter, or an X-Expect-Instance / X-Expect-Project header) is refused with 409
                    // INSTANCE_MISMATCH when this server is another one, before anything is queued or executed. /health
                    // stays exempt so discovery can always read the identity. The refusal lists the other registered
                    // instances, which means reading the registry file, so it is written by a ThreadPool responder.
                    if (!IsInstanceCheckExempt(request.HttpMethod, url.AbsolutePath))
                    {
                        var expectation = ReadInstanceExpectation(
                            url.Query, request.Headers[ExpectInstanceHeader], request.Headers[ExpectProjectHeader]);
                        string servingInstanceId = _snapInstanceId;
                        if (expectation != null && servingInstanceId != null &&
                            !expectation.Matches(servingInstanceId, _snapProjectName, _snapProjectPath))
                        {
                            var mismatch = new InstanceMismatchState
                            {
                                Context = context,
                                Expected = expectation,
                                InstanceId = servingInstanceId,
                                ProjectName = _snapProjectName,
                                ProjectPath = _snapProjectPath,
                                Port = _port,
                                RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                                AgentId = DetectAgent(request),
                            };
                            ThreadPool.QueueUserWorkItem(InstanceMismatchCallback, mismatch);
                            handedOffToResponder = true;
                            continue;
                        }
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

                        // Long-polling GET /jobs/{id}?wait=<seconds>: handed off like /events. The responder queues short
                        // status probes for the main thread and holds the response until the job is terminal, the wait
                        // runs out, or the server stops or starts a domain reload.
                        if (TryParseJobWait(fastPath, url.Query, out var waitJobId, out var waitSeconds, out var waitError))
                        {
                            if (waitError != null)
                            {
                                SendImmediateJsonResponse(context, request, 400, BuildErrorPayload(waitError));
                                continue;
                            }

                            Interlocked.Increment(ref _totalRequestsReceived);
                            var waitState = new JobWaitState
                            {
                                Context = context,
                                Path = fastPath,
                                RawQuery = url.Query,
                                JobId = waitJobId,
                                WaitSeconds = waitSeconds,
                                Generation = Volatile.Read(ref _listenerGeneration),
                                RequestId = $"req_{Interlocked.Increment(ref _requestIdCounter):X8}",
                                AgentId = DetectAgent(request),
                            };
                            ThreadPool.QueueUserWorkItem(JobWaitCallback, waitState);
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

                    // Process-chain agent attribution (see ClientProcessResolver): a request with an explicit
                    // X-Agent-Id header short-circuits entirely -- no point resolving what the caller already
                    // told us. Otherwise, BeginResolve synchronously resolves the client pid + its immediate
                    // parent (a few ms, no-fork syscalls only -- see BeginResolve's doc comment for why this
                    // step can't be deferred to a background thread) before handing the rest of the ancestor
                    // chain off asynchronously. RemoteEndPoint is already known from the accepted socket, so
                    // reading its Port here is instant.
                    bool agentIdIsExplicit = !string.IsNullOrEmpty(request.Headers["X-Agent-Id"]);
                    int remotePort = request.RemoteEndPoint?.Port ?? -1;
                    if (!agentIdIsExplicit && remotePort > 0)
                        ClientProcessResolver.BeginResolve(remotePort, _port);

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
                        request.Headers["Accept-Encoding"],
                        agentIdIsExplicit,
                        remotePort);

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
                    // A domain reload aborts in-flight responder threads: losing the answer then is the normal reload path.
                    if (ex is ThreadAbortException || ex2 is ThreadAbortException)
                        SkillsLogger.LogVerbose($"Response abandoned by a domain reload: {ex.Message}");
                    else
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
                response.Headers.Add("Access-Control-Allow-Methods", CorsAllowMethods);
                response.Headers.Add("Access-Control-Allow-Headers", CorsAllowHeaders);
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", job.RequestId);
                response.Headers.Add("X-Agent-Id", job.AgentId);
                AddInstanceHeaders(response);

                if (job.ETag != null)
                {
                    response.Headers.Add("ETag", $"\"{job.ETag}\"");
                    response.Headers.Add("Vary", "Accept-Encoding");
                }
                if (job.AllowHeader != null)
                    response.Headers.Add("Allow", job.AllowHeader);

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
    }
}

// Producer:Betsy
