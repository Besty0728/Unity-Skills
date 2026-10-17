using System.Collections.Generic;
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
    /// GameObjectFinder walked the scenes with a stack fed in Hierarchy order, so it ran bottom-up: a name several objects
    /// share bound the bottom-most one, the last loaded scene beat the first, and a path cache hit disagreed with the path
    /// walk. It now walks in Hierarchy order, an exact-case path beats one that matches only ignoring case, and an entityId
    /// or instanceId that names nothing is a miss instead of a fallback to the path or name, which could bind a same-name object.
    /// </summary>
    [TestFixture]
    public class GameObjectFinderOrderTests
    {
        private const string SceneFolder = "Assets/Temp/FinderOrderScenes";

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
            GameObjectFinder.DrainResolutionNotes();
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
        public void SameNameRoots_BindTheTopMost()
        {
            var top = Create("Twin");
            Create("Twin");

            var (go, error) = GameObjectFinder.FindOrError(name: "Twin");

            Assert.That(error, Is.Null);
            Assert.That(go, Is.EqualTo(top));
            Assert.That(GameObjectFinder.DrainResolutionNotes(), Has.Some.Contains("matches 2 objects"));
        }

        [Test]
        public void SameNameSiblings_BindTheFirst_ByNameAndByPath()
        {
            var shelf = Create("Shelf");
            var first = Create("Book", shelf);
            Create("Book", shelf);

            Assert.That(GameObjectFinder.FindOrError(name: "Book").go, Is.EqualTo(first));
            Assert.That(GameObjectFinder.FindByPath("Shelf/Book"), Is.EqualTo(first));
        }

        [Test]
        public void Cousins_FollowHierarchyOrder()
        {
            var a = Create("A");
            var b = Create("B");
            var underA = Create("X", a);
            Create("X", b);

            Assert.That(GameObjectFinder.FindOrError(name: "X").go, Is.EqualTo(underA));
        }

        [Test]
        public void Ancestor_BeatsASameNameDescendant()
        {
            var outer = Create("Node");
            Create("Node", outer);

            Assert.That(GameObjectFinder.FindOrError(name: "Node").go, Is.EqualTo(outer));
        }

        [Test]
        public void ComponentLookup_TakesTheTopMost()
        {
            var top = Create("Upper");
            top.AddComponent<BoxCollider>();
            Create("Lower").AddComponent<BoxCollider>();

            Assert.That(GameObjectFinder.Find(componentType: "BoxCollider"), Is.EqualTo(top));
        }

        [Test]
        public void SceneObjects_AreListedInHierarchyPreOrder()
        {
            var a = Create("A");
            Create("A1", a);
            var a2 = Create("A2", a);
            Create("A2a", a2);
            var b = Create("B");
            Create("B1", b);
            GameObjectFinder.InvalidateCache();

            var expected = new List<GameObject>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                AddPreOrder(root.transform, expected);

            Assert.That(GameObjectFinder.GetSceneObjects().ToList(), Is.EqualTo(expected));
        }

        [Test]
        public void ExactCasePath_BeatsOneThatMatchesOnlyIgnoringCase()
        {
            var root = Create("Root");
            var lower = Create("cube", root);
            var exact = Create("Cube", root);

            Assert.That(GameObjectFinder.FindByPath("Root/Cube"), Is.EqualTo(exact));
            Assert.That(GameObjectFinder.FindByPath("Root/cube"), Is.EqualTo(lower));
            Assert.That(GameObjectFinder.FindByPath("ROOT/CUBE"), Is.EqualTo(lower), "Ignoring case, the first in Hierarchy order.");
        }

        [Test]
        public void PathWalk_AgreesWithTheCache()
        {
            var shelf = Create("Shelf");
            var cachedFirst = Create("Book", shelf);
            Create("Book", shelf);
            GameObjectFinder.InvalidateCache();
            Assert.That(GameObjectFinder.FindByPath("Shelf/Book"), Is.EqualTo(cachedFirst));

            // Created after the cache was built, so only the path walk can reach them.
            var late = Create("Late");
            var walkedFirst = Create("Leaf", late);
            Create("Leaf", late);

            Assert.That(GameObjectFinder.FindByPath("Late/Leaf"), Is.EqualTo(walkedFirst));
        }

        [Test]
        public void FirstLoadedScene_Wins_AndAScenePrefixedPathPicksTheOther()
        {
            var createdTemp = EnsureSceneFolder();
            try
            {
                var mainScene = SceneManager.GetActiveScene();
                var first = Create("Shared");
                // An additive scene can only be created once the untitled main scene has a file.
                Assert.That(EditorSceneManager.SaveScene(mainScene, SceneFolder + "/FinderOrderMain.unity"), Is.True);
                var extra = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.That(EditorSceneManager.SaveScene(extra, SceneFolder + "/FinderOrderExtra.unity"), Is.True);
                var second = new GameObject("Shared");
                SceneManager.MoveGameObjectToScene(second, extra);
                GameObjectFinder.InvalidateCache();

                Assert.That(GameObjectFinder.FindOrError(name: "Shared").go, Is.EqualTo(first));
                Assert.That(GameObjectFinder.FindByPath("Shared"), Is.EqualTo(first));
                Assert.That(GameObjectFinder.FindByPath(extra.name + "/Shared"), Is.EqualTo(second));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                DeleteSceneFolder(createdTemp);
            }
        }

        [Test]
        public void GameObjectFind_ListsObjectsTopDown()
        {
            Create("OrderProbe_A");
            var b = Create("OrderProbe_B");
            Create("OrderProbe_B1", b);
            Create("OrderProbe_C");
            GameObjectFinder.InvalidateCache();

            var response = Execute("gameobject_find", new JObject { ["name"] = "OrderProbe_" });

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response["result"]?["objects"]?.Select(item => item["name"]?.ToString()),
                Is.EqualTo(new[] { "OrderProbe_A", "OrderProbe_B", "OrderProbe_B1", "OrderProbe_C" }));
        }

        [Test]
        public void StaleEntityId_IsAMiss_NeverTheSameNameObject()
        {
            var doomed = Create("Crate");
            var staleId = Id(doomed);
            Object.DestroyImmediate(doomed);
            var survivor = Create("Crate");
            GameObjectFinder.InvalidateCache();

            var (go, error) = GameObjectFinder.FindOrError(name: "Crate", path: "Crate", entityId: staleId);

            Assert.That(go, Is.Null);
            var json = JObject.FromObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            Assert.That(json["error"]?.ToString(), Is.EqualTo($"GameObject not found: entityId {staleId}"));
            Assert.That(json["suggestedFixes"]?[0]?["reason"]?.ToString(), Does.Contain("stale").And.Contain("path or name"));
            Assert.That(GameObjectFinder.Find(name: "Crate", entityId: staleId), Is.Null);
            Assert.That(survivor, Is.Not.Null);
        }

        [Test]
        public void StaleEntityId_LeavesTheSameNameObjectUntouched()
        {
            var doomed = Create("Crate");
            var staleId = Id(doomed);
            Object.DestroyImmediate(doomed);
            var survivor = Create("Crate");
            GameObjectFinder.InvalidateCache();

            var response = Execute("gameobject_set_active", new JObject { ["entityId"] = staleId, ["name"] = "Crate", ["active"] = false });

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"), response.ToString(Formatting.None));
            Assert.That(response["error"]?.ToString(), Does.Contain("entityId").And.Contain(staleId));
            Assert.That(survivor.activeSelf, Is.True, "A stale id must not land on the same-name object.");
        }

        [Test]
        public void StaleInstanceId_IsAMiss_AndSaysItMayBeStale()
        {
            var doomed = Create("Crate");
            var staleId = UnityObjectIdUtility.GetObjectId(doomed);
            if (staleId == 0) Assert.Ignore("This Unity version has no legacy instance ids.");
            Object.DestroyImmediate(doomed);
            Create("Crate");
            GameObjectFinder.InvalidateCache();

            var (go, error) = GameObjectFinder.FindOrError(name: "Crate", instanceId: staleId);

            Assert.That(go, Is.Null);
            var json = JObject.FromObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
            Assert.That(json["error"]?.ToString(), Is.EqualTo($"GameObject not found: instanceId {staleId}"));
            Assert.That(json["suggestedFixes"]?[0]?["reason"]?.ToString(), Does.Contain("stale").And.Contain("path or name"));
        }

        [Test]
        public void InstanceId_WithoutLegacyIds_PointsToTheEntityId()
        {
            var crate = Create("Crate");
            if (UnityObjectIdUtility.GetObjectId(crate) != 0) Assert.Ignore("Only Unity 6000.4+ has no legacy instance ids.");

            var (go, error) = GameObjectFinder.FindOrError(name: "Crate", instanceId: 12345);

            Assert.That(go, Is.Null);
            Assert.That(JObject.FromObject(error)["suggestedFixes"]?[0]?["reason"]?.ToString(), Does.Contain("entityId"));
        }

        [Test]
        public void EntityIdOfANonGameObject_NamesItsKind()
        {
            var texture = new Texture2D(2, 2);
            try
            {
                Create("Crate");
                var (go, error) = GameObjectFinder.FindOrError(name: "Crate", entityId: Id(texture));

                Assert.That(go, Is.Null);
                var json = JObject.FromObject(error);
                Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("TARGET_NOT_FOUND"));
                Assert.That(json["error"]?.ToString(), Does.Contain("names a Texture2D"));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void EntityIdOfAComponent_ResolvesItsGameObject()
        {
            var holder = Create("Holder");
            var box = holder.AddComponent<BoxCollider>();

            Assert.That(GameObjectFinder.Find(entityId: Id(box)), Is.EqualTo(holder));
        }

        [Test]
        public void EntityId_OutranksAConflictingName_AndSaysSo()
        {
            var target = Create("Left");
            Create("Right");

            var (go, _) = GameObjectFinder.FindOrError(name: "Right", entityId: Id(target));

            Assert.That(go, Is.EqualTo(target));
            Assert.That(GameObjectFinder.DrainResolutionNotes(), Has.Some.Contains($"entityId {Id(target)} outranks name 'Right'"));
        }

        [TestCase("")]
        [TestCase("  ")]
        [TestCase("0")]
        public void BlankOrZeroEntityId_IsNotALocator(string entityId)
        {
            var solo = Create("Solo");

            Assert.That(GameObjectFinder.Find(name: "Solo", entityId: entityId), Is.EqualTo(solo));
        }

        private static GameObject Create(string name, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null)
                go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void AddPreOrder(Transform transform, List<GameObject> into)
        {
            into.Add(transform.gameObject);
            foreach (Transform child in transform)
                AddPreOrder(child, into);
        }

        private static string Id(Object obj) => UnityObjectIdUtility.GetEntityId(obj);

        private static JObject Execute(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));

        /// <summary>Creates the scene folder; true when Assets/Temp had to be created for it, so cleanup removes that too.</summary>
        private static bool EnsureSceneFolder()
        {
            var createdTemp = !AssetDatabase.IsValidFolder("Assets/Temp");
            if (createdTemp)
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.CreateFolder("Assets/Temp", "FinderOrderScenes");
            return createdTemp;
        }

        private static void DeleteSceneFolder(bool createdTemp)
        {
            if (AssetDatabase.IsValidFolder(SceneFolder))
                AssetDatabase.DeleteAsset(SceneFolder);
            if (createdTemp && AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.DeleteAsset("Assets/Temp");
        }
    }
}

// Producer:Betsy
