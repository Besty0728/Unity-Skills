---
name: unity-ui
description: Create and lay out Unity UGUI (Canvas-based UI) — Canvas, panels, buttons, text, images, and layout groups. Use when building UGUI screens, adding Canvas elements, or arranging UI layout, even if the user just says "做个UI" or "界面". 创建与布局 Unity UGUI(基于 Canvas 的 UI:Canvas、面板、按钮、文本、图片、布局组);当用户要搭建 UGUI 界面、添加 Canvas 元素、或排布 UI 布局时使用。
---

# Unity UI Skills

Use this module for Unity UGUI / Canvas workflows. It is separate from UI Toolkit.

> **Batch-first**: Prefer `ui_create_batch` when creating `2+` UI elements.

## Operating Mode

- **Approval**：查询类 skill（`ui_find_all`，源码标 `SkillMode.SemiAuto`）直接执行；其余创建/修改类（`ui_create_*` / `ui_set_*` / `ui_add_*` / `ui_layout_children` / `ui_align_selected` 等，标 `SkillMode.FullAuto`）需用户 grant，grant 后服务端一步执行返结果。
- **Auto / Bypass**：所有 skill 直接执行；Auto 走 AI 自我评估，Bypass 全放行。
- 本模块**不含** Delete / PlayMode / Reload / 高危 skill，无 Bypass-only 拦截项。删除 UI 节点请走 `gameobject` 模块。

**DO NOT** (common hallucinations):
- `ui_add_canvas` does not exist -> use `ui_create_canvas`
- `ui_create_label` does not exist -> use `ui_create_text`
- `ui_create_checkbox` does not exist -> use `ui_create_toggle`
- `ui_set_color` does not exist -> use `component_set_property` on `Image`/`Text`, or the dedicated UI property skills when available
- Do not confuse UGUI (`ui`) with UI Toolkit (`uitoolkit`)

**Routing**:
- For UXML/USS/UIDocument -> use `uitoolkit`
- For XR-compatible world-space Canvas conversion -> use `xr_setup_ui_canvas`
- For text updates after creation -> use `ui_set_text`
- For layout and alignment -> use `ui_layout_children`, `ui_align_selected`, `ui_distribute_selected`

## Skills

### Create Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `ui_create_canvas` | Create Canvas | `name?`, `renderMode?` |
| `ui_create_panel` | Create panel container | `name?`, `parent?`, `r/g/b/a?` |
| `ui_create_button` | Create button | `name?`, `parent?`, `text?`, `width/height?` |
| `ui_create_text` | Create text label | `name?`, `parent?`, `text?`, `fontSize?`, `r/g/b?` |
| `ui_create_image` | Create image | `name?`, `parent?`, `spritePath?`, `width/height?` |
| `ui_create_inputfield` | Create input field | `name?`, `parent?`, `placeholder?`, `width/height?` |
| `ui_create_slider` | Create slider | `name?`, `parent?`, `minValue?`, `maxValue?`, `value?` |
| `ui_create_toggle` | Create toggle | `name?`, `parent?`, `label?`, `isOn?` |
| `ui_create_dropdown` | Create dropdown | `name?`, `parent?`, `options?`, `width/height?` |
| `ui_create_scrollview` | Create ScrollRect hierarchy | `name?`, `parent?`, `width/height?`, `horizontal?`, `vertical?` |
| `ui_create_rawimage` | Create RawImage | `name?`, `parent?`, `texturePath?`, `width/height?` |
| `ui_create_scrollbar` | Create scrollbar | `name?`, `parent?`, `direction?`, `value?`, `size?` |
| `ui_create_batch` | Create multiple UI elements | `items` (`JSON` string array) |

### Query and Layout Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `ui_find_all` | Find scene UI elements | `uiType?`, `limit?` |
| `ui_set_text` | Update text content | `name`, `text` |
| `ui_set_rect` | Set RectTransform size/offsets | target, `width`, `height`, `posX`, `posY`, `left/right/top/bottom?` |
| `ui_get_rect_transform` | Read full RectTransform data | target |
| `ui_set_rect_transform` | Set full RectTransform data | anchors, pivot, offsets, local transform, width/height |
| `ui_set_rect_transform_batch` | Set full RectTransform data for multiple elements | `items` |
| `ui_set_anchor` | Apply anchor preset | target, `preset?`, `setPivot?` |
| `ui_layout_children` | Vertical/Horizontal/Grid layout | target, `layoutType?`, `spacing?` |
| `ui_align_selected` | Align current selection | `alignment?` |
| `ui_distribute_selected` | Distribute current selection | `direction?` |

### Property and Effect Skills

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `ui_set_image` | Image type/fill/sprite | target, `type?`, `fillMethod?`, `fillAmount?`, `spritePath?` |
| `ui_add_layout_element` | Add LayoutElement constraints | target, width/height prefs, flex values |
| `ui_add_canvas_group` | Add CanvasGroup | target, `alpha?`, `interactable?`, `blocksRaycasts?` |
| `ui_add_mask` | Add `Mask` or `RectMask2D` | target, `maskType?`, `showMaskGraphic?` |
| `ui_add_outline` | Add Shadow/Outline effect | target, `effectType?`, `r/g/b/a?`, `distanceX/Y?` |
| `ui_configure_selectable` | Configure transitions/navigation/colors | target, `transition?`, `navigationMode?`, color values |

## High-Frequency Defaults

### Canvas and Parenting

- `ui_create_canvas` defaults to `ScreenSpaceOverlay`.
- Most create skills accept `parent`; if omitted, Unity will create under the active Canvas or scene root depending on the implementation context.
- For reusable menu groups, create the Canvas once, then create a Panel and put all child controls under that panel.

