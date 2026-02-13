---
name: unity-component
description: "GameObject component management. Use when users want to add, remove, or configure components like Rigidbody, Collider, AudioSource. Triggers: component, add component, rigidbody, collider, audio source, script, 组件, 添加组件, 刚体, 碰撞体."
---

# Unity Component Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects to reduce API calls from N to 1.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `component_add` | `component_add_batch` | Adding to 2+ objects |
| `component_remove` | `component_remove_batch` | Removing from 2+ objects |
| `component_set_property` | `component_set_property_batch` | Setting on 2+ objects |

**Query Skills** (no batch needed):
- `component_list` - List all components on an object
- `component_get_properties` - Get component property values

---

## Single-Object Skills

### component_add
Add a component to a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type name |

*At least one identifier required

**Returns**: `{success, gameObject, componentType, added}`

### component_remove
Remove a component from a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `componentType` | string | Yes | Component type to remove |

**Returns**: `{success, gameObject, componentType, removed}`

### component_list
List all components on a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |

**Returns**: `{success, gameObject, instanceId, components: [string]}`

### component_set_property
Set a component property value.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `componentType` | string | Yes | Component type |
| `propertyName` | string | Yes | Property to set |
| `value` | any | Yes | New value |

**Returns**: `{success, gameObject, componentType, property, oldValue, newValue}`

### component_get_properties
Get all properties of a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `componentType` | string | Yes | Component type |

**Returns**: `{success, gameObject, componentType, properties: {name: value}}`

---

## Batch Skills

### component_add_batch
Add components to multiple objects.

```python
unity_skills.call_skill("component_add_batch", items=[
    {"name": "Enemy1", "componentType": "Rigidbody"},
    {"name": "Enemy2", "componentType": "Rigidbody"},
    {"name": "Enemy3", "componentType": "Rigidbody"}
])
```

### component_remove_batch
Remove components from multiple objects.

```python
unity_skills.call_skill("component_remove_batch", items=[
    {"instanceId": 12345, "componentType": "BoxCollider"},
    {"instanceId": 12346, "componentType": "BoxCollider"}
])
```

### component_set_property_batch
Set properties on multiple objects.

```python
unity_skills.call_skill("component_set_property_batch", items=[
    {"name": "Enemy1", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Enemy2", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0}
])
```

---

## Common Component Types

### Physics
| Type | Description |
|------|-------------|
| `Rigidbody` | Physics simulation |
| `BoxCollider` | Box collision |
| `SphereCollider` | Sphere collision |
| `CapsuleCollider` | Capsule collision |
| `MeshCollider` | Mesh-based collision |
| `CharacterController` | Character movement |

### Rendering
| Type | Description |
|------|-------------|
| `MeshRenderer` | Render meshes |
| `SkinnedMeshRenderer` | Animated meshes |
| `SpriteRenderer` | 2D sprites |
| `LineRenderer` | Draw lines |
| `TrailRenderer` | Motion trails |

### Audio
| Type | Description |
|------|-------------|
| `AudioSource` | Play sounds |
| `AudioListener` | Receive audio |

### UI
| Type | Description |
|------|-------------|
| `Canvas` | UI container |
| `Image` | UI images |
| `Text` | UI text (legacy) |
| `Button` | Clickable button |

---

## Example: Efficient Physics Setup

```python
import unity_skills

# BAD: 6 API calls
unity_skills.call_skill("component_add", name="Box1", componentType="Rigidbody")
unity_skills.call_skill("component_add", name="Box2", componentType="Rigidbody")
unity_skills.call_skill("component_add", name="Box3", componentType="Rigidbody")
unity_skills.call_skill("component_set_property", name="Box1", componentType="Rigidbody", propertyName="mass", value=2.0)
unity_skills.call_skill("component_set_property", name="Box2", componentType="Rigidbody", propertyName="mass", value=2.0)
unity_skills.call_skill("component_set_property", name="Box3", componentType="Rigidbody", propertyName="mass", value=2.0)

# GOOD: 2 API calls
unity_skills.call_skill("component_add_batch", items=[
    {"name": "Box1", "componentType": "Rigidbody"},
    {"name": "Box2", "componentType": "Rigidbody"},
    {"name": "Box3", "componentType": "Rigidbody"}
])
unity_skills.call_skill("component_set_property_batch", items=[
    {"name": "Box1", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Box2", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Box3", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0}
])
```

## Best Practices

1. Add colliders before Rigidbody for physics
2. Use `component_list` to verify additions
3. Check property names with `component_get_properties` first
4. Some properties are read-only (will fail to set)
5. Use full type names for custom scripts (e.g., "MyNamespace.MyScript")
