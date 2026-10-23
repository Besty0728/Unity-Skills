### particle_add_system_batch
`particle_add_system` for several objects in one all-or-nothing call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, configJson?}`; at most 100 items, one item per object. |

**Item properties**: same fields as `particle_add_system`.

**Returns**: `{success, totalItems, successCount, failCount, results}`; all-or-nothing.
