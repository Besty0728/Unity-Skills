---
name: unity-yooasset
description: "YooAsset hot-update / asset bundle automation. Build bundles via ScriptableBuildPipeline or RawFileBuildPipeline, run Editor simulate builds, manage AssetBundleCollector package/group/collector settings, analyze BuildReport .report files at bundle/asset/dependency-graph level, run PlayMode runtime validation jobs, and open/use YooAsset Reporter/Debugger/AssetArtScanner tools. Triggers: yooasset, YooAsset, AssetBundleBuilder, AssetBundleCollector, BuildReport, ResourcePackage, ScriptableBuildPipeline, RawFileBuildPipeline, EditorSimulateBuildPipeline, CollectorPackage, CollectorGroup, AddressRule, PackRule, FilterRule, IgnoreRule, BuildReport assets, dependency graph, AssetArtScanner, Reporter, Debugger, runtime validation, 资源打包, 资源收集, 资源收集器, 资源规则, 构建报告, 构建管线, 模拟构建, 热更新打包, 资源分组, 资产分析, 依赖图, 孤立资源, 运行时验证, asset bundle build, asset bundle collector, build report, hot update build, bundle dependency, orphan asset, asset pipeline. Requires com.tuyoogame.yooasset (2.3.15+)."
---

# Unity YooAsset Skills

Editor-side automation for the YooAsset hot-update framework — build pipeline orchestration, Collector configuration CRUD, BuildReport analysis, PlayMode runtime validation, and companion YooAsset tools. Every skill wraps a concrete YooAsset Editor/runtime API path validated against 2.3.18 source. When the package is absent, every skill except `yooasset_check_installed` returns a `NoYooAsset()` error with install instructions.

> **Requires**: `com.tuyoogame.yooasset` **≥ 2.3.15**, Unity 2022.3+ (validated against 2.3.18).
> **Strongly recommended**: before writing ANY YooAsset runtime code, load [yooasset-design](../yooasset-design/SKILL.md). PlayMode / parameter-class / handle-lifecycle pitfalls are strict, and only the advisory module surfaces them.

## Guardrails

**Mode**: Both (Semi-Auto + Full-Auto).

**DO NOT** (common hallucinations):
- `yooasset_initialize` / `yooasset_load_asset` / `yooasset_create_downloader` — do NOT exist as general-purpose REST skills. Runtime APIs (`YooAssets.Initialize`, default-package `YooAssets.LoadAssetAsync`, `ResourcePackage.LoadAssetAsync`, `RequestPackageVersionAsync`, `CreateResourceDownloader`) belong in game code. Use [yooasset-design](../yooasset-design/SKILL.md) when writing runtime code.
- `yooasset_modify_group` / `yooasset_delete_collector` / `yooasset_open_scanner_window` — wrong names. Use the exact schema names below or query `GET /skills/schema?category=YooAsset`.
- `yooasset_install` — NOT a skill. Package install is a Package Manager user action.
- Do NOT pass `timeout` to `yooasset_build_bundles` — the property was removed in YooAsset 2.3.16. Runtime watchdog now lives on `CacheFileSystemParameters.DOWNLOAD_WATCH_DOG_TIME` (runtime concern, not a build parameter).
- Do NOT call `yooasset_build_bundles` while `EditorUserBuildSettings.isBuildingPlayer == true` — `BuildParameters.CheckBuildParameters` throws.

**Routing**:
- Collector configuration (packages, groups, collectors, rules) → this module.
- Actual bundle build + simulate + paths → this module.
- Build report analysis (bundle size, asset list/detail, dependency graph, report compare, orphan) → this module.
- Runtime validation in Editor PlayMode → `yooasset_runtime_validate_package` + polling/cleanup skills in this module.
- Runtime code generation/design (Initialize / Load / Download / Scene in game scripts) → write it yourself using [yooasset-design](../yooasset-design/SKILL.md).
- YooAsset companion windows and AssetArtScanner config/runs → this module.
- Version-policy decisions (2.x vs pre-2.3.15 manifest, `FindAssetType` on `IFilterRule`) → [yooasset-design/PITFALLS.md](../yooasset-design/PITFALLS.md).

## Skills

### Environment (1)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_check_installed` | Reflection probe — works even when the package is missing. Reports runtime assembly, package version, editor availability, and which of the 4 pipelines are present. | (none) |

