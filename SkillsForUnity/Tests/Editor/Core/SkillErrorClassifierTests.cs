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
    }
}

// Producer:Betsy
