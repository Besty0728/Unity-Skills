### batch_standardize_naming
Preview standardizing object names by trimming whitespace and normalizing separators. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `separator` | string | No | "_" | Replaces whitespace, `-`, `/` and runs of `_`, then is trimmed from both ends |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. Commit with `batch_execute(confirmToken)`.
