---
name: unity-bookmark
description: Manage Scene View bookmarks — save the current selection plus camera pivot/rotation/size under a name, then jump back later. Use when saving or restoring Scene View viewpoints, bookmarking a camera angle, or navigating between saved scene locations, even if the user just says "标记视角" or "存个机位". 管理 Scene View 书签(以命名方式保存当前选中对象与相机 pivot/旋转/大小,之后快速跳回);当用户要保存或恢复场景视角、收藏某个机位、或在已存位置间切换时使用。
---

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
Save current Scene View camera position as a bookmark.
**Parameters:**
- `bookmarkName` (string): Bookmark name.

### `bookmark_goto`
Move Scene View camera to a saved bookmark.
**Parameters:**
- `bookmarkName` (string): Bookmark name.

### `bookmark_list`
List all saved bookmarks.
**Parameters:** None.

### `bookmark_delete`
Delete a saved bookmark.
**Parameters:**
- `bookmarkName` (string): Bookmark name.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
