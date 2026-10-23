### particle_set_sub_emitters
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `subEmittersJson` | string | Yes | - | Complete list, at most 16: `[{system, type, properties?, emitProbability?}]`; `[]` clears |

`system` is `{entityId}` (or `name` / `path` / `instanceId`, or a path/name string) of a ParticleSystem in the parent's scene; `type` is `Birth`, `Collision`, `Death`, `Trigger` or `Manual`; `properties` lists `InheritColor`, `InheritSize`, `InheritRotation`, `InheritLifetime`, `InheritDuration` (`[]` inherits nothing, `["InheritEverything"]` all five). The system itself, cycles (judged on the graph the whole call or batch leaves behind) and a repeated system+type are refused; a sub emitter outside the parent's hierarchy is only warned about, never reparented.

**Returns**: `{name, entityId, instanceId, path, enabled, count, subEmitters, warnings}`
