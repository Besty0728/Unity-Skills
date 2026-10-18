#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>particle_create / particle_add_system / particle_set_renderer / particle_set_bursts and the read skills.</summary>
    [TestFixture]
    public class ParticleAuthoringTests : ParticleTestBase
    {
        private static int SceneObjectCount()
        {
            GameObjectFinder.InvalidateCache();
            return GameObjectFinder.GetSceneObjects().Count;
        }

        // ---------- particle_create ----------

        [Test]
        public void Create_DefaultsToAStoppedSystemThatDoesNotPlayOnAwake_WithTheDefaultMaterial()
        {
            var json = Json(ParticleSkills.ParticleCreate(name: "CreatedFx", x: 1, y: 2, z: 3));
            AssertSuccess(json);
            var go = UnityObjectIdUtility.EntityIdToObject(json["entityId"]?.ToString()) as GameObject;
            Assert.That(go, Is.Not.Null, json.ToString(Formatting.None));
            var system = go.GetComponent<ParticleSystem>();
            var renderer = go.GetComponent<ParticleSystemRenderer>();

            Assert.That(system.isStopped, Is.True);
            Assert.That(system.main.playOnAwake, Is.False);
            Assert.That(system.main.stopAction, Is.EqualTo(ParticleSystemStopAction.None));
            Assert.That(renderer.sharedMaterial, Is.Not.Null, "AddComponent leaves no material; create assigns the pipeline default.");
            Assert.That(json["material"]["name"]?.ToString(), Is.EqualTo(renderer.sharedMaterial.name));
            Assert.That(go.transform.position, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(go.transform.eulerAngles.x, Is.EqualTo(270f).Within(1e-3f), "rotX defaults to -90 so the default cone emits upwards.");
            Assert.That(json["defaultsApplied"]?.Values<string>(), Does.Contain("modules.main.playOnAwake"));
            Assert.That(json["particleSystem"]["entityId"]?.ToString(), Is.EqualTo(Id(system)));
        }

        [Test]
        public void Create_AppliesConfigJson_AndHonoursAnExplicitPlayOnAwake()
        {
            var json = Json(ParticleSkills.ParticleCreate(name: "FireFx", configJson: Config(new JObject
            {
                ["main"] = new JObject { ["playOnAwake"] = true, ["startLifetime"] = JObject.Parse("{\"mode\":\"TwoConstants\",\"min\":0.7,\"max\":1.2}"), ["maxParticles"] = 300 },
                ["emission"] = new JObject { ["rateOverTime"] = 40 },
                ["shape"] = new JObject { ["shapeType"] = "Cone", ["angle"] = 12, ["radius"] = 0.15 }
            })));
            AssertSuccess(json);
            var system = GameObject.Find("FireFx").GetComponent<ParticleSystem>();
            Assert.That(system.main.playOnAwake, Is.True);
            Assert.That(system.main.maxParticles, Is.EqualTo(300));
            Assert.That(system.main.startLifetime.mode, Is.EqualTo(ParticleSystemCurveMode.TwoConstants));
            Assert.That(system.emission.rateOverTime.constant, Is.EqualTo(40f));
            Assert.That(system.shape.angle, Is.EqualTo(12f));
            Assert.That(json["defaultsApplied"]?.Values<string>(), Does.Not.Contain("modules.main.playOnAwake"));
        }

        [Test]
        public void Create_UnderAParent_InWorldSpace()
        {
            var parent = new GameObject("FxRoot");
            parent.transform.position = new Vector3(10, 0, 0);
            var json = Json(ParticleSkills.ParticleCreate(name: "Child", x: 1, y: 0, z: 0, parentEntityId: Id(parent), space: "world"));
            AssertSuccess(json);
            var child = parent.transform.Find("Child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.position.x, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(json["path"]?.ToString(), Is.EqualTo("FxRoot/Child"));
        }

        [Test]
        public void Create_WithAnInvalidConfig_CreatesNothing()
        {
            var before = SceneObjectCount();
            var json = Json(ParticleSkills.ParticleCreate(name: "Broken", configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = -1 } })));
            AssertError(json, "SEMANTIC_INVALID");
            Assert.That(SceneObjectCount(), Is.EqualTo(before));
            Assert.That(GameObject.Find("Broken"), Is.Null);
        }

        [Test]
        public void Create_IsOneUndoStep()
        {
            Undo.IncrementCurrentGroup();
            var json = Json(ParticleSkills.ParticleCreate(name: "UndoFx", configJson: Config(new JObject { ["main"] = new JObject { ["duration"] = 2.5 } })));
            AssertSuccess(json);
            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Assert.That(GameObject.Find("UndoFx"), Is.Null);
            Undo.PerformRedo();
            var back = GameObject.Find("UndoFx");
            Assert.That(back, Is.Not.Null);
            Assert.That(back.GetComponent<ParticleSystem>().main.duration, Is.EqualTo(2.5f));
            Assert.That(back.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Not.Null);
        }

        // ---------- particle_add_system ----------

        [Test]
        public void AddSystem_AddsSystemAndRenderer_AndRefusesASecondOne()
        {
            var go = new GameObject("Host");
            var json = Json(ParticleSkills.ParticleAddSystem(entityId: Id(go), configJson: Config(new JObject { ["main"] = new JObject { ["loop"] = false } })));
            AssertSuccess(json);
            Assert.That(go.GetComponent<ParticleSystem>(), Is.Not.Null);
            Assert.That(go.GetComponent<ParticleSystemRenderer>(), Is.Not.Null);
            Assert.That(json["rendererCreated"]?.Value<bool>(), Is.True);
            Assert.That(go.GetComponent<ParticleSystem>().main.loop, Is.False);

            var again = Json(ParticleSkills.ParticleAddSystem(entityId: Id(go)));
            AssertError(again, "SEMANTIC_INVALID");
            StringAssert.Contains("already has a ParticleSystem", again["error"]?.ToString());
            Assert.That(go.GetComponent<ParticleSystem>().main.loop, Is.False, "An existing system is never reset.");
        }

        [Test]
        public void AddSystem_RefusesAnOrphanRenderer()
        {
            var go = new GameObject("Orphan");
            go.AddComponent<ParticleSystemRenderer>();
            var json = Json(ParticleSkills.ParticleAddSystem(entityId: Id(go)));
            AssertError(json, "SEMANTIC_INVALID");
            Assert.That(go.GetComponent<ParticleSystem>(), Is.Null);
        }

        [Test]
        public void AddSystem_UndoRemovesBothComponents()
        {
            var go = new GameObject("UndoHost");
            Undo.IncrementCurrentGroup();
            AssertSuccess(Json(ParticleSkills.ParticleAddSystem(entityId: Id(go))));
            Undo.IncrementCurrentGroup();
            Undo.PerformUndo();
            Assert.That(go.GetComponent<ParticleSystem>(), Is.Null);
            Assert.That(go.GetComponent<ParticleSystemRenderer>(), Is.Null);
        }

        // ---------- particle_set_renderer ----------

        [Test]
        public void SetRenderer_AppliesModeMaterialAndSorting_ByPathOrGuid()
        {
            var system = MakeSystem("RendererFx");
            var material = CreateMaterialAsset("FxMaterial");
            var path = AssetDatabase.GetAssetPath(material);

            var json = Json(ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: RendererConfig(new JObject
            {
                ["renderMode"] = "Stretch",
                ["lengthScale"] = 3,
                ["material"] = path,
                ["sortingOrder"] = 5,
                ["sortingLayerName"] = "Default"
            })));
            AssertSuccess(json);
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            Assert.That(renderer.renderMode, Is.EqualTo(ParticleSystemRenderMode.Stretch));
            Assert.That(renderer.sharedMaterial, Is.SameAs(material));
            Assert.That(renderer.sortingOrder, Is.EqualTo(5));
            Assert.That(json["state"]["renderer"]["material"]["assetPath"]?.ToString(), Is.EqualTo(path));

            var byGuid = Json(ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: RendererConfig(new JObject
            {
                ["material"] = new JObject { ["guid"] = AssetDatabase.AssetPathToGUID(path) }
            })));
            AssertSuccess(byGuid);
        }

        [Test]
        public void SetRenderer_BuiltInAssets_RoundTripThroughTheirReadback()
        {
            var system = MakeSystem("BuiltinFx");
            var json = Json(ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: RendererConfig(new JObject
            {
                ["renderMode"] = "Mesh",
                ["mesh"] = new JObject { ["assetPath"] = "Library/unity default resources", ["name"] = "Cube" },
                ["material"] = new JObject { ["assetPath"] = "Resources/unity_builtin_extra", ["name"] = "Default-ParticleSystem" }
            })));
            AssertSuccess(json);
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            Assert.That(renderer.mesh, Is.Not.Null);
            Assert.That(renderer.mesh.name, Is.EqualTo("Cube"));

            // The full renderer readback is accepted back as configJson unchanged.
            var readback = Json(ParticleSkills.ParticleGetRenderer(entityId: Id(system.gameObject)))["renderer"];
            var again = Json(ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: RendererConfig((JObject)readback)));
            AssertSuccess(again);
            Assert.That(again["adjustments"], Is.Null, again.ToString(Formatting.None));
        }

        [Test]
        public void SetRenderer_RejectsMeshModeWithoutMesh_UnknownSortingLayer_AndMinAboveMax()
        {
            var system = MakeSystem("RejectFx");
            var target = Id(system.gameObject);
            AssertError(Json(ParticleSkills.ParticleSetRenderer(entityId: target, configJson: RendererConfig(new JObject { ["renderMode"] = "Mesh" }))), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSetRenderer(entityId: target, configJson: RendererConfig(new JObject { ["sortingLayerName"] = "NoSuchLayer" }))), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSetRenderer(entityId: target, configJson: RendererConfig(new JObject { ["minParticleSize"] = 0.9, ["maxParticleSize"] = 0.1 }))), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSetRenderer(entityId: target, configJson: RendererConfig(new JObject { ["material"] = "Assets/Nope/Missing.mat" }))), "TARGET_NOT_FOUND");
            Assert.That(system.GetComponent<ParticleSystemRenderer>().renderMode, Is.EqualTo(ParticleSystemRenderMode.Billboard));
        }

        // ---------- particle_set_bursts / particle_get_bursts ----------

        [Test]
        public void SetBursts_ReplacesTheWholeList_AndAnEmptyArrayClearsIt()
        {
            var system = MakeSystem("BurstFx");
            var target = Id(system.gameObject);
            var json = Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson:
                "[{\"time\":0.25,\"count\":{\"mode\":\"TwoConstants\",\"min\":5,\"max\":15},\"cycleCount\":3,\"repeatInterval\":0.5,\"probability\":0.7},{\"time\":1,\"count\":20}]"));
            AssertSuccess(json);
            Assert.That(json["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(system.emission.burstCount, Is.EqualTo(2));
            var first = system.emission.GetBurst(0);
            Assert.That(first.cycleCount, Is.EqualTo(3));
            Assert.That(first.probability, Is.EqualTo(0.7f).Within(1e-6f));
            Assert.That(first.count.constantMax, Is.EqualTo(15f));
            Assert.That(system.emission.GetBurst(1).cycleCount, Is.EqualTo(1), "cycleCount defaults to 1.");

            var read = Json(ParticleSkills.ParticleGetBursts(entityId: target));
            Assert.That(JToken.DeepEquals(read["bursts"], json["bursts"]), Is.True);

            AssertSuccess(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[]")));
            Assert.That(system.emission.burstCount, Is.EqualTo(0));
        }

        [Test]
        public void SetBursts_RejectsInvalidBursts_AndWarnsPastTheDuration()
        {
            var system = MakeSystem("BurstRejectFx");
            var target = Id(system.gameObject);
            AssertError(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: null)), "MISSING_PARAM");
            AssertError(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[{\"time\":0,\"count\":1,\"probability\":2}]")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[{\"time\":0,\"count\":1,\"repeatInterval\":0}]")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[{\"time\":-1,\"count\":1}]")), "SEMANTIC_INVALID");
            var nine = "[" + string.Join(",", Enumerable.Range(0, 9).Select(i => $"{{\"time\":{i * 0.1},\"count\":1}}")) + "]";
            AssertError(Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: nine)), "SEMANTIC_INVALID");
            Assert.That(system.emission.burstCount, Is.EqualTo(0));

            var late = Json(ParticleSkills.ParticleSetBursts(entityId: target, burstsJson: "[{\"time\":7,\"count\":1}]"));
            AssertSuccess(late);
            Assert.That(late["warnings"]?.Values<string>().Any(warning => warning.Contains("never fires")), Is.True, late.ToString(Formatting.None));
        }

        // ---------- reads ----------

        [Test]
        public void FindAll_ListsSceneSystemsInHierarchyOrder_WithPagingAndRoot()
        {
            var root = new GameObject("Group");
            MakeSystem("A", root.transform);
            MakeSystem("B", root.transform);
            MakeSystem("Outside");
            new GameObject("NoSystem");

            var all = Json(ParticleSkills.ParticleFindAll());
            Assert.That(all["total"]?.Value<int>(), Is.EqualTo(3));
            var page = Json(ParticleSkills.ParticleFindAll(limit: 1, offset: 1));
            Assert.That(page["count"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(page["truncated"]?.Value<bool>(), Is.True);
            var scoped = Json(ParticleSkills.ParticleFindAll(rootEntityId: Id(root)));
            Assert.That(scoped["systems"].Select(entry => entry["name"]?.ToString()), Is.EqualTo(new[] { "A", "B" }));
            AssertError(Json(ParticleSkills.ParticleFindAll(limit: 0)), "SEMANTIC_INVALID");
        }

        [Test]
        public void GetInfo_SummarizesEveryModule_AndGetModuleReadsOne()
        {
            var system = MakeSystem("InfoFx");
            var info = Json(ParticleSkills.ParticleGetInfo(entityId: Id(system.gameObject)));
            AssertSuccess(info);
            Assert.That(info["modules"]["emission"]?.Value<bool>(), Is.True);
            Assert.That(info["modules"]["collision"]?.Value<bool>(), Is.False, "P1 modules still report their enabled flag.");
            Assert.That(info["main"]["duration"]?.Value<float>(), Is.EqualTo(5f));

            var emission = Json(ParticleSkills.ParticleGetModule(entityId: Id(system.gameObject), module: "emission"));
            Assert.That(emission["fields"]["rateOverTime"]["constant"]?.Value<float>(), Is.EqualTo(10f));
            Assert.That(emission["collections"][0]["readWith"]?.ToString(), Is.EqualTo("particle_get_bursts"));

            AssertError(Json(ParticleSkills.ParticleGetModule(entityId: Id(system.gameObject), module: "Main")), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleGetInfo(entityId: Id(new GameObject("Plain")))), "TARGET_NOT_FOUND");
        }

        [Test]
        public void Capabilities_DescribeEveryModule_AndFilterByModule()
        {
            var all = Json(ParticleSkills.ParticleGetCapabilities());
            AssertSuccess(all);
            Assert.That(all["available"]?.Value<bool>(), Is.True);
            Assert.That(all["schemaVersion"]?.Value<int>(), Is.EqualTo(1));
            var ids = all["modules"].Select(module => module["id"]?.ToString()).ToArray();
            Assert.That(ids, Is.SupersetOf(new[] { "system", "main", "emission", "shape", "velocityOverLifetime", "colorOverLifetime", "sizeOverLifetime", "noise", "renderer", "collision" }));

            var main = Json(ParticleSkills.ParticleGetCapabilities("main"));
            var fields = main["modules"][0]["fields"];
            var duration = fields.First(field => field["name"]?.ToString() == "duration");
            Assert.That(duration["unit"]?.ToString(), Is.EqualTo("seconds"));
            Assert.That(duration["min"]?.Value<double>(), Is.EqualTo(0.05));
            var rotation = fields.First(field => field["name"]?.ToString() == "startRotation");
            Assert.That(rotation["unit"]?.ToString(), Is.EqualTo("radians"));
            AssertError(Json(ParticleSkills.ParticleGetCapabilities("nope")), "SEMANTIC_INVALID");
        }
    }
}
#endif

// Producer:Betsy
