using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class PackageManagerHelperTestableTests
    {
        private const string PackageId = "com.besty.unity-skills";
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "UnitySkills_Testables_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [Test]
        public void IsPackageInManifestTestables_WhenMissingOrAbsent_ReturnsFalse()
        {
            var missingPath = Path.Combine(_tempDir, "missing.json");
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(missingPath), Is.False);
            Assert.That(File.Exists(missingPath), Is.False);

            var noTestablesPath = WriteManifest("{\"dependencies\":{}}");
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(noTestablesPath), Is.False);

            var otherTestablesPath = WriteManifest("{\"dependencies\":{},\"testables\":[\"com.other.pkg\"]}");
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(otherTestablesPath), Is.False);

            var invalidJsonPath = WriteManifest("{not valid json");
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(invalidJsonPath), Is.False);
        }

        [Test]
        public void TrySetPackageInManifestTestables_EnableThenDuplicateEnable_UpdatesOnce()
        {
            var path = WriteManifest("{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"1.0.0\"\n  }\n}\n");

            var ok1 = PackageManagerHelper.TrySetPackageInManifestTestables(path, true, out var changed1, out var err1);
            Assert.That(ok1, Is.True);
            Assert.That(err1, Is.Null);
            Assert.That(changed1, Is.True);
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(path), Is.True);

            var json1 = JObject.Parse(File.ReadAllText(path));
            var testables1 = json1["testables"] as JArray;
            Assert.That(testables1, Is.Not.Null);
            Assert.That(testables1.Count, Is.EqualTo(1));
            Assert.That(testables1[0].Value<string>(), Is.EqualTo(PackageId));

            var ok2 = PackageManagerHelper.TrySetPackageInManifestTestables(path, true, out var changed2, out var err2);
            Assert.That(ok2, Is.True);
            Assert.That(err2, Is.Null);
            Assert.That(changed2, Is.False);

            var json2 = JObject.Parse(File.ReadAllText(path));
            var testables2 = json2["testables"] as JArray;
            Assert.That(testables2.Count, Is.EqualTo(1));
        }

        [Test]
        public void TrySetPackageInManifestTestables_DisableRemovesEmptyTestablesProperty()
        {
            var path = WriteManifest("{\n  \"dependencies\": {},\n  \"testables\": [\n    \"com.besty.unity-skills\"\n  ]\n}\n");

            var ok1 = PackageManagerHelper.TrySetPackageInManifestTestables(path, false, out var changed1, out var err1);
            Assert.That(ok1, Is.True);
            Assert.That(err1, Is.Null);
            Assert.That(changed1, Is.True);
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(path), Is.False);

            var json = JObject.Parse(File.ReadAllText(path));
            Assert.That(json.ContainsKey("testables"), Is.False);

            var ok2 = PackageManagerHelper.TrySetPackageInManifestTestables(path, false, out var changed2, out var err2);
            Assert.That(ok2, Is.True);
            Assert.That(err2, Is.Null);
            Assert.That(changed2, Is.False);
        }

        [Test]
        public void TrySetPackageInManifestTestables_DisablePreservesOtherTestablePackages()
        {
            var path = WriteManifest("{\n  \"dependencies\": {},\n  \"testables\": [\n    \"com.other.pkg\",\n    \"com.besty.unity-skills\",\n    \"com.another.pkg\"\n  ]\n}\n");

            var ok = PackageManagerHelper.TrySetPackageInManifestTestables(path, false, out var changed, out var err);
            Assert.That(ok, Is.True);
            Assert.That(err, Is.Null);
            Assert.That(changed, Is.True);
            Assert.That(PackageManagerHelper.IsPackageInManifestTestables(path), Is.False);

            var json = JObject.Parse(File.ReadAllText(path));
            var testables = (json["testables"] as JArray)?.Select(t => t.Value<string>()).ToArray();
            Assert.That(testables, Is.EqualTo(new[] { "com.other.pkg", "com.another.pkg" }));
        }

        private string WriteManifest(string content)
        {
            var path = Path.Combine(_tempDir, "manifest.json");
            File.WriteAllText(path, content);
            return path;
        }
    }
}

// Producer:Betsy
