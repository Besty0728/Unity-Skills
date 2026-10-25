using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The dryRun preview of the workflow restore skills: what a restore would do is reported in changes, and nothing is applied.
    /// </summary>
    [TestFixture]
    public class WorkflowRestorePreviewTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowRestorePreviewAgentA";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowRestorePreviewTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "__WorkflowTests__");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/RestorePreviewScene.unity"), Is.True);
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
        public void Preview_CountsEachKindOfChange_AndChangesNothing()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WpKeep"), ("x", 0f)));
                Call("gameobject_create", Args(("name", "WpDel"), ("x", 1f)));
                Call("workflow_checkpoint", Args(("label", "wpBase")));
                Call("gameobject_create", Args(("name", "WpNew"), ("x", 2f)));
                Call("gameobject_set_transform", Args(("name", "WpKeep"), ("posX", 5f)));
                Call("component_add", Args(("name", "WpKeep"), ("componentType", "BoxCollider")));
                Call("gameobject_delete", Args(("name", "WpDel")));

                int checkpoints = WorkflowManager.History.checkpoints.Count;
                int restores = WorkflowManager.History.restores.Count;
                int tasks = WorkflowManager.History.tasks.Count;

                var dry = DryRunJson("workflow_restore", Args(("to", "wpBase")));

                Assert.That(dry.Value<bool>("valid"), Is.True, dry.ToString());
                // A transform step snapshots the GameObject and its Transform, so both are restored: two modifies.
                Assert.That(Plan(dry, "modify"), Is.EqualTo(2), dry.ToString());
                Assert.That(Plan(dry, "delete"), Is.EqualTo(2), dry.ToString());
                Assert.That(Plan(dry, "recreate"), Is.EqualTo(1), dry.ToString());
                var items = dry["changes"]["items"].Select(item => item.ToString()).ToList();
                Assert.That(items, Does.Contain("WpKeep (GameObject) modify"), dry.ToString());
                Assert.That(items, Does.Contain("WpNew (GameObject) delete"), dry.ToString());
                Assert.That(items, Does.Contain("WpDel (GameObject) recreate"), dry.ToString());
                Assert.That(WorkflowManager.History.checkpoints.Count, Is.EqualTo(checkpoints), "A dryRun takes no checkpoint.");
                Assert.That(WorkflowManager.History.restores.Count, Is.EqualTo(restores), "A dryRun writes no restore record.");
                Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(tasks), "A dryRun writes no step.");
            }

            Assert.That(X("WpKeep"), Is.EqualTo(5f).Within(0.001f), "The dryRun leaves the moved object where it is.");
            Assert.That(GameObject.Find("WpNew"), Is.Not.Null, "The dryRun leaves the created object in place.");
            Assert.That(GameObject.Find("WpDel"), Is.Null, "The dryRun leaves the deleted object deleted.");
            Assert.That(GameObject.Find("WpKeep").GetComponent<BoxCollider>(), Is.Not.Null, "The dryRun leaves the added component.");
        }

        [Test]
        public void UserEdit_PreviewListsOneConflict_WithTheIdTheRestoreRefusesWith()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WpUser"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wpUser")));
                Call("gameobject_set_transform", Args(("name", "WpUser"), ("posX", 5f)));
                UserEdit("WpUser", 9f);

                var dry = DryRunJson("workflow_restore", Args(("to", "wpUser")));
                var conflicts = dry["changes"]["conflicts"] as JArray;
                Assert.That(conflicts.Count, Is.EqualTo(1), dry.ToString());
                string previewed = conflicts[0].Value<string>("id");

                var refused = RestoreRaw("wpUser");
                var refusedConflicts = refused.Value<JArray>("conflicts");
                Assert.That(refusedConflicts[0].Value<string>("id"), Is.EqualTo(previewed), refused.ToString());
            }
            Assert.That(X("WpUser"), Is.EqualTo(9f).Within(0.001f), "The preview and the refusal change nothing.");
        }

        [Test]
        public void UnknownPosition_IsASemanticErrorOnTo()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var dry = DryRunJson("workflow_restore", Args(("to", "wpNoSuchPosition")));

                Assert.That(dry.Value<bool>("valid"), Is.False, dry.ToString());
                var errors = dry["validation"]["semanticErrors"] as JArray;
                Assert.That(errors.Any(error => error.Value<string>("field") == "to"), Is.True, dry.ToString());
            }
        }

        [Test]
        public void UndoTaskOfAMiddleStep_PreviewsItsModifies_AndChangesNothing()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WpMid"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WpMid"), ("posX", 1f)));
                Call("gameobject_set_transform", Args(("name", "WpMid"), ("posX", 2f)));
                Call("gameobject_set_transform", Args(("name", "WpMid"), ("posX", 3f)));

                // The second of the three transform steps is the middle one.
                var transforms = WorkflowManager.History.tasks
                    .Where(task => task.tag == "gameobject_set_transform")
                    .OrderBy(task => task.seq)
                    .ToList();
                var middle = transforms[transforms.Count - 2];

                var dry = DryRunJson("workflow_undo_task", Args(("taskId", "s" + middle.seq)));

                // The step's GameObject and Transform snapshots are both undone.
                Assert.That(Plan(dry, "modify"), Is.EqualTo(2), dry.ToString());
                var items = dry["changes"]["items"].Select(item => item.ToString()).ToList();
                Assert.That(items, Does.Contain("WpMid (GameObject) modify"), dry.ToString());
                Assert.That(items, Does.Contain("WpMid (Transform) modify"), dry.ToString());
            }
            Assert.That(X("WpMid"), Is.EqualTo(3f).Within(0.001f), "The dryRun leaves the object at its latest value.");
        }

        [Test]
        public void MoreThanTwentyItems_ListsTwenty_AndCountsTheRest()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "wpMany")));
                for (int i = 0; i < 25; i++)
                    Call("gameobject_create", Args(("name", "WpMany" + i), ("x", (float)i)));

                var dry = DryRunJson("workflow_restore", Args(("to", "wpMany")));

                Assert.That(Plan(dry, "delete"), Is.EqualTo(25), dry.ToString());
                Assert.That(dry["changes"]["items"].Count(), Is.EqualTo(20), dry.ToString());
                Assert.That(dry["changes"].Value<int>("moreItems"), Is.EqualTo(5), dry.ToString());
            }
        }

        private static JObject DryRunJson(string skill, string json) => JObject.Parse(SkillRouter.DryRun(skill, json));

        private static int Plan(JObject dry, string kind) => dry["changes"]["plan"].Value<int>(kind);

        // Changes the transform the way a person in the Inspector does: outside any REST call, so the journal records it as an editor change.
        private static void UserEdit(string name, float x)
        {
            EditorChangeTrackerService.ResetRestWindowForTests();
            var go = GameObject.Find(name);
            Undo.RecordObject(go.transform, "Restore preview test user edit");
            go.transform.position = new Vector3(x, 0f, 0f);
            Undo.FlushUndoRecordObjects();
            EditorChangeTrackerService.FlushPendingProperties();
        }

        private static JObject Call(string skill, string json) => JObject.Parse(SkillRouter.Execute(skill, json));

        // The whole response, refusals included.
        private static JObject RestoreRaw(string to, string conflicts = null) =>
            Call("workflow_restore", Args(("to", to), ("conflicts", conflicts)));

        private static string Args(params (string Key, object Value)[] values)
        {
            var args = new JObject();
            foreach (var (key, value) in values)
                args[key] = value == null ? null : JToken.FromObject(value);
            return args.ToString();
        }

        private static float X(string name) => GameObject.Find(name).transform.position.x;
    }
}

// Producer:Betsy
