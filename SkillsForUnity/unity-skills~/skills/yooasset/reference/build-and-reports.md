# YooAsset Build and Reports

## Build pipelines

- `yooasset_build_bundles` accepts `pipeline=ScriptableBuildPipeline` (uses `ScriptableBuildParameters`) or `RawFileBuildPipeline` (uses `RawFileBuildParameters`). Any other pipeline is rejected.
- `EditorSimulateBuildPipeline` is rejected here. Use `yooasset_simulate_build` instead: it writes virtual bundles only and no files.
- `BuiltinBuildPipeline` (2.x), and `LegacyBuildPipeline`, `ArchiveFileBuildPipeline` or `InstantAssetBuildPipeline` (3.x), are rejected.
- `packageVersion="auto"` uses the UTC time as `yyyyMMddHHmm`. Pass a semantic version when the build must be reproducible.
- Output goes to `<projectPath>/Bundles/<PackageName>/<Version>`.
- `yooasset_build_bundles` is not undo-tracked: it is external file I/O, not scene or asset state. `clearBuildCache=true` reruns the whole pipeline; the defaults skip that for speed.
- Do not call it while `EditorUserBuildSettings.isBuildingPlayer` is true. `BuildParameters.CheckBuildParameters` throws.
- Do not pass `timeout`. The 2.x build parameter was removed (2.3.16); the runtime watchdog is `CacheFileSystemParameters.DOWNLOAD_WATCH_DOG_TIME`, which is game code, not a build option.

## Report file

The BuildReport is a `.report` file next to the bundles, on both lines:

`<BuildOutputRoot>/<BuildTarget>/<PackageName>/<Version>/<PackageName>_<PackageVersion>.report`

Do not pass `BuildReport.json`, `<PackageName>_<PackageVersion>.json` or `buildlogtep.json`. The `.json` file is the package manifest, and `buildlogtep.json` is the Scriptable Build Pipeline trace log. `yooasset_list_report_bundles` reads the `.report` file on its own, so an archived copy also works.

## Report analysis flow

1. `yooasset_load_build_report` for the summary and totals.
2. `yooasset_list_report_bundles` with `sortBy="size"` for the largest bundles.
3. `yooasset_list_report_assets` with `search` to find assets.
4. `yooasset_get_bundle_detail` or `yooasset_get_asset_detail` for one item.
5. `yooasset_get_dependency_graph` with `maxNodes` to limit the graph size.
6. `yooasset_list_independ_assets` for cleanup candidates. These are assets that no main asset references. Check them by hand before removing anything: a main asset added later may still need one.
7. `yooasset_compare_build_reports` to compare two reports for additions, removals and size changes.
