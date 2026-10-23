### particle_set_bursts_batch
`particle_set_bursts` for several systems in one all-or-nothing call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, burstsJson}`; at most 100 items, one item per system. |

**Item properties**: same fields as `particle_set_bursts` (`burstsJson`: complete list, at most 8; `[]` clears).

**Returns**: `{success, totalItems, successCount, failCount, results}`; all-or-nothing.
