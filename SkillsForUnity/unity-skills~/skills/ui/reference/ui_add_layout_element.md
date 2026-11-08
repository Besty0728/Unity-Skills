### ui_add_layout_element
Add or configure LayoutElement on a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `minWidth` | float | No | null | Min width |
| `minHeight` | float | No | null | Min height |
| `preferredWidth` | float | No | null | Preferred width |
| `preferredHeight` | float | No | null | Preferred height |
| `flexibleWidth` | float | No | null | Flexible width |
| `flexibleHeight` | float | No | null | Flexible height |
| `ignoreLayout` | bool | No | null | Ignore layout |
| `layoutPriority` | int | No | null | Layout priority |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
