### ui_layout_children
Arrange child UI elements in a layout (Vertical, Horizontal, Grid).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `layoutType` | string | No | "Vertical" | Layout type |
| `spacing` | float | No | 10 | Spacing |
| `paddingLeft` | float | No | 0 | Padding left |
| `paddingRight` | float | No | 0 | Padding right |
| `paddingTop` | float | No | 0 | Padding top |
| `paddingBottom` | float | No | 0 | Padding bottom |
| `gridColumns` | int | No | 3 | Grid columns |
| `childForceExpandWidth` | bool | No | false | Child force expand width |
| `childForceExpandHeight` | bool | No | false | Child force expand height |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
