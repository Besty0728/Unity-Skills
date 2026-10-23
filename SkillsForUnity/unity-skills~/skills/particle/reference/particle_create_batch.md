### particle_create_batch
Create several ParticleSystem objects in one all-or-nothing call: every item is validated first and nothing is created if any item is invalid.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of `{name?, x?, y?, z?, rotX?, rotY?, rotZ?, space?, parentName|parentEntityId|parentInstanceId|parentPath?, configJson?}`; at most 100 items, unknown keys rejected; parents must already exist. |

**Item properties**: same fields and defaults as `particle_create`.

**Returns**: `{success, totalItems, successCount, failCount, results}`; all-or-nothing, results of the valid items of an invalid batch report `validated: true, applied: false`.
