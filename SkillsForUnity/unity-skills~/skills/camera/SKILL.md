---
name: unity-camera
description: Control Unity Scene View and Game cameras — move/rotate the view, create and configure cameras, set FOV/clip planes/projection. Use when framing the Scene View, creating or adjusting cameras, tweaking FOV or clipping planes, or scripting camera setup, even if the user just says "镜头" or "相机". 控制 Unity Scene View 与游戏相机(移动/旋转视图、创建与配置相机、设置 FOV/裁剪面/投影);当用户要取景 Scene View、创建或调整相机、修改 FOV 或裁剪面时使用。
---

# Camera Skills

Control the Scene View camera and Game Cameras (creation, transform, properties, screenshot, culling, orthographic toggle).

## Operating Mode

- **Approval** (default): mutating skills (`camera_set_transform`, `camera_create`, `camera_set_properties`, `camera_set_culling_mask`, `camera_screenshot`, `camera_sceneview_screenshot`, `camera_set_orthographic`, `camera_align_view_to_object`, `camera_look_at`) need user grant; grant triggers a single server-side execution that returns the result.
- **Auto / Bypass**: those skills execute directly.
- Query skills (`camera_get_info`, `camera_get_properties`, `camera_list`) are `SkillMode.SemiAuto` — they run in all three modes without grant.
- This module contains **no** Delete / PlayMode / Reload / high-risk skills (no NeverInSemi).

## Guardrails

**DO NOT** (common hallucinations):
- `camera_move` / `camera_rotate` do not exist → use `camera_set_transform` (Scene View) or `gameobject_set_transform` (Game Camera)
- `camera_set_fov` does not exist → use `camera_set_properties` with `fieldOfView` parameter
- `camera_*` skills control **two different cameras**: `camera_set_transform`/`camera_look_at`/`camera_align_view_to_object` control the **Scene View camera**; `camera_create`/`camera_set_properties`/`camera_screenshot` control **Game Cameras**
- `camera_delete` does not exist → use `gameobject_delete` on the camera GameObject

**Routing**:
- For Cinemachine virtual cameras → use `cinemachine` module
- For Game Camera component properties → `camera_set_properties` / `camera_get_properties` (this module)
- For screenshots → three options: `scene_screenshot` (scene module) = the **Game View** final composite (all cameras + UI; Play mode = live runtime frame); `camera_screenshot` (this module) = a **single Game Camera** off-screen render; `camera_sceneview_screenshot` (this module) = the **editor Scene View** (developer viewport, incl. grid/gizmos)

## Skills

### `camera_align_view_to_object`
Align Scene View camera to look at an object.
**Parameters:**
- `name` (string, optional): Target GameObject name.
- `instanceId` (int, optional): Target GameObject instance ID.
- `path` (string, optional): Target GameObject hierarchy path.

### `camera_get_info`
Get Scene View camera position and rotation.
**Parameters:** None.

### `camera_set_transform`
Set Scene View camera position/rotation manually.
**Parameters:**
- `posX`, `posY`, `posZ` (float): Position.
- `rotX`, `rotY`, `rotZ` (float): Rotation (Euler).
- `size` (float, optional): Orthographic size or pivot distance (default 5).
- `instant` (bool, optional): Move instantly (default true).

### `camera_look_at`
Focus Scene View camera on a world-space point.
**Parameters:**
- `x`, `y`, `z` (float): Target point.
- Does not support `targetName` or GameObject lookup. For object focus, use `camera_align_view_to_object`.

### `camera_create`
Create a new Game Camera.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | "New Camera" | Name of the new camera GameObject |
| x | float | No | 0 | Position X |
| y | float | No | 1 | Position Y |
| z | float | No | -10 | Position Z |
| addAudioListener | bool | No | false | Also attach an `AudioListener` component |

**Returns:** `{ success, name, instanceId }`

### `camera_get_properties`
Get Game Camera properties (supports name/instanceId/path).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the camera GameObject |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, name, fieldOfView, nearClipPlane, farClipPlane, orthographic, orthographicSize, depth, cullingMask, clearFlags, backgroundColor, rect }`

### `camera_set_properties`
Set Game Camera properties (FOV, clip planes, clear flags, background color, depth).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the camera GameObject |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |
| fieldOfView | float? | No | null | Camera field of view |
| nearClipPlane | float? | No | null | Near clipping plane distance |
| farClipPlane | float? | No | null | Far clipping plane distance |
| depth | float? | No | null | Camera rendering depth |
| clearFlags | string | No | null | Clear flags (e.g. Skybox, SolidColor, Depth, Nothing) |
| bgR | float? | No | null | Background color red component |
| bgG | float? | No | null | Background color green component |
| bgB | float? | No | null | Background color blue component |

**Returns:** `{ success, name }`

### `camera_set_culling_mask`
Set Game Camera culling mask by layer names (comma-separated).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| layerNames | string | Yes | - | Comma-separated layer names |
| name | string | No | null | Name of the camera GameObject |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, cullingMask }`

### `camera_screenshot`
Capture a screenshot from a Game Camera to file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| savePath | string | No | "Assets/screenshot.png" | File path to save the screenshot |
| width | int | No | 1920 | Screenshot width in pixels |
| height | int | No | 1080 | Screenshot height in pixels |
| name | string | No | null | Name of the camera GameObject |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, path, width, height }`

### `camera_sceneview_screenshot`
Capture the **editor Scene View** (the developer's editing viewport — can overlook the whole scene incl. off-camera objects). Distinct from `scene_screenshot` (Game View / player camera) and `camera_screenshot` (one Game Camera). By default captures the full Scene View incl. grid/gizmos/selection (on-screen read); auto-falls back to a clean offscreen render if the editor build lacks the internal API. The Scene View window must be open and visible for the overlay capture.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| filename | string | No | "sceneview.png" | Bare filename only (no path separators); saved under `Assets/Screenshots/` |
| includeOverlays | bool | No | true | True = full Scene View with grid/gizmos/selection (falls back to a clean render if unsupported); false = clean offscreen scene render only |

**Returns:** `{ success, path, width, height, mode, note }` — `mode` is `"screen_with_overlays"` or `"offscreen_clean"`.

### `camera_set_orthographic`
Switch Game Camera between orthographic and perspective mode.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| orthographic | bool | Yes | - | True for orthographic, false for perspective |
| orthographicSize | float? | No | null | Orthographic size (only applies in orthographic mode) |
| name | string | No | null | Name of the camera GameObject |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |

**Returns:** `{ success, orthographic, orthographicSize }`

### `camera_list`
List all cameras in the scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|

**Returns:** `{ count, cameras: [{ name, instanceId, path, depth, orthographic, enabled }] }`

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
