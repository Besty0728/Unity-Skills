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

            var unknownParams = (JArray)response["details"]?["unknownParams"];
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

            var unknownParams = (JArray)response["details"]?["unknownParams"];
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
            // "field" is SemanticErrors's established key; "parameter" is UnknownParams's. Both must carry the
            // same value now, so a consumer reading either convention gets the right answer.
            Assert.That(firstError["parameter"]?.ToString(), Is.EqualTo("path"));
            StringAssert.Contains("not an Assets resource path", firstError["error"]?.ToString());
        }

        [Test]
        public void Plan_WithUnresolvableEntityId_ReportsFieldAndParameterKeys()
        {
            // animator_get_info declares no literal 'entityId' parameter, so any entityId it accepts is the
            // router's own synthetic locator (SkillRouter.ShouldExposeSyntheticEntityId /
            // NormalizeSyntheticEntityIdLocator) - the one SemanticErrors producer that used to emit "parameter"
            // instead of "field", so a skill's own analyzer re-reporting the same failed lookup under "field"
            // was never recognized as a duplicate.
            var response = JObject.Parse(SkillRouter.Plan("animator_get_info", @"{""entityId"":""does-not-exist""}"));

            Assert.That(response["valid"]?.Value<bool>(), Is.False);

            var validation = (JObject)response["validation"];
            var semanticErrors = (JArray)validation["semanticErrors"];
            Assert.That(semanticErrors, Is.Not.Null.And.Not.Empty);

            var firstError = (JObject)semanticErrors[0];
            Assert.That(firstError["field"]?.ToString(), Is.EqualTo("entityId"));
            Assert.That(firstError["parameter"]?.ToString(), Is.EqualTo("entityId"));
            StringAssert.Contains("Object not found for entityId", firstError["error"]?.ToString());
        }

        [TestCase(@"{""verbose"":""banana""}", "verbose")]
        [TestCase(@"{""offset"":""abc""}", "offset")]
        [TestCase(@"{""limit"":-1}", "limit")]
        [TestCase(@"{""pageOffset"":-1}", "pageOffset")]
        [TestCase(@"{""pageLimit"":0}", "pageLimit")]
        public void DryRun_WithMalformedReservedParameter_MatchesExecuteRejection(string json, string parameterName)
        {
            // editor_get_tags declares no parameters of its own, so none of these five reserved envelope names
            // can be its literal parameter - the request body is only ever read by the reserved-parameter path.
            var dry = JObject.Parse(SkillRouter.DryRun("editor_get_tags", json));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False,
                $"dryRun must reject a malformed '{parameterName}' the same way Execute would - " +
                "previewing a call that Execute would refuse is exactly what dryRun is for.");

            var typeErrors = (JArray)dry["validation"]?["typeErrors"];
            Assert.That(typeErrors, Is.Not.Null.And.Not.Empty);
            Assert.That(typeErrors.OfType<JObject>().Select(entry => entry["parameter"]?.ToString()),
                Does.Contain(parameterName));

            // Same body, no ?mode=dryRun: Execute must still refuse it (the contract dryRun is now agreeing with).
            var executed = JObject.Parse(SkillRouter.Execute("editor_get_tags", json));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("error"));
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("TYPE_MISMATCH"));
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

// Producer:Betsy
