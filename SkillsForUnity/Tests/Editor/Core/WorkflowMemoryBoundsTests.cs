using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Bounds on the workflow's memory: payloads read back for a restore are released after it, unsaved steps drop their
    /// payloads past a limit, the agent activity map is capped, and a step past its capture limit says so.
    /// </summary>
    [TestFixture]
    public class WorkflowMemoryBoundsTests
    {
        private const string AssetRoot = "Assets/Temp/WorkflowMemoryBoundsTests";
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowMemoryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder("Assets/Temp")) AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets/Temp", "WorkflowMemoryBoundsTests");
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowStore.AppendFailsForTests = false;
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
        }

        [Test]
        public void UndoTask_ReleasesThePayloadsItReadBack()
        {
            var asset = WriteAsset("Undo.txt", "v0");
            WorkflowManager.BeginTask("edit", "first");
            WorkflowManager.SnapshotObject(asset);
            // The step itself makes the change; an edit after it would be a user conflict that refuses the undo.
            Overwrite("Undo.txt", "v1");
            WorkflowManager.EndTask();
            var first = WorkflowManager.History.tasks.Last();

            var undo = WorkflowManager.UndoTask(first.DisplayId);
            Assert.That(undo.success, Is.True, undo.error);
            AssertEveryPayloadReleased();
        }

        [Test]
        public void PreviewTo_ReleasesThePayloadsItReadBack()
        {
            var asset = WriteAsset("Preview.txt", "v0");
            WorkflowManager.BeginTask("edit", "first");
            WorkflowManager.SnapshotObject(asset);
            WorkflowManager.EndTask();
            var first = WorkflowManager.History.tasks.Last();

            var preview = WorkflowManager.PreviewTo(first.DisplayId, true, "test");
            Assert.That(preview.error, Is.Null);
            AssertEveryPayloadReleased();
        }

        [Test]
        public void UnsavedTasks_PastTheLimit_DropThePayloadOfTheOldest()
        {
            var asset = WriteAsset("Unsaved.txt", "v0");
            WorkflowStore.AppendFailsForTests = true;
            for (int i = 0; i < 51; i++)
            {
                WorkflowManager.BeginTask("unsaved", "step " + i);
                WorkflowManager.SnapshotObject(asset);
                WorkflowManager.EndTask();
            }
            WorkflowStore.AppendFailsForTests = false;

            var tasks = WorkflowManager.History.tasks;
            var oldest = tasks.First().snapshots[0];
            var newest = tasks.Last().snapshots[0];
            Assert.That(oldest.payloadReleased, Is.True, "The oldest unsaved step lost its payload.");
            Assert.That(oldest.payloadError, Does.Contain("never saved"));
            Assert.That(newest.payloadReleased, Is.False, "A recent unsaved step keeps its payload.");
            Assert.That(WorkflowManager.UndoTask(tasks.First().DisplayId).success, Is.False);
        }

        [Test]
        public void AgentActivity_PastTheLimit_ForgetsTheLeastRecentlyActiveAgent()
        {
            var getActivity = typeof(WorkflowManager).GetMethod("GetAgentActivity", BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < 70; i++)
            {
                object activity = getActivity.Invoke(null, new object[] { "agent-" + i });
                activity.GetType().GetField("lastCallMs").SetValue(activity, (long)(i + 1));
            }

            var agents = (IDictionary)typeof(WorkflowManager)
                .GetField("_agentActivity", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            Assert.That(agents.Count, Is.LessThanOrEqualTo(64));
            Assert.That(agents.Contains("agent-5"), Is.False, "The least recently active agents were not forgotten first.");
            Assert.That(agents.Contains("agent-6"), Is.True);
            Assert.That(agents.Contains("agent-69"), Is.True);
        }

        [Test]
        public void StepPastTheCaptureLimit_CountsTheSkippedObjects_AndSurvivesReload()
        {
            var asset = WriteAsset("Limit.txt", "v0");
            WorkflowManager.BeginTask("many", "fill the step");
            for (int i = 0; i < 500; i++)
                WorkflowManager.CurrentTask.snapshots.Add(new ObjectSnapshot { globalObjectId = "GlobalObjectId_V1-test-" + i, type = SnapshotType.Modified, originalJson = "{}" });
            for (int i = 0; i < 3; i++)
                WorkflowManager.SnapshotObject(asset);
            WorkflowManager.EndTask();

            var recorded = WorkflowManager.History.tasks.Last();
            Assert.That(recorded.snapshotsSkipped, Is.EqualTo(3));

            WorkflowManager.ResetStateForTests();
            Assert.That(WorkflowManager.History.tasks.Last().snapshotsSkipped, Is.EqualTo(3));
        }

        [Test]
        public void PreviewSteps_ReportsAStepWithSkippedObjects_AsUnresolvable()
        {
            var steps = new List<WorkflowTask> { new WorkflowTask { seq = 42, snapshotsSkipped = 3 } };

            var preview = WorkflowManager.PreviewSteps(steps, "s42", "test");
            Assert.That(preview.unresolvable.Any(item => item.target == "s42" && item.reason.Contains("3 objects")), Is.True);
        }

        private void AssertEveryPayloadReleased()
        {
            foreach (var task in WorkflowManager.History.tasks)
                foreach (var snapshot in task.snapshots)
                    Assert.That(snapshot.payloadReleased, Is.True, "Step " + task.DisplayId + " keeps a payload in memory.");
        }

        private UnityEngine.Object WriteAsset(string name, string content)
        {
            string path = AssetRoot + "/" + name;
            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        }

        private static void Overwrite(string name, string content)
        {
            File.WriteAllText(AssetRoot + "/" + name, content);
            AssetDatabase.ImportAsset(AssetRoot + "/" + name);
        }
    }
}

// Producer:Betsy
