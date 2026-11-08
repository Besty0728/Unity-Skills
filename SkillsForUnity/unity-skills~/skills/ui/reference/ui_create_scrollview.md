### ui_create_scrollview
Create a ScrollRect (ScrollView) UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "ScrollView" | Name of the new element |
| `parent` | string | No | null | Parent GameObject name |
| `width` | float | No | 300 | Width |
| `height` | float | No | 200 | Height |
| `horizontal` | bool | No | false | Horizontal |
| `vertical` | bool | No | true | Vertical |
| `movementType` | string | No | "Elastic" | Unrestricted, Elastic or Clamped (ScrollRect.MovementType, case-insensitive). |
| `parentEntityId` | string | No | null | Parent entity ID; outranks `parent` |