### Common Create Parameters

| Skill | High-frequency fields |
|-------|-----------------------|
| `ui_create_button` | `text`, `width`, `height` |
| `ui_create_text` | `text`, `fontSize`, `r/g/b` |
| `ui_create_image` | `spritePath`, `width`, `height` |
| `ui_create_slider` | `minValue`, `maxValue`, `value` |
| `ui_create_toggle` | `label`, `isOn` |
| `ui_create_dropdown` | `options` |
| `ui_create_scrollview` | `horizontal`, `vertical`, `movementType` |

Important:
- Most create skills do **not** take explicit `x/y` placement.
- Create first, then place/anchor with `ui_set_rect`, `ui_set_anchor`, or `ui_layout_children`.

### Full RectTransform Editing

Use `ui_set_rect_transform` when you need Inspector-level RectTransform coverage instead of a preset.

| Skill | Parameters |
|-------|------------|
| `ui_get_rect_transform` | `name`, `instanceId`, `path` |
| `ui_set_rect_transform` | target + `anchorMinX/Y`, `anchorMaxX/Y`, `pivotX/Y`, `anchoredPosX/Y/Z`, `sizeDeltaX/Y`, `offsetMinX/Y`, `offsetMaxX/Y`, `localPosX/Y/Z`, `localRotX/Y/Z`, `localScaleX/Y/Z`, `width`, `height` |
| `ui_set_rect_transform_batch` | `items` JSON array with the same per-target fields |

### ui_get_rect_transform
Get full RectTransform data for a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | Hierarchy path |

**Returns:** `{ success, name, instanceId, path, anchorMin, anchorMax, pivot, anchoredPosition3D, sizeDelta, offsetMin, offsetMax, localPosition, localEulerAngles, localScale, rect }`

### ui_set_rect_transform
Set full RectTransform data for a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | null | Hierarchy path |
| `anchorMinX` / `anchorMinY` | float | No | null | Anchor min |
| `anchorMaxX` / `anchorMaxY` | float | No | null | Anchor max |
| `pivotX` / `pivotY` | float | No | null | Pivot |
| `anchoredPosX` / `anchoredPosY` / `anchoredPosZ` | float | No | null | Anchored position 3D |
| `sizeDeltaX` / `sizeDeltaY` | float | No | null | Size delta |
| `offsetMinX` / `offsetMinY` | float | No | null | Offset min |
| `offsetMaxX` / `offsetMaxY` | float | No | null | Offset max |
| `localPosX` / `localPosY` / `localPosZ` | float | No | null | Local position |
| `localRotX` / `localRotY` / `localRotZ` | float | No | null | Local euler rotation |
| `localScaleX` / `localScaleY` / `localScaleZ` | float | No | null | Local scale |
| `width` / `height` | float | No | null | Size with current anchors |

**Returns:** same shape as `ui_get_rect_transform`.

### ui_set_rect_transform_batch
Set full RectTransform data for multiple UI elements.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | json string | Yes | - | JSON array of per-item target and RectTransform fields |

**Returns:** `{ success, totalItems, successCount, failCount, results }`

### Layout and Anchoring Rules

- `ui_set_anchor` is the fastest way to move a control into a standard layout position.
- `ui_set_rect` is better for precise size/offset edits after anchoring.
- `ui_layout_children` is preferred over hand-positioning every child when building vertical, horizontal, or grid menus.

Anchor presets commonly used in production:
- `MiddleCenter` for modal/menu panels
- `TopLeft` or `TopRight` for HUD corners
- `StretchAll` for full-screen backgrounds

### TextMeshPro Note

Text creation auto-detects TMP:
- TMP available -> `TextMeshProUGUI`
- TMP unavailable -> legacy `Text`

Read the response payload if you need to know which one was created before later component-specific edits.

## Workflow Notes

1. Create a Canvas first.
2. Use panels to group related controls.
3. Prefer `ui_create_batch` for menus, HUD groups, and repeated widgets.
4. Use anchors and layout groups before hand-placing every child.
5. Text creation auto-detects TextMeshPro. Responses indicate whether TMP was used.
6. For world-space gameplay UI, build the Canvas here first, then convert for XR only if needed.
7. `ui_create_batch` is mainly for bulk creation, not precise positioning. Follow it with layout or rect/anchor adjustments.
8. `ui_create_batch.items` is a JSON string parameter in the current REST/API layer, not a raw array object.

## Minimal Example

```python
import unity_skills
import json

unity_skills.call_skill("ui_create_canvas", name="MainMenu")
unity_skills.call_skill("ui_create_panel", name="MenuPanel", parent="MainMenu", a=0.7)
unity_skills.call_skill("ui_set_rect", name="MenuPanel", width=320, height=240)
unity_skills.call_skill("ui_create_batch", items=json.dumps([
    {"type": "Button", "name": "StartBtn", "parent": "MenuPanel", "text": "Start", "width": 220, "height": 44},
    {"type": "Button", "name": "OptionsBtn", "parent": "MenuPanel", "text": "Options", "width": 220, "height": 44},
    {"type": "Button", "name": "QuitBtn", "parent": "MenuPanel", "text": "Quit", "width": 220, "height": 44}
]))

unity_skills.call_skill("ui_set_anchor", name="MenuPanel", preset="MiddleCenter")
unity_skills.call_skill("ui_layout_children", name="MenuPanel", layoutType="Vertical", spacing=12)
```

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
Load `UI_REFERENCE.md` for extended element creation details, property tables, and larger UGUI examples.
