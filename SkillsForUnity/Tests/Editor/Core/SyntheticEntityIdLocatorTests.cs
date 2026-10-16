using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// A skill without its own entityId parameter used to get the entityId rewritten into the object's path, or failing
    /// that its name, so a same-name object took the call. The router now forwards the legacy instanceId where this Unity
    /// version has one, otherwise a path that leads back to the object alone, and otherwise refuses with SEMANTIC_INVALID.
    /// <see cref="SkillRouter.LegacyInstanceIdUnavailableForTests"/> runs the Unity 6000.4+ branches on every editor.
    /// </summary>
    [TestFixture]
    public class SyntheticEntityIdLocatorTests
    {
        private const string TwinParent = "LocatorRoot";
        private const string TwinPath = TwinParent + "/Twin";

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
            SkillRouter.LegacyInstanceIdUnavailableForTests = false;
            SkillsSurfaceProfile.Current = _savedProfile;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void SameNameSiblings_EachEntityIdReachesItsOwnObject()
        {
            var (first, second) = CreateTwins();

            foreach (var twin in new[] { first, second })
            {
                var response = Execute("component_list", new JObject { ["entityId"] = Id(twin) });
#if UNITY_6000_4_OR_NEWER
                AssertRefused(response, "ambiguousPath", first, second);
#else
                Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
                Assert.That(response["result"]?["entityId"]?.ToString(), Is.EqualTo(Id(twin)));
#endif
            }
        }

        [Test]
        public void SameNameSiblings_WriteLandsOnTheObjectThePathLookupMisses()
        {
            var (first, second) = CreateTwins();
            // The old rewrite sent the shared path, which reaches only this twin.
            var pathHit = GameObjectFinder.FindByPath(TwinPath);
            var hidden = pathHit == first ? second : first;
            var args = new JObject { ["entityId"] = Id(hidden), ["componentType"] = "BoxCollider" };

            var dryRun = JObject.Parse(SkillRouter.DryRun("component_add", args.ToString(Formatting.None)));
            var response = Execute("component_add", args);

#if UNITY_6000_4_OR_NEWER
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False);
            AssertRefused(response, "ambiguousPath", first, second);
            Assert.That(hidden.GetComponent<BoxCollider>(), Is.Null);
#else
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, dryRun.ToString(Formatting.None));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(hidden.GetComponent<BoxCollider>(), Is.Not.Null);
#endif
            Assert.That(pathHit.GetComponent<BoxCollider>(), Is.Null, "The same-name sibling must stay untouched.");
        }

        [Test]
        public void LegacyInstanceId_IsTheForwardedLocator()
        {
            var target = Create("Solo", Create("B"));
            Create("Solo", Create("A"));
            GameObjectFinder.InvalidateCache();

            var validation = ValidateArgs("component_list", new JObject { ["entityId"] = Id(target) });

            Assert.That(validation.Valid, Is.True);
#if UNITY_6000_4_OR_NEWER
            Assert.That(validation.Args["path"]?.ToString(), Is.EqualTo("B/Solo"));
            Assert.That(validation.Args["instanceId"], Is.Null);
#else
            Assert.That(validation.Args["instanceId"]?.Value<int>(), Is.EqualTo(UnityObjectIdUtility.GetObjectId(target)));
            Assert.That(validation.Args["path"], Is.Null);
#endif
            Assert.That(validation.Args["name"], Is.Null);
        }

        [Test]
        public void NoLegacyInstanceId_UniquePathIsForwarded()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var target = Create("Solo", Create("B"));
            Create("Solo", Create("A"));
            GameObjectFinder.InvalidateCache();
            var args = new JObject { ["entityId"] = Id(target) };

            var validation = ValidateArgs("component_list", args);
            Assert.That(validation.Valid, Is.True);
            Assert.That(validation.Args["path"]?.ToString(), Is.EqualTo("B/Solo"));
            Assert.That(validation.Args["instanceId"], Is.Null);

            var dryRun = JObject.Parse(SkillRouter.DryRun("component_list", args.ToString(Formatting.None)));
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, dryRun.ToString(Formatting.None));

            var response = Execute("component_list", args);
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response["result"]?["entityId"]?.ToString(), Is.EqualTo(Id(target)));
        }

        [Test]
        public void NoLegacyInstanceId_SharedPathIsRefusedByExecuteDryRunAndPlanAlike()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var (first, second) = CreateTwins();
            var args = new JObject { ["entityId"] = Id(first), ["componentType"] = "BoxCollider" };
            var json = args.ToString(Formatting.None);

            var dryRun = JObject.Parse(SkillRouter.DryRun("component_add", json));
            var plan = JObject.Parse(SkillRouter.Plan("component_add", json));
            var response = Execute("component_add", args);

            AssertRefused(response, "ambiguousPath", first, second);
            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False);
            Assert.That(dryRun["validation"]?["semanticErrors"]?[0]?["reason"]?.ToString(), Is.EqualTo("ambiguousPath"));
            Assert.That(plan["valid"]?.Value<bool>(), Is.False);
            Assert.That(plan["validation"]?["semanticErrors"]?[0]?["reason"]?.ToString(), Is.EqualTo("ambiguousPath"));
            Assert.That(first.GetComponent<BoxCollider>(), Is.Null);
            Assert.That(second.GetComponent<BoxCollider>(), Is.Null);
        }

        [Test]
        public void NoLegacyInstanceId_NameOnlySkillIsNeverForwardedAName()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var target = Create("Ground");
            var other = Create("Ground");
            GameObjectFinder.InvalidateCache();
            var args = new JObject { ["entityId"] = Id(target) };

            var validation = ValidateArgs("prefab_get_overrides", args);
            Assert.That(validation.Valid, Is.False);
            Assert.That(validation.Args["name"], Is.Null);
            Assert.That(validation.Args["instanceId"], Is.Null);

            AssertRefused(Execute("prefab_get_overrides", args), "noPathParameter", target, other);
        }

        [Test]
        public void NameOnlySkill_SameNameRoots_EachEntityIdReachesItsOwnObject()
        {
            var small = new TerrainData { size = new Vector3(10f, 1f, 10f) };
            var large = new TerrainData { size = new Vector3(20f, 1f, 20f) };
            var first = Create("Ground");
            var second = Create("Ground");
            try
            {
                first.AddComponent<Terrain>().terrainData = small;
                second.AddComponent<Terrain>().terrainData = large;
                GameObjectFinder.InvalidateCache();

                foreach (var ground in new[] { first, second })
                {
                    var response = Execute("terrain_get_info", new JObject { ["entityId"] = Id(ground) });
#if UNITY_6000_4_OR_NEWER
                    AssertRefused(response, "noPathParameter", first, second);
#else
                    Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
                    Assert.That(response["result"]?["entityId"]?.ToString(), Is.EqualTo(Id(ground)));
                    Assert.That(response["result"]?["size"]?["width"]?.Value<float>(),
                        Is.EqualTo(ground.GetComponent<Terrain>().terrainData.size.x));
#endif
                }
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(small);
                Object.DestroyImmediate(large);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EntityIdOutranksConflictingNameAndPath(bool legacyInstanceIdUnavailable)
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = legacyInstanceIdUnavailable;
            var target = Create("Solo", Create("B"));
            Create("Solo", Create("A"));
            GameObjectFinder.InvalidateCache();

            var response = Execute("component_list", new JObject { ["entityId"] = Id(target), ["name"] = "Solo", ["path"] = "A/Solo" });

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response["result"]?["entityId"]?.ToString(), Is.EqualTo(Id(target)));
        }

        private static (GameObject first, GameObject second) CreateTwins()
        {
            var root = Create(TwinParent);
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

        private static string Id(GameObject go) => UnityObjectIdUtility.GetEntityId(go);

        private static JObject Execute(string skill, JObject args) =>
            JObject.Parse(SkillRouter.Execute(skill, args.ToString(Formatting.None)));

        private static SkillRouter.ParameterValidationResult ValidateArgs(string skillName, JObject args)
        {
            Assert.That(SkillRouter.TryGetSkill(skillName, out var skill), Is.True, $"{skillName} is not registered.");
            try { return SkillRouter.ValidateParameters(skill, args.ToString(Formatting.None)); }
            finally { GameObjectFinder.DrainResolutionNotes(); }
        }

        private static void AssertRefused(JObject response, string reason, params GameObject[] candidates)
        {
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), response.ToString(Formatting.None));
            var entry = response["details"]?["semanticErrors"]?[0];
            Assert.That(entry?["field"]?.ToString(), Is.EqualTo("entityId"));
            Assert.That(entry?["reason"]?.ToString(), Is.EqualTo(reason));
            Assert.That(entry?["candidates"]?.Select(candidate => candidate["entityId"]?.ToString()),
                Is.EquivalentTo(candidates.Select(Id)));
        }
    }
}

// Producer:Betsy
