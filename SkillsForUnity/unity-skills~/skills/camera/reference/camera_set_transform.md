### camera_set_transform
Set the Scene View camera by pivot (the framed world point, not the eye position), rotation and size.
**Parameters:**
- `posX`, `posY`, `posZ` (float): Pivot — the world point framed, not the eye position.
- `rotX`, `rotY`, `rotZ` (float): Rotation (Euler).
- `size` (float, optional): Scene View size (zoom) (default 5).
- `instant` (bool, optional): true: applied immediately and read back; false: animated (default true).

**Returns:** `{ success, message, pivot, rotation, size, orthographic }`, plus `cameraPosition` (the eye position) when `instant=true`, or `animating: true` when `instant=false`.
