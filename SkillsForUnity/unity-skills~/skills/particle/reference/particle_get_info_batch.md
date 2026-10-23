### particle_get_info_batch
`particle_get_info` for several systems in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, includeModules?}`; at most 100 items, unknown keys rejected. |

**Item properties**: same fields as `particle_get_info` (`name`, `entityId`, `instanceId`, `path`, `includeModules?`).

**Returns**: `{success, totalItems, successCount, failCount, results}`; each result is the `particle_get_info` result of that item stamped with `target`.
