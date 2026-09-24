### script_list
List C# script files under a folder.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `folder` | string | No | "Assets" | Folder to search in |
| `filter` | string | No | null | Case-sensitive substring of the path |
| `limit` | int | No | 100 | Max results |

**Returns**: `{count, scripts: [{path, name}]}`