### Build pipeline (7)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_build_bundles` | Build bundles via `ScriptableBuildPipeline` or `RawFileBuildPipeline`. Writes to `<projectPath>/Bundles/<PackageName>/<Version>`. | `packageName`, `packageVersion="auto"`, `pipeline="ScriptableBuildPipeline"`, `buildTarget?`, `compression="LZ4"`, `fileNameStyle="HashName"`, `clearBuildCache=false`, `useAssetDependencyDB=true`, `verifyBuildingResult=true`, `replaceAssetPathWithAddress=false`, `stripUnityVersion=false`, `disableWriteTypeTree=false`, `trackSpriteAtlasDependencies=false`, `writeLinkXML=true`, `cacheServerHost=""`, `cacheServerPort=0`, `builtinShadersBundleName=""`, `monoScriptsBundleName=""`, `includePathInHash=false`, `buildinFileCopyOption="None"`, `buildinFileCopyParams=""`, `enableLog=true` |
| `yooasset_simulate_build` | Run `AssetBundleSimulateBuilder.SimulateBuild` for an `EditorSimulateMode` package — virtual bundles only, no files written. | `packageName` |
| `yooasset_get_default_paths` | Return `BuildOutputRoot` + `StreamingAssetsRoot` that YooAsset uses. | (none) |
| `yooasset_get_build_settings` | Read persisted YooAsset AssetBundle Builder EditorPrefs for a package/pipeline. | `packageName`, `pipeline?` |
| `yooasset_set_build_settings` | Persist AssetBundle Builder EditorPrefs for repeatable build UI defaults. | `packageName`, `pipeline?`, `compression?`, `fileNameStyle?`, `buildinFileCopyOption?`, `buildinFileCopyParams?`, `encryptionServicesClassName?`, `manifestProcessServicesClassName?`, `manifestRestoreServicesClassName?`, `clearBuildCache?`, `useAssetDependencyDB?` |
| `yooasset_open_builder_window` | Open the YooAsset `AssetBundle Builder` Editor window. | (none) |
| `yooasset_open_collector_window` | Open the YooAsset `AssetBundle Collector` Editor window. | (none) |

### YooAsset tools (8)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_open_reporter_window` | Open the YooAsset AssetBundle Reporter window. | (none) |
| `yooasset_open_debugger_window` | Open the YooAsset AssetBundle Debugger window. | (none) |
| `yooasset_open_assetart_scanner_window` | Open the YooAsset AssetArt Scanner window. | (none) |
| `yooasset_list_assetart_scanners` | List AssetArtScanner configurations, optionally filtered by keyword. | `keyword?` |
| `yooasset_run_assetart_scanner` | Run one scanner and optionally save its report file. | `scannerGUID`, `saveDirectory?` |
| `yooasset_run_all_assetart_scanners` | Run all scanners, optionally filtered by keyword. | `keyword?` |
| `yooasset_import_assetart_scanner_config` | Import AssetArtScanner JSON config. | `configPath` |
| `yooasset_export_assetart_scanner_config` | Export AssetArtScanner JSON config. | `configPath` |

### Runtime validation (3)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_runtime_validate_package` | Start an async PlayMode job that initializes `EditorSimulateMode`, sets default package, optionally validates one asset load/release and downloader status, then optionally cleans up and returns to Edit Mode. | `packageName`, `assetLocation?`, `restoreEditMode=true`, `cleanup=true`, `checkDownloader=true`, `downloadingMaxNumber=4`, `failedTryAgain=1` |
| `yooasset_runtime_get_validation_result` | Poll the runtime validation job status/result. | `jobId` |
| `yooasset_runtime_cleanup` | Remove completed validation jobs and optionally force `YooAssets.Destroy()` / exit PlayMode. | `jobId?`, `forceYooAssetsDestroy=false`, `exitPlayMode=false` |

