### batch_replace_material
Preview replacing materials on the queried objects' Renderers. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `materialPath` | string | Yes | - | Replacement material asset path (assigned as `sharedMaterial`) |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. Commit with `batch_execute(confirmToken)`. Same builder as `batch_preview_replace_material`.
