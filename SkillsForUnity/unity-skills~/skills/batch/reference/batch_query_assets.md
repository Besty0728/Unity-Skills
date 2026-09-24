### batch_query_assets
Query project assets by type, folder, file-name pattern and label (read-only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | No | null | Raw Unity AssetDatabase filter string |
| `folder` | string | No | "Assets" | Search root folder |
| `typeFilter` | string | No | null | Asset type (prefix `t:` optional, e.g. `Texture2D`) |
| `namePattern` | string | No | null | Case-insensitive regex for filename |
| `labelFilter` | string | No | null | Asset label (prefix `l:` optional) |
| `maxResults` | int | No | 200 | Max results returned |

**Returns**: `{success, count, totalMatched, summary, filter, folder, assets: [{path, name, type, guid}]}`; an invalid `namePattern` regex fails the call.
