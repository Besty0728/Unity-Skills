### qframework_query_api_docs
Search QFramework's built-in API documentation attributes (`ClassAPIAttribute`, `MethodAPIAttribute`, `PropertyAPIAttribute` with `APIDescriptionCN` / `EN` and `APIExampleCode`) across loaded assemblies.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| search | string | No | null | Substring of the class full name, `DisplayMenuName` or member names (case-insensitive) |
| groupName | string | No | null | Exact `ClassAPIAttribute.GroupName` (case-insensitive) |
| className | string | No | null | Substring of the type name or `DisplayClassName` |
| limit | int | No | 50 | Maximum classes, clamped to 1-500 |

**Returns:** `count`, `truncated`, `classes`
