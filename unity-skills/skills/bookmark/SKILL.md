---
name: unity-bookmark
description: "Scene view bookmarks. Use when users want to save, recall, or manage camera positions in the editor. Triggers: bookmark, save view, goto, camera position, viewpoint, 书签, 视角, 保存视图."
---

# Bookmark Skills

Save and recall Scene View camera positions.

## Skills

### `bookmark_set`
Save current Scene View camera position as a bookmark.
**Parameters:**
- `name` (string): Bookmark name.

### `bookmark_goto`
Move Scene View camera to a saved bookmark.
**Parameters:**
- `name` (string): Bookmark name.

### `bookmark_list`
List all saved bookmarks.
**Parameters:** None.

### `bookmark_delete`
Delete a saved bookmark.
**Parameters:**
- `name` (string): Bookmark name.
