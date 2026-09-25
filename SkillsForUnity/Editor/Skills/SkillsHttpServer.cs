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
    public static partial class SkillsHttpServer
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

        // CORS response headers, shared by every responder. The two X-Expect-* request headers carry the caller's
        // expected instance (see ReadInstanceExpectation), so a browser client must be allowed to send them.
        private const string CorsAllowMethods = "GET, POST, OPTIONS";
        private const string CorsAllowHeaders = "Content-Type, X-Agent-Id, X-Expect-Instance, X-Expect-Project";
        private static readonly ConcurrentBag<RequestJob> _requestJobPool = new ConcurrentBag<RequestJob>();
        private static int _poolSize;

        // Admission-rate-limit on the listener thread, to keep the queue and threads from blowing up.
        private static int _admittedThisSecond = 0;
        private static long _lastAdmissionResetTicks = 0;
        
        // Keep-alive polling interval (ms) for checking pending jobs.
        private const int KeepAlivePollingMs = 50;

        // Interval for unconditionally waking the main thread; configurable.
        private const string PrefKeyKeepAliveInterval = "UnitySkills_KeepAliveIntervalSeconds";

        // Cached copy of KeepAliveIntervalSeconds (EditorPrefs can only be read on the main thread). Written on the main
        // thread and read on the keep-alive thread, so every access goes through Volatile (a long can't be volatile).
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
                Volatile.Write(ref _cachedKeepAliveIntervalTicks, (long)Mathf.Max(1, value) * TimeSpan.TicksPerSecond);
            }
        }
        // Request processing timeout — cached for thread safety (EditorPrefs can only be read on the main thread);
        // volatile because the main thread refreshes it while listener and keep-alive threads read it.
        private static volatile int _cachedTimeoutMs = 15 * 60 * 1000;
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
        // Not on /health; read by the listener's expected-instance check (project folder name) and its mismatch payload.
        private static volatile string _snapProjectPath;
        private static volatile string _snapCurrentMode;
        private static volatile bool _snapPanelApprovalRequired;
        private static volatile string _snapDryRunPolicy = DryRunPolicyService.WireOff;
        private static volatile string _snapSurfaceProfile = SkillsSurfaceProfile.WireFull;
        private static volatile int _snapPendingCount;
        private static volatile int _snapAllowlistCount;
        private static volatile bool _snapAutoStart = true;
        private static volatile int _snapRequestTimeoutMinutes = 15;
        private static volatile bool _snapIsCompiling;
        private static volatile bool _snapIsUpdating;
        // Token-saving settings, exposed on /health so a client can confirm its own summary/paging behavior
        // without a separate call. These live behind EditorPrefs the same as SurfaceProfile, so they're
        // refreshed on the same "expensive half" cadence.
        private static volatile bool _snapSummaryAutoTruncate;
        private static volatile int _snapSummaryPageSize = SkillRouter.DefaultSummaryPageSize;
        private static volatile string _snapTokenLevel;
        // Before the first full refresh has landed, the fast path always bails out and /health falls back to the
        // main-thread queue instead of reporting placeholder values.
        private static volatile bool _snapReady;
        // Set from any thread by the SkillsModeManager.OnChanged / SkillsSurfaceProfile.OnChanged / SkillRouter.SummarySettingsChanged
        // hooks, consumed on the next main-thread frame. A flag rather than an in-place refresh is used so that every Unity API read
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
        // compressing (summary ~180KB, full schema ~707KB). Keyed by ETag, a content hash, so entries self-invalidate:
        // content changes the key, and the old key is never requested again. Compression is pure CPU, never touches
        // the Unity API, so it's legitimate on the HTTP thread; the ~707KB pass takes tens of ms, only on a cache miss.
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

        // Bumped by every Stop(). A GET /jobs/{id}?wait= responder remembers the value it was accepted under, so a stop
        // that is immediately followed by a restart (the watchdog) still ends its wait.
        private static int _listenerGeneration;

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

        private static long _requestIdCounter = 0;

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

            // Stop the process-chain resolver's background worker before the domain unloads. Its cache is lost,
            // which is fine (cheap to rebuild) -- what matters is not leaving a dangling background thread.
            ClientProcessResolver.Shutdown();

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
                // Keep the registry entry (port, pid) and mark it reloading: a client in this project waits for the same
                // port instead of falling back to another Editor. The restart's Register flips it back to running.
                RegistryService.MarkReloading(_port);
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
            // Stop() only marks the entry stopped; a closing Editor must disappear from the registry. RegistryService
            // hooks quitting too, but that must not depend on the order the two static constructors subscribed in.
            RegistryService.Unregister();
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
                SkillsLogger.LogVerbose($"Auto-starting server ({reason}, port={restorePort}, attempt {_restoreRetryCount + 1}/{MaxRestoreRetries + 1})...");
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
                        // The entry was left reloading by OnBeforeAssemblyReload; nothing is coming back on that port for now.
                        RegistryService.MarkStopped(0);
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
                Volatile.Write(ref _cachedKeepAliveIntervalTicks, (long)KeepAliveIntervalSeconds * TimeSpan.TicksPerSecond);

                // Port probing: 8090 -> 8100
                int startPort = 8090;
                int endPort = 8100;
                bool started = false;
                bool preferredPortBusy = false;

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
                        preferredPortBusy = true;
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
                    SkillRouter.SummarySettingsChanged += OnPermissionStateChanged;
                    DryRunPolicyService.OnChanged += OnPermissionStateChanged;
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
                // The one line a normal start prints. Port fallback is the only startup condition worth a warning:
                // the user's preferred port was taken and clients pointed at it will not find this instance.
                if (preferredPortBusy)
                    SkillsLogger.LogWarning($"Port {preferredPort} is in use, started on {_port} instead");
                SkillsLogger.Log($"REST Server started at {_prefix} · {skillCount} skills · {RegistryService.InstanceId}");
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
            Interlocked.Increment(ref _listenerGeneration);

            // Clear the auto-restart flag on a permanent stop
            if (permanent)
            {
                EditorPrefs.SetBool(PREF_SERVER_SHOULD_RUN, false);
                EditorPrefs.SetInt(PREF_CONSECUTIVE_FAILURES, 0);
            }

            // A permanent stop leaves the global registry; any other stop (watchdog restart, editor quit before its own
            // unregister) keeps the entry and marks it stopped, since this Editor may serve again.
            if (permanent)
                RegistryService.Unregister();
            else
                RegistryService.MarkStopped(_port);

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
                        long intervalTicks = Volatile.Read(ref _cachedKeepAliveIntervalTicks);
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

        // ===== GET /skill/{name} → 405 with the POST rewrite =====

        /// <summary>
        /// The request-level query keys, the same for POST /skill/{name} and POST /skills/batch: they stay in the URL of a
        /// GET→POST rewrite instead of moving into the body, and they are the only query keys /skills/batch accepts.
        /// One list, so the two endpoints can't drift apart.
        /// </summary>
        private static readonly string[] RequestLevelQueryKeys =
            { "mode", "dryRun", "diff", "wire", DryRunPolicyService.TokenQueryKey, ExpectInstanceQueryKey, ExpectProjectQueryKey };

        /// <summary>
        /// The full set of top-level request-body keys POST /skills/batch recognizes, and the full set of query
        /// keys it reads (see TryResolveBatchRequestMode, TryResolveDiff, the per-step dryRun wire format, and the
        /// listener's expected-instance check).
        /// Anything else is rejected rather than ignored: a silently-dropped key is exactly what causes an agent
        /// to believe it requested a preview, or requested async execution, and got neither.
        /// </summary>
        private static readonly string[] BatchBodyParams = { "steps", "params", "continueOnError", "dryRun", "mode" };
    }
}

// Producer:Betsy
