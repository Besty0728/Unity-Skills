### batch_cleanup_temp_objects
Preview deleting temporary helper objects matched by temp-name patterns. Execute with `batch_execute(confirmToken)`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `patternsCsv` | string | No | null | Comma- or semicolon-separated, case-insensitive name substrings; default `temp`, `tmp`, `preview`, `_copy`, `(clone)` |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, totalMatched, truncated, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. `totalMatched` is every object matching `queryJson` and `truncated` is `true` when `queryJson.limit` (500) left some out of the preview: `batch_execute` then changes only the previewed ones, and `summary` says how many were left out. Commit with `batch_execute(confirmToken)`. Executing it deletes the matched objects: review `sampleChanges` first.
