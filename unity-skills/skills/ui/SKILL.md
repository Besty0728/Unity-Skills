---
name: unity-ui
description: "Unity UI creation. Use when users want to create Canvas, Button, Text, Image, or other UI elements. Triggers: UI, canvas, button, text, image, panel, slider, toggle, UGUI, 界面, 按钮, 文本, 面板."
---

# Unity UI Skills

> **BATCH-FIRST**: Use `ui_create_batch` when creating 2+ UI elements.

## Skills Overview

| Single Object | Batch Version | Use Batch When |
|---------------|---------------|----------------|
| `ui_create_*` | `ui_create_batch` | Creating 2+ UI elements |

**Query/Utility Skills**:
- `ui_set_text` - Update text content
- `ui_find_all` - Find UI elements
- `ui_set_rect` - Set RectTransform size/position
- `ui_set_anchor` - Set anchor preset
- `ui_layout_children` - Arrange children in layout
- `ui_align_selected` - Align selected elements
- `ui_distribute_selected` - Distribute selected elements

---

## Single-Object Skills

### ui_create_canvas
Create a UI Canvas container.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Canvas" | Canvas name |
| `renderMode` | string | No | "ScreenSpaceOverlay" | ScreenSpaceOverlay/ScreenSpaceCamera/WorldSpace |

### ui_create_panel
Create a Panel container.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Panel" | Panel name |
| `parent` | string | No | null | Parent Canvas/object |
| `r/g/b/a` | float | No | 1,1,1,0.5 | Background color |
| `width/height` | float | No | 200 | Size in pixels |

### ui_create_button
Create a Button.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Button" | Button name |
| `parent` | string | No | null | Parent object |
| `text` | string | No | "Button" | Button label |
| `width/height` | float | No | 160/30 | Size |
| `x/y` | float | No | 0 | Position offset |

### ui_create_text
Create a Text element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Text" | Text name |
| `parent` | string | No | null | Parent object |
| `text` | string | No | "Text" | Content |
| `fontSize` | int | No | 24 | Font size |
| `r/g/b/a` | float | No | 0,0,0,1 | Text color |
| `width/height` | float | No | 200/50 | Size |

### ui_create_image
Create an Image element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Image" | Image name |
| `parent` | string | No | null | Parent object |
| `spritePath` | string | No | null | Sprite asset path |
| `r/g/b/a` | float | No | 1,1,1,1 | Tint color |
| `width/height` | float | No | 100 | Size |

### ui_create_inputfield
Create an InputField.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "InputField" | Field name |
| `parent` | string | No | null | Parent object |
| `placeholder` | string | No | "Enter text..." | Placeholder |
| `width/height` | float | No | 200/30 | Size |

### ui_create_slider
Create a Slider.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Slider" | Slider name |
| `parent` | string | No | null | Parent object |
| `minValue` | float | No | 0 | Minimum value |
| `maxValue` | float | No | 1 | Maximum value |
| `value` | float | No | 0.5 | Initial value |
| `width/height` | float | No | 160/20 | Size |

### ui_create_toggle
Create a Toggle/Checkbox.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Toggle" | Toggle name |
| `parent` | string | No | null | Parent object |
| `label` | string | No | "Toggle" | Label text |
| `isOn` | bool | No | false | Initial state |

### ui_set_text
Update text content.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Text object name |
| `text` | string | Yes | New content |

### ui_find_all
Find UI elements in scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `uiType` | string | No | null | Filter: Button/Text/Image/etc. |
| `limit` | int | No | 100 | Max results |

### ui_set_rect
Set RectTransform size, position, and padding.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | UI element name |
| `instanceId` | int | No* | Instance ID |
| `width`, `height` | float | No | Size |
| `x`, `y` | float | No | Position |
| `left`, `right`, `top`, `bottom` | float | No | Padding offsets |

### ui_set_anchor
Set anchor preset for a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | UI element name |
| `instanceId` | int | No* | - | Instance ID |
| `preset` | string | No | "MiddleCenter" | Anchor preset |
| `setPivot` | bool | No | true | Also set pivot |

**Presets**: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight, StretchHorizontal, StretchVertical, StretchAll

### ui_layout_children
Arrange child UI elements in a layout.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | Parent element name |
| `instanceId` | int | No* | - | Instance ID |
| `layout` | string | No | "Vertical" | Layout type |
| `spacing` | float | No | 10 | Spacing between elements |

**Layout types**: Vertical, Horizontal, Grid

### ui_align_selected
Align selected UI elements.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `alignment` | string | No | "Center" | Alignment type |

**Alignments**: Left, Center, Right, Top, Middle, Bottom

### ui_distribute_selected
Distribute selected UI elements evenly.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `direction` | string | No | "Horizontal" | Distribution direction |

---

## Batch Skill

### ui_create_batch
Create multiple UI elements in one call.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `items` | array | Yes | Array of UI element configs |

**Item properties**: `type` (required), `name`, `parent`, `text`, `width`, `height`, `x`, `y`, `r`, `g`, `b`, `a`, etc.

**Supported types**: Button, Text, Image, Panel, Slider, Toggle, InputField

```python
unity_skills.call_skill("ui_create_batch", items=[
    {"type": "Button", "name": "StartBtn", "parent": "MenuPanel", "text": "Start", "y": 60},
    {"type": "Button", "name": "OptionsBtn", "parent": "MenuPanel", "text": "Options", "y": 0},
    {"type": "Button", "name": "QuitBtn", "parent": "MenuPanel", "text": "Quit", "y": -60}
])
```

---

## Example: Efficient Menu Creation

```python
import unity_skills

# BAD: 5 API calls
unity_skills.call_skill("ui_create_canvas", name="MainMenu")
unity_skills.call_skill("ui_create_panel", name="MenuPanel", parent="MainMenu")
unity_skills.call_skill("ui_create_button", name="StartBtn", parent="MenuPanel", text="Start", y=60)
unity_skills.call_skill("ui_create_button", name="OptionsBtn", parent="MenuPanel", text="Options", y=0)
unity_skills.call_skill("ui_create_button", name="QuitBtn", parent="MenuPanel", text="Quit", y=-60)

# GOOD: 2 API calls
unity_skills.call_skill("ui_create_canvas", name="MainMenu")
unity_skills.call_skill("ui_create_batch", items=[
    {"type": "Panel", "name": "MenuPanel", "parent": "MainMenu", "width": 300, "height": 200},
    {"type": "Button", "name": "StartBtn", "parent": "MenuPanel", "text": "Start", "y": 60},
    {"type": "Button", "name": "OptionsBtn", "parent": "MenuPanel", "text": "Options", "y": 0},
    {"type": "Button", "name": "QuitBtn", "parent": "MenuPanel", "text": "Quit", "y": -60}
])
```

## TextMeshPro Support

UI Skills auto-detect TextMeshPro:
- **With TMP**: Uses `TextMeshProUGUI`
- **Without TMP**: Falls back to legacy `UnityEngine.UI.Text`

Response includes `usingTMP` field to indicate which was used.

## Best Practices

1. Always create Canvas first
2. Use Panels to organize related elements
3. Use meaningful names for scripting access
4. Set parent for proper hierarchy
5. WorldSpace canvas for 3D UI (health bars, etc.)
