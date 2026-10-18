#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The plain modules of P1-A (limit velocity, inherit velocity, lifetime by emitter speed, force, colour / size /
    /// rotation by speed, rotation over lifetime, external forces, custom data) and the P0 leftovers (orbital velocity,
    /// the shape radius / arc / box fields): every field writes and reads back through the real skills, false and 0
    /// apply, invalid input is rejected with its path and leaves the system byte-for-byte unchanged, and the
    /// cross-field rules (axis flags, X/Y/Z triplets sharing a mode, speed ranges, no-effect warnings) behave.
    /// </summary>
    [TestFixture]
    public class ParticlePlainModuleTests : ParticleTestBase
    {
        private ParticleSystem _system;

        [SetUp]
        public void CreateSystem() => _system = MakeSystem("PlainFx");

        [TearDown]
        public void StopSystem() => StopAndClear(_system);

        // ===================== helpers =====================

        private static readonly string[] P1Modules =
        {
            "limitVelocityOverLifetime", "inheritVelocity", "lifetimeByEmitterSpeed", "forceOverLifetime", "colorBySpeed",
            "sizeBySpeed", "rotationOverLifetime", "rotationBySpeed", "externalForces", "customData"
        };

        /// <summary>Every module this package touches, for the generic tests.</summary>
        public static string[] PlainModules => P1Modules.Concat(new[] { "velocityOverLifetime", "shape" }).ToArray();

        private static JObject Const(double value) => new JObject { ["mode"] = "Constant", ["constant"] = value };

        private static JObject Two(double min, double max) => new JObject { ["mode"] = "TwoConstants", ["min"] = min, ["max"] = max };

        private static JObject Curve(double from, double to, double multiplier = 1) => new JObject
        {
            ["mode"] = "Curve",
            ["multiplier"] = multiplier,
            ["curve"] = new JObject { ["keys"] = new JArray(Key(0, from), Key(1, to)) }
        };

        private static JObject TwoCurves(double from, double to) => new JObject
        {
            ["mode"] = "TwoCurves",
            ["multiplier"] = 1,
            ["minCurve"] = new JObject { ["keys"] = new JArray(Key(0, from), Key(1, to)) },
            ["maxCurve"] = new JObject { ["keys"] = new JArray(Key(0, from * 2), Key(1, to * 2)) }
        };

        private static JObject Key(double time, double value) => new JObject { ["time"] = time, ["value"] = value };

        private static JObject Vec(double x, double y) => new JObject { ["x"] = x, ["y"] = y };

        private static JObject Vec(double x, double y, double z) => new JObject { ["x"] = x, ["y"] = y, ["z"] = z };

        private static JObject BlackToWhite() => JObject.Parse(
            "{\"mode\":\"Gradient\",\"gradient\":{\"mode\":\"Blend\",\"colorKeys\":[{\"time\":0,\"color\":{\"r\":0,\"g\":0,\"b\":0}},{\"time\":1,\"color\":{\"r\":1,\"g\":1,\"b\":1}}]," +
            "\"alphaKeys\":[{\"time\":0,\"alpha\":1},{\"time\":1,\"alpha\":1}]}}");

        private JObject Configure(string module, JObject fields) => ConfigureModules(new JObject { [module] = fields });

        private JObject ConfigureModules(JObject modules) =>
            Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject), configJson: Config(modules)));

        private JToken Read(string module, string field) => Field(_system, module, field);

        private string Serialized() => EditorJsonUtility.ToJson(_system);

        private static string[] Paths(JObject error) => error["problems"].Select(problem => problem["path"]?.ToString()).ToArray();

        private static string[] Warnings(JObject json) => json["warnings"].Values<string>().ToArray();

        /// <summary>The call fails with <paramref name="code"/>, names the path, and the system is byte-for-byte what it was.</summary>
        private void AssertRejectedUntouched(string module, JObject fields, string path, string code = "SEMANTIC_INVALID")
        {
            var before = Serialized();
            var json = Configure(module, fields);
            AssertError(json, code);
            Assert.That(Paths(json), Does.Contain($"configJson.modules.{module}.{path}"), json.ToString(Formatting.None));
            Assert.That(Serialized(), Is.EqualTo(before), $"The rejected call for {module}.{path} changed the system.");
        }

        /// <summary>One valid, non-default configuration per module; together they form a rich patch.</summary>
        private static JObject Rich() => new JObject
        {
            ["velocityOverLifetime"] = new JObject
            {
                ["enabled"] = true,
                ["orbitalX"] = Const(1), ["orbitalY"] = Const(2), ["orbitalZ"] = Const(3),
                ["orbitalOffsetX"] = Two(0.5, 1), ["orbitalOffsetY"] = Two(0.25, 0.5), ["orbitalOffsetZ"] = Two(0.125, 0.25)
            },
            ["shape"] = new JObject
            {
                ["shapeType"] = "SingleSidedEdge",
                ["radiusMode"] = "PingPong", ["radiusSpread"] = 0.5, ["radiusSpeed"] = Curve(0, 3)
            },
            ["limitVelocityOverLifetime"] = new JObject
            {
                ["enabled"] = true, ["separateAxes"] = true,
                ["limitX"] = Two(1, 2), ["limitY"] = Two(3, 4), ["limitZ"] = Two(5, 6),
                ["space"] = "World", ["dampen"] = 0.25, ["drag"] = Const(1.5),
                ["multiplyDragByParticleSize"] = true, ["multiplyDragByParticleVelocity"] = true
            },
            ["inheritVelocity"] = new JObject { ["enabled"] = true, ["mode"] = "Current", ["curve"] = Curve(0.5, 1) },
            ["lifetimeByEmitterSpeed"] = new JObject { ["enabled"] = true, ["curve"] = Curve(0.25, 1), ["range"] = Vec(1, 7) },
            ["forceOverLifetime"] = new JObject
            {
                ["enabled"] = true, ["x"] = Two(1, 2), ["y"] = Two(3, 4), ["z"] = Two(5, 6), ["space"] = "World", ["randomized"] = true
            },
            ["colorBySpeed"] = new JObject { ["enabled"] = true, ["color"] = BlackToWhite(), ["range"] = Vec(2, 9) },
            ["sizeBySpeed"] = new JObject
            {
                ["enabled"] = true, ["separateAxes"] = true,
                ["x"] = Curve(0, 1), ["y"] = Curve(0, 2), ["z"] = Curve(0, 3), ["range"] = Vec(1, 4)
            },
            ["rotationOverLifetime"] = new JObject
            {
                ["enabled"] = true, ["separateAxes"] = true, ["x"] = Const(1), ["y"] = Const(2), ["z"] = Const(3)
            },
            ["rotationBySpeed"] = new JObject
            {
                ["enabled"] = true, ["separateAxes"] = true,
                ["x"] = Curve(0, 1), ["y"] = Curve(0, 2), ["z"] = Curve(0, 3), ["range"] = Vec(1, 8)
            },
            ["externalForces"] = new JObject { ["enabled"] = true, ["multiplierCurve"] = Curve(0.5, 2) },
            ["customData"] = new JObject
            {
                ["enabled"] = true,
                ["custom1Mode"] = "Vector", ["custom1ComponentCount"] = 3,
                ["custom1X"] = Const(0.25), ["custom1Y"] = Two(0.5, 0.75), ["custom1Z"] = Curve(0, 5),
                ["custom2Mode"] = "Color",
                ["custom2Color"] = JObject.Parse("{\"mode\":\"TwoColors\",\"minColor\":{\"r\":1,\"g\":0,\"b\":0,\"a\":1},\"maxColor\":{\"r\":0,\"g\":0,\"b\":1,\"a\":0.5}}")
            }
        };

        private static JObject Full(ParticleSystem system) =>
            Json(ParticleSkills.ParticleGetInfo(entityId: Id(system.gameObject), includeModules: true));

        // ===================== capabilities =====================

        [Test]
        public void Capabilities_ListEveryPlainModuleAsConfigurable()
        {
            var caps = Json(ParticleSkills.ParticleGetCapabilities());
            AssertSuccess(caps);
            foreach (var id in P1Modules)
            {
                var module = caps["modules"].First(candidate => candidate["id"]?.ToString() == id);
                Assert.That(module["configurable"]?.Value<bool>(), Is.True, id);
                Assert.That(module["phase"]?.ToString(), Is.EqualTo("P1"), id);
                Assert.That(module["fields"].Any(field => field["name"]?.ToString() == "enabled" && field["writable"]?.Value<bool>() == true), Is.True, id);
            }

            var rotation = caps["modules"].First(candidate => candidate["id"]?.ToString() == "rotationOverLifetime");
            foreach (var axis in new[] { "x", "y", "z" })
                Assert.That(rotation["fields"].First(field => field["name"]?.ToString() == axis)["unit"]?.ToString(), Is.EqualTo("radians/second"), axis);
            var group = rotation["axisGroups"][0];
            Assert.That(group["flag"]?.ToString(), Is.EqualTo("separateAxes"));
            Assert.That(group["whenFalse"].Count(), Is.EqualTo(0), "z is the Inspector's Angular Velocity: it has no uniform twin.");
            Assert.That(group["whenTrue"].Values<string>(), Is.EqualTo(new[] { "x", "y" }));
            Assert.That(rotation["sameModeGroups"][0]["fields"].Values<string>(), Is.EqualTo(new[] { "x", "y", "z" }));
            Assert.That(rotation["sameModeGroups"][0]["whenFlag"]?.ToString(), Is.EqualTo("separateAxes"));

            var external = caps["modules"].First(candidate => candidate["id"]?.ToString() == "externalForces");
            var count = external["fields"].First(field => field["name"]?.ToString() == "influenceCount");
            Assert.That(count["writable"]?.Value<bool>(), Is.False);
            var filter = external["fields"].First(field => field["name"]?.ToString() == "influenceFilter");
            Assert.That(filter["writableValues"].Values<string>(), Is.EqualTo(new[] { "LayerMask" }));
            Assert.That(filter["values"].Values<string>(), Is.SupersetOf(new[] { "LayerMask", "List", "LayerMaskAndList" }));

            var custom = caps["modules"].First(candidate => candidate["id"]?.ToString() == "customData");
            Assert.That(custom["fields"].Select(field => field["name"]?.ToString()), Is.EqualTo(new[]
            {
                "enabled",
                "custom1Mode", "custom1ComponentCount", "custom1X", "custom1Y", "custom1Z", "custom1W", "custom1Color",
                "custom2Mode", "custom2ComponentCount", "custom2X", "custom2Y", "custom2Z", "custom2W", "custom2Color"
            }));
        }

        // ===================== every module: write, read back, send the readback again =====================

        [Test]
        public void RichPatch_AppliesEveryModule_AndTheStateIsReadBackFromUnity()
        {
            var json = ConfigureModules(Rich());
            AssertSuccess(json);
            Assert.That(json["adjustments"], Is.Null, json.ToString(Formatting.None));
            var applied = json["applied"].Values<string>().ToArray();
            foreach (var id in PlainModules)
                Assert.That(applied.Any(path => path.StartsWith($"modules.{id}.")), Is.True, $"{id} was not applied: {string.Join(", ", applied)}");

            var limit = _system.limitVelocityOverLifetime;
            Assert.That(limit.enabled, Is.True);
            Assert.That(limit.separateAxes, Is.True);
            Assert.That(limit.limitX.constantMin, Is.EqualTo(1f));
            Assert.That(limit.limitY.constantMax, Is.EqualTo(4f));
            Assert.That(limit.limitZ.constantMin, Is.EqualTo(5f));
            Assert.That(limit.space, Is.EqualTo(ParticleSystemSimulationSpace.World));
            Assert.That(limit.dampen, Is.EqualTo(0.25f));
            Assert.That(limit.drag.constant, Is.EqualTo(1.5f));
            Assert.That(limit.multiplyDragByParticleSize, Is.True);
            Assert.That(limit.multiplyDragByParticleVelocity, Is.True);

            var inherit = _system.inheritVelocity;
            Assert.That(inherit.enabled, Is.True);
            Assert.That(inherit.mode, Is.EqualTo(ParticleSystemInheritVelocityMode.Current));
            Assert.That(inherit.curve.mode, Is.EqualTo(ParticleSystemCurveMode.Curve));
            Assert.That(inherit.curve.Evaluate(1f), Is.EqualTo(1f).Within(1e-5f));

            var lifetime = _system.lifetimeByEmitterSpeed;
            Assert.That(lifetime.enabled, Is.True);
            Assert.That(lifetime.curve.Evaluate(0f), Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(lifetime.range, Is.EqualTo(new Vector2(1f, 7f)));

            var force = _system.forceOverLifetime;
            Assert.That(force.enabled, Is.True);
            Assert.That(force.x.constantMin, Is.EqualTo(1f));
            Assert.That(force.y.constantMax, Is.EqualTo(4f));
            Assert.That(force.z.constantMax, Is.EqualTo(6f));
            Assert.That(force.space, Is.EqualTo(ParticleSystemSimulationSpace.World));
            Assert.That(force.randomized, Is.True);

            var color = _system.colorBySpeed;
            Assert.That(color.enabled, Is.True);
            Assert.That(color.color.mode, Is.EqualTo(ParticleSystemGradientMode.Gradient));
            Assert.That(color.range, Is.EqualTo(new Vector2(2f, 9f)));

            var size = _system.sizeBySpeed;
            Assert.That(size.enabled, Is.True);
            Assert.That(size.separateAxes, Is.True);
            Assert.That(size.y.Evaluate(1f), Is.EqualTo(2f).Within(1e-5f));
            Assert.That(size.z.Evaluate(1f), Is.EqualTo(3f).Within(1e-5f));
            Assert.That(size.range, Is.EqualTo(new Vector2(1f, 4f)));

            var rotation = _system.rotationOverLifetime;
            Assert.That(rotation.enabled, Is.True);
            Assert.That(rotation.separateAxes, Is.True);
            Assert.That(rotation.x.constant, Is.EqualTo(1f));
            Assert.That(rotation.y.constant, Is.EqualTo(2f));
            Assert.That(rotation.z.constant, Is.EqualTo(3f));

            var rotationBySpeed = _system.rotationBySpeed;
            Assert.That(rotationBySpeed.separateAxes, Is.True);
            Assert.That(rotationBySpeed.z.Evaluate(1f), Is.EqualTo(3f).Within(1e-5f));
            Assert.That(rotationBySpeed.range, Is.EqualTo(new Vector2(1f, 8f)));

            var external = _system.externalForces;
            Assert.That(external.enabled, Is.True);
            Assert.That(external.multiplierCurve.Evaluate(1f), Is.EqualTo(2f).Within(1e-5f));
            Assert.That(external.influenceFilter, Is.EqualTo(ParticleSystemGameObjectFilter.LayerMask));

            var custom = _system.customData;
            Assert.That(custom.enabled, Is.True);
            Assert.That(custom.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Vector));
            Assert.That(custom.GetVectorComponentCount(ParticleSystemCustomData.Custom1), Is.EqualTo(3));
            Assert.That(custom.GetVector(ParticleSystemCustomData.Custom1, 0).constant, Is.EqualTo(0.25f));
            Assert.That(custom.GetVector(ParticleSystemCustomData.Custom1, 1).constantMax, Is.EqualTo(0.75f));
            Assert.That(custom.GetVector(ParticleSystemCustomData.Custom1, 2).Evaluate(1f), Is.EqualTo(5f).Within(1e-5f));
            Assert.That(custom.GetMode(ParticleSystemCustomData.Custom2), Is.EqualTo(ParticleSystemCustomDataMode.Color));
            Assert.That(custom.GetColor(ParticleSystemCustomData.Custom2).mode, Is.EqualTo(ParticleSystemGradientMode.TwoColors));

            var velocity = _system.velocityOverLifetime;
            Assert.That(velocity.orbitalY.constant, Is.EqualTo(2f));
            Assert.That(velocity.orbitalOffsetY.constantMax, Is.EqualTo(0.5f));

            var shape = _system.shape;
            Assert.That(shape.radiusMode, Is.EqualTo(ParticleSystemShapeMultiModeValue.PingPong));
            Assert.That(shape.radiusSpread, Is.EqualTo(0.5f));
            Assert.That(shape.radiusSpeed.Evaluate(1f), Is.EqualTo(3f).Within(1e-5f));
        }

        [Test]
        public void ModuleReadback_IsAcceptedBackAsInput_AndChangesNothing([ValueSource(nameof(PlainModules))] string id)
        {
            AssertSuccess(ConfigureModules(Rich()));
            var module = ParticleSkills.Registry.Module(id);
            var fields = (JObject)Json(ParticleSkills.ParticleGetModule(entityId: Id(_system.gameObject), module: id))["fields"];
            foreach (var readOnly in module.Fields.Where(field => !field.Writable))
                fields.Remove(readOnly.Name);
            var before = Serialized();

            var json = Configure(id, fields);
            AssertSuccess(json);
            Assert.That(json["adjustments"], Is.Null, $"{id}: sending the readback back changed a value: {json.ToString(Formatting.None)}");
            Assert.That(Serialized(), Is.EqualTo(before), $"{id}: sending the readback back changed the system.");
        }

        [Test]
        public void GetInfo_IncludeModules_ReadsEveryPlainModule()
        {
            var info = Full(_system);
            AssertSuccess(info);
            foreach (var id in P1Modules)
            {
                Assert.That(info["modules"][id], Is.Not.Null, id);
                Assert.That(info["modules"][id]["enabled"]?.Type, Is.EqualTo(JTokenType.Boolean), id);
            }
            Assert.That(info["modules"]["externalForces"]["influenceCount"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(info["modules"]["customData"]["custom1Mode"]?.ToString(), Is.EqualTo("Disabled"));
        }

        // ===================== limitVelocityOverLifetime =====================

        [Test]
        public void LimitVelocity_FalseAndZero_AreApplied()
        {
            AssertSuccess(Configure("limitVelocityOverLifetime", (JObject)Rich()["limitVelocityOverLifetime"]));
            var json = Configure("limitVelocityOverLifetime", new JObject
            {
                ["enabled"] = false, ["separateAxes"] = false, ["dampen"] = 0, ["drag"] = Const(0),
                ["multiplyDragByParticleSize"] = false, ["multiplyDragByParticleVelocity"] = false
            });
            AssertSuccess(json);
            var limit = _system.limitVelocityOverLifetime;
            Assert.That(limit.enabled, Is.False);
            Assert.That(limit.separateAxes, Is.False);
            Assert.That(limit.dampen, Is.EqualTo(0f));
            Assert.That(limit.drag.constant, Is.EqualTo(0f));
            Assert.That(limit.multiplyDragByParticleSize, Is.False);
            Assert.That(limit.multiplyDragByParticleVelocity, Is.False);
            Assert.That(limit.limitY.constantMax, Is.EqualTo(4f), "Fields that were not sent keep their value.");
        }

        [Test]
        public void LimitVelocity_SpeedLimitIsSeparateFromTheAxisLimits_AndTheFlagDecidesWhichApply()
        {
            var untouched = _system.limitVelocityOverLifetime.limitX.constant;
            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["limit"] = Const(7) }));
            Assert.That(_system.limitVelocityOverLifetime.limit.constant, Is.EqualTo(7f));
            Assert.That(_system.limitVelocityOverLifetime.limitX.constant, Is.EqualTo(untouched), "limit has its own storage, not limitX's.");

            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["limitY"] = Const(2) }, "limitY");
            Assert.That(Read("limitVelocityOverLifetime", "limit"), Is.Not.Null, "With separateAxes off readback shows the speed limit...");
            Assert.That(Read("limitVelocityOverLifetime", "limitX"), Is.Null, "...and hides the per-axis limits.");

            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["limitX"] = Const(1), ["limitY"] = Const(2), ["limitZ"] = Const(3) }));
            Assert.That(Read("limitVelocityOverLifetime", "limit"), Is.Null);
            Assert.That(Read("limitVelocityOverLifetime", "limitZ"), Is.Not.Null);
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["limit"] = Const(9) }, "limit");

            var both = Configure("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["limit"] = Const(1), ["limitX"] = Const(1), ["limitY"] = Const(1), ["limitZ"] = Const(1) });
            AssertError(both, "SEMANTIC_INVALID");
        }

        [Test]
        public void LimitVelocity_PerAxisLimitsShareOneCurveMode()
        {
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["limitX"] = Curve(0, 1) }, "limitX");
            var json = Configure("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["limitX"] = Curve(0, 1) });
            StringAssert.Contains("must share one curve mode", json["error"]?.ToString());

            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["limitX"] = Curve(0, 1), ["limitY"] = Curve(0, 2), ["limitZ"] = Curve(0, 3) }));

            // Turning separateAxes on over live curves of different modes is the same contradiction.
            var other = MakeSystem("MixedLimit");
            var mixed = other.limitVelocityOverLifetime;
            mixed.limitX = new ParticleSystem.MinMaxCurve(1f);
            mixed.limitY = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));
            var turnedOn = Json(ParticleSkills.ParticleConfigure(entityId: Id(other.gameObject),
                configJson: Config(new JObject { ["limitVelocityOverLifetime"] = new JObject { ["separateAxes"] = true } })));
            AssertError(turnedOn, "SEMANTIC_INVALID");
            Assert.That(other.limitVelocityOverLifetime.separateAxes, Is.False);
        }

        [Test]
        public void LimitVelocity_InvalidValues_AreRejectedAndLeaveNothingBehind()
        {
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["dampen"] = 1.5 }, "dampen");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["dampen"] = -0.1 }, "dampen");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["limit"] = Const(-1) }, "limit.constant");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["drag"] = Two(-1, 2) }, "drag.min");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["space"] = "Custom" }, "space");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["dampen"] = "high" }, "dampen", "TYPE_MISMATCH");
            AssertRejectedUntouched("limitVelocityOverLifetime", new JObject { ["separateAxes"] = 1 }, "separateAxes", "TYPE_MISMATCH");
            var unknown = Configure("limitVelocityOverLifetime", new JObject { ["limitW"] = Const(1) });
            StringAssert.Contains("Unknown key 'limitW'", unknown["error"]?.ToString());
        }

        [Test]
        public void LimitVelocity_SpaceWithoutSeparateAxes_IsStoredWithAWarning()
        {
            var json = Configure("limitVelocityOverLifetime", new JObject { ["space"] = "World" });
            AssertSuccess(json);
            Assert.That(_system.limitVelocityOverLifetime.space, Is.EqualTo(ParticleSystemSimulationSpace.World));
            Assert.That(Warnings(json).Any(warning => warning.Contains("space only applies while separateAxes is true")), Is.True, json.ToString(Formatting.None));

            var withAxes = Configure("limitVelocityOverLifetime", new JObject { ["separateAxes"] = true, ["space"] = "Local" });
            Assert.That(Warnings(withAxes).Any(warning => warning.Contains("space only applies")), Is.False);
        }

        [Test]
        public void LimitVelocity_DampenZero_WarnsThatTheSpeedLimitDoesNothing()
        {
            Assert.That(_system.limitVelocityOverLifetime.dampen, Is.EqualTo(0f), "A new system starts with dampen 0.");
            var json = Configure("limitVelocityOverLifetime", new JObject { ["enabled"] = true, ["limit"] = Const(2) });
            AssertSuccess(json);
            Assert.That(Warnings(json).Any(warning => warning.Contains("dampen is 0, so the speed limit removes nothing")), Is.True, json.ToString(Formatting.None));

            var drag = Configure("limitVelocityOverLifetime", new JObject { ["drag"] = Const(1) });
            AssertSuccess(drag);
            Assert.That(_system.limitVelocityOverLifetime.dampen, Is.EqualTo(0f), "dampen is still 0 here, so the next assertion can fail.");
            Assert.That(Warnings(drag).Any(warning => warning.Contains("dampen")), Is.False, "Only the speed limit needs dampen; drag works regardless.");

            var raised = Configure("limitVelocityOverLifetime", new JObject { ["limit"] = Const(3), ["dampen"] = 1 });
            AssertSuccess(raised);
            Assert.That(Warnings(raised).Any(warning => warning.Contains("dampen is 0")), Is.False, "The patch's own dampen counts.");
        }

        // ===================== inheritVelocity =====================

        [Test]
        public void InheritVelocity_ModeAndCurveRoundTrip_AndNoEffectInLocalSpaceIsWarned()
        {
            var json = Configure("inheritVelocity", new JObject { ["enabled"] = true, ["mode"] = "Current", ["curve"] = Two(-1, 0.5) });
            AssertSuccess(json);
            Assert.That(_system.inheritVelocity.mode, Is.EqualTo(ParticleSystemInheritVelocityMode.Current));
            Assert.That(_system.inheritVelocity.curve.constantMin, Is.EqualTo(-1f), "The multiplier is a signed curve.");
            Assert.That(Warnings(json).Any(warning => warning.Contains("main.simulationSpace is Local")), Is.True, json.ToString(Formatting.None));

            var world = ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["simulationSpace"] = "World" },
                ["inheritVelocity"] = new JObject { ["mode"] = "Initial" }
            });
            AssertSuccess(world);
            Assert.That(Warnings(world).Any(warning => warning.Contains("Inherit Velocity")), Is.False, "The patch's own simulationSpace counts.");
        }

        [Test]
        public void InheritVelocity_InvalidValues_AreRejectedAndLeaveNothingBehind()
        {
            AssertRejectedUntouched("inheritVelocity", new JObject { ["mode"] = "Always" }, "mode");
            AssertRejectedUntouched("inheritVelocity", new JObject { ["mode"] = 1 }, "mode", "TYPE_MISMATCH");
            AssertRejectedUntouched("inheritVelocity", new JObject { ["curve"] = "full" }, "curve", "TYPE_MISMATCH");
        }

        // ===================== lifetimeByEmitterSpeed =====================

        [Test]
        public void LifetimeByEmitterSpeed_RoundTrips_AndTheRangeMustRunUpwards()
        {
            var json = Configure("lifetimeByEmitterSpeed", new JObject { ["enabled"] = true, ["curve"] = Curve(0.25, 1, 2), ["range"] = Vec(0, 10) });
            AssertSuccess(json);
            Assert.That(_system.lifetimeByEmitterSpeed.curve.curveMultiplier, Is.EqualTo(2f));
            Assert.That(_system.lifetimeByEmitterSpeed.range, Is.EqualTo(new Vector2(0f, 10f)));
            Assert.That(Read("lifetimeByEmitterSpeed", "range")["y"]?.Value<float>(), Is.EqualTo(10f));

            AssertRejectedUntouched("lifetimeByEmitterSpeed", new JObject { ["range"] = Vec(5, 1) }, "range");
            AssertSuccess(Configure("lifetimeByEmitterSpeed", new JObject { ["range"] = Vec(3, 3) }));
        }

        [Test]
        public void LifetimeByEmitterSpeed_ConstantMultiplierIgnoresTheRange_AndNegativeConstantsAreRejected()
        {
            var json = Configure("lifetimeByEmitterSpeed", new JObject { ["curve"] = 0.5, ["range"] = Vec(0, 4) });
            AssertSuccess(json);
            Assert.That(Warnings(json).Any(warning => warning.Contains("constant multiplier ignores it")), Is.True, json.ToString(Formatting.None));

            AssertRejectedUntouched("lifetimeByEmitterSpeed", new JObject { ["curve"] = -1 }, "curve");
            AssertRejectedUntouched("lifetimeByEmitterSpeed", new JObject { ["range"] = new JObject { ["x"] = 0 } }, "range.y", "MISSING_PARAM");
        }

        // ===================== forceOverLifetime =====================

        [Test]
        public void ForceOverLifetime_RoundTrips_FalseAndZeroApply()
        {
            AssertSuccess(Configure("forceOverLifetime", (JObject)Rich()["forceOverLifetime"]));
            var json = Configure("forceOverLifetime", new JObject { ["x"] = Const(0), ["y"] = Const(0), ["z"] = Const(0), ["randomized"] = false, ["space"] = "Local", ["enabled"] = false });
            AssertSuccess(json);
            var force = _system.forceOverLifetime;
            Assert.That(force.x.constant, Is.EqualTo(0f));
            Assert.That(force.randomized, Is.False);
            Assert.That(force.space, Is.EqualTo(ParticleSystemSimulationSpace.Local));
            Assert.That(force.enabled, Is.False);
        }

        [Test]
        public void ForceOverLifetime_AxesShareOneMode_AndRandomizedNeedsTwoValues()
        {
            var mixed = Configure("forceOverLifetime", new JObject { ["x"] = Curve(0, 1) });
            AssertError(mixed, "SEMANTIC_INVALID");
            StringAssert.Contains("must share one curve mode", mixed["error"]?.ToString());
            AssertSuccess(Configure("forceOverLifetime", new JObject { ["x"] = Curve(0, 1), ["y"] = Curve(0, 1), ["z"] = Curve(0, 1) }));

            var randomized = Configure("forceOverLifetime", new JObject { ["randomized"] = true });
            AssertSuccess(randomized);
            Assert.That(Warnings(randomized).Any(warning => warning.Contains("randomized only has an effect with TwoConstants or TwoCurves")), Is.True, randomized.ToString(Formatting.None));

            var twoCurves = Configure("forceOverLifetime", new JObject { ["x"] = TwoCurves(0, 1), ["y"] = TwoCurves(0, 1), ["z"] = TwoCurves(0, 1), ["randomized"] = true });
            AssertSuccess(twoCurves);
            Assert.That(Warnings(twoCurves).Any(warning => warning.Contains("randomized")), Is.False);
        }

        [Test]
        public void ForceOverLifetime_InvalidValues_AreRejectedAndLeaveNothingBehind()
        {
            AssertRejectedUntouched("forceOverLifetime", new JObject { ["space"] = "Custom" }, "space");
            AssertRejectedUntouched("forceOverLifetime", new JObject { ["x"] = Two(2, 1), ["y"] = Two(0, 1), ["z"] = Two(0, 1) }, "x.min");
            AssertRejectedUntouched("forceOverLifetime", new JObject { ["randomized"] = "yes" }, "randomized", "TYPE_MISMATCH");
        }

        // ===================== colorBySpeed =====================

        [Test]
        public void ColorBySpeed_GradientAndTwoGradients_RoundTrip_OtherModesAreRefused()
        {
            AssertSuccess(Configure("colorBySpeed", new JObject { ["color"] = BlackToWhite(), ["range"] = Vec(0, 10) }));
            var gradient = Read("colorBySpeed", "color");
            Assert.That(gradient["mode"]?.ToString(), Is.EqualTo("Gradient"));
            Assert.That(gradient["gradient"]["colorKeys"].Count(), Is.EqualTo(2));

            var two = new JObject { ["mode"] = "TwoGradients", ["minGradient"] = BlackToWhite()["gradient"].DeepClone(), ["maxGradient"] = BlackToWhite()["gradient"].DeepClone() };
            AssertSuccess(Configure("colorBySpeed", new JObject { ["color"] = two }));
            Assert.That(_system.colorBySpeed.color.mode, Is.EqualTo(ParticleSystemGradientMode.TwoGradients));

            var color = new JObject { ["mode"] = "Color", ["color"] = new JObject { ["r"] = 1, ["g"] = 0, ["b"] = 0 } };
            AssertRejectedUntouched("colorBySpeed", new JObject { ["color"] = color }, "color.mode");
            AssertRejectedUntouched("colorBySpeed", new JObject { ["color"] = new JObject { ["r"] = 1, ["g"] = 0, ["b"] = 0, ["a"] = 1 } }, "color");
            AssertRejectedUntouched("colorBySpeed", new JObject { ["range"] = Vec(9, 2) }, "range");
        }

        // ===================== sizeBySpeed =====================

        [Test]
        public void SizeBySpeed_SizeIsStoredAsX_AndTheAxisFlagDecidesWhichApply()
        {
            AssertSuccess(Configure("sizeBySpeed", new JObject { ["size"] = Curve(0, 2), ["range"] = Vec(0, 10) }));
            Assert.That(_system.sizeBySpeed.x.Evaluate(1f), Is.EqualTo(2f).Within(1e-5f), "size and x share storage.");
            Assert.That(Read("sizeBySpeed", "size"), Is.Not.Null);
            Assert.That(Read("sizeBySpeed", "x"), Is.Null);

            AssertRejectedUntouched("sizeBySpeed", new JObject { ["y"] = Curve(0, 1) }, "y");
            AssertSuccess(Configure("sizeBySpeed", new JObject { ["separateAxes"] = true, ["x"] = Curve(0, 1), ["y"] = Curve(0, 2), ["z"] = Curve(0, 3) }));
            Assert.That(Read("sizeBySpeed", "size"), Is.Null);
            AssertRejectedUntouched("sizeBySpeed", new JObject { ["size"] = Curve(0, 1) }, "size");
            AssertRejectedUntouched("sizeBySpeed", new JObject { ["x"] = Curve(0, 1), ["y"] = TwoCurves(0, 1), ["z"] = Curve(0, 1) }, "x");
        }

        [Test]
        public void SizeBySpeed_OffersCurvesOnly_AsTheInspectorDoes()
        {
            AssertRejectedUntouched("sizeBySpeed", new JObject { ["size"] = 0.5 }, "size");
            AssertRejectedUntouched("sizeBySpeed", new JObject { ["size"] = Two(0.5, 1) }, "size.mode");
            AssertRejectedUntouched("sizeBySpeed", new JObject { ["range"] = Vec(3, 2) }, "range");
        }

        // ===================== rotationOverLifetime / rotationBySpeed =====================

        [Test]
        public void RotationOverLifetime_ZIsTheOnlyAxisWithoutSeparateAxes()
        {
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["enabled"] = true, ["z"] = Const(1.5) }));
            Assert.That(_system.rotationOverLifetime.z.constant, Is.EqualTo(1.5f));
            Assert.That(Read("rotationOverLifetime", "z"), Is.Not.Null);
            Assert.That(Read("rotationOverLifetime", "x"), Is.Null, "With separateAxes off the Inspector shows only Angular Velocity (z).");
            Assert.That(Read("rotationOverLifetime", "y"), Is.Null);

            var before = Serialized();
            var json = Configure("rotationOverLifetime", new JObject { ["x"] = Const(1) });
            AssertError(json, "SEMANTIC_INVALID");
            var message = json["problems"][0]["error"]?.ToString();
            StringAssert.Contains("applies only while separateAxes is true (it is false)", message);
            StringAssert.Contains("send \"separateAxes\": true in the same patch.", message);
            StringAssert.DoesNotContain("or use", message, "There is no uniform field to suggest.");
            Assert.That(Serialized(), Is.EqualTo(before));
        }

        [Test]
        public void RotationOverLifetime_SeparateAxesNeedOneModeForXYZ()
        {
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["separateAxes"] = true, ["x"] = Const(1), ["y"] = Const(2), ["z"] = Const(3) }));
            Assert.That(Read("rotationOverLifetime", "x"), Is.Not.Null);

            var mixed = Configure("rotationOverLifetime", new JObject { ["x"] = Two(0, 1), ["y"] = Two(0, 1) });
            AssertError(mixed, "SEMANTIC_INVALID");
            StringAssert.Contains("x is TwoConstants", mixed["error"]?.ToString());
            StringAssert.Contains("z is Constant", mixed["error"]?.ToString(), "z belongs to the triplet although it has no uniform twin.");

            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["x"] = Two(0, 1), ["y"] = Two(0, 1), ["z"] = Two(0, 1) }));
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["separateAxes"] = false }));
            Assert.That(_system.rotationOverLifetime.separateAxes, Is.False);
            Assert.That(_system.rotationOverLifetime.x.mode, Is.EqualTo(ParticleSystemCurveMode.TwoConstants), "Switching the flag off keeps the stored axes.");
        }

        [Test]
        public void RotationBySpeed_RoundTrips_AndTheRangeRulesHold()
        {
            var json = Configure("rotationBySpeed", new JObject { ["enabled"] = true, ["z"] = Curve(0, 2), ["range"] = Vec(0, 10) });
            AssertSuccess(json);
            Assert.That(_system.rotationBySpeed.z.Evaluate(1f), Is.EqualTo(2f).Within(1e-5f));
            Assert.That(_system.rotationBySpeed.range, Is.EqualTo(new Vector2(0f, 10f)));
            Assert.That(Warnings(json).Any(warning => warning.Contains("ignores it")), Is.False, "A curve uses the range.");

            var constant = Configure("rotationBySpeed", new JObject { ["z"] = Const(1), ["range"] = Vec(1, 5) });
            AssertSuccess(constant);
            Assert.That(Warnings(constant).Any(warning => warning.Contains("constant angular velocity ignores it")), Is.True, constant.ToString(Formatting.None));

            AssertRejectedUntouched("rotationBySpeed", new JObject { ["range"] = Vec(6, 2) }, "range");
            AssertRejectedUntouched("rotationBySpeed", new JObject { ["y"] = Const(1) }, "y");
        }

        // ===================== externalForces =====================

        [Test]
        public void ExternalForces_MultiplierCurveRoundTrips_AndOnlyTheLayerMaskFilterIsWritable()
        {
            AssertSuccess(Configure("externalForces", new JObject { ["enabled"] = true, ["multiplierCurve"] = Const(2.5), ["influenceFilter"] = "LayerMask" }));
            Assert.That(_system.externalForces.multiplierCurve.constant, Is.EqualTo(2.5f));
            Assert.That(_system.externalForces.influenceFilter, Is.EqualTo(ParticleSystemGameObjectFilter.LayerMask));
            Assert.That(Read("externalForces", "influenceCount")?.Value<int>(), Is.EqualTo(0));

            var list = Configure("externalForces", new JObject { ["influenceFilter"] = "List" });
            AssertError(list, "SEMANTIC_INVALID");
            StringAssert.Contains("does not accept on write", list["error"]?.ToString());
            var readOnly = Configure("externalForces", new JObject { ["influenceCount"] = 2 });
            StringAssert.Contains("read-only", readOnly["error"]?.ToString());
            AssertRejectedUntouched("externalForces", new JObject { ["multiplierCurve"] = Const(-1) }, "multiplierCurve.constant");
            Assert.That(_system.externalForces.influenceFilter, Is.EqualTo(ParticleSystemGameObjectFilter.LayerMask));
        }

        // ===================== customData =====================

        [Test]
        public void CustomData_VectorStream_RoundTripsAndLeavesTheOtherStreamAlone()
        {
            var json = Configure("customData", new JObject
            {
                ["enabled"] = true, ["custom1Mode"] = "Vector", ["custom1ComponentCount"] = 2,
                ["custom1X"] = Const(0.5), ["custom1Y"] = Curve(0, 1, 4)
            });
            AssertSuccess(json);
            Assert.That(Warnings(json).Any(), Is.False, json.ToString(Formatting.None));
            var custom = _system.customData;
            Assert.That(custom.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Vector));
            Assert.That(custom.GetVectorComponentCount(ParticleSystemCustomData.Custom1), Is.EqualTo(2));
            Assert.That(custom.GetVector(ParticleSystemCustomData.Custom1, 1).curveMultiplier, Is.EqualTo(4f));
            Assert.That(custom.GetMode(ParticleSystemCustomData.Custom2), Is.EqualTo(ParticleSystemCustomDataMode.Disabled), "Custom2 was not part of the patch.");
            Assert.That(Read("customData", "custom1Y")["mode"]?.ToString(), Is.EqualTo("Curve"));
            Assert.That(Read("customData", "custom2Mode")?.ToString(), Is.EqualTo("Disabled"));
        }

        [Test]
        public void CustomData_ColorStream_RoundTrips_RandomColorIsNotOffered()
        {
            AssertSuccess(Configure("customData", new JObject { ["custom2Mode"] = "Color", ["custom2Color"] = BlackToWhite() }));
            Assert.That(_system.customData.GetColor(ParticleSystemCustomData.Custom2).mode, Is.EqualTo(ParticleSystemGradientMode.Gradient));
            AssertSuccess(Configure("customData", new JObject { ["custom2Color"] = new JObject { ["mode"] = "TwoColors", ["minColor"] = new JObject { ["r"] = 1, ["g"] = 0, ["b"] = 0 }, ["maxColor"] = new JObject { ["r"] = 0, ["g"] = 0, ["b"] = 1 } } }));
            Assert.That(_system.customData.GetColor(ParticleSystemCustomData.Custom2).mode, Is.EqualTo(ParticleSystemGradientMode.TwoColors));
            AssertRejectedUntouched("customData", new JObject { ["custom2Color"] = new JObject { ["mode"] = "RandomColor", ["gradient"] = BlackToWhite()["gradient"].DeepClone() } }, "custom2Color.mode");
        }

        [Test]
        public void CustomData_ComponentCountAndModeAreValidated()
        {
            AssertRejectedUntouched("customData", new JObject { ["custom1ComponentCount"] = 0 }, "custom1ComponentCount");
            AssertRejectedUntouched("customData", new JObject { ["custom1ComponentCount"] = 5 }, "custom1ComponentCount");
            AssertRejectedUntouched("customData", new JObject { ["custom1ComponentCount"] = 2.5 }, "custom1ComponentCount", "TYPE_MISMATCH");
            AssertRejectedUntouched("customData", new JObject { ["custom1Mode"] = "Matrix" }, "custom1Mode");
            AssertRejectedUntouched("customData", new JObject { ["custom3Mode"] = "Vector" }, "custom3Mode");
            AssertSuccess(Configure("customData", new JObject { ["custom1ComponentCount"] = 1 }));
            AssertSuccess(Configure("customData", new JObject { ["custom1ComponentCount"] = 4 }));
        }

        [Test]
        public void CustomData_ValuesOfAnInactiveModeOrBeyondTheCount_AreStoredWithAWarning()
        {
            var disabled = Configure("customData", new JObject { ["custom1X"] = Const(0.5), ["custom1Color"] = BlackToWhite() });
            AssertSuccess(disabled);
            Assert.That(Warnings(disabled).Any(warning => warning.Contains("custom1X, custom1Color are stored but not used while custom1Mode is Disabled")), Is.True, disabled.ToString(Formatting.None));
            Assert.That(_system.customData.GetVector(ParticleSystemCustomData.Custom1, 0).constant, Is.EqualTo(0.5f), "The value is kept for when the mode is switched.");

            var beyond = Configure("customData", new JObject { ["custom1Mode"] = "Vector", ["custom1ComponentCount"] = 2, ["custom1W"] = Const(1), ["custom1Z"] = Const(1) });
            AssertSuccess(beyond);
            Assert.That(Warnings(beyond).Any(warning => warning.Contains("custom1Z, custom1W lie beyond custom1ComponentCount (2)")), Is.True, beyond.ToString(Formatting.None));

            var colorMode = Configure("customData", new JObject { ["custom2Mode"] = "Color", ["custom2X"] = Const(1) });
            Assert.That(Warnings(colorMode).Any(warning => warning.Contains("custom2X is stored but not used while custom2Mode is Color")), Is.True, colorMode.ToString(Formatting.None));
        }

        // ===================== velocityOverLifetime: orbital =====================

        [Test]
        public void Velocity_OrbitalAndOffsetTriplets_ShareAModeEach_ButNotWithEachOtherOrTheLinearOnes()
        {
            var mixed = Configure("velocityOverLifetime", new JObject { ["orbitalX"] = Curve(0, 1) });
            AssertError(mixed, "SEMANTIC_INVALID");
            StringAssert.Contains("orbitalX, orbitalY, orbitalZ must share one curve mode", mixed["error"]?.ToString());
            var mixedOffset = Configure("velocityOverLifetime", new JObject { ["orbitalOffsetY"] = Two(0, 1) });
            StringAssert.Contains("orbitalOffsetX, orbitalOffsetY, orbitalOffsetZ must share one curve mode", mixedOffset["error"]?.ToString());

            var json = Configure("velocityOverLifetime", new JObject
            {
                ["orbitalX"] = Curve(0, 1), ["orbitalY"] = Curve(0, 2), ["orbitalZ"] = Curve(0, 3),
                ["orbitalOffsetX"] = Two(0, 1), ["orbitalOffsetY"] = Two(0, 1), ["orbitalOffsetZ"] = Two(0, 1)
            });
            AssertSuccess(json);
            Assert.That(_system.velocityOverLifetime.orbitalZ.Evaluate(1f), Is.EqualTo(3f).Within(1e-5f));
            Assert.That(_system.velocityOverLifetime.orbitalOffsetY.mode, Is.EqualTo(ParticleSystemCurveMode.TwoConstants));
            Assert.That(_system.velocityOverLifetime.x.mode, Is.EqualTo(ParticleSystemCurveMode.Constant), "The linear triplet is its own group.");
        }

        [Test]
        public void Velocity_OrbitOffsetWithoutOrbitalVelocity_IsWarnedAbout()
        {
            var json = Configure("velocityOverLifetime", new JObject { ["orbitalOffsetX"] = Const(1) });
            AssertSuccess(json);
            Assert.That(Warnings(json).Any(warning => warning.Contains("orbit centre offset has no effect while orbitalX, orbitalY and orbitalZ are all 0")), Is.True, json.ToString(Formatting.None));

            var withOrbit = Configure("velocityOverLifetime", new JObject { ["orbitalOffsetX"] = Const(2), ["orbitalZ"] = Const(1) });
            Assert.That(Warnings(withOrbit).Any(warning => warning.Contains("orbit centre offset")), Is.False, "The patch's own orbital velocity counts.");
            var curved = Configure("velocityOverLifetime", new JObject { ["orbitalX"] = Curve(0, 1), ["orbitalY"] = Curve(0, 1), ["orbitalZ"] = Curve(0, 1), ["orbitalOffsetX"] = Const(1) });
            Assert.That(Warnings(curved).Any(warning => warning.Contains("orbit centre offset")), Is.False, "Curves are not all zero.");
        }

        [Test]
        public void Velocity_OrbitalZeroApplies()
        {
            AssertSuccess(Configure("velocityOverLifetime", new JObject { ["orbitalX"] = Const(2), ["orbitalY"] = Const(2), ["orbitalZ"] = Const(2) }));
            AssertSuccess(Configure("velocityOverLifetime", new JObject { ["orbitalX"] = Const(0), ["orbitalY"] = Const(0), ["orbitalZ"] = Const(0) }));
            Assert.That(_system.velocityOverLifetime.orbitalY.constant, Is.EqualTo(0f));
        }

        // ===================== shape leftovers =====================

        [Test]
        public void Shape_RadiusArcAndBoxFields_RoundTrip_FalseAndZeroApply()
        {
            var json = Configure("shape", new JObject
            {
                ["shapeType"] = "SingleSidedEdge", ["radiusMode"] = "Loop", ["radiusSpread"] = 0.25, ["radiusSpeed"] = Const(2)
            });
            AssertSuccess(json);
            Assert.That(Warnings(json), Is.Empty, json.ToString(Formatting.None));
            Assert.That(_system.shape.radiusMode, Is.EqualTo(ParticleSystemShapeMultiModeValue.Loop));
            Assert.That(_system.shape.radiusSpread, Is.EqualTo(0.25f));
            Assert.That(_system.shape.radiusSpeed.constant, Is.EqualTo(2f));

            var arc = Configure("shape", new JObject { ["shapeType"] = "Circle", ["arcMode"] = "PingPong", ["arcSpeed"] = Curve(0, 4, 2), ["arcSpread"] = 0 });
            AssertSuccess(arc);
            Assert.That(Warnings(arc), Is.Empty, arc.ToString(Formatting.None));
            Assert.That(_system.shape.arcSpeed.mode, Is.EqualTo(ParticleSystemCurveMode.Curve));
            Assert.That(_system.shape.arcSpeed.curveMultiplier, Is.EqualTo(2f));

            var box = Configure("shape", new JObject { ["shapeType"] = "BoxShell", ["boxThickness"] = Vec(0.1, 0.2, 0.3) });
            AssertSuccess(box);
            Assert.That(Warnings(box), Is.Empty, box.ToString(Formatting.None));
            Assert.That(_system.shape.boxThickness, Is.EqualTo(new Vector3(0.1f, 0.2f, 0.3f)));

            AssertSuccess(Configure("shape", new JObject { ["boxThickness"] = Vec(0, 0, 0), ["radiusSpread"] = 0 }));
            Assert.That(_system.shape.boxThickness, Is.EqualTo(Vector3.zero));
            Assert.That(_system.shape.radiusSpread, Is.EqualTo(0f));
        }

        [Test]
        public void Shape_InvalidValues_AreRejectedAndLeaveNothingBehind()
        {
            AssertRejectedUntouched("shape", new JObject { ["radiusSpread"] = 1.5 }, "radiusSpread");
            AssertRejectedUntouched("shape", new JObject { ["boxThickness"] = Vec(0.5, 2, 0.5) }, "boxThickness.y");
            AssertRejectedUntouched("shape", new JObject { ["boxThickness"] = Vec(0.5, -0.1, 0.5) }, "boxThickness.y");
            AssertRejectedUntouched("shape", new JObject { ["arcSpeed"] = Two(1, 2) }, "arcSpeed.mode");
            AssertRejectedUntouched("shape", new JObject { ["radiusSpeed"] = TwoCurves(0, 1) }, "radiusSpeed.mode");
            AssertRejectedUntouched("shape", new JObject { ["radiusMode"] = "Spiral" }, "radiusMode");
            AssertRejectedUntouched("shape", new JObject { ["boxThickness"] = new JObject { ["x"] = 1, ["y"] = 1 } }, "boxThickness.z", "MISSING_PARAM");
        }

        [Test]
        public void Shape_NoEffectCases_AreWarnedAboutAndStored()
        {
            var cone = Configure("shape", new JObject { ["radiusMode"] = "Loop", ["boxThickness"] = Vec(0.5, 0.5, 0.5) });
            AssertSuccess(cone);
            var warnings = Warnings(cone);
            Assert.That(warnings.Any(warning => warning.Contains("radiusMode: has no effect for shapeType Cone (it applies to SingleSidedEdge)")), Is.True, cone.ToString(Formatting.None));
            Assert.That(warnings.Any(warning => warning.Contains("boxThickness: has no effect for shapeType Cone (it applies to BoxShell, BoxEdge)")), Is.True);
            Assert.That(_system.shape.boxThickness, Is.EqualTo(new Vector3(0.5f, 0.5f, 0.5f)));

            var speed = Configure("shape", new JObject { ["arcSpeed"] = Const(1) });
            AssertSuccess(speed);
            Assert.That(Warnings(speed).Any(warning => warning.Contains("arcSpeed only has an effect while arcMode is Loop or PingPong (it is Random)")), Is.True, speed.ToString(Formatting.None));
            var animated = Configure("shape", new JObject { ["arcMode"] = "Loop", ["arcSpeed"] = Const(1) });
            Assert.That(Warnings(animated).Any(warning => warning.Contains("arcSpeed only has an effect")), Is.False, "The patch's own arcMode counts.");
        }

        [Test]
        public void Shape_ArcApplies_ToSphereAndHemisphere_AsTheInspectorShowsIt()
        {
            foreach (var type in new[] { "Sphere", "Hemisphere", "Cone", "Circle", "Donut" })
            {
                var json = Configure("shape", new JObject { ["shapeType"] = type, ["arc"] = 90, ["arcMode"] = "Random", ["arcSpread"] = 0.5 });
                AssertSuccess(json);
                Assert.That(Warnings(json).Any(warning => warning.Contains("arc")), Is.False, $"arc is a {type} field: {json.ToString(Formatting.None)}");
            }
            var box = Configure("shape", new JObject { ["shapeType"] = "Box", ["arc"] = 90 });
            Assert.That(Warnings(box).Any(warning => warning.Contains("arc: has no effect for shapeType Box")), Is.True, box.ToString(Formatting.None));
        }

        // ===================== units and mappings, pinned by simulation =====================

        /// <summary>One particle born at the origin of a Local-space system: no shape, no speed, long life, no gravity, a fixed seed.</summary>
        private void PrepareOneParticle()
        {
            var main = _system.main;
            main.startSpeed = 0f;
            main.startLifetime = 10f;
            main.startSize = 1f;
            main.loop = false;
            main.duration = 5f;
            main.maxParticles = 1000;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            var emission = _system.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
            var shape = _system.shape;
            shape.enabled = false;
            _system.useAutoRandomSeed = false;
            _system.randomSeed = 7u;
        }

        /// <summary>Simulates from the start (Edit Mode, no playing) and returns the live particles. Steps under 0.05 s emit nothing.</summary>
        private ParticleSystem.Particle[] Simulate(float seconds)
        {
            _system.Simulate(seconds, false, true, true);
            var particles = new ParticleSystem.Particle[Mathf.Max(_system.particleCount, 1)];
            return particles.Take(_system.GetParticles(particles)).ToArray();
        }

        [Test]
        public void Rotation_IsRadiansPerSecond_ForLifetimeAndSpeed()
        {
            PrepareOneParticle();
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["enabled"] = true, ["z"] = Const(1) }));
            Assert.That(Simulate(1f)[0].rotation, Is.EqualTo(57.29578f).Within(0.01f), "1 radian per second is 57.3 degrees after one second.");

            StopAndClear(_system);
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["separateAxes"] = true, ["x"] = Const(1), ["y"] = Const(0.5), ["z"] = Const(0.25) }));
            var rotation = Simulate(1f)[0].rotation3D;
            Assert.That(rotation.x, Is.EqualTo(57.29578f).Within(0.01f));
            Assert.That(rotation.y, Is.EqualTo(28.64789f).Within(0.01f));
            Assert.That(rotation.z, Is.EqualTo(14.32394f).Within(0.01f));

            StopAndClear(_system);
            AssertSuccess(Configure("rotationOverLifetime", new JObject { ["enabled"] = false }));
            AssertSuccess(ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["startSpeed"] = 5 },
                ["rotationBySpeed"] = new JObject { ["enabled"] = true, ["z"] = Curve(0, 1), ["range"] = Vec(0, 10) }
            }));
            Assert.That(Simulate(1f)[0].rotation, Is.EqualTo(28.64789f).Within(0.01f), "Speed 5 is halfway over the range 0..10: 0.5 radian per second.");
        }

        [Test]
        public void OrbitalVelocity_IsRadiansPerSecondAroundTheSystemCentre_WithTheOffsetAsTheCentre()
        {
            PrepareOneParticle();
            var shape = _system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = Vector3.zero;
            shape.position = new Vector3(0f, 1f, 0f);
            AssertSuccess(Configure("velocityOverLifetime", new JObject { ["enabled"] = true, ["orbitalX"] = Const(1) }));
            var position = Simulate(1f)[0].position;
            Assert.That(position.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(position.y, Is.EqualTo(Mathf.Cos(1f)).Within(1e-3f), "One radian around X turns (0, 1, 0) to (0, cos 1, sin 1).");
            Assert.That(position.z, Is.EqualTo(Mathf.Sin(1f)).Within(1e-3f));

            StopAndClear(_system);
            shape = _system.shape;
            shape.position = Vector3.zero;
            AssertSuccess(Configure("velocityOverLifetime", new JObject
            {
                ["orbitalX"] = Const(0), ["orbitalZ"] = Const(Mathf.PI / 2f),
                ["orbitalOffsetX"] = Const(1)
            }));
            var around = Simulate(1f)[0].position;
            Assert.That(around.x, Is.EqualTo(1f).Within(1e-2f), "A quarter turn about the centre (1, 0, 0) takes the origin to (1, -1, 0).");
            Assert.That(around.y, Is.EqualTo(-1f).Within(1e-2f));
        }

        [Test]
        public void Force_IsUnitsPerSecondSquared()
        {
            PrepareOneParticle();
            AssertSuccess(Configure("forceOverLifetime", new JObject { ["enabled"] = true, ["x"] = Const(10) }));
            var particle = Simulate(1f)[0];
            Assert.That(particle.velocity.x, Is.EqualTo(10f).Within(0.05f), "An acceleration of 10 reaches speed 10 after one second.");
            Assert.That(particle.position.x, Is.EqualTo(5f).Within(0.25f));
        }

        [Test]
        public void LimitVelocity_DampenOneClampsHard_AndDampenZeroLeavesTheSpeed()
        {
            PrepareOneParticle();
            var main = _system.main;
            main.startSpeed = 10f;
            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["enabled"] = true, ["limit"] = Const(2), ["dampen"] = 1 }));
            Assert.That(Simulate(0.5f)[0].velocity.magnitude, Is.EqualTo(2f).Within(1e-3f));

            StopAndClear(_system);
            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["dampen"] = 0 }));
            Assert.That(Simulate(0.5f)[0].velocity.magnitude, Is.EqualTo(10f).Within(1e-3f), "dampen 0 removes nothing.");

            StopAndClear(_system);
            AssertSuccess(Configure("limitVelocityOverLifetime", new JObject { ["dampen"] = 0.5 }));
            Assert.That(Simulate(1f)[0].velocity.magnitude, Is.EqualTo(2f).Within(0.05f), "dampen 0.5 removes half of the excess per step and converges on the limit.");
        }

        [Test]
        public void LimitVelocity_PerAxisLimitsAreSpeedLimitsPerAxis()
        {
            PrepareOneParticle();
            AssertSuccess(ConfigureModules(new JObject
            {
                ["velocityOverLifetime"] = new JObject { ["enabled"] = true, ["x"] = Const(3), ["y"] = Const(4), ["z"] = Const(5) },
                ["limitVelocityOverLifetime"] = new JObject
                {
                    ["enabled"] = true, ["separateAxes"] = true, ["limitX"] = Const(1), ["limitY"] = Const(2), ["limitZ"] = Const(3), ["dampen"] = 1
                }
            }));
            var total = Simulate(0.5f)[0].totalVelocity;
            Assert.That(total.x, Is.EqualTo(1f).Within(1e-3f));
            Assert.That(total.y, Is.EqualTo(2f).Within(1e-3f));
            Assert.That(total.z, Is.EqualTo(3f).Within(1e-3f));
        }

        [Test]
        public void EmitterSpeedAndSpeedMappings_ReadTheCurveOverTheRange()
        {
            PrepareOneParticle();
            AssertSuccess(ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["startLifetime"] = 4, ["emitterVelocityMode"] = "Custom", ["emitterVelocity"] = Vec(5, 0, 0) },
                ["lifetimeByEmitterSpeed"] = new JObject { ["enabled"] = true, ["curve"] = Curve(0, 1), ["range"] = Vec(0, 10) }
            }));
            Assert.That(Simulate(0.1f)[0].startLifetime, Is.EqualTo(2f).Within(1e-3f), "Emitter speed 5 is halfway over 0..10: the lifetime of 4 is halved.");

            StopAndClear(_system);
            AssertSuccess(Configure("lifetimeByEmitterSpeed", new JObject { ["curve"] = 0.25 }));
            Assert.That(Simulate(0.1f)[0].startLifetime, Is.EqualTo(1f).Within(1e-3f), "A constant multiplier ignores the range.");

            StopAndClear(_system);
            AssertSuccess(ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["startLifetime"] = 10, ["startSpeed"] = 5, ["startSize"] = 2, ["startColor"] = new JObject { ["r"] = 1, ["g"] = 1, ["b"] = 1, ["a"] = 1 } },
                ["lifetimeByEmitterSpeed"] = new JObject { ["enabled"] = false },
                ["colorBySpeed"] = new JObject { ["enabled"] = true, ["color"] = BlackToWhite(), ["range"] = Vec(0, 10) },
                ["sizeBySpeed"] = new JObject { ["enabled"] = true, ["size"] = Curve(0, 1), ["range"] = Vec(0, 10) }
            }));
            var particle = Simulate(0.1f)[0];
            Assert.That(particle.GetCurrentColor(_system).r, Is.InRange(127, 129), "Speed 5 over 0..10 is the middle of the gradient.");
            Assert.That(particle.GetCurrentSize(_system), Is.EqualTo(1f).Within(1e-3f), "Half of the start size 2.");
        }

        [Test]
        public void InheritVelocity_ReachesParticlesInWorldSpaceOnly()
        {
            PrepareOneParticle();
            AssertSuccess(ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["emitterVelocityMode"] = "Custom", ["emitterVelocity"] = Vec(10, 0, 0) },
                ["inheritVelocity"] = new JObject { ["enabled"] = true, ["mode"] = "Initial", ["curve"] = Const(1) }
            }));
            Assert.That(Simulate(0.1f)[0].velocity.x, Is.EqualTo(0f).Within(1e-3f), "Local simulation space: no effect, as the warning says.");

            StopAndClear(_system);
            AssertSuccess(ConfigureModules(new JObject
            {
                ["main"] = new JObject { ["simulationSpace"] = "World" },
                ["inheritVelocity"] = new JObject { ["curve"] = Const(1) }
            }));
            Assert.That(Simulate(0.1f)[0].velocity.x, Is.EqualTo(10f).Within(1e-2f), "World space: the emitter velocity is inherited.");
        }

        [Test]
        public void ShapeArc_ConfinesSphereAndHemisphereEmission_AsItDoesCone()
        {
            foreach (var type in new[] { "Sphere", "Hemisphere", "Cone" })
            {
                PrepareOneParticle();
                var emission = _system.emission;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 300) });
                AssertSuccess(Configure("shape", new JObject { ["enabled"] = true, ["shapeType"] = type, ["radius"] = 1, ["arc"] = 90, ["arcMode"] = "Random" }));
                var particles = Simulate(0.1f);
                Assert.That(particles.Length, Is.EqualTo(300), type);
                foreach (var particle in particles)
                {
                    Assert.That(particle.position.x, Is.GreaterThanOrEqualTo(-1e-4f), $"{type}: arc 90 keeps emission inside the first quarter (azimuth 0..90).");
                    Assert.That(particle.position.y, Is.GreaterThanOrEqualTo(-1e-4f), type);
                }
                Assert.That(particles.Any(particle => particle.position.x > 0.1f) && particles.Any(particle => particle.position.y > 0.1f), Is.True, $"{type}: the quarter is actually filled.");
                StopAndClear(_system);
            }
        }

        [Test]
        public void ShapeSpeeds_AreTurnsPerSecondOnAnArc_AndTwoUnitsPerSecondOnAnEdge()
        {
            PrepareOneParticle();
            var emission = _system.emission;
            emission.SetBursts(new ParticleSystem.Burst[0]);
            emission.rateOverTime = 10f;
            AssertSuccess(Configure("shape", new JObject
            {
                ["enabled"] = true, ["shapeType"] = "Circle", ["radius"] = 1, ["radiusThickness"] = 0, ["arc"] = 360,
                ["arcMode"] = "Loop", ["arcSpeed"] = Const(1), ["arcSpread"] = 0
            }));
            var arc = Simulate(0.55f).OrderBy(particle => particle.remainingLifetime).Select(particle =>
            {
                var angle = Mathf.Atan2(particle.position.y, particle.position.x) * Mathf.Rad2Deg;
                return angle < 0f ? angle + 360f : angle;
            }).ToArray();
            Assert.That(arc.Length, Is.GreaterThanOrEqualTo(4));
            for (int i = 1; i < 4; i++)
                Assert.That(arc[i] - arc[i - 1], Is.EqualTo(36f).Within(0.5f), "Speed 1 turns 360 degrees per second: 36 degrees between particles 0.1 s apart.");

            StopAndClear(_system);
            AssertSuccess(Configure("shape", new JObject
            {
                ["shapeType"] = "SingleSidedEdge", ["radius"] = 4, ["radiusMode"] = "Loop", ["radiusSpeed"] = Const(1), ["radiusSpread"] = 0
            }));
            var edge = Simulate(0.55f).OrderBy(particle => particle.remainingLifetime).Select(particle => particle.position.x).ToArray();
            Assert.That(edge.Length, Is.GreaterThanOrEqualTo(4));
            for (int i = 1; i < 4; i++)
                Assert.That(edge[i] - edge[i - 1], Is.EqualTo(0.2f).Within(0.01f), "Speed 1 moves 2 units per second, whatever the radius: 0.2 units between particles 0.1 s apart.");
        }

        [Test]
        public void ShapeBoxThickness_ThickensTheShellPerAxis_AndBoxVolumeIgnoresIt()
        {
            PrepareOneParticle();
            var emission = _system.emission;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 500) });
            AssertSuccess(Configure("shape", new JObject { ["enabled"] = true, ["shapeType"] = "BoxShell", ["boxThickness"] = Vec(0, 0, 0) }));
            var box = _system.shape;
            box.scale = new Vector3(2f, 2f, 2f);
            int onSurface(ParticleSystem.Particle[] particles) => particles.Count(particle => Mathf.Max(Mathf.Abs(particle.position.x), Mathf.Abs(particle.position.y), Mathf.Abs(particle.position.z)) > 0.999f);
            Assert.That(onSurface(Simulate(0.1f)), Is.EqualTo(500), "Thickness 0: every particle is on the surface.");

            StopAndClear(_system);
            AssertSuccess(Configure("shape", new JObject { ["boxThickness"] = Vec(1, 1, 1) }));
            Assert.That(onSurface(Simulate(0.1f)), Is.LessThan(400), "Thickness 1 reaches into the volume.");
        }

        // ===================== creation, dryRun, batches and rollback =====================

        [Test]
        public void Create_ValidatesPlainModulesAgainstTheDefaultsOfANewSystem()
        {
            // inherit velocity on a new (Local space) system, rotation axes over default constant z
            var warned = Json(ParticleSkills.ParticleCreate(name: "CreatedFx", configJson: Config(new JObject
            {
                ["inheritVelocity"] = new JObject { ["enabled"] = true, ["curve"] = 1 },
                ["customData"] = new JObject { ["custom1X"] = 1 }
            })));
            AssertSuccess(warned);
            Assert.That(Warnings(warned).Any(warning => warning.Contains("main.simulationSpace is Local")), Is.True, warned.ToString(Formatting.None));
            Assert.That(Warnings(warned).Any(warning => warning.Contains("custom1X is stored but not used while custom1Mode is Disabled")), Is.True);

            var created = GameObject.Find("CreatedFx").GetComponent<ParticleSystem>();
            Assert.That(created.inheritVelocity.enabled, Is.True);
            Assert.That(created.inheritVelocity.curve.constant, Is.EqualTo(1f));

            var mixed = Json(ParticleSkills.ParticleCreate(name: "MixedFx", configJson: Config(new JObject
            {
                ["rotationOverLifetime"] = new JObject { ["separateAxes"] = true, ["x"] = Two(0, 1), ["y"] = Two(0, 1) }
            })));
            AssertError(mixed, "SEMANTIC_INVALID");
            StringAssert.Contains("z is Constant", mixed["error"]?.ToString(), "The new system's z default takes part in the triplet check.");
            Assert.That(GameObject.Find("MixedFx"), Is.Null, "Nothing was created.");
        }

        [Test]
        public void DryRun_ReportsThePlainModuleProblemsWithoutTouchingTheSystem()
        {
            var before = Serialized();
            var args = new JObject
            {
                ["entityId"] = Id(_system.gameObject),
                ["configJson"] = JObject.Parse(Config(new JObject
                {
                    ["lifetimeByEmitterSpeed"] = new JObject { ["range"] = Vec(5, 1) },
                    ["rotationOverLifetime"] = new JObject { ["x"] = Const(1) }
                }))
            };
            var dry = DryRun("particle_configure", args);
            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            var fields = ((JArray)dry["validation"]["semanticErrors"]).Select(error => error["field"]?.ToString()).ToArray();
            Assert.That(fields, Does.Contain("configJson.modules.lifetimeByEmitterSpeed.range"), dry.ToString(Formatting.None));
            Assert.That(fields, Does.Contain("configJson.modules.rotationOverLifetime.x"), dry.ToString(Formatting.None));
            Assert.That(Serialized(), Is.EqualTo(before));
            var executed = Execute("particle_configure", args);
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), "Execution reaches the dryRun's verdict.");

            var valid = DryRun("particle_configure", new JObject
            {
                ["entityId"] = Id(_system.gameObject),
                ["configJson"] = JObject.Parse(Config(Rich()))
            });
            Assert.That(valid["valid"]?.Value<bool>(), Is.True, valid.ToString(Formatting.None));
            Assert.That(valid["changes"]["modify"].Count(), Is.GreaterThan(40));
            Assert.That(Serialized(), Is.EqualTo(before), "A dryRun never writes.");
        }

        [Test]
        public void ConfigureBatch_AppliesPlainModules_AndOneInvalidItemAppliesNothing()
        {
            var other = MakeSystem("PlainFx2");
            string Items(JObject second, int force) => new JArray(
                new JObject { ["entityId"] = Id(_system.gameObject), ["configJson"] = JObject.Parse(Config(new JObject { ["forceOverLifetime"] = new JObject { ["enabled"] = true, ["x"] = force } })) },
                new JObject { ["entityId"] = Id(other.gameObject), ["configJson"] = JObject.Parse(Config(new JObject { ["customData"] = second })) }).ToString(Formatting.None);

            var ok = Json(ParticleSkills.ParticleConfigureBatch(Items(new JObject { ["custom1Mode"] = "Vector", ["custom1ComponentCount"] = 2 }, 3)));
            Assert.That(ok["success"]?.Value<bool>(), Is.True, ok.ToString(Formatting.None));
            Assert.That(_system.forceOverLifetime.x.constant, Is.EqualTo(3f));
            Assert.That(other.customData.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Vector));

            var bad = Json(ParticleSkills.ParticleConfigureBatch(Items(new JObject { ["custom1ComponentCount"] = 9 }, 4)));
            Assert.That(bad["success"]?.Value<bool>(), Is.False);
            Assert.That(bad["results"][1]["problems"][0]["path"]?.ToString(), Is.EqualTo("items[1].configJson.modules.customData.custom1ComponentCount"));
            Assert.That(other.customData.GetVectorComponentCount(ParticleSystemCustomData.Custom1), Is.EqualTo(2), "The invalid count was never written.");
            Assert.That(_system.forceOverLifetime.x.constant, Is.EqualTo(3f), "The failed batch's valid item 0 (x = 4) was never applied: the first batch's 3 stands.");
        }

        [Test]
        public void FailureWhileApplying_RevertsEveryPlainModuleIncludingCustomData()
        {
            WorkflowManager.BeginTask("p1a-apply-failure", "test");
            var before = Full(_system);
            // Fail on the very last write: customData is the last module of the registry order, so by then every module
            // of the patch (the method-API custom data included) has been written and must be reverted.
            int total = Rich().Properties().Sum(module => ((JObject)module.Value).Count);
            int writes = 0;
            ParticleSkills.AfterWriteHookForTests = write =>
            {
                if (++writes == total)
                    throw new System.InvalidOperationException("injected failure");
            };

            var json = ConfigureModules(Rich());
            AssertError(json, "INTERNAL");
            StringAssert.Contains("rolled back", json["error"]?.ToString());
            Assert.That(writes, Is.EqualTo(total), "The failure hit on the last write of the patch.");
            Assert.That(total, Is.GreaterThan(55), "The rich patch is the large one this test relies on.");
            AssertSameState(before, Full(_system), "Everything written before the failure was reverted.");
            Assert.That(WorkflowManager.CurrentTask.snapshots, Is.Empty, "The failed call's workflow snapshots were removed.");
        }

        [Test]
        public void UnityUndoRedo_RestoresPlainModules()
        {
            var before = Full(_system);
            Undo.IncrementCurrentGroup();
            AssertSuccess(ConfigureModules(Rich()));
            var after = Full(_system);
            Undo.IncrementCurrentGroup();

            Undo.PerformUndo();
            AssertSameState(before, Full(_system), "Undo restores every plain module, custom data included.");
            Assert.That(_system.customData.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Disabled));
            Undo.PerformRedo();
            AssertSameState(after, Full(_system), "Redo re-applies them.");
        }

        [Test]
        public void WorkflowUndoRedo_RestoresPlainModules()
        {
            var before = Full(_system);
            WorkflowManager.BeginTask("p1a-workflow", "test");
            AssertSuccess(ConfigureModules(Rich()));
            WorkflowManager.EndTask();
            var taskId = WorkflowManager.History.tasks.Last().id;
            var after = Full(_system);

            Assert.That(WorkflowManager.UndoTask(taskId).success, Is.True);
            AssertSameState(before, Full(_system), "Workflow undo restores the plain modules.");
            Assert.That(WorkflowManager.RedoTask(taskId).success, Is.True);
            AssertSameState(after, Full(_system), "Workflow redo re-applies them.");
        }

        [Test]
        public void Router_ExecutesAPlainModulePatch_WithDiffAndWorkflowUndo()
        {
            var executed = ExecuteWithDiff("particle_configure", new JObject
            {
                ["entityId"] = Id(_system.gameObject),
                ["configJson"] = JObject.Parse(Config(new JObject { ["forceOverLifetime"] = new JObject { ["enabled"] = true, ["x"] = 2 }, ["customData"] = new JObject { ["custom1Mode"] = "Vector" } }))
            });
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
            Assert.That(_system.forceOverLifetime.x.constant, Is.EqualTo(2f));
            Assert.That(_system.customData.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Vector));
            var changedTypes = executed["sceneDiff"]["changed"].Select(entry => entry["target"]["type"]?.ToString()).ToArray();
            Assert.That(changedTypes, Does.Contain("ParticleSystem"), executed["sceneDiff"].ToString(Formatting.None));

            var task = WorkflowManager.History.tasks.Last();
            Assert.That(task.tag, Is.EqualTo("particle_configure"));
            Assert.That(WorkflowManager.UndoTask(task.id).success, Is.True);
            Assert.That(_system.forceOverLifetime.enabled, Is.False);
            Assert.That(_system.customData.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Disabled));
        }

        [Test]
        public void TransactionalBatch_LaterFailure_RestoresPlainModules()
        {
            var steps = new JArray
            {
                new JObject { ["skill"] = "particle_configure", ["args"] = new JObject { ["entityId"] = Id(_system.gameObject), ["configJson"] = JObject.Parse(Config(Rich())) } },
                new JObject { ["skill"] = "particle_configure", ["args"] = new JObject { ["name"] = "PlainDoesNotExist", ["configJson"] = JObject.Parse(Config(new JObject { ["main"] = new JObject { ["loop"] = false } })) } },
            };
            var before = Full(_system);
            var result = SkillsHttpServer.ExecuteBatchCore(steps, new JObject(), continueOnError: false, dryRun: false, transactional: true, agentId: "tests");
            Assert.That(result["rolledBack"]?.Value<bool>(), Is.True, result.ToString(Formatting.None));
            AssertSameState(before, Full(_system), "The rich plain-module patch of step 1 was rolled back with the batch.");
        }

        [Test]
        public void PrefabInstance_GetsOverridesForPlainModules_AndTheAssetIsUntouched()
        {
            EnsureAssetRoot();
            var prefab = PrefabUtility.SaveAsPrefabAsset(_system.gameObject, AssetRoot + "/PlainFx.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

            var json = Json(ParticleSkills.ParticleConfigure(entityId: Id(instance), configJson: Config(new JObject
            {
                ["forceOverLifetime"] = new JObject { ["enabled"] = true, ["x"] = 3 },
                ["customData"] = new JObject { ["custom1Mode"] = "Vector", ["custom1X"] = 0.5 }
            })));
            AssertSuccess(json);
            var system = instance.GetComponent<ParticleSystem>();
            Assert.That(system.forceOverLifetime.x.constant, Is.EqualTo(3f));
            Assert.That(prefab.GetComponent<ParticleSystem>().forceOverLifetime.enabled, Is.False, "The prefab asset is never changed.");
            Assert.That(prefab.GetComponent<ParticleSystem>().customData.GetMode(ParticleSystemCustomData.Custom1), Is.EqualTo(ParticleSystemCustomDataMode.Disabled));
            var paths = PrefabUtility.GetPropertyModifications(instance).Where(modification => modification.target is ParticleSystem).Select(modification => modification.propertyPath).ToArray();
            Assert.That(paths.Any(path => path.StartsWith("ForceModule.")), Is.True, "The force module write is an instance override: " + string.Join(", ", paths.Take(12)));
            Assert.That(paths.Any(path => path.StartsWith("CustomDataModule.")), Is.True, "So is the custom data write: " + string.Join(", ", paths.Take(12)));
        }

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
    }
}
#endif

// Producer:Betsy
