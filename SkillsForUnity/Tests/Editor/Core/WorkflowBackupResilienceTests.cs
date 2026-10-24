using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Integration coverage for workflow history backup/restore hardening:
    /// - A blob referenced only by a "task currently being recorded (not yet EndTask'd)" must survive garbage collection.
    /// - A log segment that cannot be read puts the session in recovery mode: GC stays suspended until the history is
    ///   cleared, so an incomplete reference set doesn't reclaim backups still in use.
    /// - A torn last line (a kill mid-append) costs that one record, never the ones appended after it.
    /// - RestoreFile, and an undo reading a snapshot payload back, refuse a tampered blob and quarantine it; a missing
    ///   payload fails the undo with its reason.
    ///
    /// Reuses WorkflowPersistenceTests.cs's fixture pattern (store root override + ResetStateForTests),
    /// without duplicating any of its test cases.
    /// </summary>
    [TestFixture]
    public class WorkflowBackupResilienceTests
    {
        private const string AssetRoot = "Assets/Temp/WorkflowBackupResilienceTests";
        private string _tempRoot;
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

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowBackupTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            EnsureAssetFolder();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/WorkflowBackupResilienceTestScene.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            // Keep a valid target scene present for the whole teardown, same reasoning as WorkflowPersistenceTests.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
            WorkflowAutoCleanConfig.CapacityMB = _capacityMb;
            WorkflowAutoCleanConfig.MaxAgeDays = _maxAgeDays;
        }

        [Test]
        public void RecordingTask_UncommittedBlob_SurvivesTrim_BecauseInFlightTaskIsReferenced()
        {
            string path = AssetRoot + "/RecordingProtected.txt";
            File.WriteAllText(path, "still being recorded");
            AssetDatabase.ImportAsset(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            WorkflowManager.BeginTask("recording-in-progress", "test");
            WorkflowManager.SnapshotObject(asset);
            // Deliberately don't call EndTask: simulates a task still being recorded (e.g. a manual
            // workflow_begin_task session) happening concurrently with trim/GC.
            string hash = WorkflowManager.CurrentTask.snapshots[0].fileHash;
            Assert.That(hash, Is.Not.Null.And.Not.Empty);

            // Push the blob's write time back, past WorkflowFileStore's 10-minute "recently written" grace
            // window, so only the "in-progress task reference" (i.e. the fix under test) can save it;
            // otherwise the grace period alone would make this assertion pass even without the fix.
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, hash), DateTime.UtcNow.AddDays(-1));

            WorkflowAutoCleanConfig.Enabled = true;
            WorkflowAutoCleanConfig.CapacityMB = 0;
            WorkflowAutoCleanConfig.MaxAgeDays = 0;
            WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(WorkflowFileStore.BlobExists(hash), Is.True,
                "A blob referenced only by the still-recording current task must not be reclaimed.");
        }

        [Test]
        public void UnreadableSegment_EntersRecoveryMode_AndSuspendsGCUntilCleared()
        {
            WorkflowStore.SegmentMaxLinesForTests = 1;
            RecordProbe("sealed");
            WorkflowManager.RunMaintenance();
            Assert.That(File.Exists(Path.Combine(_tempRoot, "workflow", "log.000001.jsonl")), Is.True, "Precondition: a sealed segment.");
            RecordProbe("active");

            // An orphan blob predating the failure. It genuinely has no references, but with a segment unread we can no
            // longer prove that: recovery mode must leave it alone rather than delete a backup it can't confirm disposable.
            string orphanHash = WorkflowFileStore.StoreBytes(System.Text.Encoding.UTF8.GetBytes("orphan-blob"));
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, orphanHash), DateTime.UtcNow.AddDays(-1));
            WorkflowStore.SegmentReadFailsForTests = path => path.EndsWith("log.000001.jsonl", StringComparison.Ordinal);
            WorkflowManager.ResetStateForTests();

            var originalLevel = SkillsLogger.Level;
            SkillsLogger.Level = LogLevel.Off; // The unread segment and the skipped forced cleanup both log on purpose.
            try
            {
                Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "active" }),
                    "The readable segments still load.");
                Assert.That(WorkflowManager.IsHistoryRecoveryMode, Is.True);

                WorkflowAutoCleanConfig.Enabled = true;
                WorkflowManager.TrimHistoryIfNeeded(force: true);
                WorkflowManager.RunMaintenance();
            }
            finally
            {
                SkillsLogger.Level = originalLevel;
            }
            Assert.That(WorkflowFileStore.BlobExists(orphanHash), Is.True,
                "GC must stay suspended in recovery mode, even though the orphan blob looks unreferenced.");

            WorkflowStore.SegmentReadFailsForTests = null;
            WorkflowManager.ClearHistory();
            Assert.That(WorkflowManager.IsHistoryRecoveryMode, Is.False, "Clearing resynchronizes the store with the history.");
            Assert.That(WorkflowFileStore.BlobExists(orphanHash), Is.False);
        }

        [Test]
        public void TornLastLine_CostsOneRecord_AndTheNextAppendStartsANewLine()
        {
            RecordProbe("first");
            // A kill cut this append short: no newline, no closing brace.
            File.AppendAllText(Path.Combine(_tempRoot, "workflow", "log.jsonl"), "{\"r\":\"task\",\"st\":\"t\",\"id\":\"torn");
            WorkflowManager.ResetStateForTests();

            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "first" }));
            Assert.That(WorkflowStore.LastReplaySkippedLines, Is.EqualTo(1));

            RecordProbe("second");
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(WorkflowStore.LastReplaySkippedLines, Is.EqualTo(1), "The torn line stays one bad row.");
        }

        [Test]
        public void TamperedPayloadBlob_FailsTheUndoWithTheReason_AndIsQuarantined()
        {
            var go = new GameObject("WorkflowTamperedPayload");
            var task = RecordMove(go);
            string blob = Path.Combine(WorkflowFileStore.StoreRoot, task.snapshots[0].payloadHash);
            File.WriteAllText(blob, "{}");

            var originalLevel = SkillsLogger.Level;
            SkillsLogger.Level = LogLevel.Off; // The integrity check intentionally logs an error on mismatch.
            TaskUndoResult result;
            try
            {
                result = WorkflowManager.UndoTask(task.id);
            }
            finally
            {
                SkillsLogger.Level = originalLevel;
            }

            Assert.That(result.success, Is.False);
            Assert.That(result.details.Single().error, Does.Contain("damaged"));
            Assert.That(File.Exists(blob + ".corrupt"), Is.True, "The tampered payload is quarantined for forensics.");
            Assert.That(go.transform.position, Is.EqualTo(Vector3.one), "Nothing was restored from bad data.");
            Assert.That(WorkflowManager.History.tasks.Single().snapshots, Has.Count.EqualTo(1), "The task stays undoable.");
        }

        [Test]
        public void MissingPayloadBlob_FailsTheUndoWithTheReason()
        {
            var go = new GameObject("WorkflowMissingPayload");
            var task = RecordMove(go);
            File.Delete(Path.Combine(WorkflowFileStore.StoreRoot, task.snapshots[0].payloadHash));

            var result = WorkflowManager.UndoTask(task.DisplayId);

            Assert.That(result.success, Is.False);
            Assert.That(result.details.Single().error, Does.Contain("missing"));
            Assert.That(go.transform.position, Is.EqualTo(Vector3.one));
        }

        [Test]
        public void RestoreFile_TamperedBlob_ReturnsFalseAndQuarantinesTheBlob()
        {
            string path = AssetRoot + "/Tamper.txt";
            File.WriteAllText(path, "original contents");
            string hash = WorkflowFileStore.StoreFile(path, false, out _);
            Assert.That(hash, Is.Not.Null.And.Not.Empty);

            string hashPath = Path.Combine(WorkflowFileStore.StoreRoot, hash);
            File.WriteAllText(hashPath, "tampered contents that no longer match the recorded hash");
            File.Delete(path);

            var originalLevel = SkillsLogger.Level;
            SkillsLogger.Level = LogLevel.Off; // VerifyBlobIntegrity intentionally logs an error on mismatch.
            bool restored;
            try
            {
                restored = WorkflowFileStore.RestoreFile(hash, path, false);
            }
            finally
            {
                SkillsLogger.Level = originalLevel;
            }

            Assert.That(restored, Is.False);
            Assert.That(File.Exists(path), Is.False, "A failed integrity check must not write bad data back into the project.");
            Assert.That(File.Exists(hashPath), Is.False, "The tampered blob must be moved out of the live store path.");
            Assert.That(File.Exists(hashPath + ".corrupt"), Is.True, "The tampered blob must be quarantined for forensics, not silently deleted.");
        }

        private static void RecordProbe(string tag)
        {
            WorkflowManager.BeginTask(tag, "test");
            WorkflowManager.CurrentTask.snapshots.Add(new UnitySkills.Internal.ObjectSnapshot
            {
                globalObjectId = "g-" + tag,
                objectName = tag + "-snapshot",
                type = SnapshotType.Modified
            });
            WorkflowManager.EndTask();
        }

        /// <summary>A recorded move of <paramref name="go"/> to (1,1,1), read back from the log as a stub.</summary>
        private static WorkflowTask RecordMove(GameObject go)
        {
            WorkflowManager.BeginTask("move", "test");
            WorkflowManager.SnapshotObject(go.transform);
            go.transform.position = Vector3.one;
            WorkflowManager.EndTask();
            WorkflowManager.ResetStateForTests();
            var task = WorkflowManager.History.tasks.Single();
            Assert.That(task.snapshots[0].payloadHash, Is.Not.Null.And.Not.Empty, "Precondition: a stored payload.");
            return task;
        }

        private static void EnsureAssetFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets/Temp", "WorkflowBackupResilienceTests");
        }
    }
}

// Producer:Betsy
