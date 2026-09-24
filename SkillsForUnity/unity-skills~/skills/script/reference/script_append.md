### script_append
Insert lines into a script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `content` | string | Yes | - | Content to insert |
| `atLine` | int | No | -1 | 0-based line index to insert before; -1 or out of range = before the last line that is only `}` (else at the end) |
| `checkCompile` | bool | No | true | Check compilation after append |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

The default lands inside the class only when the file has no namespace: in a namespaced file the last `}` closes the namespace, so pass `atLine` to insert into the class.

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability}`; GET `waitUrl` for the compile result.
