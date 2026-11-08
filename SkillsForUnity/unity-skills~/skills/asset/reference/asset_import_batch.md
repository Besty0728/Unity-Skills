### asset_import_batch
Import multiple external files.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see the example) |


`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, serverAvailability?}]}`: `target` is the item's `destinationPath`; `serverAvailability` appears only when a script-domain asset was written or Unity is compiling.

```bash
curl -s "http://localhost:<port>/skill/asset_import_batch?expectProject=<project>" -d '{"items":"[{\"sourcePath\":\"C:/Downloads/tex1.png\",\"destinationPath\":\"Assets/Textures/tex1.png\"},{\"sourcePath\":\"C:/Downloads/tex2.png\",\"destinationPath\":\"Assets/Textures/tex2.png\"}]"}'
```
