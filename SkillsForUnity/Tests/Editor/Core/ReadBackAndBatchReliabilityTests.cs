using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Execution-side precision: writes answer with state read back from the Editor, creates honour the coordinate
    /// space they were given, a batch can parent to an item created earlier in the same call, a failed batch leaves
    /// nothing behind and says so, unknown batch item keys are reported, and the finder refuses to guess between
    /// several loose name matches.
    ///
    /// <para>Skills are invoked directly where the point is the skill's own behaviour, so those assertions don't
    /// depend on planner or permission-gate state; the router-level tests say so in their names.</para>
    /// </summary>
    [TestFixture]
    public class ReadBackAndBatchReliabilityTests
    {
        private const float Tolerance = 1e-3f;
        private const string ProbeFolder = "Assets/__UnitySkillsReadBackProbe__";

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        private class ProbeItem
        {
            public int value;
            public string parentPath { get; set; }
        }

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            GameObjectFinder.DrainResolutionNotes();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.DeleteAsset(ProbeFolder);

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
            GameObjectFinder.DrainResolutionNotes();
        }

        // ---------- gameobject_create: spaces and read-back ----------

        [Test]
        public void Create_UnderTransformedParent_ReportsWorldPositionAndLocalPosition()
        {
            var parent = CreateParent("RB_Parent", new Vector3(10, 0, 0), new Vector3(0, 90, 0), new Vector3(2, 2, 2));

            var json = ToJson(GameObjectSkills.GameObjectCreate("RB_Child", parentName: "RB_Parent", x: 1, y: 2, z: 3));

            var child = parent.transform.Find("RB_Child");
            Assert.That(child, Is.Not.Null, $"The child was not created under its parent: {json.ToString(Formatting.None)}");
            Assert.That(Vector3.Distance(child.position, new Vector3(1, 2, 3)), Is.GreaterThan(1f),
                "Precondition: the parent transform must move the child away from its local coordinates.");

            AssertVector(json["localPosition"], new Vector3(1, 2, 3), "localPosition");
            AssertVector(json["position"], child.position, "position must be the world position read back from the Transform");
            AssertVector(json["rotation"], child.eulerAngles, "rotation");
            AssertVector(json["scale"], child.localScale, "scale");
            Assert.That(json["parentPath"]?.ToString(), Is.EqualTo("RB_Parent"));
            Assert.That(json["path"]?.ToString(), Is.EqualTo("RB_Parent/RB_Child"));
        }

        [Test]
        public void Create_WorldSpace_UnderRotatedScaledParent_LandsOnTheWorldCoordinates()
        {
            var parent = CreateParent("RB_Parent", new Vector3(5, 1, -2), new Vector3(0, 90, 0), new Vector3(2, 2, 2));

            var json = ToJson(GameObjectSkills.GameObjectCreate("RB_WorldChild", parentName: "RB_Parent",
                x: 1, y: 2, z: 3, rotY: 45, space: "world"));

            var child = parent.transform.Find("RB_WorldChild");
            Assert.That(child, Is.Not.Null, $"The child was not parented: {json.ToString(Formatting.None)}");
            Assert.That(Vector3.Distance(child.position, new Vector3(1, 2, 3)), Is.LessThan(Tolerance),
                $"space=world must place the object at the given world coordinates, got {child.position}.");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(child.eulerAngles.y, 45f)), Is.LessThan(Tolerance),
                $"space=world must apply rotY as a world rotation, got {child.eulerAngles}.");
            AssertVector(json["position"], new Vector3(1, 2, 3), "position");
        }

        [Test]
        public void Create_InvalidSpace_IsRejectedBeforeAnythingIsCreated()
        {
            int before = CountSceneObjects();

            var json = ToJson(GameObjectSkills.GameObjectCreate("RB_BadSpace", space: "globe"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["validValues"]?.Values<string>(), Is.EquivalentTo(new[] { "local", "world" }));
            Assert.That(CountSceneObjects(), Is.EqualTo(before), "A rejected create must not leave an object behind.");
        }

        [Test]
        public void Create_ThroughRouter_ReturnsReadBackPositions()
        {
            CreateParent("RB_RouterParent", new Vector3(0, 5, 0), Vector3.zero, Vector3.one);

            var response = JObject.Parse(SkillRouter.Execute("gameobject_create",
                "{\"name\":\"RB_RouterChild\",\"parentName\":\"RB_RouterParent\",\"y\":1}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            AssertVector(response["result"]?["position"], new Vector3(0, 6, 0), "position (world)");
            AssertVector(response["result"]?["localPosition"], new Vector3(0, 1, 0), "localPosition");
        }

        // ---------- gameobject_create_batch: forward references, spaces, rollback ----------

        [Test]
        public void CreateBatch_LaterItemsParentToEarlierItems_EvenWithAWarmCacheAndALookalike()
        {
            // "RoomLight" is what the old substring fallback bound "Room" to when the request cache was stale.
            var lookalike = new GameObject("RoomLight");
            GameObjectFinder.InvalidateCache();
            // The router's validation builds the request cache before item 0 runs; reproduce that.
            GameObjectFinder.FindByPath("__warm_the_cache__");

            var json = ToJson(GameObjectSkills.GameObjectCreateBatch(
                "[{\"name\":\"Room\"},{\"name\":\"Wall\",\"parentName\":\"Room\",\"x\":2},{\"name\":\"Door\",\"parentPath\":\"Room/Wall\"}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["successCount"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(GameObject.Find("Room/Wall/Door"), Is.Not.Null, "The three items must form one hierarchy.");
            Assert.That(lookalike.transform.childCount, Is.Zero, "'Room' must not bind to the lookalike 'RoomLight'.");

            var results = (JArray)json["results"];
            Assert.That(results[1]["path"]?.ToString(), Is.EqualTo("Room/Wall"));
            Assert.That(results[1]["parentPath"]?.ToString(), Is.EqualTo("Room"));
            AssertVector(results[1]["localPosition"], new Vector3(2, 0, 0), "Wall localPosition");
        }

        [Test]
        public void CreateBatch_ThroughRouter_ForwardParentReferenceExecutes()
        {
            // Covers both halves: the planner must accept a parent created earlier in the same call, and execution must bind it.
            var response = JObject.Parse(SkillRouter.Execute("gameobject_create_batch",
                "{\"items\":[{\"name\":\"RB_Hall\"},{\"name\":\"RB_Lamp\",\"parentName\":\"RB_Hall\"}]}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(GameObject.Find("RB_Hall/RB_Lamp"), Is.Not.Null);
        }

        [Test]
        public void CreateBatch_LocalSpaceRotation_IsAppliedAsLocalEulerAngles()
        {
            var parent = CreateParent("RB_Parent", Vector3.zero, new Vector3(0, 90, 0), Vector3.one);

            ToJson(GameObjectSkills.GameObjectCreateBatch("[{\"name\":\"RB_Rot\",\"parentName\":\"RB_Parent\",\"rotY\":10}]"));

            var child = parent.transform.Find("RB_Rot");
            Assert.That(child, Is.Not.Null);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(child.localEulerAngles.y, 10f)), Is.LessThan(Tolerance),
                $"The default local space must set localEulerAngles, got {child.localEulerAngles}.");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(child.eulerAngles.y, 100f)), Is.LessThan(Tolerance));
        }

        [Test]
        public void CreateBatch_WorldSpaceExplicitZeroRotation_IsApplied()
        {
            var parent = CreateParent("RB_Parent", Vector3.zero, new Vector3(0, 90, 0), Vector3.one);

            ToJson(GameObjectSkills.GameObjectCreateBatch(
                "[{\"name\":\"RB_Zero\",\"parentName\":\"RB_Parent\",\"space\":\"world\",\"rotX\":0,\"rotY\":0,\"rotZ\":0}]"));

            var child = parent.transform.Find("RB_Zero");
            Assert.That(child, Is.Not.Null);
            Assert.That(Quaternion.Angle(child.rotation, Quaternion.identity), Is.LessThan(Tolerance),
                "An explicit zero world rotation must be applied, not skipped as 'no rotation'.");
        }

        [Test]
        public void CreateBatch_UnresolvableParent_CreatesNothingAndMarksTheOtherItemsReverted()
        {
            int before = CountSceneObjects();

            var json = ToJson(GameObjectSkills.GameObjectCreateBatch(
                "[{\"name\":\"RB_Ok\"},{\"name\":\"RB_Orphan\",\"parentName\":\"RB_NoSuchParent_7f3\"}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.False);
            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["revertedCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["successCount"]?.Value<int>(), Is.Zero, "Rolled-back items are not successes.");
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1));

            var results = (JArray)json["results"];
            Assert.That(results[0]["success"]?.Value<bool>(), Is.False);
            Assert.That(results[0]["reverted"]?.Value<bool>(), Is.True);
            Assert.That(results[1]["error"]?.ToString(), Does.Contain("RB_NoSuchParent_7f3"));

            Assert.That(CountSceneObjects(), Is.EqualTo(before), "A failed batch must leave no object behind.");
            Assert.That(GameObject.Find("RB_Ok"), Is.Null);
            Assert.That(GameObject.Find("RB_Orphan"), Is.Null);
        }

        // ---------- set_transform UI branch, component/material read-back ----------

        [Test]
        public void SetTransform_OnUiObject_ReturnsEveryDeclaredOutput()
        {
            var canvas = new GameObject("RB_Canvas", typeof(Canvas));
            var ui = new GameObject("RB_Ui", typeof(RectTransform));
            ui.transform.SetParent(canvas.transform, false);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(GameObjectSkills.GameObjectSetTransform(name: "RB_Ui", anchoredPosX: 5));

            Assert.That(json["isUI"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assume.That(SkillRouter.TryGetSkill("gameobject_set_transform", out var skill), Is.True);
            foreach (var key in skill.Outputs.Where(key => key != "entityId"))
                Assert.That(json[key], Is.Not.Null, $"The UI branch omits declared output '{key}'.");
            AssertVector(json["position"], ui.transform.position, "position");
        }

        [Test]
        public void ComponentSetProperty_ReportsTheStoredValueAndTheResolvedNames()
        {
            var go = new GameObject("RB_Prop");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetProperty(name: "RB_Prop", componentType: "Transform",
                propertyName: "LocalEulerAngles", value: "0,370,0"));

            Assert.That(json["error"], Is.Null, json.ToString(Formatting.None));
            Assert.That(json["component"]?.ToString(), Is.EqualTo("Transform"));
            Assert.That(json["property"]?.ToString(), Is.EqualTo("localEulerAngles"), "property must be the resolved member name.");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(ParseVector(json["valueSet"]).y, 10f)), Is.LessThan(Tolerance),
                $"valueSet must be read back (euler angles wrap 370 -> 10), got {json["valueSet"]}.");
            Assert.That(json["valueRequested"]?.ToString(), Is.EqualTo("(0, 370, 0)"),
                "valueRequested must appear when the stored value differs from the request.");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(go.transform.localEulerAngles.y, 10f)), Is.LessThan(Tolerance));
        }

        [Test]
        public void ComponentSetProperty_StoredEqualsRequest_OmitsValueRequested()
        {
            new GameObject("RB_Prop");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetProperty(name: "RB_Prop", componentType: "Transform",
                propertyName: "localPosition", value: "1,2,3"));

            Assert.That(json["valueSet"]?.ToString(), Is.EqualTo("(1, 2, 3)"), json.ToString(Formatting.None));
            Assert.That(json["valueRequested"], Is.Null);
        }

        [Test]
        public void ComponentSetProperty_MissingPropertyName_IsAStructuredMissingParam()
        {
            new GameObject("RB_Prop");
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentSetProperty(name: "RB_Prop", componentType: "Transform"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("propertyName"));
        }

        [Test]
        public void MaterialAssign_ReportsTheMaterialTheRendererNowUses()
        {
            var shader = Shader.Find(ProjectSkills.GetDefaultShaderName());
            Assume.That(shader, Is.Not.Null, "The project's default shader did not resolve.");
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
                AssetDatabase.CreateFolder("Assets", "__UnitySkillsReadBackProbe__");
            var materialPath = ProbeFolder + "/probe.mat";
            AssetDatabase.CreateAsset(new Material(shader), materialPath);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "RB_Cube";
            GameObjectFinder.InvalidateCache();

            var json = ToJson(MaterialSkills.MaterialAssign(name: "RB_Cube", materialPath: materialPath));

            var assigned = cube.GetComponent<Renderer>().sharedMaterial;
            Assert.That(assigned, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Material>(materialPath)), json.ToString(Formatting.None));
            Assert.That(json["material"]?.ToString(), Is.EqualTo(AssetDatabase.GetAssetPath(assigned)));
            Assert.That(json["material"]?.ToString(), Is.EqualTo(materialPath));
            Assert.That(json["materialName"]?.ToString(), Is.EqualTo(assigned.name));
        }

        // ---------- BatchExecutor ----------

        [Test]
        public void BatchExecutor_UnknownItemField_IsReportedWithTheClosestFieldName()
        {
            var json = ToJson(BatchExecutor.Execute<ProbeItem>(
                "[{\"value\":1,\"parentpathh\":\"A\"},{\"VALUE\":2,\"parentpathh\":\"B\",\"zzz\":0}]",
                item => new { success = true }));

            Assert.That(json["success"]?.Value<bool>(), Is.True, "Unknown fields warn; they must not fail the call.");
            var warnings = json["warnings"]?.Values<string>().ToList();
            Assert.That(warnings, Is.Not.Null, json.ToString(Formatting.None));
            Assert.That(warnings, Does.Contain("items[0,1]: unknown field 'parentpathh' ignored (did you mean 'parentPath'?)"));
            Assert.That(warnings, Does.Contain("items[1]: unknown field 'zzz' ignored"));
            Assert.That(warnings.Any(w => w.Contains("'VALUE'")), Is.False,
                "Keys that differ only in case bind case-insensitively and are not unknown.");
        }

        [Test]
        public void BatchExecutor_KnownFieldsOnly_CarryNoWarningsKey()
        {
            var json = ToJson(BatchExecutor.Execute<ProbeItem>("[{\"value\":1,\"parentPath\":\"A\"}]", item => new { success = true }));

            Assert.That(json.ContainsKey("warnings"), Is.False, json.ToString(Formatting.None));
        }

        [Test]
        public void BatchExecutor_AtomicBatch_RevertsEarlierItemsWhenOneFails()
        {
            var json = ToJson(BatchExecutor.Execute<ProbeItem>("[{\"value\":1},{\"value\":2},{\"value\":3}]", item =>
            {
                if (item.value == 2)
                    return new { error = "boom", target = "2" };
                var go = new GameObject("RB_Atomic_" + item.value);
                Undo.RegisterCreatedObjectUndo(go, "probe");
                return new { success = true, name = go.name };
            }, atomic: true));

            Assert.That(GameObject.Find("RB_Atomic_1"), Is.Null, "An atomic batch must undo the items before the failure.");
            Assert.That(GameObject.Find("RB_Atomic_3"), Is.Null, "An atomic batch must undo the items after the failure.");
            Assert.That(json["rolledBack"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["revertedCount"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["successCount"]?.Value<int>(), Is.Zero);
            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1));

            var results = (JArray)json["results"];
            Assert.That(results[0]["reverted"]?.Value<bool>(), Is.True);
            Assert.That(results[0]["success"]?.Value<bool>(), Is.False);
            Assert.That(results[0]["name"]?.ToString(), Is.EqualTo("RB_Atomic_1"), "A reverted item keeps its original fields.");
            Assert.That(results[1]["error"]?.ToString(), Is.EqualTo("boom"));
        }

        // ---------- GameObjectFinder: ambiguity and resolution notes ----------

        [Test]
        public void FindOrError_AmbiguousSubstring_ReturnsEveryCandidateInsteadOfPicking()
        {
            new GameObject("RoomLight");
            new GameObject("RoomDoor");

            var (go, error) = GameObjectFinder.FindOrError("Room");

            Assert.That(go, Is.Null, "An ambiguous loose match must not bind any object.");
            var json = ToJson(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            var paths = json["candidates"]?.Select(candidate => candidate["path"]?.ToString()).ToList();
            Assert.That(paths, Is.EquivalentTo(new[] { "RoomLight", "RoomDoor" }), json.ToString(Formatting.None));
            Assert.That(json["error"]?.ToString(), Does.Contain("RoomLight").And.Contain("RoomDoor"));
        }

        [Test]
        public void Find_AmbiguousSubstring_KeepsAMatchButRecordsANote()
        {
            new GameObject("RoomLight");
            new GameObject("RoomDoor");

            var go = GameObjectFinder.Find("Room");

            Assert.That(go, Is.Not.Null);
            var notes = GameObjectFinder.DrainResolutionNotes();
            Assert.That(notes.Any(note => note.Contains("2 objects contain it")), Is.True, string.Join(" | ", notes));
        }

        [Test]
        public void FindOrError_UniqueSubstring_ResolvesAndNotesTheStrategy()
        {
            var light = new GameObject("RoomLight");

            var (go, error) = GameObjectFinder.FindOrError("Room");

            Assert.That(error, Is.Null);
            Assert.That(go, Is.EqualTo(light));
            var notes = GameObjectFinder.DrainResolutionNotes();
            Assert.That(notes.Any(note => note.Contains("substring") && note.Contains("RoomLight")), Is.True, string.Join(" | ", notes));
        }

        [Test]
        public void FindOrError_WholeWordMatch_WinsOverSubstringMatches()
        {
            var wholeWord = new GameObject("Room_A");
            new GameObject("Bathroom");

            var (go, error) = GameObjectFinder.FindOrError("Room");

            Assert.That(error, Is.Null, "One whole-word match is unambiguous even though two names contain the text.");
            Assert.That(go, Is.EqualTo(wholeWord));
        }

        [Test]
        public void FindOrError_CaseInsensitiveMatch_IsNoted()
        {
            var cube = new GameObject("Cube");

            var (go, _) = GameObjectFinder.FindOrError("cube");

            Assert.That(go, Is.EqualTo(cube));
            var notes = GameObjectFinder.DrainResolutionNotes();
            Assert.That(notes.Any(note => note.Contains("case-insensitively")), Is.True, string.Join(" | ", notes));
        }

        [Test]
        public void FindOrError_PrefersTheSameCaseSpelling_WhenBothExist()
        {
            new GameObject("probe");
            var upper = new GameObject("Probe");

            var (go, _) = GameObjectFinder.FindOrError("Probe");

            Assert.That(go, Is.EqualTo(upper));
            var notes = GameObjectFinder.DrainResolutionNotes();
            Assert.That(notes.Any(note => note.Contains("matches 2 objects")), Is.True, string.Join(" | ", notes));
        }

        [Test]
        public void FindOrError_UniqueExactName_RecordsNoNote()
        {
            new GameObject("RB_Unique_42");

            var (go, _) = GameObjectFinder.FindOrError("RB_Unique_42");

            Assert.That(go, Is.Not.Null);
            Assert.That(GameObjectFinder.DrainResolutionNotes(), Is.Empty);
        }

        [Test]
        public void DrainResolutionNotes_ClearsWhatItReturns()
        {
            new GameObject("Cube");
            GameObjectFinder.FindOrError("cube");

            Assert.That(GameObjectFinder.DrainResolutionNotes(), Is.Not.Empty);
            Assert.That(GameObjectFinder.DrainResolutionNotes(), Is.Empty);
        }

        // ---------- package_check evidence ----------

        [Test]
        public void PackageCheck_ListsTheSkillsThatNeedThePackage()
        {
            var json = ToJson(PackageSkills.PackageCheck("com.unity.probuilder"));

            Assert.That(json["error"], Is.Null, json.ToString(Formatting.None));
            Assert.That(json["cacheReady"], Is.Not.Null);
            var dependents = json["dependentSkills"]?.Values<string>().ToList();
            Assert.That(dependents, Does.Contain("probuilder_create_shape").And.Contain("probuilder_extrude_faces").And.Contain("probuilder_get_info"),
                "Every ProBuilder skill declares the package, so each one must be listed.");
        }

        [Test]
        public void PackageCheck_MisspelledId_SuggestsTheInstalledPackage()
        {
            var json = ToJson(PackageSkills.PackageCheck("com.unity.nuget.newtonsoft-jsn"));
            Assume.That(json["cacheReady"]?.Value<bool>(), Is.True, "The package list cache is not loaded yet.");

            Assert.That(json["installed"]?.Value<bool>(), Is.False);
            Assert.That(json["checkedPackages"]?.Value<int>(), Is.GreaterThan(0));
            Assert.That(json["similarInstalled"]?.Values<string>(), Does.Contain("com.unity.nuget.newtonsoft-json"),
                json.ToString(Formatting.None));
        }

        // ---------- helpers ----------

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        private static GameObject CreateParent(string name, Vector3 position, Vector3 euler, Vector3 scale)
        {
            var parent = new GameObject(name);
            parent.transform.position = position;
            parent.transform.eulerAngles = euler;
            parent.transform.localScale = scale;
            GameObjectFinder.InvalidateCache();
            return parent;
        }

        private static int CountSceneObjects()
        {
            return SceneManager.GetActiveScene().GetRootGameObjects()
                .Sum(root => root.GetComponentsInChildren<Transform>(true).Length);
        }

        private static void AssertVector(JToken token, Vector3 expected, string label)
        {
            Assert.That(token, Is.Not.Null, $"{label} is missing.");
            var actual = new Vector3(token["x"].Value<float>(), token["y"].Value<float>(), token["z"].Value<float>());
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), $"{label}: expected {expected}, got {actual}.");
        }

        /// <summary>Parses the "(x, y, z)" display form that valueSet uses.</summary>
        private static Vector3 ParseVector(JToken token)
        {
            var parts = token.ToString().Trim('(', ')').Split(',')
                .Select(part => float.Parse(part.Trim(), CultureInfo.InvariantCulture))
                .ToArray();
            return new Vector3(parts[0], parts[1], parts[2]);
        }
    }
}

// Producer:Betsy
