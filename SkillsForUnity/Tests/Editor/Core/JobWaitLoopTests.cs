using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// GET /jobs/{id}?wait= long polling. The responder itself runs on a ThreadPool thread against a live listener,
    /// so these drive its three testable parts: the query parser, the loop (with a fake clock, probe and abort
    /// signal) and the response builder; plus the main-thread snapshot that adds resultData for a probe.
    /// </summary>
    [TestFixture]
    public class JobWaitLoopTests
    {
        private const string RunningSnapshot = "{\"jobId\":\"abc\",\"status\":\"running\",\"terminal\":false}";
        private const string DoneSnapshot = "{\"jobId\":\"abc\",\"status\":\"completed\",\"terminal\":true,\"resultData\":{\"compilation\":{\"hasErrors\":false}}}";

        // ---------- parsing ----------

        [TestCase("/jobs/abc", "?wait=5", 5.0)]
        [TestCase("/jobs/abc/", "?WAIT=2.5", 2.5)]
        [TestCase("/jobs/abc", "?wait=0", 0.0)]
        [TestCase("/jobs/abc", "?wait=900&recentCount=3", 120.0)]
        public void TryParseJobWait_AcceptsSecondsAndClamps(string path, string query, double expected)
        {
            Assert.That(SkillsHttpServer.TryParseJobWait(path, query, out var jobId, out var seconds, out var error), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(jobId, Is.EqualTo("abc"));
            Assert.That(seconds, Is.EqualTo(expected));
        }

        [TestCase("/jobs/abc", null)]
        [TestCase("/jobs/abc", "?recentCount=3")]
        [TestCase("/jobs/abc", "?wait=")]
        [TestCase("/jobs/abc/progress", "?wait=5")]
        [TestCase("/jobs/abc/logs", "?wait=5")]
        [TestCase("/jobs", "?wait=5")]
        [TestCase("/skill/job_wait", "?wait=5")]
        public void TryParseJobWait_LeavesEveryOtherRequestOnTheOldPath(string path, string query)
        {
            Assert.That(SkillsHttpServer.TryParseJobWait(path, query, out _, out _, out _), Is.False);
        }

        [TestCase("?wait=soon")]
        [TestCase("?wait=-1")]
        [TestCase("?wait=NaN")]
        public void TryParseJobWait_RejectsANonNumber(string query)
        {
            Assert.That(SkillsHttpServer.TryParseJobWait("/jobs/abc", query, out _, out _, out var error), Is.True);
            var body = JObject.Parse(error);
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("TYPE_MISMATCH"));
            Assert.That(body["details"]?["parameter"]?.ToString(), Is.EqualTo("wait"));
        }

        // ---------- the loop ----------

        [Test]
        public void Loop_ReturnsTheFirstTerminalSnapshot()
        {
            var clock = new FakeClock();
            var answers = new Queue<string>(new[] { RunningSnapshot, RunningSnapshot, DoneSnapshot });

            var outcome = Run(10, clock, _ => Answer(200, answers.Dequeue()));

            Assert.That(Verdict(outcome), Is.EqualTo("Terminal"));
            Assert.That(Probes(outcome), Is.EqualTo(3));
            Assert.That(clock.Sleeps, Is.EqualTo(new[] { 500, 500 }), "One poll interval between consecutive probes.");
        }

        [Test]
        public void Loop_TimesOutWithTheLatestSnapshot()
        {
            var clock = new FakeClock();

            var outcome = Run(2, clock, _ => Answer(200, RunningSnapshot));

            Assert.That(Verdict(outcome), Is.EqualTo("TimedOut"));
            Assert.That(Probes(outcome), Is.EqualTo(4), "Probes at 0, 0.5, 1.0 and 1.5 s; the deadline ends the wait at 2 s.");
            Assert.That(clock.ElapsedMs, Is.EqualTo(2000));
            Assert.That(LastJson(outcome), Is.EqualTo(RunningSnapshot));
        }

        [Test]
        public void Loop_WaitZero_IsASingleProbe()
        {
            var clock = new FakeClock();

            var outcome = Run(0, clock, _ => Answer(200, RunningSnapshot));

            Assert.That(Verdict(outcome), Is.EqualTo("TimedOut"));
            Assert.That(Probes(outcome), Is.EqualTo(1));
            Assert.That(clock.Sleeps, Is.Empty);
        }

        [Test]
        public void Loop_FirstProbeBudget_HasAFloorEvenUnderWaitZero()
        {
            var budgets = new List<int>();
            var clock = new FakeClock();

            Run(0, clock, budget => { budgets.Add(budget); return Answer(200, RunningSnapshot); });
            Run(30, new FakeClock(), budget => { budgets.Add(budget); return Answer(200, DoneSnapshot); });

            Assert.That(budgets[0], Is.GreaterThanOrEqualTo(10000),
                "A busy main thread must get as long to answer the first probe as a plain GET /jobs/{id} gets.");
            Assert.That(budgets[1], Is.EqualTo(30000));
        }

        [Test]
        public void Loop_LaterProbes_NeverOutliveTheDeadline()
        {
            var budgets = new List<int>();
            var clock = new FakeClock();

            Run(1.2, clock, budget => { budgets.Add(budget); return Answer(200, RunningSnapshot); });

            Assert.That(budgets.Count, Is.GreaterThan(1));
            for (int i = 1; i < budgets.Count; i++)
                Assert.That(budgets[i], Is.LessThanOrEqualTo(1200 - 500 * i));
        }

        [Test]
        public void Loop_AbortsWhenTheServerStopsOrReloads()
        {
            var clock = new FakeClock();
            bool aborting = false;

            var outcome = SkillsHttpServer.RunJobWaitLoop(60, 500,
                _ => { aborting = true; return Answer(200, RunningSnapshot); },
                () => aborting, clock.Now, clock.Sleep);

            Assert.That(Verdict(outcome), Is.EqualTo("Aborted"));
            Assert.That(clock.Sleeps, Is.Empty, "The loop must not sleep once the server is going away.");
        }

        [Test]
        public void Loop_TerminalSnapshot_WinsOverAConcurrentAbort()
        {
            var clock = new FakeClock();
            bool aborting = false;

            var outcome = SkillsHttpServer.RunJobWaitLoop(60, 500,
                _ => { aborting = true; return Answer(200, DoneSnapshot); },
                () => aborting, clock.Now, clock.Sleep);

            Assert.That(Verdict(outcome), Is.EqualTo("Terminal"));
        }

        [Test]
        public void Loop_PassesANon200AnswerThrough()
        {
            var outcome = Run(60, new FakeClock(), _ => Answer(404, "{\"status\":\"error\",\"errorCode\":\"NOT_FOUND\"}"));

            Assert.That(Verdict(outcome), Is.EqualTo("Passthrough"));
            Assert.That(LastStatus(outcome), Is.EqualTo(404));
        }

        [Test]
        public void Loop_StoppingQueue503_BecomesAnAbort()
        {
            bool aborting = false;
            var clock = new FakeClock();

            var outcome = SkillsHttpServer.RunJobWaitLoop(60, 500,
                _ => { aborting = true; return Answer(503, "{\"status\":\"error\",\"errorCode\":\"SERVER_STOPPED\"}"); },
                () => aborting, clock.Now, clock.Sleep);

            Assert.That(Verdict(outcome), Is.EqualTo("Aborted"));
        }

        [Test]
        public void Loop_UnansweredFirstProbe_IsNoAnswer()
        {
            var outcome = Run(5, new FakeClock(), _ => default);

            Assert.That(Verdict(outcome), Is.EqualTo("NoAnswer"));
            Assert.That(Probes(outcome), Is.EqualTo(1), "An unanswered probe is waited on, never re-queued.");
        }

        [Test]
        public void Loop_UnansweredLaterProbe_TimesOutWithThePreviousSnapshot()
        {
            int calls = 0;
            var outcome = Run(5, new FakeClock(), _ => ++calls == 1 ? Answer(200, RunningSnapshot) : default);

            Assert.That(Verdict(outcome), Is.EqualTo("TimedOut"));
            Assert.That(LastJson(outcome), Is.EqualTo(RunningSnapshot));
        }

        // ---------- the response ----------

        [Test]
        public void Response_Terminal_IsTheSnapshotWithWaitTimedOutFalse()
        {
            var (status, json) = Respond(Run(10, new FakeClock(), _ => Answer(200, DoneSnapshot)), reloadPending: false);
            var body = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(200));
            Assert.That(body["waitTimedOut"]?.Value<bool>(), Is.False);
            Assert.That(body["resultData"]?["compilation"]?["hasErrors"]?.Value<bool>(), Is.False);
            Assert.That(body["hint"], Is.Null);
        }

        [Test]
        public void Response_TimedOut_SaysToCallTheSameUrlAgain()
        {
            var (status, json) = Respond(Run(1, new FakeClock(), _ => Answer(200, RunningSnapshot)), reloadPending: false);
            var body = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(200));
            Assert.That(body["waitTimedOut"]?.Value<bool>(), Is.True);
            Assert.That(body["terminal"]?.Value<bool>(), Is.False);
            StringAssert.Contains("same URL", body["hint"]?.ToString());
        }

        [TestCase(true, "COMPILING")]
        [TestCase(false, "SERVER_STOPPED")]
        public void Response_Aborted_Is503WaitAndRetry(bool reloadPending, string expectedCode)
        {
            bool aborting = true;
            var clock = new FakeClock();
            var outcome = SkillsHttpServer.RunJobWaitLoop(60, 500, _ => Answer(200, RunningSnapshot), () => aborting, clock.Now, clock.Sleep);

            var (status, json) = Respond(outcome, reloadPending);
            var body = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(503));
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo(expectedCode));
            Assert.That(body["retryStrategy"]?.ToString(), Is.EqualTo("wait_and_retry"));
            Assert.That(body["details"]?["hint"]?.ToString(), Is.EqualTo("server reloading; retry the same URL"));
            Assert.That(body["details"]?["jobId"]?.ToString(), Is.EqualTo("abc"));
        }

        [Test]
        public void Response_NoAnswer_Is504WaitAndRetry()
        {
            var (status, json) = Respond(Run(5, new FakeClock(), _ => default), reloadPending: false);
            var body = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(504));
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("TIMEOUT"));
            Assert.That(body["retryStrategy"]?.ToString(), Is.EqualTo("wait_and_retry"));
        }

        [Test]
        public void Response_Passthrough_IsUntouched()
        {
            const string notFound = "{\"status\":\"error\",\"errorCode\":\"NOT_FOUND\"}";
            var (status, json) = Respond(Run(5, new FakeClock(), _ => Answer(404, notFound)), reloadPending: false);

            Assert.That(status, Is.EqualTo(404));
            Assert.That(json, Is.EqualTo(notFound));
        }

        // ---------- the main-thread snapshot ----------

        [Test]
        public void Snapshot_CarriesResultData_OnlyForATerminalProbe()
        {
            string jobId = "w1ctest" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var record = new BatchJobRecord
            {
                jobId = jobId,
                kind = "compile",
                status = "completed",
                progress = 100,
                startedAt = 1,
                updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                resultData = new Dictionary<string, object> { ["compilation"] = new Dictionary<string, object> { ["hasErrors"] = true } },
            };
            BatchPersistence.UpsertJob(record);
            try
            {
                var plain = JObject.Parse(ProcessJobsRequest("/jobs/" + jobId, internalProbe: false));
                var probed = JObject.Parse(ProcessJobsRequest("/jobs/" + jobId, internalProbe: true));

                Assert.That(plain.Property("resultData"), Is.Null, "The plain GET /jobs/{id} shape must not change.");
                Assert.That(probed["terminal"]?.Value<bool>(), Is.True);
                Assert.That(probed["resultData"]?["compilation"]?["hasErrors"]?.Value<bool>(), Is.True);

                record.status = "running";
                BatchPersistence.UpsertJob(record);
                var running = JObject.Parse(ProcessJobsRequest("/jobs/" + jobId, internalProbe: true));
                Assert.That(running.Property("resultData"), Is.Null, "A job still running has no result to hand back yet.");
            }
            finally
            {
                BatchPersistence.RemoveJob(jobId);
                BatchPersistence.FlushIfDirty();
            }
        }

        // ---------- helpers ----------

        private sealed class FakeClock
        {
            private long _ticks = 638_000_000_000_000_000L;
            public readonly List<int> Sleeps = new List<int>();
            public long ElapsedMs;

            public long Now() => _ticks;

            public void Sleep(int milliseconds)
            {
                Sleeps.Add(milliseconds);
                ElapsedMs += milliseconds;
                _ticks += milliseconds * TimeSpan.TicksPerMillisecond;
            }
        }

        private static SkillsHttpServer.JobWaitOutcome Run(double waitSeconds, FakeClock clock,
            Func<int, SkillsHttpServer.JobProbeResult> probe) =>
            SkillsHttpServer.RunJobWaitLoop(waitSeconds, 500, probe, () => false, clock.Now, clock.Sleep);

        private static SkillsHttpServer.JobProbeResult Answer(int statusCode, string json) =>
            new SkillsHttpServer.JobProbeResult { Answered = true, StatusCode = statusCode, Json = json };

        private static string Verdict(SkillsHttpServer.JobWaitOutcome outcome) => outcome.Verdict.ToString();
        private static int Probes(SkillsHttpServer.JobWaitOutcome outcome) => outcome.Probes;
        private static string LastJson(SkillsHttpServer.JobWaitOutcome outcome) => outcome.Last.Json;
        private static int LastStatus(SkillsHttpServer.JobWaitOutcome outcome) => outcome.Last.StatusCode;

        private static (int StatusCode, string Json) Respond(SkillsHttpServer.JobWaitOutcome outcome, bool reloadPending) =>
            SkillsHttpServer.BuildJobWaitResponse(outcome, 10, "abc", reloadPending);

        /// <summary>Runs GET {path} through SkillsHttpServer.ProcessJob, optionally flagged as a long-poll probe.</summary>
        private static string ProcessJobsRequest(string path, bool internalProbe)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, "GET");
            jobType.GetField("Path").SetValue(job, path);
            jobType.GetField("QueryString").SetValue(job, "?wait=5");
            jobType.GetField("StatusCode").SetValue(job, 200);
            jobType.GetField("IsInternalProbe").SetValue(job, internalProbe);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            Assert.That((int)jobType.GetField("StatusCode").GetValue(job), Is.EqualTo(200));
            return (string)jobType.GetField("ResponseJson").GetValue(job);
        }
    }
}

// Producer:Betsy
