### component_remove_batch
Remove components from several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType`. Unlike `component_remove` there is no index: every component of that type on the object is removed.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, removed, count}]}` (`count` = components removed). All-or-nothing. NeverInSemi.

```python
unity_skills.call_skill("component_remove_batch", items=[
    {"instanceId": 12345, "componentType": "BoxCollider"},
    {"instanceId": 12346, "componentType": "BoxCollider"}
])
```
