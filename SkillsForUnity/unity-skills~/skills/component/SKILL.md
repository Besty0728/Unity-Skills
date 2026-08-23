---
name: unity-component
description: Manage GameObject components
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Attaching or removing components
- Copying components between objects
- Toggling components
- Reading/writing serialized fields
- 挂载或移除组件、在对象间复制组件、开关组件、读写序列化字段

# Unity Component Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects to reduce API calls from N to 1.

## Operating Mode

- **Approval**：本模块 Mixed —— `component_list` / `component_get_properties` 标 `SkillMode.SemiAuto`，可直接执行；写类 skill (`component_add` / `component_set_property` / `component_set_enabled` / `component_copy` 等) 标 `SkillMode.FullAuto`，需 grant 单次执行返结果。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`component_remove` / `component_remove_batch`（Operation.Delete）。这些在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

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
| `component_set_serialized_property` | `component_set_serialized_property_batch` | Setting Inspector SerializedProperty paths |

**Other Skills** (no batch):
- `component_list` - List all components on an object
- `component_get_properties` - Get component property values
- `component_set_enabled` - Enable/disable a component (Behaviour, Renderer, Collider)
- `component_copy` - Copy a component from one object to another
- `component_get_serialized_properties` - List Inspector SerializedProperty paths
- `component_copy_exact` - Copy a component and verify serialized fields match

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

**Returns**: `{success, gameObject, instanceId, component, fullTypeName}` (returns `{warning, gameObject, instanceId}` instead if a single-instance component already exists)

### component_remove
Remove a component from a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | - | Instance ID |
| `path` | string | No* | - | Hierarchy path |
| `componentType` | string | Yes | - | Component type to remove |
| `componentIndex` | int | No | 0 | Index into the components of that type when 2+ exist on the same object |

**Returns**: `{success, gameObject, removed}` (`removed` is the requested `componentType` string)

### component_list
List all components on a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |

**Returns**: `{gameObject, instanceId, path, componentCount, components: [{type, fullType, enabled, keyProperties?}]}` (`keyProperties` only present when `includeProperties=true`)

### component_set_property
Set a component property value.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `propertyName` | string | Yes | C# property/field name — see the serialized-name note below |
| `value` | string | Cond. | New value (basic types, vectors, colors, enums) — wire format below |
| `referencePath` | string | No | Scene object hierarchy path (for scene references) |
| `referenceName` | string | No | Scene object name (for scene references) |
| `assetPath` | string | No | Project asset path (for asset references: Material, Texture, AudioClip, ScriptableObject, Prefab, etc.) |

**Which of the four value parameters to use** — exactly one carries the payload, and they are checked in this order: `assetPath` first, then `referencePath` / `referenceName`, and `value` only if neither reference form was supplied. So a `value` sent alongside an `assetPath` is silently ignored; never send two.

| Use | When the target field is | Example |
|---|---|---|
| `value` | a primitive, vector, colour, enum, `LayerMask`, `Rect`, `Bounds`, `Quaternion` | `value="2.5"`, `value="Interpolate"` |
| `referencePath` | a scene object, addressed by hierarchy path (unambiguous) | `referencePath="Root/Player/Hand"` |
| `referenceName` | a scene object, addressed by name (first match wins) | `referenceName="Player"` |
| `assetPath` | a project asset — Material, Texture, AudioClip, ScriptableObject, Prefab | `assetPath="Assets/Materials/Red.mat"` |

**Wire format of `value`** — multi-component values travel as a **comma-separated string**: `"1,2,3"` for a Vector3, `"1,0.5,0,1"` for an RGBA colour, `"0,90,0"` for a Quaternion (3 values = Euler, 4 = xyzw). The JSON object form (`{"x":1,"y":2,"z":3}` / `{"r":1,"g":0,"b":0,"a":1}`) is accepted **only** for `Vector2`/`Vector3`/`Vector4` and `Color`/`Color32`, and every component is **required**: `{"y":2}` for a Vector3 is rejected naming the missing `x, z` rather than silently zeroing them. The one optional key is a colour's `a`, which defaults to `1` — `r`, `g`, `b` are required. An explicit `null` counts as not supplied, so it fails the same way for a required key. An unrecognised key is rejected with the list of keys that were expected, and a non-numeric value is rejected naming the offending key. `Vector2Int`/`Vector3Int`, `Quaternion`, `Rect` and `Bounds` take the comma-separated string only — a JSON object for those fails. Colours additionally accept `#RRGGBB` / `#RRGGBBAA` hex and ten names: `red`, `green`, `blue`, `white`, `black`, `yellow`, `cyan`, `magenta`, `gray`/`grey`, `clear`. Enums take the member name, case-insensitively. Bools accept `true`/`1`/`yes`/`on`.

