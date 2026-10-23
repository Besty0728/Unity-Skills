### particle_play_batch
`particle_play` for several systems: every item is validated first and nothing runs if any item is invalid; then the items run in order and each result reports what happened (not atomic: an item that ran stays played when a later one fails).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, withChildren?}`; at most 100 items, unknown keys rejected, no two items reaching the same system. |

**Item properties**: same fields as `particle_play` (`withChildren` default false).

**Returns**: `{success, totalItems, successCount, failCount, results, atomic: false, undoable: false, executedCount}`; a run-time failure reports the failed items, resend only those.
