### camera_look_at
Focus Scene View camera on a world-space point. Applied immediately.
**Parameters:**
- `x`, `y`, `z` (float): Target point.
- Does not support `targetName` or GameObject lookup. For object focus, use `camera_align_view_to_object`.

**Returns:** `{ success, pivot, rotation, size }`.
