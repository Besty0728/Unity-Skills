### batch_query_assets
Query project assets by type, folder, file-name pattern and label (read-only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | No | null | Raw Unity AssetDatabase filter string |
| `folder` | string | No | "Assets" | Search root folder |
| `typeFilter` | string | No | null | Asset type (prefix `t:` optional, e.g. `Texture2D`) |
| `namePattern` | string | No | null | Regex for the file name without extension; ignores case unless `caseSensitive` (inline `(?-i)` also works) |
| `labelFilter` | string | No | null | Asset label (prefix `l:` optional) |
| `maxResults` | int | No | 200 | Max results returned |
| `caseSensitive` | bool | No | false | `true` makes `namePattern` match case exactly |

**Returns**: `{success, count, totalMatched, truncated, summary, filter, folder, assets: [{path, name, type, guid}]}`. `count` stops at `maxResults`; `totalMatched` is every asset that passed the filters, `namePattern` included, and `truncated` is `true` when `maxResults` cut the list (the `summary` then reads `showing N of M`). An invalid `namePattern` regex fails the call.
