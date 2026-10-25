using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The P1a checkpoint records and policy: idle-gap boundaries with busy time excluded, the auto checkpoint of a router run,
    /// one checkpoint per position, the cp/label/restore records surviving a reload, and the busy spans' SessionState round trip.
    /// </summary>
    [TestFixture]
    public class WorkflowCheckpointPolicyTests
    {
        private const string Agent = "checkpoint-probe";
        private const long T0 = 1_700_000_000_000;

        private string _tempRoot;
        private bool _autoCheckpoints;
        private int _idleGapSeconds;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCheckpoints = WorkflowCheckpointConfig.AutoCheckpoints;
            _idleGapSeconds = WorkflowCheckpointConfig.IdleGapSeconds;
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;
            WorkflowCheckpointConfig.AutoCheckpoints = true;
            WorkflowCheckpointConfig.IdleGapSeconds = WorkflowCheckpointConfig.DefaultIdleGapSeconds;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsCheckpoints_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));
            WorkflowLifecycle.ReplaceSpansForTests(Array.Empty<(long, long)>());
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            WorkflowLifecycle.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowCheckpointConfig.AutoCheckpoints = _autoCheckpoints;
            WorkflowCheckpointConfig.IdleGapSeconds = _idleGapSeconds;
            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
        }

        [Test]
        public void IsIdleGap_ChangesAtThirtySeconds()
        {
            var none = Array.Empty<(long start, long end)>();

            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 29_000, none, 30), Is.False);
            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 30_000, none, 30), Is.True);
            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 31_000, none, 30), Is.True);
        }

        [Test]
        public void IsIdleGap_BusyTimeInsideTheGapDoesNotCount()
        {
            var compile = new[] { (T0 + 10_000, T0 + 20_000) };

            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 40_000, compile, 30), Is.True, "30 s idle outside the 10 s compile");
            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 35_000, compile, 30), Is.False, "25 s idle outside the compile");

            var overlapping = new[] { (T0 + 10_000, T0 + 20_000), (T0 + 15_000, T0 + 25_000) };
            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 45_000, overlapping, 30), Is.True, "overlaps count once: 15 s busy");
            Assert.That(WorkflowCheckpointPolicy.IsIdleGap(T0, T0 + 44_000, overlapping, 30), Is.False, "29 s idle after the 15 s union");
        }

        [Test]
        public void IdleGapSeconds_IsClampedToAtLeastFive()
        {
            WorkflowCheckpointConfig.IdleGapSeconds = 2;
            Assert.That(WorkflowCheckpointConfig.IdleGapSeconds, Is.EqualTo(5));

            WorkflowCheckpointConfig.IdleGapSeconds = 12;
            Assert.That(WorkflowCheckpointConfig.IdleGapSeconds, Is.EqualTo(12));
        }

        [Test]
        public void AutoCheckpointsOff_RouterRunGetsNoCheckpoint_ExplicitStillWorks()
        {
            WorkflowCheckpointConfig.AutoCheckpoints = false;
            var go = new GameObject("CheckpointProbe");

            RecordAt(T0, go, auto: true);
            var second = RecordAt(T0 + 31_000, go, auto: true);

            Assert.That(WorkflowManager.GetCheckpoints(), Is.Empty);
            string id = WorkflowManager.AddCheckpoint(Agent, "explicit", "Before");
            Assert.That(WorkflowManager.GetCheckpoints().Select(c => c.id), Is.EqualTo(new[] { id }));
            Assert.That(second.seq, Is.GreaterThan(0));
        }

        [Test]
        public void EmptyTask_NeverGetsACheckpoint()
        {
            var go = new GameObject("CheckpointProbe");
            RecordAt(T0, go, auto: true);
            int before = WorkflowManager.GetCheckpoints().Count;

            WorkflowManager.NowUnixMsForTests = () => T0 + 31_000;
            WorkflowManager.BeginAutoTask("probe_read", "probe_read", null);
            WorkflowManager.EndTask();

            Assert.That(WorkflowManager.GetCheckpoints().Count, Is.EqualTo(before));
        }

        [Test]
        public void RouterRunAfterAThirtyOneSecondGap_GetsAnAutoCheckpointAtItsSeq()
        {
            var go = new GameObject("CheckpointProbe");
            var first = RecordAt(T0, go, auto: true);
            var second = RecordAt(T0 + 31_000, go, auto: true);

            Assert.That(WorkflowManager.GetCheckpoints().Select(c => c.id),
                Is.EqualTo(new[] { "c" + first.seq, "c" + second.seq }));
            var checkpoint = WorkflowManager.GetCheckpoints()[1];
            Assert.That(checkpoint.seq, Is.EqualTo(second.seq));
            Assert.That(checkpoint.kind, Is.EqualTo(WorkflowCheckpoint.KindAuto));
            Assert.That(checkpoint.agent, Is.EqualTo(Agent));
        }

        [Test]
        public void FreshAgentFirstRouterTask_GetsAnAutoCheckpoint_AndAFollowUpWithinTheGapDoesNot()
        {
            var go = new GameObject("CheckpointProbe");
            var first = RecordAt(T0, go, auto: true, agent: "fresh-agent");
            Assert.That(WorkflowManager.GetCheckpoints().Select(c => c.id), Is.EqualTo(new[] { "c" + first.seq }));

            RecordAt(T0 + 5_000, go, auto: true, agent: "fresh-agent");
            Assert.That(WorkflowManager.GetCheckpoints().Count, Is.EqualTo(1));
        }

        [Test]
        public void RouterRunAfterA29SecondGap_GetsNoCheckpoint()
        {
            var go = new GameObject("CheckpointProbe");
            var first = RecordAt(T0, go, auto: true);
            RecordAt(T0 + 29_000, go, auto: true);

            Assert.That(WorkflowManager.GetCheckpoints().Select(c => c.id), Is.EqualTo(new[] { "c" + first.seq }));
        }

        [Test]
        public void AddCheckpointTwiceAtOnePosition_KeepsOneCheckpoint_AndExplicitUpgradesAuto()
        {
            string autoId = WorkflowManager.AddCheckpoint(Agent, "auto", null);
            string explicitId = WorkflowManager.AddCheckpoint(Agent, "explicit", "Before refactor");

            Assert.That(explicitId, Is.EqualTo(autoId));
            Assert.That(WorkflowManager.GetCheckpoints().Count, Is.EqualTo(1));
            Assert.That(WorkflowManager.GetCheckpoints()[0].kind, Is.EqualTo("explicit"));
            Assert.That(WorkflowManager.GetCheckpoints()[0].label, Is.EqualTo("Before refactor"));

            WorkflowManager.AddCheckpoint(Agent, "auto", null);
            Assert.That(WorkflowManager.GetCheckpoints()[0].kind, Is.EqualTo("explicit"), "An auto checkpoint never downgrades.");

            WorkflowManager.AddCheckpoint(Agent, "explicit", "Renamed");
            Assert.That(WorkflowManager.GetCheckpoints().Count, Is.EqualTo(1));
            Assert.That(WorkflowManager.GetCheckpoints()[0].label, Is.EqualTo("Renamed"));
        }

        [Test]
        public void FindCheckpoint_MatchesIdFirst_ThenNewestLabelIgnoringCase()
        {
            var go = new GameObject("CheckpointProbe");
            string first = WorkflowManager.AddCheckpoint(Agent, "explicit", "Mark");
            RecordAt(T0, go, auto: false);
            string second = WorkflowManager.AddCheckpoint(Agent, "explicit", "mark");

            Assert.That(WorkflowManager.FindCheckpoint(first).id, Is.EqualTo(first));
            Assert.That(WorkflowManager.FindCheckpoint("MARK").id, Is.EqualTo(second));
            Assert.That(WorkflowManager.FindCheckpoint("nope"), Is.Null);
        }

        [Test]
        public void RecordsRoundTrip_CheckpointLabelRestoreTaskCursorsAndAssetFingerprint()
        {
            var go = new GameObject("CheckpointProbe");
            string id = WorkflowManager.AddCheckpoint(Agent, "explicit", "Start");
            WorkflowManager.RenameCheckpoint(id, "Renamed");

            WorkflowManager.BeginTask("probe_write", "probe_write");
            WorkflowManager.SnapshotObject(go.transform);
            WorkflowManager.CurrentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = "probe-asset-gid",
                type = SnapshotType.Modified,
                assetFingerprint = "12:34"
            });
            WorkflowManager.EndTask();
            var written = WorkflowManager.History.tasks.Last();
            long restoreSeq = WorkflowManager.NextTaskSeq;
            WorkflowStore.Append(new[]
            {
                WorkflowStore.BuildRestoreLine(new WorkflowRestoreRecord
                {
                    seq = restoreSeq, to = id, guard = "c99", agent = Agent, steps = new List<long> { 3, 4 },
                    aliases = new List<string[]> { new[] { "old-gid", "new-gid" } }
                })
            });

            WorkflowManager.ResetStateForTests();
            var history = WorkflowManager.History;

            var checkpoint = history.checkpoints.Single();
            Assert.That(checkpoint.id, Is.EqualTo(id));
            Assert.That(checkpoint.kind, Is.EqualTo("explicit"));
            Assert.That(checkpoint.label, Is.EqualTo("Renamed"));

            var task = history.tasks.Single(t => t.id == written.id);
            Assert.That(task.cursorStart, Is.EqualTo(written.cursorStart));
            Assert.That(task.cursorEnd, Is.EqualTo(written.cursorEnd));
            Assert.That(task.snapshots.Last().assetFingerprint, Is.EqualTo("12:34"));

            var restore = history.restores.Single();
            Assert.That(restore.seq, Is.EqualTo(restoreSeq));
            Assert.That(restore.to, Is.EqualTo(id));
            Assert.That(restore.guard, Is.EqualTo("c99"));
            Assert.That(restore.steps, Is.EqualTo(new[] { 3L, 4L }));
            Assert.That(restore.aliases.Single(), Is.EqualTo(new[] { "old-gid", "new-gid" }));
            Assert.That(history.nextSeq, Is.GreaterThan(restoreSeq), "A restore step's seq is never reused.");
        }

        [Test]
        public void Replay_KeepsAnExplicitCheckpointOverALaterAutoOneAtTheSamePosition()
        {
            WorkflowStore.Append(new[]
            {
                WorkflowStore.BuildCheckpointLine(new WorkflowCheckpoint { id = "c5", seq = 5, kind = "explicit", label = "Kept", agent = Agent }),
                WorkflowStore.BuildCheckpointLine(new WorkflowCheckpoint { id = "c5", seq = 5, kind = "auto", agent = Agent })
            });

            WorkflowManager.ResetStateForTests();
            var checkpoint = WorkflowManager.History.checkpoints.Single();
            Assert.That(checkpoint.kind, Is.EqualTo("explicit"));
            Assert.That(checkpoint.label, Is.EqualTo("Kept"));
        }

        [Test]
        public void BusySpans_RoundTripThroughSessionState()
        {
            string savedSpans = SessionState.GetString(WorkflowLifecycle.SpansSessionKey, string.Empty);
            string savedOpen = SessionState.GetString(WorkflowLifecycle.OpenSinceSessionKey, string.Empty);
            try
            {
                WorkflowLifecycle.ReplaceSpansForTests(new[] { (1000L, 2000L), (3000L, 4000L) }, openSinceMs: 5000);
                WorkflowLifecycle.PersistForTests();
                WorkflowLifecycle.ResetForTests();

                var spans = WorkflowLifecycle.BusySpans;
                Assert.That(spans.Take(2), Is.EqualTo(new[] { (1000L, 2000L), (3000L, 4000L) }));
                Assert.That(spans.Count, Is.EqualTo(3), "The open span counts as busy until now.");
                Assert.That(spans[2].start, Is.EqualTo(5000L));
            }
            finally
            {
                SessionState.SetString(WorkflowLifecycle.SpansSessionKey, savedSpans);
                SessionState.SetString(WorkflowLifecycle.OpenSinceSessionKey, savedOpen);
                WorkflowLifecycle.ResetForTests();
            }
        }

        /// <summary>One tracked call by the test agent at <paramref name="atMs"/> on the test clock; the router-style or explicit task.</summary>
        private static WorkflowTask RecordAt(long atMs, GameObject go, bool auto, string agent = Agent)
        {
            WorkflowManager.NowUnixMsForTests = () => atMs;
            using (SkillsAuditLog.BeginRequestContext(-1, agent, true))
            {
                var task = auto ? WorkflowManager.BeginAutoTask("probe_write", "probe_write", "Probe") : WorkflowManager.BeginTask("probe_write", "probe_write");
                WorkflowManager.SnapshotObject(go.transform);
                go.transform.position += Vector3.right; // An unchanged snapshot is pruned, and the step would not be recorded.
                WorkflowManager.EndTask();
                WorkflowManager.NoteAgentCall();
                return task;
            }
        }
    }
}

// Producer:Betsy
