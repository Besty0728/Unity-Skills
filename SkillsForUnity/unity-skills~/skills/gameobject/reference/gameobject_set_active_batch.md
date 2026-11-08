### gameobject_set_active_batch
Enable or disable several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `active` (bool, default `true` when omitted).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, entityId, success, active, activeInHierarchy}]}`. All-or-nothing.

```bash
curl -s "http://localhost:<port>/skill/gameobject_set_active_batch?expectProject=<project>" -d '{"items":"[{\"name\":\"Enemy1\",\"active\":false},{\"name\":\"Enemy2\",\"active\":false}]"}'
```
