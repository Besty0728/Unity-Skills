using System.Collections.Generic;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Pins down SkillErrorClassifier's "missing package" verdict: what's missing must be the package itself.
    /// Error messages interpolate identifiers passed in by the caller; the old substring match on "package" let a jobId
    /// ("DefaultPackage_validation_1") or a Packages/ asset path cause an ordinary lookup failure to be misjudged as
    /// MISSING_PACKAGE, steering the AI toward package_install when the real fix is the path or id.
    /// The classifier is a pure static method (touches no EditorPrefs, files, or scene state), so this fixture needs no SetUp/TearDown.
    /// </summary>
    [TestFixture]
    public class SkillErrorClassifierTests
    {
        [TestCase("Package not found: com.unity.foo")]
        [TestCase("Package 'com.unity.foo' not found")]
        [TestCase("Package com.unity.foo does not exist")]
        public void Classify_PackageItselfMissing_IsMissingPackage(string message)
        {
            Assert.AreEqual(SkillErrorCode.MissingPackage, SkillErrorClassifier.Classify(message).Code);
        }

        [Test]
        public void Classify_NotInstalledMarker_IsMissingPackage()
        {
            var message = "Addressables package (com.unity.addressables) is not installed — " +
                          "the 'Unity.Addressables.Editor' assembly could not be resolved.";
            Assert.AreEqual(SkillErrorCode.MissingPackage, SkillErrorClassifier.Classify(message).Code);
        }

        [Test]
        public void MissingPackage_FirstFixAsksTheUser_ThenInstalls()
        {
            var classified = SkillErrorClassifier.Classify("Addressables package (com.unity.addressables) is not installed.");
            var declared = SkillErrorClassifier.ForCode(SkillErrorCode.MissingPackage, "anything");

            foreach (var fixes in new[] { classified.SuggestedFixes, declared.SuggestedFixes })
            {
                Assert.AreEqual("ask_user", fixes[0].action);
                StringAssert.Contains("install it only if they ask", fixes[0].reason);
                Assert.IsTrue(fixes.Exists(fix => fix.skill == "package_install"), "The install fix stays, after the question.");
            }
            Assert.AreEqual(SkillErrorResponse.RetryInstallAndRetry, classified.RetryStrategy);
        }

        [Test]
        public void BehaviorNotInstalled_DeclaresItsOwnFixes_AskingTheUserFirst()
        {
            var fixes = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.FromObject(BehaviorReflectionHelper.NotInstalled())["suggestedFixes"];

            Assert.AreEqual("ask_user", fixes[0]["action"]?.ToString());
            StringAssert.Contains("package_install", fixes[1].ToString(), "The install hints stay, after the question.");
        }

        [Test]
        public void UnsupportedPackageVersion_IsASkillErrorThatAsksTheUser_NotMissingPackage()
        {
            var response = Newtonsoft.Json.Linq.JObject.FromObject(SkillErrorResponse.UnsupportedPackageVersion(
                "com.tuyoogame.yooasset", "4.0.0", "[2.3.15,2.4.0-0) and [3.0.4,3.1.0-0)", "Pin a supported version."));

            Assert.AreEqual("SKILL_ERROR", response["errorCode"]?.ToString());
            Assert.AreEqual("ask_user", response["suggestedFixes"]?[0]?["action"]?.ToString());
            Assert.AreEqual(SkillErrorResponse.UnsupportedPackageVersionReason, response["reason"]?.ToString());
            Assert.AreEqual("4.0.0", response["installedVersion"]?.ToString());
            Assert.AreNotEqual(SkillErrorCode.MissingPackage, SkillErrorClassifier.Classify(response["error"].ToString()).Code,
                "Classified without the declared errorCode, the wording must still not read as a missing package.");
        }

        [TestCase("2.3.19", "2.3.15", "3.0", true)]
        [TestCase("3.0.7", "2.3.15", "3.0", false)]
        [TestCase("3.0.4", "3.0.4", "4.0", true)]
        [TestCase("3.0.3-beta", "3.0.4", "4.0", false)]
        [TestCase("3.0.4-pre.1", "3.0.4", "4.0", false)]
        [TestCase("4.0.0-pre.1", "3.0.4", "4.0", true)]
        [TestCase("2.13.3", "2.0", "3.0", true)]
        [TestCase("v9.0.3", "8.12.0", "10.0", false)]
        [TestCase("", "2.0", "3.0", false)]
        public void IsVersionInRange_OrdersPrereleasesLikeVersionDefines(string version, string min, string max, bool expected)
        {
            Assert.AreEqual(expected, PackageManagerHelper.IsVersionInRange(version, min, max));
        }

        [TestCase("2.3.19", "2.x")]
        [TestCase("2.3.14", null)]
        [TestCase("3.0.0-beta", null)]
        [TestCase("3.0.3-beta", null)]
        [TestCase("3.0.4", "3.x")]
        [TestCase("3.0.7", "3.x")]
        [TestCase("3.0.9", "3.x")]
        [TestCase("3.1.0-beta", null)]
        [TestCase("2.4.0-beta", null)]
        [TestCase("3.1.0", null)]
        [TestCase("4.0.0-beta", null)]
        [TestCase("4.0.0", null)]
        public void YooAssetApiLine_MatchesTheVersionDefines(string version, string expected)
        {
            Assert.AreEqual(expected, YooAssetSkills.ResolveApiLine(version));
        }

        [TestCase("Runtime validation job 'DefaultPackage_validation_1' not found")]
        [TestCase("Job 'ContainsPackageWord' not found")]
        [TestCase("Material asset not found: Packages/com.example.fake/Materials/Nope.mat")]
        [TestCase("Asset at Packages/com.x/file.txt does not exist")]
        [TestCase("Script not found: Assets/MyPackageThing/Foo.cs")]
        public void Classify_CallerInputMentionsPackage_StaysTargetNotFound(string message)
        {
            Assert.AreEqual(SkillErrorCode.TargetNotFound, SkillErrorClassifier.Classify(message).Code);
        }

        [Test]
        public void Classify_LookupInsideExistingPackage_StaysTargetNotFound()
        {
            Assert.AreEqual(
                SkillErrorCode.TargetNotFound,
                SkillErrorClassifier.Classify("Group 'g' not found in package 'p'").Code);
        }

        [Test]
        public void InstanceLabel_FolderMatchingProductName_IsJustNameAndPort()
        {
            Assert.AreEqual("UnitySkillsEval2022@8091",
                SkillErrorResponse.InstanceLabel("UnitySkillsEval2022", "/Users/dev/UnitySkillsEval2022", 8091));
            Assert.AreEqual("UnitySkillsEval2022@8091",
                SkillErrorResponse.InstanceLabel("UnitySkillsEval2022", "/Users/dev/unityskillseval2022/", 8091));
        }

        [Test]
        public void InstanceLabel_FolderDiffersFromProductName_NamesTheFolderInParentheses()
        {
            Assert.AreEqual("UnitySkillsEval2022 (UnitySkillsEval2022b)@8091",
                SkillErrorResponse.InstanceLabel("UnitySkillsEval2022", "/Users/dev/UnitySkillsEval2022b", 8091));
        }

        [TestCase(null)]
        [TestCase("")]
        public void InstanceLabel_NoProjectPath_IsJustNameAndPort(string projectPath)
        {
            Assert.AreEqual("My Game@8092", SkillErrorResponse.InstanceLabel("My Game", projectPath, 8092));
        }

        [Test]
        public void WithTargetNotFoundInstance_AddsLabelOnlyForTargetNotFound_WithoutMutatingTheSource()
        {
            var skillExtra = new Dictionary<string, object> { ["candidates"] = 2 };

            var added = SkillErrorResponse.WithTargetNotFoundInstance(SkillErrorCode.TargetNotFound, skillExtra, "Game@8091");
            Assert.AreEqual("Game@8091", added["instance"]);
            Assert.AreEqual(2, added["candidates"]);
            Assert.IsFalse(skillExtra.ContainsKey("instance"), "The skill's own dictionary must not be mutated.");

            var untouched = SkillErrorResponse.WithTargetNotFoundInstance(SkillErrorCode.MissingParam, skillExtra, "Game@8091");
            Assert.AreSame(skillExtra, untouched);
        }

        [Test]
        public void WithTargetNotFoundInstance_SkillDeclaredInstance_Wins()
        {
            var skillExtra = new Dictionary<string, object> { ["instance"] = "Declared@1" };

            var result = SkillErrorResponse.WithTargetNotFoundInstance(SkillErrorCode.TargetNotFound, skillExtra, "Game@8091");

            Assert.AreEqual("Declared@1", result["instance"]);
        }
    }
}

// Producer:Betsy
