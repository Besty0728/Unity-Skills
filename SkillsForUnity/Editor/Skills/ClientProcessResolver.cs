using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Identifies which AI agent CLI is behind an HTTP request by walking the client process's parent chain,
    /// for the (common) case where the agent shells out to curl/a Python script instead of going through
    /// unity_skills.py -- the request then carries no X-Agent-Id header and a generic User-Agent, so
    /// SkillsHttpServer.DetectAgent alone always reports "curl"/"Unknown".
    ///
    /// Threading model (relaxed from the usual "accept thread never blocks" rule -- see agent.md and the
    /// project-lead decision recorded in <see cref="BeginResolve"/>'s doc comment): the HTTP accept thread
    /// calls <see cref="BeginResolve"/>, which *synchronously* resolves the client's TCP port to its pid and
    /// that pid's own parent pid (a few ms, no-fork syscalls only), then hands the *rest* of the parent chain
    /// (long-lived ancestors -- shells, interpreters, the agent CLI) to a single dedicated background thread.
    /// The synchronous part exists because a short-lived, one-shot client process (bare `curl`, no keep-alive)
    /// can fully exit and be reaped before any purely-async worker gets a chance to look it up -- once it's
    /// gone, its ppid is unrecoverable. Results land in a short-TTL cache that any thread can read back via
    /// <see cref="TryGetAgentId"/> -- in particular, the main thread re-checks it right before writing
    /// audit/telemetry (well after the skill has executed), as a second safety net on top of the synchronous capture.
    ///
    /// Approach: a denylist, not an allowlist. Walking up from the client pid, any ancestor whose name is a
    /// shell/terminal-host/system-process/curl-like tool is skipped; the first ancestor NOT on that list is
    /// reported as the agent. This means a brand-new agent CLI nobody has heard of is still correctly
    /// identified without a code change -- only the (small, stable) exclusion set needs maintaining, not a
    /// growing list of every agent that exists. Node/Python-hosted CLIs are special-cased: the interpreter's
    /// own process name (e.g. "node") is useless, so its command line is inspected instead to find the actual
    /// script/package.
    ///
    /// Every step degrades silently: an unsupported OS, a failed syscall, a permission error, or exhausting
    /// the parent chain without leaving the denylist all just mean "no answer" -- the caller keeps whatever
    /// DetectAgent (header/User-Agent) already produced. This is a best-effort analytics enrichment, never a
    /// correctness dependency.
    /// </summary>
    public static class ClientProcessResolver
    {
        // ===== Configuration =====

        private static string _prefEnabled;

        // Same key pattern as SkillsHttpServer/SkillInstallSyncService: includes InstanceId, so multiple
        // projects on one machine don't stomp on each other's EditorPrefs.
        internal static string PrefEnabled =>
            _prefEnabled ??= $"UnitySkills_{RegistryService.InstanceId}_ProcessAgentDetection";

        // Read by any thread (including the accept thread and the background worker) without ever touching
        // EditorPrefs off the main thread. Warmed from EditorPrefs on domain reload; the setter (main-thread
        // only -- there's no UI for it yet, see agent.md task notes) keeps it in sync.
        private static volatile bool _enabledCache = true;

        [InitializeOnLoadMethod]
        private static void WarmEnabledCache()
        {
            try { _enabledCache = EditorPrefs.GetBool(PrefEnabled, true); }
            catch { _enabledCache = true; }
        }

        /// <summary>Whether process-chain agent attribution runs. Defaults to on. Get is thread-safe; set is main-thread only.</summary>
        public static bool Enabled
        {
            get => _enabledCache;
            set
            {
                try { EditorPrefs.SetBool(PrefEnabled, value); } catch { /* best-effort */ }
                _enabledCache = value;
            }
        }

        // ===== Cache =====

        /// <summary>
        /// A tiny fixed-capacity, TTL'd, FIFO-evicted key/value cache. Pulled out as its own class (rather than
        /// inlined dictionary+queue pairs) so tests can exercise TTL expiry and eviction against a throwaway
        /// instance with an injected clock, instead of reaching into ClientProcessResolver's shared production
        /// caches (which a real, concurrently-running server may be actively reading/writing).
        /// </summary>
        internal sealed class TtlCache
        {
            private struct Entry
            {
                public string Value;
                public long ExpiresAtTicks;
            }

            private readonly ConcurrentDictionary<int, Entry> _map = new ConcurrentDictionary<int, Entry>();
            private readonly ConcurrentQueue<int> _insertionOrder = new ConcurrentQueue<int>();
            private readonly int _cap;
            private readonly long _ttlTicks;

            /// <summary>Test hook: defaults to the real clock. Swap to a fake for deterministic TTL-expiry tests.</summary>
            internal Func<long> NowTicks = () => DateTime.UtcNow.Ticks;

            public TtlCache(int cap, int ttlSeconds)
            {
                _cap = cap;
                _ttlTicks = TimeSpan.FromSeconds(ttlSeconds).Ticks;
            }

            internal int Count => _map.Count;

            public void Put(int key, string value)
            {
                var entry = new Entry { Value = value, ExpiresAtTicks = NowTicks() + _ttlTicks };
                if (_map.TryAdd(key, entry))
                {
                    _insertionOrder.Enqueue(key);
                    while (_map.Count > _cap && _insertionOrder.TryDequeue(out var oldest))
                        _map.TryRemove(oldest, out _);
                }
                else
                {
                    _map[key] = entry; // refresh value/expiry in place; eviction order keeps the original insertion slot
                }
            }

            public bool TryGet(int key, out string value)
            {
                if (_map.TryGetValue(key, out var entry) && entry.ExpiresAtTicks > NowTicks())
                {
                    value = entry.Value;
                    return true;
                }
                value = null;
                _map.TryRemove(key, out _); // lazy cleanup; harmless if another thread already removed/replaced it
                return false;
            }
        }

        private const int CacheTtlSeconds = 60;
        private const int PortCacheCap = 256;
        private const int PidCacheCap = 128;

        private static readonly TtlCache _portCache = new TtlCache(PortCacheCap, CacheTtlSeconds);

        // Backfilled with every pid walked on a successful resolution (not just the leaf), so a recurring
        // intermediate ancestor (e.g. the same long-lived agent process spawning many short curl calls) or a
        // recurring leaf (a client that reconnects with a new port each time but is the same OS process)
        // resolves instantly next time, without a fresh syscall round or -- on Windows -- a repeat PEB read.
        // Keyed by pid alone (no start-time): the 60s TTL already bounds PID-reuse risk to a small window, and
        // this is a best-effort analytics field, not worth extra P/Invoke plumbing to close that window entirely.
        private static readonly TtlCache _pidCache = new TtlCache(PidCacheCap, CacheTtlSeconds);

        /// <summary>Non-blocking cache lookup, safe from any thread. Used both as an early hint and as the final attribution check right before a telemetry/audit write.</summary>
        public static bool TryGetAgentId(int remotePort, out string agentId) => _portCache.TryGet(remotePort, out agentId);

        // ===== Background worker =====

        // A short window to batch near-simultaneous BeginResolve async-continuation calls into one round --
        // matters most for the ps fallback (a real fork per round); the primary libproc ancestor-table read
        // is fast enough that this barely matters either way. Only affects the *async* remainder of the walk
        // (ancestors, which are long-lived) -- the time-critical leaf capture is synchronous, see BeginResolve.
        private const int CoalesceDelayMs = 3;
        private const int IdleSweepMs = 1000;    // periodic wake even without new work, so Shutdown is noticed promptly
        internal const int MaxWalkDepth = 8;

        /// <summary>An async continuation queued by BeginResolve after it has already (synchronously) captured the leaf pid and its immediate parent.</summary>
        private readonly struct PendingWalk
        {
            public readonly int LeafPid;
            public readonly int LeafPpid;
            public PendingWalk(int leafPid, int leafPpid) { LeafPid = leafPid; LeafPpid = leafPpid; }
        }

        private static readonly ConcurrentDictionary<int, PendingWalk> _pendingWalks = new ConcurrentDictionary<int, PendingWalk>();
        private static readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private static Thread _worker;
        private static readonly object _workerLock = new object();
        private static volatile bool _shutdown;

        /// <summary>
        /// Accept-thread entry point. Synchronously resolves remotePort -> client pid -> that pid's own
        /// ppid+name, and -- if that's not already enough to classify the agent -- queues the *rest* of the
        /// ancestor chain (long-lived: shells, interpreters, the actual agent CLI) to be walked asynchronously.
        ///
        /// The synchronous part is not optional: a short-lived, one-shot client process (bare `curl`, no
        /// keep-alive) can fully exit and be reaped within single-digit ms, well before an async background
        /// worker (even one with zero coalescing delay) would get to read the connection/process tables. Once
        /// the process is gone, its ppid is unrecoverable -- there's no "try again later." So remotePort -> pid
        /// -> ppid has to happen right here, before returning to the caller.
        ///
        /// Bounded to the no-fork platform readers only (empirically ~4-5ms worst case for the connection scan
        /// on the dev machine, plus one or two syscalls for the leaf's own info) -- IConnectionTableReader/
        /// IProcessTableReader's sync methods never fall back to spawning lsof/ps (that would cost 50-90ms of
        /// fork overhead from Unity Editor's own large address space, blowing the accept thread's budget many
        /// times over). Any failure or unavailability degrades immediately and silently to the header/UA guess.
        /// </summary>
        public static void BeginResolve(int remotePort, int serverPort)
        {
            if (!_enabledCache || remotePort <= 0 || serverPort <= 0)
                return;
            if (_portCache.TryGet(remotePort, out _))
                return; // keep-alive reuse (e.g. a Python client's requests.Session) -- already known

            try
            {
                // Timed at Verbose level only -- silent by default, but the number that matters if this ever
                // needs re-diagnosing: empirically 1-4ms end to end on the dev machine (mac, libproc), well
                // under the accept thread's relaxed-but-still-real budget (see class doc).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (!ConnectionReader.TryFindClientPid(remotePort, serverPort, out int leafPid))
                {
                    SkillsLogger.LogVerbose($"ClientProcessResolver: sync connection lookup miss after {sw.ElapsedMilliseconds}ms");
                    return; // connection already closed, or platform unsupported -- no retry, keep the header/UA guess
                }
                if (leafPid == CurrentProcessId)
                {
                    _portCache.Put(remotePort, SelfTestAgentId);
                    return;
                }
                if (_pidCache.TryGet(leafPid, out var cachedLeaf))
                {
                    _portCache.Put(remotePort, cachedLeaf);
                    return;
                }

                if (!ProcessReader.TryGetSingle(leafPid, out var leafInfo))
                {
                    SkillsLogger.LogVerbose($"ClientProcessResolver: sync single-pid lookup miss after {sw.ElapsedMilliseconds}ms");
                    return; // pid exited between the two syscalls, or platform unsupported
                }

                // A one-entry table makes WalkChain evaluate exactly the leaf (self-pid/pid-cache/interpreter/
                // denylist checks, all already-tested logic) and naturally return null the moment it needs an
                // ancestor this table doesn't have -- no separate leaf-classification logic to duplicate or drift.
                var leafOnlyTable = new Dictionary<int, ProcessInfo> { [leafPid] = leafInfo };
                var immediate = WalkChain(leafPid, leafOnlyTable, ProcessReader.TryGetCommandLine, out var visited, _pidCache.TryGet);
                if (immediate != null)
                {
                    _portCache.Put(remotePort, immediate);
                    foreach (var vp in visited) _pidCache.Put(vp, immediate);
                    SkillsLogger.LogVerbose($"ClientProcessResolver: sync capture resolved '{immediate}' in {sw.ElapsedMilliseconds}ms");
                    return;
                }

                // Leaf alone didn't resolve (denylisted, or an interpreter whose args didn't extract) -- the
                // rest of the chain is long-lived and safe to walk asynchronously without racing an exit.
                if (leafInfo.Ppid <= 0 || leafInfo.Ppid == leafPid)
                    return; // no parent to continue to -- dead end

                SkillsLogger.LogVerbose($"ClientProcessResolver: sync capture done in {sw.ElapsedMilliseconds}ms, queuing async continuation from ppid={leafInfo.Ppid}");
                if (_pendingWalks.TryAdd(remotePort, new PendingWalk(leafPid, leafInfo.Ppid)))
                {
                    EnsureWorkerStarted();
                    _wake.Set();
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose("ClientProcessResolver: synchronous leaf capture failed: " + ex.Message);
                // degrade silently -- caller keeps the header/UA fallback
            }
        }

        private static void EnsureWorkerStarted()
        {
            if (_worker != null && _worker.IsAlive)
                return;
            lock (_workerLock)
            {
                if (_worker != null && _worker.IsAlive)
                    return;
                _shutdown = false;
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "UnitySkills-ProcessResolver" };
                _worker.Start();
            }
        }

        private static void WorkerLoop()
        {
            while (!_shutdown)
            {
                _wake.WaitOne(IdleSweepMs);
                if (_shutdown) break;
                if (_pendingWalks.IsEmpty) continue;

                Thread.Sleep(CoalesceDelayMs);
                if (_shutdown) break;

                try { ResolveRound(); }
                catch { /* never let a bad round kill the worker; next round starts clean */ }
            }
        }

        /// <summary>
        /// Walks the *remainder* of each pending chain, from the leaf's already-captured ppid upward. No
        /// connection-table read here at all -- BeginResolve already did the only time-critical lookup
        /// synchronously; every pid this round touches is a long-lived ancestor, so the ps/Toolhelp32 fork
        /// fallback inside ProcessReader.ReadAll() is an acceptable cost here (unlike on the accept thread).
        /// </summary>
        private static void ResolveRound()
        {
            var pending = new List<KeyValuePair<int, PendingWalk>>(_pendingWalks);
            if (pending.Count == 0) return;

            IReadOnlyDictionary<int, ProcessInfo> processTable;
            try { processTable = ProcessReader.ReadAll(); }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose("ClientProcessResolver: process table read failed: " + ex.Message);
                processTable = null;
            }

            foreach (var kv in pending)
            {
                int remotePort = kv.Key;
                var walk = kv.Value;
                _pendingWalks.TryRemove(remotePort, out _);
                if (processTable == null) continue; // degrade -- caller keeps the header/UA fallback

                var agentId = WalkChain(walk.LeafPpid, processTable, ProcessReader.TryGetCommandLine, out var visitedPids, _pidCache.TryGet);
                if (agentId == null) continue;

                _portCache.Put(remotePort, agentId);
                _pidCache.Put(walk.LeafPid, agentId); // backfill the original leaf too, in case it recurs
                foreach (var vp in visitedPids)
                    _pidCache.Put(vp, agentId);
            }
        }

        /// <summary>Stops the background worker before a domain reload. Cache state is allowed to be lost -- it's cheap to rebuild.</summary>
        internal static void Shutdown()
        {
            _shutdown = true;
            _wake.Set();
            try { _worker?.Join(200); } catch { /* best-effort */ }
        }

        // ===== Chain walk =====

        /// <summary>Signature for a pid->agentId cache lookup, injected into <see cref="WalkChain"/> so tests can supply an isolated fake instead of touching the shared production pid cache.</summary>
        internal delegate bool PidLookup(int pid, out string agentId);

        /// <summary>
        /// Fixed identity for SkillsHttpServer's own loopback self-test probe (UA "UnitySkills-SelfTest"), which
        /// connects to the server FROM the Unity Editor process itself. Without this short-circuit, WalkChain
        /// would see the client pid == Unity's own pid, and -- since "Unity" isn't on the denylist -- report the
        /// agent as "Unity", which is misleading noise in analytics for a liveness probe that isn't an AI agent at all.
        /// </summary>
        internal const string SelfTestAgentId = "UnitySelfTest";

        /// <summary>The running Unity Editor's own pid, computed once. Exposed so WalkChain's optional override can be tested without touching this.</summary>
        internal static readonly int CurrentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>
        /// Walks from the client's own pid up its parent chain (denylist-driven, see class docs), returning the
        /// resolved display agentId, or null if the chain was exhausted (root/depth cap) without leaving the
        /// denylist -- callers must keep whatever header/User-Agent guess they already had in that case.
        /// <paramref name="pidCacheLookup"/> is optional; production passes the shared pid cache, tests can pass
        /// a fake (or omit it) to exercise the walk in isolation. <paramref name="selfPid"/> defaults to the real
        /// Unity process id; tests override it to exercise the self-test short-circuit without needing to fake the
        /// actual current pid.
        /// </summary>
        internal static string WalkChain(int leafPid, IReadOnlyDictionary<int, ProcessInfo> table, Func<int, string> commandLineFetcher, out List<int> visitedPids, PidLookup pidCacheLookup = null, int? selfPid = null)
        {
            visitedPids = new List<int>();

            if (leafPid == (selfPid ?? CurrentProcessId))
                return SelfTestAgentId;

            if (table == null) return null;

            var seen = new HashSet<int>();
            int current = leafPid;

            for (int depth = 0; depth < MaxWalkDepth; depth++)
            {
                if (!table.TryGetValue(current, out var info)) break;
                if (!seen.Add(current)) break; // cycle guard
                visitedPids.Add(current);

                // A previously-resolved ancestor (or the leaf itself, for a client that reconnects with a new
                // port each time but is the same long-lived process) short-circuits the rest of the walk.
                if (pidCacheLookup != null && pidCacheLookup(current, out var cachedAgent))
                    return cachedAgent;

                var name = NormalizeProcessName(info.Name);
                if (IsInterpreter(name))
                {
                    var args = info.Args ?? commandLineFetcher?.Invoke(current);
                    var extracted = TryExtractFromArgs(args);
                    // NormalizeDisplayName also sanitizes for telemetry -- a token that survives extraction but
                    // sanitizes down to nothing (garbage/non-ASCII process name) is treated the same as a failed
                    // extraction: keep climbing rather than reporting a hollow identity.
                    var normalized = extracted != null ? NormalizeDisplayName(extracted) : null;
                    if (normalized != null)
                        return normalized;
                }
                else if (!IsDenylisted(name))
                {
                    var normalized = NormalizeDisplayName(name);
                    if (normalized != null)
                        return normalized;
                    // Sanitize wiped an otherwise-plausible agent name -- inconclusive, not "found nothing at all"; keep climbing.
                }

                if (info.Ppid <= 0 || info.Ppid == current)
                    break;
                current = info.Ppid;
            }

            return null;
        }

        // ===== Denylist / interpreter tables =====

        // Small and stable by design -- shells, terminal hosts, system processes, and HTTP-ish launchers. This
        // is deliberately NOT a list of known agents: any CLI not on this list is assumed to be the agent, so a
        // brand-new tool is recognized without touching this file. Case-insensitive, matched after stripping
        // any path/extension/leading "-" (login shells report as "-zsh").
        private static readonly HashSet<string> _denylistExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // shells
            "sh", "bash", "zsh", "fish", "dash", "csh", "tcsh", "ksh", "cmd", "powershell", "pwsh", "conhost", "windowsterminal", "openconsole",
            // HTTP / process-launching tools
            "curl", "wget", "httpie", "http", "xargs", "env", "sudo", "timeout", "script",
            // terminal hosts
            "terminal", "iterm2", "alacritty", "kitty", "wezterm", "hyper", "login", "screen",
            // system
            "launchd", "init", "systemd", "explorer", "services", "svchost", "winlogon", "userinit",
            // build tools / package managers / process-launching middlemen -- npm/npx/yarn/pnpm in particular
            // routinely wrap the actual agent CLI (`npx @some/agent-cli`), so excluding them and continuing
            // upward/downward the chain is exactly what surfaces the real wrapped tool.
            "make", "cmake", "npm", "npx", "yarn", "pnpm", "git", "ssh", "sshd", "nohup", "direnv", "watchexec", "just", "task", "uv", "uvx", "pipx",
        };

        // Prefix-matched denylist entries (versioned/suffixed process names).
        private static readonly string[] _denylistPrefixes = { "itermserver", "tmux" };

        // Interpreters get special handling: their own process name is useless, so their command line is
        // inspected for a script/package path instead of being treated as a plain denylist skip.
        private static readonly HashSet<string> _interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node", "python", "python3", "deno", "bun", "dotnet", "java", "ruby", "perl", "electron",
        };

        internal static bool IsDenylisted(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_denylistExact.Contains(name)) return true;
            foreach (var prefix in _denylistPrefixes)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static bool IsInterpreter(string name) => !string.IsNullOrEmpty(name) && _interpreters.Contains(name);

        /// <summary>Strips a leading "-" (login shells), any directory path, and any extension, leaving a bare comparable name.</summary>
        internal static string NormalizeProcessName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            var name = rawName;
            if (name[0] == '-') name = name.Substring(1);
            name = name.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            var withoutExt = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(withoutExt) ? name : withoutExt;
        }

        // ===== Interpreter command-line extraction =====

        private static readonly char[] _pathSeparatorChars = { '/', '\\' };
        private static readonly string[] _scriptExtensions = { ".js", ".mjs", ".cjs", ".py" };
        private static readonly string[] _genericEntryNames = { "index", "main", "cli", "bin", "app", "start" };
        private const string NodeModulesMarker = "node_modules/";

        /// <summary>
        /// Finds the first non-flag, path-like argument in an interpreter's command line and extracts a raw
        /// tool/package token from it (handling scoped npm packages under node_modules), or null if no such
        /// argument exists -- e.g. `node -e "..."` or a bare REPL invocation.
        /// </summary>
        internal static string TryExtractFromArgs(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return null;
            var tokens = args.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            // tokens[0] is the interpreter binary itself (already identified via comm); start from the first argument.
            for (int i = 1; i < tokens.Length; i++)
            {
                var tok = tokens[i];
                if (tok.Length == 0 || tok[0] == '-') continue; // flag

                bool looksLikePath = tok.IndexOfAny(_pathSeparatorChars) >= 0 ||
                    _scriptExtensions.Any(ext => tok.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                if (!looksLikePath) continue;

                var normalized = tok.Replace('\\', '/');

                int nodeModulesIdx = normalized.IndexOf(NodeModulesMarker, StringComparison.OrdinalIgnoreCase);
                if (nodeModulesIdx >= 0)
                {
                    var rest = normalized.Substring(nodeModulesIdx + NodeModulesMarker.Length).TrimStart('/');
                    var segs = rest.Split('/');
                    if (segs.Length > 0 && segs[0].Length > 0)
                        return segs[0].StartsWith("@") && segs.Length > 1 ? segs[1] : segs[0];
                    continue; // malformed node_modules path -- try the next argument
                }

                int lastSlash = normalized.LastIndexOf('/');
                var fileName = lastSlash >= 0 ? normalized.Substring(lastSlash + 1) : normalized;
                var stem = Path.GetFileNameWithoutExtension(fileName);

                if (!string.IsNullOrEmpty(stem) && _genericEntryNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
                {
                    var dir = lastSlash >= 0 ? normalized.Substring(0, lastSlash) : "";
                    int parentSlash = dir.LastIndexOf('/');
                    var parentName = parentSlash >= 0 ? dir.Substring(parentSlash + 1) : dir;
                    if (!string.IsNullOrEmpty(parentName)) return parentName;
                }

                if (!string.IsNullOrEmpty(stem)) return stem;
            }

            return null;
        }

        // ===== Display-name normalization =====

        // Cosmetic only -- maps a raw process/package token to the exact agentId strings
        // SkillsHttpServer._agentKeywords already uses, so the same agent doesn't split into two rows in
        // analytics. An entry missing here is harmless: the agent is still correctly detected, just shown
        // under its raw (capitalized) name instead of a friendly one -- new agents need no code change here.
        internal static readonly (string token, string agentId)[] DisplayNameMap =
        {
            ("claude", "ClaudeCode"), ("claude-code", "ClaudeCode"),
            ("codex", "Codex"),
            ("cursor", "Cursor"),
            ("antigravity", "Antigravity"), ("agy", "Antigravity"), // agy = Antigravity CLI's actual binary name (Go/Mach-O, confirmed live)
            ("opencode", "OpenCode"),
            ("kimi", "KimiCode"), ("kimi-code", "KimiCode"),
            ("windsurf", "Windsurf"),
            ("trae", "Trae"),
            ("cline", "Cline"),
            ("augment", "Augment"), ("auggie", "Augment"), // auggie = Augment CLI's actual binary name
            ("q", "AmazonQ"), ("amazon-q", "AmazonQ"),
            ("code", "VSCode"),
            ("gemini", "GeminiCLI"),
            ("aider", "Aider"),
            ("amp", "Amp"),
            ("goose", "Goose"),
            ("droid", "Droid"),
            ("qwen", "QwenCode"),
        };

        internal static string NormalizeDisplayName(string rawToken)
        {
            if (string.IsNullOrEmpty(rawToken)) return null;
            foreach (var (token, agentId) in DisplayNameMap)
            {
                if (string.Equals(token, rawToken, StringComparison.OrdinalIgnoreCase))
                    return agentId; // hardcoded constants -- already telemetry-safe, no need to run through Sanitize
            }
            // Unmapped but real -- capitalize rather than ever reporting "Unknown".
            var capitalized = rawToken.Length == 1
                ? rawToken.ToUpperInvariant()
                : char.ToUpperInvariant(rawToken[0]) + rawToken.Substring(1);
            return SanitizeForTelemetry(capitalized);
        }

        private static readonly Regex UnsafeTelemetryChars = new Regex(@"[^A-Za-z0-9\-_.]", RegexOptions.Compiled);
        private const int MaxTelemetryNameLength = 32;

        /// <summary>
        /// This value lands in the audit/telemetry JSONL and the /analytics aggregation, both fed by real (if
        /// unusual) process names -- restrict to a safe character set and length before it's written anywhere,
        /// same spirit as sanitizing any other externally-influenced string before logging it. An all-unsafe
        /// input (non-ASCII garbage, pure punctuation) sanitizes to empty, which callers treat as "no usable
        /// identity found" rather than caching a blank agentId.
        /// </summary>
        internal static string SanitizeForTelemetry(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var cleaned = UnsafeTelemetryChars.Replace(value, "");
            if (cleaned.Length > MaxTelemetryNameLength)
                cleaned = cleaned.Substring(0, MaxTelemetryNameLength);
            return cleaned.Length == 0 ? null : cleaned;
        }

        // ===== Injectable platform layer =====

        internal struct ProcessInfo
        {
            public int Ppid;
            public string Name;
            // Full command line when cheaply available from the same batch read as the rest of the table
            // (mac/Linux); null on platforms where fetching it per-process is expensive (Windows), in which
            // case it's fetched lazily via IProcessTableReader.TryGetCommandLine only for interpreter hits.
            public string Args;
        }

        internal interface IConnectionTableReader
        {
            /// <summary>
            /// Synchronous, targeted lookup for exactly one client (remote) port -- called from the accept
            /// thread, so it must stay in the low single-digit ms. No-fork implementations only: never falls
            /// back to spawning lsof/ps (that would blow the accept-thread's budget). Returns false when
            /// unavailable (platform unsupported, syscall failure) or the port isn't found (connection
            /// already closed) -- either way the caller degrades straight to the header/UA guess, no retry.
            /// </summary>
            bool TryFindClientPid(int remotePort, int serverPort, out int pid);
        }

        internal interface IProcessTableReader
        {
            /// <summary>
            /// Snapshot of every process's parent pid + name (+ args, where cheaply available). Null on
            /// failure. Async-worker-only: a fork fallback (ps) is acceptable here, since by the time this
            /// runs the pids being looked up are long-lived ancestors, not the short-lived leaf.
            /// </summary>
            IReadOnlyDictionary<int, ProcessInfo> ReadAll();

            /// <summary>Lazy per-pid command-line fetch, for platforms where ReadAll() can't cheaply include it. Null on failure.</summary>
            string TryGetCommandLine(int pid);

            /// <summary>
            /// Synchronous, single-pid ppid+name lookup -- called from the accept thread for the leaf client
            /// pid only, so it must stay in the low single-digit ms. No-fork implementations only (same
            /// constraint as <see cref="IConnectionTableReader.TryFindClientPid"/>). Returns false when unavailable.
            /// </summary>
            bool TryGetSingle(int pid, out ProcessInfo info);
        }

        internal static IConnectionTableReader ConnectionReader = CreateDefaultConnectionReader();
        internal static IProcessTableReader ProcessReader = CreateDefaultProcessReader();

        private static IConnectionTableReader CreateDefaultConnectionReader()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsConnectionTableReader();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacLibProcConnectionTableReader();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxConnectionTableReader();
            }
            catch { /* fall through to the inert reader */ }
            return new NullConnectionTableReader();
        }

        private static IProcessTableReader CreateDefaultProcessReader()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return new WindowsProcessTableReader();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return new MacProcessTableReader();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return new LinuxProcessTableReader();
            }
            catch { /* fall through to the inert reader */ }
            return new NullProcessTableReader();
        }

        private sealed class NullConnectionTableReader : IConnectionTableReader
        {
            public bool TryFindClientPid(int remotePort, int serverPort, out int pid) { pid = 0; return false; }
        }

        private sealed class NullProcessTableReader : IProcessTableReader
        {
            public IReadOnlyDictionary<int, ProcessInfo> ReadAll() => null;
            public string TryGetCommandLine(int pid) => null;
            public bool TryGetSingle(int pid, out ProcessInfo info) { info = default; return false; }
        }


        // ===== macOS =====

        /// <summary>
        /// Tries the no-fork libproc path first (see <see cref="MacLibProcConnectionTableReader"/>); falls back
        /// to shelling out to lsof only if that throws or comes back null (e.g. a future macOS kernel changes
        /// the proc_info layout this depends on). The libproc path exists because forking Unity Editor's own
        /// (large, GB-scale) address space to spawn lsof measured at 50-90ms in this same process -- long
        /// enough that a short-lived, one-shot `curl` invocation (no keep-alive) can fully exit and be reaped
        /// before lsof ever gets to read its socket, even started immediately at accept time. libproc reads the
        /// kernel's per-process fd/socket info directly via syscalls, with no fork at all.
        /// </summary>
        /// <summary>
        /// Reads TCP socket ownership directly via libproc (proc_listpids + proc_pidinfo/PROC_PIDLISTFDS +
        /// proc_pidfdinfo/PROC_PIDFDSOCKETINFO) -- no subprocess, no fork, safe to call from the accept
        /// thread. Field offsets below were computed by hand from &lt;sys/proc_info.h&gt; (struct socket_fdinfo
        /// = proc_fileinfo[24 bytes] + socket_info; socket_info.soi_kind at +232, soi_proto union at +240;
        /// tcp_sockinfo starts with in_sockinfo, whose first two int fields are insi_fport/insi_lport) --
        /// giving absolute offsets 256/264/268 within socket_fdinfo. This is undocumented-ish kernel ABI, so
        /// every buffer is allocated far larger than the struct actually needs and every read is guarded by
        /// the byte count the kernel actually wrote: a wrong offset can only ever produce a wrong (harmless,
        /// fails to match) number, never an out-of-bounds read.
        ///
        /// Deliberately no lsof fallback: this is called synchronously from the accept thread (see
        /// ClientProcessResolver.BeginResolve), and forking Unity Editor's own multi-GB address space to spawn
        /// lsof measured 50-90ms in this same process -- far too slow to run on that thread. A libproc failure
        /// here just means "no answer, degrade to the header/UA guess," same as any other failure mode.
        /// </summary>
        private sealed class MacLibProcConnectionTableReader : IConnectionTableReader
        {
            private const uint ProcAllPids = 1;
            private const int ProcPidListFds = 1;
            private const int ProcPidFdSocketInfo = 3;
            private const int ProxFdTypeSocket = 2;
            private const int SockInfoTcp = 2;
            private const int ProcFdInfoSize = 8; // int32 fd + uint32 fdtype, per struct proc_fdinfo

            // Absolute byte offsets within struct socket_fdinfo -- see class doc for the derivation.
            private const int SoiKindOffset = 256;
            private const int TcpFportOffset = 264;
            private const int TcpLportOffset = 268;
            // Deliberately far larger than sizeof(struct socket_fdinfo) (a few hundred bytes at most):
            // memory-safety margin, not a size we expect the kernel to ever actually fill.
            private const int SocketFdInfoBufferSize = 2048;

            [DllImport("libproc.dylib", SetLastError = true)]
            private static extern int proc_listpids(uint type, uint typeinfo, IntPtr buffer, int buffersize);

            [DllImport("libproc.dylib", SetLastError = true)]
            private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int buffersize);

            [DllImport("libproc.dylib", SetLastError = true)]
            private static extern int proc_pidfdinfo(int pid, int fd, int flavor, IntPtr buffer, int buffersize);

            /// <summary>
            /// Targeted, early-exit lookup for exactly one client port. Scans pids in descending order (a
            /// just-created client process is very likely among the highest currently running), returning as
            /// soon as a match is found. Worst case (not found -- connection already closed) still has to walk
            /// every pid, same cost as a full scan; that was empirically ~4-5ms for ~740 processes on the dev
            /// machine, which is the actual bound on this method's accept-thread blocking time.
            /// </summary>
            public bool TryFindClientPid(int remotePort, int serverPort, out int pid)
            {
                pid = 0;
                int[] pids = ListAllPids();
                if (pids == null) return false;

                Array.Sort(pids);
                for (int i = pids.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        if (SocketMatches(pids[i], remotePort, serverPort))
                        {
                            pid = pids[i];
                            return true;
                        }
                    }
                    catch { /* one pid's fd table failing (permission, exited mid-scan) must not abort the whole scan */ }
                }
                return false;
            }

            private static int[] ListAllPids()
            {
                int bytesNeeded = proc_listpids(ProcAllPids, 0, IntPtr.Zero, 0);
                if (bytesNeeded <= 0) return null;
                int bufSize = bytesNeeded + 4096; // headroom -- the process count can grow between the two calls
                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int written = proc_listpids(ProcAllPids, 0, buffer, bufSize);
                    if (written <= 0) return null;
                    int count = written / sizeof(int);
                    var pids = new int[count];
                    Marshal.Copy(buffer, pids, 0, count);
                    return pids;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            /// <summary>True if this pid owns a TCP socket local:remotePort -> foreign:serverPort (i.e. the client side of our connection).</summary>
            private static bool SocketMatches(int pid, int remotePort, int serverPort)
            {
                int fdBytesNeeded = proc_pidinfo(pid, ProcPidListFds, 0, IntPtr.Zero, 0);
                if (fdBytesNeeded <= 0) return false;

                int fdBufSize = fdBytesNeeded + 256;
                IntPtr fdBuffer = Marshal.AllocHGlobal(fdBufSize);
                try
                {
                    int fdWritten = proc_pidinfo(pid, ProcPidListFds, 0, fdBuffer, fdBufSize);
                    if (fdWritten <= 0) return false;
                    int fdCount = fdWritten / ProcFdInfoSize;

                    IntPtr socketBuffer = Marshal.AllocHGlobal(SocketFdInfoBufferSize);
                    try
                    {
                        for (int i = 0; i < fdCount; i++)
                        {
                            IntPtr entry = IntPtr.Add(fdBuffer, i * ProcFdInfoSize);
                            int fd = Marshal.ReadInt32(entry, 0);
                            uint fdType = unchecked((uint)Marshal.ReadInt32(entry, 4));
                            if (fdType != ProxFdTypeSocket) continue;

                            int siWritten = proc_pidfdinfo(pid, fd, ProcPidFdSocketInfo, socketBuffer, SocketFdInfoBufferSize);
                            // Too small to safely contain the fields we're about to read -- not a TCP socket_fdinfo (or the pid/fd raced shut).
                            if (siWritten < TcpLportOffset + 4) continue;

                            if (Marshal.ReadInt32(socketBuffer, SoiKindOffset) != SockInfoTcp) continue;
                            // insi_fport/insi_lport are packed in the low 16 bits, network (big-endian) byte order
                            // -- confirmed empirically against a live connection, same convention as Windows'
                            // MIB_TCPROW_OWNER_PID port fields (see WindowsConnectionTableReader.SwapPort).
                            int fport = SwapPort(Marshal.ReadInt32(socketBuffer, TcpFportOffset));
                            int lport = SwapPort(Marshal.ReadInt32(socketBuffer, TcpLportOffset));
                            if (fport == serverPort && lport == remotePort)
                                return true;
                        }
                    }
                    finally { Marshal.FreeHGlobal(socketBuffer); }
                }
                finally { Marshal.FreeHGlobal(fdBuffer); }
                return false;
            }

            // Ports are packed in the low 16 bits, network (big-endian) byte order.
            private static int SwapPort(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
        }

        // ===== Linux =====

        /// <summary>Pure /proc/net/tcp(+tcp6) parsing + fd inode reverse-lookup -- no forking, per agent.md's zero-Unity-API/zero-blocking-syscall preference for the accept-thread-adjacent worker.</summary>
        private sealed class LinuxConnectionTableReader : IConnectionTableReader
        {
            public bool TryFindClientPid(int remotePort, int serverPort, out int pid)
            {
                pid = 0;
                long inode = FindClientInode("/proc/net/tcp", remotePort, serverPort);
                if (inode < 0) inode = FindClientInode("/proc/net/tcp6", remotePort, serverPort);
                if (inode < 0) return false; // connection already closed, or never existed

                string[] procDirs;
                try { procDirs = Directory.GetDirectories("/proc"); }
                catch { return false; }

                foreach (var dir in procDirs)
                {
                    var pidStr = Path.GetFileName(dir);
                    if (!int.TryParse(pidStr, out int candidatePid)) continue;

                    string[] fds;
                    try { fds = Directory.GetFiles(Path.Combine(dir, "fd")); }
                    catch { continue; } // permission denied / process exited mid-scan

                    foreach (var fd in fds)
                    {
                        string target;
                        try { target = ResolveSymlink(fd); }
                        catch { continue; }
                        if (target == null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;

                        var inodeStr = target.Substring(8, target.Length - 9);
                        if (long.TryParse(inodeStr, out long fdInode) && fdInode == inode)
                        {
                            pid = candidatePid;
                            return true;
                        }
                    }
                }
                return false;
            }

            /// <summary>Finds the inode of the row whose local port is remotePort and remote port is serverPort -- the client's own socket. -1 if not found.</summary>
            private static long FindClientInode(string path, int remotePort, int serverPort)
            {
                string[] lines;
                try { lines = File.ReadAllLines(path); }
                catch { return -1; }

                for (int i = 1; i < lines.Length; i++) // line 0 is the header
                {
                    var fields = lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 10) continue;

                    var localParts = fields[1].Split(':');
                    var remParts = fields[2].Split(':');
                    if (localParts.Length != 2 || remParts.Length != 2) continue;
                    if (fields[3] != "01") continue; // TCP_ESTABLISHED

                    if (!int.TryParse(localParts[1], System.Globalization.NumberStyles.HexNumber, null, out int localPort)) continue;
                    if (localPort != remotePort) continue;
                    if (!int.TryParse(remParts[1], System.Globalization.NumberStyles.HexNumber, null, out int remPort)) continue;
                    if (remPort != serverPort) continue;
                    if (!long.TryParse(fields[9], out long inode)) continue;

                    return inode;
                }
                return -1;
            }

            [DllImport("libc", SetLastError = true)]
            private static extern int readlink(string path, byte[] buffer, int bufferSize);

            private static string ResolveSymlink(string path)
            {
                // .NET Core's FileSystemInfo.LinkTarget would be the natural fit, but the Unity-bundled Mono
                // runtime doesn't expose it -- P/Invoke readlink(2) directly instead (still zero forking).
                try
                {
                    var buffer = new byte[4096];
                    int len = readlink(path, buffer, buffer.Length);
                    return len > 0 ? Encoding.UTF8.GetString(buffer, 0, len) : null;
                }
                catch { return null; }
            }
        }

        private sealed class LinuxProcessTableReader : IProcessTableReader
        {
            public IReadOnlyDictionary<int, ProcessInfo> ReadAll()
            {
                string[] procDirs;
                try { procDirs = Directory.GetDirectories("/proc"); }
                catch { return null; }

                var result = new Dictionary<int, ProcessInfo>();
                foreach (var dir in procDirs)
                {
                    var pidStr = Path.GetFileName(dir);
                    if (!int.TryParse(pidStr, out int pid)) continue;

                    int ppid = ReadPpid(pid);
                    if (ppid < 0) continue; // process already gone

                    string args = TryGetCommandLine(pid);
                    // argv[0] from cmdline (unbounded) is always preferred over comm/status Name: (both
                    // truncated to TASK_COMM_LEN=15 chars); ReadComm is only reached when cmdline is empty
                    // (e.g. a kernel thread), where there's no argv[0] to prefer over it anyway.
                    string name = !string.IsNullOrEmpty(args) ? FirstToken(args) : ReadComm(pid);
                    if (name == null) continue;

                    result[pid] = new ProcessInfo { Ppid = ppid, Name = name, Args = args };
                }
                return result;
            }

            public string TryGetCommandLine(int pid)
            {
                try
                {
                    var bytes = File.ReadAllBytes($"/proc/{pid}/cmdline");
                    if (bytes.Length == 0) return null;
                    var text = Encoding.UTF8.GetString(bytes).Replace('\0', ' ').Trim();
                    return text.Length == 0 ? null : text;
                }
                catch { return null; }
            }

            /// <summary>Single-pid read (a few small file opens, no fork) -- safe to call synchronously from the accept thread for the leaf client pid.</summary>
            public bool TryGetSingle(int pid, out ProcessInfo info)
            {
                info = default;
                int ppid = ReadPpid(pid);
                if (ppid < 0) return false;

                string args = TryGetCommandLine(pid);
                string name = !string.IsNullOrEmpty(args) ? FirstToken(args) : ReadComm(pid);
                if (name == null) return false;

                info = new ProcessInfo { Ppid = ppid, Name = name, Args = args };
                return true;
            }

            private static int ReadPpid(int pid)
            {
                try
                {
                    foreach (var line in File.ReadLines($"/proc/{pid}/status"))
                    {
                        if (!line.StartsWith("PPid:", StringComparison.Ordinal)) continue;
                        var value = line.Substring(5).Trim();
                        return int.TryParse(value, out int ppid) ? ppid : -1;
                    }
                }
                catch { /* process exited mid-read */ }
                return -1;
            }

            private static string ReadComm(int pid)
            {
                try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
                catch { return null; }
            }

            private static string FirstToken(string args)
            {
                int space = args.IndexOf(' ');
                return space >= 0 ? args.Substring(0, space) : args;
            }
        }

        // ===== Windows =====

        private sealed class WindowsConnectionTableReader : IConnectionTableReader
        {
            private const int AF_INET = 2;
            private const int AF_INET6 = 23;
            private const int TCP_TABLE_OWNER_PID_ALL = 5;

            [DllImport("iphlpapi.dll", SetLastError = true)]
            private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tcpTableClass, int reserved);

            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_TCPROW_OWNER_PID
            {
                public uint state;
                public uint localAddr;
                public uint localPort;  // only the low 2 bytes are used, in network byte order
                public uint remoteAddr;
                public uint remotePort; // ditto
                public uint owningPid;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MIB_TCP6ROW_OWNER_PID
            {
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] localAddr;
                public uint localScopeId;
                public uint localPort;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] remoteAddr;
                public uint remoteScopeId;
                public uint remotePort;
                public uint state;
                public uint owningPid;
            }

            public bool TryFindClientPid(int remotePort, int serverPort, out int pid)
            {
                return ReadTable(AF_INET, remotePort, serverPort, out pid) || ReadTable(AF_INET6, remotePort, serverPort, out pid);
            }

            private static bool ReadTable(int ipVersion, int remotePort, int serverPort, out int pid)
            {
                pid = 0;
                int bufSize = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref bufSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
                if (bufSize <= 0) return false;

                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int rc = GetExtendedTcpTable(buffer, ref bufSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL, 0);
                    if (rc != 0) return false;

                    int numEntries = Marshal.ReadInt32(buffer);
                    IntPtr rowPtr = IntPtr.Add(buffer, 4);

                    if (ipVersion == AF_INET)
                    {
                        int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                        for (int i = 0; i < numEntries; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                            if (SwapPort(row.remotePort) == serverPort && SwapPort(row.localPort) == remotePort)
                            {
                                pid = (int)row.owningPid;
                                return true;
                            }
                        }
                    }
                    else
                    {
                        int rowSize = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                        for (int i = 0; i < numEntries; i++)
                        {
                            var row = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize));
                            if (SwapPort(row.remotePort) == serverPort && SwapPort(row.localPort) == remotePort)
                            {
                                pid = (int)row.owningPid;
                                return true;
                            }
                        }
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            // Ports are packed in the low 16 bits, network (big-endian) byte order.
            private static int SwapPort(uint raw) => ((int)(raw & 0xFF) << 8) | (int)((raw >> 8) & 0xFF);
        }

        private sealed class WindowsProcessTableReader : IProcessTableReader
        {
            private const int TH32CS_SNAPPROCESS = 0x00000002;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct PROCESSENTRY32
            {
                public uint dwSize;
                public uint cntUsage;
                public uint th32ProcessID;
                public IntPtr th32DefaultHeapID;
                public uint th32ModuleID;
                public uint cntThreads;
                public uint th32ParentProcessID;
                public int pcPriClassBase;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
            [DllImport("kernel32.dll")]
            private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
            [DllImport("kernel32.dll")]
            private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr hObject);

            public IReadOnlyDictionary<int, ProcessInfo> ReadAll()
            {
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;

                try
                {
                    var result = new Dictionary<int, ProcessInfo>();
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (!Process32First(snapshot, ref entry)) return null;

                    do
                    {
                        result[(int)entry.th32ProcessID] = new ProcessInfo
                        {
                            Ppid = (int)entry.th32ParentProcessID,
                            Name = entry.szExeFile,
                            Args = null, // Toolhelp32 doesn't expose the command line; fetched lazily via TryGetCommandLine
                        };
                    } while (Process32Next(snapshot, ref entry));

                    return result;
                }
                finally { CloseHandle(snapshot); }
            }

            /// <summary>
            /// Toolhelp32 has no single-pid query, only a full-snapshot enumeration -- same underlying cost as
            /// ReadAll(), just stopping as soon as our pid turns up. Still no-fork/native, so acceptable to
            /// call synchronously from the accept thread for the leaf client pid.
            /// </summary>
            public bool TryGetSingle(int pid, out ProcessInfo info)
            {
                info = default;
                IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
                if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return false;

                try
                {
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (!Process32First(snapshot, ref entry)) return false;

                    do
                    {
                        if ((int)entry.th32ProcessID != pid) continue;
                        info = new ProcessInfo { Ppid = (int)entry.th32ParentProcessID, Name = entry.szExeFile, Args = null };
                        return true;
                    } while (Process32Next(snapshot, ref entry));

                    return false;
                }
                finally { CloseHandle(snapshot); }
            }

            // ---- Lazy PEB command-line read (mandatory for correctly naming node/python-hosted agent CLIs on
            // Windows -- Toolhelp32 only ever gives "node.exe"). Undocumented internals, x64-only (Unity's
            // Windows Editor is x64-only), wrapped end-to-end in try/catch: any failure (access denied, PEB
            // layout mismatch, process already exited) silently falls back to the bare process name. ----

            private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            private const int PROCESS_VM_READ = 0x0010;

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
            [DllImport("kernel32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);
            [DllImport("ntdll.dll")]
            private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

            [StructLayout(LayoutKind.Sequential)]
            private struct PROCESS_BASIC_INFORMATION
            {
                public IntPtr ExitStatus;
                public IntPtr PebBaseAddress;
                public IntPtr AffinityMask;
                public IntPtr BasePriority;
                public IntPtr UniqueProcessId;
                public IntPtr InheritedFromUniqueProcessId;
            }

            // Well-known (undocumented) x64 offsets: PEB.ProcessParameters at +0x20; RTL_USER_PROCESS_PARAMETERS.CommandLine (a UNICODE_STRING) at +0x70.
            private const int PebProcessParametersOffset = 0x20;
            private const int CommandLineOffset = 0x70;

            public string TryGetCommandLine(int pid)
            {
                if (IntPtr.Size != 8) return null; // 32-bit host process reading a 64-bit target (or vice versa) needs WOW64 handling this doesn't implement -- degrade instead of misreading.

                IntPtr hProcess = IntPtr.Zero;
                try
                {
                    hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
                    if (hProcess == IntPtr.Zero) return null;

                    var pbi = new PROCESS_BASIC_INFORMATION();
                    if (NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                        return null;
                    if (pbi.PebBaseAddress == IntPtr.Zero) return null;

                    var ptrBuf = new byte[8];
                    if (!ReadProcessMemory(hProcess, IntPtr.Add(pbi.PebBaseAddress, PebProcessParametersOffset), ptrBuf, 8, out _))
                        return null;
                    IntPtr processParameters = new IntPtr(BitConverter.ToInt64(ptrBuf, 0));
                    if (processParameters == IntPtr.Zero) return null;

                    // UNICODE_STRING { ushort Length; ushort MaximumLength; (4 bytes padding on x64); IntPtr Buffer; }
                    var unicodeStringBuf = new byte[16];
                    if (!ReadProcessMemory(hProcess, IntPtr.Add(processParameters, CommandLineOffset), unicodeStringBuf, unicodeStringBuf.Length, out _))
                        return null;
                    ushort length = BitConverter.ToUInt16(unicodeStringBuf, 0);
                    IntPtr bufferPtr = new IntPtr(BitConverter.ToInt64(unicodeStringBuf, 8));
                    if (length <= 0 || bufferPtr == IntPtr.Zero) return null;

                    var commandLineBytes = new byte[length];
                    if (!ReadProcessMemory(hProcess, bufferPtr, commandLineBytes, length, out _))
                        return null;

                    return Encoding.Unicode.GetString(commandLineBytes);
                }
                catch { return null; }
                finally { if (hProcess != IntPtr.Zero) CloseHandle(hProcess); }
            }
        }

        // ===== Shared process-spawn helper (macOS) =====

        private static string ResolveTool(string absolutePath, string fallbackName)
        {
            try { return File.Exists(absolutePath) ? absolutePath : fallbackName; }
            catch { return fallbackName; }
        }

        /// <summary>Runs an external tool and captures stdout, silently degrading (returns null) on any failure, timeout, or non-zero exit. Only used by the lsof/ps fallback path -- the primary mac/Linux readers never fork.</summary>
        private static string RunProcessCapturingStdout(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.BeginErrorReadLine(); // drain stderr to avoid a full-pipe deadlock
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                    return stdout;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Tries the no-fork libproc path first (see <see cref="MacLibProcProcessTableReader"/>); falls back to
        /// shelling out to `ps` only if that throws or returns null. Same rationale as
        /// <see cref="MacConnectionTableReader"/>: forking Unity Editor's large address space to run `ps`
        /// measured at 50-90ms in this same process, long enough for a short-lived ancestor lookup (right after
        /// the fast libproc connection read finds a leaf pid) to miss a leaf process that's already exited.
        /// </summary>
        private sealed class MacProcessTableReader : IProcessTableReader
        {
            private readonly MacLibProcProcessTableReader _libproc = new MacLibProcProcessTableReader();
            private readonly MacPsProcessTableReader _psFallback = new MacPsProcessTableReader(psPath: "/bin/ps");

            public IReadOnlyDictionary<int, ProcessInfo> ReadAll()
            {
                try
                {
                    var result = _libproc.ReadAll();
                    if (result != null) return result;
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose("ClientProcessResolver: libproc process-table read failed, falling back to ps: " + ex.Message);
                }
                return _psFallback.ReadAll();
            }

            public string TryGetCommandLine(int pid)
            {
                try
                {
                    var result = _libproc.TryGetCommandLine(pid);
                    if (result != null) return result;
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose("ClientProcessResolver: libproc command-line read failed, falling back to ps: " + ex.Message);
                }
                return _psFallback.TryGetCommandLine(pid);
            }

            /// <summary>
            /// Sync-only, no ps fallback: this runs on the accept thread (see ClientProcessResolver.BeginResolve),
            /// where forking to shell out to ps is never acceptable. A libproc failure just means "no answer,
            /// degrade to the header/UA guess" -- exactly like every other synchronous failure mode.
            /// </summary>
            public bool TryGetSingle(int pid, out ProcessInfo info)
            {
                try { return _libproc.TryGetSingle(pid, out info); }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose("ClientProcessResolver: libproc single-pid read failed (accept-thread sync path, no ps fallback): " + ex.Message);
                    info = default;
                    return false;
                }
            }
        }

        /// <summary>
        /// Reads ppid+name directly via libproc -- no fork. ppid comes from proc_pidinfo/PROC_PIDTBSDINFO
        /// (struct proc_bsdinfo; offset computed by hand from &lt;sys/proc_info.h&gt;: four leading uint32_t
        /// fields put pbi_ppid at +16). Name comes from argv[0] via sysctl(KERN_PROCARGS2) (see
        /// MacSysctlArgs), fetched eagerly for every pid, NOT from proc_bsdinfo's own pbi_comm field --
        /// confirmed empirically that pbi_comm (the kernel's "accounting name", independently settable at
        /// runtime e.g. via pthread_setname_np) can legitimately diverge from a process's actual identity: a
        /// live Node-hosted agent CLI's pbi_comm showed an internal embedder version string instead of its own
        /// name, while its argv[0] was correct. pbi_comm is used only as the last-resort fallback when args
        /// aren't available at all (permission-denied, kernel threads). Same memory-safety approach as
        /// MacLibProcConnectionTableReader: buffers are allocated far larger than the struct needs, and every
        /// read is bounds-checked against the byte count the kernel actually wrote.
        /// </summary>
        private sealed class MacLibProcProcessTableReader : IProcessTableReader
        {
            private const uint ProcAllPids = 1;
            private const int ProcPidTBsdInfo = 3;
            private const int PpidOffset = 16;
            private const int CommOffset = 48;
            private const int CommMaxLen = 16; // MAXCOMLEN
            private const int BsdInfoBufferSize = 2048; // generously oversized -- see class doc

            [DllImport("libproc.dylib", SetLastError = true)]
            private static extern int proc_listpids(uint type, uint typeinfo, IntPtr buffer, int buffersize);
            [DllImport("libproc.dylib", SetLastError = true)]
            private static extern int proc_pidinfo(int pid, int flavor, ulong arg, IntPtr buffer, int buffersize);

            public IReadOnlyDictionary<int, ProcessInfo> ReadAll()
            {
                int[] pids = ListAllPids();
                if (pids == null) return null;

                var result = new Dictionary<int, ProcessInfo>();
                IntPtr buffer = Marshal.AllocHGlobal(BsdInfoBufferSize);
                try
                {
                    foreach (var pid in pids)
                    {
                        try
                        {
                            if (TryReadOne(pid, buffer, out var info))
                                result[pid] = info;
                        }
                        catch { /* one pid failing must not abort the whole scan */ }
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
                return result;
            }

            /// <summary>Single-pid ppid+name lookup: one proc_pidinfo call + one sysctl call, no fork -- safe to call synchronously from the accept thread for the leaf client pid.</summary>
            public bool TryGetSingle(int pid, out ProcessInfo info)
            {
                info = default;
                IntPtr buffer = Marshal.AllocHGlobal(BsdInfoBufferSize);
                try { return TryReadOne(pid, buffer, out info); }
                catch { info = default; return false; }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            /// <summary>Shared by ReadAll (buffer reused across every pid) and TryGetSingle (its own throwaway buffer).</summary>
            private static bool TryReadOne(int pid, IntPtr buffer, out ProcessInfo info)
            {
                info = default;
                int written = proc_pidinfo(pid, ProcPidTBsdInfo, 0, buffer, BsdInfoBufferSize);
                if (written < PpidOffset + 4) return false; // exited mid-scan / permission denied
                int ppid = Marshal.ReadInt32(buffer, PpidOffset);

                // argv[0] (via sysctl, see MacSysctlArgs) is the name source, not pbi_comm: the kernel's
                // "comm" accounting name is independently settable at runtime (e.g. via pthread_setname_np)
                // and can legitimately diverge from the executable identity -- confirmed empirically for
                // Node-hosted CLIs, whose pbi_comm showed an internal embedder version string instead of the
                // process's actual name. Falls back to pbi_comm only when args aren't available at all
                // (permission-denied, kernel threads).
                string args = MacSysctlArgs.TryGetCommandLine(pid);
                string name = !string.IsNullOrEmpty(args) ? FirstToken(args) : ReadFixedString(buffer, CommOffset, CommMaxLen);
                if (string.IsNullOrEmpty(name)) return false;

                info = new ProcessInfo { Ppid = ppid, Name = name, Args = args };
                return true;
            }

            private static string FirstToken(string args)
            {
                int space = args.IndexOf(' ');
                return space >= 0 ? args.Substring(0, space) : args;
            }

            public string TryGetCommandLine(int pid) => MacSysctlArgs.TryGetCommandLine(pid);

            private static int[] ListAllPids()
            {
                int bytesNeeded = proc_listpids(ProcAllPids, 0, IntPtr.Zero, 0);
                if (bytesNeeded <= 0) return null;
                int bufSize = bytesNeeded + 4096;
                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int written = proc_listpids(ProcAllPids, 0, buffer, bufSize);
                    if (written <= 0) return null;
                    int count = written / sizeof(int);
                    var pids = new int[count];
                    Marshal.Copy(buffer, pids, 0, count);
                    return pids;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }

            private static string ReadFixedString(IntPtr buffer, int offset, int maxLen)
            {
                var bytes = new byte[maxLen];
                Marshal.Copy(IntPtr.Add(buffer, offset), bytes, 0, maxLen);
                int len = Array.IndexOf(bytes, (byte)0);
                if (len < 0) len = maxLen;
                return len == 0 ? null : Encoding.UTF8.GetString(bytes, 0, len);
            }
        }

        /// <summary>
        /// Lazy, no-fork command-line fetch for one pid via sysctl(CTL_KERN, KERN_PROCARGS2, pid) -- only ever
        /// called for interpreter hits (node/python/...) during a chain walk, which by definition are ancestors
        /// that outlived their descendant, so this isn't on the same tight timing budget as the leaf lookup.
        /// Layout (standard, used by ps/many OSS diagnostic tools): a leading int32 argc, then the exec path
        /// (NUL-terminated) plus NUL padding, then argc NUL-terminated argv strings, then envp (ignored).
        /// </summary>
        private static class MacSysctlArgs
        {
            private const int CtlKern = 1;
            private const int KernProcArgs2 = 49;

            [DllImport("libc", SetLastError = true)]
            private static extern int sysctl(int[] name, uint namelen, IntPtr oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

            public static string TryGetCommandLine(int pid)
            {
                try
                {
                    var mib = new[] { CtlKern, KernProcArgs2, pid };
                    IntPtr len = IntPtr.Zero;
                    if (sysctl(mib, 3, IntPtr.Zero, ref len, IntPtr.Zero, IntPtr.Zero) != 0) return null;
                    int size = len.ToInt32();
                    if (size <= 4) return null;

                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        IntPtr actualLen = new IntPtr(size);
                        if (sysctl(mib, 3, buffer, ref actualLen, IntPtr.Zero, IntPtr.Zero) != 0) return null;
                        int total = actualLen.ToInt32();
                        if (total < 4) return null;

                        int argc = Marshal.ReadInt32(buffer, 0);
                        if (argc <= 0) return null;

                        int offset = 4;
                        while (offset < total && Marshal.ReadByte(buffer, offset) != 0) offset++; // skip exec_path
                        while (offset < total && Marshal.ReadByte(buffer, offset) == 0) offset++;  // skip NUL padding

                        var argv = new List<string>(argc);
                        for (int i = 0; i < argc && offset < total; i++)
                        {
                            int start = offset;
                            while (offset < total && Marshal.ReadByte(buffer, offset) != 0) offset++;
                            int strLen = offset - start;
                            if (strLen > 0)
                            {
                                var bytes = new byte[strLen];
                                Marshal.Copy(IntPtr.Add(buffer, start), bytes, 0, strLen);
                                argv.Add(Encoding.UTF8.GetString(bytes));
                            }
                            offset++; // skip the NUL terminator
                        }
                        return argv.Count == 0 ? null : string.Join(" ", argv);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                catch { return null; }
            }
        }

        /// <summary>Fallback only (see MacProcessTableReader): shells out to `ps`.</summary>
        private sealed class MacPsProcessTableReader : IProcessTableReader
        {
            private static readonly Regex PsLinePattern = new Regex(@"^\s*(\d+)\s+(\d+)\s+(.*)$", RegexOptions.Compiled);
            private readonly string _psPath;

            public MacPsProcessTableReader(string psPath) { _psPath = psPath; }

            public IReadOnlyDictionary<int, ProcessInfo> ReadAll()
            {
                var output = RunProcessCapturingStdout(ResolveTool(_psPath, "ps"), "-axo pid,ppid,args", 3000);
                if (output == null) return null;

                var result = new Dictionary<int, ProcessInfo>();
                foreach (var line in output.Split('\n'))
                {
                    var m = PsLinePattern.Match(line);
                    if (!m.Success) continue;
                    if (!int.TryParse(m.Groups[1].Value, out int pid)) continue;
                    if (!int.TryParse(m.Groups[2].Value, out int ppid)) continue;

                    var args = m.Groups[3].Value.Trim();
                    if (args.Length == 0) continue;
                    var name = FirstToken(args);

                    result[pid] = new ProcessInfo { Ppid = ppid, Name = name, Args = args };
                }
                return result;
            }

            public string TryGetCommandLine(int pid)
            {
                // Defensive fallback only -- ReadAll() above already fills Args for every pid in one shot, so
                // WalkChain should never actually need this. Kept for interface completeness / test injection.
                var output = RunProcessCapturingStdout(ResolveTool(_psPath, "ps"), $"-o args= -p {pid}", 2000);
                var trimmed = output?.Trim();
                return string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }

            private static string FirstToken(string args)
            {
                int space = args.IndexOf(' ');
                return space >= 0 ? args.Substring(0, space) : args;
            }

            /// <summary>
            /// Never implemented here: this reader only exists as the async ReadAll/TryGetCommandLine fork
            /// fallback. A sync, no-fork caller (the accept thread) must never reach this -- MacProcessTableReader
            /// routes TryGetSingle to libproc only and fails closed rather than falling back to this class.
            /// </summary>
            public bool TryGetSingle(int pid, out ProcessInfo info) { info = default; return false; }
        }
    }
}

// Producer:Betsy
