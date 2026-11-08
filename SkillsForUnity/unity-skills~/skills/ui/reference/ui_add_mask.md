### ui_add_mask
Add Mask or RectMask2D to a UI element (maskType: Mask or RectMask2D).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `maskType` | string | No | "RectMask2D" | Mask type |
| `showMaskGraphic` | bool | No | true | Show mask graphic |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
