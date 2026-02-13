---
name: unity-gameobject
description: "GameObject creation and manipulation. Use when users want to create, delete, move, rotate, scale, or parent GameObjects. Triggers: gameobject, create, delete, transform, position, rotation, scale, parent, hierarchy, 游戏对象, Unity创建, Unity删除, Unity移动, Unity旋转, Unity缩放."
---

# Unity GameObject Skills

> **BATCH-FIRST**: Use `*_batch` skills when operating on 2+ objects to reduce API calls from N to 1.

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
| `name` | string | No | "GameObject" | Object name |
| `primitiveType` | string | No | null | Cube/Sphere/Capsule/Cylinder/Plane/Quad (null=Empty) |
| `x`, `y`, `z` | float | No | 0 | Position |
| `parentName` | string | No | null | Parent object name |

**Returns**: `{success, name, instanceId, path, position}`

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
| `layer` | int | No | -1 | Layer filter |
| `component` | string | No | null | Component type filter |
| `useRegex` | bool | No | false | Use regex for name |
| `limit` | int | No | 100 | Max results |

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
Set position, rotation, and/or scale.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Object name |
| `posX/posY/posZ` | float | No | Position |
| `rotX/rotY/rotZ` | float | No | Rotation (euler) |
| `scaleX/scaleY/scaleZ` | float | No | Scale |

### gameobject_set_parent
Set parent-child relationship.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Child object name |
| `parentName` | string | Yes | Parent object name (empty string = unparent) |

### gameobject_set_active
Enable or disable a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Object name |
| `active` | bool | Yes | Enable state |

---

## Batch Skills

### gameobject_create_batch
Create multiple GameObjects in one call.

```python
unity_skills.call_skill("gameobject_create_batch", items=[
    {"name": "Cube1", "primitiveType": "Cube", "x": 0},
    {"name": "Cube2", "primitiveType": "Cube", "x": 2},
    {"name": "Cube3", "primitiveType": "Cube", "x": 4}
])
```

### gameobject_delete_batch
Delete multiple GameObjects.

```python
# By names
unity_skills.call_skill("gameobject_delete_batch", items=["Cube1", "Cube2", "Cube3"])

# By instanceId (preferred)
unity_skills.call_skill("gameobject_delete_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])
```

### gameobject_duplicate_batch
Duplicate multiple GameObjects.

```python
unity_skills.call_skill("gameobject_duplicate_batch", items=[
    {"instanceId": 12345},
    {"instanceId": 12346}
])
```

### gameobject_rename_batch
Rename multiple GameObjects.

```python
unity_skills.call_skill("gameobject_rename_batch", items=[
    {"instanceId": 12345, "newName": "Enemy_01"},
    {"instanceId": 12346, "newName": "Enemy_02"}
])
```

### gameobject_set_transform_batch
Set transforms for multiple objects.

```python
unity_skills.call_skill("gameobject_set_transform_batch", items=[
    {"name": "Cube1", "posX": 0, "posY": 1},
    {"name": "Cube2", "posX": 2, "posY": 1},
    {"name": "Cube3", "posX": 4, "posY": 1}
])
```

### gameobject_set_active_batch
Toggle multiple objects.

```python
unity_skills.call_skill("gameobject_set_active_batch", items=[
    {"name": "Enemy1", "active": False},
    {"name": "Enemy2", "active": False}
])
```

### gameobject_set_parent_batch
Parent multiple objects.

```python
unity_skills.call_skill("gameobject_set_parent_batch", items=[
    {"childName": "Wheel1", "parentName": "Car"},
    {"childName": "Wheel2", "parentName": "Car"}
])
```

### gameobject_set_layer_batch
Set layer for multiple objects.

```python
unity_skills.call_skill("gameobject_set_layer_batch", items=[
    {"name": "Enemy1", "layer": 8},
    {"name": "Enemy2", "layer": 8}
])
```

### gameobject_set_tag_batch
Set tag for multiple objects.

```python
unity_skills.call_skill("gameobject_set_tag_batch", items=[
    {"name": "Enemy1", "tag": "Enemy"},
    {"name": "Enemy2", "tag": "Enemy"}
])
```

---

## Example: Efficient Scene Setup

```python
import unity_skills

# BAD: 6 API calls
unity_skills.call_skill("gameobject_create", name="Floor", primitiveType="Plane")
unity_skills.call_skill("gameobject_create", name="Wall1", primitiveType="Cube")
unity_skills.call_skill("gameobject_create", name="Wall2", primitiveType="Cube")
unity_skills.call_skill("gameobject_set_transform", name="Wall1", posX=-5, scaleY=3)
unity_skills.call_skill("gameobject_set_transform", name="Wall2", posX=5, scaleY=3)
unity_skills.call_skill("gameobject_set_tag_batch", items=[{"name": "Wall1", "tag": "Wall"}, {"name": "Wall2", "tag": "Wall"}])

# GOOD: 3 API calls
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
