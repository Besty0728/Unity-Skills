### particle_configure_batch
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | `[{entityId|name|path|instanceId, configJson}]`, at most 100, one item per system |

**Returns**: the standard batch envelope `{success, totalItems, successCount, failCount, results, rolledBack?, revertedCount?}`; any invalid item means nothing is applied.
