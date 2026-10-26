using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The pure parts of the Evaluation panel: the grade thresholds, the model list, the manifest's task ids, the
    /// All-tasks prompt and the merge of a panel group onto the runner's sessions. No files and no UI are involved.
    /// </summary>
    [TestFixture]
    public class EvaluationPanelGroupTests
    {
        private const string ManifestJson = @"{
            ""schema"": 1,
            ""tracks"": {
                ""ops"": { ""basic"": [""S01"", ""S03""], ""standard"": [""T3"", ""T4"", ""T5""], ""challenge"": [] },
                ""dev"": { ""basic"": [""DB1""], ""standard"": [], ""challenge"": [] }
            },
            ""tasks"": {
                ""S01"": { ""requires"": { ""pipeline"": [""URP"", ""BuiltIn""] } },
                ""S03"": { ""requires"": { ""pipeline"": [""URP"", ""BuiltIn""] } },
                ""T3"": { ""requires"": { ""pipeline"": ""URP"" } },
                ""T4"": { ""requires"": { ""pipeline"": ""URP"" } },
                ""T5"": { ""requires"": { ""pipeline"": ""URP"" } },
                ""DB1"": { ""requires"": { ""pipeline"": [""URP"", ""BuiltIn""] } }
            }
        }";

        private static readonly DateTime Started = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

        [TestCase(9, 0, 1, "excellent")]
        [TestCase(8, 1, 1, "good")]
        [TestCase(7, 2, 1, "good")]
        [TestCase(6, 3, 1, "pass")]
        [TestCase(5, 0, 5, "pass")]
        [TestCase(4, 1, 5, "needs_work")]
        public void GradeFor_FollowsTheRunnerThresholds(int pass, int partial, int fail, string expected)
        {
            Assert.That(EvaluationState.GradeFor(pass, partial, fail), Is.EqualTo(expected));
        }

        [Test]
        public void GradeFor_WithNothingGradableHasNoGrade()
        {
            Assert.That(EvaluationState.GradeFor(0, 0, 0), Is.Null);
        }

        [Test]
        public void ModelNames_CollectsDistinctNamesInFirstSeenOrder()
        {
            var tasks = new[]
            {
                JObject.Parse(@"{ ""taskId"": ""T3"", ""usage"": { ""model"": { ""values"": [""claude-opus-5-5"", ""claude-sonnet-5-5""] } } }"),
                JObject.Parse(@"{ ""taskId"": ""T4"", ""usage"": { ""model"": { ""values"": [""claude-opus-5-5"", """"] } } }"),
                JObject.Parse(@"{ ""taskId"": ""T5"", ""usage"": { ""model"": { ""values"": [7] } } }"),
                JObject.Parse(@"{ ""taskId"": ""T7"" }"),
            };
            Assert.That(EvaluationState.ModelNames(tasks), Is.EqualTo(new[] { "claude-opus-5-5", "claude-sonnet-5-5" }));
        }

        [Test]
        public void ManifestTaskIds_ReadsTheTierInPackOrder()
        {
            var manifest = JObject.Parse(ManifestJson);
            Assert.That(EvaluationState.ManifestTaskIds(manifest, "ops", "standard"), Is.EqualTo(new[] { "T3", "T4", "T5" }));
            Assert.That(EvaluationState.ManifestTaskIds(manifest, "dev", "challenge"), Is.Empty);
            Assert.That(EvaluationState.ManifestTaskIds(manifest, "ops", "missing"), Is.Empty);
            Assert.That(EvaluationState.ManifestTaskIds(null, "ops", "basic"), Is.Empty);
        }

        [Test]
        public void AllCombos_KeepsOnlyTheTiersThePipelineRuns()
        {
            var manifest = JObject.Parse(ManifestJson);
            var urp = EvaluationState.AllCombos(manifest, "URP").Select(combo => combo.Track + " " + combo.Tier).ToArray();
            var builtIn = EvaluationState.AllCombos(manifest, "BuiltIn").Select(combo => combo.Track + " " + combo.Tier).ToArray();

            Assert.That(urp, Is.EqualTo(new[] { "ops basic", "ops standard", "dev basic" }));
            Assert.That(builtIn, Is.EqualTo(new[] { "ops basic", "dev basic" }), "A Built-in project runs the basic tiers only.");
        }

        [Test]
        public void PipelineAccepts_ReadsNameListsNullAndTheURPDefault()
        {
            var manifest = JObject.Parse(@"{ ""tasks"": {
                ""A"": { ""requires"": { ""pipeline"": ""BuiltIn"" } },
                ""B"": { ""requires"": { ""pipeline"": null } },
                ""C"": { ""requires"": { } } } }");
            Assert.That(EvaluationState.PipelineAccepts(manifest, "A", "BuiltIn"), Is.True);
            Assert.That(EvaluationState.PipelineAccepts(manifest, "A", "URP"), Is.False);
            Assert.That(EvaluationState.PipelineAccepts(manifest, "B", "HDRP"), Is.True);
            Assert.That(EvaluationState.PipelineAccepts(manifest, "C", "URP"), Is.True);
            Assert.That(EvaluationState.PipelineAccepts(manifest, "C", "BuiltIn"), Is.False);
        }

        [Test]
        public void BuildGroupPrompt_ListsTheStartCommandsInOrder()
        {
            var prompt = EvaluationState.BuildGroupPrompt("/usr/bin/python3", "/Pkg/Evaluation~/run_eval.py",
                "/Work/Proj", "en", EvaluationState.AllCombos(JObject.Parse(ManifestJson), "URP"));

            var lines = new[] { "--track ops --tier basic", "--track ops --tier standard", "--track dev --tier basic" };
            var positions = lines.Select(line => prompt.IndexOf(line, StringComparison.Ordinal)).ToArray();
            Assert.That(positions, Has.None.EqualTo(-1), "Every combo has its start command.");
            Assert.That(positions, Is.Ordered.Ascending, "The commands follow the group's order.");
            StringAssert.Contains("\"/usr/bin/python3\" -B \"/Pkg/Evaluation~/run_eval.py\" start --track ops --tier basic " +
                                  "--mode suite --lang en --project \"/Work/Proj\"", prompt);
            Assert.That(prompt.Where(c => c > 127).ToArray(), Is.Empty, "The agent protocol text stays ASCII.");
        }

        [Test]
        public void MergeGroup_IgnoresOlderSessionsAndTakesTheNewestOfEachCombo()
        {
            var group = GroupOf(("ops", "basic"), ("ops", "standard"));
            var sessions = new List<EvaluationState.SessionInfo>
            {
                SessionOf("s20261026T100500aaaaaa", "ops", "basic", "S01", "S03"),
                SessionOf("s20261026T100100dddddd", "ops", "standard"),
                SessionOf("s20261026T100000bbbbbb", "ops", "basic", "S01", "S03"),
                SessionOf("s20261026T090000cccccc", "ops", "basic", "S01", "S03"),
            };
            sessions[0].Tasks["S01"].State = "done";
            sessions[0].Tasks["S03"].State = "pending";

            var progress = EvaluationState.MergeGroup(group, sessions, _ => null, JObject.Parse(ManifestJson));

            Assert.That(progress.Combos[0].Session.SessionId, Is.EqualTo("s20261026T100500aaaaaa"));
            Assert.That(progress.Combos[1].Session.SessionId, Is.EqualTo("s20261026T100100dddddd"));
            Assert.That(progress.Tasks.Count, Is.EqualTo(5));
            Assert.That(progress.FinishedTasks, Is.EqualTo(1));
            Assert.That(progress.Complete, Is.False);
        }

        [Test]
        public void MergeGroup_CountsASessionStartedInTheSameSecondAsTheGroup()
        {
            var group = GroupOf(("ops", "basic"));
            group.StartedUtc = Started.AddMilliseconds(750);
            var sessions = new List<EvaluationState.SessionInfo> { SessionOf("s20261026T100000bbbbbb", "ops", "basic", "S01") };

            var progress = EvaluationState.MergeGroup(group, sessions, _ => null, JObject.Parse(ManifestJson));

            Assert.That(progress.Combos[0].Session, Is.Not.Null);
        }

        [Test]
        public void MergeGroup_CountsAMissingCombosTasksFromTheManifest()
        {
            var group = GroupOf(("ops", "basic"), ("ops", "standard"));
            var progress = EvaluationState.MergeGroup(group, new List<EvaluationState.SessionInfo>(), _ => null,
                JObject.Parse(ManifestJson));

            Assert.That(progress.Combos.All(combo => combo.Session == null), Is.True);
            Assert.That(progress.Tasks.Select(task => task.TaskId).ToArray(), Is.EqualTo(new[] { "S01", "S03", "T3", "T4", "T5" }));
            Assert.That(progress.FinishedTasks, Is.EqualTo(0));
        }

        [Test]
        public void MergeGroup_CompletesOnceEveryComboHasAReportAndSummarizesIt()
        {
            var group = GroupOf(("ops", "basic"), ("ops", "standard"));
            var basic = SessionOf("s20261026T100500aaaaaa", "ops", "basic", "S01", "S03");
            var standard = SessionOf("s20261026T100600eeeeee", "ops", "standard", "T3", "T4", "T5");
            var reports = new Dictionary<string, JObject>(StringComparer.Ordinal)
            {
                [basic.SessionId] = JObject.Parse(@"{ ""verdict"": ""Good work"", ""tasks"": [
                    { ""taskId"": ""S01"", ""status"": ""pass"", ""completion"": 100, ""usage"": { ""model"": { ""values"": [""claude-opus-5-5""] } } },
                    { ""taskId"": ""S03"", ""status"": ""fail"", ""completion"": 40 } ] }"),
                [standard.SessionId] = JObject.Parse(@"{ ""verdict"": ""Solid"", ""tasks"": [
                    { ""taskId"": ""T3"", ""status"": ""partial"", ""completion"": 75 },
                    { ""taskId"": ""T4"", ""status"": ""pass"", ""completion"": 100 },
                    { ""taskId"": ""T5"", ""status"": ""pass"", ""completion"": 100 } ] }"),
            };

            var progress = EvaluationState.MergeGroup(group,
                new List<EvaluationState.SessionInfo> { standard, basic },
                session => reports.TryGetValue(session.SessionId, out var report) ? report : null,
                JObject.Parse(ManifestJson));
            var summary = EvaluationState.SummarizeGroup(progress);

            Assert.That(progress.Complete, Is.True);
            Assert.That(progress.FinishedSessions, Is.EqualTo(2));
            Assert.That(summary.Tasks, Is.EqualTo(5));
            Assert.That(summary.Pass, Is.EqualTo(3));
            Assert.That(summary.Partial, Is.EqualTo(1));
            Assert.That(summary.Fail, Is.EqualTo(1));
            Assert.That(summary.Grade, Is.EqualTo("pass"), "3 of 5 gradable tasks passed is 60 percent.");
            Assert.That(summary.MeanCompletion, Is.EqualTo(83.0).Within(0.001));
            Assert.That(summary.Models, Is.EqualTo(new[] { "claude-opus-5-5" }));
            Assert.That(progress.Combos[0].Report["verdict"]?.ToString(), Is.EqualTo("Good work"));
        }

        private static EvaluationState.GroupRecord GroupOf(params (string Track, string Tier)[] combos)
        {
            var record = new EvaluationState.GroupRecord { StartedUtc = Started, Lang = "en" };
            foreach (var combo in combos)
                record.Combos.Add(new EvaluationState.GroupCombo { Track = combo.Track, Tier = combo.Tier });
            return record;
        }

        private static EvaluationState.SessionInfo SessionOf(string sessionId, string track, string tier, params string[] taskIds)
        {
            var session = new EvaluationState.SessionInfo
            {
                SessionId = sessionId,
                Status = "completed",
                Mode = "suite",
                Lang = "en",
                Track = track,
                Tier = tier,
            };
            session.Order.AddRange(taskIds);
            foreach (var taskId in taskIds) session.Tasks[taskId] = new EvaluationState.TaskEntry { State = "pending" };
            return session;
        }
    }
}

// Producer:Betsy
