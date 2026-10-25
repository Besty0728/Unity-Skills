using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class WorkflowPersistenceTests
    {
        private const string AssetRoot = "Assets/Temp/WorkflowPersistenceTests";
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

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            EnsureAssetFolder();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/WorkflowPersistenceTestScene.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            // While Unity Test Framework is winding down Undo state, the asset folder holding the currently
            // active scene can't be deleted; there must always be a valid target scene throughout teardown.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
            WorkflowAutoCleanConfig.CapacityMB = _capacityMb;
            WorkflowAutoCleanConfig.MaxAgeDays = _maxAgeDays;
        }

        [Test]
        public void FileStore_UsesIndependentMetaHashes()
        {
            string first = AssetRoot + "/First.txt";
            string second = AssetRoot + "/Second.txt";
            File.WriteAllText(first, "same");
            File.WriteAllText(second, "same");
            File.WriteAllText(first + ".meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n");
            File.WriteAllText(second + ".meta", "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\n");

            string firstHash = WorkflowFileStore.StoreFile(first, false, out string firstMetaHash);
            string secondHash = WorkflowFileStore.StoreFile(second, false, out string secondMetaHash);

            Assert.That(firstHash, Is.EqualTo(secondHash));
            Assert.That(firstMetaHash, Is.Not.EqualTo(secondMetaHash));
            File.Delete(first);
            File.Delete(first + ".meta");
            Assert.That(WorkflowFileStore.RestoreFile(firstHash, firstMetaHash, first, false), Is.True);
            StringAssert.Contains("11111111111111111111111111111111", File.ReadAllText(first + ".meta"));
        }

        [Test]
        public void NonEmptyFolder_DeleteUndoRedo_RestoresContentsAndMeta()
        {
            string folder = AssetRoot + "/Tree";
            string childFolder = folder + "/Child";
            Directory.CreateDirectory(childFolder);
            File.WriteAllText(childFolder + "/Data.txt", "payload");
            AssetDatabase.Refresh();

            WorkflowManager.BeginTask("folder-delete", "test");
            Assert.That(WorkflowManager.DeleteAssetToTrash(folder), Is.True);
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(Directory.Exists(folder), Is.False);
            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(File.ReadAllText(childFolder + "/Data.txt"), Is.EqualTo("payload"));
            Assert.That(File.Exists(childFolder + "/Data.txt.meta"), Is.True);
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void SceneGameObject_DeleteUndoRedo_RestoresHierarchyAndComponents()
        {
            var root = new GameObject("WorkflowDeletedRoot");
            root.AddComponent<BoxCollider>().size = new Vector3(2, 3, 4);
            var child = new GameObject("Child");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(1, 2, 3);
            var referenceFixture = root.AddComponent<WorkflowReferenceFixture>();
            referenceFixture.target = child;

            WorkflowManager.BeginTask("scene-delete", "test");
            Assert.That(WorkflowManager.DeleteSceneObject(root), Is.True);
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(GameObject.Find("WorkflowDeletedRoot"), Is.Null);
            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            var restored = GameObject.Find("WorkflowDeletedRoot");
            Assert.That(restored, Is.Not.Null);
            var restoredCollider = restored.GetComponent<BoxCollider>();
            Assert.That(restoredCollider, Is.Not.Null);
            Assert.That(restoredCollider.gameObject, Is.SameAs(restored));
            Assert.That(restoredCollider.size, Is.EqualTo(new Vector3(2, 3, 4)));
            Assert.That(restored.transform.Find("Child").localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(restored.GetComponent<WorkflowReferenceFixture>().target,
                Is.SameAs(restored.transform.Find("Child").gameObject));
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(GameObject.Find("WorkflowDeletedRoot"), Is.Null);
        }

        [Test]
        public void SceneComponent_DeleteUndoRedo_RestoresSerializedState()
        {
            var go = new GameObject("WorkflowComponentHost");
            var collider = go.AddComponent<BoxCollider>();
            collider.center = new Vector3(4, 5, 6);

            WorkflowManager.BeginTask("component-delete", "test");
            Assert.That(WorkflowManager.DeleteSceneObject(collider), Is.True);
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(go.GetComponent<BoxCollider>().center, Is.EqualTo(new Vector3(4, 5, 6)));
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(go.GetComponent<BoxCollider>(), Is.Null);
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void ModifiedComponent_UndoRedo_PreservesSceneObjectReferences()
        {
            var owner = new GameObject("WorkflowReferenceOwner");
            var beforeTarget = new GameObject("WorkflowReferenceBefore");
            var afterTarget = new GameObject("WorkflowReferenceAfter");
            var fixture = owner.AddComponent<WorkflowReferenceFixture>();
            fixture.target = beforeTarget;
            fixture.value = 10;

            WorkflowManager.BeginTask("reference-modify", "test");
            WorkflowManager.SnapshotObject(fixture);
            fixture.target = afterTarget;
            fixture.value = 20;
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(fixture.target, Is.EqualTo(beforeTarget));
            Assert.That(fixture.value, Is.EqualTo(10));
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(fixture.target, Is.EqualTo(afterTarget));
            Assert.That(fixture.value, Is.EqualTo(20));

            UnityEngine.Object.DestroyImmediate(owner);
            UnityEngine.Object.DestroyImmediate(beforeTarget);
            UnityEngine.Object.DestroyImmediate(afterTarget);
        }

        [Test]
        public void CreatedComponent_UndoTargetsExactInstanceWhenTypeIsDuplicated()
        {
            var go = new GameObject("WorkflowDuplicateComponentHost");
            var existing = go.AddComponent<BoxCollider>();
            existing.center = Vector3.one;
            var created = go.AddComponent<BoxCollider>();
            created.center = Vector3.one * 2;

            WorkflowManager.BeginTask("component-create", "test");
            WorkflowManager.SnapshotCreatedComponent(created);
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            var remaining = go.GetComponents<BoxCollider>();
            Assert.That(remaining, Has.Length.EqualTo(1));
            Assert.That(remaining[0], Is.SameAs(existing));
            Assert.That(remaining[0].center, Is.EqualTo(Vector3.one));
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            Assert.That(go.GetComponents<BoxCollider>(), Has.Length.EqualTo(2));
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void SnapshotSequence_PreservesMoveChainInsteadOfOverwriting()
        {
            string source = AssetRoot + "/A.txt";
            string middle = AssetRoot + "/B.txt";
            string destination = AssetRoot + "/C.txt";
            File.WriteAllText(source, "original");
            AssetDatabase.ImportAsset(source);

            WorkflowManager.BeginTask("move-chain", "test");
            WorkflowManager.SnapshotAssetMove(source, middle);
            Assert.That(AssetDatabase.MoveAsset(source, middle), Is.Empty);
            WorkflowManager.SnapshotAssetMove(middle, destination);
            Assert.That(AssetDatabase.MoveAsset(middle, destination), Is.Empty);
            Assert.That(WorkflowManager.DeleteAssetToTrash(destination), Is.True);
            WorkflowManager.EndTask();
            var task = WorkflowManager.History.tasks.Last();

            Assert.That(task.snapshots.Select(s => s.type), Is.EqualTo(new[]
                { SnapshotType.Moved, SnapshotType.Moved, SnapshotType.Deleted }));
            Assert.That(WorkflowManager.UndoTask(task.id).success, Is.True);
            Assert.That(File.Exists(source), Is.True);
        }

        [Test]
        public void UndoPartialFailure_ReportsFailureAndKeepsTheTaskWhole()
        {
            var created = new GameObject("WorkflowPartialSuccess");
            WorkflowManager.BeginTask("partial", "test");
            WorkflowManager.CurrentTask.snapshots.Add(new UnitySkills.Internal.ObjectSnapshot
            {
                globalObjectId = "invalid:first",
                objectName = "first",
                type = SnapshotType.Modified
            });
            WorkflowManager.SnapshotCreatedGameObject(created);
            WorkflowManager.EndTask();
            string taskId = WorkflowManager.History.tasks.Last().id;

            var result = WorkflowManager.UndoTask(taskId);

            Assert.That(result.success, Is.False);
            Assert.That(result.succeeded, Is.EqualTo(1));
            Assert.That(result.failed, Is.EqualTo(1));
            Assert.That(result.details.Single(d => !d.success).error, Is.Not.Empty);
            Assert.That(GameObject.Find("WorkflowPartialSuccess"), Is.Null);
            Assert.That(WorkflowManager.History.tasks.Single(t => t.id == taskId).snapshots.Count, Is.EqualTo(2),
                "A partial undo does not split the step: it stays whole, and the restore that ran is recorded.");
        }

        [Test]
        public void RouterLogicalFailure_AbortsWorkflowAndRevertsMutationGroup()
        {
            string source = AssetRoot + "/RouterSource.txt";
            File.WriteAllText(source, "source");
            AssetDatabase.ImportAsset(source);
            int beforeCount = WorkflowManager.History.tasks.Count;

            string response = SkillRouter.Execute("asset_move",
                "{\"sourcePath\":\"" + source + "\",\"destinationPath\":\"Assets/MissingParent/RouterDest.txt\"}");
            var json = JObject.Parse(response);

            Assert.That(json["status"]?.Value<string>(), Is.EqualTo("error"));
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(beforeCount));
            Assert.That(File.Exists(source), Is.True);
        }

        [Test]
        public void PreThreeZeroHistory_IsMovedToTheLegacyBackup_NotMigrated()
        {
            // The pre-3.0 layout beside the store: the history file, its backup and a quarantined copy, and its blobs.
            string[] files = { "workflow_history.json", "workflow_history.json.bak", "workflow_history.corrupt.20261001000000.json" };
            foreach (string name in files)
                File.WriteAllText(Path.Combine(_tempRoot, name), "{\"tasks\":[{\"id\":\"legacy-task\"}]}");
            Directory.CreateDirectory(Path.Combine(_tempRoot, "workflow_files"));
            File.WriteAllText(Path.Combine(_tempRoot, "workflow_files", "ABCDEF"), "old blob");
            WorkflowManager.ResetStateForTests();

            Assert.That(WorkflowManager.History.tasks, Is.Empty, "Old tasks are not migrated.");
            string legacy = Path.Combine(_tempRoot, "workflow_legacy");
            foreach (string name in files)
            {
                Assert.That(File.Exists(Path.Combine(_tempRoot, name)), Is.False, name);
                Assert.That(File.Exists(Path.Combine(legacy, name)), Is.True, name);
            }
            Assert.That(Directory.Exists(Path.Combine(_tempRoot, "workflow_files")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(legacy, "workflow_files", "ABCDEF")), Is.EqualTo("old blob"));

            var backup = WorkflowManager.GetLegacyBackupInfo();
            Assert.That(backup, Is.Not.Null);
            Assert.That(backup.TotalBytes, Is.GreaterThan(0));
            Assert.That(backup.MovedAtUtc, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(5)));
            Assert.That(WorkflowManager.UndoTask("legacy-task").error, Is.EqualTo("Task not found"),
                "The old task ids retire with the old history.");

            // The new store records next to the backup, and a reload finds the move already done.
            var go = new GameObject("WorkflowLegacyNeighbour");
            WorkflowManager.BeginTask("after-legacy", "test");
            WorkflowManager.SnapshotObject(go.transform);
            go.transform.position += Vector3.right; // An unchanged snapshot is pruned, and the step would not be recorded.
            WorkflowManager.EndTask();
            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks.Select(t => t.tag), Is.EqualTo(new[] { "after-legacy" }));
            Assert.That(WorkflowManager.GetLegacyBackupInfo().MovedAtUtc, Is.EqualTo(backup.MovedAtUtc));
        }

        [Test]
        public void Cleanup_WithoutLimits_KeepsAnOldReferencedBlob()
        {
            string hash = RecordTextAssetSnapshot("Protected.txt", "protected");
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, hash), DateTime.UtcNow.AddDays(-10));

            WorkflowAutoCleanConfig.Enabled = true;
            WorkflowAutoCleanConfig.CapacityMB = 0;
            WorkflowAutoCleanConfig.MaxAgeDays = 0;
            WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(WorkflowFileStore.BlobExists(hash), Is.True);
        }

        [Test]
        public void Cleanup_KeepsAnOldReferencedBlob_AndReclaimsAnOldOrphan()
        {
            string hash = RecordTextAssetSnapshot("AgeProtected.txt", "protected by history");
            string orphanHash = WorkflowFileStore.StoreBytes(new byte[] { 5, 4, 3, 2, 1 });
            DateTime old = DateTime.UtcNow.AddDays(-10);
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, hash), old);
            File.SetLastWriteTimeUtc(Path.Combine(WorkflowFileStore.StoreRoot, orphanHash), old);

            WorkflowAutoCleanConfig.Enabled = true;
            WorkflowAutoCleanConfig.CapacityMB = 0;
            WorkflowAutoCleanConfig.MaxAgeDays = 1;
            var report = WorkflowManager.TrimHistoryIfNeeded(force: true);

            Assert.That(WorkflowFileStore.BlobExists(hash), Is.True, "The active segment names it, and is never evicted.");
            Assert.That(WorkflowFileStore.BlobExists(orphanHash), Is.False);
            Assert.That(report.reclaimedFileEntries, Is.GreaterThanOrEqualTo(1));
            Assert.That(report.removedTasks, Is.Zero);
        }

        private static string RecordTextAssetSnapshot(string fileName, string contents)
        {
            string path = AssetRoot + "/" + fileName;
            File.WriteAllText(path, contents);
            AssetDatabase.ImportAsset(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            WorkflowManager.BeginTask("protected", "test");
            WorkflowManager.SnapshotObject(asset);
            WorkflowManager.EndTask();
            string hash = WorkflowManager.History.tasks.Last().snapshots[0].fileHash;
            Assert.That(hash, Is.Not.Null.And.Not.Empty, "Precondition: the asset bytes are backed up.");
            return hash;
        }

        [Test]
        public void Schema_RequiresInputMatchingStringParameter_IsRequired()
        {
            var schema = JObject.Parse(SkillRouter.GetSchema());
            AssertSchemaParameterRequired(schema, "job_status", "jobId");
            AssertSchemaParameterRequired(schema, "batch_preview_set_property", "componentType");
            AssertSchemaParameterRequired(schema, "batch_preview_set_property", "propertyName");
            AssertSchemaParameterRequired(schema, "batch_preview_replace_material", "materialPath");
            AssertSchemaParameterRequired(schema, "workflow_plan", "skillsJson");
            // Was previously published as required:false, while the method body rejects both "omitted" and
            // "empty string" — the schema and actual behavior contradict each other on this skill's only parameter.
            AssertSchemaParameterRequired(schema, "addressables_group_create", "groupName");

            var dryRun = JObject.Parse(SkillRouter.DryRun("batch_preview_set_property", "{}"));
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False);
            Assert.That(dryRun["validation"]?["missingParams"]?.Values<string>(),
                Is.EquivalentTo(new[] { "componentType", "propertyName" }));
        }

        private static void AssertSchemaParameterRequired(JObject schema, string skillName, string parameterName)
        {
            var skill = schema["skills"]?.FirstOrDefault(s => s["name"]?.Value<string>() == skillName);
            var parameter = skill?["parameters"]?.FirstOrDefault(p => p["name"]?.Value<string>() == parameterName);
            Assert.That(parameter?["required"]?.Value<bool>(), Is.True,
                $"{skillName}.{parameterName} should be required in the generated schema");
        }

        private static void EnsureAssetFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets/Temp", "WorkflowPersistenceTests");
        }

    }

    public class WorkflowReferenceFixture : MonoBehaviour
    {
        public GameObject target;
        public int value;
    }
}

// Producer:Betsy
