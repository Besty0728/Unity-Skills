### prefab_get_hierarchy
| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `prefabPath` | string | Yes | — | Exact .prefab asset path; only this asset is saved. |
| `limit` | int | No | 200 | Maximum returned records; positive. |

**Returns**: `{success, prefabPath, total, truncated, nodes}`. Inactive nodes are included; each node has the info fields above.
