using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine.TestTools;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Macro library (Library/UnitySkillsMacros) CRUD round-trip and validation tests for
    /// <see cref="MacroLibraryStore"/>. Pure file/naming layer redirected to a temp directory
    /// (OverrideDirForTests) — no recorder session, no REST, no scene — so they run stably in
    /// EditMode (style aligned with MacroFileStoreTests). Verdicts rely on explicit Asserts
    /// only; unexpected editor log noise must not fail the fixture.
    /// </summary>
    [TestFixture]
    public class MacroLibraryTests
    {
        private string _tempDir;
        private string _savedOverride;
        private bool _savedIgnoreFailing;

        private string _tempGlobalDir;
        private string _savedGlobalOverride;

        [SetUp]
        public void SetUp()
        {
            _savedIgnoreFailing = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;

            _savedOverride = MacroLibraryStore.OverrideDirForTests;
            _tempDir = Path.Combine(Path.GetTempPath(), "UnitySkillsMacroLibTests_" + Path.GetRandomFileName());
            MacroLibraryStore.OverrideDirForTests = _tempDir;

            _savedGlobalOverride = MacroLibraryStore.OverrideGlobalDirForTests;
            _tempGlobalDir = Path.Combine(Path.GetTempPath(), "UnitySkillsMacroLibTestsG_" + Path.GetRandomFileName());
            MacroLibraryStore.OverrideGlobalDirForTests = _tempGlobalDir;
        }

        [TearDown]
        public void TearDown()
        {
            MacroLibraryStore.OverrideDirForTests = _savedOverride;
            MacroLibraryStore.OverrideGlobalDirForTests = _savedGlobalOverride;
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
            if (Directory.Exists(_tempGlobalDir))
                Directory.Delete(_tempGlobalDir, recursive: true);
            LogAssert.ignoreFailingMessages = _savedIgnoreFailing;
        }

        private static JObject SampleMacro(string note = "lib test")
        {
            var steps = new JArray
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
                        ["newName"] = new JObject { ["$param"] = "heroName", ["default"] = "Hero" }
                    }
                }
            };
            return MacroFileStore.BuildJson(steps, new List<string>(), replayable: true,
                note: note, recordedAtUtc: "2026-07-10T08:00:00.000Z",
                unityVersion: "6000.3.9f1", sceneName: "SampleScene");
        }

        [Test]
        public void SaveListGetDelete_Roundtrip()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("build-hero", SampleMacro(), overwrite: false, out var saveError), saveError);

            var names = MacroLibraryStore.ListNames();
            Assert.AreEqual(1, names.Count);
            Assert.AreEqual("build-hero", names[0]);

            Assert.IsTrue(MacroLibraryStore.TryLoad("build-hero", out var file, out var sizeBytes, out var loadError), loadError);
            Assert.Greater(sizeBytes, 0);
            Assert.AreEqual(2, file.Steps.Count);
            Assert.AreEqual("gameobject_create", file.Steps[0]["skill"].ToString());
            Assert.AreEqual("lib test", file.Note);
            Assert.IsTrue(file.Replayable);

            Assert.IsTrue(MacroLibraryStore.TryDelete("build-hero", out var deleteError), deleteError);
            Assert.AreEqual(0, MacroLibraryStore.ListNames().Count);
            Assert.IsFalse(MacroLibraryStore.Exists("build-hero"));
        }

        [Test]
        public void List_EmptyLibrary_ReturnsEmptyNotError()
        {
            Assert.AreEqual(0, MacroLibraryStore.ListNames().Count);
        }

        [Test]
        public void Save_DuplicateWithoutOverwrite_Fails()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("dup", SampleMacro(), overwrite: false, out _));
            Assert.IsFalse(MacroLibraryStore.TrySave("dup", SampleMacro(), overwrite: false, out var error));
            StringAssert.Contains("overwrite", error);
        }

        [Test]
        public void Save_DuplicateWithOverwrite_ReplacesContent()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("dup", SampleMacro("first"), overwrite: false, out _));
            Assert.IsTrue(MacroLibraryStore.TrySave("dup", SampleMacro("second"), overwrite: true, out var error), error);

            Assert.IsTrue(MacroLibraryStore.TryLoad("dup", out var file, out _, out _));
            Assert.AreEqual("second", file.Note);
            Assert.AreEqual(1, MacroLibraryStore.ListNames().Count);
        }

        [Test]
        public void Delete_MissingMacro_Fails()
        {
            Assert.IsFalse(MacroLibraryStore.TryDelete("ghost", out var error));
            StringAssert.Contains("ghost", error);
        }

        // ===== 双 scope（project / global 跨项目库）=====

        [Test]
        public void Scope_Normalize_DefaultsAndRejects()
        {
            Assert.IsTrue(MacroLibraryStore.TryNormalizeScope(null, out var s1, out _));
            Assert.AreEqual(MacroLibraryStore.ScopeProject, s1);
            Assert.IsTrue(MacroLibraryStore.TryNormalizeScope(" GLOBAL ", out var s2, out _));
            Assert.AreEqual(MacroLibraryStore.ScopeGlobal, s2);
            Assert.IsFalse(MacroLibraryStore.TryNormalizeScope("workspace", out _, out var error));
            StringAssert.Contains("project", error);
            StringAssert.Contains("global", error);
        }

        [Test]
        public void Scope_GlobalSave_IsIsolatedFromProject()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("shared", SampleMacro("global copy"), false,
                MacroLibraryStore.ScopeGlobal, out var error), error);

            Assert.IsTrue(MacroLibraryStore.Exists("shared", MacroLibraryStore.ScopeGlobal));
            Assert.IsFalse(MacroLibraryStore.Exists("shared", MacroLibraryStore.ScopeProject));
            Assert.AreEqual(0, MacroLibraryStore.ListNames(MacroLibraryStore.ScopeProject).Count);
            Assert.AreEqual(1, MacroLibraryStore.ListNames(MacroLibraryStore.ScopeGlobal).Count);
        }

        [Test]
        public void Scope_Resolve_ProjectShadowsGlobal()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("shadowed", SampleMacro("proj"), false,
                MacroLibraryStore.ScopeProject, out _));
            Assert.IsTrue(MacroLibraryStore.TrySave("shadowed", SampleMacro("glob"), false,
                MacroLibraryStore.ScopeGlobal, out _));

            Assert.IsTrue(MacroLibraryStore.TryResolveExistingScope("shadowed", out var hit));
            Assert.AreEqual(MacroLibraryStore.ScopeProject, hit);

            Assert.IsTrue(MacroLibraryStore.TryLoad("shadowed", hit, out var file, out _, out _));
            Assert.AreEqual("proj", file.Note);

            // Project 副本删除后，同名解析退到 global。
            Assert.IsTrue(MacroLibraryStore.TryDelete("shadowed", MacroLibraryStore.ScopeProject, out _));
            Assert.IsTrue(MacroLibraryStore.TryResolveExistingScope("shadowed", out var hit2));
            Assert.AreEqual(MacroLibraryStore.ScopeGlobal, hit2);
        }

        [Test]
        public void Scope_Delete_OnlyTouchesPinnedScope()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("both", SampleMacro(), false, MacroLibraryStore.ScopeProject, out _));
            Assert.IsTrue(MacroLibraryStore.TrySave("both", SampleMacro(), false, MacroLibraryStore.ScopeGlobal, out _));

            Assert.IsTrue(MacroLibraryStore.TryDelete("both", MacroLibraryStore.ScopeGlobal, out var error), error);
            Assert.IsFalse(MacroLibraryStore.Exists("both", MacroLibraryStore.ScopeGlobal));
            Assert.IsTrue(MacroLibraryStore.Exists("both", MacroLibraryStore.ScopeProject));
        }

        [Test]
        public void Load_MissingMacro_Fails()
        {
            Assert.IsFalse(MacroLibraryStore.TryLoad("ghost", out _, out _, out var error));
            StringAssert.Contains("ghost", error);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("a/b")]
        [TestCase("a\\b")]
        [TestCase("..")]
        [TestCase("trailingdot.")]
        [TestCase(" leading")]
        [TestCase("CON")]
        public void ValidateName_RejectsInvalidNames(string name)
        {
            Assert.IsFalse(MacroLibraryStore.TryValidateName(name, out var error));
            Assert.IsNotEmpty(error);
            // The invalid name must also be rejected end-to-end by save.
            Assert.IsFalse(MacroLibraryStore.TrySave(name, SampleMacro(), overwrite: false, out _));
        }

        [TestCase("build-hero")]
        [TestCase("Build Hero 01")]
        [TestCase("场景搭建")]
        public void ValidateName_AcceptsReasonableNames(string name)
        {
            Assert.IsTrue(MacroLibraryStore.TryValidateName(name, out var error), error);
        }

        [Test]
        public void ComputeMissingParams_BareSlotWithoutValue_IsMissing()
        {
            var steps = new JArray
            {
                new JObject
                {
                    ["skill"] = "gameobject_create",
                    ["args"] = new JObject
                    {
                        ["name"] = new JObject { ["$param"] = "objName" },
                        ["y"] = new JObject { ["$param"] = "height", ["default"] = 2 }
                    }
                }
            };

            var missing = MacroLibraryStore.ComputeMissingParams(steps, new JObject());
            CollectionAssert.AreEqual(new[] { "objName" }, missing,
                "a bare $param must be missing; a defaulted one must not");
        }

        [Test]
        public void ComputeMissingParams_ProvidedValue_Satisfies()
        {
            var steps = new JArray
            {
                new JObject
                {
                    ["skill"] = "gameobject_create",
                    ["args"] = new JObject { ["name"] = new JObject { ["$param"] = "objName" } }
                }
            };

            var missing = MacroLibraryStore.ComputeMissingParams(steps, new JObject { ["objName"] = "Cube" });
            Assert.AreEqual(0, missing.Count);
        }

        [Test]
        public void ComputeMissingParams_MixedSlotsOfSameName_RequireValue()
        {
            // One defaulted node + one bare node of the same name: the bare one makes it mandatory.
            var steps = new JArray
            {
                new JObject
                {
                    ["skill"] = "gameobject_create",
                    ["args"] = new JObject
                    {
                        ["name"] = new JObject { ["$param"] = "objName", ["default"] = "Cube" },
                        ["parentName"] = new JObject { ["$param"] = "objName" }
                    }
                }
            };

            var missing = MacroLibraryStore.ComputeMissingParams(steps, new JObject());
            CollectionAssert.AreEqual(new[] { "objName" }, missing);
        }

        [Test]
        public void MacroRun_MissingParam_FailsBeforeExecution()
        {
            var steps = new JArray
            {
                new JObject
                {
                    ["skill"] = "gameobject_create",
                    ["args"] = new JObject { ["name"] = new JObject { ["$param"] = "objName" } }
                }
            };
            var macro = MacroFileStore.BuildJson(steps, new List<string>(), replayable: true,
                note: null, recordedAtUtc: null, unityVersion: null, sceneName: null);
            Assert.IsTrue(MacroLibraryStore.TrySave("needs-param", macro, overwrite: false, out var saveError), saveError);

            var result = JObject.FromObject(MacroSkills.MacroRun("needs-param"));
            Assert.IsNotNull(result["error"], "run must fail before executing any step");
            StringAssert.Contains("objName", result["error"].ToString());

            // Provided via params, the same run passes the pre-check (the create step itself
            // is out of scope here — a real run is covered by live REST testing).
        }

        [Test]
        public void MacroRun_UnknownMacro_FailsWithAvailableNames()
        {
            Assert.IsTrue(MacroLibraryStore.TrySave("known", SampleMacro(), overwrite: false, out _));

            var result = JObject.FromObject(MacroSkills.MacroRun("ghost"));
            Assert.IsNotNull(result["error"]);
            StringAssert.Contains("known", result["error"].ToString(),
                "the error must list the available macro names");
        }
    }
}
