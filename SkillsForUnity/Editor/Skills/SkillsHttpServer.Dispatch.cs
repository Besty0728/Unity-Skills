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
                            // Both editor launch and domain reload routinely land here when delayCall never fired: a normal
                            // start, not a recovery. Start() prints the outcome; a genuine failure trips the counter and LogError.
                            SkillsLogger.LogVerbose(shouldRun ? "[SafetyNet] Starting server (delayCall did not fire)" : "[SafetyNet] Starting server (editor launch)");
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
                if (!job.IsInternalProbe)
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
                job.ResponseJson = SkillRouter.GetMeta(SkillRouter.ResolveWireVersion(job.QueryString));
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
                // Early check: BeginResolve fired back at accept time, but if the main thread dequeued this job
                // almost instantly the background worker's ~8ms coalescing window + syscall round may not have
                // finished yet. A second, guaranteed-fresh check happens right before RecordSkillTelemetry below.
                RefineAgentId(job);
                // Marks this thread as executing inside a REST request for the duration of Execute (which is
                // where the permission gate writes its "call" audit entry): SkillsAuditLog.Append picks up the
                // ambient remotePort/fallback and late-binds the "agent" field at flush time, same rationale
                // as RecordSkillTelemetry below. DryRun/Plan don't run the gate, so no audit entry to tag either way.
                using (SkillsAuditLog.BeginRequestContext(job.RemotePort, job.AgentId, job.AgentIdIsExplicit))
                try
                {
                    job.StatusCode = 200;
                    switch (mode)
                    {
                        case SkillRouter.RequestMode.DryRun:
                            job.ResponseJson = SkillRouter.DryRun(skillName, job.Body, SkillRouter.ResolveWireVersion(job.QueryString), issuePolicyToken: true);
                            break;
                        case SkillRouter.RequestMode.Plan:
                            job.ResponseJson = SkillRouter.Plan(skillName, job.Body);
                            break;
                        default:
                            var dryRunGate = new DryRunGateContext(
                                skillQs.TryGetValue(DryRunPolicyService.TokenQueryKey, out var dryRunToken) ? dryRunToken : null,
                                QueryWithout(job.QueryString, DryRunPolicyService.TokenQueryKey));
                            job.ResponseJson = SkillRouter.Execute(skillName, job.Body, captureDiff, dryRunGate);
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
                // Guaranteed-fresh recheck: the skill has now run (tens to hundreds of ms since accept), giving
                // the background resolver ample time to land its result. RefineAgentId is an idempotent, pure
                // cache lookup -- calling it twice costs nothing when the first call already resolved it.
                RefineAgentId(job);
                // Console line moved here (was inside the switch, right after Execute): that used job.AgentId
                // as of the *early* RefineAgentId at :2383, which for a fast (single-digit-ms) skill almost
                // always printed the header/UA guess (e.g. "curl") -- the resolver hadn't landed yet. This is
                // the one place a human actually watches in real time, so it must show the resolved value, not
                // a known-stale one; DryRun/Plan never ran the skill, so nothing to log here for them.
                if (mode == SkillRouter.RequestMode.Execute)
                    SkillsLogger.LogAgent(job.AgentId, skillName);
                RecordSkillTelemetry(mode, skillName, job.AgentId, job.ResponseJson, skillSw.ElapsedMilliseconds, job.RemotePort, job.AgentIdIsExplicit);
                return;
            }

            // GET on a skill: skills only run on POST. A registered name gets 405 with the exact POST it most likely
            // meant; an unknown name falls through to the 404 below, as before. Nothing is executed either way.
            if (job.HttpMethod == "GET" && path.StartsWith("/skill/", StringComparison.OrdinalIgnoreCase))
            {
                string getSkillName = path.Substring(7);
                if (getSkillName.Length > 0 && getSkillName.IndexOf('/') < 0 &&
                    SkillRouter.TryGetSkill(getSkillName, out var getSkill))
                {
                    job.StatusCode = 405;
                    job.AllowHeader = "POST";
                    job.ResponseJson = BuildMethodNotAllowedResponse(getSkill.Name, job.QueryString, getSkill.Parameters, _port);
                    return;
                }
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
                        "GET /jobs/{id}?wait=<seconds>",
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
        /// Re-checks ClientProcessResolver's cache and, on a hit, replaces job.AgentId with the resolved
        /// process-chain identity -- a pure, idempotent dictionary lookup, safe to call repeatedly right before
        /// every audit/telemetry write. Never overwrites an explicit X-Agent-Id header.
        /// </summary>
        private static void RefineAgentId(RequestJob job)
        {
            if (job.AgentIdIsExplicit || job.RemotePort <= 0)
                return;
            if (ClientProcessResolver.TryGetAgentId(job.RemotePort, out var resolved))
                job.AgentId = resolved;
        }

        /// <summary>
        /// Records the result of a POST /skill/{name} call into <see cref="SkillTelemetryService"/>. Determines ok
        /// and extracts errorCode with a lightweight string probe (not JObject.Parse — this is the single-skill hot path).
        /// Fully isolated: a telemetry failure must never alter the business response already computed by the caller.
        /// </summary>
        private static void RecordSkillTelemetry(SkillRouter.RequestMode mode, string skillName, string agentId, string responseJson, long durationMs, int remotePort = -1, bool agentIdIsExplicit = false)
        {
            try
            {
                string modeStr = mode == SkillRouter.RequestMode.DryRun ? "dryRun"
                               : mode == SkillRouter.RequestMode.Plan ? "plan"
                               : "execute";
                ProbeOutcome(responseJson, mode == SkillRouter.RequestMode.DryRun, out bool ok, out string errorCode);
                // agentId here is only the fallback (whatever RefineAgentId's early, possibly-too-soon check
                // produced) -- SkillTelemetryService re-checks ClientProcessResolver again at flush time,
                // ~200ms later, which is what actually catches most single-digit-ms skill calls.
                SkillTelemetryService.Record(skillName, agentId, modeStr, ok, errorCode, durationMs, remotePort, agentIdIsExplicit);
            }
            catch { /* telemetry is best-effort — never surface to the caller */ }
        }

        /// <summary>
        /// Records the result of one step in /skills/batch. The batch loop already holds each step's parsed payload,
        /// so ok/errorCode are passed directly (no string probe needed). A null/blank skill name (malformed step) is
        /// recorded as "(malformed)". mode is batch_step or batch_step_dryRun, depending on the dryRun flag.
        /// remotePort/agentIdIsExplicit are forwarded to SkillTelemetryService.Record for the same flush-time
        /// late-binding as the single-skill path (see RecordSkillTelemetry).
        /// </summary>
        private static void RecordBatchStep(string skillName, string agentId, bool dryRun, bool ok, string errorCode, long durationMs, int remotePort = -1, bool agentIdIsExplicit = false)
        {
            try
            {
                SkillTelemetryService.Record(
                    string.IsNullOrWhiteSpace(skillName) ? "(malformed)" : skillName,
                    agentId,
                    dryRun ? "batch_step_dryRun" : "batch_step",
                    ok, errorCode, durationMs, remotePort, agentIdIsExplicit);
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
    }
}

// Producer:Betsy
