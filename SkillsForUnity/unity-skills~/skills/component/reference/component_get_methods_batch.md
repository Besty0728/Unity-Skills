### component_get_methods_batch
Discover method signatures for multiple types without invoking them.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of discovery requests |

**Item properties**: `componentType` (required), `isStatic` (false), `includePrivate` (true), `methodName` (null), `assembly` (null).

**Returns**: `{success, totalItems, successCount, failCount, results}`. Each successful item includes the `component_get_methods` result. Discovery errors are reported per item.
