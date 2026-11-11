# Unity Cleaner Skills

Find and clean up project bloat: unused, duplicate, missing-reference and empty assets, large assets, asset usage and dependency trees. The module reports and does not delete, except through the two-step `cleaner_delete_assets` and `cleaner_delete_empty_folders`. Parameters come from the schema (`GET /skills/schema?category=Cleaner&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Reducing project bloat
- Hunting duplicate or orphaned assets
- Auditing before a release

## Rules
- `cleaner_delete_assets` takes two calls. Call it without `confirmToken` to preview; call it again with the returned `confirmToken` to delete. The token expires after 5 minutes. There is no `dryRun` parameter.
- Analyze and query skills (`cleaner_find_*`, `cleaner_get_asset_usage`, `cleaner_get_dependency_tree`) are read-only and run under Approval.
- `cleaner_delete_assets` and `cleaner_delete_empty_folders` are delete-class: under Approval and Auto they return `MODE_FORBIDDEN`, and only Bypass or an Allowlist entry runs them. A grant does not unlock them.
- `cleaner_fix_missing_scripts` replaces missing script components in place and deletes no asset. It is not delete-class, so under Approval it needs the standard grant.
- To remove assets that the analysis finds without the two-step path, use [asset](../asset/MODULE.md) `asset_delete`. Project validation belongs to the [validation](../validation/MODULE.md) module.

## Pitfalls
- `cleaner_delete` and `cleaner_remove` do not exist: the cleaner skills find and report; use the two-step `cleaner_delete_assets` or `asset_delete`.
- `cleaner_fix` does not exist: use `cleaner_fix_missing_scripts` for missing script references.
- `cleaner_scan` and `cleaner_find_unused` do not exist: use the specific `cleaner_find_*` skills.

## Skills Overview

| Skill | Description |
|-|-|
| `cleaner_find_unused_assets` | Find assets not referenced by others |
| `cleaner_find_duplicates` | Find duplicate files by content hash |
| `cleaner_find_missing_references` | Find missing scripts/asset references |
| `cleaner_delete_assets` | Delete assets via two-step confirmToken (preview → confirm) |
| `cleaner_get_asset_usage` | Find what references a specific asset |
| `cleaner_find_empty_folders` | Find empty folders in the project |
| `cleaner_find_large_assets` | Find largest assets by file size |
| `cleaner_delete_empty_folders` | Delete all empty folders |
| `cleaner_fix_missing_scripts` | Remove missing script components from GameObjects |
| `cleaner_get_dependency_tree` | Get dependency tree for an asset |

## Skills

### cleaner_find_unused_assets
Find potentially unused assets of a specific type.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Material" | Asset type filter |
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, assetType, potentiallyUnusedCount, assets: [{path, name, type, sizeBytes}]}`

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_unused_assets?expectProject=<project>" -d '{"assetType":"Material"}'
```

Unused textures in one folder:

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_unused_assets?expectProject=<project>" -d '{"assetType":"Texture2D","searchPath":"Assets/Textures"}'
```

### cleaner_find_duplicates
Find duplicate files by MD5 hash.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetType` | string | No | "Texture2D" | Asset type |
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 50 | Max groups |

**Returns**: `{success, duplicateGroupCount, totalWastedBytes, totalWastedMB, groups: [{count, sizeBytes, wastedBytes, files}], skipped: [{path, reason}]}` — `skipped` lists files that could not be read for hashing, so they are not silently missing from the groups.

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_duplicates?expectProject=<project>" -d '{"assetType":"Texture2D"}'
```

### cleaner_find_missing_references
Find components with missing scripts or null references.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeInactive` | bool | No | true | Include inactive objects |

**Returns**: `{success, issueCount, missingScripts, missingReferences, issues: [{type, gameObject, path, ...}]}`

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_missing_references?expectProject=<project>" -d '{}'
```

### cleaner_delete_assets
Delete specified assets with **two-step confirmation**.

Both steps use this table. Step 1 sends `paths`; step 2 sends only `confirmToken`.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `paths` | string[] | Step 1 | Asset paths to preview; step 2 deletes exactly the paths captured by the token |
| `confirmToken` | string | Step 2 | Token from the preview; valid 5 minutes |

**Returns** on preview (step 1): `{success, action: "preview", totalAssets, existingAssets, totalBytes, totalMB, confirmToken, message, expiresIn, assetsToDelete: [{path, exists, sizeBytes, sizeMB}]}`

**Returns** on confirm (step 2): `{success, action: "deleted", deletedCount, totalMB, message, results: [{path, deleted}]}`

Step 1, preview (no `confirmToken`):

```bash
curl -s "http://localhost:<port>/skill/cleaner_delete_assets?expectProject=<project>" -d '{"paths":["Assets/Unused/mat1.mat","Assets/Unused/mat2.mat"]}'
```

Step 2, confirm with the token from step 1 (valid 5 minutes):

```bash
curl -s "http://localhost:<port>/skill/cleaner_delete_assets?expectProject=<project>" -d '{"confirmToken":"<token from preview>"}'
```

### cleaner_get_asset_usage
Find what objects reference a specific asset.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `assetPath` | string | Yes | Asset path |
| `limit` | int | No | Max results (default 50) |

**Returns**: `{success, asset, usedByCount, usedBy: [{path, name, type}]}`

```bash
curl -s "http://localhost:<port>/skill/cleaner_get_asset_usage?expectProject=<project>" -d '{"assetPath":"Assets/Textures/player.png"}'
```

### `cleaner_find_empty_folders`
Find empty folders in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |

**Returns:** `{ success, count, folders }`

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_empty_folders?expectProject=<project>" -d '{}'
```

### `cleaner_find_large_assets`
Find largest assets by file size.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |
| `limit` | int | No | 20 | Max results |
| `minSizeBytes` | long | No | 0 | Minimum file size in bytes |

**Returns:** `{ success, count, assets: [{ path, sizeBytes, sizeMB }] }`

```bash
curl -s "http://localhost:<port>/skill/cleaner_find_large_assets?expectProject=<project>" -d '{"limit":10,"minSizeBytes":1048576}'
```

### `cleaner_delete_empty_folders`
Delete all empty folders.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchPath` | string | No | "Assets" | Search path |

**Returns:** `{ success, deleted, total }`

```bash
curl -s "http://localhost:<port>/skill/cleaner_delete_empty_folders?expectProject=<project>" -d '{}'
```

### `cleaner_fix_missing_scripts`
Remove missing script components from GameObjects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeInactive` | bool | No | true | Include inactive objects |

**Returns:** `{ success, removedComponents }`

```bash
curl -s "http://localhost:<port>/skill/cleaner_fix_missing_scripts?expectProject=<project>" -d '{}'
```

### `cleaner_get_dependency_tree`
Get dependency tree for an asset.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `assetPath` | string | Yes | - | Asset path |
| `recursive` | bool | No | true | Recursively resolve dependencies |

**Returns:** `{ success, assetPath, dependencyCount, dependencies: [{ path, type }] }`

```bash
curl -s "http://localhost:<port>/skill/cleaner_get_dependency_tree?expectProject=<project>" -d '{"assetPath":"Assets/Prefabs/Player.prefab"}'
```

---
