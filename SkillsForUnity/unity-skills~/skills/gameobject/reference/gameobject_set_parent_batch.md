### gameobject_set_parent_batch
Parent (or unparent) several GameObjects in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `childEntityId`, `childName`, `childInstanceId`, `childPath`, `parentEntityId`, `parentName`, `parentInstanceId`, `parentPath` (no parent field = unparent that child).

**Returns**: `{success, totalItems, successCount, failCount, results: [{target, entityId, success, parent}]}`; `Child object not found` / `Parent not found` fails the item and reverts the call.

```python
unity_skills.call_skill("gameobject_set_parent_batch", items=[
    {"childName": "Wheel1", "parentName": "Car"},
    {"childInstanceId": 12345, "parentName": "Car"},
    {"childPath": "Wheels/Wheel3", "parentPath": "Vehicles/Car"}
])
```
