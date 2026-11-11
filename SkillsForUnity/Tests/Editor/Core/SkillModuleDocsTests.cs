using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The `doc` pointer on v2 schema entries: only registered categories carry it, and v1 entries never do.
    /// </summary>
    [TestFixture]
    public class SkillModuleDocsTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void RegisteredDocs_ExistInTheShippedTemplate()
        {
            var templateRoot = GetTemplateRoot();
            var missing = SkillModuleDocs.Registered
                .Select(entry => entry.Value)
                .Where(path => !File.Exists(Path.Combine(templateRoot, path.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();

            Assert.That(missing, Is.Empty,
                $"SkillModuleDocs 登记的文档必须存在于模板目录 {templateRoot} 下: {string.Join(", ", missing)}");
        }

        [Test]
        public void V2Entry_CarriesDoc_OnlyForRegisteredCategories()
        {
            var v2 = ByName("?full=1&wire=v2");

            Assert.That(v2["ui_create_canvas"]["doc"]?.ToString(), Is.EqualTo("skills/ui/MODULE.md"));
            Assert.That(v2["prefab_instantiate"]["doc"]?.ToString(), Is.EqualTo("skills/prefab/MODULE.md"));
            Assert.That(v2["gameobject_create"].ContainsKey("doc"), Is.False,
                "未登记的 category 不该输出 doc 键。");
        }

        [Test]
        public void V1Entry_NeverCarriesDoc()
        {
            var withDoc = ByName("?full=1").Where(entry => entry.Value.ContainsKey("doc")).Select(entry => entry.Key).ToArray();

            Assert.That(withDoc, Is.Empty, "v1 条目不应出现 doc 字段。");
        }

        private static string GetTemplateRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var inProject = projectRoot == null ? null : Path.Combine(projectRoot, "SkillsForUnity", "unity-skills~");
            if (inProject != null && Directory.Exists(inProject))
                return inProject;

            var package = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly);
            if (package != null)
            {
                var inPackage = Path.Combine(package.resolvedPath, "unity-skills~");
                if (Directory.Exists(inPackage))
                    return inPackage;
            }

            return inProject ?? "(project root not resolvable)";
        }

        private static JObject Manifest(string query) => JObject.Parse(SkillRouter.GetFilteredManifest(query));

        private static Dictionary<string, JObject> ByName(string query) =>
            ((JArray)Manifest(query)["skills"]).Cast<JObject>().ToDictionary(s => s["name"].ToString(), StringComparer.Ordinal);
    }
}

// Producer:Betsy
