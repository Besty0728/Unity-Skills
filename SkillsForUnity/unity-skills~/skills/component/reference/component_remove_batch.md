### component_remove_batch
Remove components from several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType`, `componentIndex?`. With `componentIndex` only that instance is removed (as in `component_remove`); without it every component of that type on the object is removed. A component another one still requires is refused before anything is removed (`requiredBy`, as in `component_remove`); items run in order, so removing the dependent in an earlier item frees it.

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, removed, count, remainingOfType}]}` (`count` = components removed, `remainingOfType` = that type still on the object). All-or-nothing. NeverInSemi.

```python
unity_skills.call_skill("component_remove_batch", items=[
    {"instanceId": 12345, "componentType": "BoxCollider"},
    {"instanceId": 12346, "componentType": "BoxCollider"}
])
```
