### asset_delete_batch
Delete multiple assets.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see the example) |


`items` currently expects a JSON string, not a native array.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, serverAvailability?}]}`: `target` is the deleted `path`; `serverAvailability` appears only when a script-domain asset was deleted or Unity is compiling.

```bash
curl -s "http://localhost:<port>/skill/asset_delete_batch?expectProject=<project>" -d '{"items":"[{\"path\":\"Assets/Textures/old1.png\"},{\"path\":\"Assets/Textures/old2.png\"}]"}'
```
