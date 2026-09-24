using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UnitySkills.Internal
{
    /// <summary>One child process run: exit code, captured output, and why it ended early if it did.</summary>
    internal sealed class ProcessRunResult
    {
        public int ExitCode = -1;
        public string StdOut = string.Empty;
        public string StdErr = string.Empty;
        public bool TimedOut;
        public bool Cancelled;
        public bool StartFailed;
        public int NativeErrorCode;
        public long ElapsedMs;

        public bool Succeeded => !StartFailed && !TimedOut && !Cancelled && ExitCode == 0;
    }

    /// <summary>
    /// Locates and runs the git CLI for the local self-update. No Unity API: it runs on the self-update worker thread and
    /// in tests. Arguments go through ProcessStartInfo.ArgumentList; on Unix Mono re-splits the pasted command line with
    /// POSIX shell rules, so arguments that would not survive that (quotes, backslashes, line breaks) are rejected before
    /// anything starts instead of surfacing later as a misleading "file not found".
    /// </summary>
    internal static class GitCliRunner
    {
        internal static readonly Version MinimumVersion = new Version(2, 15);
        internal const int VersionTimeoutMs = 10000;

        private const int PollIntervalMs = 100;
        private const int DrainTimeoutMs = 2000;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // Every call: unquoted paths, no colour, no fsmonitor daemon spawned from the editor. The empty fsmonitor value
        // reads as "off" both where the key is a boolean (2.36+) and where it is a hook path (2.16-2.35).
        private static readonly string[] BaseConfig =
            { "-c", "core.quotePath=false", "-c", "core.fsmonitor=", "-c", "color.ui=false" };

        // Network and write calls: an empty credential.helper resets the helper list, so no credential manager window
        // ever opens; no automatic gc or maintenance forked from the editor.
        private static readonly string[] IsolatedConfig =
            { "-c", "credential.helper=", "-c", "core.askPass=", "-c", "gc.auto=0", "-c", "maintenance.auto=false" };

        // The repository is always chosen by the working directory, never by an inherited environment.
        private static readonly string[] ScrubbedVariables =
        {
            "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
            "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES", "GIT_DISCOVERY_ACROSS_FILESYSTEM",
            "GIT_CONFIG_PARAMETERS", "GIT_ASKPASS", "SSH_ASKPASS", "GIT_ASK_YESNO",
        };

        private static readonly Regex VersionPattern = new Regex(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

        private static readonly object Gate = new object();
        private static readonly HashSet<Process> Active = new HashSet<Process>();
        private static string _resolvedPath;
        private static Version _resolvedVersion;

        internal static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// Finds a git of at least <see cref="MinimumVersion"/>. Only absolute candidates are tried, each verified with
        /// --version; the first good one is cached until the next domain reload, failures are not (installing git and
        /// clicking again works). When only an older git exists, returns false with <paramref name="version"/> set.
        /// </summary>
        internal static bool TryResolveGit(out string gitPath, out Version version)
        {
            lock (Gate)
            {
                if (_resolvedPath != null)
                {
                    gitPath = _resolvedPath;
                    version = _resolvedVersion;
                    return true;
                }
            }

            gitPath = null;
            version = null;
            var candidates = BuildCandidateList(IsWindows, IsMac,
                Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable);
            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                var run = Run(candidate, null, new[] { "--version" }, VersionTimeoutMs);
                if (!run.Succeeded || !TryParseGitVersion(run.StdOut, out var found)) continue;

                if (found >= MinimumVersion)
                {
                    lock (Gate)
                    {
                        _resolvedPath = candidate;
                        _resolvedVersion = found;
                    }
                    gitPath = candidate;
                    version = found;
                    return true;
                }
                if (version == null || found > version)
                {
                    gitPath = candidate;
                    version = found;
                }
            }
            return false;
        }

        /// <summary>
        /// Absolute git candidates in probe order. The current directory is never searched (a git.exe in the project root
        /// must not win), and on macOS /usr/bin/git is skipped: it is the Command Line Tools shim, which pops the
        /// installer dialog when the tools are missing. The real binaries behind it are probed directly instead.
        /// </summary>
        internal static List<string> BuildCandidateList(bool windows, bool mac, string pathVariable, Func<string, string> getEnv)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(windows || mac ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void Add(string path)
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return;
                try { path = Path.GetFullPath(path); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return; }
                if (mac && string.Equals(path, "/usr/bin/git", StringComparison.OrdinalIgnoreCase)) return;
                if (seen.Add(path)) list.Add(path);
            }

            var exe = windows ? "git.exe" : "git";
            foreach (var dir in (pathVariable ?? string.Empty).Split(windows ? ';' : ':'))
            {
                var trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0 || !Path.IsPathRooted(trimmed)) continue;
                Add(Path.Combine(trimmed, exe));
            }

            if (windows)
            {
                void AddUnder(string variable, params string[] parts)
                {
                    var root = getEnv?.Invoke(variable);
                    if (string.IsNullOrEmpty(root)) return;
                    var segments = new List<string> { root };
                    segments.AddRange(parts);
                    Add(Path.Combine(segments.ToArray()));
                }
                AddUnder("ProgramFiles", "Git", "cmd", "git.exe");
                AddUnder("ProgramFiles(x86)", "Git", "cmd", "git.exe");
                AddUnder("LocalAppData", "Programs", "Git", "cmd", "git.exe");
                AddUnder("UserProfile", "scoop", "shims", "git.exe");
            }
            else if (mac)
            {
                Add("/opt/homebrew/bin/git");
                Add("/usr/local/bin/git");
                Add("/opt/local/bin/git");
                Add("/Library/Developer/CommandLineTools/usr/bin/git");
                Add("/Applications/Xcode.app/Contents/Developer/usr/bin/git");
            }
            else
            {
                Add("/usr/bin/git");
                Add("/usr/local/bin/git");
            }
            return list;
        }

        /// <summary>Reads the first two or three numeric fields of "git version 2.54.0", "2.45.1.windows.1" or "2.50.1 (Apple Git-155)".</summary>
        internal static bool TryParseGitVersion(string output, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(output)) return false;
            var text = output.Trim();
            var marker = text.IndexOf("git version", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0) text = text.Substring(marker + "git version".Length);
            var match = VersionPattern.Match(text);
            if (!match.Success) return false;
            if (!int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor))
                return false;
            var patch = 0;
            if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out patch)) return false;
            version = new Version(major, minor, patch);
            return true;
        }

        /// <summary>
        /// Whether an argument reaches the child process byte for byte. Unix: Mono's command-line re-splitting drops
        /// unquoted backslashes and fails outright on a lone single quote. Windows: CreateProcess quoting only has to
        /// survive the absence of double quotes. Line breaks and NUL are never valid in a git argument we build.
        /// </summary>
        internal static bool IsSafeArgument(string arg, bool windows)
        {
            if (arg == null) return false;
            foreach (var c in arg)
            {
                if (c == '"' || c == '\0' || c == '\r' || c == '\n') return false;
                if (!windows && (c == '\'' || c == '\\')) return false;
            }
            return true;
        }

        /// <summary>Runs git with the base config prefix; <paramref name="isolated"/> adds the credential/maintenance lock-down.</summary>
        internal static ProcessRunResult RunGit(string gitPath, string workDir, IReadOnlyList<string> args, int timeoutMs,
            Func<bool> isCancelled = null, bool isolated = false)
        {
            var full = new List<string>(BaseConfig.Length + IsolatedConfig.Length + args.Count);
            full.AddRange(BaseConfig);
            if (isolated) full.AddRange(IsolatedConfig);
            full.AddRange(args);
            return Run(gitPath, workDir, full, timeoutMs, isCancelled);
        }

        /// <summary>
        /// Runs a process to completion, a timeout or cancellation. stdin is closed at once (any prompt reads EOF), both
        /// streams are drained asynchronously, and after exit the drain waits at most two seconds: a grandchild such as
        /// git-remote-https can keep the pipes open after its parent was killed.
        /// </summary>
        internal static ProcessRunResult Run(string exe, string workDir, IReadOnlyList<string> args, int timeoutMs,
            Func<bool> isCancelled = null)
        {
            var windows = IsWindows;
            foreach (var arg in args)
            {
                if (!IsSafeArgument(arg, windows))
                    throw new ArgumentException("Unsafe process argument: " + (arg ?? "<null>"), nameof(args));
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
                WorkingDirectory = workDir ?? string.Empty,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["LC_ALL"] = "C";
            foreach (var name in ScrubbedVariables) psi.Environment.Remove(name);

            var result = new ProcessRunResult();
            var watch = Stopwatch.StartNew();
            Process process;
            try
            {
                process = Process.Start(psi);
                if (process == null)
                {
                    result.StartFailed = true;
                    return result;
                }
            }
            catch (Exception ex)
            {
                // Win32Exception 2/3 is "not found"; anything else (bad working directory, permissions) is still a
                // process that never ran, which callers treat the same way.
                result.StartFailed = true;
                result.NativeErrorCode = (ex as Win32Exception)?.NativeErrorCode ?? 0;
                result.StdErr = ex.Message;
                return result;
            }

            lock (Gate) Active.Add(process);
            try
            {
                try { process.StandardInput.Close(); }
                catch (IOException) { /* the child may already have exited */ }

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                while (!process.WaitForExit(PollIntervalMs))
                {
                    if (watch.ElapsedMilliseconds >= timeoutMs) { result.TimedOut = true; break; }
                    if (isCancelled != null && isCancelled()) { result.Cancelled = true; break; }
                }
                if (result.TimedOut || result.Cancelled)
                {
                    TryKill(process);
                    process.WaitForExit(DrainTimeoutMs);
                }

                try { Task.WaitAll(new Task[] { stdout, stderr }, DrainTimeoutMs); }
                catch (AggregateException) { /* a faulted read leaves that stream empty below */ }

                result.StdOut = CompletedText(stdout);
                result.StdErr = CompletedText(stderr);
                try { result.ExitCode = process.HasExited ? process.ExitCode : -1; }
                catch (InvalidOperationException) { result.ExitCode = -1; }
            }
            finally
            {
                lock (Gate) Active.Remove(process);
                process.Dispose();
                result.ElapsedMs = watch.ElapsedMilliseconds;
            }
            return result;
        }

        /// <summary>Kills every child still running (domain reload, editor quit). Safe from any thread.</summary>
        internal static void KillActive()
        {
            Process[] running;
            lock (Gate)
            {
                running = new Process[Active.Count];
                Active.CopyTo(running);
            }
            foreach (var process in running) TryKill(process);
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException)
            {
                // Already gone or not ours to kill; the caller reports the run as timed out / cancelled either way.
            }
        }

        private static string CompletedText(Task<string> read) =>
            read.Status == TaskStatus.RanToCompletion ? read.Result ?? string.Empty : string.Empty;
    }
}

// Producer:Betsy