### Collector configuration (13)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_list_collector_packages` | List Packages → Groups → Collectors tree. `verbose=true` returns the full descent. | `verbose=false` |
| `yooasset_list_collector_rules` | Return registered Active / Address / Pack / Filter / Ignore rule classes. | `ruleKind="all"` (or `activeRule|addressRule|packRule|filterRule|ignoreRule`) |
| `yooasset_create_collector_package` | Create a new Collector Package. Fails on duplicate unless `allowDuplicate=true`. | `packageName`, `allowDuplicate=false` |
| `yooasset_create_collector_group` | Create a Group inside an existing Package. | `packageName`, `groupName`, `groupDesc=""`, `activeRule="EnableGroup"`, `assetTags=""` |
| `yooasset_add_collector` | Add an `AssetBundleCollector` to a Group. Validates `collectPath` via AssetDatabase and each rule via its registered class name. | `packageName`, `groupName`, `collectPath`, `collectorType="MainAssetCollector"`, `addressRule="AddressByFileName"`, `packRule="PackDirectory"`, `filterRule="CollectAll"`, `assetTags=""`, `userData=""` |
| `yooasset_save_collector_config` | Persist the `AssetBundleCollectorSetting.asset`. Optionally run `FixFile()` to repair dangling rule references. | `fixErrors=true` |
| `yooasset_modify_collector_settings` | Modify global Collector settings. | `showPackageView=false`, `uniqueBundleName=false`, `save=true` |
| `yooasset_modify_collector_package` | Rename/describe a package and set package-level options. | `packageName`, `packageDesc?`, `newPackageName?`, `enableAddressable=false`, `supportExtensionless=true`, `locationToLower=false`, `includeAssetGUID=false`, `autoCollectShaders=true`, `ignoreRule="NormalIgnoreRule"`, `save=true` |
| `yooasset_remove_collector_package` | Remove a Collector package by name. | `packageName`, `save=true` |
| `yooasset_modify_collector_group` | Rename/describe a group and update active rule/tags. | `packageName`, `groupName`, `newGroupName?`, `groupDesc?`, `activeRule="EnableGroup"`, `assetTags?`, `save=true` |
| `yooasset_remove_collector_group` | Remove a group from a package. | `packageName`, `groupName`, `save=true` |
| `yooasset_modify_collector` | Modify an existing collector matched by `collectPath`. | `packageName`, `groupName`, `collectPath`, `newCollectPath?`, `collectorType="MainAssetCollector"`, `addressRule="AddressByFileName"`, `packRule="PackDirectory"`, `filterRule="CollectAll"`, `assetTags?`, `userData?`, `save=true` |
| `yooasset_remove_collector` | Remove a collector matched by `collectPath`. | `packageName`, `groupName`, `collectPath`, `save=true` |

### Build report analysis (8)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `yooasset_load_build_report` | Deserialize a YooAsset BuildReport `.report` file and return `ReportSummary` metadata + top-level totals. | `reportPath` |
| `yooasset_list_report_bundles` | Paginated / filtered / sorted bundle listing from the report. | `reportPath`, `filterEncrypted?`, `filterTag?`, `sortBy="size"` (`size|name|refCount|dependCount`), `limit=100`, `offset=0` |
| `yooasset_get_bundle_detail` | Full `ReportBundleInfo` for a single bundle — `DependBundles`, `ReferenceBundles`, asset `BundleContents`. | `reportPath`, `bundleName` |
| `yooasset_list_report_assets` | Paginated / filtered / sorted asset listing from the report. | `reportPath`, `filterBundle?`, `filterTag?`, `search?`, `sortBy="path"` (`path|size|bundle|dependCount`), `limit=100`, `offset=0` |
| `yooasset_get_asset_detail` | Full `ReportAssetInfo` by asset path or address. | `reportPath`, `assetPath?`, `address?` |
| `yooasset_get_dependency_graph` | Compact bundle/asset dependency graph for visualization or focused analysis. | `reportPath`, `rootBundle?`, `rootAssetPath?`, `maxNodes=200` |
| `yooasset_compare_build_reports` | Compare two `.report` files for bundle/asset additions, removals, and size changes. | `oldReportPath`, `newReportPath`, `limit=100` |
| `yooasset_list_independ_assets` | Assets not referenced by any main asset — cleanup candidates. | `reportPath`, `limit=100`, `offset=0` |

## Quick Start

```python
import unity_skills as u

# 0. Probe installation (works even if package is missing)
u.call_skill("yooasset_check_installed")
# -> { installed, packageVersion, availablePipelines: [...], editorAvailable, ... }

# 1. Configure collectors (end-to-end)
u.call_skill("yooasset_list_collector_rules", ruleKind="all")  # discover available rule class names
u.call_skill("yooasset_create_collector_package", packageName="DefaultPackage")
u.call_skill("yooasset_create_collector_group",
    packageName="DefaultPackage", groupName="UI", activeRule="EnableGroup",
    assetTags="ui")
u.call_skill("yooasset_add_collector",
    packageName="DefaultPackage", groupName="UI",
    collectPath="Assets/GameRes/UI",
    collectorType="MainAssetCollector",
    addressRule="AddressByFileName",
    packRule="PackDirectory",
    filterRule="CollectAll")
u.call_skill("yooasset_modify_collector_package",
    packageName="DefaultPackage",
    enableAddressable=True,
    supportExtensionless=True,
    locationToLower=False)
u.call_skill("yooasset_save_collector_config", fixErrors=True)

# 2. Simulate build (EditorSimulateMode) — fast, no file I/O
u.call_skill("yooasset_simulate_build", packageName="DefaultPackage")
# -> { packageRootDirectory: ".../Bundles/DefaultPackage/Simulate" }

# 3. Real build
result = u.call_skill("yooasset_build_bundles",
    packageName="DefaultPackage",
    packageVersion="1.0.0",
    pipeline="ScriptableBuildPipeline",
    compression="LZ4",
    fileNameStyle="HashName",
    verifyBuildingResult=True)

# 4. Analyze the report
import os
report_path = os.path.join(
    result["outputDirectory"],
    f"{result['packageName']}_{result['packageVersion']}.report")
u.call_skill("yooasset_load_build_report", reportPath=report_path)
u.call_skill("yooasset_list_report_bundles", reportPath=report_path, sortBy="size", limit=20)
u.call_skill("yooasset_list_report_assets", reportPath=report_path, search="UI", limit=20)
u.call_skill("yooasset_get_dependency_graph", reportPath=report_path, maxNodes=100)
u.call_skill("yooasset_list_independ_assets", reportPath=report_path)

# 5. Optional PlayMode runtime validation
job = u.call_skill("yooasset_runtime_validate_package",
    packageName="DefaultPackage",
    assetLocation="ui/example",
    restoreEditMode=True,
    cleanup=True)
u.call_skill("yooasset_runtime_get_validation_result", jobId=job["jobId"])
```

