### particle_simulate_batch
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | `[{entityId|name|path|instanceId, seconds, restart?, withChildren?, fixedTimeStep?}]`, at most 100 |
| `runAsync` | bool | No | false | One sliced job for every item |
