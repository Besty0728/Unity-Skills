---
name: unity-gameobject
description: Create, delete, find, and transform GameObjects in Unity Editor via REST API
---

# Unity GameObject Skills

Manipulate GameObjects in Unity scene - the fundamental building blocks of any Unity project.

## Capabilities

- Create primitives (Cube, Sphere, Capsule, Cylinder, Plane, Quad)
- Create empty GameObjects
- Delete GameObjects by name, path, or instanceId
- Find GameObjects with filters (name, tag, layer, component, regex)
- Set transform (position, rotation, scale)
- Set parent-child relationships
- Enable/disable GameObjects
- Get detailed GameObject information

## Skills Reference

| Skill | Description |
|-------|-------------|
| `gameobject_create` | Create a new GameObject |
| `gameobject_delete` | Delete a GameObject |
| `gameobject_find` | Find GameObjects with filters |
| `gameobject_set_transform` | Set position/rotation/scale |
| `gameobject_set_parent` | Set parent-child relationship |
| `gameobject_set_active` | Enable/disable GameObject |
| `gameobject_get_info` | Get detailed information |

## Parameters

### gameobject_create

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "GameObject" | Name for the new object |
| `primitiveType` | string | No | null | Cube/Sphere/Capsule/Cylinder/Plane/Quad |
| `x` | float | No | 0 | X position |
| `y` | float | No | 0 | Y position |
| `z` | float | No | 0 | Z position |
| `parentName` | string | No | null | Parent object name |

### gameobject_delete

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

*At least one identifier required

### gameobject_find

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | Name filter |
| `tag` | string | No | null | Tag filter |
| `layer` | int | No | -1 | Layer filter |
| `component` | string | No | null | Component type filter |
| `useRegex` | bool | No | false | Use regex for name |
| `limit` | int | No | 100 | Max results |

### gameobject_set_transform

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Object name |
| `posX/posY/posZ` | float | No | Position values |
| `rotX/rotY/rotZ` | float | No | Rotation values (euler) |
| `scaleX/scaleY/scaleZ` | float | No | Scale values |

## Example Usage

```python
import unity_skills

# Create a cube at position (0, 1, 0)
unity_skills.call_skill("gameobject_create", 
    name="Player", 
    primitiveType="Cube", 
    x=0, y=1, z=0
)

# Find all objects with "Enemy" in name
enemies = unity_skills.call_skill("gameobject_find", 
    name="Enemy", 
    useRegex=True
)

# Move object
unity_skills.call_skill("gameobject_set_transform",
    name="Player",
    posX=5, posY=1, posZ=3
)

# Set parent
unity_skills.call_skill("gameobject_set_parent",
    name="Weapon",
    parentName="Player"
)

# Delete object
unity_skills.call_skill("gameobject_delete", name="Player")
```

## Response Format

```json
{
  "status": "success",
  "skill": "gameobject_create",
  "result": {
    "success": true,
    "name": "Player",
    "instanceId": 12345,
    "path": "/Player",
    "position": {"x": 0, "y": 1, "z": 0}
  }
}
```

## Best Practices

1. Use descriptive names for easy identification
2. Organize with parent-child hierarchies
3. Use tags and layers for categorization
4. Query by instanceId for guaranteed uniqueness
5. Use regex find for batch operations
