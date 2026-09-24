### batch_query_components
Query objects by component with the unified batch filters (read-only); `componentType` narrows the result.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object (keys as in `batch_query_gameobjects`) |
| `componentType` | string | No | null | Optional component type constraint (overrides the query's `componentType`) |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

**Returns**: `{success, count, summary, query, objects: [{name, entityId, instanceId, path, components}]}` (`components` = type names).
