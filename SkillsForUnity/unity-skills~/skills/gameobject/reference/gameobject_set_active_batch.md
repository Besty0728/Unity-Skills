### gameobject_set_active_batch
Enable or disable several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path`, `active` (bool, default `true` when omitted).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, entityId, success, active, activeInHierarchy}]}`. All-or-nothing.

```python
unity_skills.call_skill("gameobject_set_active_batch", items=[
    {"name": "Enemy1", "active": False},
    {"name": "Enemy2", "active": False}
])
```
