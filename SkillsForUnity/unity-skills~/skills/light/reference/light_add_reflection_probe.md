### light_add_reflection_probe
Create a Reflection Probe at a position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `probeName` | string | No | "ReflectionProbe" | Probe name |
| `x`, `y`, `z` | float | No | 0, 1, 0 | Position |
| `sizeX`, `sizeY`, `sizeZ` | float | No | 10, 10, 10 | Probe box size |
| `resolution` | int | No | 256 | Cubemap resolution |

**Returns:** `{ success, name, instanceId, resolution, size }`
