using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the round5 W0 shared helpers (bugs.md §1.7 H1-H5): stateless pure functions, so this
    /// fixture needs no SetUp/TearDown. H2/H3 read real Editor state (built-in layer/tag) but mutate
    /// nothing.
    /// </summary>
    [TestFixture]
    public class SharedHelperTests
    {
        private static JObject ToJObject(object result) => JObject.Parse(JsonConvert.SerializeObject(result));

        // H1 -- SkillParamUtil.TryParseBoolText

        [TestCase("true", true)]
        [TestCase("TRUE", true)]
        [TestCase(" On ", true)]
        [TestCase("on", true)]
        [TestCase("1", true)]
        [TestCase("yes", true)]
        [TestCase("false", false)]
        [TestCase("FALSE", false)]
        [TestCase("0", false)]
        [TestCase("no", false)]
        [TestCase("off", false)]
        public void TryParseBoolText_AcceptsTheDocumentedVocabulary(string text, bool expected)
        {
            Assert.That(SkillParamUtil.TryParseBoolText(text, out var value), Is.True);
            Assert.That(value, Is.EqualTo(expected));
        }

        [TestCase("treu")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("2")]
        [TestCase("enabled")]
        public void TryParseBoolText_RejectsEverythingElse(string text)
        {
            Assert.That(SkillParamUtil.TryParseBoolText(text, out _), Is.False);
        }

        // H2/H3 -- SkillsCommon.DefinedLayerNames / IsTagDefined

        [Test]
        public void DefinedLayerNames_IncludesTheBuiltInDefaultLayer()
        {
            Assert.That(SkillsCommon.DefinedLayerNames(), Does.Contain("Default"));
        }

        [Test]
        public void IsTagDefined_KnowsBuiltInTagsButNotAMadeUpOne()
        {
            Assert.That(SkillsCommon.IsTagDefined("Untagged"), Is.True);
            Assert.That(SkillsCommon.IsTagDefined("NoSuchTag_7f3"), Is.False);
            Assert.That(SkillsCommon.IsTagDefined(null), Is.False);
        }

        // H4 -- SkillsCommon.ClosestMatch (extracted from BatchExecutor.SuggestField, same threshold)

        [Test]
        public void ClosestMatch_OneEditAway_FindsTheIntendedField()
        {
            var known = new[] { "name", "path", "instanceId", "tag" };
            Assert.That(SkillsCommon.ClosestMatch("nam", known), Is.EqualTo("name"));
            Assert.That(SkillsCommon.ClosestMatch("tagg", known), Is.EqualTo("tag"));
            Assert.That(SkillsCommon.ClosestMatch("instanceld", known), Is.EqualTo("instanceId"));
        }

        [Test]
        public void ClosestMatch_TooFarFromEveryCandidate_ReturnsNull()
        {
            Assert.That(SkillsCommon.ClosestMatch("zzzzzzzzzz", new[] { "name", "path" }), Is.Null);
        }

        // H5 -- MaterialSkills.TryResolveMaterialSavePath (package writability stubbed; see bugs.md B3)

        private static MaterialSkills.PackageWriteCheck Writable() =>
            new MaterialSkills.PackageWriteCheck(found: true, writable: true, packageName: "com.x.y", packageSource: "Local");

        private static MaterialSkills.PackageWriteCheck ReadOnly() =>
            new MaterialSkills.PackageWriteCheck(found: true, writable: false, packageName: "com.unity.z", packageSource: "Registry");

        private static MaterialSkills.PackageWriteCheck NotFound() =>
            new MaterialSkills.PackageWriteCheck(found: false, writable: false, packageName: null, packageSource: null);

        [TestCase("Assets", "Red", "Assets/Red.mat")]
        [TestCase("Assets/Materials", "Red", "Assets/Materials/Red.mat")]
        [TestCase("Assets\\Materials", "Red", "Assets/Materials/Red.mat")]
        [TestCase("./Assets/Materials", "Red", "Assets/Materials/Red.mat")]
        [TestCase("Assets//Materials/", "Red", "Assets/Materials/Red.mat")]
        [TestCase("Assets/Materials/Blue.mat", "Red", "Assets/Materials/Blue.mat")]
        [TestCase("Assets/Materials/Blue.asset", "Red", "Assets/Materials/Blue.asset.mat")]
        public void TryResolveMaterialSavePath_NormalizesLikeSafePath(string input, string name, string expected)
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath(input, name, _ => Writable(), out var resolved, out var error);
            Assert.That(ok, Is.True, error == null ? null : ToJObject(error).ToString());
            Assert.That(resolved, Is.EqualTo(expected));
        }

        [Test]
        public void TryResolveMaterialSavePath_EmbeddedOrLocalPackage_IsAcceptedAsIs()
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath("Packages/com.x.y/Mats", "Red", _ => Writable(), out var resolved, out var error);

            Assert.That(ok, Is.True);
            Assert.That(resolved, Is.EqualTo("Packages/com.x.y/Mats/Red.mat"));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryResolveMaterialSavePath_ReadOnlyPackage_IsRejectedWithoutWriting()
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath("Packages/com.unity.z/Mats", "Red", _ => ReadOnly(), out var resolved, out var error);

            Assert.That(ok, Is.False);
            Assert.That(resolved, Is.Null);
            var json = ToJObject(error);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
            Assert.That(json["parameter"]?.ToString(), Is.EqualTo("savePath"));
            Assert.That(json["error"]?.ToString(), Does.Contain("read-only"));
        }

        [Test]
        public void TryResolveMaterialSavePath_UnknownPackage_IsRejected()
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath("Packages/com.nobody.nothing_7f3/M", "Red", _ => NotFound(), out var resolved, out var error);

            Assert.That(ok, Is.False);
            Assert.That(resolved, Is.Null);
            Assert.That(ToJObject(error)["error"]?.ToString(), Does.Contain("no installed package owns"));
        }

        [Test]
        public void TryResolveMaterialSavePath_PackagesRootAlone_IsRejected()
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath("Packages", "Red", _ => Writable(), out var resolved, out var error);

            Assert.That(ok, Is.False);
            Assert.That(resolved, Is.Null);
            Assert.That(ToJObject(error)["error"]?.ToString(), Does.Contain("the Packages root is not a folder"));
        }

        [Test]
        public void TryResolveMaterialSavePath_Omitted_IsAcceptedWithNoResolvedPath()
        {
            var ok = MaterialSkills.TryResolveMaterialSavePath(null, "Red", _ => Writable(), out var resolved, out var error);

            Assert.That(ok, Is.True);
            Assert.That(resolved, Is.Null);
            Assert.That(error, Is.Null);
        }
    }
}

// Producer:Betsy