## Critical Rules (must read)

1. **`yooasset_check_installed` is the ONLY skill that works without `YOO_ASSET` compile define.** Every other skill returns `NoYooAsset()` — correct the environment (install or upgrade the package, then let Unity recompile) before retrying.
2. **Pipeline ↔ Parameters pairing is enforced.** `yooasset_build_bundles pipeline=ScriptableBuildPipeline` uses `ScriptableBuildParameters`; `pipeline=RawFileBuildPipeline` uses `RawFileBuildParameters`. `EditorSimulateBuildPipeline` is rejected here — use `yooasset_simulate_build`. `BuiltinBuildPipeline` is legacy and explicitly rejected.
3. **`packageVersion="auto"` fills `DateTime.UtcNow.ToString("yyyyMMddHHmm")`.** Pass a semver string when you want deterministic versioning.
4. **`yooasset_add_collector` validates `collectPath`** via `AssetDatabase.LoadAssetAtPath`. Paths must be under `Assets/...`; absolute or relative `../` paths will fail.
5. **Rule names in Create Skills are CLASS names, not display names.** Use `yooasset_list_collector_rules` to discover valid values (e.g. `EnableGroup`, `AddressByFileName`, `PackDirectory`, `CollectAll`, `NormalIgnoreRule`).
6. **Collector mutations can save immediately or be batched.** Create skills mark `IsDirty=true`; modify/remove skills expose `save=true`. For bulk edits, pass `save=false` and finish with `yooasset_save_collector_config`.
7. **`yooasset_build_bundles` is NOT undo-tracked** (external file I/O, not scene/asset state). `clearBuildCache=true` re-runs the full pipeline; defaults avoid it for incremental speed.
8. **BuildReport lives next to bundles as a `.report` file** — YooAsset 2.3.18 writes it as `<BuildOutputRoot>/<BuildTarget>/<PackageName>/<Version>/<PackageName>_<PackageVersion>.report`. Do not pass `BuildReport.json`, `<PackageName>_<PackageVersion>.json`, or `buildlogtep.json`: the `.json` file is the package manifest and `buildlogtep.json` is the Scriptable Build Pipeline trace log. `yooasset_list_report_bundles` reads the `.report` file standalone, so you can also point it at an archived copy.
9. **Runtime validation may enter PlayMode.** `yooasset_runtime_validate_package` is intentionally async; poll the job result and use `yooasset_runtime_cleanup` if the editor is left in a state you do not want.
10. **`yooasset_list_independ_assets` identifies assets not referenced by any main asset** — candidates for the Collector to drop. Verify manually before removing; a main asset created later could still need them.

## Version Scope

- **Target**: YooAsset `2.3.18` (published 2025-12-04). Source anchors use this version.
- **Minimum**: `2.3.15` (enforced by the asmdef `versionDefines` entry). Earlier versions use an incompatible manifest format and pre-`FindAssetType` `IFilterRule` (would not compile against this Skill module).
- **2.3.17** fixed a critical CRC validation bug; users on 2.3.15/2.3.16 should upgrade before relying on `verifyBuildingResult=true`.
- Runtime-side rules (PlayMode mapping, handle lifecycle, update flow, legacy-API migration) → [yooasset-design/SKILL.md](../yooasset-design/SKILL.md).

## Exact Signatures

For authoritative parameter names, defaults, and return fields, query `GET /skills/schema?category=YooAsset` or `unity_skills.get_skill_schema()`. This document is a routing / best-practice guide, not the signature source.
