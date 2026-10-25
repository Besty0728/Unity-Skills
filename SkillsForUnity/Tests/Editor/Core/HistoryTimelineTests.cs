using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class HistoryTimelineTests
    {
        private const string Caller = "Caller";
        private const string Other = "OtherAI";

        [Test]
        public void CheckpointsHeadTheirStepsAboveThem()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(3), Checkpoint(1) },
                steps: new[] { Step(1), Step(2), Step(3), Step(4) });

            var rows = HistoryTabController.BuildTimelineRows(history, null, Caller);

            Assert.That(rows.Select(row => row.Id).ToList(), Is.EqualTo(new[] { "c3", "s4", "s3", "c1", "s2", "s1" }));
            Assert.That(rows.Single(row => row.Id == "c3").StepCount, Is.EqualTo(2));
            Assert.That(rows.Single(row => row.Id == "c1").StepCount, Is.EqualTo(2));
        }

        [Test]
        public void AutoCheckpointIsFlagged()
        {
            var history = History(
                checkpoints: new[]
                {
                    Checkpoint(3, kind: WorkflowCheckpoint.KindAuto),
                    Checkpoint(1, kind: WorkflowCheckpoint.KindExplicit, label: "before lighting"),
                },
                steps: new[] { Step(1), Step(3) });

            var rows = HistoryTabController.BuildTimelineRows(history, null, Caller);

            var auto = rows.Single(row => row.Id == "c3");
            Assert.That(auto.IsAuto, Is.True);
            Assert.That(auto.Label, Is.Null);

            var explicitRow = rows.Single(row => row.Id == "c1");
            Assert.That(explicitRow.IsAuto, Is.False);
            Assert.That(explicitRow.Label, Is.EqualTo("before lighting"));
        }

        [Test]
        public void UndoneStepsAreFlagged()
        {
            var history = History(
                steps: new[] { Step(1), Step(2), Step(3) },
                restores: new[] { new WorkflowRestoreRecord { seq = 4, to = "c1", steps = new List<long> { 2 } } });

            var rows = HistoryTabController.BuildTimelineRows(history, null, Caller);

            Assert.That(rows.Single(row => row.Id == "s2").IsReverted, Is.True);
            Assert.That(rows.Single(row => row.Id == "s1").IsReverted, Is.False);
            Assert.That(rows.Single(row => row.Id == "s3").IsReverted, Is.False);
        }

        [Test]
        public void AgentFilterKeepsOnlyThatAgentsRows()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(1, agent: Caller), Checkpoint(2, agent: Other) },
                steps: new[] { Step(1, agent: Caller), Step(2, agent: Other), Step(3, agent: Caller) });

            var rows = HistoryTabController.BuildTimelineRows(history, Caller, Caller);

            Assert.That(rows.Select(row => row.Id).ToList(), Is.EqualTo(new[] { "s3", "c1", "s1" }));
            Assert.That(rows.All(row => row.Agent == Caller), Is.True);
            Assert.That(rows.Single(row => row.Id == "c1").StepCount, Is.EqualTo(1));
        }

        [Test]
        public void RowsAreNewestFirstAndMarkOtherAgents()
        {
            var history = History(steps: new[] { Step(1), Step(2, agent: Other), Step(3) });

            var rows = HistoryTabController.BuildTimelineRows(history, null, Caller);

            Assert.That(rows.Select(row => row.Id).ToList(), Is.EqualTo(new[] { "s3", "s2", "s1" }));
            Assert.That(rows.Single(row => row.Id == "s2").IsOtherAgent, Is.True);
            Assert.That(rows.Single(row => row.Id == "s3").IsOtherAgent, Is.False);
        }

        private static WorkflowHistoryData History(WorkflowCheckpoint[] checkpoints = null, WorkflowTask[] steps = null,
            WorkflowRestoreRecord[] restores = null)
        {
            var history = new WorkflowHistoryData();
            history.checkpoints.AddRange(checkpoints ?? Array.Empty<WorkflowCheckpoint>());
            history.tasks.AddRange(steps ?? Array.Empty<WorkflowTask>());
            history.restores.AddRange(restores ?? Array.Empty<WorkflowRestoreRecord>());
            return history;
        }

        private static WorkflowCheckpoint Checkpoint(long seq, string kind = WorkflowCheckpoint.KindExplicit,
            string label = null, string agent = Caller) => new WorkflowCheckpoint
        {
            id = "c" + seq,
            seq = seq,
            timestampMs = 1_000_000 + seq * 1000,
            agent = agent,
            kind = kind,
            label = label,
        };

        private static WorkflowTask Step(long seq, string agent = Caller) => new WorkflowTask
        {
            id = "task-" + seq,
            seq = seq,
            tag = "tag" + seq,
            agent = agent,
            timestamp = 1000 + seq,
        };
    }
}

// Producer:Betsy
