---
name: unity-gameobject
description: "GameObject creation and manipulation. Use when users want to create, delete, move, rotate, scale, or parent GameObjects. Triggers: gameobject, create, delete, transform, position, rotation, scale, parent, hierarchy, 游戏对象, Unity创建, Unity删除, Unity移动, Unity旋转, Unity缩放."
---

# Unity GameObject Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects to reduce API calls from N to 1.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `gameobject_move` / `gameobject_rotate` / `gameobject_set_scale` do not exist → use `gameobject_set_transform` (handles position, rotation, and scale together)
- `gameobject_set_position` does not exist → use `gameobject_set_transform` with `posX/posY/posZ`
- `gameobject_add_component` does not exist → use `component_add` (component module)
- `gameobject_get_transform` does not exist → use `gameobject_get_info` (returns position/rotation/scale)

**Routing**:
- To add/remove components → use `component` module
- To set material/color → use `material` module
- To search objects by name/tag/component → `gameobject_find` (this module) or `scene_find_objects` (scene module, Semi-Auto)

> **Object Targeting**: All single-object skills accept three identifiers: `name` (string), `instanceId` (int, preferred for precision), `path` (string, hierarchy path like "Parent/Child"). Provide at least one. When only `name` is shown in a parameter table, `instanceId` and `path` are also accepted.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `gameobject_create` | `gameobject_create_batch` | Creating 2+ objects |
| `gameobject_delete` | `gameobject_delete_batch` | Deleting 2+ objects |
| `gameobject_duplicate` | `gameobject_duplicate_batch` | Duplicating 2+ objects |
| `gameobject_rename` | `gameobject_rename_batch` | Renaming 2+ objects |
| `gameobject_set_transform` | `gameobject_set_transform_batch` | Moving 2+ objects |
| `gameobject_set_active` | `gameobject_set_active_batch` | Toggling 2+ objects |
| `gameobject_set_parent` | `gameobject_set_parent_batch` | Parenting 2+ objects |
| - | `gameobject_set_layer_batch` | Setting layer on 2+ objects |
| - | `gameobject_set_tag_batch` | Setting tag on 2+ objects |

**Query Skills** (no batch needed):
- `gameobject_find` - Find objects by name/tag/layer/component
- `gameobject_get_info` - Get detailed object information

---

## Single-Object Skills

### gameobject_create
Create a new GameObject (primitive or empty).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | Yes | - | Object name |
| `primitiveType` | string | No | null | Cube/Sphere/Capsule/Cylinder/Plane/Quad (null=Empty) |
| `x`, `y`, `z` | float | No | 0 | Local position (relative to parent if set) |
| `parentName` | string | No | null | Parent object name |
| `parentInstanceId` | int | No | 0 | Parent instance ID |
| `parentPath` | string | No | null | Parent hierarchy path |

**Returns**: `{success, name, instanceId, path, parent, position}`

### gameobject_delete
Delete a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |

*At least one identifier required

### gameobject_duplicate
Duplicate a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

**Returns**: `{originalName, copyName, copyInstanceId, copyPath}`

### gameobject_rename
Rename a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Current object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `newName` | string | Yes | New name |

**Returns**: `{success, oldName, newName, instanceId}`

### gameobject_find
Find GameObjects matching criteria.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | Name filter |
| `tag` | string | No | null | Tag filter |
| `layer` | string | No | null | Layer filter |
| `component` | string | No | null | Component type filter |
| `useRegex` | bool | No | false | Use regex for name |
| `limit` | int | No | 50 | Max results |

**Returns**: `{count, objects: [{name, instanceId, path, tag, layer}]}`

### gameobject_get_info
Get detailed GameObject information.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

**Returns**: `{name, instanceId, path, tag, layer, active, position, rotation, scale, components, children}`

### gameobject_set_transform
Set position, rotation, and/or scale. Supports world / local / RectTransform spaces.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |
| `posX/posY/posZ` | float | No | World position |
| `rotX/rotY/rotZ` | float | No | World rotation (euler) |
| `scaleX/scaleY/scaleZ` | float | No | Local scale |
| `localPosX/localPosY/localPosZ` | float | No | Local position (relative to parent; works for both 3D and UI) |
| `anchoredPosX/anchoredPosY` | float | No | RectTransform anchored position (UI only) |
| `anchorMinX/anchorMinY` | float | No | RectTransform anchor min (0-1, UI only) |
| `anchorMaxX/anchorMaxY` | float | No | RectTransform anchor max (0-1, UI only) |
| `pivotX/pivotY` | float | No | RectTransform pivot (0-1, UI only) |
| `sizeDeltaX/sizeDeltaY` | float | No | RectTransform size delta (UI only) |
| `width/height` | float | No | Convenience aliases for sizeDeltaX/sizeDeltaY (UI only) |

