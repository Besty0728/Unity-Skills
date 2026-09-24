### gameobject_rename_batch
Rename several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `newName` (required per item).

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, oldName, newName, entityId, instanceId}]}`. All-or-nothing: a missing object or `newName` reverts every rename.

```python
unity_skills.call_skill("gameobject_rename_batch", items=[
    {"instanceId": 12345, "newName": "Enemy_01"},
    {"instanceId": 12346, "newName": "Enemy_02"}
])
```
