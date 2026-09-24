### batch_preview_rename
Preview renaming the queried objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object |
| `mode` | string | No | "prefix" | `prefix`, `suffix`, `replace` (literal, case-sensitive `search` → `replacement`) or `regex_replace` (`regexPattern` → `regexReplacement`) |
| `prefix` | string | No | null | Prefix to add |
| `suffix` | string | No | null | Suffix to add |
| `search` | string | No | null | Plain text search term |
| `replacement` | string | No | null | Plain text replacement |
| `regexPattern` | string | No | null | Regex search pattern |
| `regexReplacement` | string | No | null | Regex replacement text |
| `sampleLimit` | int | No | 10 | Max preview items |

**Returns** (shared by every preview and fixer): `{success, status: "preview", confirmToken, kind, summary, riskLevel, rollbackAvailable, mayCreateJob, targetCount, executableCount, skipCount, sampleChanges: [{targetName, targetPath, entityId, action, before, after}], skipReasons: [{reason, count}], surfaceExclusion?}`. Commit with `batch_execute(confirmToken)`.
