# Physics Skills

Raycasts, overlap checks, and gravity settings.

## Skills

### `physics_raycast`
Cast a ray and get hit info.
**Parameters:**
- `originX`, `originY`, `originZ` (float): Origin point.
- `dirX`, `dirY`, `dirZ` (float): Direction vector.
- `maxDistance` (float, optional): Max distance (default 1000).
- `layerMask` (int, optional): Layer mask (default -1).

**Returns:** `{ hit: true, collider: "Cube", distance: 5.2, ... }`

### `physics_check_overlap`
Check for colliders in a sphere.
**Parameters:**
- `x`, `y`, `z` (float): Center point.
- `radius` (float): Sphere radius.
- `layerMask` (int, optional): Layer mask.

### `physics_get_gravity`
Get global gravity setting.
**Parameters:** None.

### `physics_set_gravity`
Set global gravity setting.
**Parameters:**
- `x`, `y`, `z` (float): Gravity vector (e.g. 0, -9.81, 0).
