### ui_add_outline
Add Shadow or Outline effect to a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `effectType` | string | No | "Outline" | Outline or Shadow (case-insensitive); any other value is rejected. |
| `r` | float | No | 0 | r/g/b/a: colour channels as 0-1 floats (not 0-255). |
| `g` | float | No | 0 | G |
| `b` | float | No | 0 | B |
| `a` | float | No | 0.5 | A |
| `distanceX` | float | No | 1 | Distance x |
| `distanceY` | float | No | -1 | Distance y |
| `useGraphicAlpha` | bool | No | true | Use graphic alpha |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
