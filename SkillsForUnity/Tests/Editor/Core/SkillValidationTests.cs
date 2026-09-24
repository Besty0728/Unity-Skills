using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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

        [Test]
        public void MaterialCreate_DeclaresNameAsRequired()
        {
            // Canary: isolates metadata attachment from the validation-pipeline computation below. If this ever
            // fails, the attribute itself isn't reaching the registry (reflection/registration bug); if only the
            // tests below fail while this one passes, the bug is downstream in IsParameterRequired/ValidateParameters.
            Assert.That(SkillRouter.TryGetSkill("material_create", out var skill), Is.True, "material_create is not registered.");
            Assert.That(skill.RequiredParams, Is.Not.Null.And.Contains("name"));
        }

        [Test]
        public void MaterialCreate_MissingName_ReportsMissingParam()
        {
            // material_create.name has no CLR default and was never declared RequiredParams, so an omitted
            // name used to sail through dryRun as valid and fail deep inside (a null Material.name).
            var dry = JObject.Parse(SkillRouter.DryRun("material_create", "{}"));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            var missingParams = (JArray)dry["validation"]?["missingParams"];
            Assert.That(missingParams?.Select(token => token.ToString()), Does.Contain("name"),
                dry.ToString(Formatting.None));

            var executed = JObject.Parse(SkillRouter.Execute("material_create", "{}"));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("error"), executed.ToString(Formatting.None));
            Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
        }

        [Test]
        public void MaterialCreate_WithNameProvided_StillSucceeds()
        {
            // Adding the guard must not touch the behaviour of a call that already provides name: in-memory
            // (no savePath) so the test writes nothing to disk.
            var response = JObject.Parse(SkillRouter.Execute("material_create",
                @"{""name"":""__m7_missing_param_probe__""}"));
            var result = response["result"];
            Assert.That(result?["success"]?.Value<bool>(), Is.True, response.ToString(Formatting.None));
            Assert.That(result?["name"]?.ToString(), Is.EqualTo("__m7_missing_param_probe__"));

            var created = EditorUtility.InstanceIDToObject(result["instanceId"].Value<int>());
            if (created != null)
                UnityEngine.Object.DestroyImmediate(created);
        }

        [Test]
        public void SmartReferenceBind_MissingTargetName_ReportsMissingParam()
        {
            // targetName has no CLR default, no alias and no _requiredInputGroups coverage (unlike componentName,
            // which the "component" group already protects) - it used to reach GameObjectFinder.Find(name: null)
            // and fail with a generic "not found" instead of a clean MISSING_PARAM.
            var dry = JObject.Parse(SkillRouter.DryRun("smart_reference_bind",
                @"{""componentName"":""Transform"",""fieldName"":""spawns""}"));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False);
            var missingParams = (JArray)dry["validation"]?["missingParams"];
            Assert.That(missingParams?.Select(token => token.ToString()), Does.Contain("targetName"));
        }

        [Test]
        public void ScriptReplace_OmittedReplace_DeletesMatchesInsteadOfThrowing()
        {
            // Regex.Replace(input, pattern, replacement, ...) throws ArgumentNullException on a null replacement
            // (confirmed against the .NET docs), unlike string.Replace(old, null) which already treats it as a
            // delete - so isRegex=true with replace omitted used to crash instead of deleting every match.
            // Calls ScriptSkills.ScriptReplace directly (same pattern as ScriptCreateContentTests). Unlike
            // WriteNewScriptFile, ScriptReplace itself runs Validate.SafePath on scriptPath, which rejects
            // anything outside Assets/ or Packages/ - so, unlike that sibling test, this one cannot use a plain
            // OS temp path and must write under Assets/. ".txt" (not ".cs") keeps AssetDatabase.ImportAsset from
            // ever treating it as a script to compile, so there is still no domain-reload risk.
            var relativePath = $"Assets/UnitySkillsTests_Temp/ScriptReplaceProbe_{Guid.NewGuid():N}.txt";
            var absolutePath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllText(absolutePath, "aXbXc");
            string jobId = null;
            try
            {
                object result = null;
                Assert.DoesNotThrow(() =>
                {
                    result = ScriptSkills.ScriptReplace(relativePath, "X", null, isRegex: true, checkCompile: false);
                });

                Assert.That(File.ReadAllText(absolutePath), Is.EqualTo("abc"),
                    "Omitted replace must delete every match, matching string.Replace's own null-replacement behaviour.");

                var resultObject = JObject.FromObject(result);
                Assert.That(resultObject["replacements"]?.Value<int>(), Is.EqualTo(2), resultObject.ToString(Formatting.None));
                jobId = resultObject["jobId"]?.ToString();
            }
            finally
            {
                if (!string.IsNullOrEmpty(jobId))
                    BatchPersistence.RemoveJob(jobId);
                AssetDatabase.DeleteAsset(relativePath);
                try { File.Delete(absolutePath); } catch { }
            }
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
