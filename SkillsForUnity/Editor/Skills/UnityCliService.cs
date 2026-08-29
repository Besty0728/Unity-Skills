using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Integration service for the Unity CLI (the official <c>unity</c> command-line tool, experimental beta).
    ///
    /// Responsibilities:
    ///  1. Detect whether Unity CLI is installed locally (a background thread runs `unity
    ///     --version`; the result is polled by the UI);
    ///  2. Read/write the project-level binding config — Library/UnitySkills/cli_config.json
    ///     (machine-local, not checked into git; AI clients also read it at this fixed path
    ///     while the editor is closed, for cold-start gating);
    ///  3. Sync the binding state into RegistryService's global registry, for cross-project discovery.
    ///
    /// Zero hard dependency on the CLI: a detection failure only affects the panel display, not any REST skill.
    /// </summary>
    public static class UnityCliService
    {
        public const int ConfigSchemaVersion = 1;

        // Detection-failure reason constants (values of DetectResult.error). Not written to
        // cli_config.json, used only for runtime/UI diagnostics.
        public const string CliErrorNotFound = "not_found";
        public const string CliErrorNotExecutable = "not_executable";
        public const string CliErrorLaunchFailed = "launch_failed";
        public const string CliErrorIncompatibleSystem = "incompatible_system";

        // ===== Config model (Newtonsoft-serialized; field names are the JSON keys clients read against) =====

        public class CliFeatures
        {
            public bool coldStart = true;   // cold start / lifecycle management
            public bool openArgs  = true;   // launch via unity open --args
            public bool cliTest   = true;   // unity test headless tests
            public bool cliRun    = false;  // unity run batch execution (new capability, off by default; a missing key in old configs = false, must be opted into explicitly in the panel)
            public bool cliBuild  = false;  // unity build headless build (same as above, explicit opt-in)
        }

        public class CliConfig
        {
            public int schemaVersion = ConfigSchemaVersion;
            public bool enabled;
            public string cliPath = "";
            public string cliVersion = "";
            public string projectPath = "";
            public string editorVersion = "";
            public string boundAt = "";     // ISO-8601 UTC
            public CliFeatures features = new CliFeatures();
        }

        // ===== Detection result (written by a background thread, polled by the main-thread UI) =====

        public class DetectResult
        {
            public bool found;
            public string cliPath = "";
            public string version = "";
            public string error = "";
        }

        private static volatile bool _detecting;
        private static volatile DetectResult _lastResult;

        /// <summary>Whether a detection is currently running in the background.</summary>
        public static bool IsDetecting => _detecting;

        /// <summary>The most recent detection result; null if never detected. The UI polls this via schedule.Execute.</summary>
        public static DetectResult LastResult => _lastResult;

        // ===== Paths (captured on the main thread during static init; background threads only read the cached values) =====

        private static readonly string ConfigDir =
            Path.Combine(Application.dataPath, "../Library/UnitySkills");
        private static readonly string ConfigFile =
            Path.Combine(ConfigDir, "cli_config.json");
        private static readonly string ProjectRoot =
            Directory.GetParent(Application.dataPath).FullName;

        private static CliConfig _cached;

        // ===== Detection =====

        /// <summary>
        /// Detects Unity CLI on a background thread. Probe order: user-specified path → path
        /// from the bound config → PATH (via a login shell, since macOS GUI processes don't
        /// inherit the shell PATH) → common install locations.
        /// Writes <see cref="LastResult"/> when done, with no callback (the UI polls instead, to
        /// avoid touching Unity APIs across threads).
        /// </summary>
        public static void DetectAsync(string userPath = null)
        {
            if (_detecting) return;
            _detecting = true;

            string configuredPath = LoadConfig()?.cliPath;

            var t = new Thread(() =>
            {
                var result = new DetectResult();
                try
                {
                    string lastConcreteError = null;
                    foreach (var candidate in EnumerateCandidates(userPath, configuredPath))
                    {
                        if (string.IsNullOrEmpty(candidate)) continue;
                        var attempt = TryGetVersion(candidate);
                        if (attempt.success)
                        {
                            result.found = true;
                            result.cliPath = candidate;
                            result.version = attempt.version;
                            break;
                        }
                        // Record the specific reason for the last "the file was found but launch
                        // failed" case; if a candidate simply doesn't exist, keep the more specific error instead.
                        if (attempt.error != null)
                            lastConcreteError = attempt.error;
                    }
                    if (!result.found)
                        result.error = lastConcreteError ?? CliErrorNotFound;
                }
                catch (Exception ex)
                {
                    result.error = ex.Message;
                }
                _lastResult = result;
                _detecting = false;
            });
            t.IsBackground = true;
            t.Start();
        }

        private static IEnumerable<string> EnumerateCandidates(string userPath, string configuredPath)
        {
            if (!string.IsNullOrEmpty(userPath)) yield return userPath;
            if (!string.IsNullOrEmpty(configuredPath)) yield return configuredPath;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
#if UNITY_EDITOR_WIN
            yield return Path.Combine(home, ".unity", "bin", "unity.exe");
            yield return ResolveViaShell("where unity", winWhere: true);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Unity", "cli", "unity.exe");
            yield return "unity.exe";
#else
            // The official install.sh's default install location (its PATH injection goes
            // through .zshrc's `. ~/.unity/env`, which a GUI process can't pick up, so this
            // directory must be probed directly)
            yield return Path.Combine(home, ".unity", "bin", "unity");
            yield return ResolveViaShell("command -v unity", winWhere: false);
            yield return Path.Combine(home, ".local", "bin", "unity");
            yield return "/usr/local/bin/unity";
            yield return "/opt/homebrew/bin/unity";
            yield return "unity";
#endif
        }

        /// <summary>
        /// Resolves unity on PATH via a login shell. The editor's GUI process (especially when
        /// launched from the macOS Dock) doesn't inherit the user shell's PATH, so this must go
        /// through -lc to let the profile take effect. Returns null on failure.
        /// </summary>
        private static string ResolveViaShell(string cmd, bool winWhere)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (winWhere)
                {
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/c " + cmd;
                }
                else
                {
                    string shell = Environment.GetEnvironmentVariable("SHELL");
                    psi.FileName = string.IsNullOrEmpty(shell) ? "/bin/zsh" : shell;
                    // -lic: login + interactive. -l alone doesn't load .zshrc (that's
                    // interactive-only), and Unity CLI and similar tools' PATH injection is
                    // written precisely in .zshrc — verified in practice that -lc alone fails to find it.
                    psi.Arguments = "-lic \"" + cmd + "\"";
                }
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.BeginErrorReadLine();          // Drain stderr, to avoid a deadlock from a full buffer (rc files can be noisy)
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    if (p.ExitCode != 0) return null;
                    // "where" may return multiple lines; take the first one
                    var first = (stdout ?? "").Trim()
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return first.Length > 0 ? first[0].Trim() : null;
                }
            }
            catch { return null; }
        }

        private readonly struct VersionAttempt
        {
            public readonly bool success;
            public readonly string version;
            public readonly string error;
            public VersionAttempt(bool success, string version = "", string error = null)
            {
                this.success = success;
                this.version = version ?? "";
                this.error = error;
            }
        }

        /// <summary>Runs `&lt;path&gt; --version` to verify it's executable and get the version number; returns the specific failure reason.</summary>
        /// <remarks>
        /// Starts async stdout/stderr reads before WaitForExit, to avoid a deadlock from the
        /// child process's output filling the pipe buffer.
        /// Only suitable for probes with tiny output like `--version`; not for commands that may produce a lot of log output.
        /// </remarks>
        private static VersionAttempt TryGetVersion(string cliPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cliPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // Start the async reads first, to prevent WaitForExit from hanging forever due to a full pipe buffer.
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();

                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return new VersionAttempt(false, error: CliErrorLaunchFailed); }

                    string stdout = stdoutTask.Result;
                    string stderr = stderrTask.Result;

                    if (p.ExitCode != 0)
                    {
                        if (LooksLikeGlibcError(stderr) || LooksLikeGlibcError(stdout))
                            return new VersionAttempt(false, error: CliErrorIncompatibleSystem);
                        return new VersionAttempt(false, error: CliErrorLaunchFailed);
                    }
                    string version = (stdout ?? "").Trim();
                    if (string.IsNullOrEmpty(version))
                        return new VersionAttempt(false, error: CliErrorLaunchFailed);
                    return new VersionAttempt(true, version);
                }
            }
            catch (System.ComponentModel.Win32Exception w32)
            {
                // NativeErrorCode is more reliable than Message: Mono's wording ("Cannot find the
                // specified file" / "Access denied") differs from .NET Framework's, and varies by locale.
                // 2 = ERROR_FILE_NOT_FOUND / ENOENT, 3 = ERROR_PATH_NOT_FOUND, 5 = ERROR_ACCESS_DENIED / EACCES, 13 = EACCES (POSIX).
                switch (w32.NativeErrorCode)
                {
                    // File doesn't exist or isn't found on PATH: not a "launch failure" — let probing continue to the next candidate.
                    case 2:
                    case 3:
                        return new VersionAttempt(false);
                    case 5:
                    case 13:
                        return new VersionAttempt(false, error: CliErrorNotExecutable);
                    default:
                        // Unknown errno: fall back to message matching, covering the different wording across Mono / .NET / each platform.
                        return ClassifyByMessage(w32.Message);
                }
            }
            catch (FileNotFoundException)
            {
                return new VersionAttempt(false);
            }
            catch (UnauthorizedAccessException)
            {
                return new VersionAttempt(false, error: CliErrorNotExecutable);
            }
            catch (Exception ex)
            {
                return ClassifyByMessage(ex.Message);
            }
        }

        /// <summary>Fallback classification when no errno is available: distinguishes "doesn't exist", "not executable", and other launch failures by the exception's wording.</summary>
        private static VersionAttempt ClassifyByMessage(string message)
        {
            string msg = (message ?? "").ToLowerInvariant();
            if (msg.Contains("no such file") || msg.Contains("cannot find") || msg.Contains("not found"))
                return new VersionAttempt(false);
            if (msg.Contains("permission denied") || msg.Contains("access denied") || msg.Contains("access is denied"))
                return new VersionAttempt(false, error: CliErrorNotExecutable);
            return new VersionAttempt(false, error: CliErrorLaunchFailed);
        }

        private static bool LooksLikeGlibcError(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            return t.Contains("glibc") || t.Contains("libc.so") || t.Contains("version `glib");
        }

        // ===== Config read/write =====

        /// <summary>Reads the project binding config; returns null if the file doesn't exist or is corrupt. The result is cached and invalidated after Bind/Unbind.</summary>
        public static CliConfig LoadConfig()
        {
            if (_cached != null) return _cached;
            try
            {
                if (!File.Exists(ConfigFile)) return null;
                _cached = JsonConvert.DeserializeObject<CliConfig>(File.ReadAllText(ConfigFile));
                return _cached;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Failed to read cli_config.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>Whether the current project is bound and Unity CLI is enabled.</summary>
        public static bool IsBound
        {
            get
            {
                var cfg = LoadConfig();
                return cfg != null && cfg.enabled && !string.IsNullOrEmpty(cfg.cliPath);
            }
        }

        /// <summary>Binds the current project: writes cli_config.json and syncs the registry. Preserves existing feature toggles.</summary>
        public static void Bind(string cliPath, string cliVersion)
        {
            var cfg = LoadConfig() ?? new CliConfig();
            cfg.schemaVersion = ConfigSchemaVersion;
            cfg.enabled = true;
            cfg.cliPath = cliPath ?? "";
            cfg.cliVersion = cliVersion ?? "";
            cfg.projectPath = ProjectRoot;
            cfg.editorVersion = Application.unityVersion;
            cfg.boundAt = DateTime.UtcNow.ToString("o");
            SaveConfig(cfg);
            RegistryService.UpdateCliBinding(true, cfg.cliPath);
            SkillsLogger.Log($"Unity CLI bound: {cfg.cliPath} ({cfg.cliVersion})");
        }

        /// <summary>Unbinds: enabled=false (keeps the path to make rebinding easy), and clears the registry marker.</summary>
        public static void Unbind()
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            cfg.enabled = false;
            SaveConfig(cfg);
            RegistryService.UpdateCliBinding(false, null);
            SkillsLogger.Log("Unity CLI unbound.");
        }

        /// <summary>Updates a single feature toggle and saves it (called directly by the panel's Toggle).</summary>
        public static void SetFeature(Action<CliFeatures> mutate)
        {
            var cfg = LoadConfig();
            if (cfg == null) return;
            if (cfg.features == null) cfg.features = new CliFeatures();
            mutate(cfg.features);
            SaveConfig(cfg);
        }

        private static void SaveConfig(CliConfig cfg)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigFile, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                _cached = cfg;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogError($"Failed to write cli_config.json: {ex.Message}");
            }
        }

        /// <summary>For RegistryService.Register to read the binding state (without triggering detection).</summary>
        public static void GetRegistryBinding(out bool bound, out string cliPath)
        {
            var cfg = LoadConfig();
            bound = cfg != null && cfg.enabled && !string.IsNullOrEmpty(cfg.cliPath);
            cliPath = bound ? cfg.cliPath : null;
        }

        // ===== Cold-start auto server start =====

        /// <summary>The marker AI passes via --args when cold-starting through Unity CLI.</summary>
        public const string ColdStartArg = "-unityskills-coldstart";

        private const string ColdStartConsumedKey = "UnitySkills_CliColdStartConsumed";

        /// <summary>
        /// Whether this editor session was cold-started by Unity CLI and should auto-start the
        /// server. Conditions: the command line contains <see cref="ColdStartArg"/> + the project
        /// is bound + the coldStart toggle is on.
        /// Returns true only once per editor session (SessionState remembers consumption across
        /// Domain Reload), so a user who manually stops the server mid-session isn't force-started
        /// again by every subsequent reload.
        /// </summary>
        public static bool ConsumeColdStartRequest()
        {
            if (UnityEditor.SessionState.GetBool(ColdStartConsumedKey, false)) return false;
            UnityEditor.SessionState.SetBool(ColdStartConsumedKey, true);

            bool hasArg = false;
            foreach (var arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, ColdStartArg, StringComparison.OrdinalIgnoreCase)) { hasArg = true; break; }
            if (!hasArg) return false;

            var cfg = LoadConfig();
            return cfg != null && cfg.enabled
                && (cfg.features == null || cfg.features.coldStart);
        }
    }
}

// Producer:Betsy
