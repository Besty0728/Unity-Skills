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
            // Wire value of DryRunPolicyService.Current ("off" / "highRisk" / "allWrites").
            public string DryRunPolicy;
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
            // Token-saving settings (see SkillsTokenLevel): whether Summary mode auto-truncates, the page size
            // it uses, and the derived preset name ("minimal"/"standard"/"full"/"maximum"/"custom").
            public bool SummaryAutoTruncate;
            public int SummaryPageSize;
            public string TokenLevel;

            /// <summary>HTTP-thread safe: reads only plain static fields, zero Unity API.</summary>
            public static HealthVitals FromSnapshot() => new HealthVitals
            {
                UnityVersion = _snapUnityVersion,
                InstanceId = _snapInstanceId,
                ProjectName = _snapProjectName,
                CurrentMode = _snapCurrentMode,
                PanelApprovalRequired = _snapPanelApprovalRequired,
                DryRunPolicy = _snapDryRunPolicy,
                SurfaceProfile = _snapSurfaceProfile,
                PendingCount = _snapPendingCount,
                AllowlistCount = _snapAllowlistCount,
                AutoRestart = _snapAutoStart,
                RequestTimeoutMinutes = _snapRequestTimeoutMinutes,
                IsCompiling = _snapIsCompiling,
                IsUpdating = _snapIsUpdating,
                SummaryAutoTruncate = _snapSummaryAutoTruncate,
                SummaryPageSize = _snapSummaryPageSize,
                TokenLevel = _snapTokenLevel,
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
                    DryRunPolicy = DryRunPolicyService.CurrentWire,
                    SurfaceProfile = SkillsSurfaceProfile.CurrentWire,
                    PendingCount = SkillsModeManager.PendingGrantRequests.Count,
                    AllowlistCount = SkillsModeManager.AllowlistSkills.Count,
                    // Fully qualified: inside this nested type, the fields below would shadow the outer class's same-named members.
                    AutoRestart = SkillsHttpServer.AutoStart,
                    RequestTimeoutMinutes = SkillsHttpServer.RequestTimeoutMinutes,
                    IsCompiling = EditorApplication.isCompiling,
                    IsUpdating = EditorApplication.isUpdating,
                    SummaryAutoTruncate = SkillRouter.SummaryAutoTruncate,
                    SummaryPageSize = SkillRouter.SummaryPageSize,
                    // Lowercase wire form, matching ModeToWire/CurrentWire's convention for other enum-backed fields.
                    TokenLevel = SkillsTokenLevel.Current.ToString().ToLowerInvariant(),
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
                _snapProjectPath = RegistryService.ProjectPath;
                _snapCurrentMode = vitals.CurrentMode;
                _snapPanelApprovalRequired = vitals.PanelApprovalRequired;
                _snapDryRunPolicy = vitals.DryRunPolicy;
                _snapSurfaceProfile = vitals.SurfaceProfile;
                _snapPendingCount = vitals.PendingCount;
                _snapAllowlistCount = vitals.AllowlistCount;
                _snapAutoStart = vitals.AutoRestart;
                _snapRequestTimeoutMinutes = vitals.RequestTimeoutMinutes;
                _snapIsCompiling = vitals.IsCompiling;
                _snapIsUpdating = vitals.IsUpdating;
                _snapSummaryAutoTruncate = vitals.SummaryAutoTruncate;
                _snapSummaryPageSize = vitals.SummaryPageSize;
                _snapTokenLevel = vitals.TokenLevel;
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
        /// Hooked onto <see cref="SkillsModeManager.OnChanged"/>, <see cref="SkillsSurfaceProfile.OnChanged"/>,
        /// <see cref="SkillRouter.SummarySettingsChanged"/> and <see cref="DryRunPolicyService.OnChanged"/>, so changes to mode /
        /// grants / allowlist / surface profile / summary settings / dryRun policy are reflected in /health immediately, instead of waiting out <see cref="HealthSnapshotInterval"/>.
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
                // Whether gated writes must be previewed first (off / highRisk / allWrites). Only the panel changes it.
                dryRunPolicy = v.DryRunPolicy,
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
                // Token-saving settings (SkillsTokenLevel): whether Summary mode auto-truncates arrays over
                // summaryPageSize, the page size itself, and the derived preset name. "custom" means the current
                // tuple of (surfaceProfile, summaryAutoTruncate, summaryPageSize) doesn't match any of the four presets.
                summaryAutoTruncate = v.SummaryAutoTruncate,
                summaryPageSize = v.SummaryPageSize,
                tokenLevel = v.TokenLevel,
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
                response.Headers.Add("Access-Control-Allow-Methods", CorsAllowMethods);
                response.Headers.Add("Access-Control-Allow-Headers", CorsAllowHeaders);
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("X-Request-Id", $"req_{Interlocked.Increment(ref _requestIdCounter):X8}");
                response.Headers.Add("X-Agent-Id", DetectAgent(request));
                AddInstanceHeaders(response);
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

        /// <summary>
        /// Recognizes the AI agent from the User-Agent or X-Agent-Id header.
        /// </summary>
        private static string DetectAgent(HttpListenerRequest request)
        {
            // Priority 1: explicit X-Agent-Id header, folded onto the process walk's spelling ("claude-code" ->
            // "ClaudeCode") so one agent doesn't split into two analytics rows; unknown ids pass through verbatim.
            var explicitId = request.Headers["X-Agent-Id"];
            if (!string.IsNullOrEmpty(explicitId))
                return ClientProcessResolver.CanonicalizeExplicitAgentId(explicitId);

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
    }
}

// Producer:Betsy
