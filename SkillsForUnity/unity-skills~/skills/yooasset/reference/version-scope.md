# YooAsset Version Scope

The module supports two YooAsset lines at once. Skill names and parameter names are the same on both.

| Line | Range | Compile define | Verified against |
|------|-------|----------------|------------------|
| 2.x | `[2.3.15,2.4.0-0)` | `YOO_ASSET` | 2.3.19 |
| 3.x | `[3.0.4,3.1.0-0)` | `YOO_ASSET_3` | 3.0.7 |

The ranges come from `Editor/UnitySkills.Editor.asmdef` (`versionDefines`), and the same range text is reported in `supportedRange` (`Editor/Skills/YooAssetSkills.cs`).

## Version rules

- The `-0` upper bound keeps the next minor line's prereleases out. Each line is pinned to one minor.
- 3.0.0-beta to 3.0.3-beta are prereleases and are rejected as unsupported. 2.4+ and 3.1+ are also unsupported.
- Earlier 2.x versions use an incompatible manifest format and a `IFilterRule` without `FindAssetType`. Use 2.3.15 or later. 2.3.17 fixed a CRC validation bug, so 2.3.15 and 2.3.16 users should upgrade before relying on `verifyBuildingResult=true`.
- An installed package outside both ranges returns `SKILL_ERROR` with `reason: "unsupported_package_version"`, `installedVersion` and `supportedRange`. Tell the user; do not install or upgrade the package yourself.
- `yooasset_check_installed` reports `apiLine` (`"2.x"`, `"3.x"` or null), `supportedRange` and `versionSupported`. It is the only skill that works without the compile define.
- The six AssetArt skills (`yooasset_*assetart*`) exist on 2.x only. On 3.x they return `SKILL_ERROR` saying that YooAsset 3.x removed the feature. Do not call them when `apiLine` is `"3.x"`.
- Builder settings saved by the 2.x Builder window use a different EditorPrefs key on 3.x. After an upgrade, `yooasset_get_build_settings` returns defaults.
- A `.report` written by one major version and read by the other loses renamed fields (`IndependAssets`, `FileCRC`, `AssetGUID`). The report skills add `warnings` when the report's `YooVersion` major differs from the installed line.

## Parameter mapping

Parameter names stay on the 2.x names on both lines. Internally they map to the 3.x API:

| Skill parameter or output | 3.x API it maps to |
|---------------------------|--------------------|
| `buildinFileCopyOption` | `BuildParameters.BundledCopyOption`, `BundleBuilderSetting.*BundledCopyOption` |
| `buildinFileCopyParams` | `BuildParameters.BundledCopyParams`, `BundleBuilderSetting.*BundledCopyParams` |
| `encryptionServicesClassName` | `BundleBuilderSetting.*BundleEncryptorClassName` |
| `manifestProcessServicesClassName` | `BundleBuilderSetting.*ManifestEncryptorClassName` |
| `manifestRestoreServicesClassName` | `BundleBuilderSetting.*ManifestDecryptorClassName` |
| `independAssetCount`, `yooasset_list_independ_assets` | `BuildReport.IndependentAssets` |
| `fileCRC` | `ReportBundleInfo.FileCrc` |
| `assetGUID` (report assets and independent assets) | `ReportAssetInfo.AssetGuid`, `ReportIndependentAsset.AssetGuid` |
| `activeRule`, `packRule`, `filterRule`, `ignoreRule` validation | `HasGroupActiveRuleName`, `HasBundlePackRuleName`, `HasAssetFilterRuleName`, `HasAssetIgnoreRuleName` |
| `downloadingMaxNumber`, `failedTryAgain` | `ResourceDownloaderOptions(maximumConcurrency, retryCount)` |

Runtime-side rules (PlayMode mapping, handle lifecycle, update flow, legacy-API migration) are in [yooasset-design](../../yooasset-design/SKILL.md). 3.x renames are in [yooasset-design V3](../../yooasset-design/V3.md).
