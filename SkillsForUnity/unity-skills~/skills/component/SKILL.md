---
name: unity-component
description: "GameObject component management. Use when users want to add, remove, or configure components like Rigidbody, Collider, AudioSource. Triggers: component, add component, rigidbody, collider, audio source, script, 组件, 添加组件, 刚体, 碰撞体."
---

# Unity Component Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects to reduce API calls from N to 1.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `component_create` / `component_get` do not exist → use `component_add` (add) and `component_get_properties` (read)
- `component_find` does not exist → use `component_list` to list components on an object
- `componentType` is case-sensitive — `Rigidbody` not `rigidbody`, `BoxCollider` not `boxcollider`
- Custom scripts need exact class name; if namespaced, use `Namespace.ClassName`

**Routing**:
- To create a C# component script → use `script` module's `script_create` first, then `component_add`
- To set multiple properties at once → use `component_set_property_batch`
- To enable/disable a component → `component_set_enabled` (not `component_set_property`)

> **Object Targeting**: All single-object skills accept `name` (string), `instanceId` (int, preferred), and `path` (string, hierarchy path). Provide at least one.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `component_add` | `component_add_batch` | Adding to 2+ objects |
| `component_remove` | `component_remove_batch` | Removing from 2+ objects |
| `component_set_property` | `component_set_property_batch` | Setting on 2+ objects |

**Other Skills** (no batch):
- `component_list` - List all components on an object
- `component_get_properties` - Get component property values
- `component_set_enabled` - Enable/disable a component (Behaviour, Renderer, Collider)
- `component_copy` - Copy a component from one object to another

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
| `value` | any | Cond. | New value (for basic types, vectors, colors) |
| `referencePath` | string | No | Scene object hierarchy path (for scene references) |
| `referenceName` | string | No | Scene object name (for scene references) |
| `assetPath` | string | No | Project asset path (for asset references: Material, Texture, AudioClip, ScriptableObject, Prefab, etc.) |

> Provide one of: `value` (basic types), `referencePath`/`referenceName` (scene objects), or `assetPath` (project assets).

**`value` type examples**:
```python
# float / int / bool / string
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="mass", value=2.5)
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="useGravity", value=False)

# Vector3 (JSON object with x, y, z)
call_skill("component_set_property", name="Obj", componentType="Transform", propertyName="localPosition",
           value={"x": 1, "y": 2, "z": 3})

# Color (JSON object with r, g, b, a — values 0-1)
call_skill("component_set_property", name="Obj", componentType="Light", propertyName="color",
           value={"r": 1, "g": 0.5, "b": 0, "a": 1})

# Enum (use string name)
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="interpolation",
           value="Interpolate")
```

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

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, componentType, added}]}`

```python
unity_skills.call_skill("component_add_batch", items=[
    {"name": "Enemy1", "componentType": "Rigidbody"},
    {"name": "Enemy2", "componentType": "Rigidbody"},
    {"name": "Enemy3", "componentType": "Rigidbody"}
])
```

### component_remove_batch
Remove components from multiple objects.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, componentType, removed}]}`

```python
unity_skills.call_skill("component_remove_batch", items=[
    {"instanceId": 12345, "componentType": "BoxCollider"},
    {"instanceId": 12346, "componentType": "BoxCollider"}
])
```

### component_set_property_batch
Set properties on multiple objects.

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, componentType, property, oldValue, newValue}]}`

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

---

## Additional Skills

### `component_copy`
Copy a component from one GameObject to another.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceName` | string | No* | null | Source GameObject name |
| `sourceInstanceId` | int | No* | 0 | Source Instance ID |
| `sourcePath` | string | No* | null | Source hierarchy path |
| `targetName` | string | No* | null | Target GameObject name |
| `targetInstanceId` | int | No* | 0 | Target Instance ID |
| `targetPath` | string | No* | null | Target hierarchy path |
| `componentType` | string | Yes | - | Component type to copy |

*At least one source identifier and one target identifier required

**Returns:** `{ success, source, target, componentType }`

### `component_set_enabled`
Enable or disable a component (Behaviour, Renderer, Collider, etc.).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `componentType` | string | Yes | - | Component type to enable/disable |
| `enabled` | bool | No | true | Whether to enable or disable |

*At least one identifier required

**Returns:** `{ success, gameObject, componentType, enabled }`
