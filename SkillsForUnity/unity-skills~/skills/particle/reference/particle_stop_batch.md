### particle_stop_batch
`particle_stop` for several systems: every item is validated first and nothing runs if any item is invalid; then the items run in order, each result reporting what happened (not atomic).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, withChildren?, stopBehavior?}`; at most 100 items, no two items reaching the same system. |

**Item properties**: same fields as `particle_stop` (`withChildren` default false, `stopBehavior` default `StopEmittingAndClear`).

**Returns**: `{success, totalItems, successCount, failCount, results, atomic: false, undoable: false, executedCount}`.
