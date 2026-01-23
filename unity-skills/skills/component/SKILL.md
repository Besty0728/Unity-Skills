---
name: unity-component
description: Add, remove, and configure components on GameObjects in Unity Editor via REST API
---

# Unity Component Skills

Manage components on GameObjects - add behaviors, physics, rendering, and more.

## Capabilities

- Add any Unity component by type name
- Remove components from GameObjects
- List all components on an object
- Get component properties
- Get component properties
- Set component property values
- **Batch Operations**: Efficiently add, remove, and configure components on multiple objects.

## Skills Reference

| Skill | Description |
|-------|-------------|
| `component_add` | Add a component to GameObject |
| `component_remove` | Remove a component |
| `component_list` | List all components |
| `component_set_property` | Set component property |
| `component_get_properties` | Get all properties |
| `component_add_batch` | Add components to multiple objects |
| `component_set_property_batch` | Set property for multiple objects |
| `component_remove_batch` | Remove components from multiple objects |

## Parameters

### component_add

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `componentType` | string | Yes | Component type (e.g., "Rigidbody") |

### component_remove

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `componentType` | string | Yes | Component type to remove |

### component_list

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |

### component_set_property

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `componentType` | string | Yes | Component type |
| `propertyName` | string | Yes | Property to set |
| `value` | any | Yes | New value |

### component_get_properties

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | GameObject name |
| `componentType` | string | Yes | Component type |

### Batch Operations
Batch skills take a single `items` parameter which is a JSON array of objects.

| Skill | Item Properties |
|-------|-----------------|
| `component_add_batch` | `name`, `componentType` |
| `component_set_property_batch` | `name`, `componentType`, `propertyName`, `value` |
| `component_remove_batch` | `name`, `componentType` |

## Common Component Types

**Physics:**
- `Rigidbody` - Physics simulation
- `BoxCollider` - Box collision
- `SphereCollider` - Sphere collision
- `MeshCollider` - Mesh-based collision
- `CharacterController` - Character movement

**Rendering:**
- `MeshRenderer` - Render meshes
- `SkinnedMeshRenderer` - Animated meshes
- `SpriteRenderer` - 2D sprites
- `LineRenderer` - Draw lines
- `TrailRenderer` - Motion trails

**Audio:**
- `AudioSource` - Play sounds
- `AudioListener` - Receive audio

**UI:**
- `Canvas` - UI container
- `Image` - UI images
- `Text` - UI text (legacy)
- `Button` - Clickable button

## Example Usage

```python
import unity_skills

# Add Rigidbody for physics
unity_skills.call_skill("component_add",
    name="Player",
    componentType="Rigidbody"
)

# Configure the Rigidbody
unity_skills.call_skill("component_set_property",
    name="Player",
    componentType="Rigidbody",
    propertyName="mass",
    value=2.0
)

# Add a collider
unity_skills.call_skill("component_add",
    name="Player",
    componentType="BoxCollider"
)

# List all components
components = unity_skills.call_skill("component_list",
    name="Player"
)

# Get Rigidbody properties
props = unity_skills.call_skill("component_get_properties",
    name="Player",
    componentType="Rigidbody"
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "component_list",
  "result": {
    "success": true,
    "gameObject": "Player",
    "components": [
      "Transform",
      "MeshFilter",
      "MeshRenderer",
      "BoxCollider",
      "Rigidbody"
    ]
  }
}
```

## Best Practices

1. Add colliders before Rigidbody for physics
2. Use component_list to verify additions
3. Check property names with component_get_properties first
4. Some properties are read-only (will fail to set)
5. Use full type names for custom scripts (e.g., "MyNamespace.MyScript")
