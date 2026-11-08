### qframework_mark_asset_bundle
Mark or unmark a project folder as a ResKit AssetBundle through `ResKitAssetsMenu.MarkAB`. `MarkAB` toggles, so this skill checks the current state first; repeating the same `marked` value is a no-op (`changed: false`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| folderPath | string | Yes | - | Project folder to mark or unmark |
| marked | bool | No | true | Desired mark state |

**Returns:** `path`, `marked`, `assetBundleName`, `changed`
