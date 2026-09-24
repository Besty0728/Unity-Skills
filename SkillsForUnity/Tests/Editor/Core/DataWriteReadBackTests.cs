using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Component and prefab writes answer with what the Editor now holds (resolved names, world positions, saved
    /// paths, re-read values), record Undo/workflow where they claim to, and dryRun reaches the verdict execution
    /// does for material_create's save path and light_set_enabled_batch's required enabled.
    /// </summary>
    [TestFixture]
    public class DataWriteReadBackTests
    {
        private const string ProbeFolder = "Assets/__UnitySkillsDataReadBackProbe__";
        private const float Tolerance = 1e-3f;

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (WorkflowManager.IsRecording)
                WorkflowManager.AbortTask();
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- component_set_enabled ----------

        [Test]
        public void SetEnabled_OnAnInactiveObject_ReportsIsActiveAndEnabledFalse()
        {
            var parent = new GameObject("DR_InactiveParent");
            var child = new GameObject("DR_Child");
            child.transform.SetParent(parent.transform);
            child.AddComponent<AudioSource>().enabled = false;
            parent.SetActive(false);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetEnabled(path: "DR_InactiveParent/DR_Child", componentType: "audiosource", enabled: true));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["componentType"]?.ToString(), Is.EqualTo("AudioSource"), "componentType is the resolved type name.");
            Assert.That(json["enabled"]?.Value<bool>(), Is.True);
            Assert.That(json["isActiveAndEnabled"]?.Value<bool>(), Is.False, "An enabled Behaviour under an inactive parent does not run.");
        }

        [Test]
        public void SetEnabled_OnACollider_HasNoIsActiveAndEnabled()
        {
            var go = new GameObject("DR_Collider");
            go.AddComponent<BoxCollider>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetEnabled(name: "DR_Collider", componentType: "BoxCollider", enabled: false));

            Assert.That(json["enabled"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            Assert.That(json["isActiveAndEnabled"], Is.Null);
            Assert.That(go.GetComponent<BoxCollider>().enabled, Is.False);
        }

        [Test]
        public void SetEnabled_IsSnapshottedForWorkflowRollback()
        {
            var go = new GameObject("DR_Snapshot");
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            WorkflowManager.BeginTask("DR_SetEnabled", "test");
            ComponentSkills.ComponentSetEnabled(name: "DR_Snapshot", componentType: "AudioSource", enabled: false);

            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Not.Empty, "component_set_enabled tracks workflow, so it must snapshot what it modifies.");
        }

        // ---------- component_copy ----------

        [Test]
        public void Copy_ByPath_ReportsTheResolvedNamesAndPasted()
        {
            var source = new GameObject("DR_Source");
            source.AddComponent<BoxCollider>().size = new Vector3(2, 3, 4);
            new GameObject("DR_Target");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentCopy(sourcePath: "DR_Source", targetPath: "DR_Target", componentType: "boxcollider"));

            Assert.That(json["source"]?.ToString(), Is.EqualTo("DR_Source"), json.ToString(Formatting.None));
            Assert.That(json["target"]?.ToString(), Is.EqualTo("DR_Target"));
            Assert.That(json["componentType"]?.ToString(), Is.EqualTo("BoxCollider"));
            Assert.That(json["pasted"]?.Value<bool>(), Is.True);
            Assert.That(GameObject.Find("DR_Target").GetComponent<BoxCollider>().size, Is.EqualTo(new Vector3(2, 3, 4)));
        }

        [Test]
        public void Copy_ThroughRouter_IsUndoable()
        {
            new GameObject("DR_UndoSource").AddComponent<AudioSource>();
            var target = new GameObject("DR_UndoTarget");
            GameObjectFinder.InvalidateCache();

            var copy = JObject.Parse(SkillRouter.Execute("component_copy",
                "{\"sourceName\":\"DR_UndoSource\",\"targetName\":\"DR_UndoTarget\",\"componentType\":\"AudioSource\"}"));
            Assert.That(copy["status"]?.ToString(), Is.EqualTo("success"), copy.ToString(Formatting.None));
            Assert.That(target.GetComponent<AudioSource>(), Is.Not.Null);

            var undo = JObject.Parse(SkillRouter.Execute("editor_undo", "{}"));
            Assert.That(undo["status"]?.ToString(), Is.EqualTo("success"), undo.ToString(Formatting.None));
            Assert.That(target.GetComponent<AudioSource>(), Is.Null, "editor_undo must remove the pasted copy.");
        }

        [Test]
        public void Copy_DeclaresThatItMutatesTheScene()
        {
            Assert.That(SkillRouter.TryGetSkill("component_copy", out var skill), Is.True);
            Assert.That(skill.MutatesScene, Is.True);
        }

        // ---------- prefab_create / prefab_create_variant ----------

        [Test]
        public void PrefabCreate_ReadsBackThePathAndTheConnection()
        {
            EnsureProbeFolder();
            new GameObject("DR_PrefabSource");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(PrefabSkills.PrefabCreate(name: "DR_PrefabSource", savePath: ProbeFolder + "/DR_Made"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["prefabPath"]?.ToString(), Is.EqualTo(ProbeFolder + "/DR_Made.prefab"), "'.prefab' is appended and the saved path reported.");
            Assert.That(json["connected"]?.Value<bool>(), Is.True);
            Assert.That(File.Exists(ProbeFolder + "/DR_Made.prefab"), Is.True);
        }

        [Test]
        public void PrefabCreateVariant_ReportsIsVariant()
        {
            var prefabPath = CreateProbePrefab("DR_VariantBase");

            var json = ToJson(PrefabSkills.PrefabCreateVariant(prefabPath, ProbeFolder + "/DR_Variant"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["variantPath"]?.ToString(), Is.EqualTo(ProbeFolder + "/DR_Variant.prefab"));
            Assert.That(json["isVariant"]?.Value<bool>(), Is.True);
            Assert.That(FindHelper.FindAll<GameObject>(includeInactive: true).Any(g => g.name.StartsWith("DR_VariantBase")), Is.False,
                "The temporary instance used to save the variant must not stay in the scene.");
        }

        // ---------- prefab_instantiate_batch ----------

        [Test]
        public void PrefabInstantiateBatch_UnderAParent_ReportsWorldAndLocalPosition()
        {
            var prefabPath = CreateProbePrefab("DR_Spawnable");
            var parent = new GameObject("DR_SpawnParent");
            parent.transform.position = new Vector3(10, 0, 0);
            parent.transform.localScale = new Vector3(2, 2, 2);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(PrefabSkills.PrefabInstantiateBatch(
                "[{\"prefabPath\":\"" + prefabPath + "\",\"name\":\"DR_Spawned\",\"x\":1,\"parentName\":\"DR_SpawnParent\"}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var spawned = parent.transform.Find("DR_Spawned");
            Assert.That(spawned, Is.Not.Null);
            AssertVector(json["results"]?[0]?["position"], spawned.position, "position is the world position");
            AssertVector(json["results"]?[0]?["localPosition"], new Vector3(1, 0, 0), "localPosition");
            Assert.That(spawned.position.x, Is.EqualTo(12f).Within(Tolerance), "Precondition: the parent moves and scales the child.");
        }

        [Test]
        public void PrefabInstantiateBatch_BadParent_LeavesNothingBehind()
        {
            var prefabPath = CreateProbePrefab("DR_Rollback");
            GameObjectFinder.InvalidateCache();
            int before = FindHelper.FindAll<GameObject>(includeInactive: true).Length;

            var json = ToJson(PrefabSkills.PrefabInstantiateBatch(
                "[{\"prefabPath\":\"" + prefabPath + "\",\"name\":\"DR_Good\"}," +
                "{\"prefabPath\":\"" + prefabPath + "\",\"name\":\"DR_Bad\",\"parentName\":\"DR_NoSuchParent_7f3\"}]"));

            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(FindHelper.FindAll<GameObject>(includeInactive: true).Length, Is.EqualTo(before),
                "A failed item must not leave a stray instance, and the atomic batch removes the one that succeeded.");
        }

        // ---------- prefab_set_property ----------

        [Test]
        public void PrefabSetProperty_ReadsBackTheStoredValueAndResolvedNames()
        {
            var prefabPath = CreateProbePrefab("DR_SetProp", typeof(BoxCollider));

            var json = ToJson(PrefabSkills.PrefabSetProperty(prefabPath, "boxcollider", "size", "1,2,3"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["component"]?.ToString(), Is.EqualTo("BoxCollider"));
            Assert.That(json["property"]?.ToString(), Is.EqualTo("m_Size"));
            Assert.That(json["valueSet"]?.ToString(), Is.EqualTo("1,2,3"));
            Assert.That(json["valueRequested"], Is.Null, "valueRequested appears only when the stored value differs.");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<BoxCollider>().size, Is.EqualTo(new Vector3(1, 2, 3)));
        }

        [Test]
        public void PrefabSetProperty_InvalidBool_IsRejectedAndWritesNothing()
        {
            var prefabPath = CreateProbePrefab("DR_Bool", typeof(BoxCollider));

            var json = ToJson(PrefabSkills.PrefabSetProperty(prefabPath, "BoxCollider", "m_IsTrigger", "treu"));

            Assert.That(json["error"]?.ToString(), Does.StartWith("Invalid value"), json.ToString(Formatting.None));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<BoxCollider>().isTrigger, Is.False);
        }

        [Test]
        public void PrefabSetProperty_EnumIndexNumber_WarnsLikeTheSerializedWriters()
        {
            var prefabPath = CreateProbePrefab("DR_Enum", typeof(EnumProbe));

            var json = ToJson(PrefabSkills.PrefabSetProperty(prefabPath, nameof(EnumProbe), "flags", "3"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["valueSet"]?.ToString(), Is.EqualTo("3"), "Enums read back as their member index.");
            Assert.That(json["warnings"]?[0]?.ToString(), Does.Contain("index 3"));
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath).GetComponent<EnumProbe>().flags, Is.EqualTo(EnumProbe.ProbeFlags.C));
        }

        [Test]
        public void PrefabSetProperty_UndeclaredEnumBits_AreRejected()
        {
            var prefabPath = CreateProbePrefab("DR_EnumBad", typeof(EnumProbe));

            var json = ToJson(PrefabSkills.PrefabSetProperty(prefabPath, nameof(EnumProbe), "sparse", "99"));

            Assert.That(json["error"]?.ToString(), Does.StartWith("Invalid value"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("Enum value '99' not found"));
        }

        // ---------- planner parity ----------

        [Test]
        public void MaterialCreate_DryRunPath_MatchesExecution()
        {
            const string rawSavePath = "Assets\\__UnitySkillsDataReadBackProbe__";

            var dry = JObject.Parse(SkillRouter.DryRun("material_create",
                new JObject { ["name"] = "DR_Material", ["savePath"] = rawSavePath }.ToString(Formatting.None)));
            var planned = dry["changes"]?["create"]?[0]?["path"]?.ToString();

            var executed = ToJson(MaterialSkills.MaterialCreate("DR_Material", null, rawSavePath));

            Assert.That(executed["path"]?.ToString(), Is.EqualTo(ProbeFolder + "/DR_Material.mat"), executed.ToString(Formatting.None));
            Assert.That(planned, Is.EqualTo(executed["path"]?.ToString()), "dryRun must predict the path material_create writes.");
        }

        [Test]
        public void MaterialCreate_ReadOnlyPackagePath_IsRejectedByDryRunToo()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("material_create",
                "{\"name\":\"DR_Material\",\"savePath\":\"Packages/com.nobody.nothing7f3/Mats\"}"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            Assert.That(dry["validation"]?["semanticErrors"]?.Any(e => e["field"]?.ToString() == "savePath"), Is.True);
        }

        [Test]
        public void LightEnabledBatch_OmittedEnabled_DryRunAndRestExecutionAgree()
        {
            var a = CreateLight("DR_LightA");
            var b = CreateLight("DR_LightB");
            var body = new JObject { ["items"] = "[{\"name\":\"DR_LightA\",\"enabled\":false},{\"name\":\"DR_LightB\"}]" }.ToString(Formatting.None);

            var dry = JObject.Parse(SkillRouter.DryRun("light_set_enabled_batch", body));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            // Listed once, although a dryRun runs the planners twice on the same validation.
            Assert.That(dry["validation"]?["missingParams"]?.Values<string>().ToArray(), Is.EqualTo(new[] { "items[1].enabled" }),
                dry.ToString(Formatting.None));

            var executed = JObject.Parse(SkillRouter.Execute("light_set_enabled_batch", body));
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), executed.ToString(Formatting.None));
            Assert.That(a.enabled && b.enabled, Is.True, "No light may be switched while an item is missing enabled.");
        }

        [Test]
        public void LightEnabledBatch_ExplicitValues_AreValidInDryRun()
        {
            CreateLight("DR_LightC");
            var body = new JObject { ["items"] = "[{\"name\":\"DR_LightC\",\"enabled\":false}]" }.ToString(Formatting.None);

            var dry = JObject.Parse(SkillRouter.DryRun("light_set_enabled_batch", body));

            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));
        }

        /// <summary>Needs light_set_enabled_batch's own B5 change (MISSING_PARAM per item), owned by the scene module.</summary>
        [Test]
        public void LightEnabledBatch_OmittedEnabled_DirectExecutionMatchesThePlanner()
        {
            var a = CreateLight("DR_LightD");
            var b = CreateLight("DR_LightE");

            var json = ToJson(LightSkills.LightSetEnabledBatch("[{\"name\":\"DR_LightD\",\"enabled\":false},{\"name\":\"DR_LightE\"}]"));

            Assert.That(json["results"]?[1]?["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(a.enabled && b.enabled, Is.True);
        }

        // ---------- helpers ----------

        private static Light CreateLight(string name)
        {
            var light = new GameObject(name).AddComponent<Light>();
            light.enabled = true;
            GameObjectFinder.InvalidateCache();
            return light;
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(ProbeFolder));
        }

        private static string CreateProbePrefab(string name, params System.Type[] components)
        {
            EnsureProbeFolder();
            var source = new GameObject(name, components);
            foreach (var type in components)
                Assert.That(source.GetComponent(type), Is.Not.Null, $"Unity did not attach {type.Name}; fixtures must live in a non-editor assembly.");
            var path = ProbeFolder + "/" + name + ".prefab";
            var saved = PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            Assume.That(saved, Is.Not.Null, "Could not create the prefab fixture.");
            GameObjectFinder.InvalidateCache();
            return path;
        }

        private static void AssertVector(JToken token, Vector3 expected, string label)
        {
            Assert.That(token, Is.Not.Null, label);
            var actual = new Vector3(token.Value<float>("x"), token.Value<float>("y"), token.Value<float>("z"));
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"{label}: expected {expected}, got {actual}");
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
