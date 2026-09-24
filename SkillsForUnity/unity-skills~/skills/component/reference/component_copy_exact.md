### component_copy_exact
Copy a component from one GameObject to another and verify that every serialized Inspector field matches.

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

**Returns**: `{success: true, source, target, componentType, copiedComponentIndex, verified: true, mismatchCount: 0}`. When fields differ after the paste the call still returns normally, not as an error: `{success: false, source, target, componentType, verified: false, mismatchCount, mismatches}`; check `verified`.
