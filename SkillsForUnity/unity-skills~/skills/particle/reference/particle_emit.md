### particle_emit
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `count` | int | Yes | - | 1-10000 particles |

**Returns**: `{name, entityId, instanceId, path, action, requested, emitted, particleCount, playback, systems, undoable, warnings}`; `emitted` < `requested` when `maxParticles` is reached.
