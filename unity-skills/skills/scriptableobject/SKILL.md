---
name: unity-scriptableobject
description: "ScriptableObject management. Use when users want to create, read, or modify ScriptableObject assets. Triggers: scriptableobject, SO, data asset, config, settings asset, 数据资产, 配置文件."
---

# ScriptableObject Skills

Create and manage ScriptableObject assets.

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
- `sourcePath` (string): Source asset path.
- `destPath` (string): Destination path.

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
