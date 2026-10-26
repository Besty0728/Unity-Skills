using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The preview of an undo restore (<see cref="WorkflowManager.PreviewRedo"/>): it describes the redo a restore's undo would run, and applies nothing.
    /// </summary>
    [TestFixture]
    public class WorkflowRedoPreviewTests
    {
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowRedoPreviewTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));
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
        }

        [Test]
        public void PreviewRedo_DescribesTheRedoAndAppliesNothing()
        {
            var go = new GameObject("RedoPreviewProbe");
            WorkflowManager.BeginTask("redo-preview-probe", "test");
            WorkflowManager.SnapshotObject(go.transform);
            go.transform.position = new Vector3(1f, 0f, 0f);
            WorkflowManager.EndTask();
            var step = WorkflowManager.History.tasks.Last();

            Assert.That(WorkflowManager.UndoTask(step.DisplayId).success, Is.True);
            var restore = WorkflowManager.GetUndoableRestores().Single();
            string taskId = "s" + restore.seq;
            int taskCount = WorkflowManager.History.tasks.Count;

            var preview = WorkflowManager.PreviewRedo(taskId, "test");

            Assert.That(preview.error, Is.Null);
            Assert.That(preview.steps, Is.EqualTo(1), "The redo covers the restore's own step.");
            Assert.That(preview.to, Is.EqualTo(taskId));
            Assert.That(go.transform.position, Is.EqualTo(Vector3.zero), "The undo stays in place: the preview applies nothing.");
            Assert.That(WorkflowManager.GetUndoableRestores().Single().seq, Is.EqualTo(restore.seq));
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(taskCount));
        }

        [Test]
        public void PreviewRedo_RefusesATaskIdWithNoRestoreToUndo()
        {
            var preview = WorkflowManager.PreviewRedo("s999", "test");

            Assert.That(preview.error, Is.Not.Null);
            Assert.That(preview.errorCode, Is.EqualTo(SkillErrorCode.NotFound.ToWireString()));
        }
    }
}

// Producer:Betsy
