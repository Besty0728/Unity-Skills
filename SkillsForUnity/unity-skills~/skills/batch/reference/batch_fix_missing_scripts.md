### batch_fix_missing_scripts
Preview removing missing-script components from the queried objects. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, totalMatched, truncated, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. `totalMatched` is every object matching `queryJson` and `truncated` is `true` when `queryJson.limit` (500) left some out of the preview: `batch_execute` then changes only the previewed ones, and `summary` says how many were left out. Commit with `batch_execute(confirmToken)`.
