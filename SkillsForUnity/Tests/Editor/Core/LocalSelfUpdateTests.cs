using NUnit.Framework;
using System;
using System.IO;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class LocalSelfUpdateTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "us-selfupdate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/package.json", "package.json")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/Editor/Skills/Foo.cs", "Editor/Skills/Foo.cs")]
        [TestCase("Unity-Skills-2.9.0/SkillsForUnity/package.json", "package.json")]
        public void TryMapArchiveEntry_MapsSkillsForUnityEntries(string entryName, string expected)
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(entryName, out var relativePath), Is.True);
            Assert.That(relativePath, Is.EqualTo(expected));
        }

        [TestCase("Unity-Skills-2.8.2/README.md")]
        [TestCase("Unity-Skills-2.8.2/docs/SETUP_GUIDE.md")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/")] // 目录条目
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity")]
        [TestCase("Unity-Skills-2.8.2/SkillsForUnity/../evil.txt")] // Zip Slip 防护
        [TestCase("")]
        public void TryMapArchiveEntry_RejectsNonPackageEntries(string entryName)
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(entryName, out _), Is.False);
        }

        [Test]
        public void TryMapArchiveEntry_RejectsNull()
        {
            Assert.That(LocalSelfUpdateService.TryMapArchiveEntry(null, out _), Is.False);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ClassifySpec_TreatsMissingSpecAsLocal(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Local));
        }

        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#v2.8.2")]
        public void ClassifySpec_GitUrlWithoutBetaFragmentIsStable(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Stable));
        }

        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#beta")]
        [TestCase("https://github.com/Besty0728/Unity-Skills.git?path=/SkillsForUnity#BETA")]
        public void ClassifySpec_BetaFragmentIsCaseInsensitive(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Beta));
        }

        [TestCase("file:/Users/x/Unity-Skills/SkillsForUnity")]
        [TestCase("file:../local/copy")]
        public void ClassifySpec_FileSpecIsLocal(string spec)
        {
            Assert.That(PackageManagerHelper.ClassifySpec(spec), Is.EqualTo(PackageManagerHelper.SelfInstallKind.Local));
        }

        [Test]
        public void ValidateStagedPackage_AcceptsMatchingPackage()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.True);
        }

        [Test]
        public void ValidateStagedPackage_RejectsVersionMismatch()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.1");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsNameMismatch()
        {
            WritePackageJson("com.example.other", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingPackageJson()
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMalformedPackageJson()
        {
            File.WriteAllText(Path.Combine(_tempDir, "package.json"), "not json");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingEditorDir()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "unity-skills~"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void ValidateStagedPackage_RejectsMissingSkillsDir()
        {
            WritePackageJson("com.besty.unity-skills", "2.8.2");
            Directory.CreateDirectory(Path.Combine(_tempDir, "Editor"));

            Assert.That(LocalSelfUpdateService.ValidateStagedPackage(_tempDir, "2.8.2"), Is.False);
        }

        [Test]
        public void SwapDirectories_ReplacesContentAndCleansUp()
        {
            var target = Path.Combine(_tempDir, "target");
            var staging = Path.Combine(_tempDir, "staging");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(target, "package.json"), "{ \"version\": \"2.8.1\" }");
            File.WriteAllText(Path.Combine(staging, "package.json"), "{ \"version\": \"2.8.2\" }");
            File.WriteAllText(Path.Combine(staging, "new.txt"), "new");

            LocalSelfUpdateService.SwapDirectories(target, staging);

            Assert.That(File.Exists(Path.Combine(target, "old.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Combine(target, "package.json")), Does.Contain("2.8.2"));
            Assert.That(File.ReadAllText(Path.Combine(target, "new.txt")), Is.EqualTo("new"));
            Assert.That(Directory.Exists(staging), Is.False);
            Assert.That(Directory.GetDirectories(_tempDir, ".unityskills-old-*"), Is.Empty);
        }

        private void WritePackageJson(string name, string version)
        {
            File.WriteAllText(Path.Combine(_tempDir, "package.json"),
                $"{{ \"name\": \"{name}\", \"version\": \"{version}\" }}");
        }

    }
}

// Producer:Betsy
