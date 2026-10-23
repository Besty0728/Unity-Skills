### material_create_batch
Create multiple materials.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, path}]}`

```python
unity_skills.call_skill("material_create_batch", items=[
    {"name": "Red", "savePath": "Assets/Materials"},
    {"name": "Blue", "savePath": "Assets/Materials"},
    {"name": "Green", "savePath": "Assets/Materials"}
])
```

Batch-first: one `material_create_batch` + `material_set_colors_batch` + `material_assign_batch` replaces six single calls.

```python
import unity_skills

# BAD: 6 API calls
unity_skills.call_skill("material_create", name="Mat1", savePath="Assets/Materials")
unity_skills.call_skill("material_create", name="Mat2", savePath="Assets/Materials")
unity_skills.call_skill("material_set_color", path="Assets/Materials/Mat1.mat", r=1, g=0, b=0)
unity_skills.call_skill("material_set_color", path="Assets/Materials/Mat2.mat", r=0, g=0, b=1)
unity_skills.call_skill("material_assign", name="Cube1", materialPath="Assets/Materials/Mat1.mat")
unity_skills.call_skill("material_assign", name="Cube2", materialPath="Assets/Materials/Mat2.mat")

# GOOD: 3 API calls
unity_skills.call_skill("material_create_batch", items=[
    {"name": "Mat1", "savePath": "Assets/Materials"},
    {"name": "Mat2", "savePath": "Assets/Materials"}
])
unity_skills.call_skill("material_set_colors_batch", items=[
    {"path": "Assets/Materials/Mat1.mat", "r": 1, "g": 0, "b": 0},
    {"path": "Assets/Materials/Mat2.mat", "r": 0, "g": 0, "b": 1}
])
unity_skills.call_skill("material_assign_batch", items=[
    {"name": "Cube1", "materialPath": "Assets/Materials/Mat1.mat"},
    {"name": "Cube2", "materialPath": "Assets/Materials/Mat2.mat"}
])
```
