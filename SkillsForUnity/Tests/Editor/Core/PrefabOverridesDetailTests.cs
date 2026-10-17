using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// prefab_get_overrides described a list but returned counts, and could only find its instance by name or
    /// instance id (P4). Two same-name instances are now told apart by path, and includeDetails=true lists each
    /// override beside the unchanged counts: property overrides, added and removed components, added GameObjects, capped by
    /// limit with count, totalMatched and truncated as prefab_find_instances reports them. prefab_revert_overrides and
    /// prefab_apply_overrides take the same path.
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

            var total = json["propertyOverrides"].Value<int>() + json["addedComponents"].Value<int>() +
                        json["removedComponents"].Value<int>() + json["addedGameObjects"].Value<int>();
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(total), "totalMatched is every counted override.");
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(overrides.Count));
            Assert.That(json["truncated"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void Details_Limit_CutsTheListButNotTheTotal_InKindOrder()
        {
            var whole = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true));
            var total = whole["totalMatched"].Value<int>();
            Assume.That(total, Is.GreaterThan(2));

            var one = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true, limit: 1));
            var two = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true, limit: 2));

            Assert.That(one["count"]?.Value<int>(), Is.EqualTo(1), one.ToString(Formatting.None));
            Assert.That(one["overrides"], Has.Count.EqualTo(1));
            Assert.That(one["totalMatched"]?.Value<int>(), Is.EqualTo(total));
            Assert.That(one["truncated"]?.Value<bool>(), Is.True);
            Assert.That((string)one["overrides"][0]["kind"], Is.EqualTo("property"), "Property overrides are listed first.");
            Assert.That(one["propertyOverrides"], Is.EqualTo(whole["propertyOverrides"]), "The counts do not depend on limit.");
            Assert.That(two["overrides"].Select(o => (string)o["kind"]), Is.EqualTo(new[] { "property", "addedComponent" }),
                "Then added components, removed components and added GameObjects.");
        }

        [Test]
        public void Details_LimitEqualToTheTotal_IsNotTruncated_AndLimitZeroListsNothing()
        {
            var total = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true))["totalMatched"].Value<int>();

            var exact = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true, limit: total));
            var none = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy", includeDetails: true, limit: 0));

            Assert.That(exact["truncated"]?.Value<bool>(), Is.False, exact.ToString(Formatting.None));
            Assert.That(exact["overrides"], Has.Count.EqualTo(total));
            Assert.That(none["overrides"], Is.Empty);
            Assert.That(none["totalMatched"]?.Value<int>(), Is.EqualTo(total));
            Assert.That(none["truncated"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void Details_AnOverrideWithoutALiveObject_IsListedWithANullPath_NotDropped()
        {
            // Unity hands out these three with null members when it has no live object for an entry (a missing script, a destroyed
            // object); they are built by reflection so a Unity version without a public constructor skips the test, not the build.
            object New(System.Type type) => System.Activator.CreateInstance(type, true);
            AddedComponent added;
            RemovedComponent removed;
            AddedGameObject addedObject;
            try
            {
                added = (AddedComponent)New(typeof(AddedComponent));
                removed = (RemovedComponent)New(typeof(RemovedComponent));
                addedObject = (AddedGameObject)New(typeof(AddedGameObject));
            }
            catch (System.Exception ex)
            {
                Assert.Ignore("Unity does not let the override types be constructed here: " + ex.Message);
                return;
            }

            var entries = PrefabSkills.DescribeInstanceOverrides(_plain,
                new List<(PropertyModification modification, Object live)>(),
                new List<AddedComponent> { added }, new List<RemovedComponent> { removed }, new List<AddedGameObject> { addedObject })
                .Cast<IDictionary<string, object>>().ToList();

            Assert.That(entries.Select(e => (string)e["kind"]), Is.EqualTo(new[] { "addedComponent", "removedComponent", "addedGameObject" }));
            Assert.That(entries.Select(e => e["gameObjectPath"]), Is.All.Null, "No live object, so no path; the entry is still there.");
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
            Assert.That(json["totalMatched"]?.Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void Details_ComponentIndex_CountsExactTypes_AndIsTheIndexThePrefabEditsTake()
        {
            // GetComponents(typeof(IndexProbeBase)) lists the subclass instance in front of the base one, so counting subclasses
            // would call the base component index 1 while prefab_list_components, and the edits that take its index, say 0.
            var source = new GameObject("Probe");
            source.AddComponent<IndexProbeDerived>();
            source.AddComponent<IndexProbeBase>();
            var probePath = TempRoot + "/IndexProbe.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, probePath);
            Object.DestroyImmediate(source);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(probePath));
            instance.name = "IndexInstance";
            var onInstance = ExactBase(instance);
            Undo.RecordObject(onInstance, "Set value");
            onInstance.value = 5;
            PrefabUtility.RecordPrefabInstancePropertyModifications(onInstance);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(PrefabSkills.PrefabGetOverrides(path: "IndexInstance", includeDetails: true));
            var entry = ((JArray)json["overrides"]).Cast<JObject>().Single(o => (string)o["kind"] == "property" && (string)o["property"] == "value");
            Assert.That((string)entry["component"], Is.EqualTo(nameof(IndexProbeBase)), json.ToString(Formatting.None));
            Assert.That(entry["componentIndex"]?.Value<int>(), Is.EqualTo(0), "The only component of exactly this type.");

            var listed = ToJson(PrefabSkills.PrefabListComponents(probePath));
            var listedBase = ((JArray)listed["components"]).Cast<JObject>().Single(c => (string)c["componentType"] == typeof(IndexProbeBase).FullName);
            Assert.That(listedBase["componentIndex"]?.Value<int>(), Is.EqualTo(entry["componentIndex"]?.Value<int>()), "prefab_list_components agrees.");

            var edit = ToJson(PrefabSkills.PrefabSetSerializedProperty(probePath, typeof(IndexProbeBase).FullName, "value",
                componentIndex: entry["componentIndex"].Value<int>(), value: "9"));
            Assert.That(edit["success"]?.Value<bool>(), Is.True, edit.ToString(Formatting.None));

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(probePath);
            Assert.That(ExactBase(asset).value, Is.EqualTo(9), "The index hit the base component.");
            Assert.That(asset.GetComponent<IndexProbeDerived>().value, Is.EqualTo(0), "And not the subclass one that comes first.");
        }

        // ---------- prefab_revert_overrides / prefab_apply_overrides take a path ----------

        [Test]
        public void Revert_ByPath_RevertsOnlyThatSameNameInstance()
        {
            var arm = _plain.transform.Find("Arm");
            Undo.RecordObject(arm, "Move Arm");
            arm.localPosition = new Vector3(5f, 0f, 0f);
            PrefabUtility.RecordPrefabInstancePropertyModifications(arm);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(PrefabSkills.PrefabRevertOverrides(path: "ChangedHolder/Enemy"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var changed = ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy"));
            Assert.That(changed["hasOverrides"]?.Value<bool>(), Is.False, changed.ToString(Formatting.None));
            var plain = ToJson(PrefabSkills.PrefabGetOverrides(path: "PlainHolder/Enemy"));
            Assert.That(plain["hasOverrides"]?.Value<bool>(), Is.True, "The other instance keeps its override: " + plain.ToString(Formatting.None));
        }

        [Test]
        public void Apply_ByPath_PushesThatInstancesOverridesToTheAsset()
        {
            var json = ToJson(PrefabSkills.PrefabApplyOverrides(path: "ChangedHolder/Enemy"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["appliedTo"]?.ToString(), Is.EqualTo(PrefabPath));
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.That(asset.transform.Find("Arm").localPosition.x, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void RevertAndApply_ThroughTheRouter_AcceptAPath_AndAnEmptyBodyIsStillRefused()
        {
            foreach (var skill in new[] { "prefab_revert_overrides", "prefab_apply_overrides" })
            {
                var dryRun = JObject.Parse(SkillRouter.DryRun(skill, new JObject { ["path"] = "ChangedHolder/Enemy" }.ToString(Formatting.None)));
                Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, $"{skill}: {dryRun.ToString(Formatting.None)}");

                var empty = JObject.Parse(SkillRouter.DryRun(skill, "{}"));
                Assert.That(empty["valid"]?.Value<bool>(), Is.False, $"{skill}: {empty.ToString(Formatting.None)}");

                Assert.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);
                Assert.That(info.Parameters.Single(p => p.Name == "path").GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty);
            }
        }

        [Test]
        public void SyntheticEntityId_WithoutLegacyInstanceIds_ReachesRevertAndGetOverrides_ThroughTheUniquePath()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            try
            {
                var entityId = UnityObjectIdUtility.GetEntityId(_changed);
                var overrides = JObject.Parse(SkillRouter.Execute("prefab_get_overrides", new JObject { ["entityId"] = entityId }.ToString(Formatting.None)));
                Assert.That(overrides["status"]?.ToString(), Is.EqualTo("success"), overrides.ToString(Formatting.None));
                Assert.That(overrides["result"]["hasOverrides"]?.Value<bool>(), Is.True);

                var revert = JObject.Parse(SkillRouter.Execute("prefab_revert_overrides", new JObject { ["entityId"] = entityId }.ToString(Formatting.None)));
                Assert.That(revert["status"]?.ToString(), Is.EqualTo("success"), revert.ToString(Formatting.None));
                Assert.That(ToJson(PrefabSkills.PrefabGetOverrides(path: "ChangedHolder/Enemy"))["hasOverrides"]?.Value<bool>(), Is.False);
            }
            finally
            {
                SkillRouter.LegacyInstanceIdUnavailableForTests = false;
            }
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
            Assert.That(info.ParameterNames, Is.SupersetOf(new[] { "name", "instanceId", "path", "includeDetails", "limit" }));
            foreach (var documented in new[] { "path", "includeDetails", "limit" })
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

        // GetComponent<IndexProbeBase>() would return the subclass component that comes first.
        private static IndexProbeBase ExactBase(GameObject go) =>
            go.GetComponents<IndexProbeBase>().Single(component => component.GetType() == typeof(IndexProbeBase));

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
