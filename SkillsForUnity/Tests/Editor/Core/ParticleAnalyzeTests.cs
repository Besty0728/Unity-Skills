#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// particle_analyze: every check fires on a system built to fail it (code, severity, field, evidence), a system
    /// made by particle_create is clean, the scope is one system / a root / the open scenes, thresholds override and are
    /// validated, findings are ordered and paged, and the analysis changes nothing.
    /// </summary>
    [TestFixture]
    public class ParticleAnalyzeTests : ParticleTestBase
    {
        private const string ScenePath = AssetRoot + "/AnalyzeScene.unity";

        /// <summary>A system that passes every check: AddComponent leaves the renderer without a material.</summary>
        private static ParticleSystem Clean(ParticleSystem system)
        {
            system.GetComponent<ParticleSystemRenderer>().sharedMaterial = ParticleSkills.DefaultParticleMaterial();
            return system;
        }

        private static JObject Analyze(ParticleSystem system, string thresholds = null) =>
            Json(ParticleSkills.ParticleAnalyze(entityId: Id(system.gameObject), thresholdsJson: thresholds));

        private static JToken FindingOf(JObject analysis, string code) =>
            analysis["findings"].FirstOrDefault(finding => finding["code"]?.ToString() == code);

        [Test]
        public void Analyze_ASystemMadeByParticleCreate_HasNoErrorOrWarning()
        {
            var created = Json(ParticleSkills.ParticleCreate(name: "CleanFx"));
            AssertSuccess(created);
            var json = Json(ParticleSkills.ParticleAnalyze(entityId: created["entityId"]?.ToString()));
            AssertSuccess(json);
            Assert.That(json["systemsAnalyzed"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(json["scope"]["kind"]?.ToString(), Is.EqualTo("system"));
            Assert.That(json["counts"]["error"]?.Value<int>(), Is.EqualTo(0), json.ToString(Formatting.None));
            Assert.That(json["counts"]["warning"]?.Value<int>(), Is.EqualTo(0), json.ToString(Formatting.None));
            Assert.That(json["note"]?.ToString(), Does.Contain("nothing was changed"));
        }

        [Test]
        public void Analyze_ReportsEachProblem_WithItsSeverityAndField()
        {
            var outsider = Clean(MakeSystem("AnalyzeOutsider"));
            var lightHost = new GameObject("AnalyzeLight").AddComponent<Light>();
            int index = 0;

            void Expect(string code, string severity, string field, Action<ParticleSystem> setup)
            {
                var system = Clean(MakeSystem("Analyze" + code + (index++)));
                setup(system);
                var json = Analyze(system);
                AssertSuccess(json);
                var finding = FindingOf(json, code);
                Assert.That(finding, Is.Not.Null, $"{code} was not reported: {json.ToString(Formatting.None)}");
                Assert.That(finding["severity"]?.ToString(), Is.EqualTo(severity), code);
                Assert.That(finding["field"]?.ToString(), Is.EqualTo(field), code);
                Assert.That(finding["system"]["entityId"]?.ToString(), Is.EqualTo(Id(system.gameObject)), code);
                Assert.That(finding["message"]?.ToString(), Is.Not.Empty, code);
            }

            Expect("MATERIAL_MISSING", "error", "renderer.material", s => s.GetComponent<ParticleSystemRenderer>().sharedMaterial = null);
            Expect("SHADER_ERROR", "error", "renderer.material", s => s.GetComponent<ParticleSystemRenderer>().sharedMaterial = new Material(Shader.Find("Hidden/InternalErrorShader")));
            Expect("RENDERER_DISABLED", "warning", "renderer.enabled", s => s.GetComponent<ParticleSystemRenderer>().enabled = false);
            Expect("NO_RENDERER", "warning", "renderer", s => UnityEngine.Object.DestroyImmediate(s.GetComponent<ParticleSystemRenderer>()));
            Expect("MESH_MISSING", "error", "renderer.mesh", s =>
            {
                var renderer = s.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = null;
            });
            Expect("EMISSION_DISABLED", "warning", "modules.emission.enabled", s => { var e = s.emission; e.enabled = false; });
            Expect("NO_EMISSION", "warning", "modules.emission", s => { var e = s.emission; e.rateOverTime = 0f; });
            Expect("EMISSION_RATE_HIGH", "info", "modules.emission.rateOverTime", s => { var e = s.emission; e.rateOverTime = 10000f; });
            Expect("BURST_AFTER_DURATION", "info", "modules.emission.bursts[0].time", s => s.emission.SetBursts(new[] { new ParticleSystem.Burst(10f, 5) }));
            Expect("INVISIBLE_START_COLOR", "warning", "modules.main.startColor", s => { var m = s.main; m.startColor = new Color(1f, 1f, 1f, 0f); });
            Expect("INVISIBLE_COLOR_OVER_LIFETIME", "warning", "modules.colorOverLifetime.color", s =>
            {
                var gradient = new Gradient();
                gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f) }, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0f, 1f) });
                var col = s.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(gradient);
            });
            Expect("START_SIZE_TINY", "warning", "modules.main.startSize", s => { var m = s.main; m.startSize = 0f; });
            Expect("START_SIZE_HUGE", "info", "modules.main.startSize", s => { var m = s.main; m.startSize = 5000f; });
            Expect("START_LIFETIME_SHORT", "warning", "modules.main.startLifetime", s => { var m = s.main; m.startLifetime = 0.001f; });
            Expect("START_LIFETIME_LONG", "info", "modules.main.startLifetime", s => { var m = s.main; m.startLifetime = 1000f; });
            Expect("MAX_PARTICLES_HIGH", "info", "modules.main.maxParticles", s => { var m = s.main; m.maxParticles = 50000; });
            Expect("STOP_ACTION_SIDE_EFFECT", "warning", "modules.main.stopAction", s => { var m = s.main; m.stopAction = ParticleSystemStopAction.Destroy; });
            Expect("PREWARM_WITHOUT_LOOP", "info", "modules.main.prewarm", s => { var m = s.main; m.loop = false; m.prewarm = true; });
            Expect("CUSTOM_SPACE_MISSING", "error", "modules.main.customSimulationSpace", s => { var m = s.main; m.simulationSpace = ParticleSystemSimulationSpace.Custom; m.customSimulationSpace = null; });
            Expect("SHAPE_REFERENCE_MISSING", "error", "modules.shape.mesh", s => { var shape = s.shape; shape.shapeType = ParticleSystemShapeType.Mesh; shape.mesh = null; });
            Expect("COLLISION_WORLD_COST", "info", "modules.collision", s => { var c = s.collision; c.enabled = true; c.type = ParticleSystemCollisionType.World; });
            Expect("COLLISION_NO_PLANES", "warning", "modules.collision", s => { var c = s.collision; c.enabled = true; c.type = ParticleSystemCollisionType.Planes; });
            Expect("SCRIPT_CALLBACKS", "info", "modules.collision.sendCollisionMessages", s => { var c = s.collision; c.enabled = true; c.sendCollisionMessages = true; });
            Expect("TRIGGER_NO_COLLIDERS", "warning", "modules.trigger", s => { var t = s.trigger; t.enabled = true; });
            Expect("LIGHT_MISSING", "error", "modules.lights.light", s => { var l = s.lights; l.enabled = true; l.light = null; });
            Expect("LIGHTS_COST", "info", "modules.lights.maxLights", s => { var l = s.lights; l.enabled = true; l.light = lightHost; l.maxLights = 100; });
            Expect("TRAILS_COST", "info", "modules.trails", s => { var t = s.trails; t.enabled = true; });
            Expect("TRAIL_MATERIAL_MISSING", "error", "renderer.trailMaterial", s => { var t = s.trails; t.enabled = true; s.GetComponent<ParticleSystemRenderer>().trailMaterial = null; });
            Expect("SUB_EMITTERS_EMPTY", "info", "modules.subEmitters", s => { var sub = s.subEmitters; sub.enabled = true; });
            Expect("SUB_EMITTER_NOT_CHILD", "info", "modules.subEmitters.subEmitters[0].system", s =>
            {
                var sub = s.subEmitters;
                sub.enabled = true;
                sub.AddSubEmitter(outsider, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            });
            Expect("SUB_EMITTER_MISSING", "error", "modules.subEmitters.subEmitters[0].system", s =>
            {
                var child = MakeSystem("DoomedChild", s.transform);
                var sub = s.subEmitters;
                sub.enabled = true;
                sub.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            });
        }

        [Test]
        public void Analyze_FindingsCarryEvidenceAndARunnableFix()
        {
            var system = Clean(MakeSystem("EvidenceFx"));
            var main = system.main;
            main.maxParticles = 50000;
            system.GetComponent<ParticleSystemRenderer>().sharedMaterial = null;
            var json = Analyze(system);

            var cap = FindingOf(json, "MAX_PARTICLES_HIGH");
            Assert.That(cap["evidence"]["maxParticles"]?.Value<int>(), Is.EqualTo(50000));
            Assert.That(cap["evidence"]["threshold"]?.Value<int>(), Is.EqualTo(10000));
            Assert.That(cap["fix"]["skill"]?.ToString(), Is.EqualTo("particle_configure"));

            // The suggested arguments are a valid call of the suggested skill.
            var fix = FindingOf(json, "MATERIAL_MISSING")["fix"];
            Assert.That(fix["skill"]?.ToString(), Is.EqualTo("particle_set_renderer"));
            AssertSuccess(Json(ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: fix["args"]["configJson"].ToString(Formatting.None))));
            Assert.That(system.GetComponent<ParticleSystemRenderer>().sharedMaterial, Is.Not.Null);
            Assert.That(FindingOf(Analyze(system), "MATERIAL_MISSING"), Is.Null);
        }

        [Test]
        public void Analyze_ScopeIsOneSystemARootOrTheOpenScenes_NeverTwoAtOnce()
        {
            var root = new GameObject("FxRoot");
            var a = Clean(MakeSystem("RootedA", root.transform));
            Clean(MakeSystem("RootedB", root.transform));
            var outside = Clean(MakeSystem("Outside"));

            var rooted = Json(ParticleSkills.ParticleAnalyze(rootEntityId: Id(root)));
            AssertSuccess(rooted);
            Assert.That(rooted["scope"]["kind"]?.ToString(), Is.EqualTo("root"));
            Assert.That(rooted["systemsAnalyzed"]?.Value<int>(), Is.EqualTo(2));

            var all = Json(ParticleSkills.ParticleAnalyze());
            Assert.That(all["scope"]["kind"]?.ToString(), Is.EqualTo("openScenes"));
            Assert.That(all["systemsAnalyzed"]?.Value<int>(), Is.EqualTo(3));

            outside.gameObject.SetActive(false);
            Assert.That(Json(ParticleSkills.ParticleAnalyze(includeInactive: false))["systemsAnalyzed"]?.Value<int>(), Is.EqualTo(2));

            AssertError(Json(ParticleSkills.ParticleAnalyze(entityId: Id(a.gameObject), rootEntityId: Id(root))), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleAnalyze(entityId: Id(root))), "TARGET_NOT_FOUND");
        }

        [Test]
        public void Analyze_ThresholdsOverrideTheDefaults_AndAreValidated()
        {
            var system = Clean(MakeSystem("ThresholdFx"));
            Assert.That(FindingOf(Analyze(system), "MAX_PARTICLES_HIGH"), Is.Null, "1000 is below the default threshold.");
            var json = Analyze(system, "{\"maxParticlesPerSystem\": 500}");
            AssertSuccess(json);
            Assert.That(FindingOf(json, "MAX_PARTICLES_HIGH"), Is.Not.Null);
            Assert.That(json["thresholds"]["maxParticlesPerSystem"]?.Value<int>(), Is.EqualTo(500));
            Assert.That(json["thresholds"]["maxLights"]?.Value<int>(), Is.EqualTo(20), "Omitted thresholds keep their defaults.");

            AssertError(Analyze(system, "{\"maxParticles\": 5}"), "SEMANTIC_INVALID");
            AssertError(Analyze(system, "{\"maxSystems\": \"many\"}"), "TYPE_MISMATCH");
            AssertError(Analyze(system, "{\"minStartSize\": 2, \"maxStartSize\": 1}"), "SEMANTIC_INVALID");
            AssertError(Analyze(system, "{\"minStartLifetime\": 0}"), "SEMANTIC_INVALID");
            AssertError(Analyze(system, "{\"maxLights\": 0}"), "SEMANTIC_INVALID");
            AssertError(Analyze(system, "[1]"), "TYPE_MISMATCH");
            AssertError(Analyze(system, "{\"maxLights\": 3, \"maxLights\": 4}"), "INVALID_JSON");
        }

        [Test]
        public void Analyze_OrdersBySeverity_PagesByLimit_AndCountsEverything()
        {
            var system = MakeSystem("ManyProblemsFx");
            var emission = system.emission;
            emission.enabled = false;
            var main = system.main;
            main.maxParticles = 50000;

            var json = Json(ParticleSkills.ParticleAnalyze(entityId: Id(system.gameObject), limit: 1));
            AssertSuccess(json);
            Assert.That(json["findingCount"]?.Value<int>(), Is.GreaterThanOrEqualTo(3), json.ToString(Formatting.None));
            Assert.That(((JArray)json["findings"]).Count, Is.EqualTo(1));
            Assert.That(json["truncated"]?.Value<bool>(), Is.True);
            Assert.That(json["findings"][0]["severity"]?.ToString(), Is.EqualTo("error"));
            Assert.That(json["counts"]["error"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));
            Assert.That(json["counts"]["warning"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));
            Assert.That(json["counts"]["info"]?.Value<int>(), Is.GreaterThanOrEqualTo(1));

            var full = Json(ParticleSkills.ParticleAnalyze(entityId: Id(system.gameObject)));
            var ranks = full["findings"].Select(finding => finding["severity"]?.ToString() == "error" ? 0 : finding["severity"]?.ToString() == "warning" ? 1 : 2).ToArray();
            Assert.That(ranks, Is.Ordered, "Findings come most severe first.");

            AssertError(Json(ParticleSkills.ParticleAnalyze(limit: 0)), "SEMANTIC_INVALID");
            AssertError(Json(ParticleSkills.ParticleAnalyze(limit: 501)), "SEMANTIC_INVALID");
        }

        [Test]
        public void Analyze_ScopeTotals_ReportTheParticleBudgetAndTheSystemCount()
        {
            var root = new GameObject("BudgetRoot");
            for (int i = 0; i < 3; i++)
                Clean(MakeSystem("Budget" + i, root.transform));
            var json = Json(ParticleSkills.ParticleAnalyze(rootEntityId: Id(root), thresholdsJson: "{\"totalMaxParticles\": 2000, \"maxSystems\": 2}"));
            AssertSuccess(json);
            Assert.That(json["totalMaxParticles"]?.Value<long>(), Is.EqualTo(3000));
            var budget = FindingOf(json, "TOTAL_MAX_PARTICLES_HIGH");
            Assert.That(budget, Is.Not.Null, json.ToString(Formatting.None));
            Assert.That(budget["system"].Type, Is.EqualTo(JTokenType.Null), "A scope-level finding names no single system.");
            Assert.That(budget["severity"]?.ToString(), Is.EqualTo("warning"));
            Assert.That(FindingOf(json, "SYSTEM_COUNT_HIGH"), Is.Not.Null);
        }

        [Test]
        public void Analyze_ChangesNothing()
        {
            var system = MakeSystem("ReadOnlyFx");
            var main = system.main;
            main.maxParticles = 50000;
            EnsureAssetRoot();
            Assert.That(EditorSceneManager.SaveScene(system.gameObject.scene, ScenePath), Is.True);
            var before = EditorJsonUtility.ToJson(system) + EditorJsonUtility.ToJson(system.GetComponent<ParticleSystemRenderer>());
            int undoGroup = Undo.GetCurrentGroup();
            int tasks = WorkflowManager.History.tasks.Count;

            AssertSuccess(Json(ParticleSkills.ParticleAnalyze()));
            var viaRouter = Execute("particle_analyze", new JObject { ["entityId"] = Id(system.gameObject) });
            Assert.That(viaRouter["status"]?.ToString(), Is.EqualTo("success"), viaRouter.ToString(Formatting.None));

            var after = EditorJsonUtility.ToJson(system) + EditorJsonUtility.ToJson(system.GetComponent<ParticleSystemRenderer>());
            Assert.That(after, Is.EqualTo(before), "The analysis wrote to the system.");
            Assert.That(system.gameObject.scene.isDirty, Is.False, "The analysis dirtied the scene.");
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
            Assert.That(WorkflowManager.History.tasks.Count, Is.EqualTo(tasks));
        }

        [Test]
        public void PipelineHelpers_ClassifyShaderNamesAndTheActivePipeline()
        {
            Assert.That(ParticleSkills.ShaderPipelineKind("Universal Render Pipeline/Particles/Unlit"), Is.EqualTo("URP"));
            Assert.That(ParticleSkills.ShaderPipelineKind("HDRP/Unlit"), Is.EqualTo("HDRP"));
            Assert.That(ParticleSkills.ShaderPipelineKind("Particles/Standard Unlit"), Is.EqualTo("Built-in"));
            Assert.That(ParticleSkills.ShaderPipelineKind("Legacy Shaders/Particles/Alpha Blended"), Is.EqualTo("Built-in"));
            Assert.That(ParticleSkills.ShaderPipelineKind("Custom/Glow"), Is.Null, "An unknown family is never called a mismatch.");
            var expected = GraphicsSettings.currentRenderPipeline == null ? "Built-in" : null;
            if (expected != null)
                Assert.That(ParticleSkills.ActivePipelineKind(), Is.EqualTo(expected));
            else
                Assert.That(new[] { "URP", "HDRP", "SRP" }, Does.Contain(ParticleSkills.ActivePipelineKind()));
        }

        [Test]
        public void Recommend_RoutesSubEmitterAndDiagnosisIntents()
        {
            var savedTelemetry = SkillTelemetryService.Enabled;
            SkillTelemetryService.Enabled = false;
            try
            {
                string[] Top(string intent, int n) => ((JArray)JObject.Parse(SkillRouter.GetRecommendations("?intent=" + intent + "&topN=10"))["results"])
                    .Take(n).Select(result => result["name"]?.ToString()).ToArray();

                Assert.That(Top("analyze+particle+systems+for+problems", 3), Does.Contain("particle_analyze"));
                Assert.That(Top("particle+sub+emitters", 5).Any(name => name.Contains("sub_emitters")), Is.True);
            }
            finally
            {
                SkillTelemetryService.Enabled = savedTelemetry;
            }
        }
    }
}
#endif

// Producer:Betsy
