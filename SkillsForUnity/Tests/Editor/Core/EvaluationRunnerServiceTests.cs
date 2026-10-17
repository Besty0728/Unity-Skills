using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Panel command files and runner tracking (contracts 4.10, 7.6) on a temp project. Nothing is launched: records
    /// and result files are written the way the panel and the runner write them, and a pid that cannot exist stands
    /// in for an exited runner.
    /// </summary>
    [TestFixture]
    public class EvaluationRunnerServiceTests
    {
        private const int MissingPid = int.MaxValue - 7;

        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "unityskills-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_project);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_project)) Directory.Delete(_project, true);
        }

        [Test]
        public void NewCommandId_MatchesTheContractPattern()
        {
            var id = EvaluationRunnerService.NewCommandId(new DateTime(2026, 10, 17, 3, 4, 5, 678, DateTimeKind.Utc));
            Assert.That(id, Does.Match(@"^cmd-\d{8}T\d{9}-[0-9a-f]{6}$"));
            StringAssert.StartsWith("cmd-20261017T030405678-", id);
            Assert.That(EvaluationRunnerService.NewCommandId(DateTime.UtcNow), Is.Not.EqualTo(EvaluationRunnerService.NewCommandId(DateTime.UtcNow)));
        }

        [TestCase("/Users/me/My Project/Library/x.json", false, true)]
        [TestCase(@"C:\Users\me\My Project\x.json", true, true)]
        [TestCase(@"C:\Users\me\x.json", false, false)]
        [TestCase("/Users/o'brien/project", false, false)]
        [TestCase("/Users/o'brien/project", true, true)]
        [TestCase("say \"hi\"", true, false)]
        [TestCase("two\nlines", true, false)]
        [TestCase("", true, false)]
        public void IsPassableArgument(string arg, bool windows, bool expected)
        {
            Assert.That(EvaluationRunnerService.IsPassableArgument(arg, windows), Is.EqualTo(expected));
        }

        [Test]
        public void BuildArgv_PutsThePanelFlagsAfterTheCommandArguments()
        {
            var record = NewRecord(null, "check");
            var argv = EvaluationRunnerService.BuildArgv("/py", "/pkg/Evaluation~/run_eval.py", "check",
                new List<string> { "--track", "ops" }, _project, record);

            Assert.That(argv, Is.EqualTo(new[]
            {
                "/py", "-B", "/pkg/Evaluation~/run_eval.py", "check", "--track", "ops",
                "--project", _project, "--launcher", "panel", "--result-file", record.ResultFile,
                "--log-file", record.LogFile, "--cmd-id", record.CmdId,
            }));
        }

        [Test]
        public void Record_RoundTripsWithSchemaFirstAndExplicitNulls()
        {
            var record = NewRecord(null, "start");
            record.Argv = new List<string> { "/py", "-B", "run_eval.py", "start" };
            var json = EvaluationState.ParseObject(EvaluationRunnerService.SerializeRecord(record));

            Assert.That(json.Properties().Select(p => p.Name), Is.EqualTo(new[]
            {
                "schema", "cmdId", "command", "argv", "resultFile", "logFile", "startedAt", "pid", "ackAt",
            }));
            Assert.That(json["pid"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Null));
            Assert.That(json["ackAt"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Null));

            var parsed = EvaluationRunnerService.ParseRecord(json);
            Assert.That(parsed.CmdId, Is.EqualTo(record.CmdId));
            Assert.That(parsed.Argv, Is.EqualTo(record.Argv));
            Assert.That(parsed.Pid, Is.Null);
            Assert.That(parsed.StartedAt, Is.EqualTo(record.StartedAt));
        }

        [Test]
        public void LoadPending_SkipsAcknowledgedAndForeignFiles()
        {
            var pending = Save(NewRecord(null, "check", pid: MissingPid));
            var done = NewRecord("cmd-20261017T030406000-d4e5f6", "start", pid: MissingPid);
            done.AckAt = EvaluationState.FormatIso(DateTime.UtcNow);
            Save(done);
            File.WriteAllText(Path.Combine(PanelDir, pending.CmdId + ".result.json"), "{\"schema\":1}");
            File.WriteAllText(Path.Combine(PanelDir, "notes.json"), "{\"schema\":1}");

            var loaded = EvaluationRunnerService.LoadPending(_project);
            Assert.That(loaded.Select(r => r.CmdId), Is.EqualTo(new[] { pending.CmdId }));
        }

        [Test]
        public void Poll_ParsesTheResultFile()
        {
            var record = Save(NewRecord(null, "check", pid: MissingPid));
            File.WriteAllText(record.ResultFile,
                "{\"schema\":1,\"ok\":false,\"command\":\"check\",\"next\":null,\"exitCode\":2,\"finishedAt\":\"2026-10-17T03:04:09.000Z\"," +
                "\"cmdId\":\"" + record.CmdId + "\",\"problems\":[{\"code\":\"SCENE_DIRTY\",\"message\":\"m\",\"fix\":\"f\",\"args\":[\"Assets/A.unity\"]}]," +
                "\"data\":{\"ready\":false,\"notices\":[{\"code\":\"PROJECT_USER\",\"message\":\"m\",\"fix\":\"f\"}]}}");

            var outcome = EvaluationRunnerService.Poll(record, DateTime.UtcNow);
            Assert.That(outcome, Is.Not.Null);
            Assert.That(outcome.Ok, Is.False);
            Assert.That(outcome.ExitCode, Is.EqualTo(2));
            Assert.That(outcome.FinishedAt, Is.EqualTo("2026-10-17T03:04:09.000Z"));
            Assert.That(outcome.Problems.Single().Code, Is.EqualTo("SCENE_DIRTY"));
            Assert.That(outcome.Problems.Single().Args, Is.EqualTo(new[] { "Assets/A.unity" }));
            Assert.That(outcome.Notices.Single().Code, Is.EqualTo("PROJECT_USER"));
        }

        [Test]
        public void Poll_ExitedWithoutResult_IsACrashAfterTheGracePeriod()
        {
            var record = Save(NewRecord(null, "start", pid: MissingPid));
            var now = DateTime.UtcNow;

            Assert.That(EvaluationRunnerService.Poll(record, now), Is.Null, "The exit is only noted the first time.");
            Assert.That(EvaluationRunnerService.Poll(record, now.AddSeconds(1)), Is.Null);
            var outcome = EvaluationRunnerService.Poll(record, now.AddSeconds(3));

            Assert.That(outcome, Is.Not.Null);
            Assert.That(outcome.Problems.Single().Code, Is.EqualTo("RUNNER_CRASHED"));
            Assert.That(outcome.Problems.Single().Args, Is.EqualTo(new[] { record.LogFile }));
        }

        [Test]
        public void Poll_UnstartedRecord_WaitsBeforeGivingUp()
        {
            var record = Save(NewRecord(null, "check"));
            var started = EvaluationState.ParseIso(record.StartedAt).Value;

            Assert.That(EvaluationRunnerService.Poll(record, started.AddSeconds(5)), Is.Null);
            Assert.That(EvaluationRunnerService.Poll(record, started.AddSeconds(40)), Is.Null);
            Assert.That(EvaluationRunnerService.Poll(record, started.AddSeconds(43))?.Problems.Single().Code, Is.EqualTo("RUNNER_CRASHED"));
        }

        [Test]
        public void Poll_LiveProcess_IsStillRunning()
        {
            int pid;
            using (var self = Process.GetCurrentProcess()) pid = self.Id;
            var record = Save(NewRecord(null, "check", pid: pid));
            record.StartedAt = EvaluationState.FormatIso(DateTime.UtcNow);

            var now = DateTime.UtcNow;
            Assert.That(EvaluationRunnerService.Poll(record, now), Is.Null);
            Assert.That(EvaluationRunnerService.Poll(record, now.AddSeconds(10)), Is.Null);
        }

        [TestCase("{\"schema\":2,\"ok\":true}", "SCHEMA_UNSUPPORTED")]
        [TestCase("{\"ok\":true", "RESULT_UNREADABLE")]
        public void Poll_BadResultFile_IsReported(string content, string code)
        {
            var record = Save(NewRecord(null, "check", pid: MissingPid));
            File.WriteAllText(record.ResultFile, content);
            Assert.That(EvaluationRunnerService.Poll(record, DateTime.UtcNow)?.Problems.Single().Code, Is.EqualTo(code));
        }

        [Test]
        public void Acknowledge_WritesAckAtSoTheCommandIsNoLongerPending()
        {
            var record = Save(NewRecord(null, "check", pid: MissingPid));
            var now = new DateTime(2026, 10, 17, 3, 5, 0, DateTimeKind.Utc);
            EvaluationRunnerService.Acknowledge(_project, record, now);

            Assert.That(EvaluationRunnerService.LoadPending(_project), Is.Empty);
            var json = EvaluationState.ParseObject(File.ReadAllText(EvaluationRunnerService.CommandFile(_project, record.CmdId)));
            Assert.That(json.Value<string>("ackAt"), Is.EqualTo("2026-10-17T03:05:00.000Z"));
        }

        [Test]
        public void Prune_KeepsTheNewestGroupsAndEveryPendingOne()
        {
            var ids = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var record = NewRecord($"cmd-20261017T03040{i}000-a1b2c{i}", "check", pid: MissingPid);
                record.AckAt = i == 0 ? null : EvaluationState.FormatIso(DateTime.UtcNow);
                Save(record);
                File.WriteAllText(record.ResultFile, "{\"schema\":1}");
                File.WriteAllText(record.LogFile, "log");
                ids.Add(record.CmdId);
            }

            EvaluationRunnerService.Prune(_project, 3);

            var left = EvaluationRunnerService.LoadRecords(_project).Select(r => r.CmdId).ToArray();
            Assert.That(left, Is.EqualTo(new[] { ids[0], ids[4], ids[5] }), "The oldest group is pending, so it survives.");
            Assert.That(File.Exists(Path.Combine(PanelDir, ids[1] + ".result.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(PanelDir, ids[3] + ".log")), Is.False);
        }

        [Test]
        public void LoadLatestOutcomes_ReturnsTheNewestConsumedResultPerCommand()
        {
            var older = NewRecord("cmd-20261017T030401000-a1b2c3", "check", pid: MissingPid);
            var newer = NewRecord("cmd-20261017T030402000-d4e5f6", "check", pid: MissingPid);
            foreach (var record in new[] { older, newer })
            {
                record.AckAt = EvaluationState.FormatIso(DateTime.UtcNow);
                Save(record);
                File.WriteAllText(record.ResultFile, "{\"schema\":1,\"ok\":true,\"exitCode\":0,\"problems\":[],\"data\":{\"ready\":true}}");
            }

            var latest = EvaluationRunnerService.LoadLatestOutcomes(_project);
            Assert.That(latest.Keys, Is.EqualTo(new[] { "check" }));
            Assert.That(latest["check"].CmdId, Is.EqualTo(newer.CmdId));
            Assert.That(latest["check"].Ok, Is.True);
        }

        [Test]
        public void BuildDeclaredJson_OnlyCarriesWhatWasDeclared()
        {
            Assert.That(EvaluationRunnerService.BuildDeclaredJson("", "  ", null), Is.Null);

            var json = EvaluationState.ParseObject(EvaluationRunnerService.BuildDeclaredJson("claude-code", " opus ", null));
            Assert.That(json.Value<int>("schema"), Is.EqualTo(1));
            Assert.That(json["host"]["value"].ToString(), Is.EqualTo("claude-code"));
            Assert.That(json["host"]["source"].ToString(), Is.EqualTo("user"));
            Assert.That(json["model"]["value"].ToString(), Is.EqualTo("opus"));
            Assert.That(json["effort"], Is.Null);
        }

        [Test]
        public void IsRunnerBusy_FollowsTheLockHolder()
        {
            var now = DateTime.UtcNow;
            Assert.That(EvaluationRunnerService.IsRunnerBusy(_project, now, out _), Is.False);

            int pid;
            using (var self = Process.GetCurrentProcess()) pid = self.Id;
            var lockFile = EvaluationState.LockFile(_project);
            EvaluationState.WriteTextAtomic(lockFile, "{\"schema\":1,\"pid\":" + pid + ",\"cmd\":\"next\",\"launcher\":\"agent\"}");
            Assert.That(EvaluationRunnerService.IsRunnerBusy(_project, now, out var holder), Is.True);
            Assert.That(holder.Command, Is.EqualTo("next"));

            EvaluationState.WriteTextAtomic(lockFile, "{\"schema\":1,\"pid\":" + MissingPid + ",\"cmd\":\"next\"}");
            Assert.That(EvaluationRunnerService.IsRunnerBusy(_project, now, out _), Is.False, "A dead holder is a stale lock.");

            File.WriteAllText(lockFile, string.Empty);
            Assert.That(EvaluationRunnerService.IsRunnerBusy(_project, DateTime.UtcNow, out _), Is.True, "A lock being written is busy.");
            Assert.That(EvaluationRunnerService.IsRunnerBusy(_project, DateTime.UtcNow.AddSeconds(30), out _), Is.False);
        }

        private string PanelDir => EvaluationState.PanelDir(_project);

        /// <summary>A record as the panel writes it; a null id gets a fresh one (the tracker keeps per-id state).</summary>
        private PanelCommand NewRecord(string cmdId, string command, int? pid = null)
        {
            cmdId = cmdId ?? EvaluationRunnerService.NewCommandId(DateTime.UtcNow);
            Assert.That(Regex.IsMatch(cmdId, @"^cmd-\d{8}T\d{9}-[0-9a-f]{6}$"), cmdId);
            return new PanelCommand
            {
                CmdId = cmdId,
                Command = command,
                ResultFile = Path.Combine(PanelDir, cmdId + ".result.json"),
                LogFile = Path.Combine(PanelDir, cmdId + ".log"),
                StartedAt = EvaluationState.FormatIso(DateTime.UtcNow),
                Pid = pid,
            };
        }

        private PanelCommand Save(PanelCommand record)
        {
            EvaluationRunnerService.WriteRecord(_project, record);
            return record;
        }
    }
}

// Producer:Betsy
