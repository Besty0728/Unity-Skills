### asset_create_folder_batch
Create multiple folders.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see the example) |


`items` currently expects a JSON string, not a native array. The parent folder of each entry must already exist, so order nested folders outermost-first within one call.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, path, guid}]}`

```bash
curl -s "http://localhost:<port>/skill/asset_create_folder_batch?expectProject=<project>" -d '{"items":"[{\"folderPath\":\"Assets/Art\"},{\"folderPath\":\"Assets/Art/Textures\"}]"}'
```
