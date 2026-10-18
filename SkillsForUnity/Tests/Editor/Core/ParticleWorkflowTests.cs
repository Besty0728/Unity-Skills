#if COM_UNITY_MODULES_PARTICLESYSTEM
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
    /// Undo/Redo and workflow rollback of particle writes, including a history reloaded from disk with the scene saved and
    /// reopened in between: constants, every curve/gradient shape, bursts and the renderer material all come back.
    /// </summary>
    [TestFixture]
    public class ParticleWorkflowTests : ParticleTestBase
    {
        private const string ScenePath = AssetRoot + "/ParticleWorkflowScene.unity";

        private static readonly string RichConfig = Config(new JObject
        {
            ["main"] = new JObject
            {
                ["duration"] = 2.5,
                ["loop"] = false,
                ["startLifetime"] = JObject.Parse("{\"mode\":\"TwoConstants\",\"min\":0.4,\"max\":0.9}"),
                ["startSpeed"] = JObject.Parse("{\"mode\":\"Curve\",\"multiplier\":2,\"curve\":{\"keys\":[{\"time\":0,\"value\":0,\"rightTangentMode\":\"Constant\"},{\"time\":1,\"value\":1,\"leftTangentMode\":\"Linear\"}]}}"),
                ["startColor"] = JObject.Parse("{\"mode\":\"TwoColors\",\"minColor\":{\"r\":2,\"g\":0,\"b\":0,\"a\":1},\"maxColor\":{\"r\":0,\"g\":0,\"b\":1,\"a\":0.5}}")
            },
            ["colorOverLifetime"] = new JObject
            {
                ["enabled"] = true,
                ["color"] = JObject.Parse("{\"mode\":\"Gradient\",\"gradient\":{\"mode\":\"Blend\",\"colorKeys\":[{\"time\":0,\"color\":{\"r\":1,\"g\":0.6,\"b\":0.1}},{\"time\":1,\"color\":{\"r\":0.2,\"g\":0.2,\"b\":0.2}}],\"alphaKeys\":[{\"time\":0,\"alpha\":0},{\"time\":0.2,\"alpha\":1},{\"time\":1,\"alpha\":0}]}}")
            },
            ["noise"] = new JObject { ["enabled"] = true, ["strength"] = 0.75, ["quality"] = "Medium" }
        });

        private static JObject Full(ParticleSystem system) =>
            Json(ParticleSkills.ParticleGetInfo(entityId: Id(system.gameObject), includeModules: true));

        /// <summary>
        /// The configuration without what legitimately changes on its own: object ids, playback, and the random seed
        /// while useAutoRandomSeed is on (Unity draws a new one whenever the system is loaded or re-created).
        /// </summary>
        private static JObject WithoutVolatile(JObject info)
        {
            var copy = (JObject)info.DeepClone();
            foreach (var key in new[] { "entityId", "instanceId", "playback" })
                copy.Remove(key);
            if (copy["system"] is JObject system && system["useAutoRandomSeed"]?.Value<bool>() == true)
                system.Remove("randomSeed");
            return copy;
        }

        private static void AssertSameState(JObject expected, JObject actual, string because) =>
            Assert.That(JToken.DeepEquals(WithoutVolatile(expected), WithoutVolatile(actual)), Is.True,
                because + "\nexpected: " + WithoutVolatile(expected).ToString(Formatting.None) + "\nactual: " + WithoutVolatile(actual).ToString(Formatting.None));

        private static void ApplyEverything(ParticleSystem system, Material material)
        {
            var target = Id(system.gameObject);
            AssertSuccess(Json(ParticleSkills.ParticleConfigure(entityId: target, configJson: RichConfig)));
            AssertSuccess(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[{\"time\":0.5,\"count\":{\"mode\":\"TwoConstants\",\"min\":3,\"max\":6},\"cycleCount\":2,\"repeatInterval\":0.25}]")));
            AssertSuccess(Json(ParticleSkills.ParticleSetRenderer(entityId: target, configJson: RendererConfig(new JObject
            {
                ["material"] = AssetDatabase.GetAssetPath(material),
                ["renderMode"] = "Stretch",
                ["sortingOrder"] = 9
            }))));
        }

        [Test]
        public void UnityUndoRedo_RestoresAConfiguration()
        {
            var system = MakeSystem("UndoRedoFx");
            var before = Full(system);
            Undo.IncrementCurrentGroup();
            AssertSuccess(Json(ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: RichConfig)));
            var after = Full(system);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            AssertSameState(before, Full(system), "Undo restores the configuration before the call.");
            Undo.PerformRedo();
            AssertSameState(after, Full(system), "Redo re-applies it.");
        }

        [Test]
        public void WorkflowUndoRedo_RestoresModulesBurstsAndRendererMaterial()
        {
            var system = MakeSystem("WorkflowFx");
            var material = CreateMaterialAsset("WorkflowMat");
            var before = Full(system);

            WorkflowManager.BeginTask("particle-workflow", "test");
            ApplyEverything(system, material);
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;
            var after = Full(system);
            Assert.That(system.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.SameAs(material));

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            AssertSameState(before, Full(system), "Workflow undo restores every written field.");
            Assert.That(system.emission.burstCount, Is.EqualTo(0));
            Assert.That(system.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Null);

            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            AssertSameState(after, Full(system), "Workflow redo re-applies them.");
            Assert.That(system.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.SameAs(material));
        }

        [Test]
        public void WorkflowUndo_AfterSavingReopeningTheSceneAndReloadingTheHistory_RestoresTheConfiguration()
        {
            var system = MakeSystem("PersistentFx");
            var material = CreateMaterialAsset("PersistentMat");
            EnsureAssetRoot();
            Assert.That(EditorSceneManager.SaveScene(system.gameObject.scene, ScenePath), Is.True);
            var before = Full(system);

            WorkflowManager.BeginTask("particle-persistent", "test");
            ApplyEverything(system, material);
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;
            var after = Full(system);
            Assert.That(EditorSceneManager.SaveScene(system.gameObject.scene), Is.True);

            // Reopen the scene (new objects) and read the history back from disk (a fresh editor session would).
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorkflowManager.ResetStateForTests();
            WorkflowManager.LoadHistory();
            GameObjectFinder.InvalidateCache();
            var reloaded = GameObject.Find("PersistentFx").GetComponent<ParticleSystem>();
            AssertSameState(after, Full(reloaded), "The saved scene kept the configuration.");

            var undo = WorkflowManager.UndoTask(taskId);
            Assert.That(undo.success, Is.True, JsonConvert.SerializeObject(undo));
            AssertSameState(before, Full(reloaded), "The reloaded history restores the reopened object.");
            Assert.That(reloaded.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Null);
        }

        // A create (or add) and later edits of the created object are kept in separate tasks here: a workflow redo
        // re-creates the object, and a later snapshot of the same task still names the instance that no longer exists
        // (a framework limitation for every module, not particle-specific).

        [Test]
        public void WorkflowUndoRedo_OfACreate_RemovesAndRebuildsTheConfiguredObject()
        {
            WorkflowManager.BeginTask("particle-create", "test");
            var created = Json(ParticleSkills.ParticleCreate(name: "WorkflowCreated", configJson: RichConfig));
            AssertSuccess(created);
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;
            var after = Full(GameObject.Find("WorkflowCreated").GetComponent<ParticleSystem>());

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(GameObject.Find("WorkflowCreated"), Is.Null);

            var redo = WorkflowManager.RedoTask(taskId);
            Assert.That(redo.success, Is.True, JsonConvert.SerializeObject(redo));
            var rebuilt = GameObject.Find("WorkflowCreated");
            Assert.That(rebuilt, Is.Not.Null);
            Assert.That(rebuilt.GetComponents<ParticleSystemRenderer>().Length, Is.EqualTo(1));
            Assert.That(rebuilt.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Not.Null,
                "The renderer Unity re-adds with the system gets its own saved configuration (the default material) back.");
            AssertSameState(after, Full(rebuilt.GetComponent<ParticleSystem>()), "Redo rebuilds exactly the configured system.");
        }

        [Test]
        public void WorkflowUndoRedo_OfAnAdd_RemovesAndRestoresBothComponents()
        {
            var host = new GameObject("WorkflowHost");
            WorkflowManager.BeginTask("particle-add", "test");
            AssertSuccess(Json(ParticleSkills.ParticleAddSystem(entityId: Id(host), configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = 1.5 } }))));
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;

            var undo = WorkflowManager.UndoTask(taskId);
            Assert.That(undo.success, Is.True, JsonConvert.SerializeObject(undo));
            Assert.That(host.GetComponent<ParticleSystem>(), Is.Null);
            Assert.That(host.GetComponent<ParticleSystemRenderer>(), Is.Null);

            var redo = WorkflowManager.RedoTask(taskId);
            Assert.That(redo.success, Is.True, JsonConvert.SerializeObject(redo));
            Assert.That(host.GetComponent<ParticleSystem>().main.duration, Is.EqualTo(1.5f));
            Assert.That(host.GetComponents<ParticleSystemRenderer>().Length, Is.EqualTo(1));
            Assert.That(host.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Not.Null,
                "The re-added renderer gets its saved configuration back instead of a second AddComponent failing.");

            // Undo once more: the redone task removes both components again.
            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            Assert.That(host.GetComponent<ParticleSystemRenderer>(), Is.Null);
        }
    }
}
#endif

// Producer:Betsy
