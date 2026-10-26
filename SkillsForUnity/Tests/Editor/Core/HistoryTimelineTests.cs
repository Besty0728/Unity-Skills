using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class HistoryTimelineTests
    {
        private const string Caller = "Caller";
        private const string Other = "OtherAI";

        [Test]
        public void GroupsHeadTheirStepsNewestFirst()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(3), Checkpoint(1) },
                steps: new[] { Step(1), Step(2), Step(3), Step(4) });

            var groups = Groups(history);

            Assert.That(groups.Select(group => group.Key).ToList(), Is.EqualTo(new[] { "c3", "c1" }));
            Assert.That(StepIds(groups[0]), Is.EqualTo(new[] { "s4", "s3" }));
            Assert.That(StepIds(groups[1]), Is.EqualTo(new[] { "s2", "s1" }));
            Assert.That(groups[0].StepCount, Is.EqualTo(2));
        }

        [Test]
        public void StepsBeforeTheFirstCheckpointFormTheirOwnGroup()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(3) },
                steps: new[] { Step(1), Step(2), Step(3), Step(4) });

            var groups = Groups(history);

            Assert.That(groups.Select(group => group.Key).ToList(), Is.EqualTo(new[] { "c3", HistoryTabController.PreGroupKey }));
            var before = groups[1];
            Assert.That(before.Checkpoint, Is.Null);
            Assert.That(before.TitleKind, Is.EqualTo(HistoryTitleKind.BeforeFirstCheckpoint));
            Assert.That(StepIds(before), Is.EqualTo(new[] { "s2", "s1" }));
        }

        [Test]
        public void TitleKindFollowsTheCheckpointKind()
        {
            Assert.That(HistoryTabController.TitleKindOf(Checkpoint(1, kind: WorkflowCheckpoint.KindAuto)), Is.EqualTo(HistoryTitleKind.Auto));
            Assert.That(HistoryTabController.TitleKindOf(Checkpoint(1, kind: WorkflowCheckpoint.KindGuard, label: "before restore to c0")),
                Is.EqualTo(HistoryTitleKind.Guard));
            Assert.That(HistoryTabController.TitleKindOf(Checkpoint(1, label: "before lighting")), Is.EqualTo(HistoryTitleKind.Label));
            Assert.That(HistoryTabController.TitleKindOf(Checkpoint(1)), Is.EqualTo(HistoryTitleKind.Unnamed));
        }

        [Test]
        public void SummaryNamesTheObjectsOfEachVerbFromSnapshots()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(0) },
                steps: new[]
                {
                    Step(1, snapshots: new[] { Snapshot(SnapshotType.Created, objectName: "Enemy_01") }),
                    Step(2, snapshots: new[] { Snapshot(SnapshotType.Created, objectName: "Enemy_02") }),
                    Step(3, snapshots: new[] { Snapshot(SnapshotType.Modified, assetPath: "Assets/Materials/Ground.mat") }),
                });

            var group = Groups(history).Single();

            // Newest step first: the Modify of step 3 leads, and its verb keeps its place.
            Assert.That(group.SummaryParts, Is.EqualTo(new[]
            {
                Part("history_verb_modify", "Ground.mat"),
                Part("history_verb_create", "Enemy_02, Enemy_01"),
            }));
        }

        [Test]
        public void SummaryFallsBackToTheSkillOperationWhenNoSnapshotWasRecorded()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(0) },
                steps: new[]
                {
                    Step(1, tag: "gameobject_create", target: "Player"),
                    Step(2, tag: "manual step without a skill", target: "Notes"),
                });

            var group = Groups(history).Single();

            Assert.That(group.SummaryParts, Is.EqualTo(new[]
            {
                Part("history_verb_generic", "Notes"),
                Part("history_verb_create", "Player"),
            }));
        }

        [Test]
        public void SummaryNamesAtMostThreeObjectsPerVerb()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(0) },
                steps: Enumerable.Range(1, 5)
                    .Select(seq => Step(seq, snapshots: new[] { Snapshot(SnapshotType.Created, objectName: "E" + seq) }))
                    .ToArray());

            var group = Groups(history).Single();

            string more = string.Format(SkillsLocalization.Get("history_names_more_fmt"), 2);
            Assert.That(group.SummaryParts, Is.EqualTo(new[] { Part("history_verb_create", "E5, E4, E3 " + more) }));
        }

        [Test]
        public void RestoredHereMarksTheCheckpointOfAnUndoableRestore()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(1, label: "before lighting"), Checkpoint(3, kind: WorkflowCheckpoint.KindAuto) },
                steps: new[] { Step(1), Step(2), Step(3), Step(4) });
            var undoable = new List<WorkflowRestoreRecord>
            {
                new WorkflowRestoreRecord { seq = 6, to = "s2" },
                new WorkflowRestoreRecord { seq = 5, to = "c1" },
            };

            var groups = Groups(history, undoable: undoable);

            Assert.That(groups.Single(group => group.Id == "c1").RestoredHere, Is.True);
            Assert.That(groups.Single(group => group.Id == "c1").UndoRestoreTaskId, Is.EqualTo("s5"));
            Assert.That(groups.Single(group => group.Id == "c3").RestoredHere, Is.False, "A step target does not mark a group.");
        }

        [Test]
        public void RestoredHereResolvesALabelTarget()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(1, label: "before lighting") },
                steps: new[] { Step(1), Step(2) });
            var undoable = new List<WorkflowRestoreRecord> { new WorkflowRestoreRecord { seq = 3, to = "before lighting" } };

            var group = Groups(history, undoable: undoable).Single(item => item.Id == "c1");

            Assert.That(group.RestoredHere, Is.True);
            Assert.That(group.UndoRestoreTaskId, Is.EqualTo("s3"));
        }

        [Test]
        public void AgentFilterKeepsOnlyThatAgentsSteps()
        {
            var history = History(
                checkpoints: new[] { Checkpoint(1, agent: Caller), Checkpoint(2, agent: Other) },
                steps: new[] { Step(1, agent: Caller), Step(2, agent: Other), Step(3, agent: Caller) });

            var groups = Groups(history, agentFilter: Caller);

            Assert.That(groups.Select(group => group.Key).ToList(), Is.EqualTo(new[] { "c2", "c1" }));
            Assert.That(StepIds(groups[0]), Is.EqualTo(new[] { "s3" }));
            Assert.That(StepIds(groups[1]), Is.EqualTo(new[] { "s1" }));
            Assert.That(groups.SelectMany(group => group.Steps).All(step => step.Agent == Caller), Is.True);
        }

        [Test]
        public void UndoneStepsAreFlagged()
        {
            var history = History(
                steps: new[] { Step(1), Step(2), Step(3) },
                restores: new[] { new WorkflowRestoreRecord { seq = 4, to = "c1", steps = new List<long> { 2 } } });

            var steps = Groups(history).Single().Steps;

            Assert.That(steps.Single(step => step.Id == "s2").IsReverted, Is.True);
            Assert.That(steps.Single(step => step.Id == "s1").IsReverted, Is.False);
            Assert.That(steps.Single(step => step.Id == "s3").IsReverted, Is.False);
        }

        [Test]
        public void OtherAgentsStepsAreMarked()
        {
            var history = History(steps: new[] { Step(1), Step(2, agent: Other), Step(3) });

            var steps = Groups(history).Single().Steps;

            Assert.That(steps.Single(step => step.Id == "s2").IsOtherAgent, Is.True);
            Assert.That(steps.Single(step => step.Id == "s3").IsOtherAgent, Is.False);
        }

        private static List<HistoryGroup> Groups(WorkflowHistoryData history, string agentFilter = null,
            List<WorkflowRestoreRecord> undoable = null) =>
            HistoryTabController.BuildTimelineGroups(history, agentFilter, Caller, undoable ?? new List<WorkflowRestoreRecord>(),
                to => Find(history, to));

        private static WorkflowCheckpoint Find(WorkflowHistoryData history, string to)
        {
            if (string.IsNullOrEmpty(to))
                return null;
            return history.checkpoints.FirstOrDefault(checkpoint => checkpoint.id == to || checkpoint.label == to);
        }

        private static List<string> StepIds(HistoryGroup group) => group.Steps.Select(step => step.Id).ToList();

        private static string Part(string verbKey, string names) =>
            string.Format(SkillsLocalization.Get("history_summary_part_fmt"), SkillsLocalization.Get(verbKey), names);

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

        private static WorkflowTask Step(long seq, string agent = Caller, string tag = null, string target = null,
            ObjectSnapshot[] snapshots = null) => new WorkflowTask
        {
            id = "task-" + seq,
            seq = seq,
            tag = tag ?? "tag" + seq,
            agent = agent,
            target = target,
            timestamp = 1000 + seq,
            snapshots = snapshots?.ToList() ?? new List<ObjectSnapshot>(),
        };

        private static ObjectSnapshot Snapshot(SnapshotType type, string objectName = null, string assetPath = null) => new ObjectSnapshot
        {
            type = type,
            objectName = objectName,
            assetPath = assetPath,
        };
    }
}

// Producer:Betsy
