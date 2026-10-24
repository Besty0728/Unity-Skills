using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The 3.0 workflow store (P3): an append-only log in segments plus content-addressed blobs. The hot path (one append,
    /// no enumeration, no replay after a domain reload), payload dedupe, deferred sealing with reference lists, eviction
    /// by capacity and age, GC, the legacy backup's lifetime, and a replay that rebuilds exactly what was in memory.
    /// </summary>
    [TestFixture]
    public class WorkflowStoreTests
    {
        private string _tempRoot;
        private string _root;
        private bool _autoCleanEnabled;
        private int _capacityMb;
        private int _maxAgeDays;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            _capacityMb = WorkflowAutoCleanConfig.CapacityMB;
            _maxAgeDays = WorkflowAutoCleanConfig.MaxAgeDays;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowStore_" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(_tempRoot, "workflow");
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(_root);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
            WorkflowAutoCleanConfig.CapacityMB = _capacityMb;
            WorkflowAutoCleanConfig.MaxAgeDays = _maxAgeDays;
        }

        [Test]
        public void FirstWriteAfterADomainReload_AppendsOnce_WithoutEnumeratingOrReplaying()
        {
            var go = new GameObject("StoreHotPathProbe");
            Move("before-reload", go);
            WorkflowManager.ResetStateForTests();

            int appends = WorkflowStore.AppendsForTests;
            int enumerations = WorkflowStore.DirectoryEnumerationsForTests;
            int replays = WorkflowStore.FullReplaysForTests;
            Move("after-reload", go);

            Assert.That(WorkflowStore.AppendsForTests - appends, Is.EqualTo(1));
            Assert.That(WorkflowStore.DirectoryEnumerationsForTests - enumerations, Is.Zero);
            Assert.That(WorkflowStore.FullReplaysForTests - replays, Is.Zero, "The seq comes from the head and the log's tail.");

            Assert.That(WorkflowManager.History.tasks.Select(t => t.seq), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(WorkflowStore.FullReplaysForTests - replays, Is.EqualTo(1));
        }

        [Test]
        public void SnapshottingAnUnchangedObjectAgain_StoresItsPayloadOnce()
        {
            var go = new GameObject("StoreDedupeProbe");
            Record("unchanged-1", go);
            int blobs = BlobCount();
            Record("unchanged-2", go);

            Assert.That(BlobCount(), Is.EqualTo(blobs));
            WorkflowManager.ResetStateForTests();
            var tasks = WorkflowManager.History.tasks;
            Assert.That(tasks[1].snapshots[0].payloadHash, Is.EqualTo(tasks[0].snapshots[0].payloadHash));
        }

        [Test]
        public void FullActiveSegment_IsSealedByTheDeferredPass_WithItsReferences()
        {
            WorkflowStore.SegmentMaxLinesForTests = 2;
            var go = new GameObject("StoreSealProbe");
            var first = Move("seal-1", go);
            var second = Move("seal-2", go);
            Assert.That(File.Exists(SegmentPath(1)), Is.False, "Sealing is deferred, never inside EndTask.");

            WorkflowManager.RunMaintenance();

            Assert.That(File.Exists(SegmentPath(1)), Is.True);
            Assert.That(File.Exists(ActiveLogPath), Is.False);
            Assert.That(ReadRefs(1), Is.EquivalentTo(new[] { first.snapshots[0].payloadHash, second.snapshots[0].payloadHash }));
            var head = JObject.Parse(File.ReadAllText(HeadPath));
            Assert.That(head["lastSegment"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(head["nextSeq"]?.Value<long>(), Is.EqualTo(3));

            Assert.That(Move("seal-3", go).seq, Is.EqualTo(3));
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "seal-1", "seal-2", "seal-3" }));
        }

        [Test]
        public void SealedSegmentMissingItsReferences_GetsThemRebuiltOnLoad()
        {
            WorkflowStore.SegmentMaxLinesForTests = 1;
            var go = new GameObject("StoreRefsProbe");
            Move("refs", go);
            WorkflowManager.RunMaintenance();
            var refs = ReadRefs(1);
            Assert.That(refs, Is.Not.Empty, "Precondition: a sealed segment with references.");
            File.Delete(RefsPath(1));

            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks, Has.Count.EqualTo(1));

            Assert.That(ReadRefs(1), Is.EquivalentTo(refs));
            Assert.That(WorkflowManager.IsHistoryRecoveryMode, Is.False);
        }

        [Test]
        public void Cleanup_EvictsTheOldestSegments_UntilTheStoreFitsItsCapacity()
        {
            WorkflowStore.SegmentMaxLinesForTests = 1;
            var go = new GameObject("StoreCapacityProbe");
            var oldest = SealedMoves(go, "cap-1", "cap-2", "cap-3");
            Move("cap-active", go);
            AgeAllBlobs();
            string oldestPayload = oldest[0].snapshots[0].payloadHash;
            string keptPayload = oldest[1].snapshots[0].payloadHash;

            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks, Has.Count.EqualTo(4));
            long total = WorkflowManager.GetHistoryFileSizeBytes() + WorkflowFileStore.GetStoreSizeBytes();
            WorkflowManager.CapacityBytesForTests = total - 1;
            var report = WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(report.removedSegments, Is.EqualTo(1), "Deleting the oldest segment and its blobs is enough.");
            Assert.That(report.removedTasks, Is.EqualTo(1));
            Assert.That(File.Exists(SegmentPath(1)), Is.False);
            Assert.That(File.Exists(RefsPath(1)), Is.False);
            Assert.That(File.Exists(SegmentPath(2)), Is.True);
            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "cap-2", "cap-3", "cap-active" }));
            Assert.That(WorkflowFileStore.BlobExists(oldestPayload), Is.False);
            Assert.That(WorkflowFileStore.BlobExists(keptPayload), Is.True);
            Assert.That(WorkflowManager.GetHistoryFileSizeBytes() + WorkflowFileStore.GetStoreSizeBytes(), Is.LessThan(total));
        }

        [Test]
        public void DeferredPass_EvictsSegmentsPastTheMaxAge_ButNeverTheActiveOne()
        {
            WorkflowStore.SegmentMaxLinesForTests = 1;
            var go = new GameObject("StoreAgeProbe");
            SealedMoves(go, "age-old", "age-recent");
            WorkflowStore.SegmentMaxLinesForTests = 0;
            Move("age-active", go);
            File.SetLastWriteTimeUtc(SegmentPath(1), DateTime.UtcNow.AddDays(-40));
            File.SetLastWriteTimeUtc(ActiveLogPath, DateTime.UtcNow.AddDays(-40));
            WorkflowAutoCleanConfig.Enabled = true;
            WorkflowAutoCleanConfig.CapacityMB = 0;
            WorkflowAutoCleanConfig.MaxAgeDays = 30;

            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks, Has.Count.EqualTo(3), "Loading measures the store in the deferred pass.");
            WorkflowManager.RunMaintenance();

            Assert.That(File.Exists(SegmentPath(1)), Is.False);
            Assert.That(File.Exists(SegmentPath(2)), Is.True);
            Assert.That(File.Exists(ActiveLogPath), Is.True);
            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "age-recent", "age-active" }));
        }

        [Test]
        public void GarbageCollection_KeepsReferencedAndRecentlyWrittenBlobs()
        {
            var go = new GameObject("StoreGcProbe");
            string referenced = Move("gc", go).snapshots[0].payloadHash;
            string oldOrphan = WorkflowFileStore.StoreBytes(Encoding.UTF8.GetBytes("old orphan"));
            string recentOrphan = WorkflowFileStore.StoreBytes(Encoding.UTF8.GetBytes("recent orphan"));
            AgeBlob(referenced);
            AgeBlob(oldOrphan);
            WorkflowAutoCleanConfig.CapacityMB = 0;
            WorkflowAutoCleanConfig.MaxAgeDays = 0;

            var report = WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(WorkflowFileStore.BlobExists(referenced), Is.True);
            Assert.That(WorkflowFileStore.BlobExists(recentOrphan), Is.True, "Its snapshot may still be on its way to the log.");
            Assert.That(WorkflowFileStore.BlobExists(oldOrphan), Is.False);
            Assert.That(report.reclaimedFileEntries, Is.EqualTo(1));
        }

        [Test]
        public void DeleteTask_KeepsItsBlobsUntilTheSegmentNamingThemIsEvicted()
        {
            var go = new GameObject("StoreDeleteProbe");
            var task = Move("deleted", go);
            string payload = task.snapshots[0].payloadHash;

            WorkflowManager.DeleteTask(task.DisplayId);

            Assert.That(WorkflowFileStore.BlobExists(payload), Is.True);
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks, Is.Empty);
        }

        [Test]
        public void ClearHistory_DeletesTheLogAndBlobs_AndSeqsKeepCounting()
        {
            var go = new GameObject("StoreClearProbe");
            Move("clear-1", go);
            Move("clear-2", go);

            WorkflowManager.ClearHistory();

            Assert.That(Directory.GetFiles(_root, "log.*"), Is.Empty);
            Assert.That(BlobCount(), Is.Zero);
            Assert.That(Move("after-clear", go).seq, Is.EqualTo(3));
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks.Select(t => t.seq), Is.EqualTo(new long[] { 3 }));
        }

        [Test]
        public void LegacyBackup_IsDeletedFourteenDaysAfterTheMove()
        {
            File.WriteAllText(Path.Combine(_tempRoot, "workflow_history.json"), "{}");
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.GetLegacyBackupInfo(), Is.Not.Null, "Precondition: the old history was moved aside.");
            string legacy = Path.Combine(_tempRoot, "workflow_legacy");

            SetLegacyMovedAt(DateTimeOffset.UtcNow.AddDays(-13));
            WorkflowManager.RunMaintenance();
            Assert.That(Directory.Exists(legacy), Is.True);

            SetLegacyMovedAt(DateTimeOffset.UtcNow.AddDays(-15));
            WorkflowManager.RunMaintenance();
            Assert.That(Directory.Exists(legacy), Is.False);
            Assert.That(WorkflowManager.GetLegacyBackupInfo(), Is.Null);
            Assert.That(JObject.Parse(File.ReadAllText(HeadPath))["legacyMovedAtMs"]?.Value<long>(), Is.Zero);
        }

        [Test]
        public void DeleteLegacyBackup_RemovesItOnRequest()
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, "workflow_files"));
            File.WriteAllText(Path.Combine(_tempRoot, "workflow_files", "ABCDEF"), "old blob");
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.GetLegacyBackupInfo()?.TotalBytes, Is.EqualTo("old blob".Length));

            Assert.That(WorkflowManager.DeleteLegacyBackup(), Is.True);
            Assert.That(WorkflowManager.GetLegacyBackupInfo(), Is.Null);
            Assert.That(Directory.Exists(Path.Combine(_tempRoot, "workflow_legacy")), Is.False);
        }

        [Test]
        public void Replay_RebuildsTheStacksExactlyAsTheyStoodInMemory()
        {
            var answers = new Dictionary<int, string>();
            WorkflowManager.AgentLookupForTests = (int port, out string agentId) => answers.TryGetValue(port, out agentId);
            var a = new GameObject("StoreReplayA");
            var b = new GameObject("StoreReplayB");
            var c = new GameObject("StoreReplayC");
            var d = new GameObject("StoreReplayD");
            var e = new GameObject("StoreReplayE");
            var f = new GameObject("StoreReplayF");

            // s1's agent is bound only after it ended; s4 holds an unresolvable change before a resolvable one.
            using (SkillsAuditLog.BeginRequestContext(50300, "curl", false))
                Move("late-agent", a);
            Move("to-delete", b);
            Move("two-moves", c, e);
            WorkflowManager.BeginTask("split", "split");
            WorkflowManager.CurrentTask.snapshots.Add(new ObjectSnapshot
            {
                globalObjectId = "invalid:replay",
                objectName = "unresolvable",
                type = SnapshotType.Modified
            });
            WorkflowManager.SnapshotObject(d.transform);
            d.transform.position = Vector3.one;
            WorkflowManager.EndTask();
            answers[50300] = "Codex";
            WorkflowManager.BindPendingAgents();

            WorkflowManager.DeleteTask("s2");
            Assert.That(WorkflowManager.UndoTask("s4").success, Is.False, "Precondition: a partial undo splits s4 across both stacks.");
            Assert.That(WorkflowManager.UndoTask("s3").success, Is.True);
            Assert.That(WorkflowManager.RedoTask("s3").success, Is.True);
            var mark = WorkflowManager.MarkBatchStart();
            Move("rolled-back", f);
            WorkflowManager.RollBackBatch(mark, restoreRedoStack: true);

            var history = WorkflowManager.History;
            Assert.That(history.tasks.Select(t => t.seq), Is.EqualTo(new long[] { 1, 4, 3 }));
            Assert.That(history.undoneStack.Select(t => t.seq), Is.EqualTo(new long[] { 4 }));
            Assert.That(history.tasks[0].agent, Is.EqualTo("Codex"));
            Assert.That(history.tasks[2].snapshots, Has.Count.EqualTo(2));
            string before = Describe(history);

            WorkflowManager.ResetStateForTests();

            Assert.That(Describe(WorkflowManager.History), Is.EqualTo(before));
            Assert.That(WorkflowStore.LastReplaySkippedLines, Is.Zero);
        }

        private static WorkflowTask Move(string tag, params GameObject[] targets)
        {
            var task = WorkflowManager.BeginTask(tag, tag);
            foreach (var go in targets)
            {
                WorkflowManager.SnapshotObject(go.transform);
                go.transform.position += Vector3.right;
            }
            WorkflowManager.EndTask();
            return task;
        }

        private static void Record(string tag, GameObject go)
        {
            WorkflowManager.BeginTask(tag, tag);
            WorkflowManager.SnapshotObject(go.transform);
            WorkflowManager.EndTask();
        }

        /// <summary>One sealed segment per move (the line cap must be 1).</summary>
        private static List<WorkflowTask> SealedMoves(GameObject go, params string[] tags)
        {
            var tasks = new List<WorkflowTask>();
            foreach (string tag in tags)
            {
                tasks.Add(Move(tag, go));
                WorkflowManager.RunMaintenance();
            }
            return tasks;
        }

        private string SegmentPath(int n) => Path.Combine(_root, $"log.{n:D6}.jsonl");
        private string RefsPath(int n) => Path.Combine(_root, $"log.{n:D6}.refs");
        private string ActiveLogPath => Path.Combine(_root, "log.jsonl");
        private string HeadPath => Path.Combine(_root, "head.json");
        private string BlobsPath => Path.Combine(_root, "blobs");

        private List<string> ReadRefs(int n) => File.ReadAllLines(RefsPath(n)).Where(line => line.Length > 0).ToList();

        private int BlobCount() => Directory.Exists(BlobsPath) ? Directory.GetFiles(BlobsPath).Length : 0;

        // Past the 10-minute grace that keeps a blob whose record may still be on its way.
        private void AgeAllBlobs()
        {
            foreach (string blob in Directory.GetFiles(BlobsPath))
                File.SetLastWriteTimeUtc(blob, DateTime.UtcNow.AddDays(-1));
        }

        private void AgeBlob(string hash) =>
            File.SetLastWriteTimeUtc(Path.Combine(BlobsPath, hash), DateTime.UtcNow.AddDays(-1));

        private void SetLegacyMovedAt(DateTimeOffset when)
        {
            var head = JObject.Parse(File.ReadAllText(HeadPath));
            head["legacyMovedAtMs"] = when.ToUnixTimeMilliseconds();
            File.WriteAllText(HeadPath, head.ToString(Formatting.None));
            WorkflowManager.ResetStateForTests();
        }

        private static string Describe(WorkflowHistoryData history)
        {
            var text = new StringBuilder("next=").Append(history.nextSeq).Append('\n');
            foreach (var (stack, tasks) in new[] { ("t", history.tasks), ("u", history.undoneStack) })
            {
                foreach (var task in tasks)
                {
                    text.Append(stack).Append(' ').Append(task.id).Append(" s").Append(task.seq).Append(' ').Append(task.tag)
                        .Append(" @").Append(task.agent).Append(" g=").Append(task.group).Append(" tg=").Append(task.target)
                        .Append(" ss=").Append(task.sessionId).Append(" is=").Append(task.idSession)
                        .Append(" ts=").Append(task.timestamp).Append(" d=").Append(task.description).Append('\n');
                    foreach (var s in task.snapshots)
                    {
                        text.Append("  ").Append(s.type).Append('|').Append(s.globalObjectId).Append('|').Append(s.entityId)
                            .Append('|').Append(s.objectInstanceId).Append('|').Append(s.objectName).Append('|').Append(s.typeName)
                            .Append('|').Append(s.assetPath).Append('|').Append(s.idSession).Append('|').Append(s.fileHash)
                            .Append('|').Append(s.metaFileHash).Append('|').Append(s.payloadHash).Append('\n');
                    }
                }
            }
            return text.ToString();
        }
    }
}

// Producer:Betsy
