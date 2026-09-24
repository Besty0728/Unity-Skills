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

**Returns**: `{success, source, target, componentType, pasted}`: the resolved source and target object names, the resolved type name, and `pasted` (false when Unity did not add the copy, e.g. a type that disallows a second instance). The paste is undoable and tracked by workflow rollback. Use `component_copy_exact` when every field must be verified.
