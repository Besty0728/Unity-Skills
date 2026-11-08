### ui_add_canvas_group
Add or configure CanvasGroup on a UI element (alpha, interactable, blocksRaycasts).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `alpha` | float | No | null | 0-1 float (not 0-255). |
| `interactable` | bool | No | null | Interactable |
| `blocksRaycasts` | bool | No | null | Blocks raycasts |
| `ignoreParentGroups` | bool | No | null | Ignore parent groups |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
