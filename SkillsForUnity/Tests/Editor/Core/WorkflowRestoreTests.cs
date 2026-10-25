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
    [TestFixture]
    public class WorkflowRestoreTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowRestoreAgentA";
        private const string AgentB = "WorkflowRestoreAgentB";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowRestoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "__WorkflowTests__");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/RestoreScene.unity"), Is.True);
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
        public void Restore_RevertsTheCallersStepsAfterTheCheckpoint()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfA"), ("x", 0f)));
                Call("gameobject_create", Args(("name", "WfC")));
                Call("workflow_checkpoint", Args(("label", "cp1")));
                Call("gameobject_set_transform", Args(("name", "WfA"), ("posX", 5f)));
                Call("gameobject_create", Args(("name", "WfB")));
                Call("gameobject_delete", Args(("name", "WfC")));

                Restore("cp1");
            }

            Assert.That(GameObject.Find("WfA"), Is.Not.Null);
            Assert.That(X("WfA"), Is.EqualTo(0f).Within(0.001f));
            Assert.That(GameObject.Find("WfB"), Is.Null);
            Assert.That(GameObject.Find("WfC"), Is.Not.Null, "A deleted object from before the checkpoint comes back.");
        }

        [Test]
        public void Restore_ToItsGuardUndoesTheRestore()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfG"), ("x", 0f)));
                Call("gameobject_create", Args(("name", "WfGC")));
                Call("workflow_checkpoint", Args(("label", "guardCp")));
                Call("gameobject_set_transform", Args(("name", "WfG"), ("posX", 5f)));
                Call("gameobject_create", Args(("name", "WfGB")));
                Call("gameobject_delete", Args(("name", "WfGC")));

                var first = Restore("guardCp");
                Assert.That(X("WfG"), Is.EqualTo(0f).Within(0.001f));

                // Before the first restore: WfG moved to x=5, WfGB exists, WfGC was deleted.
                Restore(first.Value<string>("guard"));
            }

            Assert.That(X("WfG"), Is.EqualTo(5f).Within(0.001f));
            Assert.That(GameObject.Find("WfGB"), Is.Not.Null);
            Assert.That(GameObject.Find("WfGC"), Is.Null);
        }

        [Test]
        public void Restore_TwiceAcrossARecreate_StillFindsTheObject()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfX"), ("x", 1f)));
                Call("workflow_checkpoint", Args(("label", "beforeDelete")));
                Call("gameobject_delete", Args(("name", "WfX")));

                Restore("beforeDelete");
                Assert.That(GameObject.Find("WfX"), Is.Not.Null);

                Call("gameobject_set_transform", Args(("name", "WfX"), ("posX", 3f)));
                Restore("beforeDelete");
            }

            Assert.That(GameObject.Find("WfX"), Is.Not.Null, "The second restore follows the recreated object through its alias.");
            Assert.That(RootCount("WfX"), Is.EqualTo(1), "The second restore must not duplicate the object.");
            Assert.That(X("WfX"), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Restore_RepeatedToTheSameCheckpointAfterADelete_DoesNotDuplicate()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfR"), ("x", 2f)));
                Call("workflow_checkpoint", Args(("label", "beforeDeleteTwice")));
                Call("gameobject_delete", Args(("name", "WfR")));

                Restore("beforeDeleteTwice");
                var second = Restore("beforeDeleteTwice");

                Assert.That(second.Value<int>("failed"), Is.EqualTo(0), second.ToString());
                Assert.That(second.Value<int>("skipped"), Is.GreaterThanOrEqualTo(1), second.ToString());
            }

            Assert.That(RootCount("WfR"), Is.EqualTo(1));
            Assert.That(X("WfR"), Is.EqualTo(2f).Within(0.001f));
        }

        [Test]
        public void Restore_SelfLeavesAnotherAgentsStepsAlone()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
                Call("workflow_checkpoint", Args(("label", "shared")));
            using (SkillsAuditLog.BeginRequestContext(-1, AgentB, true))
                Call("gameobject_create", Args(("name", "WfOther")));
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfMine")));
                Restore("shared");
            }

            Assert.That(GameObject.Find("WfMine"), Is.Null);
            Assert.That(GameObject.Find("WfOther"), Is.Not.Null, "Another agent's step is not reverted by agents 'self'.");
        }

        [Test]
        public void Restore_MaterialColorChangeMatchesTheFileAtTheCheckpoint()
        {
            string matPath = AssetRoot + "/RestoreMat.mat";
            byte[] atCheckpoint;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("material_create", Args(("name", "RestoreMat"), ("savePath", matPath)));
                Call("workflow_checkpoint", Args(("label", "matCp")));
                atCheckpoint = File.ReadAllBytes(matPath);
                Call("material_set_color", Args(("path", matPath), ("r", 1f), ("g", 0f), ("b", 0f), ("a", 1f)));
                AssetDatabase.SaveAssets();
                Assert.That(File.ReadAllBytes(matPath), Is.Not.EqualTo(atCheckpoint), "The colour change must reach the file first.");

                Restore("matCp");
            }

            CollectionAssert.AreEqual(atCheckpoint, File.ReadAllBytes(matPath));
        }

        [Test]
        public void Restore_FailingItemIsReportedAndTheRestOfTheRunStillRuns()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfP")));
                Call("workflow_checkpoint", Args(("label", "payloadCp")));
                Call("gameobject_set_transform", Args(("name", "WfP"), ("posX", 4f)));
                Call("gameobject_create", Args(("name", "WfNew")));
                DeletePayloadBlob("WfP");

                var result = Restore("payloadCp");

                Assert.That(result.Value<int>("failed"), Is.EqualTo(1), result.ToString());
                Assert.That(result.Value<JArray>("failedItems").ToString(), Does.Contain("WfP"));
                Assert.That(GameObject.Find("WfNew"), Is.Null, "The later creation is still reverted.");
            }
        }

        [Test]
        public void Restore_UnknownPositionIsNotFoundAndListsRecentCheckpoints()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "known")));
                var result = Call("workflow_restore", Args(("to", "nowhere")));

                Assert.That(result.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.NotFound.ToWireString()));
                Assert.That(result.Value<JArray>("recentCheckpoints").ToString(), Does.Contain("known"));
            }
        }

        [Test]
        public void Restore_TransformOfADeletedObjectIsRestoredOntoTheRecreatedObject()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfCube"), ("primitiveType", "Cube"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "beforeMove")));
                Call("gameobject_set_transform", Args(("name", "WfCube"), ("posX", 5f)));
                Call("gameobject_delete", Args(("name", "WfCube")));

                var result = Restore("beforeMove");

                Assert.That(result.Value<int>("failed"), Is.EqualTo(0), result.ToString());
            }

            Assert.That(GameObject.Find("WfCube"), Is.Not.Null);
            Assert.That(X("WfCube"), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void Restore_ComponentPropertyOfAChildComesBackWithItsParent()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfParent")));
                Call("gameobject_create", Args(("name", "WfChild")));
                Call("gameobject_set_parent", Args(("childName", "WfChild"), ("parentName", "WfParent")));
                Call("component_add", Args(("name", "WfChild"), ("componentType", "Rigidbody")));
                Call("workflow_checkpoint", Args(("label", "beforeProp")));
                Call("component_set_property", Args(("name", "WfChild"), ("componentType", "Rigidbody"), ("propertyName", "mass"), ("value", "9")));
                Call("gameobject_delete", Args(("name", "WfParent")));

                var result = Restore("beforeProp");
                Assert.That(result.Value<int>("failed"), Is.EqualTo(0), result.ToString());

                var again = Restore("beforeProp");
                Assert.That(again.Value<int>("failed"), Is.EqualTo(0), again.ToString());
            }

            Assert.That(RootCount("WfParent"), Is.EqualTo(1), "A repeated restore must not duplicate the parent.");
            Assert.That(Mass("WfParent/WfChild"), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Restore_ChangeToARecreatedChildResolvesAndRestoresAgain()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfParent2")));
                Call("gameobject_create", Args(("name", "WfChild2")));
                Call("gameobject_set_parent", Args(("childName", "WfChild2"), ("parentName", "WfParent2")));
                Call("component_add", Args(("name", "WfChild2"), ("componentType", "Rigidbody")));
                Call("workflow_checkpoint", Args(("label", "beforeProp2")));
                Call("component_set_property", Args(("name", "WfChild2"), ("componentType", "Rigidbody"), ("propertyName", "mass"), ("value", "9")));
                Call("gameobject_delete", Args(("name", "WfParent2")));
                Restore("beforeProp2");

                Call("workflow_checkpoint", Args(("label", "beforeSecond")));
                Call("component_set_property", Args(("name", "WfChild2"), ("componentType", "Rigidbody"), ("propertyName", "mass"), ("value", "7")));
                var result = Restore("beforeSecond");

                Assert.That(result.Value<int>("failed"), Is.EqualTo(0), result.ToString());
            }

            Assert.That(Mass("WfParent2/WfChild2"), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void Restore_ExplicitCheckpointAtTheHeadKeepsItsLabelAsTheGuard()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "early")));
                Call("gameobject_create", Args(("name", "WfK")));
                Call("workflow_checkpoint", Args(("label", "keep me")));
                string keepId = WorkflowManager.FindCheckpoint("keep me").id;

                var result = Restore("early");

                Assert.That(result.Value<string>("guard"), Is.EqualTo(keepId));
                Assert.That(WorkflowManager.FindCheckpoint(keepId).label, Is.EqualTo("keep me"));

                WorkflowManager.LoadHistory();
                Assert.That(WorkflowManager.FindCheckpoint(keepId).label, Is.EqualTo("keep me"), "The label survives a replay of the log.");
            }
        }

        [Test]
        public void Restore_MaterialMovedThenDeleted_ComesBackAtItsOriginalPath()
        {
            string matPath = AssetRoot + "/RestoreMoved.mat";
            string movedPath = AssetRoot + "/RestoreMovedAway.mat";
            JObject restored;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("material_create", Args(("name", "RestoreMoved"), ("savePath", matPath)));
                Call("workflow_checkpoint", Args(("label", "moveCp")));
                Call("asset_move", Args(("sourcePath", matPath), ("destinationPath", movedPath)));
                Call("asset_delete", Args(("assetPath", movedPath)));

                restored = Restore("moveCp");
            }

            Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            Assert.That(File.Exists(matPath), Is.True, "The material comes back at the path it had at the checkpoint.");
            Assert.That(File.Exists(movedPath), Is.False);
        }

        [Test]
        public void Undo_OfAMiddleStep_LeavesALaterStepOnAnotherObjectIntact()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfU1"), ("x", 0f)));
                Call("gameobject_create", Args(("name", "WfU2"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WfU1"), ("posX", 1f)));
                string middle = WorkflowManager.History.tasks.Last().DisplayId;
                Call("gameobject_set_transform", Args(("name", "WfU2"), ("posX", 2f)));

                Undo(middle);
            }

            Assert.That(X("WfU1"), Is.EqualTo(0f).Within(0.001f));
            Assert.That(X("WfU2"), Is.EqualTo(2f).Within(0.001f), "A later step on another object stays.");
        }

        [Test]
        public void Redo_AfterUndoOfAStep_ReappliesIt()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfRd"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WfRd"), ("posX", 3f)));
                string step = WorkflowManager.History.tasks.Last().DisplayId;
                Undo(step);
                Assert.That(X("WfRd"), Is.EqualTo(0f).Within(0.001f));

                var redo = Call("workflow_redo_task", Args(("taskId", step)));
                Assert.That(redo.Value<string>("error"), Is.Null, redo.ToString());
            }

            Assert.That(X("WfRd"), Is.EqualTo(3f).Within(0.001f));
        }

        [Test]
        public void Redo_OfAnEarlierUndo_LeavesLaterStepsAlone()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfRa"), ("x", 0f)));
                Call("gameobject_create", Args(("name", "WfRb"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WfRa"), ("posX", 1f)));
                string first = WorkflowManager.History.tasks.Last().DisplayId;
                Call("gameobject_set_transform", Args(("name", "WfRb"), ("posX", 2f)));
                string second = WorkflowManager.History.tasks.Last().DisplayId;

                Undo(first);
                Call("gameobject_set_transform", Args(("name", "WfRb"), ("posY", 5f)));
                UndoOverwritingConflicts(second);
                Assert.That(X("WfRb"), Is.EqualTo(0f).Within(0.001f), "Precondition: the second undo reverted WfRb.");

                var redo = Call("workflow_redo_task", Args(("taskId", first)));
                Assert.That(redo.Value<int>("failed"), Is.EqualTo(0), redo.ToString());
            }

            Assert.That(X("WfRa"), Is.EqualTo(1f).Within(0.001f), "The redo takes back only the undo of the first step.");
            Assert.That(X("WfRb"), Is.EqualTo(0f).Within(0.001f), "The later undo of the second step stays.");
        }

        [Test]
        public void UndoneList_ListsTheRestoreUntilItIsRedone()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfL"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WfL"), ("posX", 4f)));
                string step = WorkflowManager.History.tasks.Last().DisplayId;
                Undo(step);

                var listed = Call("workflow_undone_list", Args()).Value<JObject>("result");
                Assert.That(listed.Value<int>("count"), Is.EqualTo(1));
                string restoreId = listed["restores"][0].Value<string>("seq");

                var redo = Call("workflow_redo_task", Args(("taskId", step)));
                Assert.That(redo.Value<string>("error"), Is.Null, redo.ToString());
                var after = Call("workflow_undone_list", Args()).Value<JObject>("result");
                Assert.That(after["restores"].Select(restore => restore.Value<string>("seq")), Does.Not.Contain(restoreId),
                    "The redone restore is no longer listed.");
            }
        }

        [Test]
        public void SessionUndo_RevertsOnlyThatSessionsSteps()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WfS1")));
                WorkflowManager.History.tasks.Last().sessionId = "restore-test-session";
                Call("gameobject_create", Args(("name", "WfS2")));

                var undo = Call("workflow_session_undo", Args(("sessionId", "restore-test-session")));
                Assert.That(undo.Value<string>("error"), Is.Null, undo.ToString());
            }

            Assert.That(GameObject.Find("WfS1"), Is.Null);
            Assert.That(GameObject.Find("WfS2"), Is.Not.Null, "A step outside the session stays.");
        }

        // Undoes a step whose target a later step of the caller changed: the refusal's conflicts are overwritten by their ids.
        private static void UndoOverwritingConflicts(string taskId)
        {
            var refused = Call("workflow_undo_task", Args(("taskId", taskId)));
            if (refused.Value<string>("errorCode") != SkillErrorCode.WorkflowConflict.ToWireString())
            {
                Assert.That(refused.Value<string>("error"), Is.Null, refused.ToString());
                return;
            }
            string ids = string.Join(",", refused.Value<JArray>("conflicts").Select(conflict => conflict.Value<string>("id")));
            var response = Call("workflow_undo_task", Args(("taskId", taskId), ("conflicts", ids)));
            Assert.That(response.Value<string>("error"), Is.Null, response.ToString());
            Assert.That(response.Value<JObject>("result").Value<int>("failed"), Is.EqualTo(0), response.ToString());
        }

        // The success result of workflow_undo_task, failing the test when the call was refused.
        private static JObject Undo(string taskId)
        {
            var response = Call("workflow_undo_task", Args(("taskId", taskId)));
            Assert.That(response.Value<string>("error"), Is.Null, response.ToString());
            Assert.That(response.Value<JObject>("result").Value<int>("failed"), Is.EqualTo(0), response.ToString());
            return response.Value<JObject>("result");
        }

        private static float Mass(string path) => GameObject.Find(path).GetComponent<Rigidbody>().mass;

        private static JObject Call(string skill, string json) => JObject.Parse(SkillRouter.Execute(skill, json));

        // A successful skill result comes back under "result"; a failure is top-level.
        private static JObject Restore(string to)
        {
            var response = Call("workflow_restore", Args(("to", to)));
            Assert.That(response.Value<string>("error"), Is.Null, response.ToString());
            return response.Value<JObject>("result");
        }

        private static string Args(params (string Key, object Value)[] values)
        {
            var args = new JObject();
            foreach (var (key, value) in values)
                args[key] = value == null ? null : JToken.FromObject(value);
            return args.ToString();
        }

        private static int RootCount(string name) =>
            SceneManager.GetActiveScene().GetRootGameObjects().Count(go => go.name == name);

        private static float X(string name) => GameObject.Find(name).transform.position.x;

        // Removes the stored payload of the Modified snapshot of one object, so reading it back fails.
        private static void DeletePayloadBlob(string objectName)
        {
            var snapshot = WorkflowManager.History.tasks
                .SelectMany(task => task.snapshots)
                .Last(candidate => candidate.type == SnapshotType.Modified && candidate.objectName == objectName);
            Assert.That(snapshot.payloadHash, Is.Not.Null.And.Not.Empty, "The snapshot should have been stored with a payload.");
            foreach (var file in Directory.GetFiles(WorkflowFileStore.StoreRoot, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith(snapshot.payloadHash, StringComparison.OrdinalIgnoreCase))
                    File.Delete(file);
            }
        }
    }
}

// Producer:Betsy
