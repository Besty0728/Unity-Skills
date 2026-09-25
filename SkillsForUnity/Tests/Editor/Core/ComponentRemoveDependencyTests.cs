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
    /// component_remove / component_remove_batch honour RequireComponent the way the requirement is meant: a
    /// dependent blocks removing the last component that satisfies it (base types included), not a component that
    /// another instance still covers; the batch checks before removing anything, takes an optional componentIndex,
    /// and the planner reaches the same verdict with the same message.
    /// </summary>
    [TestFixture]
    public class ComponentRemoveDependencyTests
    {
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
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void RemoveBatch_RequiredComponent_IsRefusedAndNothingRemoved()
        {
            var go = new GameObject("RD_Dependent");
            Attach<RequiresLeafProbe>(go);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemoveBatch("[{\"name\":\"RD_Dependent\",\"componentType\":\"RequiredLeafProbe\"}]"));

            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            var item = json["results"]?[0];
            Assert.That(item?["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(item?["requiredBy"]?.Values<string>(), Does.Contain(nameof(RequiresLeafProbe)));
            Assert.That(item?["target"]?.ToString(), Is.EqualTo("RD_Dependent"));
            Assert.That(go.GetComponent<RequiredLeafProbe>(), Is.Not.Null, "A refused removal must remove nothing.");
        }

        [Test]
        public void RemoveBatch_WithoutIndex_StillRemovesEveryInstance()
        {
            var go = new GameObject("RD_Audio");
            go.AddComponent<AudioSource>();
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemoveBatch("[{\"name\":\"RD_Audio\",\"componentType\":\"AudioSource\"}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["results"]?[0]?["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(json["results"]?[0]?["remainingOfType"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(go.GetComponents<AudioSource>(), Is.Empty);
        }

        [Test]
        public void RemoveBatch_ComponentIndex_RemovesOnlyThatInstance()
        {
            var go = new GameObject("RD_AudioPair");
            var first = go.AddComponent<AudioSource>();
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemoveBatch(
                "[{\"name\":\"RD_AudioPair\",\"componentType\":\"AudioSource\",\"componentIndex\":1}]"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["warnings"], Is.Null, "componentIndex must be a known item field.");
            Assert.That(json["results"]?[0]?["count"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["results"]?[0]?["remainingOfType"]?.Value<int>(), Is.EqualTo(1));
            var remaining = go.GetComponents<AudioSource>();
            Assert.That(remaining.Length, Is.EqualTo(1));
            Assert.That(remaining[0], Is.SameAs(first), "componentIndex 1 must remove the second instance.");
        }

        [Test]
        public void RemoveBatch_ComponentIndexOutOfRange_IsRejectedWithTheTarget()
        {
            var go = new GameObject("RD_AudioOne");
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemoveBatch(
                "[{\"name\":\"RD_AudioOne\",\"componentType\":\"AudioSource\",\"componentIndex\":3}]"));

            Assert.That(json["failCount"]?.Value<int>(), Is.EqualTo(1), json.ToString(Formatting.None));
            Assert.That(json["results"]?[0]?["error"]?.ToString(), Does.Contain("out of range"));
            Assert.That(json["results"]?[0]?["target"]?.ToString(), Is.EqualTo("RD_AudioOne"));
            Assert.That(go.GetComponent<AudioSource>(), Is.Not.Null);
        }

        [Test]
        public void Remove_BaseTypeRequirement_IsRefused()
        {
            var go = new GameObject("RD_AnyBase");
            go.AddComponent<RequiredLeafProbe>();
            Attach<RequiresAnyBaseProbe>(go);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemove(name: "RD_AnyBase", componentType: "RequiredLeafProbe"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(json["requiredBy"]?.Values<string>(), Does.Contain(nameof(RequiresAnyBaseProbe)));
            Assert.That(json["hint"], Is.Not.Null, "The existing hint stays; the new keys are additive.");
            Assert.That(go.GetComponent<RequiredLeafProbe>(), Is.Not.Null);
        }

        /// <summary>
        /// Unity's own reaction to removing a component another one requires was not verified headlessly; if Unity
        /// refuses even though a second RequiredLeafProbe still satisfies the requirement, this fails with the post-check's
        /// "Failed to capture and remove" and FindBlockingDependents must be tightened to match Unity.
        /// </summary>
        [Test]
        public void Remove_OneOfTwoSatisfyingInstances_IsAllowed()
        {
            var go = new GameObject("RD_TwoLeaves");
            var kept = go.AddComponent<RequiredLeafProbe>();
            go.AddComponent<RequiredLeafProbe>();
            Attach<RequiresLeafProbe>(go);
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemove(name: "RD_TwoLeaves", componentType: "RequiredLeafProbe", componentIndex: 1));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["remainingOfType"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(go.GetComponents<RequiredLeafProbe>().Single(), Is.SameAs(kept));
        }

        [Test]
        public void Remove_ReportsTheResolvedTypeName()
        {
            var go = new GameObject("RD_Resolved");
            go.AddComponent<AudioSource>();
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemove(name: "RD_Resolved", componentType: "audiosource"));

            Assert.That(json["success"]?.Value<bool>(), Is.True, json.ToString(Formatting.None));
            Assert.That(json["removed"]?.ToString(), Is.EqualTo("AudioSource"), "removed is the resolved type, not the request text.");
            Assert.That(json["remainingOfType"]?.Value<int>(), Is.EqualTo(1));
        }

        [Test]
        public void Remove_NegativeIndex_IsRejected()
        {
            var go = new GameObject("RD_Negative");
            go.AddComponent<AudioSource>();
            GameObjectFinder.InvalidateCache();

            var json = ToJson(ComponentSkills.ComponentRemove(name: "RD_Negative", componentType: "AudioSource", componentIndex: -1));

            Assert.That(json["error"]?.ToString(), Does.Contain("out of range"), json.ToString(Formatting.None));
            Assert.That(go.GetComponent<AudioSource>(), Is.Not.Null);
        }

        [Test]
        public void Remove_DryRunAndExecution_Agree()
        {
            var go = new GameObject("RD_PlanSingle");
            Attach<RequiresLeafProbe>(go);
            GameObjectFinder.InvalidateCache();

            var dry = JObject.Parse(SkillRouter.DryRun("component_remove", "{\"name\":\"RD_PlanSingle\",\"componentType\":\"RequiredLeafProbe\"}"));
            var executed = ToJson(ComponentSkills.ComponentRemove(name: "RD_PlanSingle", componentType: "RequiredLeafProbe"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            var planned = dry["validation"]?["semanticErrors"]?.FirstOrDefault(e => e["field"]?.ToString() == "component");
            Assert.That(planned?["error"]?.ToString(), Is.EqualTo(executed["error"]?.ToString()),
                "dryRun and execution must refuse with the same message.");
        }

        [Test]
        public void RemoveBatch_DryRunAndExecution_Agree()
        {
            var go = new GameObject("RD_PlanBatch");
            Attach<RequiresLeafProbe>(go);
            GameObjectFinder.InvalidateCache();
            const string items = "[{\"name\":\"RD_PlanBatch\",\"componentType\":\"RequiredLeafProbe\"}]";

            var dry = JObject.Parse(SkillRouter.DryRun("component_remove_batch", new JObject { ["items"] = items }.ToString(Formatting.None)));
            var executed = ToJson(ComponentSkills.ComponentRemoveBatch(items));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            var planned = dry["validation"]?["semanticErrors"]?.FirstOrDefault(e => e["field"]?.ToString() == "items[0]");
            Assert.That(planned?["error"]?.ToString(), Is.EqualTo(executed["results"]?[0]?["error"]?.ToString()));
            Assert.That(go.GetComponent<RequiredLeafProbe>(), Is.Not.Null);
        }

        /// <summary>
        /// Items run in order, so removing the dependent first frees its requirement for a later item; the planner
        /// judges each item against the object as execution will find it, not against the untouched scene.
        /// </summary>
        [Test]
        public void RemoveBatch_DependentRemovedByAnEarlierItem_IsAllowedInDryRunAndExecution()
        {
            var go = new GameObject("RD_Ordered");
            Attach<RequiresLeafProbe>(go);
            GameObjectFinder.InvalidateCache();
            const string items = "[{\"name\":\"RD_Ordered\",\"componentType\":\"RequiresLeafProbe\"}," +
                                 "{\"name\":\"RD_Ordered\",\"componentType\":\"RequiredLeafProbe\"}]";

            var dry = JObject.Parse(SkillRouter.DryRun("component_remove_batch", new JObject { ["items"] = items }.ToString(Formatting.None)));
            Assert.That(dry["valid"]?.Value<bool>(), Is.True, dry.ToString(Formatting.None));

            var executed = ToJson(ComponentSkills.ComponentRemoveBatch(items));
            Assert.That(executed["success"]?.Value<bool>(), Is.True, executed.ToString(Formatting.None));
            Assert.That(go.GetComponent<RequiredLeafProbe>(), Is.Null);
            Assert.That(go.GetComponent<RequiresLeafProbe>(), Is.Null);
        }

        [Test]
        public void FindBlockingDependents_BlocksTheRequirementUnlessTheDependentGoesToo()
        {
            var go = new GameObject("RD_Together");
            var dependent = Attach<RequiresLeafProbe>(go);
            var leaf = go.GetComponent<RequiredLeafProbe>();

            Assert.That(ComponentSkills.FindBlockingDependents(go, new Component[] { leaf }),
                Is.EqualTo(new[] { nameof(RequiresLeafProbe) }), "Removing the required leaf alone is blocked.");
            Assert.That(ComponentSkills.FindBlockingDependents(go, new Component[] { leaf, dependent }), Is.Empty,
                "Removing the dependent in the same call leaves nothing to block.");
        }

        /// <summary>
        /// Adds a fixture and fails loudly if Unity refused it, so a dependency test can never pass vacuously
        /// against an object that carries no dependent.
        /// </summary>
        private static T Attach<T>(GameObject go) where T : Component
        {
            var component = go.AddComponent<T>();
            Assert.That(component, Is.Not.Null, $"Unity did not attach {typeof(T).Name}; fixtures must live in a non-editor assembly.");
            if (typeof(T) == typeof(RequiresLeafProbe))
                Assert.That(go.GetComponent<RequiredLeafProbe>(), Is.Not.Null, "RequireComponent should have added the RequiredLeafProbe.");
            return component;
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
