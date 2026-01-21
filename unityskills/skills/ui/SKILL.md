---
name: unity-ui
description: Create UI elements (Canvas, Button, Text, etc.) in Unity Editor via REST API
---

# Unity UI Skills

Build user interfaces - menus, HUDs, dialogs, and interactive elements.

## Capabilities

- Create Canvas containers
- Create UI elements (Button, Text, Image, InputField, Slider, Toggle)
- Configure UI properties
- Find UI elements in scene

## Skills Reference

| Skill | Description |
|-------|-------------|
| `ui_create_canvas` | Create UI Canvas |
| `ui_create_panel` | Create Panel container |
| `ui_create_button` | Create Button |
| `ui_create_text` | Create Text element |
| `ui_create_image` | Create Image element |
| `ui_create_inputfield` | Create InputField |
| `ui_create_slider` | Create Slider |
| `ui_create_toggle` | Create Toggle/Checkbox |
| `ui_set_text` | Set text content |
| `ui_find_all` | Find UI elements |

## Canvas Render Modes

| Mode | Description |
|------|-------------|
| `ScreenSpaceOverlay` | UI always on top |
| `ScreenSpaceCamera` | UI rendered by camera |
| `WorldSpace` | UI in 3D world |

## Parameters

### ui_create_canvas

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Canvas" | Canvas name |
| `renderMode` | string | No | "ScreenSpaceOverlay" | Render mode |

### ui_create_panel

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Panel" | Panel name |
| `parent` | string | No | null | Parent Canvas/object |
| `r/g/b/a` | float | No | 1,1,1,0.5 | Background color |
| `width/height` | float | No | 200 | Size in pixels |

### ui_create_button

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Button" | Button name |
| `parent` | string | No | null | Parent object |
| `text` | string | No | "Button" | Button label |
| `width/height` | float | No | 160/30 | Size |
| `x/y` | float | No | 0 | Position offset |

### ui_create_text

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Text" | Text name |
| `parent` | string | No | null | Parent object |
| `text` | string | No | "Text" | Content |
| `fontSize` | int | No | 24 | Font size |
| `r/g/b/a` | float | No | 0,0,0,1 | Text color |
| `width/height` | float | No | 200/50 | Size |

### ui_create_image

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Image" | Image name |
| `parent` | string | No | null | Parent object |
| `spritePath` | string | No | null | Sprite asset path |
| `r/g/b/a` | float | No | 1,1,1,1 | Tint color |
| `width/height` | float | No | 100 | Size |

### ui_create_inputfield

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "InputField" | Field name |
| `parent` | string | No | null | Parent object |
| `placeholder` | string | No | "Enter text..." | Placeholder |
| `width/height` | float | No | 200/30 | Size |

### ui_create_slider

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Slider" | Slider name |
| `parent` | string | No | null | Parent object |
| `minValue` | float | No | 0 | Minimum value |
| `maxValue` | float | No | 1 | Maximum value |
| `value` | float | No | 0.5 | Initial value |
| `width/height` | float | No | 160/20 | Size |

### ui_create_toggle

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "Toggle" | Toggle name |
| `parent` | string | No | null | Parent object |
| `label` | string | No | "Toggle" | Label text |
| `isOn` | bool | No | false | Initial state |

### ui_set_text

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Text object name |
| `text` | string | Yes | New content |

### ui_find_all

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `uiType` | string | No | null | Filter by type |
| `limit` | int | No | 100 | Max results |

## Example Usage

```python
import unity_skills

# Create main canvas
unity_skills.call_skill("ui_create_canvas",
    name="MainMenu",
    renderMode="ScreenSpaceOverlay"
)

# Create title
unity_skills.call_skill("ui_create_text",
    name="Title",
    parent="MainMenu",
    text="My Awesome Game",
    fontSize=48,
    r=1, g=1, b=1
)

# Create menu panel
unity_skills.call_skill("ui_create_panel",
    name="MenuPanel",
    parent="MainMenu",
    width=300,
    height=400,
    r=0, g=0, b=0, a=0.8
)

# Create buttons
unity_skills.call_skill("ui_create_button",
    name="StartButton",
    parent="MenuPanel",
    text="Start Game",
    width=200,
    height=50,
    y=100
)

unity_skills.call_skill("ui_create_button",
    name="OptionsButton",
    parent="MenuPanel",
    text="Options",
    width=200,
    height=50,
    y=30
)

unity_skills.call_skill("ui_create_button",
    name="QuitButton",
    parent="MenuPanel",
    text="Quit",
    width=200,
    height=50,
    y=-40
)

# Create settings slider
unity_skills.call_skill("ui_create_slider",
    name="VolumeSlider",
    parent="MenuPanel",
    minValue=0,
    maxValue=100,
    value=80
)

# Create input field
unity_skills.call_skill("ui_create_inputfield",
    name="PlayerName",
    parent="MenuPanel",
    placeholder="Enter your name..."
)

# Update text dynamically
unity_skills.call_skill("ui_set_text",
    name="Title",
    text="Welcome Back!"
)

# Find all buttons
buttons = unity_skills.call_skill("ui_find_all",
    uiType="Button"
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "ui_create_button",
  "result": {
    "success": true,
    "name": "StartButton",
    "instanceId": 12345,
    "parent": "MenuPanel",
    "text": "Start Game",
    "usingTMP": true
  }
}
```

## TextMeshPro Support

UI Skills 动态检测 TextMeshPro 是否已安装：

| 项目配置 | 文本组件 |
|---------|---------|
| 有 TMP 包 | `TextMeshProUGUI` |
| 无 TMP 包 | Legacy `UnityEngine.UI.Text` |

返回值中的 `usingTMP` 字段指示使用了哪种组件。

## Best Practices

1. Always create Canvas first
2. Use Panels to organize related elements
3. Use meaningful names for scripting access
4. Set parent for proper hierarchy
5. Text components auto-detect TMP availability
6. WorldSpace canvas for 3D UI (health bars, etc.)
