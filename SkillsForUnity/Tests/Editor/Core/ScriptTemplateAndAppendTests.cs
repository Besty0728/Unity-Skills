using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// script_create's template rule (known names ignore case and spaces, a bare unknown name is rejected before
    /// anything is written, text containing code stays a literal template) and script_append's placement plan, as
    /// pure functions; nothing here imports a script, so no domain reload is triggered.
    /// </summary>
    [TestFixture]
    public class ScriptTemplateAndAppendTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsScriptTemplateTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        // ---------- script_create: template ----------

        [TestCase("monobehaviour", ": MonoBehaviour")]
        [TestCase(" Editor ", ": Editor")]
        [TestCase("EDITORWINDOW", ": EditorWindow")]
        [TestCase("Scriptable Object", ": ScriptableObject")]
        [TestCase(null, ": MonoBehaviour")]
        public void TryResolveTemplate_KnownNames_IgnoreCaseAndSpaces(string template, string baseClass)
        {
            Assert.That(ScriptSkills.TryResolveTemplate(template, null, out var source, out var error), Is.True, $"{error}");
            StringAssert.Contains(baseClass, source);
        }

        [TestCase("Monobehavior")]
        [TestCase("SO")]
        [TestCase("StateMachineBehaviour")]
        [TestCase("plain-class")]
        public void TryResolveTemplate_BareUnknownName_IsRejected(string template)
        {
            Assert.That(ScriptSkills.TryResolveTemplate(template, null, out _, out var error), Is.False);

            var json = ToJson(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("template"));
            Assert.That(json["validValues"]?.Values<string>(),
                Is.EquivalentTo(new[] { "MonoBehaviour", "ScriptableObject", "Editor", "EditorWindow" }));
        }

        [Test]
        public void TryResolveTemplate_Misspelling_SuggestsTheClosestTemplate()
        {
            ScriptSkills.TryResolveTemplate("Monobehavior", null, out _, out var error);

            Assert.That(ToJson(error)["suggestedFixes"]?[0]?["args"]?["template"]?.ToString(), Is.EqualTo("MonoBehaviour"));
        }

        [Test]
        public void TryResolveTemplate_LiteralTemplate_KeepsPlaceholders()
        {
            var path = Path.Combine(_tempRoot, "Foo.cs");

            var written = ScriptSkills.WriteNewScriptFile(path, "Foo", "public class {CLASS} : UnityEngine.MonoBehaviour {}",
                null, null, new List<string>());

            Assert.That(written, Is.EqualTo("public class Foo : UnityEngine.MonoBehaviour {}"));
            Assert.That(File.ReadAllText(path), Is.EqualTo(written));
        }

        [TestCase("// generated {CLASS}")]
        [TestCase("[assembly: System.CLSCompliant(true)]")]
        [TestCase("public class {CLASS}\n{\n}")]
        public void TryResolveTemplate_TextContainingCode_IsLiteral(string template)
        {
            Assert.That(ScriptSkills.TryResolveTemplate(template, null, out var source, out _), Is.True);
            Assert.That(source, Is.EqualTo(template), "A literal template is used as given.");
        }

        [Test]
        public void ScriptCreate_BareUnknownTemplate_TouchesNothing()
        {
            const string folder = "Assets/__RB_NoSuchFolder__";

            var json = ToJson(ScriptSkills.ScriptCreate("RB_Probe", folder: folder, template: "Monobehavior"));

            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), json.ToString(Formatting.None));
            Assert.That(Directory.Exists(folder), Is.False, "A rejected template must not create the folder.");
        }

        [Test]
        public void ScriptCreateBatch_BareUnknownTemplate_KeepsTheStructuredError()
        {
            var json = ToJson(ScriptSkills.ScriptCreateBatch(
                "[{\"scriptName\":\"RB_BatchProbe\",\"folder\":\"Assets/__RB_NoSuchBatchFolder__\",\"template\":\"SO\"}]"));

            var item = json["results"]?[0];
            Assert.That(item?["success"]?.Value<bool>(), Is.False, json.ToString(Formatting.None));
            Assert.That(item?["target"]?.ToString(), Is.EqualTo("RB_BatchProbe"));
            Assert.That(item?["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(item?["validValues"], Is.Not.Null);
            Assert.That(Directory.Exists("Assets/__RB_NoSuchBatchFolder__"), Is.False);
        }

        [Test]
        public void DryRun_RejectsBareUnknownTemplate_WithTheExecutionMessage()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("script_create", "{\"scriptName\":\"RB_DryProbe\",\"template\":\"Monobehavior\"}"));
            var executed = ToJson(ScriptSkills.ScriptCreate("RB_DryProbe", template: "Monobehavior"));

            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            var error = dry["validation"]?["semanticErrors"]?.FirstOrDefault(e => e["field"]?.ToString() == "template");
            Assert.That(error?["error"]?.ToString(), Does.StartWith("Invalid value 'Monobehavior'"));
            Assert.That(error?["error"]?.ToString(), Is.EqualTo(executed["error"]?.ToString()), "dryRun and execution must refuse alike.");
        }

        [Test]
        public void DryRun_WithContent_IgnoresTheTemplate()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("script_create",
                "{\"scriptName\":\"RB_DryProbe\",\"template\":\"Monobehavior\",\"content\":\"public class RB_DryProbe {}\"}"));

            var semantic = dry["validation"]?["semanticErrors"] as JArray;
            Assert.That(semantic?.Any(e => e["field"]?.ToString() == "template") ?? false, Is.False, dry.ToString(Formatting.None));
        }

        [Test]
        public void DryRun_EditorTemplate_PredictsTheEditorFolder()
        {
            var dry = JObject.Parse(SkillRouter.DryRun("script_create", "{\"scriptName\":\"RB_DryEditor\",\"template\":\"Editor Window\"}"));

            Assert.That(dry["changes"]?["create"]?[0]?["path"]?.ToString(), Is.EqualTo("Assets/Editor/RB_DryEditor.cs"),
                "script_create writes an Editor/EditorWindow template to Assets/Editor when folder is the default.");
        }

        // ---------- script_append: placement ----------

        [Test]
        public void PlanAppend_NoNamespace_MemberGoesBeforeClassBrace()
        {
            var lines = new List<string> { "using UnityEngine;", "", "public class B : MonoBehaviour", "{", "    void Start() {}", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "    void M() {}", -1);

            Assert.That(placement.Index, Is.EqualTo(5));
            Assert.That(placement.Scope, Is.EqualTo("type:B"));
            Assert.That(placement.Warning, Is.Null);
        }

        [Test]
        public void PlanAppend_Namespaced_MemberGoesIntoTheLastClass()
        {
            var lines = new List<string> { "namespace A", "{", "    public class B", "    {", "    }", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "    void M() {}", -1);

            Assert.That(placement.Index, Is.EqualTo(4));
            Assert.That(placement.Scope, Is.EqualTo("type:B"));
            Assert.That(placement.Warning, Does.Contain("namespace 'A'"));
        }

        [Test]
        public void PlanAppend_Namespaced_TypeStaysAtNamespaceLevel()
        {
            var lines = new List<string> { "namespace A", "{", "    public class B", "    {", "    }", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "public class C {}", -1);

            Assert.That(placement.Index, Is.EqualTo(5));
            Assert.That(placement.Scope, Is.EqualTo("namespace:A"));
            Assert.That(placement.Warning, Is.Null);
        }

        [Test]
        public void PlanAppend_Namespaced_TwoClasses_MemberGoesIntoTheLastOne()
        {
            var lines = new List<string>
            {
                "namespace Game.Props", "{", "    public class First", "    {", "    }", "",
                "    public class Second : UnityEngine.MonoBehaviour", "    {", "        int x;", "    }", "}"
            };

            var placement = ScriptSkills.PlanAppend(lines, "    public int y;", -1);

            Assert.That(placement.Index, Is.EqualTo(9));
            Assert.That(placement.Scope, Is.EqualTo("type:Second"));
        }

        [Test]
        public void PlanAppend_BracesInStringsAndComments_AreIgnored()
        {
            var lines = new List<string>
            {
                "namespace A", "{", "    public class B", "    {",
                "        void Log() { UnityEngine.Debug.Log(\"}\"); }",
                "        // }",
                "        string s = @\"{\";",
                "        char c = '{';",
                "        string t = $\"{1 + 1}}}\";",
                "    }", "}"
            };

            var placement = ScriptSkills.PlanAppend(lines, "    void M() {}", -1);

            Assert.That(placement.Index, Is.EqualTo(9), $"scope={placement.Scope}, warning={placement.Warning}");
            Assert.That(placement.Scope, Is.EqualTo("type:B"));
        }

        [Test]
        public void PlanAppend_UnreadableStructure_FallsBackToLegacy()
        {
            var lines = new List<string> { "namespace A", "{", "    public class B", "    {", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "    void M() {}", -1);

            Assert.That(placement.Index, Is.EqualTo(4));
            Assert.That(placement.Scope, Is.EqualTo("legacy"));
            Assert.That(placement.Warning, Is.Not.Null);
        }

        [Test]
        public void PlanAppend_ExplicitAtLine_IsVerbatim()
        {
            var lines = new List<string> { "namespace A", "{", "    public class B", "    {", "    }", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "    void M() {}", 2);

            Assert.That(placement.Index, Is.EqualTo(2));
            Assert.That(placement.Scope, Is.EqualTo("atLine"));
        }

        [Test]
        public void PlanAppend_AtLineEqualToLineCount_AppendsAtTheEnd()
        {
            var lines = new List<string> { "public class B", "{", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "// trailer", 3);

            Assert.That(placement.Index, Is.EqualTo(3));
            Assert.That(placement.Scope, Is.EqualTo("endOfFile"));
        }

        [Test]
        public void PlanAppend_AttributeThenType_CountsAsType()
        {
            var lines = new List<string> { "namespace A", "{", "    public class B", "    {", "    }", "}" };

            var placement = ScriptSkills.PlanAppend(lines, "[System.Serializable]\npublic class D {}", -1);

            Assert.That(placement.Scope, Is.EqualTo("namespace:A"));
            Assert.That(placement.Index, Is.EqualTo(5));
        }

        [Test]
        public void PlanAppend_AllmanClassHeaderOverSeveralLines_IsFound()
        {
            var lines = new List<string>
            {
                "using UnityEngine;", "namespace A", "{", "    [DisallowMultipleComponent]", "    public sealed class Spinner : MonoBehaviour,",
                "        ISerializationCallbackReceiver", "    {", "        public void OnBeforeSerialize() { }", "        public void OnAfterDeserialize() { }", "    }", "}"
            };

            var placement = ScriptSkills.PlanAppend(lines, "    public float speed;", -1);

            Assert.That(placement.Index, Is.EqualTo(9));
            Assert.That(placement.Scope, Is.EqualTo("type:Spinner"));
        }

        private static JObject ToJson(object result) => JObject.Parse(JsonConvert.SerializeObject(result));
    }
}

// Producer:Betsy
