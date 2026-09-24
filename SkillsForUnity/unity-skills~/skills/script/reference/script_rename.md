### script_rename
Rename a script file in place.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `newName` | string | Yes | - | New script name (without extension or path separators) |
| `checkCompile` | bool | No | true | Check compilation after rename |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

Only the file is renamed, not the class inside it: a MonoBehaviour or ScriptableObject stays unbound until the class name matches again (use `script_replace`).

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability, oldPath, newName}` (`path` = the new path).
