### material_set_emission_batch
Set emission on multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, target, emissionColor, intensity, hdrColor, emissionEnabled}]}` — a rejected item fails the whole batch and rolls back the others (`rolledBack:true`).

```python
unity_skills.call_skill("material_set_emission_batch", items=[
    {"name": "Neon1", "r": 1, "g": 0, "b": 1, "intensity": 5.0},
    {"name": "Neon2", "r": 0, "g": 1, "b": 1, "intensity": 5.0}
])
```
