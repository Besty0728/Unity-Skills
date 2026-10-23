### particle_set_renderer_batch
`particle_set_renderer` for several systems in one all-or-nothing call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, configJson}`; at most 100 items, one item per system. |

**Item properties**: same fields as `particle_set_renderer` (`configJson` = `{"schemaVersion": 1, "renderer": {...}}`).

**Returns**: `{success, totalItems, successCount, failCount, results}`; all-or-nothing.
