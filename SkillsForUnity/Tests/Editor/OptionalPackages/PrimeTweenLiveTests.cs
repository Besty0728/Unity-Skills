using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// PrimeTween Free's read-only diagnostics. The module's other two registered skills
    /// (primetween_generate_tween_script / primetween_generate_sequence_script) write and compile a script
    /// on every accepted call (MayTriggerReload = true) and are never invoked here, per the hard rule
    /// against triggering script compilation/domain reload from a test — there is no rejection path that
    /// both exercises them and stays inert, since a valid className is all it takes to reach the write.
    /// </summary>
    [TestFixture]
    public class PrimeTweenSkillsLiveTests : OptionalPackageTestBase
    {
        // Zero parameters, so an empty ProbeArgs can never trip the framework's MissingParam gate — which
        // is checked before the RequiresPackages gate this skill declares (SkillRouter.Execute.cs), so a
        // skill with any CLR-required parameter would report MISSING_PARAM instead of MISSING_PACKAGE here.
        protected override string ProbeSkill => "primetween_get_status";

        [Test]
        public void GetStatus_ReportsInstalledWithVersionAndCoreTypes()
        {
            var result = Ok(Run(ProbeSkill, new JObject()));
            Assert.That(result["isInstalled"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["packageVersion"]?.ToString(), Is.Not.Null.And.Not.Empty, result.ToString());
            var types = result["types"]?.ToObject<string[]>() ?? Array.Empty<string>();
            Assert.That(types, Does.Contain("PrimeTween.Tween"), result.ToString());
        }

        [Test]
        public void GetConfig_AccountsForBothProbedProperties()
        {
            var result = Ok(Run("primetween_get_config", new JObject()));
            Assert.That(result["success"]?.Value<bool>(), Is.True, result.ToString());
            // Each of the two properties this skill probes (defaultEase, defaultUpdateType) must land in
            // exactly one of properties/unavailable — never silently dropped from both.
            int properties = (result["properties"] as JObject)?.Properties().Count() ?? 0;
            int unavailable = (result["unavailable"] as JArray)?.Count ?? 0;
            Assert.That(properties + unavailable, Is.EqualTo(2), result.ToString());
        }

        [Test]
        public void ListFactories_FindsPositionOnTween()
        {
            var result = Ok(Run("primetween_list_factories", new JObject { ["typeName"] = "Tween", ["methodPrefix"] = "Position" }));
            Assert.That(result["type"]?.ToString(), Is.EqualTo("PrimeTween.Tween"), result.ToString());
            var methods = (JArray)result["methods"];
            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(methods.Count), result.ToString());
            Assert.That(methods.Count, Is.GreaterThan(0), result.ToString());
            Assert.That(methods.All(m => m["name"]?.ToString().StartsWith("Position") == true), Is.True, result.ToString());
        }
    }
}

// Producer:Betsy
