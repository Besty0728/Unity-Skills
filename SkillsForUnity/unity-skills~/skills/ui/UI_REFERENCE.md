---
name: unity-ui-reference
description: "Extended UGUI reference — fuller UGUI examples, element/property details, and a longer menu-building workflow beyond the ui SKILL.md. UGUI 扩展参考(更完整的 UGUI 示例、元素/属性细节、超出 ui SKILL.md 的菜单搭建流程)。"
type: reference
---

# UGUI Reference

Load this file when you need fuller UGUI examples, extended element/property details, or a longer menu-building workflow. The main `SKILL.md` keeps only routing and core summaries.

## Efficient Menu Example

```python
import unity_skills
import json

unity_skills.call_skill("ui_create_canvas", name="MainMenu")
unity_skills.call_skill("ui_create_panel", name="MenuPanel", parent="MainMenu", a=0.65)
unity_skills.call_skill("ui_set_rect", name="MenuPanel", width=300, height=200)
unity_skills.call_skill("ui_create_batch", items=json.dumps([
    {"type": "Button", "name": "StartBtn", "parent": "MenuPanel", "text": "Start", "width": 220, "height": 44},
    {"type": "Button", "name": "OptionsBtn", "parent": "MenuPanel", "text": "Options", "width": 220, "height": 44},
    {"type": "Button", "name": "QuitBtn", "parent": "MenuPanel", "text": "Quit", "width": 220, "height": 44}
]))
unity_skills.call_skill("ui_layout_children", name="MenuPanel", layoutType="Vertical", spacing=12)
```

## TextMeshPro Support

UI creation auto-detects TextMeshPro:
- With TMP installed: uses `TextMeshProUGUI`
- Without TMP: falls back to legacy `UnityEngine.UI.Text`

## Extended Create Skills

### Dropdown

| Parameter | Description |
|-----------|-------------|
| `name` | Dropdown name |
| `parent` | Parent object |
| `options` | Comma-separated options |
| `width/height` | Rect size |

### ScrollView

| Parameter | Description |
|-----------|-------------|
| `name` | ScrollView name |
| `parent` | Parent object |
| `width/height` | Size |
| `horizontal` | Enable horizontal scroll |
| `vertical` | Enable vertical scroll |
| `movementType` | `Unrestricted`, `Elastic`, `Clamped` |

### RawImage

| Parameter | Description |
|-----------|-------------|
| `name` | Element name |
| `parent` | Parent object |
| `texturePath` | Texture asset path |
| `width/height` | Size |

### Scrollbar

| Parameter | Description |
|-----------|-------------|
| `name` | Element name |
| `parent` | Parent object |
| `direction` | `LeftToRight`, `RightToLeft`, `BottomToTop`, `TopToBottom` |
| `value` | Initial value |
| `size` | Handle size |
| `numberOfSteps` | `0` for continuous |

## Property Skills

### `ui_set_image`

Useful fields:
- `type`: `Simple`, `Sliced`, `Tiled`, `Filled`
- `fillMethod`: `Radial360`, `Radial180`, `Radial90`, `Horizontal`, `Vertical`
- `fillAmount`
- `fillClockwise`
- `preserveAspect`
- `spritePath`

### `ui_add_layout_element`

Useful fields:
- `minWidth`, `minHeight`
- `preferredWidth`, `preferredHeight`
- `flexibleWidth`, `flexibleHeight`
- `ignoreLayout`
- `layoutPriority`

### `ui_add_canvas_group`

Useful fields:
- `alpha`
- `interactable`
- `blocksRaycasts`
- `ignoreParentGroups`

### `ui_add_mask`

Useful fields:
- `maskType`: `Mask` or `RectMask2D`
- `showMaskGraphic`

### `ui_add_outline`

Useful fields:
- `effectType`: `Shadow` or `Outline`
- `r/g/b/a`
- `distanceX`, `distanceY`
- `useGraphicAlpha`

### `ui_configure_selectable`

Useful fields:
- `transition`
- `interactable`
- `navigationMode`
- `normalR/G/B`
- `highlightedR/G/B`
- `pressedR/G/B`
- `disabledR/G/B`
- `colorMultiplier`
- `fadeDuration`

## Best Practices

1. Create the Canvas before child controls.
2. Use panels to separate logical UI groups.
3. Use meaningful names for later scripting access.
4. Prefer layout groups and anchors over manual pixel nudging when building reusable UI.
5. For 3D or VR-facing UI, convert the Canvas after creation rather than hand-building a world-space hierarchy from scratch.
