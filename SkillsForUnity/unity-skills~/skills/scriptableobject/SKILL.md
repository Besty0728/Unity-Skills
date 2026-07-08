---
name: unity-scriptableobject
description: Manage ScriptableObject assets — create, read, modify, delete, duplicate, find, and list SO assets. Use when creating or editing ScriptableObject data assets, finding SO instances, or scripting SO management, even if the user just says "配置表" or "SO资产". 管理 ScriptableObject 资产(创建、读取、修改、删除、复制、查找、列出 SO 资产);当用户要创建或编辑 ScriptableObject 数据资产、查找 SO 实例、或脚本化管理 SO 时使用。
---

# ScriptableObject Skills

Create and manage ScriptableObject assets.

## Operating Mode

- **Approval**：本模块 Mixed —— `scriptableobject_get` / `scriptableobject_get_serialized_properties` / `scriptableobject_list_types` / `scriptableobject_find` / `scriptableobject_export_json` 标 `SkillMode.SemiAuto`，可直接执行；写类 skill (`scriptableobject_create` / `scriptableobject_set` / `scriptableobject_set_batch` / `scriptableobject_set_serialized_property` / `scriptableobject_set_serialized_property_batch` / `scriptableobject_duplicate` / `scriptableobject_import_json`) 标 `SkillMode.FullAuto`，需 grant 单次执行返结果。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`scriptableobject_delete`（Operation.Delete）。该 skill 在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

**DO NOT** (common hallucinations):
- `scriptableobject_create_type` does not exist → create SO scripts via `script_create` with template "ScriptableObject"
- `scriptableobject_get_properties` / `scriptableobject_read` do not exist → use `scriptableobject_get` (reflection view) or `scriptableobject_get_serialized_properties` (Inspector/serialized view)
- `scriptableobject_set_property` / `scriptableobject_set_field` do not exist → use `scriptableobject_set` (top-level public field) or `scriptableobject_set_serialized_property` (nested/array/reference/private)
- `scriptableobject_save` does not exist → changes are auto-saved to the asset

**Routing**:
- For ScriptableObject script creation → use `script` module with template "ScriptableObject"
- For JSON import/export → `scriptableobject_import_json` / `scriptableobject_export_json` (this module)
- Choosing a setter: `scriptableobject_set` / `scriptableobject_set_batch` only reach top-level public simple fields (reflection). For nested paths, arrays/lists, object references, private `[SerializeField]` fields, gradients, curves and `[Flags]` enums → `scriptableobject_set_serialized_property` (+ `_batch`).

## Skills

### `scriptableobject_create`
Create a new ScriptableObject asset.
**Parameters:**
- `typeName` (string): ScriptableObject type name.
- `savePath` (string): Asset save path.

### `scriptableobject_get`
Get properties of a ScriptableObject.
**Parameters:**
- `assetPath` (string): Asset path.

### `scriptableobject_set`
Set a top-level public field/property on a ScriptableObject via reflection. For nested paths, arrays, object references or private `[SerializeField]` fields use `scriptableobject_set_serialized_property`.
**Parameters:**
- `assetPath` (string): Asset path.
- `fieldName` (string): Field or property name.
- `value` (string): Value to set.

### `scriptableobject_list_types`
List available ScriptableObject types in the project.
**Parameters:**
- `filter` (string, optional): Filter by name.

### `scriptableobject_duplicate`
Duplicate a ScriptableObject asset.
**Parameters:**
- `assetPath` (string): Source asset path to duplicate.

### `scriptableobject_set_batch`
Set multiple fields on a ScriptableObject at once. fields: JSON object {fieldName: value, ...}

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the ScriptableObject |
| fields | string | Yes | - | JSON object with field-value pairs, e.g. `{"fieldName": "value", ...}` |

**Returns:** `{ success, fieldsSet }`

### `scriptableobject_get_serialized_properties`
List Inspector serialized properties of a ScriptableObject asset — `propertyPath`, type, and current value for every serialized field, including private `[SerializeField]` and nested/array children. Use the returned `propertyPath` values with `scriptableobject_set_serialized_property`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the ScriptableObject |
| includeChildren | bool | No | `true` | Recurse into nested/array child properties |
| limit | int | No | `200` | Maximum number of properties to return |

