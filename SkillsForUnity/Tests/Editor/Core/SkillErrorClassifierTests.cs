using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 钉住 SkillErrorClassifier 的"缺包"判定：缺失的必须是包本身。
    /// 错误消息里会插值调用方传入的标识符，早先按子串匹配 "package"，会让 jobId
    /// （"DefaultPackage_validation_1"）或 Packages/ 资源路径把一次普通的查找失败误判成
    /// MISSING_PACKAGE，把 AI 引向 package_install，而真正该改的是路径或 id。
    /// 分类器是纯静态方法（不碰 EditorPrefs、文件、场景状态），故本夹具无需 SetUp/TearDown。
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
