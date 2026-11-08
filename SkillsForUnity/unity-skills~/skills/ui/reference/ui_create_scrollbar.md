### ui_create_scrollbar
Create a standalone Scrollbar UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Scrollbar" | Name of the new element |
| `parent` | string | No | null | Parent GameObject name |
| `direction` | string | No | "BottomToTop" | LeftToRight, RightToLeft, TopToBottom or BottomToTop (case-insensitive); also selects the bar's long axis (horizontal vs vertical). |
| `value` | float | No | 0 | Value |
| `size` | float | No | 0.2 | Size |
| `numberOfSteps` | int | No | 0 | Number of steps |
| `parentEntityId` | string | No | null | Parent entity ID; outranks `parent` |
