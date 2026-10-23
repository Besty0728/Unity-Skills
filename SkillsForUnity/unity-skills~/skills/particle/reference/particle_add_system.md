### particle_add_system
Add a ParticleSystem (Unity adds its ParticleSystemRenderer) to an existing object that has none, optionally configured by configJson; an object that already has a system is refused, never reset. Like `particle_create`, playOnAwake becomes false and the default particle material is assigned.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | null | Target by name |
| `instanceId` | int | No* | 0 | Target by instance id (older Unity) |
| `path` | string | No* | null | Target by hierarchy path |
| `configJson` | string | No | null | Optional initial configuration, same shape as `particle_configure`'s configJson. |

*Exactly one locator (`entityId`, `name`, `path` or `instanceId`) identifies the target; an id that names nothing is an error, never a fallback to the name.

**Returns**: `{success, name, entityId, instanceId, path, particleSystem, renderer, rendererCreated, material, defaultsApplied, applied, state, warnings, adjustments?}`
