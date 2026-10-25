﻿using System;
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
    /// The property-level merge of a restore (design section 6.4): a change to another property, or one the user made that the
    /// step did not touch, restores without a conflict; the same property is a leaf conflict; a net-zero change, a material
    /// Unity re-saved, and an asset changed on disk are judged by their state.
    /// </summary>
    [TestFixture]
    public class WorkflowMergeTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowMergeAgentA";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowMergeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.CreateFolder("Assets", "__WorkflowTests__");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/MergeScene.unity"), Is.True);
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
        public void StepUndoneBeforeALaterIntensityChange_MergesTheColourBack()
        {
            Color original;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                string lightId = CreateLight("WmLight");
                original = GameObject.Find("WmLight").GetComponent<Light>().color;
                var coloured = Call("light_set_properties", Args(("entityId", lightId), ("r", 0.2f), ("g", 0.4f), ("b", 0.6f)));
                Assert.That(coloured.Value<string>("error"), Is.Null, coloured.ToString());
                string colourStep = LastStepId();
                Call("light_set_properties", Args(("entityId", lightId), ("intensity", 3f)));

                var undone = UndoOk(colourStep);

                Assert.That(undone.Value<int>("merged"), Is.EqualTo(1), undone.ToString());
                Assert.That(undone.Value<int>("failed"), Is.EqualTo(0), undone.ToString());
            }
            var light = GameObject.Find("WmLight").GetComponent<Light>();
            AssertColour(light.color, original);
            Assert.That(light.intensity, Is.EqualTo(3f).Within(0.001f), "The later step's intensity stays.");
        }

        [Test]
        public void UserEditOfAnotherProperty_IsNotAConflict()
        {
            Color original;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                string lightId = CreateLight("WmOther");
                original = GameObject.Find("WmOther").GetComponent<Light>().color;
                Call("workflow_checkpoint", Args(("label", "wmOther")));
                Call("light_set_properties", Args(("entityId", lightId), ("r", 0.2f), ("g", 0.4f), ("b", 0.6f)));
            }
            UserEditLight("WmOther", intensity: 5f);

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = RestoreOk("wmOther");
                Assert.That(restored.Value<int>("merged"), Is.EqualTo(1), restored.ToString());
            }
            var light = GameObject.Find("WmOther").GetComponent<Light>();
            AssertColour(light.color, original);
            Assert.That(light.intensity, Is.EqualTo(5f).Within(0.001f), "The user's intensity stays.");
        }

        [Test]
        public void UserEditOfTheSameProperty_IsALeafConflict()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                SetUpRedChannelStep("WmSame", "wmSame");
                UserEditLightColourR("WmSame", 0.6f);

                var refused = RestoreRaw("wmSame");

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                var conflicts = refused.Value<JArray>("conflicts");
                Assert.That(conflicts.Count, Is.EqualTo(1), refused.ToString());
                Assert.That(conflicts[0].Value<string>("target"), Does.Contain("m_Color"), refused.ToString());
            }
            Assert.That(GameObject.Find("WmSame").GetComponent<Light>().color.r, Is.EqualTo(0.6f).Within(0.001f), "A refused restore changes nothing.");
        }

        [Test]
        public void UserEditOfTheSameProperty_KeepLeavesTheUsersValue()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                SetUpRedChannelStep("WmKeep", "wmKeep");
                UserEditLightColourR("WmKeep", 0.6f);

                var kept = RestoreOk("wmKeep", "keep");

                Assert.That(kept.Value<JArray>("skippedItems").ToString(), Does.Contain("m_Color.r (kept: "), kept.ToString());
            }
            Assert.That(GameObject.Find("WmKeep").GetComponent<Light>().color.r, Is.EqualTo(0.6f).Within(0.001f), "The user's value stays.");
        }

        [Test]
        public void UserEditOfTheSameProperty_OverwriteByLeafId_TakesTheStepsBeforeValue()
        {
            float originalR;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                originalR = SetUpRedChannelStep("WmOver", "wmOver");
                UserEditLightColourR("WmOver", 0.6f);
                string id = RestoreRaw("wmOver").Value<JArray>("conflicts")[0].Value<string>("id");

                var restored = RestoreOk("wmOver", id);

                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("WmOver").GetComponent<Light>().color.r, Is.EqualTo(originalR).Within(0.001f));
        }

        [Test]
        public void NetZeroChange_RestoresWithoutAConflict()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WmMove"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wmMove")));
                Call("gameobject_set_transform", Args(("name", "WmMove"), ("posX", 5f)));
            }
            UserEditPosition("WmMove", 9f);
            UserEditPosition("WmMove", 5f);

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = RestoreOk("wmMove");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("WmMove").transform.position.x, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void MaterialSavedByUnity_RestoresWithoutAConflict()
        {
            string matPath = AssetRoot + "/WmMat.mat";
            Color original;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("material_create", Args(("name", "WmMat"), ("savePath", matPath)));
                original = MaterialColour(matPath);
                Call("workflow_checkpoint", Args(("label", "wmMat")));
                Call("material_set_color", Args(("path", matPath), ("r", 0.2f), ("g", 0.2f), ("b", 0.2f), ("a", 1f)));
            }
            // Unity re-saves the material outside any REST call, which changes the file's fingerprint but not its state.
            AssetDatabase.SaveAssets();
            EditorChangeTrackerService.FlushPendingProperties();

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = RestoreOk("wmMat");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            AssertColour(MaterialColour(matPath), original);
        }

        [Test]
        public void FileOnlyAssetChangedOnDisk_IsAConflict()
        {
            string textPath = AssetRoot + "/WmNote.txt";
            string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), textPath);
            File.WriteAllText(fullPath, "first");
            AssetDatabase.ImportAsset(textPath);

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "wmNote")));
                WorkflowManager.BeginTask("wm-note", "merge test");
                WorkflowManager.SnapshotObject(AssetDatabase.LoadAssetAtPath<TextAsset>(textPath));
                WorkflowManager.EndTask();
            }
            // Written directly, with no import: the editor never sees the change, so only the file's bytes show it.
            File.WriteAllText(fullPath, "second, written outside Unity");

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var refused = RestoreRaw("wmNote");

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                var conflicts = refused.Value<JArray>("conflicts");
                Assert.That(conflicts.Count, Is.EqualTo(1), refused.ToString());
                Assert.That(conflicts[0].Value<string>("what"), Is.EqualTo("file changed on disk"), refused.ToString());
            }
        }

        [Test]
        public void StepWithoutAfterState_FallsBackToTheTargetLevelConflict()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "WmOld"), ("x", 0f)));
                Call("workflow_checkpoint", Args(("label", "wmOld")));
                Call("gameobject_set_transform", Args(("name", "WmOld"), ("posX", 5f)));
                // A step recorded before after-states existed.
                foreach (var snapshot in WorkflowManager.History.tasks.Last().snapshots)
                    snapshot.afterStateHash = null;
            }
            UserEditPosition("WmOld", 9f);

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var refused = RestoreRaw("wmOld");

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.WorkflowConflict.ToWireString()), refused.ToString());
                var conflicts = refused.Value<JArray>("conflicts");
                Assert.That(conflicts.Count, Is.EqualTo(1), refused.ToString());
                Assert.That(conflicts[0].Value<string>("by"), Is.EqualTo("user"));
                Assert.That(conflicts[0].Value<string>("target"), Does.Not.Contain("m_"), "A target-level conflict names the object, not a property.");
            }
        }

        [Test]
        public void DryRun_CountsTheMergeAndListsTheSameLeafConflict()
        {
            string refusedId;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                SetUpRedChannelStep("WmPreview", "wmPreview");
                UserEditLightColourR("WmPreview", 0.6f);
                refusedId = RestoreRaw("wmPreview").Value<JArray>("conflicts")[0].Value<string>("id");

                var dry = DryRunJson("workflow_restore", Args(("to", "wmPreview")));

                Assert.That(dry.Value<bool>("valid"), Is.True, dry.ToString());
                Assert.That(dry["changes"]["plan"].Value<int>("merge"), Is.EqualTo(1), dry.ToString());
                var conflicts = dry["changes"]["conflicts"] as JArray;
                Assert.That(conflicts.Count, Is.EqualTo(1), dry.ToString());
                Assert.That(conflicts[0].Value<string>("id"), Is.EqualTo(refusedId), dry.ToString());
                var items = dry["changes"]["items"].Select(item => item.ToString()).ToList();
                Assert.That(items.Any(item => item.EndsWith(" merge (1 kept)", StringComparison.Ordinal)), Is.True, dry.ToString());
            }
            Assert.That(GameObject.Find("WmPreview").GetComponent<Light>().color.r, Is.EqualTo(0.6f).Within(0.001f), "A preview changes nothing.");
        }

        // The creation is a step of its own: a light changed while its creation is still an open task would be part of that task.
        // Returns the entity id the light is addressed by afterwards (a fresh light is not always found by its name).
        private static string CreateLight(string name)
        {
            var created = Call("light_create", Args(("name", name), ("lightType", "Point")));
            Assert.That(created.Value<string>("error"), Is.Null, created.ToString());
            WorkflowManager.EndTask();
            return created.Value<JObject>("result").Value<string>("entityId");
        }

        // A light whose red channel a step sets to 0.2 after a checkpoint. Returns the red the checkpoint holds.
        private static float SetUpRedChannelStep(string name, string label)
        {
            string lightId = CreateLight(name);
            float originalR = GameObject.Find(name).GetComponent<Light>().color.r;
            Call("workflow_checkpoint", Args(("label", label)));
            var coloured = Call("light_set_properties", Args(("entityId", lightId), ("r", 0.2f)));
            Assert.That(coloured.Value<string>("error"), Is.Null, coloured.ToString());
            return originalR;
        }

        // Changes the light the way a person in the Inspector does: outside any REST call, so the journal records it as an editor change.
        private static void UserEditLight(string name, float intensity)
        {
            EditorChangeTrackerService.ResetRestWindowForTests();
            var light = GameObject.Find(name).GetComponent<Light>();
            Undo.RecordObject(light, "Merge test user edit");
            light.intensity = intensity;
            Undo.FlushUndoRecordObjects();
            EditorChangeTrackerService.FlushPendingProperties();
        }

        private static void UserEditLightColourR(string name, float r)
        {
            EditorChangeTrackerService.ResetRestWindowForTests();
            var light = GameObject.Find(name).GetComponent<Light>();
            Undo.RecordObject(light, "Merge test user edit");
            light.color = new Color(r, light.color.g, light.color.b, light.color.a);
            Undo.FlushUndoRecordObjects();
            EditorChangeTrackerService.FlushPendingProperties();
        }

        private static void UserEditPosition(string name, float x)
        {
            EditorChangeTrackerService.ResetRestWindowForTests();
            var go = GameObject.Find(name);
            Undo.RecordObject(go.transform, "Merge test user edit");
            go.transform.position = new Vector3(x, 0f, 0f);
            Undo.FlushUndoRecordObjects();
            EditorChangeTrackerService.FlushPendingProperties();
        }

        private static string LastStepId() => WorkflowManager.History.tasks.Last().DisplayId;

        private static Color MaterialColour(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            return material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : material.color;
        }

        private static void AssertColour(Color actual, Color expected)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.001f), "red");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.001f), "green");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.001f), "blue");
        }

        private static JObject Call(string skill, string json) => JObject.Parse(SkillRouter.Execute(skill, json));

        private static JObject DryRunJson(string skill, string json) => JObject.Parse(SkillRouter.DryRun(skill, json));

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

        // The success result of undoing one step, failing the test when the call was refused.
        private static JObject UndoOk(string taskId)
        {
            var response = Call("workflow_undo_task", Args(("taskId", taskId)));
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
    }
}

// Producer:Betsy
