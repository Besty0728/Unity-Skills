### gameobject_rename_batch
Rename several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `newName` (required per item).

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, oldName, newName, entityId, instanceId}]}`. All-or-nothing: a missing object or `newName` reverts every rename.

```bash
curl -s "http://localhost:<port>/skill/gameobject_rename_batch?expectProject=<project>" -d '{"items":"[{\"entityId\":\"<enemy_a_entityId>\",\"newName\":\"Enemy_01\"},{\"entityId\":\"<enemy_b_entityId>\",\"newName\":\"Enemy_02\"}]"}'
```
