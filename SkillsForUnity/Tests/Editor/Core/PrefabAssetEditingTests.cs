using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnitySkills.Internal;
using UnitySkills.Tests.Fixtures;
using Object = UnityEngine.Object;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class PrefabAssetEditingTests
    {
        private const string Folder = "Assets/__UnitySkillsPrefabAssetProbe__";
        private string _path;
        private SkillsOperatingMode _mode;
        private SurfaceProfileKind _profile;

        [SetUp]
        public void SetUp()
        {
            _mode = SkillsModeManager.CurrentMode; _profile = SkillsSurfaceProfile.Current;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass; SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            AssetDatabase.CreateFolder("Assets", "__UnitySkillsPrefabAssetProbe__");
            _path = Create("Root");
            Undo.ClearAll();
        }

        [TearDown]
        public void TearDown()
        {
            PrefabAssetEditor.SaveOverrideForTests = null;
            if (WorkflowManager.IsRecording) WorkflowManager.AbortTask();
            Undo.ClearAll();
            AssetDatabase.DeleteAsset(Folder);
            SkillsModeManager.CurrentMode = _mode; SkillsSurfaceProfile.Current = _profile;
        }

        private static JObject Json(object result) => result is string text ? JObject.Parse(text) : JObject.FromObject(result, JsonSerializer.Create(SkillsCommon.JsonSettings));
        private static JObject Success(object result)
        {
            var json = Json(result);
            Assert.That(json["error"] == null || json["error"].Type == JTokenType.Null, Is.True, json.ToString());
            Assert.That(json.Value<bool>("success"), Is.True, json.ToString());
            return json;
        }
        private static JObject Failure(object result)
        {
            var json = Json(result); Assert.That(json["error"]?.ToString(), Is.Not.Null.And.Not.Empty, json.ToString()); return json;
        }
        private static GameObject Asset(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path);
        private string Create(string name, Action<GameObject> setup = null)
        {
            var go = new GameObject(name);
            try
            {
                var child = new GameObject("Child"); child.transform.SetParent(go.transform, false);
                child.AddComponent<BoxCollider>();
                setup?.Invoke(go);
                var path = Folder + "/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                return path;
            }
            finally { Object.DestroyImmediate(go); }
        }
        private static JObject Step(string skill, object args) => new JObject { ["skill"] = skill, ["args"] = JObject.FromObject(args) };
        private object Edit(params JObject[] steps) => PrefabSkills.PrefabEditBatch(_path, new JArray(steps).ToString());

        [Test]
        public void Queries_ReportInactiveNodesComponentsAndNestedProperties()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, typeof(PrefabAssetProbe).FullName, "Child"));
            Success(PrefabSkills.PrefabSetActive(_path, false, "Child"));
            var hierarchy = Success(PrefabSkills.PrefabGetHierarchy(_path, 1));
            Assert.That(hierarchy.Value<int>("total"), Is.EqualTo(2)); Assert.That(hierarchy.Value<bool>("truncated"), Is.True);
            var info = Success(PrefabSkills.PrefabGetInfo(_path, "Child")); Assert.That(info.Value<bool>("activeSelf"), Is.False);
            var components = Success(PrefabSkills.PrefabListComponents(_path, "Child", true));
            Assert.That(((JArray)components["components"]).Count, Is.EqualTo(3));
            var properties = Success(PrefabSkills.PrefabGetSerializedProperties(_path, typeof(PrefabAssetProbe).FullName, "Child"));
            Assert.That(properties["properties"].Any(p => p.Value<string>("propertyPath") == "settings.speed"), Is.True);
        }

        [Test]
        public void AddThenConfigure_UsesNewChildAndReadsFinalRenamedPath()
        {
            var result = Success(Edit(
                Step("prefab_create_gameobject", new { name = "Body" }),
                Step("prefab_add_component", new { gameObjectPath = "Body", componentType = "BoxCollider" }),
                Step("prefab_set_serialized_property", new { gameObjectPath = "Body", componentType = "BoxCollider", propertyPath = "m_IsTrigger", value = "true" }),
                Step("prefab_rename_gameobject", new { gameObjectPath = "Body", newName = "Final" })));
            Assert.That(result["results"][0].Value<string>("gameObjectPath"), Is.EqualTo("Final"));
            Assert.That(Asset(_path).transform.Find("Final").GetComponent<BoxCollider>().isTrigger, Is.True);
        }

        [Test]
        public void ComponentIndex_TargetsSecondMatchingComponent()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, "BoxCollider", "Child"));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, "BoxCollider", "m_IsTrigger", "Child", 1, "true"));
            var colliders = Asset(_path).transform.Find("Child").GetComponents<BoxCollider>();
            Assert.That(colliders[0].isTrigger, Is.False); Assert.That(colliders[1].isTrigger, Is.True);
            Success(PrefabSkills.PrefabRemoveComponent(_path, "BoxCollider", "Child", 0));
            Assert.That(Asset(_path).transform.Find("Child").GetComponent<BoxCollider>().isTrigger, Is.True);
        }

        [Test]
        public void ComponentDependency_IsAddedAndCannotBeRemovedWhileRequired()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, typeof(RequiresLeafProbe).FullName));
            Assert.That(Asset(_path).GetComponent<RequiredLeafProbe>(), Is.Not.Null);
            Failure(PrefabSkills.PrefabRemoveComponent(_path, typeof(RequiredLeafProbe).FullName));
            Success(PrefabSkills.PrefabRemoveComponent(_path, typeof(RequiresLeafProbe).FullName));
            Success(PrefabSkills.PrefabRemoveComponent(_path, typeof(RequiredLeafProbe).FullName));
        }

        [Test]
        public void Enabled_ReadsSavedValue()
        {
            var result = Success(PrefabSkills.PrefabSetComponentEnabled(_path, "BoxCollider", false, "Child"));
            Assert.That(result.Value<string>("valueSet"), Is.EqualTo("False"));
            Assert.That(Asset(_path).transform.Find("Child").GetComponent<BoxCollider>().enabled, Is.False);
        }

        [Test]
        public void SerializedNestedArrayAndEmptyString_ArePersisted()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, typeof(PrefabAssetProbe).FullName));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "settings.speed", value: "5.5"));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "values.Array.size", value: "3"));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "values.Array.data[2]", value: "9"));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "label", value: ""));
            var probe = Asset(_path).GetComponent<PrefabAssetProbe>();
            Assert.That(probe.settings.speed, Is.EqualTo(5.5f)); Assert.That(probe.values, Is.EqualTo(new[] { 1, 2, 9 })); Assert.That(probe.label, Is.Empty);
        }

        [Test]
        public void InternalReferences_SurviveSaveAndCanBeCleared()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, nameof(PrefabAssetProbe)));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "target", referenceGameObjectPath: "Child"));
            var componentResult = Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "component",
                referenceGameObjectPath: "Child", referenceComponentType: "BoxCollider"));
            var asset = Asset(_path); var probe = asset.GetComponent<PrefabAssetProbe>();
            Assert.That(probe.target, Is.EqualTo(asset.transform.Find("Child").gameObject));
            Assert.That(probe.component, Is.EqualTo(asset.transform.Find("Child").GetComponent<BoxCollider>()));
            Assert.That(componentResult.Value<string>("valueSet"), Does.StartWith("Child:UnityEngine.BoxCollider"));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "target", clearReference: true));
            Assert.That(Asset(_path).GetComponent<PrefabAssetProbe>().target, Is.Null);
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "target", referenceGameObjectPath: ""));
            Assert.That(Asset(_path).GetComponent<PrefabAssetProbe>().target, Is.EqualTo(Asset(_path)));
        }

        [Test]
        public void AssetReference_SurvivesSaveAndWrongTypeIsRejected()
        {
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var materialPath = Folder + "/Probe.mat"; AssetDatabase.CreateAsset(material, materialPath);
            Success(PrefabSkills.PrefabAddComponent(_path, nameof(PrefabAssetProbe)));
            Success(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "material", assetPath: materialPath));
            Assert.That(Asset(_path).GetComponent<PrefabAssetProbe>().material, Is.EqualTo(material));
            var before = File.ReadAllBytes(_path);
            Failure(PrefabSkills.PrefabSetSerializedProperty(_path, nameof(PrefabAssetProbe), "target", assetPath: materialPath));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void SceneReferenceArguments_AreRejectedWithoutSaving()
        {
            var before = File.ReadAllBytes(_path);
            Failure(Edit(Step("prefab_set_serialized_property", new { componentType = "Transform", propertyPath = "m_Father", referenceName = "SceneObject" })));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void NodeEdits_KeepLocalAxesAndSupportReparentDuplicateDelete()
        {
            Success(PrefabSkills.PrefabSetTransform(_path, "Child", posX: 3, posY: 4));
            Success(PrefabSkills.PrefabSetTransform(_path, "Child", posZ: 5));
            Success(PrefabSkills.PrefabCreateGameObject(_path, "Parent"));
            Success(PrefabSkills.PrefabSetParent(_path, "Child", "Parent"));
            Assert.That(Asset(_path).transform.Find("Parent/Child").localPosition, Is.EqualTo(new Vector3(3, 4, 5)));
            Success(PrefabSkills.PrefabDuplicateGameObject(_path, "Parent/Child", "Copy"));
            Success(PrefabSkills.PrefabSetSiblingIndex(_path, 0, "Parent/Copy"));
            Assert.That(Asset(_path).transform.Find("Parent/Copy").GetSiblingIndex(), Is.Zero);
            Success(PrefabSkills.PrefabDeleteGameObject(_path, "Parent/Child"));
            Assert.That(Asset(_path).transform.Find("Parent/Child"), Is.Null);
        }

        [Test]
        public void RectTransform_LayoutPersistsAndNonRectFieldsFail()
        {
            Success(PrefabSkills.PrefabCreateGameObject(_path, "UI", rectTransform: true));
            Success(PrefabSkills.PrefabSetTransform(_path, "UI", anchorMinX: 0.2f, pivotY: 0.8f, sizeDeltaX: 123, anchoredPosY: 17));
            var rect = (RectTransform)Asset(_path).transform.Find("UI");
            Assert.That(rect.anchorMin.x, Is.EqualTo(0.2f)); Assert.That(rect.pivot.y, Is.EqualTo(0.8f));
            Assert.That(rect.sizeDelta.x, Is.EqualTo(123)); Assert.That(rect.anchoredPosition.y, Is.EqualTo(17));
            Failure(PrefabSkills.PrefabSetTransform(_path, "Child", pivotX: 0));
        }

        [Test]
        public void RootStructuralChangesAndCycles_AreRejected()
        {
            var before = File.ReadAllBytes(_path);
            Failure(PrefabSkills.PrefabDeleteGameObject(_path, ""));
            Failure(PrefabSkills.PrefabSetParent(_path, "", "Child"));
            Failure(PrefabSkills.PrefabRemoveComponent(_path, "Transform"));
            Failure(PrefabSkills.PrefabSetParent(_path, "Child", "Child"));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void DuplicateSiblingPaths_ReturnCandidatesAndWriteNothing()
        {
            var path = Create("Ambiguous", root => { var child = new GameObject("Child"); child.transform.SetParent(root.transform, false); });
            var before = File.ReadAllBytes(path);
            var result = Failure(PrefabSkills.PrefabAddComponent(path, "Rigidbody", "Child"));
            Assert.That(((JArray)result["details"]["candidates"]).Count, Is.EqualTo(2));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        [Test]
        public void FailedStep_DiscardsWholeAssetAndUnloadsPreviewScene()
        {
            var before = File.ReadAllBytes(_path); var count = EditorSceneManager.previewSceneCount;
            var failure = Failure(Edit(Step("prefab_create_gameobject", new { name = "New" }), Step("prefab_add_component", new { componentType = "MissingType" })));
            Assert.That(failure.Value<bool>("rolledBack"), Is.True);
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before)); Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(count));
        }

        [Test]
        public void CrossAssetBatch_PreservesSuccessfulAssetAndGroupsSameAssetAtomically()
        {
            var other = Create("Other");
            var before = File.ReadAllBytes(_path);
            var items = new JArray(
                new JObject { ["prefabPath"] = _path, ["componentType"] = "Rigidbody" },
                new JObject { ["prefabPath"] = other, ["componentType"] = "Rigidbody" },
                new JObject { ["prefabPath"] = _path, ["componentType"] = "MissingType" });
            var result = Json(PrefabSkills.PrefabAddComponentBatch(items.ToString()));
            Assert.That(result.Value<int>("successCount"), Is.EqualTo(1)); Assert.That(result.Value<int>("failCount"), Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before)); Assert.That(Asset(other).GetComponent<Rigidbody>(), Is.Not.Null);
        }

        [Test]
        public void CrossAssetBatch_ThroughRouterKeepsSuccessfulWorkflowAndUndo()
        {
            var other = Create("RouterOther");
            var result = JObject.Parse(SkillRouter.Execute("prefab_add_component_batch", JsonConvert.SerializeObject(new { items = new JArray(
                new JObject { ["prefabPath"] = _path, ["componentType"] = "Rigidbody" },
                new JObject { ["prefabPath"] = other, ["componentType"] = "MissingType" }).ToString() })));
            Assert.That(result.Value<string>("status"), Is.EqualTo("success"), result.ToString());
            Assert.That(Asset(_path).GetComponent<Rigidbody>(), Is.Not.Null);
            Undo.PerformUndo(); Assert.That(Asset(_path).GetComponent<Rigidbody>(), Is.Null);
            Undo.PerformRedo(); Assert.That(Asset(_path).GetComponent<Rigidbody>(), Is.Not.Null);
        }

        [Test]
        public void NativeUndoRedo_RestoresWholeAssetAndMeta()
        {
            var before = File.ReadAllBytes(_path); var meta = File.ReadAllBytes(_path + ".meta");
            Success(PrefabSkills.PrefabAddComponent(_path, "Rigidbody"));
            var after = File.ReadAllBytes(_path);
            Undo.IncrementCurrentGroup(); Undo.PerformUndo();
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
            Undo.PerformRedo(); Assert.That(File.ReadAllBytes(_path), Is.EqualTo(after));
            Assert.That(File.ReadAllBytes(_path + ".meta"), Is.EqualTo(meta));
        }

        [Test]
        public void NativeUndoRedo_MultipleEditsInOneGroupUseCorrectBoundary()
        {
            var before = File.ReadAllBytes(_path);
            Success(PrefabSkills.PrefabAddComponent(_path, "Rigidbody"));
            Success(PrefabSkills.PrefabSetActive(_path, false, "Child"));
            var after = File.ReadAllBytes(_path);
            Undo.IncrementCurrentGroup(); Undo.PerformUndo(); Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
            Undo.PerformRedo(); Assert.That(File.ReadAllBytes(_path), Is.EqualTo(after));
        }

        [Test]
        public void WorkflowRollbackRedo_RestoresStructureAndInternalReferences()
        {
            var task = WorkflowManager.BeginTask("prefab-probe", "Prefab asset editing test");
            Success(Edit(Step("prefab_add_component", new { componentType = nameof(PrefabAssetProbe) }),
                Step("prefab_set_serialized_property", new { componentType = nameof(PrefabAssetProbe), propertyPath = "target", referenceGameObjectPath = "Child" })));
            WorkflowManager.EndTask();
            Assert.That(WorkflowManager.UndoTask(task.id).success, Is.True);
            Assert.That(Asset(_path).GetComponent<PrefabAssetProbe>(), Is.Null);
            Assert.That(WorkflowManager.RedoTask(task.id).success, Is.True);
            var root = Asset(_path); Assert.That(root.GetComponent<PrefabAssetProbe>().target, Is.EqualTo(root.transform.Find("Child").gameObject));
        }

        [Test]
        public void SaveFailure_RestoresBytesAndDoesNotLeaveWorkflowSnapshots()
        {
            var before = File.ReadAllBytes(_path); WorkflowManager.BeginTask("save-failure", "Save failure test");
            PrefabAssetEditor.SaveOverrideForTests = (root, path) => { File.WriteAllText(path, "partial save"); return false; };
            Failure(PrefabSkills.PrefabAddComponent(_path, "Rigidbody"));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before)); Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty);
        }

        [Test]
        public void VariantEdits_PreserveBaseAndInheritance()
        {
            var variant = Folder + "/Variant.prefab";
            Success(PrefabSkills.PrefabCreateVariant(_path, variant)); var before = File.ReadAllBytes(_path);
            Success(PrefabSkills.PrefabAddComponent(variant, "Rigidbody"));
            Success(PrefabSkills.PrefabSetActive(variant, false, "Child"));
            Assert.That(PrefabUtility.GetPrefabAssetType(Asset(variant)), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(Asset(variant).GetComponent<Rigidbody>(), Is.Not.Null);
            Assert.That(Asset(variant).transform.Find("Child").gameObject.activeSelf, Is.False);
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
            Failure(PrefabSkills.PrefabDeleteGameObject(variant, "Child"));
        }

        [Test]
        public void NestedEdits_PreserveSourceAndInstanceConnection()
        {
            var source = Create("NestedSource");
            var outer = Create("Outer", root => { var nested = (GameObject)PrefabUtility.InstantiatePrefab(Asset(source)); nested.transform.SetParent(root.transform, false); });
            var before = File.ReadAllBytes(source);
            Success(PrefabSkills.PrefabAddComponent(outer, "Rigidbody", "NestedSource/Child"));
            Assert.That(Asset(outer).transform.Find("NestedSource/Child").GetComponent<Rigidbody>(), Is.Not.Null);
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(Asset(outer).transform.Find("NestedSource").gameObject), Is.EqualTo(Asset(source)));
            Assert.That(File.ReadAllBytes(source), Is.EqualTo(before));
            var outerBytes = File.ReadAllBytes(outer);
            Failure(PrefabSkills.PrefabDeleteGameObject(outer, "NestedSource/Child"));
            Assert.That(File.ReadAllBytes(outer), Is.EqualTo(outerBytes));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedDuplication_PreservesConnectionAndInternalReferences(bool removedCollider)
        {
            var source = Create("DuplicateSource", root => root.AddComponent<PrefabAssetProbe>().target = root.transform.Find("Child").gameObject);
            var outer = Create("DuplicateOuter", root =>
            {
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(Asset(source)); nested.transform.SetParent(root.transform, false);
                if (removedCollider) Object.DestroyImmediate(nested.transform.Find("Child").GetComponent<BoxCollider>());
                var body = nested.AddComponent<Rigidbody>();
                var extra = new GameObject("Extra"); extra.transform.SetParent(nested.transform, false);
                nested.GetComponent<PrefabAssetProbe>().component = body;
                PrefabUtility.RecordPrefabInstancePropertyModifications(nested.GetComponent<PrefabAssetProbe>());
            });
            Success(PrefabSkills.PrefabDuplicateGameObject(outer, "DuplicateSource", "Copy"));
            var copy = Asset(outer).transform.Find("Copy").gameObject;
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(copy), Is.EqualTo(Asset(source)));
            Assert.That(copy.GetComponent<PrefabAssetProbe>().target, Is.EqualTo(copy.transform.Find("Child").gameObject));
            Assert.That(copy.GetComponent<PrefabAssetProbe>().component, Is.EqualTo(copy.GetComponent<Rigidbody>()));
            Assert.That(copy.transform.Find("Extra"), Is.Not.Null);
            Assert.That(copy.transform.Find("Child").GetComponent<BoxCollider>() == null, Is.EqualTo(removedCollider));
        }

        [Test]
        public void EditBatchDeleteRisk_CannotHideBehindModifyMetadata()
        {
            var skills = SkillRouter.GetAllSkillsSnapshotUnfiltered().ToDictionary(s => s.Name);
            Assert.That(SkillsModeManager.IsForbiddenInSemi(skills["prefab_edit_batch"]), Is.True);
            Assert.That(SkillsModeManager.IsForbiddenInSemi(skills["prefab_delete_gameobject_batch"]), Is.True);
            Assert.That(skills["prefab_get_hierarchy"].ReadOnly, Is.True);
            Assert.That(skills["prefab_get_hierarchy"].Mode, Is.EqualTo(SkillMode.SemiAuto));
        }

        [Test]
        public void DryRun_RejectsBookkeepingAndInvalidReferenceSources()
        {
            Success(PrefabSkills.PrefabAddComponent(_path, nameof(PrefabAssetProbe)));
            var before = File.ReadAllBytes(_path);
            var script = JObject.Parse(SkillRouter.DryRun("prefab_set_serialized_property", JsonConvert.SerializeObject(new { prefabPath = _path,
                componentType = nameof(PrefabAssetProbe), propertyPath = "m_Script", assetPath = _path })));
            Assert.That(script.Value<bool>("valid"), Is.False);
            var sources = JObject.Parse(SkillRouter.DryRun("prefab_set_serialized_property", JsonConvert.SerializeObject(new { prefabPath = _path,
                componentType = nameof(PrefabAssetProbe), propertyPath = "target", referenceGameObjectPath = "Child", clearReference = true })));
            Assert.That(sources.Value<bool>("valid"), Is.False);
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void LegacyPropertyWriter_KeepsFieldsAndAliasLookup()
        {
            var result = Success(PrefabSkills.PrefabSetProperty(_path, "BoxCollider", "isTrigger", "true", gameObjectName: "Child"));
            Assert.That(result.Value<string>("component"), Is.EqualTo("BoxCollider"));
            Assert.That(result.Value<string>("property"), Is.EqualTo("m_IsTrigger"));
            Assert.That(result.Value<string>("gameObject"), Is.EqualTo("Child"));
        }

        [TestCase("[]")]
        [TestCase("not-json")]
        [TestCase("[null]")]
        [TestCase("[{\"skill\":\"gameobject_create\",\"args\":{\"name\":\"BAD\"}}]")]
        [TestCase("[{\"skill\":\"prefab_set_active\",\"args\":{\"active\":false,\"extra\":1}}]")]
        public void InvalidSteps_DoNotWrite(string steps)
        {
            var before = File.ReadAllBytes(_path); Failure(PrefabSkills.PrefabEditBatch(_path, steps));
            Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }

        [Test]
        public void DryRun_ReportsAssetAndRejectsRootDeletionWithoutMutating()
        {
            var before = File.ReadAllBytes(_path);
            var bad = JObject.Parse(SkillRouter.DryRun("prefab_delete_gameobject", JsonConvert.SerializeObject(new { prefabPath = _path, gameObjectPath = "" })));
            Assert.That(bad.Value<bool>("valid"), Is.False);
            var good = JObject.Parse(SkillRouter.DryRun("prefab_add_component", JsonConvert.SerializeObject(new { prefabPath = _path, componentType = "Rigidbody" })));
            Assert.That(good.ToString(), Does.Contain(_path)); Assert.That(File.ReadAllBytes(_path), Is.EqualTo(before));
        }
    }
}

// Producer:Betsy
