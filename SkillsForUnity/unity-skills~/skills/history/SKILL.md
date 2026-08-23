---
name: unity-history
description: Manage undo/redo history over the native undo stack
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Reviewing or navigating undo history
- Stepping undo/redo
- Auditing what changed
- 查看或浏览撤销历史、逐步撤销/重做、审查改动

# History Skills

Manage Unity Editor undo/redo history.

## Operating Mode

本模块 `history_get_current`（纯读）标 `SkillMode.SemiAuto`，三档下均可直接执行；`history_undo` / `history_redo` 会改变场景状态，为默认 `SkillMode.FullAuto`（Operation=Execute），Approval 模式下需 grant。**不含 NeverInSemi 高危 skill**。

`history_undo` / `history_redo` replay Unity's own native Undo/Redo stack, whose contents cannot be classified by write category the way `workflow` module's task snapshots can, so unlike `workflow_undo_task` / `workflow_session_undo` they carry no payload-level `SURFACE_EXCLUDED` check.

**DO NOT** (common hallucinations):
- `history_list` / `history_get` do not exist → use `history_get_current` for current undo group
- `history_clear` does not exist → Unity undo history cannot be cleared via API
- `history_save` does not exist → undo history is managed by Unity automatically

**Routing**:
- For simple undo/redo → `history_undo` / `history_redo` (this module) or `editor_undo` / `editor_redo`
- For persistent task-level undo → use `workflow` module
- For conversation-level undo → use `workflow` module's `workflow_session_undo`

## Skills

### `history_undo`
Undo the last operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of undo steps to perform |

**Returns:** `{ success, undoneSteps }`

### `history_redo`
Redo the last undone operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of redo steps to perform |

**Returns:** `{ success, redoneSteps }`

### `history_get_current`
Get the name of the current undo group.

No parameters.

**Returns:** `{ success, currentGroup, groupIndex }`

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
