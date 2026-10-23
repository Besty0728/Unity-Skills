### particle_emit_batch
`particle_emit` for several systems: every item is validated first (at most 100000 particles per call) and nothing runs if any item is invalid; then the items run in order without a rollback. Not idempotent: never resend the whole batch after a timeout.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, count}`; at most 100 items, one item per system. |

**Item properties**: same fields as `particle_emit` (`count` 1-10000 per item).

**Returns**: `{success, totalItems, successCount, failCount, results, atomic: false, undoable: false, executedCount}`; a run-time failure reports the failed items, resend only those.
