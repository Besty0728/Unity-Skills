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
- **Batch Operations**: Efficiently create, delete, transform, and configure multiple objects in a single call.

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
| `gameobject_duplicate` | Duplicate a single GameObject (returns copyName, copyInstanceId) |
| `gameobject_create_batch` | Create multiple GameObjects (Efficient) |
| `gameobject_delete_batch` | Delete multiple GameObjects (Efficient) |
| `gameobject_duplicate_batch` | **Duplicate multiple GameObjects (Efficient, NEW)** |
| `gameobject_set_active_batch` | Set active state for multiple objects |
| `gameobject_set_transform_batch` | Set transform for multiple objects |
| `gameobject_set_layer_batch` | Set layer for multiple objects |
| `gameobject_set_tag_batch` | Set tag for multiple objects |
| `gameobject_set_parent_batch` | Set parent for multiple objects |

> ⚠️ **IMPORTANT**: When operating on multiple objects, ALWAYS use `*_batch` skills instead of calling single-object skills in a loop. This reduces API calls from N to 1!

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

### gameobject_duplicate / gameobject_duplicate_batch

**Single**: Returns `{originalName, copyName, copyInstanceId, copyPath}`

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (from editor_get_context) |
| `path` | string | No* | Hierarchy path |

**Batch**: `items` = JSON array of `{name, instanceId, path}`
```json
[{"instanceId": 12345}, {"instanceId": 12346}, {"name": "Cube"}]
```

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

### Batch Operations (Use these for multiple objects!)

> 🚀 **Performance Tip**: Use batch skills to reduce 30 API calls to just 1!

| Skill | Item Properties |
|-------|-----------------|
| `gameobject_create_batch` | `name`, `primitiveType`, `x`, `y`, `z`, `rotX`, `scaleX`, etc. |
| `gameobject_delete_batch` | `name` OR `{name, instanceId}` |
| `gameobject_duplicate_batch` | `name`, `instanceId`, or `path` |
| `gameobject_set_active_batch` | `name`, `active` |
| `gameobject_set_transform_batch` | `name` or `instanceId`, `posX`, `rotY`, `scaleZ`, etc. |
| `gameobject_set_layer_batch` | `name`, `layer`, `recursive` |
| `gameobject_set_tag_batch` | `name`, `tag` |
| `gameobject_set_parent_batch` | `childName`, `parentName` |

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
