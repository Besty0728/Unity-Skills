### gameobject_delete_batch
Delete several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array: plain name strings, or objects |

**Item properties**: `entityId`, `name`, `instanceId`, `path` (at least one per object item).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success}]}`; a missing object gives `{error: "Object not found", target}` and, being all-or-nothing, reverts the deletions of the other items. NeverInSemi.

```python
# By names
unity_skills.call_skill("gameobject_delete_batch", items=["Cube1", "Cube2", "Cube3"])

# By instanceId (preferred for precision)
unity_skills.call_skill("gameobject_delete_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])

# By path
unity_skills.call_skill("gameobject_delete_batch", items=[
    {"path": "Environment/Cube1"},
    {"path": "Environment/Cube2"}
])
```
