### particle_create
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Particle System" | New object name |
| `x`, `y`, `z` | float | No | 0 | Position, local to the parent unless `space='world'` |
| `parentEntityId` | string | No | null | Parent (also `parentName` / `parentPath` / `parentInstanceId`); must exist |
| `rotX` | float | No | -90 | Euler degrees; -90 points the default cone up (`rotY`, `rotZ` default 0) |
| `space` | string | No | "local" | `local` or `world` |
| `configJson` | string | No | null | Optional initial configuration (same shape as `particle_configure`) |

**Returns**: `{name, entityId, instanceId, path, parentPath, position, localPosition, rotation, particleSystem: {type, entityId, instanceId}, renderer: {type, entityId, instanceId}, material, defaultsApplied, applied, state, playback, warnings, adjustments?}`

Example (`particle_configure` takes the same `configJson` without `name`):

```json
{"name": "FireFX", "configJson": {"schemaVersion": 1, "modules": {
  "main": {"startLifetime": {"mode": "TwoConstants", "min": 0.7, "max": 1.2}, "startSpeed": 1.5,
           "startSize": 0.35, "startColor": {"mode": "Color", "color": {"r": 1, "g": 0.4, "b": 0.05, "a": 1}}, "maxParticles": 300},
  "emission": {"rateOverTime": 40},
  "shape": {"shapeType": "Cone", "angle": 12, "radius": 0.15}}}}
```
