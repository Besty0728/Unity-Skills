using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The entityId checks that run before a skill does. A skill with no entityId parameter of its own (a custom skill in a
    /// user assembly, which TypeCache discovers too) still gets the router's synthetic one: forwarded as the legacy
    /// instanceId where this Unity version has one, else as a path that leads back to that object alone, else refused, and
    /// never as a name. A skill that declares entityId / *EntityId gets each one resolved during validation, so dryRun and
    /// plan refuse an id that names nothing as execution would, without a second report when the skill's own analyzer
    /// already covered that locator. The probe skills are built with SkillRouter.BuildSkillInfo and never registered.
    /// </summary>
    [TestFixture]
    public class EntityIdLocatorValidationTests
    {
        public static class Probes
        {
            public static object NamePath(string name = null, int instanceId = 0, string path = null) => Locate(name, instanceId, path);

            public static object NameOnly(string name = null, int instanceId = 0) => Locate(name, instanceId, null);

            private static object Locate(string name, int instanceId, string path)
            {
                var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
                return error ?? new { entityId = UnityObjectIdUtility.GetEntityId(go) };
            }
        }

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
            GameObjectFinder.DrainResolutionNotes();
        }

        // ---------- synthetic entityId (skills without their own) ----------

        [Test]
        public void SkillWithoutEntityId_GetsTheSyntheticOne()
        {
            var skill = Probe(nameof(Probes.NamePath));

            Assert.That(skill.AllowedParameterSet.Contains("entityId"), Is.True);
            var detail = Validate(skill, new JObject()).ParameterDetails
                .Select(entry => JObject.FromObject(entry))
                .Single(entry => entry["name"]?.ToString() == "entityId");
            Assert.That(detail["synthetic"]?.Value<bool>(), Is.True);
        }

        [Test]
        public void LegacyInstanceId_IsTheForwardedLocator()
        {
            var (_, second) = CreateTwins();
            if (UnityObjectIdUtility.GetObjectId(second) == 0) Assert.Ignore("This Unity version has no legacy instance ids.");
            var skill = Probe(nameof(Probes.NamePath));

            var validation = Validate(skill, new JObject { ["entityId"] = Id(second) });

            Assert.That(validation.Valid, Is.True);
            Assert.That(validation.Args["instanceId"]?.Value<int>(), Is.EqualTo(UnityObjectIdUtility.GetObjectId(second)));
            Assert.That(validation.Args["path"], Is.Null);
            Assert.That(Invoke(skill, validation)["entityId"]?.ToString(), Is.EqualTo(Id(second)));
        }

        [Test]
        public void NoLegacyInstanceId_UniquePathIsForwarded()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var target = Create("Solo", Create("B"));
            Create("Solo", Create("A"));
            GameObjectFinder.InvalidateCache();
            var skill = Probe(nameof(Probes.NamePath));

            var validation = Validate(skill, new JObject { ["entityId"] = Id(target) });

            Assert.That(validation.Valid, Is.True);
            Assert.That(validation.Args["path"]?.ToString(), Is.EqualTo("B/Solo"));
            Assert.That(validation.Args["instanceId"], Is.Null);
            Assert.That(Invoke(skill, validation)["entityId"]?.ToString(), Is.EqualTo(Id(target)));
        }

        [Test]
        public void NoLegacyInstanceId_SharedPathIsRefused()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var (first, second) = CreateTwins();

            var validation = Validate(Probe(nameof(Probes.NamePath)), new JObject { ["entityId"] = Id(first) });

            AssertRefused(validation, "ambiguousPath", first, second);
        }

        [Test]
        public void NoLegacyInstanceId_NameOnlySkillIsNeverForwardedAName()
        {
            SkillRouter.LegacyInstanceIdUnavailableForTests = true;
            var target = Create("Ground");
            var other = Create("Ground");
            GameObjectFinder.InvalidateCache();

            var validation = Validate(Probe(nameof(Probes.NameOnly)), new JObject { ["entityId"] = Id(target) });

            AssertRefused(validation, "noPathParameter", target, other);
            Assert.That(validation.Args["name"], Is.Null);
            Assert.That(validation.Args["instanceId"], Is.Null);
        }

        [Test]
        public void SyntheticEntityId_NamingNothing_IsRefused()
        {
            var staleId = StaleId();

            var validation = Validate(Probe(nameof(Probes.NamePath)), new JObject { ["entityId"] = staleId, ["name"] = "Crate" });

            Assert.That(validation.Valid, Is.False);
            var entry = JObject.FromObject(validation.SemanticErrors.Single());
            Assert.That(entry["field"]?.ToString(), Is.EqualTo("entityId"));
            Assert.That(entry["error"]?.ToString(), Is.EqualTo($"Object not found for entityId: {staleId}"));
        }

        [Test]
        public void ZeroEntityId_IsNoLocator()
        {
            Create("Solo");
            GameObjectFinder.InvalidateCache();
            var skill = Probe(nameof(Probes.NamePath));

            var validation = Validate(skill, new JObject { ["entityId"] = "0", ["name"] = "Solo" });

            Assert.That(validation.Valid, Is.True);
            Assert.That(validation.Args["instanceId"], Is.Null);
            Assert.That(validation.Args["path"], Is.Null);
            Assert.That(Invoke(skill, validation)["entityId"], Is.Not.Null);
        }

        // ---------- native entityId / *EntityId parameters ----------

        [Test]
        public void NativeEntityId_NamingNothing_IsRefusedByDryRunAndPlan()
        {
            var staleId = StaleId();
            var json = new JObject { ["entityId"] = staleId, ["name"] = "Crate", ["active"] = false }.ToString(Formatting.None);

            foreach (var payload in new[] { SkillRouter.DryRun("gameobject_set_active", json), SkillRouter.Plan("gameobject_set_active", json) })
            {
                var response = JObject.Parse(payload);
                Assert.That(response["valid"]?.Value<bool>(), Is.False, payload);
                var errors = (JArray)response["validation"]?["semanticErrors"];
                Assert.That(errors, Has.Count.EqualTo(1), payload);
                Assert.That(errors[0]["field"]?.ToString(), Is.EqualTo("entityId"));
                Assert.That(errors[0]["error"]?.ToString(), Is.EqualTo($"Object not found for entityId: {staleId}"));
            }
        }

        [Test]
        public void NativeEntityId_ReportedOnce_WhenTheSkillsAnalyzerCoversIt()
        {
            var staleId = StaleId();

            var dryRun = JObject.Parse(SkillRouter.DryRun("gameobject_delete", new JObject { ["entityId"] = staleId }.ToString(Formatting.None)));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False, dryRun.ToString(Formatting.None));
            var errors = (JArray)dryRun["validation"]?["semanticErrors"];
            Assert.That(errors, Has.Count.EqualTo(1), dryRun.ToString(Formatting.None));
            Assert.That(errors[0]["error"]?.ToString(), Does.Contain("entityId " + staleId));
        }

        [Test]
        public void PrefixedEntityId_NamingNothing_IsReportedOnce()
        {
            Create("Child");
            GameObjectFinder.InvalidateCache();
            var staleId = StaleId();

            var dryRun = JObject.Parse(SkillRouter.DryRun("gameobject_set_parent",
                new JObject { ["childName"] = "Child", ["parentEntityId"] = staleId }.ToString(Formatting.None)));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False, dryRun.ToString(Formatting.None));
            var errors = (JArray)dryRun["validation"]?["semanticErrors"];
            Assert.That(errors, Has.Count.EqualTo(1), dryRun.ToString(Formatting.None));
            Assert.That(errors[0]["error"]?.ToString(), Does.Contain(staleId));
        }

        [Test]
        public void NativeEntityId_OfAnyObject_PassesWhereTheSkillTakesAnyObject()
        {
            var texture = new Texture2D(2, 2);
            try
            {
                var dryRun = JObject.Parse(SkillRouter.DryRun("workflow_snapshot_object",
                    new JObject { ["entityId"] = Id(texture) }.ToString(Formatting.None)));

                Assert.That(dryRun["validation"]?["semanticErrors"]?.Any(entry => entry["field"]?.ToString() == "entityId") ?? false,
                    Is.False, dryRun.ToString(Formatting.None));
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ZeroNativeEntityId_SatisfiesNoLocatorRequirement()
        {
            var dryRun = JObject.Parse(SkillRouter.DryRun("gameobject_set_active",
                new JObject { ["entityId"] = "0", ["active"] = false }.ToString(Formatting.None)));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.False, dryRun.ToString(Formatting.None));
            var errors = (JArray)dryRun["validation"]?["semanticErrors"];
            Assert.That(errors.Select(entry => entry["error"]?.ToString()), Has.Some.StartsWith("Provide one of:"));
            Assert.That(errors.Select(entry => entry["field"]?.ToString()), Has.None.EqualTo("entityId"));
        }

        private static SkillRouter.SkillInfo Probe(string methodName) =>
            SkillRouter.BuildSkillInfo(typeof(Probes).GetMethod(methodName),
                new UnitySkillAttribute("entity_probe_" + methodName.ToLowerInvariant(), "Locator probe.")
                {
                    Category = SkillCategory.GameObject,
                    Operation = SkillOperation.Query,
                    ReadOnly = true
                });

        private static SkillRouter.ParameterValidationResult Validate(SkillRouter.SkillInfo skill, JObject args)
        {
            try { return SkillRouter.ValidateParameters(skill, args.ToString(Formatting.None)); }
            finally { GameObjectFinder.DrainResolutionNotes(); }
        }

        private static JObject Invoke(SkillRouter.SkillInfo skill, SkillRouter.ParameterValidationResult validation) =>
            JObject.FromObject(skill.Method.Invoke(null, validation.InvokeArgs));

        private static void AssertRefused(SkillRouter.ParameterValidationResult validation, string reason, params GameObject[] candidates)
        {
            Assert.That(validation.Valid, Is.False);
            var entry = JObject.FromObject(validation.SemanticErrors[0]);
            Assert.That(entry["field"]?.ToString(), Is.EqualTo("entityId"));
            Assert.That(entry["reason"]?.ToString(), Is.EqualTo(reason));
            Assert.That(entry["candidates"]?.Select(candidate => candidate["entityId"]?.ToString()),
                Is.EquivalentTo(candidates.Select(Id)));
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

        private static string Id(Object obj) => UnityObjectIdUtility.GetEntityId(obj);
    }
}

// Producer:Betsy
