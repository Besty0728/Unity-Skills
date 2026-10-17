using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// prefab_get_overrides described a list but returned counts, and could only find its instance by name or
    /// instance id (P4). Two same-name instances are now told apart by path, and includeDetails=true lists each
    /// override beside the unchanged counts: property overrides, added and removed components, added GameObjects.
    /// </summary>
    [TestFixture]
    public class PrefabOverridesDetailTests
    {
        private const string TempRoot = "Assets/Temp/PrefabOverridesDetailTests";
        private const string PrefabPath = TempRoot + "/Enemy.prefab";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private GameObject _plain;
        private GameObject _changed;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureTempFolder();

            var source = new GameObject("Enemy");
            var arm = new GameObject("Arm");
            arm.transform.SetParent(source.transform, false);
            arm.AddComponent<BoxCollider>();
            source.AddComponent<AudioSource>();
            PrefabUtility.SaveAsPrefabAsset(source, PrefabPath);
            Object.DestroyImmediate(source);

            _plain = Instantiate("PlainHolder");
            _changed = Instantiate("ChangedHolder");
            Modify(_changed);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        [Test]
        public void Default_ReturnsTheSameCountsAsBefore_AndNoList()
        {
            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy"));

            Assert.That(json.Properties().Select(p => p.Name), Is.EquivalentTo(new[]
            {
                "success", "prefabPath", "propertyOverrides", "addedComponents", "removedComponents", "addedGameObjects", "hasOverrides"
            }), json.ToString(Formatting.None));
            Assert.That(json["prefabPath"]?.ToString(), Is.EqualTo(PrefabPath));
            Assert.That(json["propertyOverrides"]?.Value<int>(), Is.GreaterThan(0));
            Assert.That(json["addedComponents"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["removedComponents"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["addedGameObjects"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["hasOverrides"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void Path_PicksTheSameNameInstanceItNames()
        {
            var plain = ToJson(PrefabSkills.PrefabGetOverrides(path: "PlainHolder/Enemy"));
            var changed = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy"));

            Assert.That(plain["hasOverrides"]?.Value<bool>(), Is.False, plain.ToString(Formatting.None));
            Assert.That(plain["propertyOverrides"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(changed["hasOverrides"]?.Value<bool>(), Is.True, changed.ToString(Formatting.None));
        }

        [Test]
        public void Path_OfAChildInsideTheInstance_ResolvesTheOutermostInstance()
        {
            var viaChild = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy/Arm"));
            var viaRoot = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy"));

            Assert.That(viaChild["propertyOverrides"], Is.EqualTo(viaRoot["propertyOverrides"]));
            Assert.That(viaChild["addedComponents"], Is.EqualTo(viaRoot["addedComponents"]));
        }

        [Test]
        public void Details_ListEveryOverrideTheCountsCount()
        {
            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true));
            var overrides = ((JArray)json["overrides"]).Cast<JObject>().ToList();

            Assert.That(CountOf(overrides, "property"), Is.EqualTo(json["propertyOverrides"].Value<int>()), json.ToString(Formatting.None));
            Assert.That(CountOf(overrides, "addedComponent"), Is.EqualTo(json["addedComponents"].Value<int>()));
            Assert.That(CountOf(overrides, "removedComponent"), Is.EqualTo(json["removedComponents"].Value<int>()));
            Assert.That(CountOf(overrides, "addedGameObject"), Is.EqualTo(json["addedGameObjects"].Value<int>()));
        }

        [Test]
        public void Details_NameTheOverriddenPropertyAndItsValue()
        {
            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true));
            var overrides = ((JArray)json["overrides"]).Cast<JObject>().ToList();

            var moved = overrides.Single(o => (string)o["kind"] == "property" && (string)o["property"] == "m_LocalPosition.x");
            Assert.That((string)moved["gameObjectPath"], Is.EqualTo("Arm"));
            Assert.That((string)moved["component"], Is.EqualTo("Transform"));
            Assert.That(moved["componentIndex"]?.Value<int>(), Is.EqualTo(0));
            Assert.That((string)moved["value"], Is.EqualTo("2"));
        }

        [Test]
        public void Details_NameAddedAndRemovedComponentsAndAddedObjects()
        {
            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true));
            var overrides = ((JArray)json["overrides"]).Cast<JObject>().ToList();

            var added = overrides.Single(o => (string)o["kind"] == "addedComponent");
            Assert.That((string)added["gameObjectPath"], Is.EqualTo("Arm"));
            Assert.That((string)added["component"], Is.EqualTo(nameof(SphereCollider)));

            var removed = overrides.Single(o => (string)o["kind"] == "removedComponent");
            Assert.That((string)removed["gameObjectPath"], Is.EqualTo(""));
            Assert.That((string)removed["component"], Is.EqualTo(nameof(AudioSource)));

            var extra = overrides.Single(o => (string)o["kind"] == "addedGameObject");
            Assert.That((string)extra["gameObjectPath"], Is.EqualTo("Extra"));
            Assert.That((string)extra["name"], Is.EqualTo("Extra"));
        }

        [Test]
        public void Details_OfAnUntouchedInstance_AreEmpty()
        {
            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "PlainHolder/Enemy", includeDetails: true));

            Assert.That(json["overrides"], Is.Empty, json.ToString(Formatting.None));
            Assert.That(json["hasOverrides"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void ThroughTheRouter_AcceptsPathAndIncludeDetails()
        {
            var args = new JObject { ["path"] = "ChangedHolder/Enemy", ["includeDetails"] = true };

            var dryRun = JObject.Parse(SkillRouter.DryRun("prefab_get_overrides", args.ToString(Formatting.None)));
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, dryRun.ToString(Formatting.None));

            var response = JObject.Parse(SkillRouter.Execute("prefab_get_overrides", args.ToString(Formatting.None)));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response["result"]["overrides"], Is.Not.Empty);
        }

        [Test]
        public void ThroughTheRouter_AnEmptyBodyIsStillRefused()
        {
            var dryRun = JObject.Parse(SkillRouter.DryRun("prefab_get_overrides", "{}"));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False, dryRun.ToString(Formatting.None));
        }

        [Test]
        public void Metadata_DescribesCountsAndTheListSwitch()
        {
            Assert.That(SkillRouter.TryGetSkill("prefab_get_overrides", out var info), Is.True);

            Assert.That(info.Description, Does.Contain("includeDetails"));
            Assert.That(info.Description, Does.Not.StartWith("Get list"), "The skill returns counts unless includeDetails is true.");
            Assert.That(info.ParameterNames, Is.SupersetOf(new[] { "name", "instanceId", "path", "includeDetails" }));
            foreach (var documented in new[] { "path", "includeDetails" })
                Assert.That(info.Parameters.Single(p => p.Name == documented).GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty,
                    $"{documented} needs a [SkillParam] note.");
        }

        // ---------- helpers ----------

        private static GameObject Instantiate(string holderName)
        {
            var holder = new GameObject(holderName);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            instance.transform.SetParent(holder.transform, false);
            return instance;
        }

        // A moved child, a component added to it, a component removed from the root, and a new child object.
        private static void Modify(GameObject instance)
        {
            var arm = instance.transform.Find("Arm");
            Undo.RecordObject(arm, "Move Arm");
            arm.localPosition = new Vector3(2f, 0f, 0f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(arm);

            Undo.AddComponent<SphereCollider>(arm.gameObject);
            Undo.DestroyObjectImmediate(instance.GetComponent<AudioSource>());

            var extra = new GameObject("Extra");
            Undo.RegisterCreatedObjectUndo(extra, "Add Extra");
            extra.transform.SetParent(instance.transform, false);
        }

        private static int CountOf(System.Collections.Generic.IEnumerable<JObject> overrides, string kind) =>
            overrides.Count(o => (string)o["kind"] == kind);

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "PrefabOverridesDetailTests");
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
