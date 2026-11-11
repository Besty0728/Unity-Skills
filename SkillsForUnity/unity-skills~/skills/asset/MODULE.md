# Unity Asset Skills

AssetDatabase operations: import, move, duplicate, delete, folders, find, refresh, reimport and labels. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Organizing project assets, folders and labels
- Importing or relocating files
- Querying asset metadata
- Refreshing or reimporting assets after external file changes

## Rules
- Use the `*_batch` skill when operating on 2 or more assets. `asset_import_batch`, `asset_delete_batch`, `asset_move_batch` and `asset_create_folder_batch` take `items` as a JSON string, not a native array.
- Batch folder creation needs each parent to exist: list nested folders outermost first within one call.
- Paths are project-relative under `Assets/` or `Packages/`.
- After writing a `.cs` file yourself, call `asset_refresh` and wait on its `waitUrl`; the reply documents the wait loop.
- Import settings belong to the [importer](../importer/MODULE.md) module, materials to [material](../material/MODULE.md), scripts to [script](../script/MODULE.md).

## Pitfalls
- `asset_create`, `asset_search` and `asset_copy` do not exist: use `asset_create_folder` (folders), `material_create` (materials), `script_create` (scripts), `asset_find` with a search filter such as `t:Texture2D player`, and `asset_duplicate`.
- `asset_rename` does not exist: `asset_move` to the new path.
- `asset_find` filters: `t:Type`, `l:Label`, a name fragment, or a combination such as `t:Material player`.
- `TARGET_NOT_FOUND`: check the path with `asset_find` or `asset_get_info`. `SEMANTIC_INVALID`: the path contains `..`, does not start with `Assets/` or `Packages/`, or the folder already exists. `SKILL_ERROR`: an AssetDatabase or filesystem operation failed (for example a locked file); fix the cause and retry.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`asset_import`](reference/asset_import.md) | Import one external file into the project |
| [`asset_import_batch`](reference/asset_import_batch.md) | Import several external files |
| [`asset_delete`](reference/asset_delete.md) | Delete one asset |
| [`asset_delete_batch`](reference/asset_delete_batch.md) | Delete several assets |
| [`asset_move`](reference/asset_move.md) | Move or rename one asset |
| [`asset_move_batch`](reference/asset_move_batch.md) | Move several assets |
| [`asset_duplicate`](reference/asset_duplicate.md) | Duplicate one asset |
| [`asset_find`](reference/asset_find.md) | Find assets by search filter |
| [`asset_create_folder`](reference/asset_create_folder.md) | Create one folder |
| [`asset_create_folder_batch`](reference/asset_create_folder_batch.md) | Create several folders |
| [`asset_refresh`](reference/asset_refresh.md) | Refresh the AssetDatabase after external changes |
| [`asset_get_info`](reference/asset_get_info.md) | Read one asset's information |
| [`asset_reimport`](reference/asset_reimport.md) | Force reimport of one asset |
| [`asset_reimport_batch`](reference/asset_reimport_batch.md) | Reimport assets matching a filter |
| [`asset_set_labels`](reference/asset_set_labels.md) | Overwrite an asset's labels |
| [`asset_get_labels`](reference/asset_get_labels.md) | Read an asset's labels |
