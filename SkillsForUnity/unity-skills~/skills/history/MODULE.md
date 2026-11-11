# History Skills

Undo, redo and inspect the current undo group of the Unity Editor's native undo stack. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

The skills are defined in `WorkflowSkills.cs` (category Workflow) and documented here as well as in the [workflow](../workflow/MODULE.md) module.

## When to use
- Reviewing or navigating undo history
- Stepping undo and redo
- Auditing what changed

## Rules
- `history_get_current` is read-only and runs under Approval. `history_undo` and `history_redo` change scene state and need a grant under Approval.
- `history_undo` and `history_redo` replay Unity's own native undo stack. Its entries cannot be classified by write category the way workflow task snapshots can, so they carry no payload-level `SURFACE_EXCLUDED` check. [workflow](../workflow/MODULE.md) undo (`workflow_undo_task`, `workflow_session_undo`) does carry that check.
- For persistent task-level or conversation-level undo, use the [workflow](../workflow/MODULE.md) module. For simple undo and redo, the [editor](../editor/MODULE.md) skills `editor_undo` and `editor_redo` are the alternative.

## Pitfalls
- `history_list` and `history_get` do not exist: use `history_get_current` for the current undo group.
- `history_clear` does not exist, and no skill clears the undo history. Unity exposes `Undo.ClearAll()` (this repository's prefab tests call it, `Tests/Editor/Core/PrefabAssetEditingTests.cs:32`), but no skill wraps it.
- `history_save` does not exist: Unity manages the undo history automatically.

## Skills

### history_undo
Undo the last operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of undo steps to perform |

**Returns:** `{ success, undoneSteps }`

### history_redo
Redo the last undone operation (or multiple steps).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| steps | int | No | 1 | Number of redo steps to perform |

**Returns:** `{ success, redoneSteps }`

### history_get_current
Get the name of the current undo group.

No parameters.

**Returns:** `{ success, currentGroup, groupIndex }`