**Returns:** `{ success, path, typeName, properties: [{ propertyPath, name, displayName, propertyType, type, isArray, editable, value }] }`

### `scriptableobject_set_serialized_property`
Set a single Inspector serialized property by `propertyPath`. Works where `scriptableobject_set` cannot: nested fields, arrays/lists, `UnityEngine.Object` references, private `[SerializeField]` fields, gradients, animation curves, and `[Flags]` enums.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the target ScriptableObject |
| propertyPath | string | Yes | - | Serialized property path (see syntax below); `speed` auto-falls back to `m_Speed` / `_speed` |
| value | string | No | `null` | Value to set (format depends on property type) |
| valueAssetPath | string | No | `null` | For ObjectReference properties: asset path of the asset to reference |
| valueObjectType | string | No | `null` | Optional type filter for the referenced asset (e.g. `"Sprite"`, useful for sub-assets) |

**propertyPath syntax:**
- Top-level field: `speed`
- Nested field: `stats.maxHp`
- Array/List element: `items.Array.data[2]`
- Array/List resize: `items.Array.size` with `value: "5"` (resize first, then set elements)

**Value formats:**
- Primitives: `"3.5"` / `"true"` / `"hello"`; Vector: `"1,2,3"`; Color: `"1,0,0,1"`; Enum: index or name
- `[Flags]` enum: comma-separated names `"Fire,Ice"` (also `|`), or a raw bitmask number (`"3"`, `"-1"` = Everything)
- ObjectReference: pass `valueAssetPath: "Assets/Icons/sword.png"` (optionally `valueObjectType`); clear with `value: "null"`
- Gradient: `{"colorKeys":[{"color":"1,0,0,1","time":0},{"color":"0,0,1,1","time":1}],"alphaKeys":[{"alpha":1,"time":0},{"alpha":1,"time":1}],"mode":"Blend"}`
- AnimationCurve: `{"keys":[{"time":0,"value":0},{"time":1,"value":1,"inTangent":2,"outTangent":2}],"preWrapMode":"ClampForever","postWrapMode":"Loop"}`

**Returns:** `{ success, assetPath, propertyPath, valueSet }`. Unknown propertyPath returns `error` plus `availableProperties` (first 60) for self-correction.

### `scriptableobject_set_serialized_property_batch`
Set multiple Inspector serialized properties on one ScriptableObject asset in a single call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the target ScriptableObject |
| items | string | Yes | - | JSON array of `{propertyPath, value, valueAssetPath, valueObjectType}` |

Example `items`: `[{"propertyPath":"items.Array.size","value":"2"},{"propertyPath":"items.Array.data[0]","value":"101"},{"propertyPath":"icon","valueAssetPath":"Assets/Icons/sword.png"}]`

**Returns:** `{ success, totalItems, successCount, failCount, results }`

### `scriptableobject_delete`
Delete a ScriptableObject asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the ScriptableObject to delete |

**Returns:** `{ success, deleted }`

### `scriptableobject_find`
Find ScriptableObject assets by type name.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| typeName | string | Yes | - | ScriptableObject type name to search for |
| searchPath | string | No | `"Assets"` | Folder path to search within |
| limit | int | No | `50` | Maximum number of results to return |

**Returns:** `{ success, count, assets }`

### `scriptableobject_export_json`
Export a ScriptableObject to JSON.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the ScriptableObject to export |
| savePath | string | No | `null` | File path to save the JSON output; if omitted, JSON is returned inline |

**Returns:** `{ success, path }` or `{ success, json }`

### `scriptableobject_import_json`
Import JSON data into a ScriptableObject. Two accepted formats:
- Bare field object `{"hp": 100, "title": "Boss"}` — auto-wrapped into `{"MonoBehaviour": {...}}` before applying
- The `{"MonoBehaviour": {...}}` envelope produced by `scriptableobject_export_json`

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the target ScriptableObject |
| json | string | No | `null` | JSON string to import |
| jsonFilePath | string | No | `null` | Path to a JSON file to read and import |

**Returns:** `{ success, assetPath }`. If no serialized field changed (field names did not match, or values already equal), returns `success: true` plus a `warning` — treat the warning as "nothing was written" and verify field names against `scriptableobject_export_json` output.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
