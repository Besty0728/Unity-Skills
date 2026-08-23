using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// workflow 历史备份/恢复加固的集成覆盖：
    /// - 只被"当前正在记录（尚未 EndTask）的任务"引用的 blob 必须活过垃圾回收。
    /// - 损坏的历史文件必须被隔离（而不是静默重置），且本次会话余下时间 GC 保持挂起，
    ///   免得一份不完整的引用集合把还在用的备份回收掉。
    /// - SaveHistory 必须把上一版主文件留成 .bak，而不是删掉。
    /// - RestoreFile 必须拒绝交还被篡改的 store blob，并将其隔离。
    ///
    /// 沿用 WorkflowPersistenceTests.cs 的夹具范式（路径 override + ResetStateForTests），
    /// 不重复它的任何用例。
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
            // 整个 teardown 期间保持有一个有效目标场景，理由同 WorkflowPersistenceTests。
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
            // 故意不调 EndTask：模拟一个仍在记录中的任务（例如手工 workflow_begin_task 会话）
            // 与 trim/GC 并发发生。
            string hash = WorkflowManager.CurrentTask.snapshots[0].fileHash;
            Assert.That(hash, Is.Not.Null.And.Not.Empty);

            // 把 blob 的写入时间往前拨，越过 WorkflowFileStore 那 10 分钟"近期写入"宽限窗，
            // 使得只有"进行中任务的引用"（即被测的修复）能保住它；否则光靠宽限期，
            // 这条断言在没有修复的情况下也会通过。
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
            // 一个早于损坏发生的孤儿 blob。它确实没人引用，但历史一旦加载失败，我们就无法再证明
            // 这一点——恢复模式必须放它不管，而不是删掉一份无法确认可弃的备份。
            string orphanHash = WorkflowFileStore.StoreBytes(System.Text.Encoding.UTF8.GetBytes("orphan-blob"));
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, orphanHash), DateTime.UtcNow.AddDays(-1));

            File.WriteAllText(WorkflowManager.OverrideHistoryFilePathForTests, "{ this is not valid workflow history json !!");
            WorkflowManager.ResetStateForTests();

            var originalLevel = SkillsLogger.Level;
            SkillsLogger.Level = LogLevel.Off; // 隔离路径会有意打一条 error，这里压掉。
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

            // 即便显式 force=true，恢复模式也必须压住 GC。该路径同样会打一条 warning
            // （恢复模式 + force），所以日志压制也要一并留着。
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
