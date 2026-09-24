### batch_set_render_layer
Preview setting the layer of the queried GameObjects. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `layer` | string | Yes | - | Target layer name (must already exist in Tags & Layers; not an index) |
| `recursive` | bool | No | false | Apply recursively to children |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. Commit with `batch_execute(confirmToken)`.
