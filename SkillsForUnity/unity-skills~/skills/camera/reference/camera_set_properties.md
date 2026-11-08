### camera_set_properties
Set Game Camera properties (FOV, clip planes, clear flags, background color, depth).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No | null | Name of the camera GameObject |
| entityId | string | No | null | Entity ID of the camera GameObject (preferred; the only id on Unity 6000.4+) |
| instanceId | int | No | 0 | Instance ID of the camera GameObject |
| path | string | No | null | Hierarchy path of the camera GameObject |
| fieldOfView | float? | No | null | Camera field of view |
| nearClipPlane | float? | No | null | Near clipping plane distance |
| farClipPlane | float? | No | null | Far clipping plane distance |
| depth | float? | No | null | Camera rendering depth |
| clearFlags | string | No | null | Clear flags: `Skybox`, `Color`, `SolidColor`, `Depth`, `Nothing`. `Color` and `SolidColor` are two names for the same underlying `CameraClearFlags` value, so both are accepted and the response always echoes it back as `Color` — do not read that as the write having been ignored |
| bgR | float? | No | null | Background color red component (0-1) |
| bgG | float? | No | null | Background color green component (0-1) |
| bgB | float? | No | null | Background color blue component (0-1) |
| bgA | float? | No | null | Background color alpha (0-1) |

Any `bg*` channel you omit keeps the camera's current value, so you can set alpha alone.

**Returns:** `{ success, name, applied, fieldOfView, nearClipPlane, farClipPlane, orthographic, orthographicSize, depth, cullingMask, clearFlags, backgroundColor, rect }` — the response echoes the camera's state **after** the write, and `applied` lists which parameters were actually changed, named exactly as you passed them (the background channels appear individually as `bgR`, `bgG`, `bgB`, `bgA`, not lumped under `backgroundColor`), so a parameter absent from `applied` was not supplied. That echo is your verification: a second `camera_get_properties` call is redundant. An unparseable `clearFlags` rejects the whole call with `SEMANTIC_INVALID` + `validValues` before anything is applied, so the numeric parameters in the same call are never half-written.
