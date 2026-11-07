using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.OptionalPackages
{
    /// <summary>
    /// YooAsset editor-side skills, restricted to queries and Collector configuration. Never a build, a
    /// runtime-validation job (needs Play Mode), or an *_open_*_window skill (opening one has previously
    /// left the Editor window layout dirty for later tests).
    /// </summary>
    [TestFixture]
    public class YooAssetSkillsLiveTests : OptionalPackageTestBase
    {
        // Zero parameters, so an empty ProbeArgs can never trip the framework's MissingParam gate. This
        // skill is also ReadOnly, so a missing package only warns at the framework level (SkillRouter.
        // Validation.cs ApplyRequiredPackages) rather than short-circuiting — the actual MISSING_PACKAGE
        // comes from its own #if !YOO_ASSET_ANY / NoYooAsset() body once compiled without the package.
        // yooasset_check_installed cannot be used as the probe instead: it never errors, it always answers
        // success with installed:false, so the base class's Assert.Ignore gate would never fire.
        protected override string ProbeSkill => "yooasset_get_default_paths";

        private const string ProbePackageName = "R6OptProbePkg";
        private static readonly string BogusReportPath = ProbeFolder + "/missing.report";

        // 2.x and 3.x name the lazily created collector asset differently. Queried one type at a time, so nothing
        // depends on how FindAssets combines several t: filters.
        private static readonly string[] CollectorSettingTypes = { "AssetBundleCollectorSetting", "BundleCollectorSetting" };
        private static readonly string[] AssetArtSettingTypes = { "AssetArtScannerSetting" };

        [Test]
        public void CheckInstalled_ReportsInstalledTrue()
        {
            var result = Ok(Run("yooasset_check_installed", new JObject()));
            Assert.That(result["installed"]?.Value<bool>(), Is.True, result.ToString());
            Assert.That(result["compileDefineSet"]?.Value<bool>(), Is.True, result.ToString());
        }

        [Test]
        public void GetDefaultPaths_ReturnsBuildOutputAndStreamingAssetsRoots()
        {
            var result = Ok(Run(ProbeSkill, new JObject()));
            Assert.That(result["defaultBuildOutputRoot"]?.ToString(), Is.Not.Null.And.Not.Empty, result.ToString());
            Assert.That(result["streamingAssetsRoot"]?.ToString(), Is.Not.Null.And.Not.Empty, result.ToString());
        }

        [Test]
        public void CreateModifyAndRemoveCollectorPackage_ReadsBackDescription()
        {
            // The collector setting asset (AssetBundleCollectorSetting on 2.x, BundleCollectorSetting on 3.x) lives
            // at a fixed project path (Assets/, found by type, not by name) and is created lazily on first access —
            // outside Assets/R6OptProbe, so it is only ever deleted here if this test is the one that brought it into existence.
            bool settingPreexisted = FindSettingGuids(CollectorSettingTypes).Length > 0;
            try
            {
                Ok(Run("yooasset_create_collector_package", new JObject { ["packageName"] = ProbePackageName }));
                Ok(Run("yooasset_modify_collector_package", new JObject
                {
                    ["packageName"] = ProbePackageName,
                    ["packageDesc"] = "r6 probe package",
                }));

                var list = Ok(Run("yooasset_list_collector_packages", new JObject()));
                var pkg = list.SelectTokens($"$.packages[?(@.name == '{ProbePackageName}')]").FirstOrDefault();
                Assert.That(pkg, Is.Not.Null, list.ToString());
                Assert.That(pkg["desc"]?.ToString(), Is.EqualTo("r6 probe package"));

                Ok(Run("yooasset_remove_collector_package", new JObject { ["packageName"] = ProbePackageName }));

                var afterRemoval = Ok(Run("yooasset_list_collector_packages", new JObject()));
                var stillPresent = afterRemoval.SelectTokens($"$.packages[?(@.name == '{ProbePackageName}')]").Any();
                Assert.That(stillPresent, Is.False, afterRemoval.ToString());
            }
            finally
            {
                // Best-effort: if an assertion above threw before the explicit remove call ran, this still
                // takes the probe package back out of a settings file that predates this test.
                Run("yooasset_remove_collector_package", new JObject { ["packageName"] = ProbePackageName });
                if (!settingPreexisted) DeleteSettingsIfExist(CollectorSettingTypes);
            }
        }

        [Test]
        public void ListReportBundles_UnparseableFilterEncrypted_IsRejectedBeforeReportLookup()
        {
            // filterEncrypted/sortBy are validated before File.Exists(reportPath), so a report that was
            // never created still exercises the rejection — see YooAssetSkills.cs ListReportBundles.
            AssertSemanticInvalid(Run("yooasset_list_report_bundles", new JObject
            {
                ["reportPath"] = BogusReportPath,
                ["filterEncrypted"] = "maybe",
            }), "filterEncrypted");
        }

        [Test]
        public void ListReportBundles_UnknownSortBy_IsRejectedBeforeReportLookup()
        {
            AssertSemanticInvalid(Run("yooasset_list_report_bundles", new JObject
            {
                ["reportPath"] = BogusReportPath,
                ["sortBy"] = "bogus",
            }), "sortBy");
        }

        [Test]
        public void ListReportAssets_UnknownSortBy_IsRejectedBeforeReportLookup()
        {
            // Same ordering as ListReportBundles: sortBy is checked before TryLoadReport, so this path
            // needs no fabricated BuildReport file either.
            AssertSemanticInvalid(Run("yooasset_list_report_assets", new JObject
            {
                ["reportPath"] = BogusReportPath,
                ["sortBy"] = "bogus",
            }), "sortBy");
        }

        [Test]
        public void AssetArtSkill_On3x_ReportsRemovedFeature()
        {
            if (ApiLine() != "3.x") Assert.Ignore("AssetArt removal applies to the YooAsset 3.x line only.");
            var json = Run("yooasset_list_assetart_scanners", new JObject());
            AssertError(json);
            Assert.That(json["errorCode"]?.ToString(), Is.EqualTo("SKILL_ERROR"), json.ToString());
            Assert.That(json["error"]?.ToString(), Does.Contain("YooAsset 3.x removed this feature"), json.ToString());
            Assert.That(json["suggestedFixes"]?.First?["action"]?.ToString(), Is.EqualTo("ask_user"), json.ToString());
        }

        [Test]
        public void AssetArtSkill_On2x_ListsScanners()
        {
            if (ApiLine() != "2.x") Assert.Ignore("AssetArt exists on the YooAsset 2.x line only.");
            // AssetArtScannerSetting.asset is created lazily under Assets/ like the collector setting.
            bool settingPreexisted = FindSettingGuids(AssetArtSettingTypes).Length > 0;
            try
            {
                var result = Ok(Run("yooasset_list_assetart_scanners", new JObject()));
                Assert.That(result["scannerCount"], Is.Not.Null, result.ToString());
            }
            finally
            {
                if (!settingPreexisted) DeleteSettingsIfExist(AssetArtSettingTypes);
            }
        }

        private static string ApiLine() =>
            Ok(Run("yooasset_check_installed", new JObject()))["apiLine"]?.ToString();

        private static string[] FindSettingGuids(string[] typeNames) =>
            typeNames.SelectMany(t => AssetDatabase.FindAssets("t:" + t)).Distinct().ToArray();

        private static void DeleteSettingsIfExist(string[] typeNames)
        {
            foreach (var guid in FindSettingGuids(typeNames))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            }
        }
    }
}

// Producer:Betsy
