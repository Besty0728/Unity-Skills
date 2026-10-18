using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Tests.Fixtures;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Built-in skills declare entityId themselves, so an id reaches exactly the object it names on every Unity version.
    /// Same-name siblings share a name and a path, and only the id tells them apart; the router's synthetic entityId had to
    /// refuse them on Unity 6000.4+, where there is no legacy instanceId to forward. Each case acts on the second of two
    /// same-name objects and checks that the first stayed as it was. Secondary locators (component_copy's source and
    /// target, a reference member, a UI parent) and batch items take their own ids the same way.
    /// </summary>
    [TestFixture]
    public class NativeEntityIdLocatorTests
    {
        private ModePreferenceSnapshot _modePreferences;
        private SurfaceProfileKind _savedProfile;

        [OneTimeSetUp]
        public void OneTimeSetUp() => _modePreferences = ModePreferenceSnapshot.Capture();

        [OneTimeTearDown]
        public void OneTimeTearDown() => _modePreferences.Restore();

        [SetUp]
        public void SetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = false;
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
            GameObjectFinder.DrainResolutionNotes();
        }

        [Test]
        public void ComponentAdd_ReachesTheSecondOfTwoSameNameSiblings()
        {
            var (first, second) = CreateTwins();

            AssertSuccess(Execute("component_add", new JObject { ["entityId"] = Id(second), ["componentType"] = "BoxCollider" }));

            Assert.That(second.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(first.GetComponent<BoxCollider>(), Is.Null);
        }

        [Test]
        public void LightSetEnabled_ReachesTheSecondOfTwoSameNameSiblings()
        {
            var (first, second) = CreateTwins();
            first.AddComponent<Light>();
            second.AddComponent<Light>();

            AssertSuccess(Execute("light_set_enabled", new JObject { ["entityId"] = Id(second), ["enabled"] = false }));

            Assert.That(second.GetComponent<Light>().enabled, Is.False);
            Assert.That(first.GetComponent<Light>().enabled, Is.True);
        }

        [Test]
        public void CameraGetProperties_ReadsTheSecondOfTwoSameNameSiblings()
        {
            var (first, second) = CreateTwins();
            first.AddComponent<Camera>().fieldOfView = 40f;
            second.AddComponent<Camera>().fieldOfView = 70f;

            var response = Execute("camera_get_properties", new JObject { ["entityId"] = Id(second) });

            AssertSuccess(response);
            Assert.That(response["result"]?["fieldOfView"]?.Value<float>(), Is.EqualTo(70f));
        }

        [Test]
        public void UiSetRect_ReachesTheSecondOfTwoSameNameSiblings()
        {
            var root = Create("LocatorRoot");
            var first = CreateRect("Twin", root);
            var second = CreateRect("Twin", root);
            GameObjectFinder.InvalidateCache();
            var firstSize = first.GetComponent<RectTransform>().sizeDelta;

            AssertSuccess(Execute("ui_set_rect", new JObject { ["entityId"] = Id(second), ["width"] = 123 }));

            Assert.That(second.GetComponent<RectTransform>().sizeDelta.x, Is.EqualTo(123f));
            Assert.That(first.GetComponent<RectTransform>().sizeDelta, Is.EqualTo(firstSize));
        }

        [Test]
        public void TerrainGetInfo_EntityIdAndPath_PickAmongSameNameTerrains()
        {
            var small = new TerrainData { size = new Vector3(10f, 1f, 10f) };
            var large = new TerrainData { size = new Vector3(20f, 1f, 20f) };
            var first = Create("Ground", Create("A"));
            var second = Create("Ground", Create("B"));
            try
            {
                first.AddComponent<Terrain>().terrainData = small;
                second.AddComponent<Terrain>().terrainData = large;
                GameObjectFinder.InvalidateCache();

                var byId = Execute("terrain_get_info", new JObject { ["entityId"] = Id(second) });
                var byPath = Execute("terrain_get_info", new JObject { ["path"] = "A/Ground" });

                AssertSuccess(byId);
                Assert.That(byId["result"]?["size"]?["width"]?.Value<float>(), Is.EqualTo(20f));
                AssertSuccess(byPath);
                Assert.That(byPath["result"]?["size"]?["width"]?.Value<float>(), Is.EqualTo(10f));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(small);
                Object.DestroyImmediate(large);
            }
        }

        [Test]
        public void ComponentCopy_SourceAndTargetIds_PickAmongSameNameObjects()
        {
            var source = Create("Source");
            source.AddComponent<BoxCollider>().size = new Vector3(2f, 3f, 4f);
            var (first, second) = CreateTwins();

            AssertSuccess(Execute("component_copy", new JObject
            {
                ["sourceEntityId"] = Id(source), ["targetEntityId"] = Id(second), ["componentType"] = "BoxCollider"
            }));

            Assert.That(second.GetComponent<BoxCollider>(), Is.Not.Null);
            Assert.That(second.GetComponent<BoxCollider>().size, Is.EqualTo(new Vector3(2f, 3f, 4f)));
            Assert.That(first.GetComponent<BoxCollider>(), Is.Null);
        }

        [Test]
        public void ComponentSetProperty_ReferenceEntityId_OutranksTheReferenceName()
        {
            var joint = Create("Holder").AddComponent<HingeJoint>();
            var (first, second) = CreateTwins();
            first.AddComponent<Rigidbody>();
            var wanted = second.AddComponent<Rigidbody>();

            AssertSuccess(Execute("component_set_property", new JObject
            {
                ["name"] = "Holder", ["componentType"] = "HingeJoint", ["propertyName"] = "connectedBody",
                ["referenceName"] = "Twin", ["referenceEntityId"] = Id(second)
            }));

            Assert.That(joint.connectedBody, Is.SameAs(wanted));
        }

        [Test]
        public void ComponentAddBatch_ItemEntityId_ReachesItsObject()
        {
            var (first, second) = CreateTwins();

            var response = Execute("component_add_batch", new JObject
            {
                ["items"] = new JArray { new JObject { ["entityId"] = Id(second), ["componentType"] = "SphereCollider" } }
            });

            AssertSuccess(response);
            Assert.That(response["result"]?["warnings"], Is.Null, "entityId is an item field, not an unknown key.");
            Assert.That(second.GetComponent<SphereCollider>(), Is.Not.Null);
            Assert.That(first.GetComponent<SphereCollider>(), Is.Null);
        }

        [Test]
        public void ComponentAddBatch_StaleItemEntityId_ChangesNothing()
        {
            var survivor = Create("Crate");
            var staleId = StaleId();

            var response = Execute("component_add_batch", new JObject
            {
                ["items"] = new JArray { new JObject { ["entityId"] = staleId, ["name"] = "Crate", ["componentType"] = "SphereCollider" } }
            });

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"), response.ToString(Formatting.None));
            Assert.That(survivor.GetComponent<SphereCollider>(), Is.Null, "A stale id must not land on the same-name object.");
        }

        [Test]
        public void UiCreatePanel_ParentEntityId_PicksThatParent()
        {
            var first = Create("Menu");
            first.AddComponent<Canvas>();
            var second = Create("Menu");
            second.AddComponent<Canvas>();
            GameObjectFinder.InvalidateCache();

            AssertSuccess(Execute("ui_create_panel", new JObject { ["name"] = "PanelProbe", ["parent"] = "Menu", ["parentEntityId"] = Id(second) }));

            Assert.That(second.transform.Find("PanelProbe"), Is.Not.Null);
            Assert.That(first.transform.Find("PanelProbe"), Is.Null);
        }

        [Test]
        public void UiCreatePanel_StaleParentEntityId_CreatesNothing()
        {
            var canvas = Create("Menu");
            canvas.AddComponent<Canvas>();
            var staleId = StaleId();

            var response = Execute("ui_create_panel", new JObject { ["name"] = "PanelProbe", ["parentEntityId"] = staleId });

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"), response.ToString(Formatting.None));
            Assert.That(canvas.transform.childCount, Is.EqualTo(0), "A stale parent id must not fall back to the first Canvas.");
        }

        [Test]
        public void EventAddListener_SourceEntityId_PicksTheSecondOfTwoSameNameEmitters()
        {
            var (first, second) = CreateTwins();
            var firstProbe = first.AddComponent<EventProbe>();
            var secondProbe = second.AddComponent<EventProbe>();
            var receiver = Create("Receiver").AddComponent<EventProbe>();

            AssertSuccess(Execute("event_add_listener", new JObject
            {
                ["entityId"] = Id(second), ["componentName"] = nameof(EventProbe), ["eventName"] = nameof(EventProbe.onFire),
                ["targetEntityId"] = Id(receiver), ["targetComponentName"] = nameof(EventProbe), ["methodName"] = nameof(EventProbe.Ping)
            }));

            Assert.That(secondProbe.onFire.GetPersistentEventCount(), Is.EqualTo(1));
            Assert.That(firstProbe.onFire.GetPersistentEventCount(), Is.EqualTo(0));
        }

        [Test]
        public void UitkSetDocument_ReachesTheSecondOfTwoSameNameSiblings()
        {
            var (first, second) = CreateTwins();
            var firstDocument = first.AddComponent<UnityEngine.UIElements.UIDocument>();
            var secondDocument = second.AddComponent<UnityEngine.UIElements.UIDocument>();

            AssertSuccess(Execute("uitk_set_document", new JObject { ["entityId"] = Id(second), ["sortOrder"] = 7 }));

            Assert.That(secondDocument.sortingOrder, Is.EqualTo(7f));
            Assert.That(firstDocument.sortingOrder, Is.EqualTo(0f));
        }

        [Test]
        public void UitkCreateDocument_ParentEntityId_PicksThatParent()
        {
            var (first, second) = CreateTwins();

            AssertSuccess(Execute("uitk_create_document", new JObject { ["name"] = "DocumentProbe", ["parentEntityId"] = Id(second) }));

            Assert.That(second.transform.Find("DocumentProbe"), Is.Not.Null);
            Assert.That(first.transform.Find("DocumentProbe"), Is.Null);
        }

        [Test]
        public void EntityId_OutranksAConflictingNameAndPath()
        {
            var target = Create("Solo", Create("B"));
            Create("Solo", Create("A"));
            GameObjectFinder.InvalidateCache();

            var response = Execute("component_list", new JObject { ["entityId"] = Id(target), ["name"] = "Solo", ["path"] = "A/Solo" });

            AssertSuccess(response);
            Assert.That(response["result"]?["entityId"]?.ToString(), Is.EqualTo(Id(target)));
            Assert.That(response["resolutionNotes"]?.Select(note => note.ToString()), Has.Some.Contains("outranks path 'A/Solo'"));
        }

        [Test]
        public void NativeEntityId_LooksLikeTheSyntheticOneInTheSchema()
        {
            var schema = JObject.Parse(SkillRouter.GetFilteredSchema("?names=component_add"));
            var entry = ((JArray)schema["skills"]).Single()["parameters"].Single(parameter => parameter["name"]?.ToString() == "entityId");

            Assert.That(entry["type"]?.ToString(), Is.EqualTo("string"));
            Assert.That(entry["required"]?.Value<bool>(), Is.False);
            Assert.That(entry["description"], Is.Null);
        }

        private static (GameObject first, GameObject second) CreateTwins()
        {
            var root = Create("LocatorRoot");
            var first = Create("Twin", root);
            var second = Create("Twin", root);
            GameObjectFinder.InvalidateCache();
            return (first, second);
        }

        private static GameObject Create(string name, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null)
                go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static GameObject CreateRect(string name, GameObject parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        /// <summary>The id of an object that existed and is gone.</summary>
        private static string StaleId()
        {
            var doomed = new GameObject("Doomed");
            var id = Id(doomed);
            Object.DestroyImmediate(doomed);
            GameObjectFinder.InvalidateCache();
            return id;
        }

        private static string Id(Object obj) => UnityObjectIdUtility.GetEntityId(obj);

        private static JObject Execute(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));

        private static void AssertSuccess(JObject response) =>
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
    }
}

// Producer:Betsy
