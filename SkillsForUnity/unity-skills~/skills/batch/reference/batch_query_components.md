### batch_query_components
Query objects by component with the unified batch filters (read-only); `componentType` narrows the result.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `queryJson` | string | No | null | JSON filter object (keys as in `batch_query_gameobjects`) |
| `componentType` | string | No | null | Optional component type constraint (overrides the query's `componentType`); an unknown type is rejected with `similarTypes` |
| `sampleLimit` | int | No | 20 | Max sample objects returned |

**Returns**: `{success, count, totalMatched, truncated, summary, query, objects: [{name, entityId, instanceId, path, components}], warnings?}` (`components` = type names). `count` stops at `queryJson.limit`; `totalMatched` is every match before that cap and `truncated` is `true` when it cut the list. `queryJson` is validated, and `name` / `namePattern` / `caseSensitive` behave, as in `batch_query_gameobjects`.
