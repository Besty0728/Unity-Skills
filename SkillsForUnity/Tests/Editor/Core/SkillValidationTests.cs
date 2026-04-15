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
