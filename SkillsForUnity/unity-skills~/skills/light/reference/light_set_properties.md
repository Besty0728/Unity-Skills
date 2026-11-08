### light_set_properties
Configure light properties. Every parameter is optional; omitted ones keep their current value.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Light object name |
| `entityId` | string | No* | Entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `r`, `g`, `b` | float | No | Colour, 0-1; each channel defaults to the light's current value |
| `a` | float | No | Colour alpha, 0-1 |
| `intensity` | float | No | Light intensity |
| `range` | float | No | Range (Point and Spot only) |
| `spotAngle` | float | No | Cone angle (Spot only) |
| `shadows` | string | No | `None` / `Hard` / `Soft`; matched case-insensitively |

**Returns:** `{success, name, applied, skipped, lightType, color, intensity, range, spotAngle, shadows}`. `applied` lists the parameters that took effect, named as passed (colour channels as `r`, `g`, `b`, `a`). `skipped` lists parameters the light type cannot carry (a `range` on Directional, a `spotAngle` on anything but Spot), each with its reason. A parameter in neither list was not supplied.

An unrecognised `shadows` value rejects the whole call with `SEMANTIC_INVALID` and `validValues`; nothing is applied.
