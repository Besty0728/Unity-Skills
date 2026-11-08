---
name: unity-bookmark
description: Manage Scene View bookmarks and saved viewpoints
---

# Bookmark Skills

Save and recall Scene View camera positions with the current selection. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

The skills are defined in `WorkflowSkills.cs` (category Workflow) and documented here as well as in the [workflow](../workflow/SKILL.md) module.

## When to use
- Saving or restoring Scene View viewpoints
- Bookmarking camera angles
- Navigating saved scene locations

## Rules
- Bookmarks store the Scene View position and the current selection, not scene state. They live in memory (`WorkflowSkills.cs:18`), so they do not survive a domain reload.
- `bookmark_set` and `bookmark_goto` are FullAuto: under Approval they need a grant. `bookmark_list` is read-only. `bookmark_delete` is delete-class and is refused under Approval and Auto.
- Object-state undo belongs to the [workflow](../workflow/SKILL.md) module; scene save and load belong to the [scene](../scene/SKILL.md) module.

## Pitfalls
- `bookmark_save` does not exist: use `bookmark_set`.
- `bookmark_load` and `bookmark_restore` do not exist: use `bookmark_goto`.
- `bookmark_remove` does not exist: use `bookmark_delete`.

## Skills

### bookmark_set
Save current selection and scene view position as a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |
| note | string | No | null | Optional note for the bookmark |

**Returns:** `{ success, bookmark, selectedCount, hasSceneView, note }`

### bookmark_goto
Restore selection and scene view from a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |

**Returns:** `{ success, bookmark, restoredSelection, note }`

### bookmark_list
List all saved bookmarks.

No parameters.

**Returns:** `{ success, count, bookmarks: [{ name, selectedCount, hasSceneView, note, createdAt }] }`

### bookmark_delete
Delete a bookmark.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| bookmarkName | string | Yes | - | Bookmark name |

**Returns:** `{ success, deleted }`
