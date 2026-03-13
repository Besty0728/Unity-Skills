---
name: unity-camera
description: "Scene View camera control. Use when users want to move, rotate, or align the editor camera view. Triggers: camera, view, scene view, look at, align, viewport, 相机, 摄像机, 视角."
---

# Camera Skills

Control the Scene View camera.

## Skills

### `camera_align_view_to_object`
Align Scene View camera to look at an object.
**Parameters:**
- `objectName` (string): Name of the target GameObject.

### `camera_get_info`
Get Scene View camera position and rotation.
**Parameters:** None.

### `camera_set_transform`
Set Scene View camera position/rotation manually.
**Parameters:**
- `posX`, `posY`, `posZ` (float): Position.
- `rotX`, `rotY`, `rotZ` (float): Rotation (Euler).
- `size` (float, optional): Orthographic size or distance.
- `instant` (bool, optional): Move instantly (default true).

### `camera_look_at`
Focus Scene View camera on a point.
**Parameters:**
- `x`, `y`, `z` (float): Target point.

### `camera_create`
Create a new Game Camera.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | "New Camera" | Name of the new camera GameObject |
| x | float | No | 0 | Position X |
| y | float | No | 1 | Position Y |
| z | float | No | -10 | Position Z |

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
