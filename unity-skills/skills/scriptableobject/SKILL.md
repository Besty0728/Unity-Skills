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
