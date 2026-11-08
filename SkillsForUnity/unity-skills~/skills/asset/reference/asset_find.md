### asset_find
Find assets by search filter.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `searchFilter` | string | Yes | - | Search query |
| `limit` | int | No | 50 | Max results to return |

**Search Filter Syntax**:
| Filter | Example | Description |
|--------|---------|-------------|
| `t:Type` | `t:Texture2D` | By type |
| `l:Label` | `l:Architecture` | By label |
| `name` | `player` | By name |
| Combined | `t:Material player` | Multiple filters |

**Returns**: `{count, totalFound, assets: [{path, name, type}]}`
