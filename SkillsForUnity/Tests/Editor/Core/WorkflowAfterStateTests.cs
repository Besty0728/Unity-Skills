using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The after-state of each step (P2a): the state and hash a Modified snapshot has when its step ends, the pruning of
    /// snapshots that did not change, and the round trip of the after-state through the log.
    /// </summary>
    [TestFixture]
    public class WorkflowAfterStateTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowAfterStateAgentA";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowAfterStateTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "__WorkflowTests__");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/AfterStateScene.unity"), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            // The scene that holds the test objects must be closed before its folder can be deleted.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
        }

        [Test]
        public void SetTransform_RecordsTheTransformAfterTheStep()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "AsMoved"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "AsMoved"), ("posX", 3f)));
            }

            var snapshot = StepSnapshot("AsMoved", SnapshotType.Modified, "Transform");
            Assert.That(snapshot.afterStateHash, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.afterStateHash, Is.Not.EqualTo(WorkflowStateHash.StateHash(snapshot.originalJson, snapshot.objectReferences)));
            Assert.That(snapshot.afterStateHash, Is.EqualTo(WorkflowStateHash.StateHash(snapshot.afterJson, snapshot.afterObjectReferences)));
            Assert.That(snapshot.afterJson, Is.EqualTo(EditorJsonUtility.ToJson(GameObject.Find("AsMoved").transform)));
            Assert.That(JObject.Parse(snapshot.afterJson).Value<JObject>("Transform").Value<JObject>("m_LocalPosition").Value<float>("x"), Is.EqualTo(3f).Within(0.001f));
        }

        [Test]
        public void UnchangedSnapshotIsPrunedWhileTheChangedOneStays()
        {
            var unchanged = new GameObject("AsUnchanged");
            var changed = new GameObject("AsChanged");

            WorkflowManager.BeginTask("after-state-prune", "test");
            WorkflowManager.SnapshotObject(unchanged.transform);
            WorkflowManager.SnapshotObject(changed.transform);
            changed.transform.position = new Vector3(4f, 0f, 0f);
            WorkflowManager.EndTask();

            var step = WorkflowManager.History.tasks.Last();
            Assert.That(step.snapshots.Any(snapshot => snapshot.objectName == "AsUnchanged"), Is.False, "An unchanged object is not part of the step.");
            Assert.That(step.snapshots.Any(snapshot => snapshot.objectName == "AsChanged" && snapshot.type == SnapshotType.Modified), Is.True);
        }

        [Test]
        public void MaterialChange_KeepsItsAssetSnapshotWithAnAfterState()
        {
            string matPath = AssetRoot + "/AsColor.mat";
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("material_create", Args(("name", "AsColor"), ("savePath", matPath)));
                Call("material_set_color", Args(("path", matPath), ("r", 1f), ("g", 0f), ("b", 0f)));
            }

            var snapshot = Hydrated(WorkflowManager.History.tasks.Last().snapshots
                .Single(candidate => candidate.type == SnapshotType.Modified && candidate.assetPath == matPath));
            Assert.That(snapshot.afterStateHash, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.afterJson, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.afterJson, Is.Not.EqualTo(snapshot.originalJson));
        }

        [Test]
        public void AfterState_SurvivesTheLogRoundTrip()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "AsReplayed"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "AsReplayed"), ("posX", 2f)));
            }
            var before = StepSnapshot("AsReplayed", SnapshotType.Modified, "Transform");
            string afterHash = before.afterStateHash;
            string afterJson = before.afterJson;

            WorkflowManager.LoadHistory();

            var reloaded = WorkflowManager.History.tasks.Last().snapshots
                .Single(candidate => candidate.type == SnapshotType.Modified && candidate.objectName == "AsReplayed" && candidate.typeName == "Transform");
            Assert.That(reloaded.afterStateHash, Is.EqualTo(afterHash), "The header keeps the after-state hash.");

            Assert.That(Hydrated(reloaded).afterJson, Is.EqualTo(afterJson));
            Assert.That(reloaded.afterObjectReferencesCaptured, Is.True);
        }

        private static ObjectSnapshot StepSnapshot(string objectName, SnapshotType type, string typeName)
        {
            return Hydrated(WorkflowManager.History.tasks.Last().snapshots
                .Single(candidate => candidate.type == type && candidate.objectName == objectName && candidate.typeName == typeName));
        }

        // A step's payload leaves memory once it is in the log; the after-state comes back from the blob on demand.
        private static ObjectSnapshot Hydrated(ObjectSnapshot snapshot)
        {
            Assert.That(WorkflowManager.TryHydrate(snapshot, out string error), Is.True, error);
            return snapshot;
        }

        private static JObject Call(string skill, string json) => JObject.Parse(SkillRouter.Execute(skill, json));

        private static string Args(params (string Key, object Value)[] values)
        {
            var args = new JObject();
            foreach (var (key, value) in values)
                args[key] = value == null ? null : JToken.FromObject(value);
            return args.ToString();
        }
    }
}

// Producer:Betsy
