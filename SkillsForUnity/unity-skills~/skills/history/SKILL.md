---
name: unity-history
description: Manage undo/redo history over the Unity Editor native undo stack — inspect, step through, and control recorded operations. Use when reviewing or navigating the undo history, stepping undo/redo, or auditing what changed, even if the user just says "撤销历史" or "回退操作". 管理 Unity 编辑器原生撤销栈上的撤销/重做历史(查看、逐步遍历、控制已记录的操作);当用户要查看或浏览撤销历史、逐步撤销/重做、或审查改动时使用。
---

# History Skills

Manage Unity Editor undo/redo history.

## Operating Mode

本模块全部 3 个 skill (`history_undo` / `history_redo` / `history_get_current`) 均标 `SkillMode.SemiAuto`，Approval / Auto / Bypass 三档下都可直接执行。**不含 NeverInSemi 高危 skill**。

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
Undo the last operation.
**Parameters:**
- `steps` (int, optional, default 1): Number of operations to undo.

### `history_redo`
Redo the last undone operation.
**Parameters:**
- `steps` (int, optional, default 1): Number of operations to redo.

### `history_get_current`
Get current undo history state.
**Parameters:** None.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