```python
# float / int / bool / string
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="mass", value=2.5)
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="useGravity", value=False)

# Vector3
call_skill("component_set_property", name="Obj", componentType="Transform", propertyName="localPosition",
           value="1,2,3")

# Color — comma string, hex, or a name
call_skill("component_set_property", name="Obj", componentType="Light", propertyName="color", value="1,0.5,0,1")
call_skill("component_set_property", name="Obj", componentType="Light", propertyName="color", value="#FF8000")

# Enum (member name)
call_skill("component_set_property", name="Obj", componentType="Rigidbody", propertyName="interpolation",
           value="Interpolate")
```

**Returns**: `{success, gameObject, component, property, valueSet, valueType}` (`valueSet` is the string form of the actual value applied; `valueType` is the resolved target type name)

### component_get_properties
Get all properties of a component.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `componentType` | string | Yes | Component type |

**Returns**: `{gameObject, component, fullTypeName, properties: [{name, type, fullType, value, canWrite}], fields: [{name, type, fullType, value, isSerializable}]}`

### component_get_serialized_properties
List Inspector serialized properties on a component via `SerializedObject`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `includeChildren` | bool | No | Include nested properties |
| `limit` | int | No | Max properties returned |

**Returns**: `{success, gameObject, component, fullTypeName, properties}`

> **The names here are not the names you write with.** This skill reads Unity's serialized backing fields, so a Rigidbody's kinematic flag comes back as `m_IsKinematic`, its mass as `m_Mass`. `component_set_property` / `component_get_properties` work on the **C# API names** instead — `isKinematic`, `mass`. To convert: drop the `m_` prefix and lowercase the first letter. Feed the `m_`-prefixed path back only to `component_set_serialized_property`, which expects a `propertyPath` verbatim. Writing `m_IsKinematic` through `component_set_property` fails with "Property/field not found" (and the error lists the available properties).

### component_set_serialized_property
Set an Inspector serialized property by `propertyPath`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type |
| `propertyPath` | string | Yes | SerializedProperty path, e.g. `items.Array.data[0]` |
| `value` | string | Cond. | Primitive/vector/color/enum value |
| `referenceName` | string | No | Scene object name for ObjectReference |
| `referenceInstanceId` | int | No | Scene object instance ID for ObjectReference |
| `referencePath` | string | No | Scene object path for ObjectReference |
| `assetPath` | string | No | Project asset path for ObjectReference |
| `objectType` | string | No | Expected object/component type for references |

> Provide `value` for scalar properties, or a scene/project reference for ObjectReference fields.

**Returns**: `{success, gameObject, component, propertyPath, valueSet}`

---

## Batch Skills

### component_add_batch
Add components to multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


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
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, componentType, removed}]}`

```python
unity_skills.call_skill("component_remove_batch", items=[
    {"instanceId": 12345, "componentType": "BoxCollider"},
    {"instanceId": 12346, "componentType": "BoxCollider"}
])
```

### component_set_property_batch
Set properties on multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, gameObject, componentType, property, oldValue, newValue}]}`

```python
unity_skills.call_skill("component_set_property_batch", items=[
    {"name": "Enemy1", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0},
    {"name": "Enemy2", "componentType": "Rigidbody", "propertyName": "mass", "value": 2.0}
])
```

### component_set_serialized_property_batch
Set Inspector serialized properties on multiple components.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects |

**Item properties**: `name`, `instanceId`, `path`, `componentType`, `propertyPath`, `value`, `referenceName`, `referenceInstanceId`, `referencePath`, `assetPath`, `objectType`

**Returns**: `{success, totalItems, successCount, failCount, results}`

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

### `component_copy_exact`
Copy a component from one GameObject to another and verify serialized Inspector fields match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceName` | string | No* | null | Source GameObject name |
| `sourceInstanceId` | int | No* | 0 | Source Instance ID |
| `sourcePath` | string | No* | null | Source hierarchy path |
| `targetName` | string | No* | null | Target GameObject name |
| `targetInstanceId` | int | No* | 0 | Target Instance ID |
| `targetPath` | string | No* | null | Target hierarchy path |
| `componentType` | string | Yes | - | Component type to copy |

**Returns:** `{ success, source, target, componentType, verified, mismatchCount, mismatches? }`

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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.

## Common Errors

Full transport-level codes (COMPILING/RATE_LIMIT etc.) → ../../references/protocol-error-codes.md

| Error | Trigger | Fix |
|---|---|---|
| `TARGET_NOT_FOUND` | The GameObject, component type, component instance, property/field, or asset reference could not be located. | List components with `component_list`, resolve asset paths with `asset_find`, or verify the object with `gameobject_find` / `scene_get_hierarchy`. |
| `MISSING_PARAM` | A required parameter is missing, such as `componentType` in `component_add` or `componentType`/`propertyName` in `component_set_property`. | Provide the missing parameter; use `mode=dryRun` to preview the required arguments. |
| `SEMANTIC_INVALID` | A value is out of range or otherwise invalid, such as a `componentIndex` that exceeds the available components. | Adjust the value to fit the valid range or enum described in the error message. |
| `SKILL_ERROR` | A runtime constraint blocked the operation, such as a read-only property or a component that cannot be removed because it is required. | Read the error message, resolve the underlying constraint (e.g., choose a writable property), then retry. |
