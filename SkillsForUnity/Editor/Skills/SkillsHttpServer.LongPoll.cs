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
        private static void WriteEventsResponse(EventsPollState poll, int statusCode, string json) =>
            WriteRawJsonResponse(poll.Context, poll.RequestId, poll.AgentId, statusCode, json);

        /// <summary>
        /// Writes an already-serialized JSON body from a ThreadPool responder (/events, GET /jobs/{id}?wait=, the
        /// INSTANCE_MISMATCH refusal) and always closes the response. Only headers, encoding, and socket writes.
        /// </summary>
        private static void WriteRawJsonResponse(HttpListenerContext context, string requestId, string agentId, int statusCode, string json)
        {
            HttpListenerResponse response = null;
            try
            {
                response = context.Response;
                response.Headers.Add("Access-Control-Allow-Methods", CorsAllowMethods);
                response.Headers.Add("Access-Control-Allow-Headers", CorsAllowHeaders);
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", requestId);
                response.Headers.Add("X-Agent-Id", agentId);
                AddInstanceHeaders(response);
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                byte[] buffer = Encoding.UTF8.GetBytes(json ?? string.Empty);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (HttpListenerException) { /* Client disconnected */ }
            catch (System.IO.IOException) { /* Client disconnected mid-write */ }
            catch (ObjectDisposedException) { /* Response already closed */ }
            catch { /* Never let responder write errors bubble */ }
            finally
            {
                try { response?.Close(); } catch { }
            }
        }

        // ===== GET /jobs/{id}?wait= long polling =====

        internal const double JobWaitMaxSeconds = 120;
        private const int JobWaitPollIntervalMs = 500;
        // How quickly a waiting responder notices a stop or a pending domain reload.
        private const int JobWaitSliceMs = 100;
        // A probe waits at least this long for its first answer, even under wait=0: a busy main thread must not turn
        // "no snapshot yet" into a timeout faster than a plain GET /jobs/{id} would.
        private const int JobWaitFirstAnswerFloorMs = 10000;

        /// <summary>Raw request data the accept loop hands to the job long-poll responder.</summary>
        private sealed class JobWaitState
        {
            public HttpListenerContext Context;
            public string Path;
            public string RawQuery;
            public string JobId;
            public double WaitSeconds;
            public int Generation;
            public string RequestId;
            public string AgentId;
        }

        /// <summary>One status probe's outcome. Answered=false: no answer from the main thread within the probe's budget.</summary>
        internal struct JobProbeResult
        {
            public bool Answered;
            public int StatusCode;
            public string Json;
        }

        internal enum JobWaitVerdict
        {
            /// <summary>A snapshot reported terminal:true.</summary>
            Terminal,
            /// <summary>The wait ran out while the job was still going; Last is the newest snapshot.</summary>
            TimedOut,
            /// <summary>The server started stopping or a domain reload became pending.</summary>
            Aborted,
            /// <summary>A non-200 answer (unknown job, a 503 from a stopping queue), returned as-is.</summary>
            Passthrough,
            /// <summary>Not even the first probe was answered.</summary>
            NoAnswer,
        }

        internal struct JobWaitOutcome
        {
            public JobWaitVerdict Verdict;
            public JobProbeResult Last;
            public int Probes;
        }

        /// <summary>
        /// Recognizes GET /jobs/{id}?wait=&lt;seconds&gt;: exactly one path segment after /jobs/ and a non-blank wait value.
        /// Anything else returns false and keeps today's single-snapshot path (including /jobs/{id}/progress and /logs).
        /// A wait that is not a non-negative number sets errorJson (400 TYPE_MISMATCH); larger values are clamped to
        /// <see cref="JobWaitMaxSeconds"/>. Pure string work, safe on the listener thread.
        /// </summary>
        internal static bool TryParseJobWait(string path, string rawQuery, out string jobId, out double waitSeconds, out string errorJson)
        {
            jobId = null;
            waitSeconds = 0;
            errorJson = null;

            const string prefix = "/jobs/";
            if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(rawQuery) || rawQuery.IndexOf("wait", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string remainder = path.Substring(prefix.Length).TrimEnd('/');
            if (remainder.Length == 0 || remainder.IndexOf('/') >= 0)
                return false;

            var qs = SkillRouter.ParseQueryString(rawQuery);
            if (!qs.TryGetValue("wait", out var raw) || string.IsNullOrWhiteSpace(raw))
                return false;

            jobId = remainder;
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) ||
                double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0)
            {
                errorJson = SkillErrorResponse.Build(
                    SkillErrorCode.TypeMismatch,
                    $"Invalid 'wait' value '{raw}' — expected seconds as a non-negative number (at most {JobWaitMaxSeconds}).",
                    details: new
                    {
                        parameter = "wait",
                        received = raw,
                        validRange = $"0-{JobWaitMaxSeconds}",
                        hint = "GET /jobs/{id}?wait=90 holds the request until the job is terminal or 90 seconds pass; omit 'wait' for an immediate snapshot.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return true;
            }

            waitSeconds = Math.Min(parsed, JobWaitMaxSeconds);
            return true;
        }

        /// <summary>
        /// The long-poll loop behind GET /jobs/{id}?wait=, kept free of sockets and queues so tests can drive it with
        /// fakes. probe(budgetMs) requests one snapshot and waits at most budgetMs for it; shouldAbort reports a stopping
        /// server or a pending domain reload; sleep(ms) pauses between probes (the real one returns early on abort).
        /// Ends on a terminal snapshot, a non-200 answer, an abort, or the deadline, whichever comes first; wait=0 is a
        /// single probe. Only one probe is ever outstanding: an unanswered probe is waited on, never duplicated.
        /// </summary>
        internal static JobWaitOutcome RunJobWaitLoop(double waitSeconds, int pollIntervalMs,
            Func<int, JobProbeResult> probe, Func<bool> shouldAbort, Func<long> utcNowTicks, Action<int> sleep)
        {
            long deadline = utcNowTicks() + (long)(Math.Max(0, waitSeconds) * TimeSpan.TicksPerSecond);
            var outcome = new JobWaitOutcome();

            while (true)
            {
                if (shouldAbort())
                {
                    outcome.Verdict = JobWaitVerdict.Aborted;
                    return outcome;
                }

                long remainingMs = (deadline - utcNowTicks()) / TimeSpan.TicksPerMillisecond;
                long budgetMs = outcome.Last.Answered ? remainingMs : Math.Max(remainingMs, JobWaitFirstAnswerFloorMs);
                if (budgetMs <= 0)
                {
                    outcome.Verdict = JobWaitVerdict.TimedOut;
                    return outcome;
                }

                var answer = probe((int)Math.Min(budgetMs, int.MaxValue));
                outcome.Probes++;

                if (!answer.Answered)
                {
                    outcome.Verdict = shouldAbort() ? JobWaitVerdict.Aborted
                        : outcome.Last.Answered ? JobWaitVerdict.TimedOut
                        : JobWaitVerdict.NoAnswer;
                    return outcome;
                }

                if (answer.StatusCode != 200)
                {
                    // A stopping server fails queued probes with 503; answer that with the reload hint instead.
                    outcome.Verdict = shouldAbort() ? JobWaitVerdict.Aborted : JobWaitVerdict.Passthrough;
                    if (outcome.Verdict == JobWaitVerdict.Passthrough)
                        outcome.Last = answer;
                    return outcome;
                }

                outcome.Last = answer;
                if (IsTerminalJobSnapshot(answer.Json))
                {
                    outcome.Verdict = JobWaitVerdict.Terminal;
                    return outcome;
                }

                if (shouldAbort())
                {
                    outcome.Verdict = JobWaitVerdict.Aborted;
                    return outcome;
                }

                remainingMs = (deadline - utcNowTicks()) / TimeSpan.TicksPerMillisecond;
                if (remainingMs <= 0)
                {
                    outcome.Verdict = JobWaitVerdict.TimedOut;
                    return outcome;
                }

                sleep((int)Math.Min(pollIntervalMs, remainingMs));
            }
        }

        private static bool IsTerminalJobSnapshot(string json)
        {
            if (string.IsNullOrEmpty(json))
                return false;
            try
            {
                var terminal = JObject.Parse(json)["terminal"];
                return terminal != null && terminal.Type == JTokenType.Boolean && terminal.Value<bool>();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Turns a loop outcome into the HTTP answer. Terminal and TimedOut return the GET /jobs/{id} snapshot plus
        /// waitTimedOut (a terminal snapshot also carries resultData, added by the probe); Passthrough returns the answer
        /// untouched; Aborted is a 503 wait_and_retry telling the caller to repeat the same URL, since jobs outlive a
        /// domain reload; NoAnswer is a 504 like any request the main thread never reached.
        /// </summary>
        internal static (int StatusCode, string Json) BuildJobWaitResponse(JobWaitOutcome outcome, double waitSeconds,
            string jobId, bool domainReloadPending)
        {
            switch (outcome.Verdict)
            {
                case JobWaitVerdict.Terminal:
                case JobWaitVerdict.TimedOut:
                {
                    JObject snapshot;
                    try { snapshot = JObject.Parse(outcome.Last.Json); }
                    catch (Exception) { return (200, outcome.Last.Json); }

                    bool timedOut = outcome.Verdict == JobWaitVerdict.TimedOut;
                    snapshot["waitTimedOut"] = timedOut;
                    if (timedOut)
                        snapshot["hint"] = $"Job not finished within wait={FormatSeconds(waitSeconds)}s; GET the same URL again to keep waiting.";
                    return (200, snapshot.ToString(Formatting.None));
                }
                case JobWaitVerdict.Passthrough:
                    return (outcome.Last.StatusCode, outcome.Last.Json);
                case JobWaitVerdict.Aborted:
                    return (503, SkillErrorResponse.Build(
                        domainReloadPending ? SkillErrorCode.Compiling : SkillErrorCode.ServerStopped,
                        "Server reloading; retry the same URL — the job survives the domain reload.",
                        details: new
                        {
                            jobId,
                            domainReloadPending,
                            hint = "server reloading; retry the same URL",
                        },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                        retryAfterSeconds: domainReloadPending ? 5 : 3));
                default:
                    return (504, SkillErrorResponse.Build(
                        SkillErrorCode.Timeout,
                        $"Main thread did not answer the status probe for job {jobId} within the {FormatSeconds(waitSeconds)}s wait",
                        details: new
                        {
                            jobId,
                            domainReloadPending,
                            suggestion = "Unity Editor may be paused, showing a modal dialog, or processing a long operation. Retry the same URL.",
                        },
                        retryStrategy: SkillErrorResponse.RetryWaitAndRetry,
                        retryAfterSeconds: 5));
            }
        }

        private static string FormatSeconds(double seconds) =>
            seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>True once the listener a long-poll arrived on has stopped (even if a new one started) or a reload is pending.</summary>
        private static bool ShouldAbortJobWait(int generation) =>
            !_isRunning || _domainReloadPending || Volatile.Read(ref _listenerGeneration) != generation;

        private static void SleepUnlessJobWaitAborted(int generation, int milliseconds)
        {
            long deadline = DateTime.UtcNow.Ticks + milliseconds * TimeSpan.TicksPerMillisecond;
            while (!ShouldAbortJobWait(generation))
            {
                long remainingMs = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (remainingMs <= 0)
                    return;
                Thread.Sleep((int)Math.Min(JobWaitSliceMs, remainingMs));
            }
        }

        /// <summary>
        /// Queues one GET /jobs/{id} probe on the light lane (the same main-thread handler as a plain request) and waits
        /// at most budgetMs for it, in short slices so a stop or pending reload ends the wait at once. A probe is never
        /// pooled: one left unanswered may still be processed after this responder has moved on.
        /// </summary>
        private static JobProbeResult ProbeJobSnapshot(JobWaitState wait, int budgetMs)
        {
            long deadline = DateTime.UtcNow.Ticks + budgetMs * TimeSpan.TicksPerMillisecond;

            // A full queue is waited out rather than failing the long-poll.
            while (QueuedRequests >= MaxQueuedRequests)
            {
                if (ShouldAbortJobWait(wait.Generation) || DateTime.UtcNow.Ticks >= deadline)
                    return default;
                Thread.Sleep(JobWaitSliceMs);
            }

            var probe = new RequestJob();
            probe.Prepare(null, "GET", wait.Path, string.Empty, wait.RequestId, wait.AgentId, wait.RawQuery);
            probe.IsInternalProbe = true;
            Interlocked.Increment(ref _lightQueued);
            _lightQueue.Enqueue(probe);

            while (true)
            {
                long remainingMs = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (remainingMs <= 0)
                    return default;
                if (probe.CompletionSignal.Wait((int)Math.Min(JobWaitSliceMs, remainingMs)))
                    return new JobProbeResult { Answered = true, StatusCode = probe.StatusCode, Json = probe.ResponseJson };
                if (ShouldAbortJobWait(wait.Generation))
                    return default;
            }
        }

        private static void JobWaitCallback(object state)
        {
            if (!(state is JobWaitState wait))
                return;

            try
            {
                RespondJobWait(wait.Context, wait.RequestId, wait.AgentId, () =>
                {
                    var outcome = RunJobWaitLoop(wait.WaitSeconds, JobWaitPollIntervalMs,
                        budgetMs => ProbeJobSnapshot(wait, budgetMs),
                        () => ShouldAbortJobWait(wait.Generation),
                        () => DateTime.UtcNow.Ticks,
                        milliseconds => SleepUnlessJobWaitAborted(wait.Generation, milliseconds));
                    var answer = BuildJobWaitResponse(outcome, wait.WaitSeconds, wait.JobId, _domainReloadPending);
                    Interlocked.Increment(ref _totalRequestsProcessed);
                    return answer;
                });
            }
            finally
            {
                ReleasePendingSlot();
            }
        }

        private const string InterruptedJobWaitRetryAfterSeconds = "2";

        /// <summary>
        /// Holds a long-poll open while <paramref name="waitForAnswer"/> runs, then writes its answer. A domain reload can abort
        /// this thread mid-wait, and whatever closes the connection then (the catch below, or the listener's own shutdown)
        /// sends the status already on the response. It is preset to 503 with Retry-After, so an interrupted wait reads as
        /// "retry the same URL" (curl --retry does) instead of an empty 200; the real answer replaces both, byte for byte as before.
        /// </summary>
        internal static void RespondJobWait(HttpListenerContext context, string requestId, string agentId,
            Func<(int StatusCode, string Json)> waitForAnswer)
        {
            try
            {
                var response = context.Response;
                response.StatusCode = 503;
                response.ContentType = "application/json; charset=utf-8";
                response.Headers.Set("Retry-After", InterruptedJobWaitRetryAfterSeconds);

                var (statusCode, json) = waitForAnswer();
                // Undo the preset so the real answer's headers come out exactly as before (same set, same order).
                response.Headers.Remove("Retry-After");
                response.ContentType = null;
                WriteRawJsonResponse(context, requestId, agentId, statusCode, json);
            }
            catch
            {
                // Client gone or listener closed mid-wait (domain reload): the preset 503 tells the caller to retry the same URL.
                CloseContextSafely(context);
            }
        }
    }
}

// Producer:Betsy
