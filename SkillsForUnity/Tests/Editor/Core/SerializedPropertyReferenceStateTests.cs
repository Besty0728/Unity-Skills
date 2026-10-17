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
    /// A serialized object reference read as "null" whether it was never assigned or pointed at something destroyed (P7),
    /// and fields the Inspector hides, such as HingeJoint.m_ConnectedBody, were never listed (P9). An ObjectReference
    /// entry now carries referenceState (none, missing or assigned) with its value still "null", and includeHidden=true
    /// lists the hidden fields too.
    /// </summary>
    [TestFixture]
    public class SerializedPropertyReferenceStateTests
    {
        private const string TempRoot = "Assets/Temp/SerializedPropertyReferenceStateTests";

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
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        // ---------- P7: referenceState ----------

        [Test]
        public void ANeverAssignedReference_IsNone_AndStillReadsNull()
        {
            var probe = new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            GameObjectFinder.InvalidateCache();

            var entry = Entry(Properties("Holder", nameof(PrefabAssetProbe)), "target");

            Assert.That((string)entry["referenceState"], Is.EqualTo("none"), entry.ToString(Formatting.None));
            Assert.That((string)entry["value"], Is.EqualTo("null"));
            Assert.That(probe.target, Is.Null);
        }

        [Test]
        public void AnAssignedReference_IsAssigned_AndNamesItsTarget()
        {
            var target = new GameObject("Referenced");
            var probe = new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            probe.target = target;
            GameObjectFinder.InvalidateCache();

            var entry = Entry(Properties("Holder", nameof(PrefabAssetProbe)), "target");

            Assert.That((string)entry["referenceState"], Is.EqualTo("assigned"), entry.ToString(Formatting.None));
            Assert.That((string)entry["value"], Does.StartWith(UnityObjectIdUtility.GetEntityId(target) + ":").And.EndWith(":Referenced"));
        }

        [Test]
        public void ADestroyedTarget_IsMissing_YetStillReadsNull()
        {
            var target = new GameObject("Referenced");
            var probe = new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            probe.target = target;
            Object.DestroyImmediate(target);
            GameObjectFinder.InvalidateCache();

            var entry = Entry(Properties("Holder", nameof(PrefabAssetProbe)), "target");

            Assert.That((string)entry["referenceState"], Is.EqualTo("missing"), entry.ToString(Formatting.None));
            Assert.That((string)entry["value"], Is.EqualTo("null"), "The value keeps its old reading; referenceState is what tells the cases apart.");
        }

        [Test]
        public void ClearingAReference_ReturnsItToNone()
        {
            var target = new GameObject("Referenced");
            var probe = new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            probe.target = target;
            probe.target = null;
            GameObjectFinder.InvalidateCache();

            Assert.That((string)Entry(Properties("Holder", nameof(PrefabAssetProbe)), "target")["referenceState"], Is.EqualTo("none"));
        }

        [Test]
        public void OnlyObjectReferenceEntries_CarryReferenceState()
        {
            new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            GameObjectFinder.InvalidateCache();
            var properties = Properties("Holder", nameof(PrefabAssetProbe));

            foreach (var entry in properties.Cast<JObject>())
            {
                var isReference = (string)entry["propertyType"] == "ObjectReference";
                Assert.That(entry.Property("referenceState") != null, Is.EqualTo(isReference),
                    $"{entry["propertyPath"]} ({entry["propertyType"]}) {(isReference ? "must" : "must not")} carry referenceState.");
            }

            Assert.That(properties.Any(e => (string)e["propertyType"] == "ObjectReference"), Is.True);
            Assert.That(properties.Any(e => (string)e["propertyType"] == "String"), Is.True);
        }

        [Test]
        public void ThePrefabAssetReader_CarriesReferenceState()
        {
            EnsureTempFolder();
            var source = new GameObject("Source");
            source.AddComponent<PrefabAssetProbe>().target = new GameObject("Referenced");
            var prefabPath = TempRoot + "/Probe.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);

            var json = JObject.Parse(JsonConvert.SerializeObject(PrefabSkills.PrefabGetSerializedProperties(prefabPath, nameof(PrefabAssetProbe))));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var properties = (JArray)json["properties"];
            Assert.That((string)Entry(properties, "component")["referenceState"], Is.EqualTo("none"));
            Assert.That((string)Entry(properties, "m_Script")["referenceState"], Is.EqualTo("assigned"));
        }

        [Test]
        public void TheScriptableObjectReader_CarriesReferenceState()
        {
            EnsureTempFolder();
            var assetPath = TempRoot + "/Probe.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<EventProbeAsset>(), assetPath);

            var json = JObject.Parse(JsonConvert.SerializeObject(ScriptableObjectSkills.ScriptableObjectGetSerializedProperties(assetPath)));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That((string)Entry((JArray)json["properties"], "m_Script")["referenceState"], Is.EqualTo("assigned"));
        }

        [Test]
        public void ReferenceState_ThroughTheRouter_ReachesTheResponse()
        {
            var target = new GameObject("Referenced");
            new GameObject("Holder").AddComponent<PrefabAssetProbe>().target = target;
            GameObjectFinder.InvalidateCache();
            var args = new JObject { ["name"] = "Holder", ["componentType"] = nameof(PrefabAssetProbe) };

            var response = JObject.Parse(SkillRouter.Execute("component_get_serialized_properties", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            var entry = ((JArray)response["result"]["properties"]).Cast<JObject>().Single(e => (string)e["propertyPath"] == "target");
            Assert.That((string)entry["referenceState"], Is.EqualTo("assigned"));
        }

        // ---------- P9: includeHidden ----------

        [Test]
        public void IncludeHidden_ListsFieldsTheInspectorHides()
        {
            var owner = new GameObject("HingeOwner", typeof(Rigidbody), typeof(HingeJoint));
            var body = new GameObject("HingeBody", typeof(Rigidbody));
            owner.GetComponent<HingeJoint>().connectedBody = body.GetComponent<Rigidbody>();
            GameObjectFinder.InvalidateCache();

            var visible = Properties("HingeOwner", nameof(HingeJoint));
            var everything = Properties("HingeOwner", nameof(HingeJoint), includeHidden: true);

            Assert.That(visible.Any(e => (string)e["propertyPath"] == "m_ConnectedBody"), Is.False,
                "m_ConnectedBody is hidden from the Inspector's property walk; if it shows by default the flag has nothing to add.");
            var connected = Entry(everything, "m_ConnectedBody");
            Assert.That((string)connected["referenceState"], Is.EqualTo("assigned"), connected.ToString(Formatting.None));
            Assert.That(everything.Count, Is.GreaterThan(visible.Count));
        }

        [Test]
        public void IncludeHidden_KeepsEveryDefaultEntry_InTheSameOrder()
        {
            new GameObject("HingeOwner", typeof(Rigidbody), typeof(HingeJoint));
            GameObjectFinder.InvalidateCache();

            var visible = Properties("HingeOwner", nameof(HingeJoint)).Select(e => (string)e["propertyPath"]).ToList();
            var everything = Properties("HingeOwner", nameof(HingeJoint), includeHidden: true).Select(e => (string)e["propertyPath"]).ToList();

            Assert.That(everything.Where(visible.Contains).ToList(), Is.EqualTo(visible));
        }

        [Test]
        public void IncludeHidden_DoesNotWalkIntoStringsOrReferences()
        {
            new GameObject("Holder").AddComponent<PrefabAssetProbe>();
            GameObjectFinder.InvalidateCache();

            var paths = Properties("Holder", nameof(PrefabAssetProbe), includeHidden: true).Select(e => (string)e["propertyPath"]).ToList();

            Assert.That(paths, Does.Contain("m_PrefabInstance"), "The hidden bookkeeping fields are listed.");
            Assert.That(paths, Is.SupersetOf(new[] { "target", "component", "material", "settings.speed", "values.Array.size", "label" }));
            Assert.That(paths.Where(p => p.EndsWith(".m_FileID") || p.EndsWith(".m_PathID") || p.StartsWith("m_Name.") || p.StartsWith("m_EditorClassIdentifier.")),
                Is.Empty, "A reference is one entry, not its file and path ids; a string is one entry, not its characters.");
        }

        [Test]
        public void IncludeHidden_StillHonorsLimit()
        {
            new GameObject("HingeOwner", typeof(Rigidbody), typeof(HingeJoint));
            GameObjectFinder.InvalidateCache();

            Assert.That(Properties("HingeOwner", nameof(HingeJoint), includeHidden: true, limit: 3), Has.Count.EqualTo(3));
        }

        [Test]
        public void IncludeHidden_ThroughTheRouter_IsAcceptedAndDocumentedInTheSchema()
        {
            var owner = new GameObject("HingeOwner", typeof(Rigidbody), typeof(HingeJoint));
            owner.GetComponent<HingeJoint>().connectedBody = new GameObject("HingeBody", typeof(Rigidbody)).GetComponent<Rigidbody>();
            GameObjectFinder.InvalidateCache();
            var args = new JObject { ["name"] = "HingeOwner", ["componentType"] = nameof(HingeJoint), ["includeHidden"] = true };

            var response = JObject.Parse(SkillRouter.Execute("component_get_serialized_properties", args.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(((JArray)response["result"]["properties"]).Any(e => (string)e["propertyPath"] == "m_ConnectedBody"), Is.True);

            Assert.That(SkillRouter.TryGetSkill("component_get_serialized_properties", out var info), Is.True);
            var parameter = info.Parameters.Single(p => p.Name == "includeHidden");
            Assert.That(parameter.GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty);
        }

        [Test]
        public void IncludeHidden_ScriptableObjectReader_ListsTheHiddenFields()
        {
            EnsureTempFolder();
            var assetPath = TempRoot + "/Hidden.asset";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<EventProbeAsset>(), assetPath);

            var visible = PathsOf(ScriptableObjectSkills.ScriptableObjectGetSerializedProperties(assetPath));
            var everything = PathsOf(ScriptableObjectSkills.ScriptableObjectGetSerializedProperties(assetPath, includeHidden: true));

            Assert.That(visible, Does.Not.Contain("m_ObjectHideFlags"), "The default listing is what the Inspector shows.");
            Assert.That(visible, Does.Contain("hits"));
            Assert.That(everything, Does.Contain("m_ObjectHideFlags").And.Contain("m_PrefabInstance").And.Contain("hits"));
            Assert.That(everything.Where(visible.Contains).ToList(), Is.EqualTo(visible), "includeHidden only adds entries.");
        }

        [Test]
        public void IncludeHidden_PrefabReaders_ListTheHiddenFields()
        {
            var prefabPath = SaveProbePrefab();

            var visible = PathsOf(PrefabSkills.PrefabGetSerializedProperties(prefabPath, nameof(PrefabAssetProbe)));
            var everything = PathsOf(PrefabSkills.PrefabGetSerializedProperties(prefabPath, nameof(PrefabAssetProbe), includeHidden: true));

            Assert.That(visible, Does.Not.Contain("m_ObjectHideFlags"));
            Assert.That(visible, Does.Contain("label"));
            Assert.That(everything, Does.Contain("m_ObjectHideFlags").And.Contain("m_PrefabInstance").And.Contain("label"));
            Assert.That(everything.Where(visible.Contains).ToList(), Is.EqualTo(visible));

            var listed = ComponentProperties(PrefabSkills.PrefabListComponents(prefabPath, null, true));
            var listedHidden = ComponentProperties(PrefabSkills.PrefabListComponents(prefabPath, null, true, includeHidden: true));
            Assert.That(listed, Does.Not.Contain("m_ObjectHideFlags"));
            Assert.That(listedHidden, Does.Contain("m_ObjectHideFlags").And.Contain("label"));

            var withoutProperties = JObject.Parse(JsonConvert.SerializeObject(PrefabSkills.PrefabListComponents(prefabPath, null, false, includeHidden: true)));
            Assert.That(((JArray)withoutProperties["components"]).Cast<JObject>().All(c => c["properties"] == null), Is.True,
                "includeHidden only matters together with includeProperties.");
        }

        [TestCase("component_get_serialized_properties")]
        [TestCase("scriptableobject_get_serialized_properties")]
        [TestCase("prefab_get_serialized_properties")]
        [TestCase("prefab_list_components")]
        public void EveryReader_OffersIncludeHidden_WithANote(string skill)
        {
            Assert.That(SkillRouter.TryGetSkill(skill, out var info), Is.True);

            var parameter = info.Parameters.SingleOrDefault(p => p.Name == "includeHidden");
            Assert.That(parameter, Is.Not.Null, $"{skill} has no includeHidden parameter.");
            Assert.That(parameter.HasDefaultValue && Equals(parameter.DefaultValue, false), Is.True, "includeHidden must default to false.");
            Assert.That(parameter.GetCustomAttributes(typeof(SkillParamAttribute), false), Is.Not.Empty);
        }

        // ---------- helpers ----------

        private static string SaveProbePrefab()
        {
            EnsureTempFolder();
            var source = new GameObject("Source");
            source.AddComponent<PrefabAssetProbe>();
            var prefabPath = TempRoot + "/Hidden.prefab";
            PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            Object.DestroyImmediate(source);
            return prefabPath;
        }

        private static System.Collections.Generic.List<string> PathsOf(object result)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(result));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            return ((JArray)json["properties"]).Select(e => (string)e["propertyPath"]).ToList();
        }

        // The propertyPath of every property listed for the PrefabAssetProbe component.
        private static System.Collections.Generic.List<string> ComponentProperties(object result)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(result));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            var probe = ((JArray)json["components"]).Cast<JObject>().Single(c => ((string)c["componentType"]).EndsWith(nameof(PrefabAssetProbe)));
            return ((JArray)probe["properties"]).Select(e => (string)e["propertyPath"]).ToList();
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "SerializedPropertyReferenceStateTests");
        }

        private static JArray Properties(string objectName, string componentType, bool includeHidden = false, int limit = 200)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(
                ComponentSkills.ComponentGetSerializedProperties(name: objectName, componentType: componentType, includeHidden: includeHidden, limit: limit)));
            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            return (JArray)json["properties"];
        }

        private static JObject Entry(JArray properties, string propertyPath)
        {
            var entry = properties.Cast<JObject>().FirstOrDefault(e => (string)e["propertyPath"] == propertyPath);
            Assert.That(entry, Is.Not.Null, $"No '{propertyPath}' among {string.Join(", ", properties.Select(e => (string)e["propertyPath"]))}");
            return entry;
        }
    }
}

// Producer:Betsy
