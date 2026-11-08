### qframework_build_asset_bundles
Build AssetBundles for a build target through `BuildScript.BuildAssetBundles`. **Blocks the Editor main thread for the duration of the build.** Refused under Approval and Auto (`RiskLevel="high"`, `LongRunning`, no dryRun).

The output directory is fixed by QFramework: `AssetBundles/<platform>` at the project root, mirrored into `StreamingAssets/AssetBundles/<platform>`. It is not configurable. `outputDir` and `streamingAssetsDir` always come from `EditorUserBuildSettings.activeBuildTarget`, not from the requested `buildTarget`: QFramework's helper reads the active target and ignores the argument, which only steers the internal `BuildPipeline.BuildAssetBundles` step. A `buildTarget` that differs from the active platform still builds for that target, but the files land under the active platform's directory and the response adds `warning`. Switch the active build target first if the paths and the build must agree.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| buildTarget | string | No | active build target | `UnityEditor.BuildTarget` enum name |

**Returns:** `success`, `error`, `buildTarget`, `outputDir`, `streamingAssetsDir`, `elapsedSeconds`, and `warning` when `buildTarget` differs from the active platform.
