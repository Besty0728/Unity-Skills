### prefab_add_component_batch
Grouped batch version of `prefab_add_component`. Each item contains `prefabPath` and the single-operation arguments. Items targeting the same asset execute in input order and save together. At most 50 items.

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `items` | string | Yes | — | Nonempty JSON array of items with prefabPath and the single-operation arguments. |

**Returns**: `{success, completed, totalItems, assetCount, countsUnit, successCount, failCount, results}`. completed means processing finished; success means all asset groups succeeded. totalItems counts input items; successCount/failCount count **assets**, indicated by countsUnit:"assets". Each results entry is an asset transaction containing its own operation results or structured error. Partial success has no top-level error, so successful assets and their Undo/Workflow history remain committed. Retry only failed asset groups.
