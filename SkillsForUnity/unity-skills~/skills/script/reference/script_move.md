### script_move
Move a script to another folder.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script asset path |
| `newFolder` | string | Yes | - | Destination folder under `Assets/` or `Packages/`; created (and registered in AssetDatabase) if missing |
| `checkCompile` | bool | No | true | Check compilation after move |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability, oldPath, newPath}`
