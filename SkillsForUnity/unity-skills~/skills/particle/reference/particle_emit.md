### particle_emit
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `count` | int | Yes | - | 1-10000 particles |

**Returns**: `{name, entityId, instanceId, path, action, requested, emitted, particleCount, playback, systems, undoable, warnings}`; `emitted` < `requested` when `maxParticles` is reached.
