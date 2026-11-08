### ui_create_batch
Create multiple UI elements (Efficient). items: JSON array of {type, name, parent, parentEntityId, text, width, height, ...}.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | string | Yes | - | type: canvas/panel/button/text/image/inputfield/slider/toggle/dropdown/scrollview/rawimage/scrollbar; other fields match that type's ui_create_* parameters. |
