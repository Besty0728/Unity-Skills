using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// Addressables (com.unity.addressables) live skill tests: group create/list, entry add with an
    /// explicit address, and profile read. No skill in AddressablesSkills.cs creates the
    /// AddressableAssetSettings singleton (every skill only reads
    /// AddressableAssetSettingsDefaultObject.Settings) -- if this project has never opened
    /// Window > Asset Management > Addressables > Groups, settings must be created the same way that
    /// window does, via AddressableAssetSettingsDefaultObject.GetSettings(true), reached only by
    /// reflection here. Assets/AddressableAssetsData is removed again in OneTimeTearDown, but only if
    /// this fixture is the one that created it.
    /// </summary>
    [TestFixture]
    public class AddressablesSkillsLiveTests : OptionalPackageTestBase
    {
        private const string SettingsFolder = "Assets/AddressableAssetsData";

        private static bool _settingsCreatedByFixture;

        protected override string ProbeSkill => "addressables_group_list";

        [OneTimeSetUp]
        public void EnsureAddressablesSettingsExist()
        {
            var defaultObjectType = PackageType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");

            var existsProperty = defaultObjectType.GetProperty("SettingsExists", BindingFlags.Public | BindingFlags.Static);
            Assert.That(existsProperty, Is.Not.Null, "AddressableAssetSettingsDefaultObject.SettingsExists was not found.");
            if ((bool)existsProperty.GetValue(null))
                return;

            var getSettingsMethod = defaultObjectType.GetMethod("GetSettings", BindingFlags.Public | BindingFlags.Static);
            Assert.That(getSettingsMethod, Is.Not.Null, "AddressableAssetSettingsDefaultObject.GetSettings(bool) was not found.");
            getSettingsMethod.Invoke(null, new object[] { true });
            _settingsCreatedByFixture = true;
        }

        [OneTimeTearDown]
        public void RemoveSettingsIfCreatedByFixture()
        {
            if (_settingsCreatedByFixture && AssetDatabase.IsValidFolder(SettingsFolder))
                AssetDatabase.DeleteAsset(SettingsFolder);
        }

        [Test]
        public void GroupCreate_AppearsInGroupList()
        {
            var created = Ok(Run("addressables_group_create", new JObject { ["groupName"] = "R6AddrGroupA" }));
            var actualName = created["groupName"]?.ToString();
            Assert.That(actualName, Is.Not.Null.And.Not.Empty, created.ToString());

            try
            {
                var list = Ok(Run("addressables_group_list", new JObject()));
                var group = list["groups"]?.FirstOrDefault(g => g["name"]?.ToString() == actualName);
                Assert.That(group, Is.Not.Null, list.ToString());
                Assert.That(group["isDefault"]?.Value<bool>(), Is.False);
                Assert.That(group["entryCount"]?.Value<int>(), Is.EqualTo(0));
            }
            finally
            {
                Ok(Run("addressables_group_delete", new JObject { ["groupName"] = actualName }));
            }
        }

        [Test]
        public void GroupAddEntry_WritesAddressAndGroupListShowsEntryCount()
        {
            var created = Ok(Run("addressables_group_create", new JObject { ["groupName"] = "R6AddrGroupB" }));
            var actualName = created["groupName"]?.ToString();

            try
            {
                EnsureProbeFolder();
                var assetPath = $"{ProbeFolder}/R6AddrAsset.asset";
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ScriptableObject>(), assetPath);

                var added = Ok(Run("addressables_group_add_entry", new JObject
                {
                    ["assetPath"] = assetPath, ["groupName"] = actualName, ["address"] = "R6TestAddress",
                }));
                Assert.That(added["address"]?.ToString(), Is.EqualTo("R6TestAddress"), added.ToString());
                Assert.That(added["groupName"]?.ToString(), Is.EqualTo(actualName));

                var list = Ok(Run("addressables_group_list", new JObject()));
                var group = list["groups"]?.FirstOrDefault(g => g["name"]?.ToString() == actualName);
                Assert.That(group?["entryCount"]?.Value<int>(), Is.EqualTo(1), list.ToString());
            }
            finally
            {
                Ok(Run("addressables_group_delete", new JObject { ["groupName"] = actualName }));
            }
        }

        [Test]
        public void ProfileGet_ReturnsActiveProfileAndProfilesList()
        {
            var result = Ok(Run("addressables_profile_get", new JObject()));
            Assert.That(result["activeProfile"]?.ToString(), Is.Not.Null.And.Not.Empty, result.ToString());
            Assert.That(result["profiles"]?.Values<string>().Any(), Is.True, result.ToString());
        }
    }
}

// Producer:Betsy
