---
name: unity-ui
description: "Unity UI creation. Use when users want to create Canvas, Button, Text, Image, or other UI elements. Triggers: UI, canvas, button, text, image, panel, slider, toggle, UGUI, 界面, 按钮, 文本, 面板."
---

# Unity UI Skills

> **BATCH-FIRST**: Use `ui_create_batch` when creating 2+ UI elements.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `ui_create_canvas` is the correct name — NOT `ui_add_canvas`
- `ui_create_label` does not exist → use `ui_create_text`
- `ui_create_checkbox` does not exist → use `ui_create_toggle`
- `ui_set_color` does not exist → use `component_set_property` on Image/Text component
- Do not confuse UGUI (this module) with UI Toolkit (uitoolkit module)

**Routing**:
- For UI Toolkit (UXML/USS) → use `uitoolkit` module
- For XR-compatible UI → use `xr` module's `xr_setup_ui_canvas` to convert Canvas to World Space
- For text updates → `ui_set_text` (this module)
- For layout arrangement → `ui_layout_children`, `ui_align_selected`, `ui_distribute_selected`

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
- `ui_set_image` - Set Image properties (type/fill/sprite)
- `ui_add_layout_element` - Add/configure LayoutElement
- `ui_add_canvas_group` - Add/configure CanvasGroup
- `ui_add_mask` - Add Mask or RectMask2D
- `ui_add_outline` - Add Shadow/Outline effect
- `ui_configure_selectable` - Configure Selectable colors/transition/navigation

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

**Supported types**: Button, Text, Image, Panel, Slider, Toggle, InputField, Dropdown, ScrollView, RawImage, Scrollbar

**Returns**: `{success, totalItems, successCount, failCount, results: [{success, name, type, instanceId}]}`

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

## New Element Creation Skills

### ui_create_dropdown
Create a Dropdown with options list and full Template/ScrollRect hierarchy.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Dropdown" | Dropdown name |
| `parent` | string | No | null | Parent object |
| `options` | string | No | "Option A,Option B,Option C" | Comma-separated options |
| `width/height` | float | No | 160/30 | Size |

### ui_create_scrollview
Create a ScrollRect with Viewport, RectMask2D, and Content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "ScrollView" | Name |
| `parent` | string | No | null | Parent object |
| `width/height` | float | No | 300/200 | Size |
| `horizontal` | bool | No | false | Enable horizontal scroll |
| `vertical` | bool | No | true | Enable vertical scroll |
| `movementType` | string | No | "Elastic" | Unrestricted/Elastic/Clamped |

### ui_create_rawimage
Create a RawImage element (for Texture2D/RenderTexture).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "RawImage" | Name |
| `parent` | string | No | null | Parent object |
| `texturePath` | string | No | null | Texture asset path |
| `width/height` | float | No | 100 | Size |

### ui_create_scrollbar
Create a standalone Scrollbar.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Scrollbar" | Name |
| `parent` | string | No | null | Parent object |
| `direction` | string | No | "BottomToTop" | LeftToRight/RightToLeft/BottomToTop/TopToBottom |
| `value` | float | No | 0 | Initial value (0-1) |
| `size` | float | No | 0.2 | Handle size (0-1) |
| `numberOfSteps` | int | No | 0 | 0=continuous, >0=discrete |

## Property Configuration Skills

### ui_set_image
Set Image advanced properties (type, fill, sprite).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Image element name |
| `instanceId` | int | No* | 0 | Instance ID |
| `type` | string | No | — | Simple/Sliced/Tiled/Filled |
| `fillMethod` | string | No | — | Radial360/Radial180/Radial90/Horizontal/Vertical |
| `fillAmount` | float | No | — | 0-1 fill amount |
| `fillClockwise` | bool | No | — | Fill direction |
| `preserveAspect` | bool | No | — | Preserve aspect ratio |
| `spritePath` | string | No | — | Sprite asset path |

### ui_add_layout_element
Add/configure LayoutElement constraints.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Element name |
| `minWidth/minHeight` | float | No | — | Minimum size |
| `preferredWidth/preferredHeight` | float | No | — | Preferred size |
| `flexibleWidth/flexibleHeight` | float | No | — | Flexible size (0=fixed, >0=grow) |
| `ignoreLayout` | bool | No | — | Ignore layout group |
| `layoutPriority` | int | No | — | Priority (higher overrides) |

### ui_add_canvas_group
Add/configure CanvasGroup (alpha, interactable, blocksRaycasts).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Element name |
| `alpha` | float | No | — | Group alpha (0-1) |
| `interactable` | bool | No | — | Children interactable |
| `blocksRaycasts` | bool | No | — | Blocks raycasts |
| `ignoreParentGroups` | bool | No | — | Ignore parent groups |

### ui_add_mask
Add Mask (stencil-based) or RectMask2D (rect clipping).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Element name |
| `maskType` | string | No | "RectMask2D" | Mask/RectMask2D |
| `showMaskGraphic` | bool | No | true | Show mask graphic (Mask only) |

### ui_add_outline
Add Shadow or Outline visual effect.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Element name |
| `effectType` | string | No | "Outline" | Shadow/Outline |
| `r/g/b/a` | float | No | 0,0,0,0.5 | Effect color |
| `distanceX/distanceY` | float | No | 1/-1 | Effect offset |
| `useGraphicAlpha` | bool | No | true | Use graphic alpha |

### ui_configure_selectable
Configure Selectable properties on Button/Toggle/Slider/etc.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Element name |
| `transition` | string | No | — | None/ColorTint/SpriteSwap/Animation |
| `interactable` | bool | No | — | Interactable state |
| `navigationMode` | string | No | — | None/Horizontal/Vertical/Automatic/Explicit |
| `normalR/G/B` | float | No | — | Normal state color |
| `highlightedR/G/B` | float | No | — | Highlighted state color |
| `pressedR/G/B` | float | No | — | Pressed state color |
| `disabledR/G/B` | float | No | — | Disabled state color |
| `colorMultiplier` | float | No | — | Color multiplier (1-5) |
| `fadeDuration` | float | No | — | Fade duration |

## Best Practices

1. Always create Canvas first
2. Use Panels to organize related elements
3. Use meaningful names for scripting access
4. Set parent for proper hierarchy
5. WorldSpace canvas for 3D UI (health bars, etc.)
