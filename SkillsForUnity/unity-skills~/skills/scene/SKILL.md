---
name: unity-scene
description: Manage Unity scenes
---

# Unity Scene Skills

Control Unity scenes, the containers that hold the GameObjects. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Opening, creating or saving scenes
- Loading scenes additively or switching the active scene
- Querying scene contents

## Rules
- `scene_save` overwrites the active scene's own file when `scenePath` is omitted. A different `scenePath` re-associates the active scene with that file (Save As); it does not copy. An untitled scene has no path, so it needs `scenePath`.
- `scene_save` undo restores the on-disk `.unity` file when the scene is closed. While the scene is open, undo does not rewrite the file; it marks the scene dirty, so save again to apply it.
- `scene_save` fails with `SEMANTIC_INVALID` during Play Mode, with a suggested `editor_stop` retry. Exit Play Mode first.
- `scene_create`, `scene_load` and `scene_save` are high-risk writes. Other scene skills that modify state are gated by the schema's `approvalBehavior`.
- `scene_screenshot` captures the Game View (all cameras and UI; the live runtime frame in Play Mode), not the Scene view. For one Game Camera use [camera](../camera/SKILL.md) `camera_screenshot`.
- For the detailed hierarchy tree or statistics, use the `perception` module (`hierarchy_describe`, `scene_summarize`).
- `DontDestroyOnLoad` objects appear in Play Mode as a pseudo-scene. They are found by `gameobject_find`, but the pseudo-scene cannot be unloaded or activated.

## Pitfalls
- `scene_delete` and `scene_rename` do not exist. Delete scene files with `asset_delete` and rename them with `asset_move`.
- `scene_list` does not exist. Use `scene_get_loaded` for loaded scenes, or `asset_find` with `t:Scene` for all scene assets.
- `scene_find_objects` is a plain name, tag or component filter. For regex, layer or path search use `gameobject_find`.

## Skills

### scene_create
Create a new empty scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| scenePath | string | Yes | Path of the new scene, e.g. "Assets/Scenes/MyScene.unity" |

### scene_load
Load a scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| scenePath | string | Yes | - | Scene asset path |
| additive | bool | No | false | `true` keeps the loaded scenes open (`OpenSceneMode.Additive`); `false` closes them first (`Single`) |

### scene_save
Save the active scene.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| scenePath | string | No | Omit to overwrite the active scene's own file. A different path re-associates the scene with that file (Save As). Needed for an untitled scene. |

### scene_get_info
Get information about the active scene.

**Parameters**: none.

**Returns**: `{sceneName, scenePath, isDirty, rootObjectCount, rootObjects: [{name, entityId, instanceId, childCount}]}`. `childCount` lets you decide which roots are worth descending into before a hierarchy call.

### scene_get_hierarchy
Get the active scene's hierarchy tree, depth-limited.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| maxDepth | int | No | 3 | Maximum depth to expand |

**Returns**: `{sceneName, hierarchy: [node, ...]}`. Each node is `{name, scene, entityId, instanceId, components: [type, ...], childCount, children}`. `scene` is the owning scene's name.

In Play Mode, roots of the `DontDestroyOnLoad` pseudo-scene follow the active-scene nodes, marked `scene: "DontDestroyOnLoad"`.

`childCount` is always the real number of children, whatever `maxDepth` is. `children` is `null` once the depth limit is reached:

| `childCount` | `children` | Meaning |
|---|---|---|
| `0` | `null` | Genuine leaf |
| `> 0` | `null` | Clipped by `maxDepth`; children exist but were not returned |
| `> 0` | array | Expanded at this level |

Never read `children: null` as "empty". To see what is below a clipped node, call again with a larger `maxDepth` (the default is 3), or query that subtree with `gameobject_find`.

### scene_screenshot
Capture the Game View: the final composited frame of all cameras and UI.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filename | string | No | "screenshot.png" | Bare filename, no path separators; saved under `Assets/Screenshots/` |
| width | int | No | 1920 | Image width |
| height | int | No | 1080 | Image height |
| returnImage | bool | No | false | Also return a PNG as base64 in `imageBase64`, for clients without filesystem access |
| maxDimension | int | No | 1280 | Used only with `returnImage=true`. Downscales the returned image (not the saved file) so its longer edge is at most this value. Clamped to 256-4096. |

**Returns**: `{success, path, width, height, isPlaying, note, imageBase64, imageWidth, imageHeight, imageBytes}`. `isPlaying` tells a live Play Mode frame from a static Edit-mode frame. The four `image*` fields appear only with `returnImage=true`. A base64 payload over 8 MB returns an error asking for a smaller `maxDimension`; the file at `path` is still saved.

The PNG is written about one frame later. If reading `path` at once fails, wait about 200 ms and retry. `returnImage` does not read that file. It makes a separate synchronous capture of the current Game View backbuffer, so the returned image can be a moment older than the file.

A local agent that can read files should omit `returnImage` and read the PNG at `path`, which costs fewer tokens.

### scene_get_loaded
List all loaded scenes.

**Parameters**: none.

**Returns**: `{success, count, scenes: [{name, path, isLoaded, isDirty, isActive, rootCount, isPseudoScene}]}`. In Play Mode the `DontDestroyOnLoad` pseudo-scene (`isPseudoScene: true`, empty `path`) is appended when it has root objects.

### scene_unload
Unload a loaded scene (additive).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sceneName | string | Yes | Name of the scene to unload |

### scene_set_active
Set the active scene for multi-scene editing.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| sceneName | string | Yes | Name of the scene to make active |

### scene_find_objects
Search GameObjects by name, tag or component type across the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| namePattern | string | No | - | Case-insensitive substring, not a wildcard or regex |
| tag | string | No | - | Tag filter; a tag not defined in Tags & Layers is rejected |
| componentType | string | No | - | Component type filter; an unknown type is rejected |
| limit | int | No | 50 | Max objects returned |

**Returns**: `{success, count, totalMatched, truncated, objects: [{name, path, instanceId, active, tag}]}`. `count` is the number returned (at most `limit`); `totalMatched` counts every match; `truncated` is `true` when `limit` cut the list. Router paging (`pageOffset` / `pageLimit`) pages `objects` and adds its own `totalCount` and `limit`.

## Errors

Transport-level codes (COMPILING, RATE_LIMIT and others): [error codes](../../references/protocol-error-codes.md).

| Error | Trigger | Fix |
|---|---|---|
| `TARGET_NOT_FOUND` | The scene is not found or not loaded (`Scene not found`, `Scene is not loaded`). | Check the path with `asset_find`, or list loaded scenes with `scene_get_loaded`. |
| `MISSING_PARAM` | `scenePath` is missing for `scene_create`, or the active scene has no save path. | Pass `scenePath`, or save the scene once first. |
| `SEMANTIC_INVALID` | An unknown tag or component type for `scene_find_objects`; `scene_save` during Play Mode. | Use a valid name, consider `gameobject_find`, or run `editor_stop` first. |
| `SKILL_ERROR` | A state constraint blocked the call, such as unloading the only loaded scene. | Keep at least one scene loaded. |
