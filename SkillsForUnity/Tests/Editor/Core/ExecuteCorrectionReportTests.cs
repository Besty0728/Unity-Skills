using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// A failed execute is a complete correction report: whichever bucket picks the errorCode, details carries every
    /// validation bucket (the same block dryRun returns), allowedParams and the effective parameter list -- so fixing
    /// the call never needs a second probe. Also pins the declared-package gate and the resolution notes.
    /// </summary>
    [TestFixture]
    public class ExecuteCorrectionReportTests
    {
        private const string NeverInstalledPackage = "com.unityskills.tests.never-installed";

        // camera_look_at(float x, float y, float z): three required value-type parameters, no planner, no package.
        private const string LookAt = "camera_look_at";

        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.CurrentMode = _savedMode;
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void FailedValidation_CarriesEveryBucket_AndTheSignature()
        {
            Assume.That(SkillRouter.HasSkill(LookAt), Is.True);
            // One body, three kinds of mistake: an unknown key, a wrong type, two omitted required parameters.
            var response = Execute(LookAt, "{\"x\":\"not-a-number\",\"bogus\":1}");

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"),
                "The first failing bucket still picks the code, in the same order as before.");
            var details = (JObject)response["details"];
            Assert.That(details["unknownParams"], Is.InstanceOf<JArray>(), "Existing keys stay for older clients.");
            Assert.That(StringArray(details["allowedParams"]), Is.EquivalentTo(new[] { "x", "y", "z" }));

            var validation = (JObject)details["validation"];
            Assert.That(validation, Is.Not.Null, response.ToString(Formatting.None));
            Assert.That(((JArray)validation["unknownParams"]).Count, Is.EqualTo(1));
            Assert.That(((JArray)validation["typeErrors"]).Count, Is.EqualTo(1));
            Assert.That(StringArray(validation["missingParams"]), Is.EqualTo(new[] { "y", "z" }));
            foreach (var bucket in new[] { "semanticErrors", "missingPackages", "warnings" })
                Assert.That(validation.Property(bucket), Is.Not.Null, $"An empty bucket is null, never omitted: {bucket}");

            var parameters = ((JArray)details["parameters"]).Cast<JObject>().ToArray();
            Assert.That(parameters.Select(p => (string)p["name"]), Is.EqualTo(new[] { "x", "y", "z" }));
            Assert.That(parameters.All(p => (string)p["type"] == "number" && p["required"]?.Value<bool>() == true), Is.True,
                string.Join(", ", parameters.Select(p => p.ToString(Formatting.None))));
        }

        [Test]
        public void FailedValidation_ReportsTheSameBlockAsDryRun()
        {
            Assume.That(SkillRouter.HasSkill(LookAt), Is.True);
            const string body = "{\"x\":\"not-a-number\",\"bogus\":1}";

            var executed = Execute(LookAt, body)["details"]?["validation"];
            var previewed = JObject.Parse(SkillRouter.DryRun(LookAt, body))["validation"];

            Assert.That(JToken.DeepEquals(executed, previewed), Is.True,
                $"execute={executed?.ToString(Formatting.None)}\ndryRun={previewed?.ToString(Formatting.None)}");
        }

        [TestCase("{\"x\":\"abc\",\"y\":0,\"z\":0}", "TYPE_MISMATCH", "typeErrors")]
        [TestCase("{\"y\":0}", "MISSING_PARAM", "missingParams")]
        public void EveryValidationBranch_CarriesAllowedParamsAndParameters(string body, string expectedCode, string legacyKey)
        {
            Assume.That(SkillRouter.HasSkill(LookAt), Is.True);
            var response = Execute(LookAt, body);

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo(expectedCode), response.ToString(Formatting.None));
            var details = (JObject)response["details"];
            Assert.That(details[legacyKey], Is.InstanceOf<JArray>());
            Assert.That(StringArray(details["allowedParams"]), Is.EquivalentTo(new[] { "x", "y", "z" }));
            Assert.That(details["validation"]?[legacyKey], Is.InstanceOf<JArray>());
            Assert.That(details["parameters"], Is.InstanceOf<JArray>());
        }

        [Test]
        public void SemanticFailure_CarriesAllowedParamsAndParameters()
        {
            Assume.That(SkillRouter.HasSkill("camera_set_properties"), Is.True);
            var response = Execute("camera_set_properties", "{\"name\":\"__report_probe__\",\"clearFlags\":\"NoSuchFlag\"}");

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"), response.ToString(Formatting.None));
            var details = (JObject)response["details"];
            Assert.That(details["semanticErrors"], Is.InstanceOf<JArray>());
            Assert.That(StringArray(details["allowedParams"]), Does.Contain("clearFlags"));
            Assert.That(details["validation"]?["semanticErrors"], Is.InstanceOf<JArray>());
            Assert.That(((JArray)details["parameters"]).Select(p => (string)p["name"]), Does.Contain("clearFlags"));
        }

        // ---------- declared packages ----------

        [Test]
        public void MissingPackage_MakesAWriteSkillInvalid()
        {
            Assume.That(SkillRouter.TryGetSkill(LookAt, out var real), Is.True);
            var probe = CloneWith(real, clone =>
            {
                clone.RequiresPackages = new[] { NeverInstalledPackage };
                clone.ReadOnly = false;
            });

            var validation = SkillRouter.ValidateParameters(probe, "{\"x\":0,\"y\":0,\"z\":0}");
            if (PackageManagerHelper.InstalledPackages == null)
            {
                // The async list is not loaded yet: unknown must never read as "not installed".
                Assert.That(validation.MissingPackages, Is.Empty);
                Assert.That(validation.Warnings, Has.Some.Contains(NeverInstalledPackage));
                return;
            }

            Assert.That(validation.MissingPackages, Is.EqualTo(new[] { NeverInstalledPackage }));
            Assert.That(validation.Valid, Is.False, "dryRun must stop calling a call valid that can only fail.");
        }

        [Test]
        public void MissingPackage_IsOnlyAWarning_ForAReadOnlySkill()
        {
            Assume.That(SkillRouter.TryGetSkill(LookAt, out var real), Is.True);
            var probe = CloneWith(real, clone =>
            {
                clone.RequiresPackages = new[] { NeverInstalledPackage };
                clone.ReadOnly = true;
            });

            var validation = SkillRouter.ValidateParameters(probe, "{\"x\":0,\"y\":0,\"z\":0}");

            Assert.That(validation.MissingPackages, Is.Empty, "A status probe must keep answering 'not installed' itself.");
            Assert.That(validation.Valid, Is.True);
            Assert.That(validation.Warnings, Has.Some.Contains(NeverInstalledPackage));
        }

        [Test]
        public void MissingPackage_Execute_AnswersBeforeThePermissionGate()
        {
            var candidate = FindMissingPackageCandidate();
            Assume.That(candidate, Is.Not.Null,
                "No write skill here declares a package that is missing (or the package list is not loaded); nothing to probe.");

            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            int pendingBefore = SkillsModeManager.PendingGrantRequests.Count;

            var response = Execute(candidate.Name, "{}");

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("MISSING_PACKAGE"), response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.RetryInstallAndRetry));
            Assert.That(SkillsModeManager.PendingGrantRequests.Count, Is.EqualTo(pendingBefore),
                "The user must never be asked to approve a call that can only fail.");

            var missing = StringArray(response["details"]?["missingPackages"]);
            Assert.That(missing, Is.Not.Empty);
            Assert.That(StringArray(response["details"]?["validation"]?["missingPackages"]), Is.EqualTo(missing));
            var install = ((JArray)response["suggestedFixes"]).FirstOrDefault(fix => (string)fix["skill"] == "package_install");
            Assert.That(install?["args"]?["packageId"]?.ToString(), Is.EqualTo(missing[0]));
        }

        [Test]
        public void MissingPackage_DryRunAndPlan_ReportIt()
        {
            var candidate = FindMissingPackageCandidate();
            Assume.That(candidate, Is.Not.Null,
                "No write skill here declares a package that is missing (or the package list is not loaded); nothing to probe.");

            var dry = JObject.Parse(SkillRouter.DryRun(candidate.Name, "{}"));
            Assert.That(dry["valid"]?.Value<bool>(), Is.False, dry.ToString(Formatting.None));
            Assert.That(StringArray(dry["validation"]?["missingPackages"]), Is.Not.Empty);

            var dryV2 = JObject.Parse(SkillRouter.DryRun(candidate.Name, "{}", 2));
            Assert.That(StringArray(dryV2["validation"]?["missingPackages"]), Is.Not.Empty);

            var plan = JObject.Parse(SkillRouter.Plan(candidate.Name, "{}"));
            Assert.That(plan["valid"]?.Value<bool>(), Is.False);
            Assert.That(StringArray(plan["validation"]?["missingPackages"]), Is.Not.Empty);
        }

        // ---------- resolution notes ----------

        [Test]
        public void InexactNameResolution_IsNotedInExecuteAndDryRun()
        {
            var name = "NotesProbe_" + Guid.NewGuid().ToString("N");
            var go = new GameObject(name);
            try
            {
                GameObjectFinder.InvalidateCache();
                var loose = name.ToLowerInvariant();

                var executed = Execute("gameobject_get_info", "{\"name\":\"" + loose + "\"}");
                Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
                Assert.That(StringArray(executed[SkillRouter.ResolutionNotesKey]), Has.Some.Contains(name),
                    "A case-insensitive match must say which object it landed on.");

                GameObjectFinder.InvalidateCache();
                var dry = JObject.Parse(SkillRouter.DryRun("gameobject_rename",
                    "{\"name\":\"" + loose + "\",\"newName\":\"Renamed\"}"));
                Assert.That(StringArray(dry[SkillRouter.ResolutionNotesKey]), Has.Some.Contains(name), dry.ToString(Formatting.None));

                GameObjectFinder.InvalidateCache();
                var exact = Execute("gameobject_get_info", "{\"name\":\"" + name + "\"}");
                Assert.That(exact.Property(SkillRouter.ResolutionNotesKey), Is.Null,
                    "An exact match needs no note, and the success envelope keeps its old shape.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                GameObjectFinder.InvalidateCache();
            }
        }

        // ---------- helpers ----------

        private static JObject Execute(string skill, string body) => JObject.Parse(SkillRouter.Execute(skill, body));

        private static string[] StringArray(JToken token) =>
            (token as JArray)?.Select(t => t.ToString()).ToArray() ?? Array.Empty<string>();

        private static SkillRouter.SkillInfo CloneWith(SkillRouter.SkillInfo source, Action<SkillRouter.SkillInfo> change)
        {
            var clone = new SkillRouter.SkillInfo();
            foreach (var field in typeof(SkillRouter.SkillInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(clone, field.GetValue(source));
            change(clone);
            return clone;
        }

        /// <summary>A write skill whose declared package is absent and whose empty body is otherwise valid, if this project has one.</summary>
        private static SkillRouter.SkillInfo FindMissingPackageCandidate()
        {
            if (PackageManagerHelper.InstalledPackages == null)
                return null;

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered().OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (skill.ReadOnly || skill.RequiresPackages == null || skill.RequiresPackages.Length == 0 ||
                    SkillsSurfaceProfile.IsExcluded(skill))
                    continue;

                var validation = SkillRouter.ValidateParameters(skill, "{}");
                if (validation.MissingPackages.Count > 0 && validation.MissingParams.Count == 0 &&
                    validation.UnknownParams.Count == 0 && validation.TypeErrors.Count == 0 &&
                    validation.SemanticErrors.Count == 0)
                    return skill;
            }
            return null;
        }
    }
}

// Producer:Betsy
