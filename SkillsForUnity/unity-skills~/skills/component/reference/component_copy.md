### component_copy
Copy a component from one GameObject to another (pasted as a new component).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceName` | string | No* | null | Source GameObject name |
| `sourceInstanceId` | int | No* | 0 | Source Instance ID |
| `sourcePath` | string | No* | null | Source hierarchy path |
| `targetName` | string | No* | null | Target GameObject name |
| `targetInstanceId` | int | No* | 0 | Target Instance ID |
| `targetPath` | string | No* | null | Target hierarchy path |
| `componentType` | string | Yes | - | Component type to copy |

*At least one source identifier and one target identifier required

**Returns**: `{success, source, target, componentType}`; `source` / `target` echo the names you passed (null when you used ids or paths). Use `component_copy_exact` when the copy must be verified.
