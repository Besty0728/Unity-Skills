---
name: unity-bookmark
description: Manage Scene View bookmarks and saved viewpoints
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Saving or restoring Scene View viewpoints
- Bookmarking camera angles
- Navigating saved scene locations
- 保存或恢复场景视角、收藏机位、在已存位置间切换

# Bookmark Skills

Save and recall Scene View camera positions.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): 只有 `bookmark_list` 标 `SkillMode.SemiAuto`（纯读），Approval 模式下可直接执行，无需走 grant 协议。`bookmark_set`（`Operation.Create`，写入书签表）与 `bookmark_goto`（`Operation.Execute`，改动编辑器选中对象与 Scene View 视角）都有副作用，走默认 `SkillMode.FullAuto`，Approval 模式下需 grant。与 `workflow` 模块文档保持一致（以 C# `WorkflowSkills.cs` 的特性标注为准）。
- **Auto** / **Bypass**: SemiAuto and FullAuto run directly.
- Auto-forbidden in this module: `bookmark_delete` (`SkillOperation.Delete`). Reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`. Bookmarks themselves are in-memory only — `bookmark_delete` only removes the entry, no asset I/O.

**DO NOT** (common hallucinations):
- `bookmark_save` does not exist → use `bookmark_set`
- `bookmark_load` / `bookmark_restore` do not exist → use `bookmark_goto`
- `bookmark_remove` does not exist → use `bookmark_delete`
- Bookmarks save Scene View position + current selection, not scene state

**Routing**:
- For workflow snapshots (object state undo) → use `workflow` module
- For scene save/load → use `scene` module

## Skills

### `bookmark_set`
Save current selection and scene view position as a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |
| note | string | No | null | Optional note for the bookmark |

**Returns:** `{ success, bookmark, selectedCount, hasSceneView, note }`

### `bookmark_goto`
Restore selection and scene view from a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |

**Returns:** `{ success, bookmark, restoredSelection, note }`

### `bookmark_list`
List all saved bookmarks.

No parameters.

**Returns:** `{ success, count, bookmarks: [{ name, selectedCount, hasSceneView, note, createdAt }] }`

### `bookmark_delete`
Delete a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |

**Returns:** `{ success, deleted }`

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
