### light_create
Create a new light GameObject at the scene root.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "New Light" | Light name |
| `lightType` | string | No | "Point" | `Directional`, `Point`, `Spot`, `Area` (Rectangle) or `Disc`; case-insensitive |
| `x`, `y`, `z` | float | No | 0, 3, 0 | World position |
| `r`, `g`, `b` | float | No | 1, 1, 1 | Colour channels, 0-1 (not 0-255) |
| `intensity` | float | No | 1 | Light intensity |
| `range` | float | No | 10 | Range in world units; Point and Spot only, ignored for other types |
| `spotAngle` | float | No | 30 | Cone angle (Spot only) |
| `shadows` | string | No | "Soft" | `None` / `Hard` / `Soft` |

**Returns:** `{success, name, instanceId, lightType, position, color, intensity, shadows}`

```bash
curl -s "http://localhost:<port>/skill/light_create?expectProject=<project>" -d '{"name":"Sun","lightType":"Directional","r":1,"g":0.95,"b":0.85,"intensity":1.2,"shadows":"Soft"}'
```
