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
