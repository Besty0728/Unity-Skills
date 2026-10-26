using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>A problem or notice from the runner envelope (contract 2.2), or one the panel raises itself.</summary>
    internal sealed class EvaluationProblem
    {
        public string Code;
        public string Fix;
        public List<string> Args = new List<string>();

        internal static EvaluationProblem Local(string code, params string[] args)
        {
            return new EvaluationProblem { Code = code, Args = new List<string>(args ?? Array.Empty<string>()) };
        }
    }

    /// <summary>What one panel command ended with: the parsed result file, or a panel-side failure.</summary>
    internal sealed class EvaluationOutcome
    {
        public string Command;
        public string CmdId;
        public bool Ok;
        public int? ExitCode;
        public string FinishedAt;
        public string LogFile;
        public JObject Data;
        public List<EvaluationProblem> Problems = new List<EvaluationProblem>();
        public List<EvaluationProblem> Notices = new List<EvaluationProblem>();

        internal static EvaluationOutcome Failure(string command, string cmdId, string logFile, EvaluationProblem problem)
        {
            var outcome = new EvaluationOutcome
            {
                Command = command,
                CmdId = cmdId,
                LogFile = logFile,
                FinishedAt = EvaluationState.FormatIso(DateTime.UtcNow),
            };
            outcome.Problems.Add(problem);
            return outcome;
        }
    }

    /// <summary>panel/&lt;cmdId&gt;.json (contract 4.10): written by the panel before and after starting the runner.</summary>
    internal sealed class PanelCommand
    {
        public string CmdId;
        public string Command;
        public List<string> Argv = new List<string>();
        public string ResultFile;
        public string LogFile;
        public string StartedAt;
        public int? Pid;
        public string AckAt;
    }

    internal sealed class LaunchResult
    {
        public PanelCommand Command;
        /// <summary>Set when the runner could not be started; the command (if any) is already acknowledged.</summary>
        public EvaluationOutcome Failure;
        public bool Started => Failure == null;
    }

    /// <summary>
    /// Starts the evaluation runner for the Evaluation tab and follows it across domain reloads (contract 7.6). Every
    /// launch writes panel/&lt;cmdId&gt;.json first, starts python without waiting (the runner calls back into this
    /// editor's REST server, which is served on this main thread) and records the pid; the result file the runner
    /// writes carries the exit code, so a reloaded domain without the Process object can still finish the command.
    /// Nothing here polls: the tab calls <see cref="Poll"/> from its own scheduler tick.
    /// </summary>
    internal static class EvaluationRunnerService
    {
        internal const int KeptCommandGroups = 50;
        private const double CrashGraceSeconds = 2;
        private const double PidReuseSlackSeconds = 5;
        private const double UnstartedTimeoutSeconds = 30;
        // The runner treats a lock older than an hour as stale; a command silent for three is not coming back.
        private const double MaxCommandHours = 3;

        private static readonly Regex CommandFilePattern = new Regex(@"^cmd-\d{8}T\d{9}-[0-9a-f]{6}\.json$");
        private static readonly Dictionary<string, Process> Processes = new Dictionary<string, Process>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> ExitSeenAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>Test seam: a runner script used instead of the package's Evaluation~/run_eval.py.</summary>
        internal static string RunnerPathOverrideForTests;

        internal static bool IsWindows => UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor;

        // ===== Launch =====

        /// <summary>
        /// Starts <c>python -B run_eval.py &lt;command&gt; &lt;args&gt; --project P --launcher panel --result-file F
        /// --log-file L --cmd-id ID</c>.
        /// </summary>
        internal static LaunchResult Launch(string projectRoot, string python, string command, IList<string> args)
        {
            var runner = ResolveRunnerPath(out var missing);
            if (runner == null)
                return new LaunchResult { Failure = EvaluationOutcome.Failure(command, null, null, EvaluationProblem.Local(missing)) };

            var now = DateTime.UtcNow;
            var panelDir = EvaluationState.PanelDir(projectRoot);
            var cmdId = NewCommandId(now);
            var record = new PanelCommand
            {
                CmdId = cmdId,
                Command = command,
                ResultFile = Path.Combine(panelDir, cmdId + ".result.json"),
                LogFile = Path.Combine(panelDir, cmdId + ".log"),
                StartedAt = EvaluationState.FormatIso(now),
            };

            var commandArgs = new List<string>(args ?? Array.Empty<string>());
            record.Argv = BuildArgv(python, runner, command, commandArgs, projectRoot, record);

            foreach (var arg in record.Argv)
            {
                if (!IsPassableArgument(arg, IsWindows))
                    return new LaunchResult { Failure = EvaluationOutcome.Failure(command, null, null, EvaluationProblem.Local("ARGUMENT_UNSAFE", arg)) };
            }

            try
            {
                WriteRecord(projectRoot, record);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new LaunchResult { Failure = EvaluationOutcome.Failure(command, cmdId, null, EvaluationProblem.Local("IO_ERROR", panelDir, ex.Message)) };
            }

            Process process;
            try
            {
                var psi = new ProcessStartInfo(record.Argv[0])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = projectRoot,
                };
                for (int i = 1; i < record.Argv.Count; i++) psi.ArgumentList.Add(record.Argv[i]);
                psi.Environment["PYTHONUTF8"] = "1";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
                process = Process.Start(psi);
                if (process == null) throw new InvalidOperationException("no process was started");
            }
            catch (Exception ex)
            {
                record.AckAt = EvaluationState.FormatIso(DateTime.UtcNow);
                TryWriteRecord(projectRoot, record);
                return new LaunchResult
                {
                    Command = record,
                    Failure = EvaluationOutcome.Failure(command, cmdId, null, EvaluationProblem.Local("LAUNCH_FAILED", ex.Message)),
                };
            }

            record.Pid = process.Id;
            TryWriteRecord(projectRoot, record);
            Processes[cmdId] = process;
            SkillsLogger.LogVerbose($"Evaluation runner started: {command} ({cmdId}, pid {process.Id})");
            Prune(projectRoot, KeptCommandGroups);
            return new LaunchResult { Command = record };
        }

        internal static List<string> BuildArgv(string python, string runner, string command, IList<string> args,
            string projectRoot, PanelCommand record)
        {
            var argv = new List<string> { python, "-B", runner, command };
            argv.AddRange(args);
            argv.AddRange(new[]
            {
                "--project", projectRoot, "--launcher", "panel", "--result-file", record.ResultFile,
                "--log-file", record.LogFile, "--cmd-id", record.CmdId,
            });
            return argv;
        }

        internal static string ResolveRunnerPath(out string problemCode)
        {
            problemCode = null;
            if (!string.IsNullOrEmpty(RunnerPathOverrideForTests)) return RunnerPathOverrideForTests;
            if (!PackageRootLocator.TryGet(out var root))
            {
                problemCode = "PACKAGE_NOT_FOUND";
                return null;
            }
            var runner = Path.GetFullPath(Path.Combine(root, "Evaluation~", "run_eval.py"));
            if (File.Exists(runner)) return runner;
            problemCode = "RUNNER_NOT_FOUND";
            return null;
        }

        /// <summary>cmd-YYYYMMDDTHHMMSSmmm-xxxxxx (contract 0), UTC to the millisecond.</summary>
        internal static string NewCommandId(DateTime utc)
        {
            var bytes = new byte[3];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return "cmd-" + utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "-" +
                   BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>
        /// ProcessStartInfo.ArgumentList is pasted into one command line; Mono on Unix re-splits it with POSIX shell
        /// rules, so quotes, backslashes and line breaks would not arrive intact. Such an argument (in practice a
        /// path) is refused before anything starts.
        /// </summary>
        internal static bool IsPassableArgument(string arg, bool windows)
        {
            if (string.IsNullOrEmpty(arg)) return false;
            foreach (var c in arg)
            {
                if (c == '"' || c == '\0' || c == '\r' || c == '\n') return false;
                if (!windows && (c == '\'' || c == '\\')) return false;
            }
            return true;
        }

        // ===== Command files =====

        internal static string CommandFile(string projectRoot, string cmdId) =>
            Path.Combine(EvaluationState.PanelDir(projectRoot), cmdId + ".json");

        internal static string SerializeRecord(PanelCommand record)
        {
            return new JObject
            {
                ["schema"] = 1,
                ["cmdId"] = record.CmdId,
                ["command"] = record.Command,
                ["argv"] = new JArray(record.Argv),
                ["resultFile"] = record.ResultFile,
                ["logFile"] = record.LogFile,
                ["startedAt"] = record.StartedAt,
                ["pid"] = record.Pid.HasValue ? (JToken)record.Pid.Value : JValue.CreateNull(),
                ["ackAt"] = record.AckAt != null ? (JToken)record.AckAt : JValue.CreateNull(),
            }.ToString(Formatting.Indented);
        }

        internal static PanelCommand ParseRecord(JObject value)
        {
            var cmdId = value?.Value<string>("cmdId");
            if (cmdId == null || !CommandFilePattern.IsMatch(cmdId + ".json")) return null;
            var record = new PanelCommand
            {
                CmdId = cmdId,
                Command = value.Value<string>("command"),
                ResultFile = value.Value<string>("resultFile"),
                LogFile = value.Value<string>("logFile"),
                StartedAt = value.Value<string>("startedAt"),
                Pid = value["pid"]?.Type == JTokenType.Integer ? (int?)value.Value<int>("pid") : null,
                AckAt = value.Value<string>("ackAt"),
            };
            if (value["argv"] is JArray argv)
            {
                foreach (var item in argv) record.Argv.Add(item.Type == JTokenType.String ? item.Value<string>() : item.ToString());
            }
            return string.IsNullOrEmpty(record.Command) || string.IsNullOrEmpty(record.ResultFile) ? null : record;
        }

        internal static void WriteRecord(string projectRoot, PanelCommand record)
        {
            EvaluationState.WriteTextAtomic(CommandFile(projectRoot, record.CmdId), SerializeRecord(record));
        }

        private static void TryWriteRecord(string projectRoot, PanelCommand record)
        {
            try { WriteRecord(projectRoot, record); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SkillsLogger.LogWarning($"Could not update evaluation command file {record.CmdId}: {ex.Message}");
            }
        }

        /// <summary>Every readable command file, oldest first (cmdIds sort by time).</summary>
        internal static List<PanelCommand> LoadRecords(string projectRoot)
        {
            var records = new List<PanelCommand>();
            var dir = EvaluationState.PanelDir(projectRoot);
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "cmd-*.json") : Array.Empty<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return records; }

            foreach (var file in files)
            {
                if (!CommandFilePattern.IsMatch(Path.GetFileName(file))) continue;
                var raw = EvaluationState.Read(file);
                var record = raw.Status == EvaluationState.ReadStatus.Ok ? ParseRecord(raw.Value) : null;
                if (record != null) records.Add(record);
            }
            records.Sort((a, b) => string.CompareOrdinal(a.CmdId, b.CmdId));
            return records;
        }

        /// <summary>Commands whose result has not been consumed yet; after a reload these are re-attached.</summary>
        internal static List<PanelCommand> LoadPending(string projectRoot)
        {
            return LoadRecords(projectRoot).FindAll(record => record.AckAt == null);
        }

        /// <summary>The newest consumed outcome of each command, to show again after a reload.</summary>
        internal static Dictionary<string, EvaluationOutcome> LoadLatestOutcomes(string projectRoot)
        {
            var latest = new Dictionary<string, EvaluationOutcome>(StringComparer.Ordinal);
            var records = LoadRecords(projectRoot);
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                // No pid: the launch itself failed, and that message only lived in the panel that saw it.
                if (record.AckAt == null || !record.Pid.HasValue || latest.ContainsKey(record.Command)) continue;
                var raw = EvaluationState.Read(record.ResultFile, requireSchema: false);
                latest[record.Command] = raw.Status == EvaluationState.ReadStatus.Ok
                    ? ParseResult(record, raw.Value)
                    : Crashed(record);
            }
            return latest;
        }

        /// <summary>Writes ackAt (the result has been consumed) and forgets the process.</summary>
        internal static void Acknowledge(string projectRoot, PanelCommand record, DateTime nowUtc)
        {
            record.AckAt = EvaluationState.FormatIso(nowUtc);
            TryWriteRecord(projectRoot, record);
            ExitSeenAt.Remove(record.CmdId);
            if (Processes.TryGetValue(record.CmdId, out var process))
            {
                Processes.Remove(record.CmdId);
                process.Dispose();
            }
        }

        /// <summary>Deletes the oldest consumed command groups beyond <paramref name="keep"/>; pending ones stay.</summary>
        internal static void Prune(string projectRoot, int keep)
        {
            var records = LoadRecords(projectRoot);
            var excess = records.Count - keep;
            var dir = EvaluationState.PanelDir(projectRoot);
            for (int i = 0; i < records.Count && excess > 0; i++)
            {
                if (records[i].AckAt == null) continue;
                foreach (var suffix in new[] { ".json", ".result.json", ".declared.json", ".log" })
                {
                    try { File.Delete(Path.Combine(dir, records[i].CmdId + suffix)); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* retried on the next prune */ }
                }
                excess--;
            }
        }

        // ===== Tracking =====

        /// <summary>
        /// One tracking step for a pending command: the outcome once the result file exists, a crash once the process
        /// has been gone for 2 s without one, otherwise null (still running).
        /// </summary>
        internal static EvaluationOutcome Poll(PanelCommand record, DateTime nowUtc)
        {
            var outcome = PollOnce(record, nowUtc);
            if (outcome != null) ExitSeenAt.Remove(record.CmdId);
            return outcome;
        }

        private static EvaluationOutcome PollOnce(PanelCommand record, DateTime nowUtc)
        {
            var raw = EvaluationState.Read(record.ResultFile, requireSchema: false);
            switch (raw.Status)
            {
                case EvaluationState.ReadStatus.Ok:
                    return ParseResult(record, raw.Value);
                case EvaluationState.ReadStatus.Unsupported:
                    return EvaluationOutcome.Failure(record.Command, record.CmdId, record.LogFile, EvaluationProblem.Local("SCHEMA_UNSUPPORTED"));
                case EvaluationState.ReadStatus.Corrupt:
                    return EvaluationOutcome.Failure(record.Command, record.CmdId, record.LogFile, EvaluationProblem.Local("RESULT_UNREADABLE", record.ResultFile));
            }

            if (IsRunning(record, nowUtc))
            {
                ExitSeenAt.Remove(record.CmdId);
                return null;
            }
            if (!ExitSeenAt.TryGetValue(record.CmdId, out var since))
            {
                ExitSeenAt[record.CmdId] = nowUtc;
                return null;
            }
            if ((nowUtc - since).TotalSeconds < CrashGraceSeconds) return null;

            // The result may have landed between the first read and the exit check.
            raw = EvaluationState.Read(record.ResultFile, requireSchema: false);
            return raw.Status == EvaluationState.ReadStatus.Ok ? ParseResult(record, raw.Value) : Crashed(record);
        }

        private static bool IsRunning(PanelCommand record, DateTime nowUtc)
        {
            var started = EvaluationState.ParseIso(record.StartedAt);
            if (started.HasValue && (nowUtc - started.Value).TotalHours > MaxCommandHours) return false;

            if (Processes.TryGetValue(record.CmdId, out var process))
            {
                try { return !process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
            if (!record.Pid.HasValue)
                return started.HasValue && (nowUtc - started.Value).TotalSeconds < UnstartedTimeoutSeconds;
            return IsProcessAlive(record.Pid.Value, started);
        }

        /// <summary>
        /// Whether <paramref name="pid"/> runs, and (when <paramref name="startedNoLaterThanUtc"/> is known) was not
        /// started more than 5 s after it, which would mean the pid was reused. Platforms that cannot report a start
        /// time skip that check.
        /// </summary>
        internal static bool IsProcessAlive(int pid, DateTime? startedNoLaterThanUtc = null)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    if (process.HasExited) return false;
                    if (startedNoLaterThanUtc.HasValue)
                    {
                        try
                        {
                            if (process.StartTime.ToUniversalTime() > startedNoLaterThanUtc.Value.AddSeconds(PidReuseSlackSeconds))
                                return false;
                        }
                        catch (Exception) { /* no start time here: trust the pid */ }
                    }
                    return true;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Another runner command holds the lock (contract 2.6): its pid is alive, or the lock is still being written
        /// (no readable pid yet, younger than 10 s). Otherwise the lock is stale and the runner will take it over.
        /// </summary>
        internal static bool IsRunnerBusy(string projectRoot, DateTime nowUtc, out EvaluationState.LockInfo holder)
        {
            holder = EvaluationState.ReadLock(projectRoot);
            if (holder == null) return false;
            if (!holder.Pid.HasValue) return (nowUtc - holder.ModifiedUtc).TotalSeconds < 10;
            return IsProcessAlive(holder.Pid.Value);
        }

        internal static EvaluationOutcome ParseResult(PanelCommand record, JObject result)
        {
            var outcome = new EvaluationOutcome
            {
                Command = record.Command,
                CmdId = record.CmdId,
                LogFile = record.LogFile,
                Ok = result["ok"]?.Type == JTokenType.Boolean && result.Value<bool>("ok"),
                ExitCode = result["exitCode"]?.Type == JTokenType.Integer ? (int?)result.Value<int>("exitCode") : null,
                FinishedAt = result.Value<string>("finishedAt"),
                Data = result["data"] as JObject,
            };
            outcome.Problems.AddRange(ParseProblems(result["problems"]));
            outcome.Notices.AddRange(ParseProblems(outcome.Data?["notices"]));
            return outcome;
        }

        internal static List<EvaluationProblem> ParseProblems(JToken token)
        {
            var problems = new List<EvaluationProblem>();
            if (!(token is JArray array)) return problems;
            foreach (var item in array)
            {
                if (!(item is JObject obj)) continue;
                var problem = new EvaluationProblem { Code = obj.Value<string>("code"), Fix = obj.Value<string>("fix") };
                if (obj["args"] is JArray args)
                {
                    foreach (var arg in args) problem.Args.Add(arg.Type == JTokenType.String ? arg.Value<string>() : arg.ToString(Formatting.None));
                }
                problems.Add(problem);
            }
            return problems;
        }

        private static EvaluationOutcome Crashed(PanelCommand record)
        {
            return EvaluationOutcome.Failure(record.Command, record.CmdId, record.LogFile,
                EvaluationProblem.Local("RUNNER_CRASHED", record.LogFile ?? string.Empty));
        }
    }
}

// Producer:Betsy
