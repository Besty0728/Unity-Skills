using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class SkillValidationTests
    {
        [Test]
        public void Execute_WithUnknownTransformParameters_ReturnsStructuredErrorAndSuggestions()
        {
            var response = JObject.Parse(SkillRouter.Execute("gameobject_set_transform", @"{""x"":0,""y"":1,""z"":2}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"));
            StringAssert.Contains("Unknown parameters", response["error"]?.ToString());

            var unknownParams = (JArray)response["unknownParams"];
            Assert.That(unknownParams, Is.Not.Null);
            Assert.That(unknownParams.Count, Is.EqualTo(3));

            AssertSuggestion(unknownParams, "x", "posX");
            AssertSuggestion(unknownParams, "y", "posY");
            AssertSuggestion(unknownParams, "z", "posZ");
        }

        [Test]
        public void Execute_WithUnknownShaderParameter_SuggestsCanonicalParameter()
        {
            var response = JObject.Parse(SkillRouter.Execute("shader_find", @"{""shaderName"":""Standard""}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"));

            var unknownParams = (JArray)response["unknownParams"];
            Assert.That(unknownParams, Is.Not.Null);
            Assert.That(unknownParams.Count, Is.EqualTo(1));
            AssertSuggestion(unknownParams, "shaderName", "searchName");
        }

        /// <summary>
        /// R1 (issue #1 Define→Develop gate): legacy clients depend on the nested
        /// <c>details.unknownParams</c> path. D2 surfaces <c>unknownParams</c>
        /// additively at top-level; this fixture pins the nested path so D2 stays
        /// backward-compatible. If a future refactor consolidates to a single
        /// source of truth (top-level only), this test must change in the same
        /// PR as the consumer migration.
        /// </summary>
        [Test]
        public void Execute_UnknownParamErrorEnvelope_PreservesNestedDetailsPath_ForLegacyClients()
        {
            var response = JObject.Parse(SkillRouter.Execute("gameobject_set_transform", @"{""x"":0,""y"":1,""z"":2}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("error"));

            // Nested path is the legacy contract — must stay structurally unchanged.
            var details = response["details"] as JObject;
            Assert.That(details, Is.Not.Null, "details object must be present");

            var nestedUnknown = details["unknownParams"] as JArray;
            Assert.That(nestedUnknown, Is.Not.Null, "details.unknownParams (legacy) must remain present");
            Assert.That(nestedUnknown.Count, Is.EqualTo(3));

            var nestedAllowed = details["allowedParams"] as JArray;
            Assert.That(nestedAllowed, Is.Not.Null.And.Not.Empty, "details.allowedParams must remain present");

            // And the top-level path D2 adds must be structurally equivalent to the nested
            // one — same count, same parameter names — so consumers reading either get the
            // same data. (If they ever drift, the "two sources of truth" risk has manifested.)
            var topLevel = response["unknownParams"] as JArray;
            Assert.That(topLevel, Is.Not.Null, "top-level unknownParams (D2) must be present");
            Assert.That(topLevel.Count, Is.EqualTo(nestedUnknown.Count),
                "top-level and nested unknownParams must have the same element count");

            var nestedNames = nestedUnknown.OfType<JObject>().Select(o => o["parameter"]?.ToString()).OrderBy(s => s).ToList();
            var topLevelNames = topLevel.OfType<JObject>().Select(o => o["parameter"]?.ToString()).OrderBy(s => s).ToList();
            CollectionAssert.AreEqual(nestedNames, topLevelNames,
                "top-level and nested unknownParams must list the same parameter names");
        }

        [Test]
        public void Plan_WithTimelineAssetPath_ReturnsSemanticValidationError()
        {
            var response = JObject.Parse(SkillRouter.Plan("timeline_list_tracks", @"{""path"":""Assets/TL.playable""}"));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("plan"));
            Assert.That(response["valid"]?.Value<bool>(), Is.False);

            var validation = (JObject)response["validation"];
            var semanticErrors = (JArray)validation["semanticErrors"];
            Assert.That(semanticErrors, Is.Not.Null);
            Assert.That(semanticErrors.Count, Is.GreaterThan(0));

            var firstError = (JObject)semanticErrors[0];
            Assert.That(firstError["field"]?.ToString(), Is.EqualTo("path"));
            StringAssert.Contains("不是 Assets 资源路径", firstError["error"]?.ToString());
        }

        private static void AssertSuggestion(JArray unknownParams, string parameterName, string expectedSuggestion)
        {
            var entry = unknownParams
                .OfType<JObject>()
                .FirstOrDefault(item => item["parameter"]?.ToString() == parameterName);

            Assert.That(entry, Is.Not.Null, $"未找到未知参数 {parameterName}");

            var suggestions = entry["suggestions"] as JArray;
            Assert.That(suggestions, Is.Not.Null.And.Not.Empty, $"参数 {parameterName} 缺少 suggestions");
            Assert.That(suggestions.Select(token => token.ToString()), Does.Contain(expectedSuggestion));
        }
    }
}
