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
    /// Conflict detection of workflow restores: a restore refuses with WORKFLOW_CONFLICT while changes it would overwrite are
    /// unanswered, skips them under "keep", or overwrites the listed ids. Changes the editor made outside a REST call come from
    /// the journal; another agent's changes come from its steps.
    /// </summary>
    [TestFixture]
    public class WorkflowConflictTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowConflictAgentA";
        private const string AgentB = "WorkflowConflictAgentB";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowConflictTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "__WorkflowTests__");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/ConflictScene.unity"), Is.True);
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
        public void UserEdit_RefusesUntilTheConflictIsAnswered()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcUser"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcUser")));
                Call("gameobject_set_transform", Args(("name", "WcUser"), ("posX", 5f)));
                UserEdit("WcUser", 9f);

                var refused = RestoreRaw("wcUser");

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                var conflicts = refused.Value<JArray>("conflicts");
                Assert.That(conflicts.Count, Is.EqualTo(1), refused.ToString());
                Assert.That(conflicts[0].Value<string>("by"), Is.EqualTo("user"));
                Assert.That(conflicts[0].Value<string>("target"), Does.StartWith("WcUser"));
                Assert.That(conflicts[0].Value<string>("confidence"), Is.EqualTo("logged"));
                Assert.That(refused.Value<string>("coverage"), Is.EqualTo("full"));
                Assert.That(refused.Value<string>("guard"), Is.Null, "A refused restore takes no guard.");
            }
            Assert.That(X("WcUser"), Is.EqualTo(9f).Within(0.001f), "A refused restore changes nothing.");
        }

        [Test]
        public void UserEdit_KeepLeavesTheUsersProperty()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcKeep"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcKeep")));
                Call("gameobject_set_transform", Args(("name", "WcKeep"), ("posX", 5f)));
                UserEdit("WcKeep", 9f);

                // The user's change is to the position the step also moved: the restore runs and keeps that property, not the target.
                var kept = RestoreOk("wcKeep", "keep");

                Assert.That(kept.Value<int>("merged"), Is.EqualTo(1), kept.ToString());
                Assert.That(kept.Value<int>("skipped"), Is.EqualTo(0), kept.ToString());
                Assert.That(kept.Value<JArray>("skippedItems").ToString(), Does.Contain("(kept: user change k"));
            }
            Assert.That(X("WcKeep"), Is.EqualTo(9f).Within(0.001f), "The user's value stays.");
        }

        [Test]
        public void UserEdit_OverwriteByTheReturnedId_TakesTheCheckpointValue()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcOverwrite"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcOverwrite")));
                Call("gameobject_set_transform", Args(("name", "WcOverwrite"), ("posX", 5f)));
                UserEdit("WcOverwrite", 9f);

                string id = RestoreRaw("wcOverwrite").Value<JArray>("conflicts")[0].Value<string>("id");
                var restored = RestoreOk("wcOverwrite", id);

                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(X("WcOverwrite"), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void OtherAgentsStep_IsAStepConflictOfTheRestore()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcB"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcB")));
                Call("gameobject_set_transform", Args(("name", "WcB"), ("posX", 5f)));
            }
            using (SkillsAuditLog.BeginRequestContext(-1, AgentB, true))
                Call("gameobject_set_transform", Args(("name", "WcB"), ("posX", 7f)));

            JArray conflicts;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var refused = RestoreRaw("wcB");
                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                conflicts = refused.Value<JArray>("conflicts");
            }

            Assert.That(conflicts.Count, Is.EqualTo(1), "The journal entry of the other agent's change is covered by its step.");
            Assert.That(conflicts[0].Value<string>("by"), Is.EqualTo(AgentB));
            Assert.That(conflicts[0].Value<string>("what"), Does.Contain("m_LocalPosition.x"), "The step's change is a property conflict now.");
            Assert.That(conflicts[0].Value<string>("confidence"), Is.Null, "A step conflict is not a logged one.");
            Assert.That(X("WcB"), Is.EqualTo(7f).Within(0.001f));
        }

        [Test]
        public void NoOtherChange_RestoresInOneCallWithFullCoverage()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcFree"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcFree")));
                Call("gameobject_set_transform", Args(("name", "WcFree"), ("posX", 5f)));

                var restored = RestoreOk("wcFree");

                Assert.That(restored.Value<string>("coverage"), Is.EqualTo("full"));
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(X("WcFree"), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void UndoOfAMiddleStep_ConflictsWithALaterStepOfTheSameCaller()
        {
            string middle;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcMid"), ("x", 0f)));
                Call("gameobject_set_transform", Args(("name", "WcMid"), ("posX", 1f)));
                middle = WorkflowManager.History.tasks.Last().DisplayId;
                Call("gameobject_set_transform", Args(("name", "WcMid"), ("posX", 2f)));

                var refused = Call("workflow_undo_task", Args(("taskId", middle)));

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                var conflicts = refused.Value<JArray>("conflicts");
                Assert.That(conflicts.Count, Is.EqualTo(1), refused.ToString());
                Assert.That(conflicts[0].Value<string>("by"), Is.EqualTo(AgentA));
                Assert.That(conflicts[0].Value<string>("what"), Does.Contain("m_LocalPosition.x"), "The step's change is a property conflict now.");
            }
            Assert.That(X("WcMid"), Is.EqualTo(2f).Within(0.001f), "The refused undo changes nothing.");
        }

        [Test]
        public void AssetWrittenByTheTask_NoConflictEvenWithAnEditorImport()
        {
            string matPath = AssetRoot + "/ConflictMat.mat";
            JObject restored;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "wcMat")));
                Call("material_create", Args(("name", "ConflictMat"), ("savePath", matPath)));
                // The import of the file the task wrote is in the journal, but the file still has the fingerprint the task recorded.
                EditorChangeTrackerService.ResetRestWindowForTests();
                AssetDatabase.Refresh();
                EditorChangeTrackerService.FlushPendingProperties();

                restored = RestoreOk("wcMat");
            }
            Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            Assert.That(File.Exists(matPath), Is.False, "The restore removes the material the task created.");
        }

        [Test]
        public void JournalRotatedPastTheCheckpoint_CoverageIsPartial()
        {
            string savedLogPath = EditorChangeTrackerService.OverrideLogPathForTests;
            EditorChangeTrackerService.OverrideLogPathForTests = Path.Combine(_tempRoot, "editor_changes.jsonl");
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
            try
            {
                // Entries before the checkpoint, so its cursor and the step's are not 0 (0 means the journal has no position to read from).
                for (int i = 0; i < 5; i++)
                    EditorChangeTrackerService.PublishForTests("rotation_probe", "editor");
                using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
                {
                    Call("gameobject_create", Args(("name", "WcPart"), ("x", 0f)));
                    Call("workflow_checkpoint", Args(("label", "wcPart")));
                    Call("gameobject_set_transform", Args(("name", "WcPart"), ("posX", 3f)));
                }
                EditorChangeTrackerService.ResetRestWindowForTests();

                // Enough unrelated entries to rotate the checkpoint's cursor out of the ring and out of the kept segments.
                EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 5;
                for (int i = 0; i < 600; i++)
                    EditorChangeTrackerService.PublishForTests("rotation_probe", "editor");

                using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
                {
                    var restored = RestoreOk("wcPart");
                    Assert.That(restored.Value<string>("coverage"), Is.EqualTo("partial"), restored.ToString());
                }
            }
            finally
            {
                EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 0;
                EditorChangeTrackerService.ResetForTests(deleteFile: true);
                EditorChangeTrackerService.OverrideLogPathForTests = savedLogPath;
                EditorChangeTrackerService.ReloadForTests();
            }
        }

        [Test]
        public void UnknownConflictsValue_IsSemanticInvalid()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "wcBad")));
                var response = RestoreRaw("wcBad", "maybe");
                Assert.That(response.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.SemanticInvalid.ToWireString()), response.ToString());
            }
        }

        // Changes the transform the way a person in the Inspector does: outside any REST call, so the journal records it as an editor change.
        [Test]
        public void EditInAWindowTwoCallersShared_IsNotAConflict()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcMixed"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcMixed")));
                Call("gameobject_set_transform", Args(("name", "WcMixed"), ("posX", 5f)));
            }

            try
            {
                // Two callers in one window: the journal cannot tell whose edit it was, so it counts as the caller's own.
                EditorChangeTrackerService.ResetRestWindowForTests();
                using (SkillsAuditLog.BeginRequestContext(-1, AgentB, true))
                    EditorChangeTrackerService.BeginRestExecution();
                using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
                    EditorChangeTrackerService.BeginRestExecution();
                var go = GameObject.Find("WcMixed");
                Undo.RecordObject(go.transform, "Conflict test shared window edit");
                go.transform.position = new Vector3(7f, 0f, 0f);
                Undo.FlushUndoRecordObjects();
                EditorChangeTrackerService.FlushPendingProperties();
            }
            finally
            {
                EditorChangeTrackerService.ResetRestWindowForTests();
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
                RestoreOk("wcMixed");
            Assert.That(X("WcMixed"), Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void ChangeWithNoJournalTrace_IsAConflictByUnknown()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WcTrace"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wcTrace")));
                Call("gameobject_set_transform", Args(("name", "WcTrace"), ("posX", 5f)));
            }

            // A write that bypasses Undo leaves no journal entry; only the state shows it.
            EditorChangeTrackerService.ResetRestWindowForTests();
            GameObject.Find("WcTrace").transform.position = new Vector3(7f, 0f, 0f);

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var refused = RestoreRaw("wcTrace");
                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                Assert.That(refused.Value<JArray>("conflicts")[0].Value<string>("by"), Is.EqualTo("unknown"), refused.ToString());
                RestoreOk("wcTrace", "keep");
            }
            Assert.That(X("WcTrace"), Is.EqualTo(7f).Within(0.001f), "The kept value stays.");
        }

        private static void UserEdit(string name, float x)
        {
            EditorChangeTrackerService.ResetRestWindowForTests();
            var go = GameObject.Find(name);
            Undo.RecordObject(go.transform, "Conflict test user edit");
            go.transform.position = new Vector3(x, 0f, 0f);
            Undo.FlushUndoRecordObjects();
            EditorChangeTrackerService.FlushPendingProperties();
        }

        private static JObject Call(string skill, string json) => JObject.Parse(SkillRouter.Execute(skill, json));

        // The whole response, refusals included.
        private static JObject RestoreRaw(string to, string conflicts = null) =>
            Call("workflow_restore", Args(("to", to), ("conflicts", conflicts)));

        // The success result of a restore, failing the test when the call was refused.
        private static JObject RestoreOk(string to, string conflicts = null)
        {
            var response = RestoreRaw(to, conflicts);
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

        private static float X(string name) => GameObject.Find(name).transform.position.x;
    }
}

// Producer:Betsy
