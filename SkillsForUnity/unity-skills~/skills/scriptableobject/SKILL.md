---
name: unity-scriptableobject
description: Manage ScriptableObject assets — create, read, modify, delete, duplicate, find, and list SO assets. Use when creating or editing ScriptableObject data assets, finding SO instances, or scripting SO management, even if the user just says "配置表" or "SO资产". 管理 ScriptableObject 资产(创建、读取、修改、删除、复制、查找、列出 SO 资产);当用户要创建或编辑 ScriptableObject 数据资产、查找 SO 实例、或脚本化管理 SO 时使用。
---

# ScriptableObject Skills

Create and manage ScriptableObject assets.

## Operating Mode

- **Approval**：本模块 Mixed —— `scriptableobject_get` / `scriptableobject_list_types` / `scriptableobject_find` / `scriptableobject_export_json` 标 `SkillMode.SemiAuto`，可直接执行；写类 skill (`scriptableobject_create` / `scriptableobject_set` / `scriptableobject_set_batch` / `scriptableobject_duplicate` / `scriptableobject_import_json`) 标 `SkillMode.FullAuto`，需 grant 单次执行返结果。
- **Auto / Bypass**：FullAuto 直接执行。
- **含 NeverInSemi 高危 skill**：`scriptableobject_delete`（Operation.Delete）。该 skill 在 Approval/Auto 下返 `MODE_FORBIDDEN`，仅 Bypass 或 Allowlist 命中可调。

**DO NOT** (common hallucinations):
- `scriptableobject_create_type` does not exist → create SO scripts via `script_create` with template "ScriptableObject"
- `scriptableobject_get_properties` / `scriptableobject_read` do not exist → use `scriptableobject_get`
- `scriptableobject_set_property` / `scriptableobject_set_field` do not exist → use `scriptableobject_set` (single field) or `scriptableobject_set_batch` (multiple fields)
- `scriptableobject_save` does not exist → changes are auto-saved to the asset

**Routing**:
- For ScriptableObject script creation → use `script` module with template "ScriptableObject"
- For JSON import/export → `scriptableobject_import_json` / `scriptableobject_export_json` (this module)

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
Set a field/property on a ScriptableObject.
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
Import JSON data into a ScriptableObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| assetPath | string | Yes | - | Asset path of the target ScriptableObject |
| json | string | No | `null` | JSON string to import |
| jsonFilePath | string | No | `null` | Path to a JSON file to read and import |

**Returns:** `{ success, assetPath }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
