### particle_get_bursts
Read the complete burst list of a ParticleSystem: time, count (MinMaxCurve), cycleCount, repeatInterval and probability of each burst, with the system's duration and loop.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | null | Target by name |
| `instanceId` | int | No* | 0 | Target by instance id (older Unity) |
| `path` | string | No* | null | Target by hierarchy path |

*Exactly one locator (`entityId`, `name`, `path` or `instanceId`) identifies the target; an id that names nothing is an error, never a fallback to the name.

**Returns**: `{success, name, entityId, instanceId, path, count, bursts, duration, loop, emissionEnabled}`
