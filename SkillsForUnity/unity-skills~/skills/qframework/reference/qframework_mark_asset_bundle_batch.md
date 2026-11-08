### qframework_mark_asset_bundle_batch
Mark or unmark several folders in one request.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| items | string | Yes | - | JSON array of `{folderPath, marked}` |

**Returns:** `totalItems`, `successCount`, `failCount`, `results`
