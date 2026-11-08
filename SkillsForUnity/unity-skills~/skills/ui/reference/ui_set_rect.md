### ui_set_rect
Set RectTransform size, position, and padding (offsets).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `width` | float | No | null | Width |
| `height` | float | No | null | Height |
| `posX` | float | No | null | posX/posY: RectTransform.anchoredPosition, not world position; omitted axes keep their current value. |
| `posY` | float | No | null | Pos y |
| `left` | float | No | null | left/right/top/bottom: RectTransform offsets from each edge, meaningful mainly when anchors are stretched; omitted sides keep their value. |
| `right` | float | No | null | Right |
| `top` | float | No | null | Top |
| `bottom` | float | No | null | Bottom |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
