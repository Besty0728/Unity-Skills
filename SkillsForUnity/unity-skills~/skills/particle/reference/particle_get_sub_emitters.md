### particle_get_sub_emitters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |

**Returns**: `{name, entityId, instanceId, path, enabled, count, subEmitters: [{system: {name, entityId, instanceId, path} | null, type, properties, emitProbability, missing?}], warnings}`
