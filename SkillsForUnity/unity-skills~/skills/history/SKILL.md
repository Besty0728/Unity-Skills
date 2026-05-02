---
name: unity-history
description: "Undo/redo history management. Use when users want to undo, redo, or check edit history. Triggers: history, undo, redo, revert, previous state, Unity历史, Unity撤销, Unity重做."
---

# History Skills

Manage Unity Editor undo/redo history.

## Guardrails

**Mode**: Full-Auto required

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
