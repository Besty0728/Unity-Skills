### qframework_clear_asset_bundles
Delete all built ResKit AssetBundle output through `ResKitEditorAPI.ForceClearAssetBundles`: `AssetBundles/` at the project root and `StreamingAssets/AssetBundles`.

**Irreversible.** The delete happens on the filesystem, outside `AssetDatabase`, so no workflow snapshot exists and `TracksWorkflow` is false. Treat it as permanent. Refused under Approval and Auto (`Operation=Delete`, `RiskLevel="high"`).

**Parameters:** None

**Returns:** `success`, `error`, `clearedDirs`
