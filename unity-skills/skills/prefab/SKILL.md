---
name: unity-prefab
description: "Create, instantiate, and manage prefabs. Use prefab_instantiate_batch for spawning 2+ instances."
---

# Unity Prefab Skills

> **BATCH-FIRST**: Use `prefab_instantiate_batch` when spawning 2+ prefab instances.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `prefab_instantiate` | `prefab_instantiate_batch` | Spawning 2+ instances |

**No batch needed**:
- `prefab_create` - Create prefab from scene object
- `prefab_apply` - Apply instance changes to prefab
- `prefab_unpack` - Unpack prefab instance

---

## Skills

### prefab_create
Create a prefab from a scene GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `gameObjectName` | string | No* | Source object name |
| `instanceId` | int | No* | Instance ID |
| `savePath` | string | Yes | Prefab save path |

**Returns**: `{success, prefabPath, sourceObject}`

### prefab_instantiate
Instantiate a prefab into the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Prefab asset path |
| `name` | string | No | prefab name | Instance name |
| `x`, `y`, `z` | float | No | 0 | Position |
| `parentName` | string | No | null | Parent object |

**Returns**: `{success, name, instanceId, prefabPath, position}`

### prefab_instantiate_batch
Instantiate multiple prefabs in one call.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `items` | array | Yes | Array of instantiation configs |

**Item properties**: `prefabPath`, `name`, `x`, `y`, `z`, `rotX`, `rotY`, `rotZ`, `scaleX`, `scaleY`, `scaleZ`, `parentName`

```python
unity_skills.call_skill("prefab_instantiate_batch", items=[
    {"prefabPath": "Assets/Prefabs/Enemy.prefab", "x": 0, "z": 0, "name": "Enemy_01"},
    {"prefabPath": "Assets/Prefabs/Enemy.prefab", "x": 2, "z": 0, "name": "Enemy_02"},
    {"prefabPath": "Assets/Prefabs/Enemy.prefab", "x": 4, "z": 0, "name": "Enemy_03"}
])
```

### prefab_apply
Apply instance changes back to the prefab asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `gameObjectName` | string | No* | Prefab instance name |
| `instanceId` | int | No* | Instance ID |

**Returns**: `{success, gameObject, prefabPath}`

### prefab_unpack
Unpack a prefab instance (break prefab connection).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `gameObjectName` | string | No* | - | Prefab instance name |
| `instanceId` | int | No* | - | Instance ID |
| `completely` | bool | No | false | Unpack all nested prefabs |

**Returns**: `{success, gameObject, mode}`

---

## Example: Efficient Enemy Spawning

```python
import unity_skills

# BAD: 10 API calls for 10 enemies
for i in range(10):
    unity_skills.call_skill("prefab_instantiate",
        prefabPath="Assets/Prefabs/Enemy.prefab",
        name=f"Enemy_{i}",
        x=i * 2
    )

# GOOD: 1 API call for 10 enemies
unity_skills.call_skill("prefab_instantiate_batch", items=[
    {"prefabPath": "Assets/Prefabs/Enemy.prefab", "name": f"Enemy_{i}", "x": i * 2}
    for i in range(10)
])
```

## Best Practices

1. Organize prefabs in dedicated folders
2. Use prefabs for repeated objects
3. Apply changes to update all instances
4. Unpack only when unique modifications needed
5. Use batch instantiation for level generation
