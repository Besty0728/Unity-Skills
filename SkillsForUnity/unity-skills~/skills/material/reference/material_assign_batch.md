### material_assign_batch
Assign materials to multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, material}]}` — each item echoes `gameObject` (the object name) and `material` (the assigned asset path); failures come back as `{error, target}`.

```python
unity_skills.call_skill("material_assign_batch", items=[
    {"name": "Cube1", "materialPath": "Assets/Materials/Red.mat"},
    {"name": "Cube2", "materialPath": "Assets/Materials/Blue.mat"}
])
```