*At least one identifier required. RectTransform / `anchored*` / `anchor*` / `pivot*` / `sizeDelta*` / `width` / `height` only apply to UI elements; ignored on regular Transforms.

### gameobject_set_parent
Set parent-child relationship.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `childName` | string | No* | Child object name |
| `childInstanceId` | int | No* | Child Instance ID (preferred) |
| `childPath` | string | No* | Child hierarchy path |
| `parentName` | string | No* | Parent object name (empty string = unparent) |
| `parentInstanceId` | int | No* | Parent Instance ID |
| `parentPath` | string | No* | Parent hierarchy path |

*At least one child identifier and one parent identifier required

### gameobject_set_active
Enable or disable a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |
| `active` | bool | Yes | Enable state |

*At least one identifier required

---

## Batch Skills

### gameobject_create_batch
Create multiple GameObjects in one call.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Item properties**: `name`, `primitiveType`, `x`, `y`, `z`, `rotX`, `rotY`, `rotZ`, `scaleX`, `scaleY`, `scaleZ`, `parentName`, `parentInstanceId`, `parentPath`

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, instanceId, path, position}]}`

```python
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "Parent", "primitiveType": "Empty"},
    {"name": "Child1", "primitiveType": "Cube", "x": 0, "parentName": "Parent"},
    {"name": "Child2", "primitiveType": "Sphere", "x": 2, "parentName": "Parent"}
])
```

### gameobject_delete_batch
Delete multiple GameObjects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name}]}`

```python
# By names
unity_skills.call_skill("gameobject_delete_batch", items=["Cube1", "Cube2", "Cube3"])

# By instanceId (preferred for precision)
unity_skills.call_skill("gameobject_delete_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])

# By path
unity_skills.call_skill("gameobject_delete_batch", items=[
    {"path": "Environment/Cube1"},
    {"path": "Environment/Cube2"}
])
```

### gameobject_duplicate_batch
Duplicate multiple GameObjects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, originalName, copyName, copyInstanceId, copyPath}]}`

```python
unity_skills.call_skill("gameobject_duplicate_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])
```

### gameobject_rename_batch
Rename multiple GameObjects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, oldName, newName, instanceId}]}`

```python
unity_skills.call_skill("gameobject_rename_batch", items=[
    {"instanceId": 12345, "newName": "Enemy_01"},
    {"instanceId": 12346, "newName": "Enemy_02"}
])
```

### gameobject_set_transform_batch
Set transforms for multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, position, rotation, scale}]}`

```python
unity_skills.call_skill("gameobject_set_transform_batch", items=[
    {"name": "Cube1", "posX": 0, "posY": 1},
    {"instanceId": 12345, "posX": 2, "posY": 1},
    {"path": "Env/Cube3", "posX": 4, "posY": 1}
])
```

### gameobject_set_active_batch
Toggle multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, active}]}`

```python
unity_skills.call_skill("gameobject_set_active_batch", items=[
    {"name": "Enemy1", "active": False},
    {"name": "Enemy2", "active": False}
])
```

### gameobject_set_parent_batch
Parent multiple objects. Each item supports `childName`/`childInstanceId`/`childPath` and `parentName`/`parentInstanceId`/`parentPath`.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, child, parent}]}`

```python
unity_skills.call_skill("gameobject_set_parent_batch", items=[
    {"childName": "Wheel1", "parentName": "Car"},
    {"childInstanceId": 12345, "parentName": "Car"},
    {"childPath": "Wheels/Wheel3", "parentPath": "Vehicles/Car"}
])
```

### gameobject_set_layer_batch
Set layer for multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, layer}]}`

```python
unity_skills.call_skill("gameobject_set_layer_batch", items=[
    {"name": "Enemy1", "layer": "Water"},
    {"name": "Enemy2", "layer": "Water"}
])
```

### gameobject_set_tag_batch
Set tag for multiple objects.
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item objects (see example below) |


**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, tag}]}`

```python
unity_skills.call_skill("gameobject_set_tag_batch", items=[
    {"name": "Enemy1", "tag": "Enemy"},
    {"name": "Enemy2", "tag": "Enemy"}
])
```

---

## Minimal Example

```python
import unity_skills

# GOOD: 3 API calls instead of 6
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "Floor", "primitiveType": "Plane"},
    {"name": "Wall1", "primitiveType": "Cube"},
    {"name": "Wall2", "primitiveType": "Cube"}
])
unity_skills.call_skill("gameobject_set_transform_batch", items=[
    {"name": "Wall1", "posX": -5, "scaleY": 3},
    {"name": "Wall2", "posX": 5, "scaleY": 3}
])
unity_skills.call_skill("gameobject_set_tag_batch", items=[
    {"name": "Wall1", "tag": "Wall"},
    {"name": "Wall2", "tag": "Wall"}
])
```

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.