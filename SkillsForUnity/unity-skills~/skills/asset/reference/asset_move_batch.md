### asset_move_batch
Move multiple assets.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see the example) |


`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, from, to, serverAvailability?}]}`: `target` is the item's `sourcePath`; `serverAvailability` appears only when a script-domain asset was moved or Unity is compiling.

```bash
curl -s "http://localhost:<port>/skill/asset_move_batch?expectProject=<project>" -d '{"items":"[{\"sourcePath\":\"Assets/Old/mat1.mat\",\"destinationPath\":\"Assets/New/mat1.mat\"},{\"sourcePath\":\"Assets/Old/mat2.mat\",\"destinationPath\":\"Assets/New/mat2.mat\"}]"}'
```
