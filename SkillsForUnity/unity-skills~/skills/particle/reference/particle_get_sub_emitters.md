### particle_get_sub_emitters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |

**Returns**: `{name, entityId, instanceId, path, enabled, count, subEmitters: [{system: {name, entityId, instanceId, path} | null, type, properties, emitProbability, missing?}], warnings}`
