### particle_configure
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `configJson` | string | Yes | - | `{"schemaVersion": 1, "system"?, "modules"?}` partial patch |

**Returns**: `{name, entityId, instanceId, path, applied: ["modules.main.duration", ...], state: {system?, modules?}, warnings, adjustments?}`
