using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Macro file (schemaVersion 1) build / parse round-trip and strict-validation tests
    /// for <see cref="MacroFileStore"/>. Pure layer only — no recorder session or file IO —
    /// so they run stably in EditMode (style aligned with ShortcutConflictTests).
    /// </summary>
    [TestFixture]
    public class MacroFileStoreTests
    {
        private static JArray SampleSteps()
        {
            return new JArray
            {
                new JObject
                {
                    ["skill"] = "gameobject_create",
                    ["args"] = new JObject { ["name"] = "Cube", ["primitiveType"] = "Cube" }
                },
                new JObject
                {
                    ["skill"] = "gameobject_rename",
                    ["args"] = new JObject
                    {
                        ["instanceId"] = new JObject { ["$ref"] = "$0.instanceId" },
                        ["newName"] = "Hero"
                    }
                }
            };
        }

        [Test]
        public void BuildThenParse_PreservesFields()
        {
            var steps = SampleSteps();
            var warnings = new List<string> { "example warning" };
            var json = MacroFileStore.BuildJson(steps, warnings, replayable: true,
                note: "my session", recordedAtUtc: "2026-07-09T10:00:00.000Z",
                unityVersion: "6000.3.9f1", sceneName: "SampleScene").ToString();

            Assert.IsTrue(MacroFileStore.TryParse(json, out var file, out var error), error);
            Assert.AreEqual(MacroFileStore.CurrentSchemaVersion, file.SchemaVersion);
            Assert.AreEqual("my session", file.Note);
            Assert.AreEqual("2026-07-09T10:00:00.000Z", file.RecordedAtUtc);
            Assert.AreEqual("6000.3.9f1", file.UnityVersion);
            Assert.AreEqual("SampleScene", file.SceneName);
            Assert.IsTrue(file.Replayable);
            Assert.AreEqual(2, file.Steps.Count);
            Assert.AreEqual("gameobject_create", file.Steps[0]["skill"].ToString());
            Assert.AreEqual("Hero", file.Steps[1]["args"]["newName"].ToString());
            Assert.AreEqual(1, file.Warnings.Count);
            Assert.IsNotNull(file.Params);
            Assert.AreEqual(0, file.Params.Count);
        }

        [Test]
        public void Build_DefaultsParamsToEmptyObject()
        {
            var json = MacroFileStore.BuildJson(SampleSteps(), null, replayable: false,
                note: null, recordedAtUtc: null, unityVersion: null, sceneName: null);
            var root = JObject.Parse(json.ToString());
            Assert.IsInstanceOf<JObject>(root["params"]);
            Assert.AreEqual(0, ((JObject)root["params"]).Count);
        }

        [Test]
        public void Parse_PreservesUserParams()
        {
            var root = MacroFileStore.BuildJson(SampleSteps(), null, replayable: true,
                note: null, recordedAtUtc: null, unityVersion: null, sceneName: null);
            root["params"] = new JObject { ["height"] = 3 };

            Assert.IsTrue(MacroFileStore.TryParse(root.ToString(), out var file, out var error), error);
            Assert.AreEqual(1, file.Params.Count);
            Assert.AreEqual(3, file.Params["height"].Value<int>());
        }

        [Test]
        public void Parse_UnknownSchemaVersion_Fails()
        {
            var root = MacroFileStore.BuildJson(SampleSteps(), null, true, null, null, null, null);
            root["schemaVersion"] = 99;
            Assert.IsFalse(MacroFileStore.TryParse(root.ToString(), out _, out var error));
            StringAssert.Contains("schemaVersion", error);
        }

        [Test]
        public void Parse_MissingSchemaVersion_Fails()
        {
            var root = new JObject { ["steps"] = SampleSteps() };
            Assert.IsFalse(MacroFileStore.TryParse(root.ToString(), out _, out var error));
            StringAssert.Contains("schemaVersion", error);
        }

        [Test]
        public void Parse_StepsNotArray_Fails()
        {
            var root = new JObject
            {
                ["schemaVersion"] = MacroFileStore.CurrentSchemaVersion,
                ["steps"] = "not an array"
            };
            Assert.IsFalse(MacroFileStore.TryParse(root.ToString(), out _, out var error));
            StringAssert.Contains("steps", error);
        }

        [Test]
        public void Parse_StepWithoutSkill_Fails()
        {
            var root = new JObject
            {
                ["schemaVersion"] = MacroFileStore.CurrentSchemaVersion,
                ["steps"] = new JArray { new JObject { ["args"] = new JObject() } }
            };
            Assert.IsFalse(MacroFileStore.TryParse(root.ToString(), out _, out var error));
            StringAssert.Contains("skill", error);
        }

        [Test]
        public void Parse_MalformedJson_Fails()
        {
            Assert.IsFalse(MacroFileStore.TryParse("{ this is not json", out _, out var error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Parse_NonObjectParams_Fails()
        {
            var root = MacroFileStore.BuildJson(SampleSteps(), null, true, null, null, null, null);
            root["params"] = new JArray();
            Assert.IsFalse(MacroFileStore.TryParse(root.ToString(), out _, out var error));
            StringAssert.Contains("params", error);
        }
    }
}
