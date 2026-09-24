using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// script_create's verbatim content: written byte-for-byte (UTF-8, no BOM) instead of a template, with warnings
    /// when template/namespaceName are ignored or no type carries the file's name. The writer is exercised on a temp
    /// path outside Assets/, so no test ever imports a script and triggers a domain reload.
    /// </summary>
    [TestFixture]
    public class ScriptCreateContentTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsScriptContentTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        [Test]
        public void Content_IsWrittenVerbatim_WithoutBom()
        {
            const string content = "using UnityEngine;\r\n\r\npublic class Mover : MonoBehaviour\n{\n    public float speed = 2.5f; // caf\u00e9\n}\n";
            var path = Path.Combine(_tempRoot, "Mover.cs");
            var warnings = new List<string>();

            string written = ScriptSkills.WriteNewScriptFile(path, "Mover", null, null, content, warnings);

            Assert.That(written, Is.EqualTo(content));
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(new UTF8Encoding(false).GetBytes(content)),
                "Line endings, non-ASCII text and the missing BOM must all survive untouched.");
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void WithoutContent_TheTemplateIsStillFilled()
        {
            var path = Path.Combine(_tempRoot, "Spinner.cs");

            string written = ScriptSkills.WriteNewScriptFile(path, "Spinner", null, "Game.Props", null, new List<string>());

            StringAssert.Contains("public class Spinner : MonoBehaviour", written);
            StringAssert.Contains("namespace Game.Props", written);
            Assert.That(File.ReadAllText(path), Is.EqualTo(written));
        }

        [Test]
        public void Content_IgnoresTemplateAndNamespace_WithAWarning()
        {
            var warnings = ScriptSkills.CheckVerbatimContent("public class Foo : UnityEngine.MonoBehaviour {}", "Foo", "Editor", "Game");

            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("template and namespaceName were ignored", warnings[0]);
        }

        [Test]
        public void Content_WithoutATypeNamedLikeTheFile_Warns()
        {
            var warnings = ScriptSkills.CheckVerbatimContent("public class FooBar : UnityEngine.MonoBehaviour {}", "Foo", null, null);

            Assert.That(warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("no type named 'Foo'", warnings[0]);
        }

        [TestCase("public class Foo : MonoBehaviour {}", true)]
        [TestCase("namespace A.B { internal sealed class Foo<T> where T : class {} }", true)]
        [TestCase("public struct Foo {}", true)]
        [TestCase("public class FooBar {}", false)]
        [TestCase("public class Bar { Foo field; }", false)]
        public void DeclaresTypeNamed_MatchesWholeTypeNamesOnly(string source, bool expected)
        {
            Assert.That(ScriptSkills.DeclaresTypeNamed(source, "Foo"), Is.EqualTo(expected));
        }

        [Test]
        public void Content_IsADeclaredParameter_AndOptional()
        {
            var dryRun = JObject.Parse(SkillRouter.DryRun("script_create",
                "{\"scriptName\":\"Foo\",\"content\":\"public class Foo : UnityEngine.MonoBehaviour {}\"}"));

            Assert.That(dryRun["valid"]?.Value<bool>(), Is.True, dryRun.ToString());
            var unknown = dryRun["validation"]?["unknownParams"] as JArray;
            Assert.That(unknown == null || unknown.Count == 0, Is.True, "content must not be reported as an unknown parameter.");

            Assert.That(SkillRouter.TryGetSkill("script_create", out var skill), Is.True);
            Assert.That(skill.Parameters.Any(p => p.Name == "content" && p.ParameterType == typeof(string)), Is.True);
            Assert.That(skill.Outputs, Has.Member("waitUrl"));
        }

        [TestCase("script_rename", "newName")]
        [TestCase("script_replace", "find")]
        public void BodyRequiredParameters_AreRequiredInTheSchema(string skillName, string parameter)
        {
            var dryRun = JObject.Parse(SkillRouter.DryRun(skillName, "{\"scriptPath\":\"Assets/Nothing.cs\"}"));
            var missing = dryRun["validation"]?["missingParams"] as JArray;

            Assert.That(missing?.Select(m => m.ToString()), Has.Member(parameter),
                $"{skillName} returns MISSING_PARAM for an empty {parameter}; dryRun must report it instead of valid:true. {dryRun}");
        }
    }
}

// Producer:Betsy
