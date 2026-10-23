### particle_set_sub_emitters_batch
`particle_set_sub_emitters` for several systems in one all-or-nothing call; cycles are checked across the whole batch, so a rocket -> explosion -> sparks chain can be set in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name|entityId|instanceId|path, subEmittersJson}`; at most 100 items, one item per system. |

**Item properties**: same fields as `particle_set_sub_emitters` (`subEmittersJson`: complete list, at most 16; `[]` clears).

**Returns**: `{success, totalItems, successCount, failCount, results}`; all-or-nothing.
