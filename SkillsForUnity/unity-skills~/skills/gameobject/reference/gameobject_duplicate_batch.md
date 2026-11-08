### gameobject_duplicate_batch
Duplicate several GameObjects in one call; each copy is a sibling named `<name>_Copy`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, originalName, copyName, copyEntityId, copyInstanceId, copyPath, copyParentPath}]}`. All-or-nothing: one missing object reverts every copy.

```bash
curl -s "http://localhost:<port>/skill/gameobject_duplicate_batch?expectProject=<project>" -d '{"items":"[{\"entityId\":\"<tree_entityId>\"},{\"entityId\":\"<rock_entityId>\"}]"}'
```
