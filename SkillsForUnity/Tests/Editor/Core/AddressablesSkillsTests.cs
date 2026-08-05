using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class AddressablesSkillsTests
    {
        private string _groupName;
        private string _assetPath;

        [SetUp]
        public void SetUp()
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            _groupName = "Endpoint Test " + Guid.NewGuid().ToString("N").Substring(0, 8);
            _assetPath = "Assets/" + _groupName + ".txt";
        }

        [TearDown]
        public void TearDown()
        {
            Execute("addressables_group_delete", new JObject { ["groupName"] = _groupName });
            AssetDatabase.DeleteAsset(_assetPath);
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
        }

        [Test]
        public void Endpoints_CreateAddressableAndBuildCatalogAndBundle()
        {
            var installed = Execute("addressables_check_installed");
            if (installed["result"]?["installed"]?.Value<bool>() != true ||
                installed["result"]?["configured"]?.Value<bool>() != true)
                Assert.Ignore("Addressables 3.1.0 and configured settings are required.");

            Assert.That(installed["result"]?["version"]?.ToString(), Is.EqualTo("3.1.0"));

            var groups = Success(Execute("addressables_group_list"));
            var defaultGroup = groups["groups"]?.Children<JObject>().Single(group => group["isDefault"]?.Value<bool>() == true);
            Assert.That(defaultGroup, Is.Not.Null);
            var protectedDelete = Execute("addressables_group_delete", new JObject { ["groupName"] = defaultGroup["name"] });
            Assert.That(protectedDelete["error"]?.ToString(), Does.Contain("Cannot delete default group"));

            Success(Execute("addressables_group_create", new JObject { ["groupName"] = _groupName }));
            File.WriteAllText(_assetPath, "addressables endpoint integration test");
            AssetDatabase.ImportAsset(_assetPath, ImportAssetOptions.ForceSynchronousImport);

            const string address = "endpoint-test-address";
            Success(Execute("addressables_group_add_entry", new JObject
            {
                ["assetPath"] = _assetPath,
                ["groupName"] = _groupName,
                ["address"] = address
            }));

            var createdGroup = Success(Execute("addressables_group_list"))["groups"]?.Children<JObject>()
                .Single(group => group["name"]?.ToString() == _groupName);
            Assert.That(createdGroup?["entryCount"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(AssetDatabase.FindAssets(_groupName + " BundledAssetGroupSchema").Length, Is.EqualTo(1));
            Assert.That(AssetDatabase.FindAssets(_groupName + " ContentUpdateGroupSchema").Length, Is.EqualTo(1));

            Success(Execute("addressables_build"));

            var buildRoot = Path.Combine("Library", "com.unity.addressables", "aa");
            var catalog = Directory.GetFiles(buildRoot, "catalog.*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            Assert.That(catalog, Is.Not.Null, "Addressables build did not generate a catalog.");
            Assert.That(Encoding.UTF8.GetString(File.ReadAllBytes(catalog)), Does.Contain(address));

            var bundles = Directory.GetFiles(buildRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("catalog.bin", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith("catalog.hash", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith("settings.json", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith("link.xml", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.That(bundles, Is.Not.Empty, "Addressables build did not generate an asset bundle.");
        }

        private static JObject Execute(string skill, JObject args = null)
        {
            return JObject.Parse(SkillRouter.Execute(skill, (args ?? new JObject()).ToString()));
        }

        private static JObject Success(JObject response)
        {
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString());
            Assert.That(response["result"]?["success"]?.Value<bool>(), Is.True, response.ToString());
            return (JObject)response["result"];
        }
    }
}

// Producer:Betsy
