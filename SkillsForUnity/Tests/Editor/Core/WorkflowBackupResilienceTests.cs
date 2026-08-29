using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Integration coverage for workflow history backup/restore hardening:
    /// - A blob referenced only by a "task currently being recorded (not yet EndTask'd)" must survive garbage collection.
    /// - A corrupted history file must be quarantined (not silently reset), and GC must stay suspended for the
    ///   rest of this session, so an incomplete reference set doesn't reclaim backups still in use.
    /// - SaveHistory must keep the previous main file as a .bak instead of deleting it.
    /// - RestoreFile must refuse to hand back a tampered store blob, and must quarantine it.
    ///
    /// Reuses WorkflowPersistenceTests.cs's fixture pattern (path override + ResetStateForTests),
    /// without duplicating any of its test cases.
    /// </summary>
    [TestFixture]
    public class WorkflowBackupResilienceTests
    {
        private const string AssetRoot = "Assets/Temp/WorkflowBackupResilienceTests";
        private string _tempRoot;
        private bool _autoCleanEnabled;
        private int _maxTasks;
        private int _maxHistoryMb;
        private int _maxTaskAgeDays;
        private int _maxStoreMb;
        private int _storeMaxAgeDays;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            _maxTasks = WorkflowAutoCleanConfig.MaxTasks;
            _maxHistoryMb = WorkflowAutoCleanConfig.MaxHistoryMB;
            _maxTaskAgeDays = WorkflowAutoCleanConfig.MaxTaskAgeDays;
            _maxStoreMb = WorkflowAutoCleanConfig.MaxStoreMB;
            _storeMaxAgeDays = WorkflowAutoCleanConfig.StoreMaxAgeDays;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowBackupTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(_tempRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(_tempRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();

            EnsureAssetFolder();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/WorkflowBackupResilienceTestScene.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.ResetStateForTests();
            WorkflowManager.OverrideHistoryFilePathForTests = null;
            WorkflowFileStore.OverrideStoreRootForTests = null;
            // Keep a valid target scene present for the whole teardown, same reasoning as WorkflowPersistenceTests.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
            WorkflowAutoCleanConfig.MaxTasks = _maxTasks;
            WorkflowAutoCleanConfig.MaxHistoryMB = _maxHistoryMb;
            WorkflowAutoCleanConfig.MaxTaskAgeDays = _maxTaskAgeDays;
            WorkflowAutoCleanConfig.MaxStoreMB = _maxStoreMb;
            WorkflowAutoCleanConfig.StoreMaxAgeDays = _storeMaxAgeDays;
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
            WorkflowAutoCleanConfig.MaxTasks = 0;
            WorkflowAutoCleanConfig.MaxHistoryMB = 0;
            WorkflowAutoCleanConfig.MaxTaskAgeDays = 0;
            WorkflowAutoCleanConfig.MaxStoreMB = 0;
            WorkflowAutoCleanConfig.StoreMaxAgeDays = 0;
            WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(WorkflowFileStore.BlobExists(hash), Is.True,
                "A blob referenced only by the still-recording current task must not be reclaimed.");
        }

        [Test]
        public void LoadHistory_CorruptMainFile_QuarantinesFileAndSuppressesGC()
        {
            // An orphan blob predating the corruption. It genuinely has no references, but once history
            // fails to load we can no longer prove that — restore mode must leave it alone rather than
            // delete a backup that can't be confirmed disposable.
            string orphanHash = WorkflowFileStore.StoreBytes(System.Text.Encoding.UTF8.GetBytes("orphan-blob"));
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, orphanHash), DateTime.UtcNow.AddDays(-1));

            File.WriteAllText(WorkflowManager.OverrideHistoryFilePathForTests, "{ this is not valid workflow history json !!");
            WorkflowManager.ResetStateForTests();

            var originalLevel = SkillsLogger.Level;
            SkillsLogger.Level = LogLevel.Off; // The quarantine path intentionally logs an error; suppress it here.
            try
            {
                Assert.That(WorkflowManager.History, Is.Not.Null, "A fresh empty history must still be usable after quarantine.");
            }
            finally
            {
                SkillsLogger.Level = originalLevel;
            }

            Assert.That(WorkflowManager.IsHistoryRecoveryMode, Is.True);
            Assert.That(File.Exists(WorkflowManager.OverrideHistoryFilePathForTests), Is.False,
                "The unreadable file must be moved aside, not left in place for the next save to clobber.");
            var quarantined = Directory.GetFiles(_tempRoot, "workflow_history.corrupt.*.json");
            Assert.That(quarantined, Has.Length.EqualTo(1));

            // Even with force=true explicitly set, restore mode must still suppress GC. That path also
            // logs a warning (restore mode + force), so the log suppression needs to stay in place too.
            WorkflowAutoCleanConfig.Enabled = true;
            SkillsLogger.Level = LogLevel.Off;
            try
            {
                WorkflowManager.TrimHistoryIfNeeded(force: true);
            }
            finally
            {
                SkillsLogger.Level = originalLevel;
            }
            Assert.That(WorkflowFileStore.BlobExists(orphanHash), Is.True,
                "GC must stay suspended in recovery mode, even though the orphan blob looks unreferenced.");
        }

        [Test]
        public void SaveHistory_PreviousMainFileIsKeptAsBak()
        {
            WorkflowManager.BeginTask("first", "test");
            WorkflowManager.CurrentTask.snapshots.Add(new UnitySkills.Internal.ObjectSnapshot
            {
                globalObjectId = "g-first",
                objectName = "first-snapshot",
                type = SnapshotType.Modified
            });
            WorkflowManager.EndTask();

            string backupPath = WorkflowManager.OverrideHistoryFilePathForTests + ".bak";
            Assert.That(File.Exists(backupPath), Is.False, "No prior main file existed yet, so there is nothing to back up.");

            WorkflowManager.BeginTask("second", "test");
            WorkflowManager.CurrentTask.snapshots.Add(new UnitySkills.Internal.ObjectSnapshot
            {
                globalObjectId = "g-second",
                objectName = "second-snapshot",
                type = SnapshotType.Modified
            });
            WorkflowManager.EndTask();

            Assert.That(File.Exists(backupPath), Is.True,
                "The second SaveHistory must retain the first file's content as .bak instead of deleting it.");
            StringAssert.Contains("g-first", File.ReadAllText(backupPath));
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

        private static void EnsureAssetFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets/Temp", "WorkflowBackupResilienceTests");
        }
    }
}

// Producer:Betsy
