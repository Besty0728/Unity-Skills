using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The per-project settings store on its own: two project roots in a temp directory, the legacy global values
    /// through a stand-in dictionary (or, read-only, the real EditorPrefs), and unreadable files. Reopening a root
    /// builds a fresh store, which is what a domain reload does.
    /// </summary>
    [TestFixture]
    public class ProjectPrefsTests
    {
        // A key that was a machine-wide EditorPrefs value before 3.0, and one that never was.
        private const string LegacyKey = "UnitySkills_SummaryPageSize";
        private const string ModeKey = "UnitySkills_OperatingMode";
        private const string TelemetryKey = "UnitySkills_TelemetryEnabled";
        private const string ProjectOnlyKey = "UnitySkills_Test_ProjectPrefsOnly";

        private string _tempRoot;

        private string ProjectA => Path.Combine(_tempRoot, "ProjectA");
        private string ProjectB => Path.Combine(_tempRoot, "ProjectB");

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "unityskills-prefs-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }

        [Test]
        public void TwoProjects_KeepTheirOwnValues()
        {
            using (ProjectPrefs.UseForTests(ProjectA))
            {
                ProjectPrefs.SetInt(LegacyKey, 25);
                ProjectPrefs.SetString(ModeKey, "Approval");
            }
            using (ProjectPrefs.UseForTests(ProjectB))
            {
                Assert.That(ProjectPrefs.HasKey(LegacyKey), Is.False);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(10));
                Assert.That(ProjectPrefs.GetString(ModeKey, "none"), Is.EqualTo("none"));
                ProjectPrefs.SetInt(LegacyKey, 5);
            }
            using (ProjectPrefs.UseForTests(ProjectA))
            {
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(25));
                Assert.That(ProjectPrefs.GetString(ModeKey, "none"), Is.EqualTo("Approval"));
            }
            using (ProjectPrefs.UseForTests(ProjectB))
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(5));
        }

        [Test]
        public void File_IsSchemaOneJsonWithoutBom_InUserSettings()
        {
            using (ProjectPrefs.UseForTests(ProjectA))
            {
                Assert.That(ProjectPrefs.SettingsFilePath,
                    Is.EqualTo(Path.Combine(ProjectA, "UserSettings", "UnitySkills", "settings.json")));
                ProjectPrefs.SetString("UnitySkills_SurfaceProfile", "guide");
                ProjectPrefs.SetBool(TelemetryKey, false);
                ProjectPrefs.SetInt(LegacyKey, 20);
            }

            var file = ProjectPrefs.SettingsFileFor(ProjectA);
            var bytes = File.ReadAllBytes(file);
            Assert.That(bytes[0], Is.EqualTo((byte)'{'), "UTF-8 without BOM");
            var root = JObject.Parse(Encoding.UTF8.GetString(bytes));
            Assert.That(root.Value<int>("schema"), Is.EqualTo(1));
            var values = (JObject)root["values"];
            Assert.That(values.Properties().Select(p => p.Name),
                Is.EqualTo(new[] { LegacyKey, "UnitySkills_SurfaceProfile", TelemetryKey }), "keys sorted ordinally");
            Assert.That(values[LegacyKey].Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(values[TelemetryKey].Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(values.Value<string>("UnitySkills_SurfaceProfile"), Is.EqualTo("guide"));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(file)), Is.EqualTo(new[] { file }),
                "the atomic write leaves no temp file behind");
        }

        [Test]
        public void ReadingAlone_CreatesNothing_TheFirstChangeCreatesTheFile()
        {
            using (ProjectPrefs.UseForTests(ProjectA))
            {
                Assert.That(ProjectPrefs.HasKey(ProjectOnlyKey), Is.False);
                Assert.That(ProjectPrefs.GetBool(ProjectOnlyKey, true), Is.True);
                Assert.That(Directory.Exists(Path.Combine(ProjectA, "UserSettings")), Is.False);

                ProjectPrefs.SetBool(ProjectOnlyKey, false);
                Assert.That(File.Exists(ProjectPrefs.SettingsFileFor(ProjectA)), Is.True);
                Assert.That(ProjectPrefs.GetBool(ProjectOnlyKey, true), Is.False);

                ProjectPrefs.DeleteKey(ProjectOnlyKey);
                Assert.That(ProjectPrefs.HasKey(ProjectOnlyKey), Is.False);
            }
            using (ProjectPrefs.UseForTests(ProjectA))
                Assert.That(ProjectPrefs.HasKey(ProjectOnlyKey), Is.False, "the deletion was saved");
        }

        [Test]
        public void InMemoryStore_NeverTouchesTheDisk()
        {
            using (ProjectPrefs.UseForTests())
            {
                Assert.That(ProjectPrefs.SettingsFilePath, Is.Null);
                ProjectPrefs.SetString(ModeKey, "Approval");
                Assert.That(ProjectPrefs.GetString(ModeKey), Is.EqualTo("Approval"));
            }
            using (ProjectPrefs.UseForTests())
                Assert.That(ProjectPrefs.HasKey(ModeKey), Is.False, "every in-memory store starts empty");
        }

        [Test]
        public void LegacyGlobalValue_IsReadUntilTheProjectHasItsOwn()
        {
            var legacy = new Dictionary<string, object>
            {
                [LegacyKey] = 33,
                [ModeKey] = "Approval",
                [TelemetryKey] = false,
            };
            using (ProjectPrefs.UseForTests(ProjectA, legacy))
            {
                Assert.That(ProjectPrefs.HasKey(LegacyKey), Is.True);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(33));
                Assert.That(ProjectPrefs.GetString(ModeKey, "none"), Is.EqualTo("Approval"));
                Assert.That(ProjectPrefs.GetBool(TelemetryKey, true), Is.False);
                Assert.That(File.Exists(ProjectPrefs.SettingsFileFor(ProjectA)), Is.False, "reading never writes");

                ProjectPrefs.SetInt(LegacyKey, 12);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(12), "the project value wins");

                ProjectPrefs.DeleteKey(LegacyKey);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(33),
                    "deleting the project value exposes the global one again");
            }
            using (ProjectPrefs.UseForTests(ProjectB, legacy))
            {
                Assert.That(ProjectPrefs.GetString(ModeKey, "none"), Is.EqualTo("Approval"),
                    "another project still starts from the global value");
            }
            Assert.That(legacy, Is.EqualTo(new Dictionary<string, object>
            {
                [LegacyKey] = 33,
                [ModeKey] = "Approval",
                [TelemetryKey] = false,
            }));
        }

        [Test]
        public void KeysThatWereNeverGlobal_HaveNoFallback()
        {
            var legacy = new Dictionary<string, object>
            {
                [ProjectOnlyKey] = "global",
                [EvaluationModeGuard.PrefKey] = "{\"schema\":1}",
            };
            using (ProjectPrefs.UseForTests(legacyValues: legacy))
            {
                Assert.That(ProjectPrefs.HasKey(ProjectOnlyKey), Is.False);
                Assert.That(ProjectPrefs.GetString(ProjectOnlyKey, "default"), Is.EqualTo("default"));
                Assert.That(ProjectPrefs.HasKey(EvaluationModeGuard.PrefKey), Is.False);
            }
        }

        [Test]
        public void Writes_NeverTouchTheGlobalEditorPrefs()
        {
            // The editor's own configuration: the real EditorPrefs is the legacy source. This test only reads it.
            bool hadGlobal = EditorPrefs.HasKey(LegacyKey);
            int globalValue = EditorPrefs.GetInt(LegacyKey, -12345);
            using (ProjectPrefs.UseForTests(ProjectA, legacyFromEditorPrefs: true))
            {
                Assert.That(ProjectPrefs.HasKey(LegacyKey), Is.EqualTo(hadGlobal));
                Assert.That(ProjectPrefs.GetInt(LegacyKey, -12345), Is.EqualTo(globalValue));

                ProjectPrefs.SetInt(LegacyKey, globalValue == 77 ? 78 : 77);
                ProjectPrefs.DeleteKey(LegacyKey);
                ProjectPrefs.SetInt(LegacyKey, globalValue == 91 ? 92 : 91);
                ProjectPrefs.SetBool(ProjectOnlyKey, true);
            }

            Assert.That(EditorPrefs.HasKey(LegacyKey), Is.EqualTo(hadGlobal));
            Assert.That(EditorPrefs.GetInt(LegacyKey, -12345), Is.EqualTo(globalValue));
            Assert.That(EditorPrefs.HasKey(ProjectOnlyKey), Is.False);
            var values = (JObject)JObject.Parse(File.ReadAllText(ProjectPrefs.SettingsFileFor(ProjectA)))["values"];
            Assert.That(values.Value<int>(LegacyKey), Is.EqualTo(globalValue == 91 ? 92 : 91));
        }

        [Test]
        public void UnknownKeys_AndValuesOfAnotherType_SurviveARewrite()
        {
            var file = ProjectPrefs.SettingsFileFor(ProjectA);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,
                "{\"schema\":1,\"values\":{\"Future_Key\":{\"nested\":[1,2]},\"" + LegacyKey + "\":\"oops\"}}");

            using (ProjectPrefs.UseForTests(ProjectA, new Dictionary<string, object> { [LegacyKey] = 33 }))
            {
                Assert.That(ProjectPrefs.HasKey("Future_Key"), Is.True);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(33),
                    "a value of the wrong type is skipped like an absent one");
                ProjectPrefs.SetBool(ProjectOnlyKey, true);
            }

            var values = (JObject)JObject.Parse(File.ReadAllText(file))["values"];
            Assert.That(JToken.DeepEquals(values["Future_Key"], JToken.Parse("{\"nested\":[1,2]}")), Is.True);
            Assert.That(values.Value<string>(LegacyKey), Is.EqualTo("oops"));
            Assert.That(values.Value<bool>(ProjectOnlyKey), Is.True);
        }

        [TestCase("{not json")]
        [TestCase("")]
        [TestCase("[1,2]")]
        [TestCase("{\"schema\":2,\"values\":{}}")]
        [TestCase("{\"schema\":1,\"values\":[]}")]
        [TestCase("{\"values\":{\"UnitySkills_SummaryPageSize\":20}}")]
        public void UnreadableFile_FallsBack_AndIsNeverOverwritten(string content)
        {
            var file = ProjectPrefs.SettingsFileFor(ProjectA);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content);

            ExpectLog(LogLevel.Warning, LogType.Warning, "Could not read the project settings file");
            // Only the first refused change logs; a second one would fail the test as an unexpected error.
            ExpectLog(LogLevel.Error, LogType.Error, "changed for this session only");
            using (ProjectPrefs.UseForTests(ProjectA, new Dictionary<string, object> { [LegacyKey] = 33 }))
            {
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(33), "falls back to the global value");
                Assert.That(ProjectPrefs.GetBool(TelemetryKey, true), Is.True, "falls back to the default");

                ProjectPrefs.SetInt(LegacyKey, 20);
                ProjectPrefs.SetBool(TelemetryKey, false);
                Assert.That(ProjectPrefs.GetInt(LegacyKey, 10), Is.EqualTo(20), "the change applies for this session");
                Assert.That(ProjectPrefs.GetBool(TelemetryKey, true), Is.False);
            }

            Assert.That(File.ReadAllText(file), Is.EqualTo(content), "the unreadable file is left untouched");
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(file)), Is.EqualTo(new[] { file }));
            LogAssert.NoUnexpectedReceived();
        }

        private static void ExpectLog(LogLevel needed, LogType type, string text)
        {
            // SkillsLogger honours the user's log level; only expect what it will actually print.
            if (SkillsLogger.Level >= needed)
                LogAssert.Expect(type, new Regex(Regex.Escape(text)));
        }
    }
}

// Producer:Betsy
