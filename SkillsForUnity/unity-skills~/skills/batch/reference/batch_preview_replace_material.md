### batch_preview_replace_material
Preview replacing Renderer materials across the queried objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `materialPath` | string | Yes | - | Replacement material asset path (assigned as each Renderer's `sharedMaterial`) |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, totalMatched, truncated, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. `totalMatched` is every object matching `queryJson` and `truncated` is `true` when `queryJson.limit` (500) left some out of the preview: `batch_execute` then changes only the previewed ones, and `summary` says how many were left out. Commit with `batch_execute(confirmToken)`. Same builder as `batch_replace_material`.
