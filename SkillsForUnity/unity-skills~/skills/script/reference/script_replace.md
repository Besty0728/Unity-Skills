### script_replace
Find and replace inside one script file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `find` | string | Yes | - | Literal, case-sensitive text (every occurrence), or a .NET regex with `isRegex` |
| `replace` | string | No | - | Replacement text; omit to delete every match. With `isRegex`, `$1` / `${name}` substitutions apply |
| `isRegex` | bool | No | false | Use regex matching |
| `checkCompile` | bool | No | true | Check compilation after replace |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability, replacements}`. `replacements: 0` means nothing matched, yet the file is still rewritten and recompiled.
