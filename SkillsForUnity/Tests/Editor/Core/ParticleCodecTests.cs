#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The value contract through a real ParticleSystem: every MinMaxCurve and MinMaxGradient mode, keyframe tangents /
    /// weights / wrap modes / tangent modes / steps, HDR colours and gradient keys survive write -> readback -> write ->
    /// readback unchanged, and malformed values are rejected with their JSON path while nothing is written.
    /// </summary>
    [TestFixture]
    public class ParticleCodecTests : ParticleTestBase
    {
        private ParticleSystem _system;

        [SetUp]
        public void CreateSystem() => _system = MakeSystem("CodecFx");

        /// <summary>Writes one main/colorOverLifetime field, reads it back, writes the readback again: both readbacks must match.</summary>
        private JToken RoundTrip(string module, string field, JToken value)
        {
            var first = Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject),
                configJson: Config(new JObject { [module] = new JObject { [field] = value } })));
            AssertSuccess(first);
            Assert.That(first["adjustments"], Is.Null, first.ToString(Formatting.None));
            var readback = Field(_system, module, field);

            var second = Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject),
                configJson: Config(new JObject { [module] = new JObject { [field] = readback.DeepClone() } })));
            AssertSuccess(second);
            var again = Field(_system, module, field);
            Assert.That(JToken.DeepEquals(readback, again), Is.True, $"Round trip changed the value:\n{readback}\n->\n{again}");
            return readback;
        }

        [Test]
        public void Curve_Constant_RoundTrips()
        {
            var back = RoundTrip("main", "startSpeed", JObject.Parse("{\"mode\":\"Constant\",\"constant\":2.5}"));
            Assert.That(back.ToString(Formatting.None), Is.EqualTo("{\"mode\":\"Constant\",\"constant\":2.5}"));
        }

        [Test]
        public void Curve_BareNumber_IsConstantShorthand()
        {
            var back = RoundTrip("main", "startSpeed", new JValue(0.3));
            Assert.That(back["mode"]?.ToString(), Is.EqualTo("Constant"));
            Assert.That(back["constant"]?.Value<float>(), Is.EqualTo(0.3f).Within(1e-6f));
        }

        [Test]
        public void Curve_TwoConstants_RoundTrips()
        {
            var back = RoundTrip("main", "startLifetime", JObject.Parse("{\"mode\":\"TwoConstants\",\"min\":0.5,\"max\":1.25}"));
            Assert.That(back.ToString(Formatting.None), Is.EqualTo("{\"mode\":\"TwoConstants\",\"min\":0.5,\"max\":1.25}"));
        }

        [Test]
        public void Curve_Curve_KeepsTangentsWeightsWrapModesAndSteps()
        {
            var value = JObject.Parse(@"{
                ""mode"": ""Curve"", ""multiplier"": 2.5,
                ""curve"": {
                    ""preWrapMode"": ""PingPong"", ""postWrapMode"": ""Loop"",
                    ""keys"": [
                        {""time"": 0, ""value"": 0, ""inTangent"": 0, ""rightTangentMode"": ""Constant""},
                        {""time"": 0.5, ""value"": 0.8, ""inTangent"": 2, ""outTangent"": -1, ""inWeight"": 0.2, ""outWeight"": 0.7, ""weightedMode"": ""Both"", ""broken"": true},
                        {""time"": 1, ""value"": 1, ""outTangent"": 0, ""leftTangentMode"": ""Linear""}
                    ]
                }
            }");
            var back = RoundTrip("main", "startLifetime", value);

            Assert.That(back["mode"]?.ToString(), Is.EqualTo("Curve"));
            Assert.That(back["multiplier"]?.Value<float>(), Is.EqualTo(2.5f));
            var curve = back["curve"];
            Assert.That(curve["preWrapMode"]?.ToString(), Is.EqualTo("PingPong"));
            Assert.That(curve["postWrapMode"]?.ToString(), Is.EqualTo("Loop"));
            var keys = (JArray)curve["keys"];
            Assert.That(keys.Count, Is.EqualTo(3));
            Assert.That(keys[0]["rightTangentMode"]?.ToString(), Is.EqualTo("Constant"));
            Assert.That(keys[0]["outTangent"], Is.Null, "A stepped side carries no tangent value (Infinity is not JSON).");
            Assert.That(keys[1]["inTangent"]?.Value<float>(), Is.EqualTo(2f));
            Assert.That(keys[1]["outTangent"]?.Value<float>(), Is.EqualTo(-1f));
            Assert.That(keys[1]["inWeight"]?.Value<float>(), Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(keys[1]["outWeight"]?.Value<float>(), Is.EqualTo(0.7f).Within(1e-6f));
            Assert.That(keys[1]["weightedMode"]?.ToString(), Is.EqualTo("Both"));
            Assert.That(keys[1]["broken"]?.Value<bool>(), Is.True);
            Assert.That(keys[2]["leftTangentMode"]?.ToString(), Is.EqualTo("Linear"));
            Assert.That(keys[2]["inTangent"]?.Value<float>(), Is.EqualTo(0.4f).Within(1e-5f), "A Linear side is computed by Unity from the neighbouring key.");
        }

        [Test]
        public void Curve_TwoCurves_RoundTrips()
        {
            var value = JObject.Parse(@"{""mode"":""TwoCurves"",""multiplier"":3,
                ""minCurve"":{""keys"":[{""time"":0,""value"":0},{""time"":1,""value"":1}]},
                ""maxCurve"":{""keys"":[{""time"":0,""value"":1,""leftTangentMode"":""ClampedAuto"",""rightTangentMode"":""ClampedAuto""},{""time"":1,""value"":2}]}}");
            var back = RoundTrip("main", "startSpeed", value);
            Assert.That(back["mode"]?.ToString(), Is.EqualTo("TwoCurves"));
            Assert.That(back["multiplier"]?.Value<float>(), Is.EqualTo(3f));
            Assert.That(((JArray)back["minCurve"]["keys"]).Count, Is.EqualTo(2));
            Assert.That(back["maxCurve"]["keys"][0]["leftTangentMode"]?.ToString(), Is.EqualTo("ClampedAuto"));
            Assert.That(back["minCurve"]["preWrapMode"]?.ToString(), Is.EqualTo("ClampForever"), "Omitted wrap modes default to ClampForever.");
        }

        [Test]
        public void Curve_UnsortedKeys_AreStoredSortedByTime()
        {
            var back = RoundTrip("main", "startSpeed", JObject.Parse(@"{""mode"":""Curve"",""multiplier"":1,""curve"":{""keys"":[{""time"":1,""value"":5},{""time"":0,""value"":2}]}}"));
            Assert.That(back["curve"]["keys"][0]["time"]?.Value<float>(), Is.EqualTo(0f));
            Assert.That(back["curve"]["keys"][0]["value"]?.Value<float>(), Is.EqualTo(2f));
        }

        [Test]
        public void Gradient_Color_KeepsHdrChannels()
        {
            var back = RoundTrip("main", "startColor", JObject.Parse("{\"mode\":\"Color\",\"color\":{\"r\":3,\"g\":1,\"b\":0.5,\"a\":0.25}}"));
            Assert.That(back.ToString(Formatting.None), Is.EqualTo("{\"mode\":\"Color\",\"color\":{\"r\":3.0,\"g\":1.0,\"b\":0.5,\"a\":0.25}}"));
        }

        [Test]
        public void Gradient_BareColour_IsColorShorthand()
        {
            var back = RoundTrip("main", "startColor", JObject.Parse("{\"r\":1,\"g\":0.5,\"b\":0}"));
            Assert.That(back["mode"]?.ToString(), Is.EqualTo("Color"));
            Assert.That(back["color"]["a"]?.Value<float>(), Is.EqualTo(1f), "Alpha defaults to 1.");
        }

        [Test]
        public void Gradient_TwoColors_RoundTrips()
        {
            var back = RoundTrip("main", "startColor", JObject.Parse("{\"mode\":\"TwoColors\",\"minColor\":{\"r\":1,\"g\":0,\"b\":0,\"a\":1},\"maxColor\":{\"r\":0,\"g\":1,\"b\":0,\"a\":0.5}}"));
            Assert.That(back["minColor"]["r"]?.Value<float>(), Is.EqualTo(1f));
            Assert.That(back["maxColor"]["a"]?.Value<float>(), Is.EqualTo(0.5f));
        }

        private static JObject SampleGradient(string mode = "Fixed") => JObject.Parse(@"{
            ""mode"": """ + mode + @""", ""colorSpace"": ""Linear"",
            ""colorKeys"": [{""time"": 0, ""color"": {""r"": 2, ""g"": 0.5, ""b"": 0.1}}, {""time"": 1, ""color"": {""r"": 0.2, ""g"": 0.2, ""b"": 0.2}}],
            ""alphaKeys"": [{""time"": 0, ""alpha"": 0}, {""time"": 0.2, ""alpha"": 1}, {""time"": 1, ""alpha"": 0}]
        }");

        [Test]
        public void Gradient_Gradient_KeepsModeColorSpaceAndKeys()
        {
            var back = RoundTrip("colorOverLifetime", "color", new JObject { ["mode"] = "Gradient", ["gradient"] = SampleGradient() });
            var gradient = back["gradient"];
            Assert.That(gradient["mode"]?.ToString(), Is.EqualTo("Fixed"));
            Assert.That(gradient["colorSpace"]?.ToString(), Is.EqualTo("Linear"));
            Assert.That(((JArray)gradient["colorKeys"]).Count, Is.EqualTo(2));
            Assert.That(gradient["colorKeys"][0]["color"]["r"]?.Value<float>(), Is.EqualTo(2f), "HDR colour keys are kept.");
            Assert.That(gradient["colorKeys"][0]["color"]["a"], Is.Null, "Colour keys carry no alpha.");
            Assert.That(((JArray)gradient["alphaKeys"]).Count, Is.EqualTo(3));
            Assert.That(gradient["alphaKeys"][1]["time"]?.Value<float>(), Is.EqualTo(0.2f).Within(2e-5f), "Unity quantizes key times to 1/65535.");
        }

        [Test]
        public void Gradient_TwoGradients_And_RandomColor_RoundTrip()
        {
            var two = RoundTrip("main", "startColor", new JObject { ["mode"] = "TwoGradients", ["minGradient"] = SampleGradient("Blend"), ["maxGradient"] = SampleGradient("PerceptualBlend") });
            Assert.That(two["mode"]?.ToString(), Is.EqualTo("TwoGradients"));
            Assert.That(two["maxGradient"]["mode"]?.ToString(), Is.EqualTo("PerceptualBlend"));

            var random = RoundTrip("main", "startColor", new JObject { ["mode"] = "RandomColor", ["gradient"] = SampleGradient() });
            Assert.That(random["mode"]?.ToString(), Is.EqualTo("RandomColor"));
            Assert.That(((JArray)random["gradient"]["alphaKeys"]).Count, Is.EqualTo(3));
        }

        // ---------- rejection: nothing is written, the path names the bad value ----------

        private void AssertRejected(string configJson, string expectedPath, string errorCode = "SEMANTIC_INVALID")
        {
            var before = Json(ParticleSkills.ParticleGetInfo(entityId: Id(_system.gameObject), includeModules: true));
            var json = Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject), configJson: configJson));
            AssertError(json, errorCode);
            var paths = ((JArray)json["problems"]).Select(problem => problem["path"]?.ToString()).ToArray();
            Assert.That(paths, Does.Contain(expectedPath), json.ToString(Formatting.None));
            var after = Json(ParticleSkills.ParticleGetInfo(entityId: Id(_system.gameObject), includeModules: true));
            Assert.That(JToken.DeepEquals(before, after), Is.True, "A rejected call must not change the system.");
        }

        [Test]
        public void Rejects_UnknownKeyInsideACurve()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startSpeed\":{\"mode\":\"Constant\",\"constant\":1,\"cosntant\":2}}}}",
                "configJson.modules.main.startSpeed.cosntant");
        }

        [Test]
        public void Rejects_DuplicateKeys_InsteadOfKeepingTheLast()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":2,\"duration\":3}}}",
                "configJson.modules.main.duration", "INVALID_JSON");
        }

        [Test]
        public void Rejects_NullForANonNullableField()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"loop\":null}}}", "configJson.modules.main.loop");
        }

        [Test]
        public void Rejects_WrongJsonTypes()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":\"5\"}}}", "configJson.modules.main.duration", "TYPE_MISMATCH");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"maxParticles\":10.5}}}", "configJson.modules.main.maxParticles", "TYPE_MISMATCH");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"loop\":1}}}", "configJson.modules.main.loop", "TYPE_MISMATCH");
        }

        [Test]
        public void Rejects_NonFiniteNumbers()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":NaN}}}", "configJson.modules.main.duration");
        }

        [Test]
        public void Rejects_IllegalEnumValues_ByNameAndByNumber()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"simulationSpace\":\"Galactic\"}}}", "configJson.modules.main.simulationSpace");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"simulationSpace\":1}}}", "configJson.modules.main.simulationSpace", "TYPE_MISMATCH");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"shape\":{\"shapeType\":\"SphereShell\"}}}", "configJson.modules.shape.shapeType");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"stopAction\":\"Destroy\"}}}", "configJson.modules.main.stopAction");
        }

        [Test]
        public void Rejects_OutOfRangeValues()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":0}}}", "configJson.modules.main.duration");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"shape\":{\"angle\":120}}}", "configJson.modules.shape.angle");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startLifetime\":0}}}", "configJson.modules.main.startLifetime");
        }

        [Test]
        public void Rejects_TwoConstantsWithMinAboveMax()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startSpeed\":{\"mode\":\"TwoConstants\",\"min\":3,\"max\":1}}}}",
                "configJson.modules.main.startSpeed.min");
        }

        [Test]
        public void Rejects_CurveModeTheFieldDoesNotOffer()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startDelay\":{\"mode\":\"Curve\",\"multiplier\":1,\"curve\":{\"keys\":[{\"time\":0,\"value\":0}]}}}}}",
                "configJson.modules.main.startDelay.mode");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"colorOverLifetime\":{\"color\":{\"mode\":\"Color\",\"color\":{\"r\":1,\"g\":0,\"b\":0}}}}}",
                "configJson.modules.colorOverLifetime.color.mode");
        }

        [Test]
        public void Rejects_RepeatedAndOutOfRangeKeyTimes()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startSpeed\":{\"mode\":\"Curve\",\"multiplier\":1,\"curve\":{\"keys\":[{\"time\":0.5,\"value\":0},{\"time\":0.5,\"value\":1}]}}}}}",
                "configJson.modules.main.startSpeed.curve.keys[1]");
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startSpeed\":{\"mode\":\"Curve\",\"multiplier\":1,\"curve\":{\"keys\":[{\"time\":-1,\"value\":0},{\"time\":2,\"value\":1}]}}}}}",
                "configJson.modules.main.startSpeed.curve.keys[0].time");
        }

        [Test]
        public void Rejects_ATangentValueOnASteppedSide()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"main\":{\"startSpeed\":{\"mode\":\"Curve\",\"multiplier\":1,\"curve\":{\"keys\":[{\"time\":0,\"value\":0,\"outTangent\":1,\"rightTangentMode\":\"Constant\"}]}}}}}",
                "configJson.modules.main.startSpeed.curve.keys[0].outTangent");
        }

        [Test]
        public void Rejects_AlphaOnAGradientColourKey_AndTooManyKeys()
        {
            AssertRejected("{\"schemaVersion\":1,\"modules\":{\"colorOverLifetime\":{\"color\":{\"mode\":\"Gradient\",\"gradient\":{\"colorKeys\":[{\"time\":0,\"color\":{\"r\":1,\"g\":1,\"b\":1,\"a\":0.5}}],\"alphaKeys\":[{\"time\":0,\"alpha\":1}]}}}}}",
                "configJson.modules.colorOverLifetime.color.gradient.colorKeys[0].color.a");

            var keys = new JArray(Enumerable.Range(0, 9).Select(i => new JObject { ["time"] = i / 8.0, ["color"] = new JObject { ["r"] = 1, ["g"] = 1, ["b"] = 1 } }));
            var config = new JObject
            {
                ["schemaVersion"] = 1,
                ["modules"] = new JObject { ["colorOverLifetime"] = new JObject { ["color"] = new JObject { ["mode"] = "Gradient", ["gradient"] = new JObject { ["colorKeys"] = keys, ["alphaKeys"] = new JArray(new JObject { ["time"] = 0, ["alpha"] = 1 }) } } } }
            };
            AssertRejected(config.ToString(Formatting.None), "configJson.modules.colorOverLifetime.color.gradient.colorKeys");
        }

        [Test]
        public void Rejects_EveryProblemOfACall_InOneResponse()
        {
            var json = Json(ParticleSkills.ParticleConfigure(entityId: Id(_system.gameObject),
                configJson: "{\"schemaVersion\":1,\"modules\":{\"main\":{\"duration\":-1,\"loop\":\"yes\",\"nope\":1},\"emission\":{\"rateOverTime\":-3}}}"));
            var paths = ((JArray)json["problems"]).Select(problem => problem["path"]?.ToString()).ToArray();
            Assert.That(paths, Is.SupersetOf(new[]
            {
                "configJson.modules.main.duration", "configJson.modules.main.loop", "configJson.modules.main.nope", "configJson.modules.emission.rateOverTime"
            }), json.ToString(Formatting.None));
        }
    }
}
#endif

// Producer:Betsy
