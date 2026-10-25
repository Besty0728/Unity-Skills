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
    /// A partial restore (scope): the targets it keeps by entity, name, path and asset folder, the refusal of an unmatched scope, the steps
    /// it leaves undone, the references a preview expects to dangle, and the open scene file it does not write.
    /// </summary>
    [TestFixture]
    public class WorkflowScopeTests
    {
        private const string AssetRoot = "Assets/__WorkflowTests__";
        private const string AgentA = "WorkflowScopeAgentA";
        private string _tempRoot;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowScopeTests_" + Guid.NewGuid().ToString("N"));
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
        public void ScopeByName_RestoresOnlyThatObject()
        {
            float originalA;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                string lightA = CreateLight("ScA");
                string lightB = CreateLight("ScB");
                originalA = GameObject.Find("ScA").GetComponent<Light>().color.r;
                Call("workflow_checkpoint", Args(("label", "scNames")));
                Call("light_set_properties", Args(("entityId", lightA), ("r", 0.2f)));
                Call("light_set_properties", Args(("entityId", lightB), ("r", 0.7f)));
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scNames", "ScA");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("ScA").GetComponent<Light>().color.r, Is.EqualTo(originalA).Within(0.001f), "The scoped light goes back.");
            Assert.That(GameObject.Find("ScB").GetComponent<Light>().color.r, Is.EqualTo(0.7f).Within(0.001f), "The other light keeps its change.");
        }

        [Test]
        public void ScopeByEntity_RestoresOnlyThatLight()
        {
            float originalA;
            string lightA, lightB;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                lightA = CreateLight("ScEntityA");
                lightB = CreateLight("ScEntityB");
                originalA = GameObject.Find("ScEntityA").GetComponent<Light>().color.r;
                Call("workflow_checkpoint", Args(("label", "scEntity")));
                Call("light_set_properties", Args(("entityId", lightA), ("r", 0.2f)));
                Call("light_set_properties", Args(("entityId", lightB), ("r", 0.2f)));
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scEntity", "entity:" + lightA);
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("ScEntityA").GetComponent<Light>().color.r, Is.EqualTo(originalA).Within(0.001f));
            Assert.That(GameObject.Find("ScEntityB").GetComponent<Light>().color.r, Is.EqualTo(0.2f).Within(0.001f), "The other light keeps its change.");
        }

        [Test]
        public void ScopeWithoutSlash_LeavesTheChild()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                SetUpParentAndChild();
                Call("workflow_checkpoint", Args(("label", "scTreeName")));
                Call("gameobject_set_transform", Args(("name", "ScParent"), ("posX", 5f)));
                Call("gameobject_set_transform", Args(("name", "ScChild"), ("localPosX", 1f)));
            }
            float childLocalBefore = GameObject.Find("ScChild").transform.localPosition.x;

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scTreeName", "ScParent");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("ScParent").transform.position.x, Is.EqualTo(0f).Within(0.001f), "The parent goes back.");
            Assert.That(GameObject.Find("ScChild").transform.localPosition.x, Is.EqualTo(childLocalBefore).Within(0.001f), "The child keeps its change.");
        }

        [Test]
        public void ScopeWithTrailingSlash_RestoresTheChildToo()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                SetUpParentAndChild();
                Call("workflow_checkpoint", Args(("label", "scTreeSub")));
                Call("gameobject_set_transform", Args(("name", "ScParent"), ("posX", 5f)));
                Call("gameobject_set_transform", Args(("name", "ScChild"), ("localPosX", 1f)));
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scTreeSub", "ScParent/");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(GameObject.Find("ScParent").transform.position.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(GameObject.Find("ScChild").transform.localPosition.x, Is.EqualTo(0f).Within(0.001f), "The child goes back with its parent.");
        }

        [Test]
        public void ScopeByAssetPath_RestoresOnlyThatMaterial()
        {
            string firstPath = AssetRoot + "/ScFolder/ScM1.mat";
            string secondPath = AssetRoot + "/ScFolder/ScM2.mat";
            Color original;
            string restoredJson = null;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                CreateMaterials(firstPath, secondPath);
                original = MaterialColour(firstPath);
                Call("workflow_checkpoint", Args(("label", "scMatFile")));
                Call("material_set_color", Args(("path", firstPath), ("r", 0.2f), ("g", 0.2f), ("b", 0.2f), ("a", 1f)));
                Call("material_set_color", Args(("path", secondPath), ("r", 0.2f), ("g", 0.2f), ("b", 0.2f), ("a", 1f)));
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scMatFile", firstPath);
                restoredJson = restored.ToString();
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restoredJson);
            }
            Assert.That(MaterialColour(firstPath).r, Is.EqualTo(original.r).Within(0.001f), "The scoped material goes back. restore: " + restoredJson);
            Assert.That(MaterialColour(secondPath).r, Is.EqualTo(0.2f).Within(0.001f), "The other material keeps its change.");
        }

        [Test]
        public void ScopeByAssetFolder_RestoresEveryMaterialUnderIt()
        {
            string firstPath = AssetRoot + "/ScFolder/ScM1.mat";
            string secondPath = AssetRoot + "/ScFolder/ScM2.mat";
            Color original;
            string restoredJson = null;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                CreateMaterials(firstPath, secondPath);
                original = MaterialColour(firstPath);
                Call("workflow_checkpoint", Args(("label", "scMatFolder")));
                Call("material_set_color", Args(("path", firstPath), ("r", 0.2f), ("g", 0.2f), ("b", 0.2f), ("a", 1f)));
                Call("material_set_color", Args(("path", secondPath), ("r", 0.2f), ("g", 0.2f), ("b", 0.2f), ("a", 1f)));
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scMatFolder", AssetRoot + "/ScFolder/");
                restoredJson = restored.ToString();
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restoredJson);
            }
            Assert.That(MaterialColour(firstPath).r, Is.EqualTo(original.r).Within(0.001f), "First material. restore: " + restoredJson);
            Assert.That(MaterialColour(secondPath).r, Is.EqualTo(original.r).Within(0.001f), "Second material. restore: " + restoredJson);
        }

        [Test]
        public void UnmatchedScope_RefusesWithoutChangingAnything()
        {
            int checkpointsBefore;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                string lightId = CreateLight("ScRefused");
                Call("workflow_checkpoint", Args(("label", "scRefused")));
                Call("light_set_properties", Args(("entityId", lightId), ("r", 0.2f)));
                checkpointsBefore = WorkflowManager.History.checkpoints.Count;

                var refused = ScopedRestore("scRefused", "NoSuchObject");

                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.SemanticInvalid.ToWireString()), refused.ToString());
                Assert.That(refused.Value<string>("error"), Does.StartWith("scope "), refused.ToString());
                Assert.That(WorkflowManager.History.checkpoints.Count, Is.EqualTo(checkpointsBefore), "A refused scope takes no guard checkpoint.");
            }
            Assert.That(GameObject.Find("ScRefused").GetComponent<Light>().color.r, Is.EqualTo(0.2f).Within(0.001f), "A refused restore changes nothing.");
        }

        [Test]
        public void ScopedRestore_DoesNotMarkItsStepsUndone_AndKeepsTheScopeOnReload()
        {
            long stepSeq;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                string lightA = CreateLight("ScRecord");
                Call("workflow_checkpoint", Args(("label", "scRecord")));
                Call("light_set_properties", Args(("entityId", lightA), ("r", 0.2f)));
                stepSeq = WorkflowManager.History.tasks.Last().seq;
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var restored = ScopedRestoreOk("scRecord", "ScRecord");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
            }
            Assert.That(WorkflowManager.UndoneSteps(WorkflowManager.History.restores).Contains(stepSeq), Is.False, "A scoped restore undoes no step.");

            WorkflowManager.ResetStateForTests();
            var record = WorkflowManager.History.restores.Last();
            Assert.That(record.scope, Is.EqualTo("ScRecord"), "The scope survives a reload.");
            Assert.That(WorkflowManager.UndoneSteps(WorkflowManager.History.restores).Contains(stepSeq), Is.False);
        }

        [Test]
        public void DanglingReferences_ListTheHolderOfADeletedObject()
        {
            long doomedStep;
            JObject dry;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("gameobject_create", Args(("name", "ScHolder")));
                Call("gameobject_create", Args(("name", "ScDoomed")));
                WorkflowManager.EndTask();
                doomedStep = WorkflowManager.History.tasks.Last().seq;
                Call("component_add", Args(("name", "ScDoomed"), ("componentType", "Rigidbody")));
                AddJointReferencing("ScHolder", "ScDoomed");

                // The preview is the caller's: it lists the steps AgentA recorded.
                dry = DryRunJson("workflow_restore", Args(("to", "s" + doomedStep), ("scope", "ScDoomed")));
            }

            var dangling = dry["changes"]["danglingReferences"] as JArray;
            Assert.That(dangling, Is.Not.Null, dry.ToString());
            Assert.That(dangling.Count, Is.EqualTo(1), dry.ToString());
            Assert.That(dangling[0].ToString(), Does.Contain("m_ConnectedBody"), dry.ToString());
            Assert.That(dangling[0].ToString(), Does.Contain("ScDoomed"), dry.ToString());
        }

        [Test]
        public void OpenSceneFile_UndoMarksTheSceneDirtyAndLeavesTheFileAlone()
        {
            string sceneFullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), AssetRoot + "/MergeScene.unity");
            string saveStep;
            byte[] savedBytes;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                CreateLight("ScOpenScene");
                Call("scene_save", Args());
                saveStep = LastStepId();
                savedBytes = File.ReadAllBytes(sceneFullPath);
            }

            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                var dry = DryRunJson("workflow_undo_task", Args(("taskId", saveStep)));
                var items = dry["changes"]["items"].Select(item => item.ToString()).ToList();
                Assert.That(items.Any(item => item.Contains("not written")), Is.True, dry.ToString());

                var undone = UndoOk(saveStep);
                Assert.That(undone.Value<JArray>("skippedItems").ToString(), Does.Contain("scene is open"), undone.ToString());
            }
            Assert.That(File.ReadAllBytes(sceneFullPath), Is.EqualTo(savedBytes), "The open scene's file is not written by undo.");
            Assert.That(EditorSceneManager.GetActiveScene().isDirty, Is.True, "The scene is marked dirty, so saving applies the undo.");
        }

        [Test]
        public void ClosedSceneFile_UndoRestoresTheFile()
        {
            string sceneFullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), AssetRoot + "/MergeScene.unity");
            byte[] beforeSave = File.ReadAllBytes(sceneFullPath);
            string saveStep;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                CreateLight("ScClosed");
                Call("scene_save", Args());
                saveStep = LastStepId();
            }
            Assert.That(File.ReadAllBytes(sceneFullPath), Is.Not.EqualTo(beforeSave), "The save must change the file.");

            // Close MergeScene, so the undo writes its file rather than marking it dirty.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            JObject undone;
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                undone = UndoOk(saveStep);
            }

            Assert.That(File.ReadAllBytes(sceneFullPath), Is.EqualTo(beforeSave), "Undo restores the closed scene's file. undo: " + undone);
            Assert.That(undone.Value<JArray>("skippedItems")?.ToString() ?? string.Empty, Does.Not.Contain("scene is open"), undone.ToString());
        }

        [Test]
        public void ConsoleSettingScope_RestoresTheSettingByPrefix()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, AgentA, true))
            {
                Call("workflow_checkpoint", Args(("label", "scConsole")));
                var set = Call("console_set_collapse", Args(("enabled", true)));
                Assert.That(set.Value<string>("error"), Is.Null, set.ToString());

                var refused = ScopedRestore("scConsole", "console.pauseOnError");
                Assert.That(refused.Value<string>("errorCode"), Is.EqualTo(SkillErrorCode.SemanticInvalid.ToWireString()), refused.ToString());

                var restored = ScopedRestoreOk("scConsole", "console.");
                Assert.That(restored.Value<int>("failed"), Is.EqualTo(0), restored.ToString());
                Assert.That(restored.Value<int>("restored"), Is.EqualTo(1), restored.ToString());
            }
        }

        // A parent GameObject with a child under it, both created before the checkpoint the tests restore to.
        private static void SetUpParentAndChild()
        {
            Call("gameobject_create", Args(("name", "ScParent")));
            Call("gameobject_create", Args(("name", "ScChild")));
            Call("gameobject_set_parent", Args(("childName", "ScChild"), ("parentName", "ScParent")));
        }

        // Two materials in the scope folder, created before the checkpoint.
        private static void CreateMaterials(string firstPath, string secondPath)
        {
            if (!AssetDatabase.IsValidFolder(AssetRoot + "/ScFolder")) AssetDatabase.CreateFolder(AssetRoot, "ScFolder");
            Call("material_create", Args(("name", "ScM1"), ("savePath", firstPath)));
            WorkflowManager.EndTask();
            Call("material_create", Args(("name", "ScM2"), ("savePath", secondPath)));
            WorkflowManager.EndTask();
        }

        // Adds a FixedJoint to the holder whose connectedBody is the doomed object's Rigidbody, as a tracked change of its own step.
        private static void AddJointReferencing(string holderName, string doomedName)
        {
            WorkflowManager.BeginTask("sc-joint", "scope test");
            var joint = GameObject.Find(holderName).AddComponent<FixedJoint>();
            WorkflowManager.SnapshotCreatedComponent(joint);
            WorkflowManager.SnapshotObject(joint);
            var serialized = new SerializedObject(joint);
            serialized.FindProperty("m_ConnectedBody").objectReferenceValue = GameObject.Find(doomedName).GetComponent<Rigidbody>();
            serialized.ApplyModifiedProperties();
            WorkflowManager.EndTask();
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

        // The whole response of a scoped restore, refusals included.
        private static JObject ScopedRestore(string to, string scope) =>
            Call("workflow_restore", Args(("to", to), ("scope", scope)));

        // The success result of a scoped restore, failing the test when the call was refused.
        private static JObject ScopedRestoreOk(string to, string scope)
        {
            var response = ScopedRestore(to, scope);
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
