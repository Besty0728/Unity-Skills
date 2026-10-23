### material_set_colors_batch
Set colors on multiple objects. Each item accepts: identifier (`name`/`entityId`/`instanceId`/`path`) + `r`, `g`, `b`, `a`, optional per-item `propertyName` (same resolution rules as `material_set_color`; overrides the batch-level `propertyName`).
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name\|entityId\|instanceId\|path, r, g, b, a, propertyName?}` per-item objects (see example below) |
| `propertyName` | string | No | auto-detect | Default color property applied to all items unless overridden |


**Returns**: `{success, totalItems, successCount, failCount, results: [{target, success, propertyUsed, color}]}` — the per-item key is `target` (GameObject name, or the path when you addressed the asset), not `name`; a rejected item fails the whole batch and rolls back the others (`rolledBack:true`, `results[i].reverted:true`).

```python
unity_skills.call_skill("material_set_colors_batch", items=[
    {"name": "Cube1", "r": 1, "g": 0, "b": 0},
    {"name": "Cube2", "r": 0, "g": 1, "b": 0},
    {"name": "Cube3", "r": 0, "g": 0, "b": 1}
])
```
