### particle_get_renderer
Read the ParticleSystemRenderer of a ParticleSystem: enabled, renderMode, material and mesh references, sorting, alignment and display fields, in the shape `particle_set_renderer` accepts.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | null | Target by name |
| `instanceId` | int | No* | 0 | Target by instance id (older Unity) |
| `path` | string | No* | null | Target by hierarchy path |

*Exactly one locator (`entityId`, `name`, `path` or `instanceId`) identifies the target; an id that names nothing is an error, never a fallback to the name.

**Returns**: `{success, name, entityId, instanceId, path, rendererEntityId, renderer}`
