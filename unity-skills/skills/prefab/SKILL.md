---
name: unity-prefab
description: Create, instantiate, and manage prefabs in Unity Editor via REST API
---

# Unity Prefab Skills

Work with prefabs - reusable GameObject templates for efficient scene building.

## Capabilities

- Create prefabs from scene objects
- Instantiate prefabs into scene
- Apply changes to prefab
- Unpack prefab instances

## Skills Reference

| Skill | Description |
|-------|-------------|
| `prefab_create` | Create prefab from GameObject |
| `prefab_instantiate` | Instantiate prefab in scene |
| `prefab_apply` | Apply instance changes to prefab |
| `prefab_unpack` | Unpack prefab instance |

## Parameters

### prefab_create

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `gameObjectName` | string | Yes | - | Source object name |
| `savePath` | string | Yes | - | Prefab save path |

### prefab_instantiate

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `prefabPath` | string | Yes | - | Prefab asset path |
| `name` | string | No | prefab name | Instance name |
| `x` | float | No | 0 | X position |
| `y` | float | No | 0 | Y position |
| `z` | float | No | 0 | Z position |
| `parentName` | string | No | null | Parent object |

### prefab_apply

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `gameObjectName` | string | Yes | Prefab instance name |

### prefab_unpack

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `gameObjectName` | string | Yes | - | Prefab instance name |
| `completely` | bool | No | false | Unpack all nested |

## Example Usage

```python
import unity_skills

# Create a complex object
unity_skills.call_skill("gameobject_create", name="EnemyTemplate", primitiveType="Cube")
unity_skills.call_skill("component_add", name="EnemyTemplate", componentType="Rigidbody")

# Save as prefab
unity_skills.call_skill("prefab_create",
    gameObjectName="EnemyTemplate",
    savePath="Assets/Prefabs/Enemy.prefab"
)

# Spawn multiple instances
for i in range(5):
    unity_skills.call_skill("prefab_instantiate",
        prefabPath="Assets/Prefabs/Enemy.prefab",
        name=f"Enemy_{i}",
        x=i * 2, y=0, z=0
    )

# Modify an instance and apply changes to prefab
unity_skills.call_skill("component_add", name="Enemy_0", componentType="AudioSource")
unity_skills.call_skill("prefab_apply", gameObjectName="Enemy_0")

# Unpack instance (break prefab connection)
unity_skills.call_skill("prefab_unpack",
    gameObjectName="Enemy_1",
    completely=True
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "prefab_create",
  "result": {
    "success": true,
    "prefabPath": "Assets/Prefabs/Enemy.prefab",
    "sourceObject": "EnemyTemplate"
  }
}
```

## Best Practices

1. Organize prefabs in dedicated folders
2. Use prefabs for repeated objects
3. Apply changes to update all instances
4. Unpack only when unique modifications needed
5. Nested prefabs for complex hierarchies
