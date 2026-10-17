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
    /// prefab_find_instances saw only active objects and counted the children of an instance as instances (P12). It now
    /// returns instance roots only, includes inactive ones when includeInactive is true, and reports totalMatched and
    /// truncated beside count (named totalMatched, not totalCount: the router's paging wrapper writes its own totalCount).
    /// </summary>
    [TestFixture]
    public class PrefabFindInstancesTests
    {
        private const string TempRoot = "Assets/Temp/PrefabFindInstancesTests";
        private const string CratePath = TempRoot + "/Crate.prefab";
        private const string BarrelPath = TempRoot + "/Barrel.prefab";
        private const string StackPath = TempRoot + "/Stack.prefab";
        private const string VariantPath = TempRoot + "/CrateVariant.prefab";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;
        private GameObject _active;
        private GameObject _inactive;
        private GameObject _underHolder;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureTempFolder();

            SavePrefab(CratePath, "Crate", "Lid", "Hinge");
            SavePrefab(BarrelPath, "Barrel", "Hoop");

            _active = Instantiate(CratePath, "CrateActive");
            _inactive = Instantiate(CratePath, "CrateInactive");
            _inactive.SetActive(false);
            _underHolder = Instantiate(CratePath, "CrateHeld", new GameObject("Holder").transform);
            Instantiate(BarrelPath, "BarrelOne");
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
        public void ReturnsInstanceRootsOnly_NotTheirChildren()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath));
            var names = Names(json);

            Assert.That(names, Is.EquivalentTo(new[] { "CrateActive", "CrateHeld" }), json.ToString(Formatting.None));
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void SkipsInactiveInstances_ByDefault()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath));

            Assert.That(Names(json), Does.Not.Contain("CrateInactive"), json.ToString(Formatting.None));
        }

        [Test]
        public void IncludeInactive_ListsThemAndSaysWhichAreInactive()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath, includeInactive: true));
            var byName = ((JArray)json["instances"]).Cast<JObject>().ToDictionary(i => (string)i["name"]);

            Assert.That(byName.Keys, Is.EquivalentTo(new[] { "CrateActive", "CrateInactive", "CrateHeld" }), json.ToString(Formatting.None));
            Assert.That(byName["CrateInactive"]["active"]?.Value<bool>(), Is.False);
            Assert.That(byName["CrateActive"]["active"]?.Value<bool>(), Is.True);
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(3));
        }

        [Test]
        public void InstancesUnderAnInactiveParent_AreInactiveToo()
        {
            _underHolder.transform.parent.gameObject.SetActive(false);
            GameObjectFinder.InvalidateCache();

            Assert.That(Names(ToJson(PrefabSkills.PrefabFindInstances(CratePath))), Is.EquivalentTo(new[] { "CrateActive" }));
            Assert.That(Names(ToJson(PrefabSkills.PrefabFindInstances(CratePath, includeInactive: true))), Does.Contain("CrateHeld"));
        }

        [Test]
        public void EachEntry_NamesItsObjectBothWays()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath));
            var held = ((JArray)json["instances"]).Cast<JObject>().Single(i => (string)i["name"] == "CrateHeld");

            Assert.That((string)held["path"], Is.EqualTo("Holder/CrateHeld"));
            Assert.That((string)held["entityId"], Is.EqualTo(UnityObjectIdUtility.GetEntityId(_underHolder)));
            Assert.That(held["instanceId"]?.Value<int>(), Is.EqualTo(UnityObjectIdUtility.GetObjectId(_underHolder)));
            Assert.That(held["nested"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void Limit_CutsTheListButNotTheTotal()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath, limit: 1, includeInactive: true));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["instances"], Has.Count.EqualTo(1));
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(json["truncated"]?.Value<bool>(), Is.True);
            Assert.That(json["totalCount"], Is.Null, "totalCount is the router's paging key and must not be used here.");
        }

        [Test]
        public void Limit_EqualToTheMatches_IsNotTruncated()
        {
            var json = ToJson(PrefabSkills.PrefabFindInstances(CratePath, limit: 2));

            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void ANestedInstanceRoot_IsAnInstance_AndIsMarkedNested()
        {
            var stackSource = new GameObject("Stack");
            var inner = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(BarrelPath));
            inner.name = "InnerBarrel";
            inner.transform.SetParent(stackSource.transform, false);
            PrefabUtility.SaveAsPrefabAsset(stackSource, StackPath);
            Object.DestroyImmediate(stackSource);
            Instantiate(StackPath, "StackOne");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(PrefabSkills.PrefabFindInstances(BarrelPath));
            var byName = ((JArray)json["instances"]).Cast<JObject>().ToDictionary(i => (string)i["name"]);

            Assert.That(byName.Keys, Is.EquivalentTo(new[] { "BarrelOne", "InnerBarrel" }), json.ToString(Formatting.None));
            Assert.That(byName["InnerBarrel"]["nested"]?.Value<bool>(), Is.True);
            Assert.That(byName["BarrelOne"]["nested"]?.Value<bool>(), Is.False);
            Assert.That(Names(ToJson(PrefabSkills.PrefabFindInstances(StackPath))), Is.EquivalentTo(new[] { "StackOne" }));
        }

        [Test]
        public void AVariantInstance_BelongsToTheVariant_NotToItsBase()
        {
            var seed = Instantiate(CratePath, "VariantSeed");
            PrefabUtility.SaveAsPrefabAsset(seed, VariantPath);
            Object.DestroyImmediate(seed);
            Instantiate(VariantPath, "VariantOne");
            GameObjectFinder.InvalidateCache();

            Assert.That(Names(ToJson(PrefabSkills.PrefabFindInstances(VariantPath))), Is.EquivalentTo(new[] { "VariantOne" }));
            Assert.That(Names(ToJson(PrefabSkills.PrefabFindInstances(CratePath))), Does.Not.Contain("VariantOne"));
        }

        [Test]
        public void ThroughTheRouter_ReturnsTheNewKeysAndNoTotalCount()
        {
            var args = new JObject { ["prefabPath"] = CratePath, ["includeInactive"] = true, ["limit"] = 2 };

            var response = JObject.Parse(SkillRouter.Execute("prefab_find_instances", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            var result = response["result"];
            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(result["totalMatched"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(result["truncated"]?.Value<bool>(), Is.True);
            Assert.That(result["instances"], Has.Count.EqualTo(2));
            Assert.That(result["totalCount"], Is.Null);
        }

        [Test]
        public void Metadata_DeclaresTheNewKeysAndParameters()
        {
            Assert.That(SkillRouter.TryGetSkill("prefab_find_instances", out var info), Is.True);

            Assert.That(info.Outputs, Is.SupersetOf(new[] { "prefabPath", "count", "totalMatched", "truncated", "instances" }));
            Assert.That(info.ParameterNames, Is.SupersetOf(new[] { "prefabPath", "limit", "includeInactive" }));
            foreach (var documented in new[] { "limit", "includeInactive" })
                Assert.That(info.Parameters.Single(p => p.Name == documented).GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty,
                    $"{documented} needs a [SkillParam] note.");
        }

        // ---------- helpers ----------

        private static string[] Names(JObject json) =>
            ((JArray)json["instances"]).Select(i => (string)i["name"]).ToArray();

        // A root with a chain of children, saved as a prefab asset.
        private static void SavePrefab(string path, string rootName, params string[] childNames)
        {
            var root = new GameObject(rootName);
            var parent = root.transform;
            foreach (var childName in childNames)
            {
                var child = new GameObject(childName);
                child.transform.SetParent(parent, false);
                parent = child.transform;
            }

            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
        }

        private static GameObject Instantiate(string prefabPath, string name, Transform parent = null)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            instance.name = name;
            if (parent != null)
                instance.transform.SetParent(parent, false);
            return instance;
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "PrefabFindInstancesTests");
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
