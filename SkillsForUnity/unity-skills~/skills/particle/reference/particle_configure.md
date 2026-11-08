### particle_configure
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | null | GameObject name (`entityId` preferred) |
| `path` | string | No | null | Hierarchy path |
| `instanceId` | int | No | 0 | Instance ID (older Unity only) |
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `configJson` | string | Yes | - | `{"schemaVersion": 1, "system"?, "modules"?}` partial patch |

**Returns**: `{name, entityId, instanceId, path, applied: ["modules.main.duration", ...], state: {system?, modules?}, warnings, adjustments?}`
