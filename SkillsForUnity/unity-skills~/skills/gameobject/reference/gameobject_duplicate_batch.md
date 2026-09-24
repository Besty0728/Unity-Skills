### gameobject_duplicate_batch
Duplicate several GameObjects in one call; each copy is a sibling named `<name>_Copy`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, originalName, copyName, copyEntityId, copyInstanceId, copyPath, copyParentPath}]}`. All-or-nothing: one missing object reverts every copy.

```python
unity_skills.call_skill("gameobject_duplicate_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])
```
