using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Internal
{
    /// <summary>One interpreter to try: an executable (or the Windows py launcher) plus the arguments that select it.</summary>
    internal sealed class PythonCandidate
    {
        public readonly string FileName;
        public readonly string[] PrefixArgs;
        public readonly string Source;

        public PythonCandidate(string fileName, string source, params string[] prefixArgs)
        {
            FileName = fileName;
            Source = source;
            PrefixArgs = prefixArgs ?? Array.Empty<string>();
        }
    }

    /// <summary>The outcome of a lookup: the verified interpreter, or why none could be used.</summary>
    internal sealed class PythonLocation
    {
        public bool Found;
        /// <summary>Absolute interpreter path as reported by sys.executable (falls back to the candidate itself).</summary>
        public string Executable;
        public string Version;
        public string Source;
        public bool FromOverride;
        public string Candidate;
        public string Error;

        internal static PythonLocation Failure(PythonCandidate candidate, string error) => new PythonLocation
        {
            Found = false,
            Candidate = candidate?.FileName,
            Source = candidate?.Source,
            FromOverride = candidate?.Source == PythonLocator.SourceOverride,
            Error = error,
        };
    }

    /// <summary>
    /// Finds a Python 3.9+ interpreter for the evaluation runner. A path set by the user (EditorPrefs
    /// UnitySkills_EvalPythonPath) is the only candidate when present, so a broken override is reported instead of
    /// silently replaced. Otherwise the platform's usual places are tried in order, and each candidate is run once with
    /// -c to prove it starts and to read its real version and executable. Windows Store app-execution stubs are
    /// skipped (they can open the Store instead of running), and on macOS /usr/bin/python3 is only tried when the
    /// developer tools ship a python3, because the bare shim opens their installer. Probing runs on a worker thread;
    /// the result is handed back on the main thread.
    /// </summary>
    internal static class PythonLocator
    {
        internal const string PrefKeyOverride = "UnitySkills_EvalPythonPath";
        internal const string SourceOverride = "override";
        internal const string SourcePyLauncher = "py-launcher";
        internal const string SourceShell = "shell";
        internal const string SourceKnownPath = "known-path";
        internal static readonly Version MinimumVersion = new Version(3, 9);
        internal const int ProbeTimeoutMs = 8000;
        private const int ShellLookupTimeoutMs = 10000;
        private const string MacCommandLineToolsPython = "/Library/Developer/CommandLineTools/usr/bin/python3";
        private const string MacSystemPython = "/usr/bin/python3";

        // No quotes, backslashes or spaces: Mono on Unix re-splits the pasted command line with POSIX shell rules.
        internal const string ProbeScript =
            "import sys;print(sys.version_info[0],sys.version_info[1],sys.version_info[2]);print(sys.executable)";

        private static readonly Regex InstallDirPattern = new Regex(@"^Python(\d)(\d+)", RegexOptions.IgnoreCase);
        private static readonly Regex WindowsRootedPath = new Regex(@"^([A-Za-z]:[\\/]|\\\\)");
        private static readonly List<Action<PythonLocation>> Waiters = new List<Action<PythonLocation>>();
        private static bool _locating;
        private static bool _restartRequested;

        internal static bool IsLocating => _locating;

        /// <summary>The last completed lookup in this domain; null until one finished.</summary>
        internal static PythonLocation Last { get; private set; }

        internal static string OverridePath
        {
            get => EditorPrefs.GetString(PrefKeyOverride, string.Empty);
            set
            {
                if (string.IsNullOrWhiteSpace(value)) EditorPrefs.DeleteKey(PrefKeyOverride);
                else EditorPrefs.SetString(PrefKeyOverride, value.Trim());
            }
        }

        /// <summary>Machine facts the candidate order depends on. Captured on the main thread, or built by tests.</summary>
        internal sealed class Facts
        {
            public bool IsWindows;
            public bool IsMac;
            public string OverridePath;
            public string LocalAppData;
            public Func<string, bool> FileExists = File.Exists;
            public Func<string, IEnumerable<string>> ListDirectories = _ => Array.Empty<string>();
            /// <summary>Lines printed by "where python" / "command -v python3"; null when the lookup failed.</summary>
            public Func<List<string>> ShellLookup = () => null;
            public Func<bool> MacDeveloperToolsHavePython = () => false;
        }

        /// <summary>
        /// Starts a lookup on a worker thread and calls <paramref name="done"/> on the main thread. A call made while a
        /// lookup runs restarts it with fresh facts (e.g. a changed override) and every caller gets the final result.
        /// </summary>
        internal static void LocateAsync(Action<PythonLocation> done)
        {
            if (done != null) Waiters.Add(done);
            if (_locating)
            {
                _restartRequested = true;
                return;
            }
            StartWorker();
        }

        private static void StartWorker()
        {
            _locating = true;
            _restartRequested = false;
            var facts = CaptureFacts();
            var context = SynchronizationContext.Current;
            if (context == null)
            {
                Finish(Locate(facts));
                return;
            }

            var worker = new Thread(() =>
            {
                PythonLocation result;
                try { result = Locate(facts); }
                catch (Exception ex) { result = PythonLocation.Failure(null, ex.Message); }
                // Post is the thread-safe hop onto the main thread; delayCall then runs the UI work outside a repaint.
                context.Post(_ => EditorApplication.delayCall += () => Finish(result), null);
            })
            {
                IsBackground = true,
                Name = "UnitySkills-PythonLocator",
            };
            worker.Start();
        }

        private static void Finish(PythonLocation result)
        {
            _locating = false;
            if (_restartRequested)
            {
                StartWorker();
                return;
            }

            Last = result;
            var waiters = Waiters.ToArray();
            Waiters.Clear();
            foreach (var waiter in waiters)
            {
                try { waiter(result); }
                catch (Exception ex) { SkillsLogger.LogWarning("Python lookup callback failed: " + ex.Message); }
            }
        }

        internal static Facts CaptureFacts()
        {
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            return new Facts
            {
                IsWindows = isWindows,
                IsMac = Application.platform == RuntimePlatform.OSXEditor,
                OverridePath = OverridePath,
                LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FileExists = File.Exists,
                ListDirectories = dir => Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(),
                ShellLookup = () => WithTimeout(
                    () => ShellCommandResolver.ResolveAll(isWindows ? "where python" : "command -v python3", isWindows),
                    ShellLookupTimeoutMs),
                MacDeveloperToolsHavePython = MacDeveloperToolsHavePython,
            };
        }

        /// <summary>Blocking lookup: the first candidate that runs and reports 3.9+ wins.</summary>
        internal static PythonLocation Locate(Facts facts)
        {
            PythonLocation lastFailure = null;
            foreach (var candidate in EnumerateCandidates(facts))
            {
                var probe = Verify(candidate);
                if (probe.Found) return probe;
                if (candidate.Source == SourceOverride) return probe;
                lastFailure = probe;
            }
            return lastFailure ?? PythonLocation.Failure(null, null);
        }

        /// <summary>
        /// Candidates in trial order, de-duplicated. Lazy, so the shell lookup only runs when the earlier candidates
        /// are exhausted.
        /// </summary>
        internal static IEnumerable<PythonCandidate> EnumerateCandidates(Facts facts)
        {
            if (!string.IsNullOrWhiteSpace(facts.OverridePath))
            {
                yield return new PythonCandidate(facts.OverridePath.Trim(), SourceOverride);
                yield break;
            }

            var seen = new HashSet<string>(facts.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (facts.IsWindows)
            {
                yield return new PythonCandidate("py", SourcePyLauncher, "-3");

                foreach (var line in facts.ShellLookup() ?? new List<string>())
                {
                    if (IsUsablePath(line, facts) && !IsWindowsStoreStub(line) && seen.Add(Normalize(line, facts)))
                        yield return new PythonCandidate(line, SourceShell);
                }

                foreach (var path in WindowsInstallCandidates(facts))
                {
                    if (facts.FileExists(path) && seen.Add(Normalize(path, facts)))
                        yield return new PythonCandidate(path, SourceKnownPath);
                }
                yield break;
            }

            foreach (var line in facts.ShellLookup() ?? new List<string>())
            {
                if (IsUsablePath(line, facts) && AllowedOnMac(line, facts) && seen.Add(Normalize(line, facts)))
                    yield return new PythonCandidate(line, SourceShell);
            }

            var known = facts.IsMac
                ? new[] { "/opt/homebrew/bin/python3", "/usr/local/bin/python3", MacSystemPython }
                : new[] { "/usr/bin/python3", "/usr/local/bin/python3" };
            foreach (var path in known)
            {
                if (facts.FileExists(path) && AllowedOnMac(path, facts) && seen.Add(Normalize(path, facts)))
                    yield return new PythonCandidate(path, SourceKnownPath);
            }
        }

        /// <summary>%LOCALAPPDATA%\Programs\Python\Python3XY\python.exe, newest version first.</summary>
        private static IEnumerable<string> WindowsInstallCandidates(Facts facts)
        {
            if (string.IsNullOrEmpty(facts.LocalAppData)) return Array.Empty<string>();
            var root = facts.LocalAppData.TrimEnd('\\', '/') + "\\Programs\\Python";
            var installs = new List<(Version version, string exe)>();
            foreach (var dir in facts.ListDirectories(root) ?? Array.Empty<string>())
            {
                var trimmed = dir.TrimEnd('\\', '/');
                var version = ParseInstallDirVersion(trimmed.Substring(trimmed.LastIndexOfAny(new[] { '\\', '/' }) + 1));
                if (IsSupported(version)) installs.Add((version, trimmed + "\\python.exe"));
            }
            installs.Sort((a, b) => b.version.CompareTo(a.version));
            var paths = new List<string>(installs.Count);
            foreach (var install in installs) paths.Add(install.exe);
            return paths;
        }

        /// <summary>"Python311" -> 3.11, "Python39-32" -> 3.9; null for anything else.</summary>
        internal static Version ParseInstallDirVersion(string directoryName)
        {
            var match = InstallDirPattern.Match(directoryName ?? string.Empty);
            if (!match.Success) return null;
            return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }

        /// <summary>An absolute path of the platform in <paramref name="facts"/> (not the host's) that exists.</summary>
        private static bool IsUsablePath(string path, Facts facts)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            bool rooted = facts.IsWindows
                ? WindowsRootedPath.IsMatch(path)
                : path.StartsWith("/", StringComparison.Ordinal);
            return rooted && facts.FileExists(path);
        }

        private static bool AllowedOnMac(string path, Facts facts)
        {
            return !facts.IsMac || !string.Equals(path, MacSystemPython, StringComparison.Ordinal) ||
                   facts.MacDeveloperToolsHavePython();
        }

        private static string Normalize(string path, Facts facts)
        {
            return facts.IsWindows ? path.Replace('/', '\\').TrimEnd('\\') : path.TrimEnd('/');
        }

        /// <summary>App-execution aliases under ...\Microsoft\WindowsApps\ (the Store's python.exe stubs).</summary>
        internal static bool IsWindowsStoreStub(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var normalized = path.Replace('/', '\\');
            return normalized.IndexOf("\\Microsoft\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The macOS python3 shim is safe to run only when the selected developer tools contain python3.</summary>
        private static bool MacDeveloperToolsHavePython()
        {
            if (File.Exists(MacCommandLineToolsPython)) return true;
            var developerDir = WithTimeout(() => RunForFirstLine("/usr/bin/xcode-select", "-p"), ProbeTimeoutMs);
            return !string.IsNullOrEmpty(developerDir) && File.Exists(Path.Combine(developerDir, "usr", "bin", "python3"));
        }

        /// <summary>Runs <paramref name="candidate"/> with the probe script; blocking, so worker thread only.</summary>
        internal static PythonLocation Verify(PythonCandidate candidate)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate.FileName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                foreach (var arg in candidate.PrefixArgs) psi.ArgumentList.Add(arg);
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(ProbeScript);
                psi.Environment["PYTHONUTF8"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

                using (var process = Process.Start(psi))
                {
                    if (process == null) return PythonLocation.Failure(candidate, "the process did not start");
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(ProbeTimeoutMs))
                    {
                        try { process.Kill(); }
                        catch (Exception) { /* it may have exited meanwhile */ }
                        return PythonLocation.Failure(candidate, "no answer within " + ProbeTimeoutMs / 1000 + " s");
                    }
                    try { Task.WaitAll(new Task[] { stdout, stderr }, 2000); }
                    catch (AggregateException) { /* a faulted read leaves that stream empty below */ }
                    if (process.ExitCode != 0)
                        return PythonLocation.Failure(candidate, "exit code " + process.ExitCode + FirstLine(CompletedText(stderr)));
                    if (!TryParseProbeOutput(CompletedText(stdout), out var version, out var executable))
                        return PythonLocation.Failure(candidate, "not a Python 3 interpreter");
                    if (!IsSupported(version))
                        return PythonLocation.Failure(candidate, "Python " + version + " is older than " + MinimumVersion);
                    return new PythonLocation
                    {
                        Found = true,
                        Executable = string.IsNullOrEmpty(executable) ? candidate.FileName : executable,
                        Version = version.ToString(),
                        Source = candidate.Source,
                        FromOverride = candidate.Source == SourceOverride,
                        Candidate = candidate.FileName,
                    };
                }
            }
            catch (Exception ex)
            {
                // Win32Exception for a missing or non-executable file, ArgumentException for a malformed path, ...:
                // every way a candidate fails to start just moves on to the next one.
                return PythonLocation.Failure(candidate, ex.Message);
            }
        }

        /// <summary>Parses the probe's "major minor micro" line and the sys.executable line.</summary>
        internal static bool TryParseProbeOutput(string stdout, out Version version, out string executable)
        {
            version = null;
            executable = null;
            var lines = (stdout ?? string.Empty).Replace("\r", string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            var parts = lines[0].Trim().Split(' ');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
                !int.TryParse(parts[2], out var micro))
                return false;
            if (major < 0 || minor < 0 || micro < 0) return false;

            version = new Version(major, minor, micro);
            executable = lines.Length > 1 ? lines[1].Trim() : null;
            return true;
        }

        internal static bool IsSupported(Version version) => version != null && version >= MinimumVersion;

        private static string CompletedText(Task<string> read)
        {
            return read.Status == TaskStatus.RanToCompletion ? read.Result ?? string.Empty : string.Empty;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return ": " + text.Trim().Split('\n')[0].Trim();
        }

        private static string RunForFirstLine(string fileName, string argument)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, argument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(ProbeTimeoutMs) || process.ExitCode != 0) return null;
                    stdout.Wait(2000);
                    var text = CompletedText(stdout).Trim();
                    return text.Length == 0 ? null : text.Split('\n')[0].Trim();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static T WithTimeout<T>(Func<T> work, int timeoutMs)
        {
            var task = Task.Run(work);
            return task.Wait(timeoutMs) ? task.Result : default;
        }
    }
}

// Producer:Betsy
