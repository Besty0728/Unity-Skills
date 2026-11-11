# Unity UI Skills

Unity UGUI and Canvas workflows. This module is separate from UI Toolkit. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Extended examples: [UI_REFERENCE.md](UI_REFERENCE.md).

## When to use
- Building UGUI screens
- Adding Canvas elements
- Arranging UI layout

## Rules
- Prefer `ui_create_batch` when creating two or more UI elements. Its `items` parameter is a JSON string, not a raw array.
- Most create skills take no `x` or `y`. Create the element, then place it with `ui_set_rect`, `ui_set_anchor` or `ui_layout_children`. `ui_create_batch` is for bulk creation, not precise placement.
- `ui_create_canvas` defaults to `ScreenSpaceOverlay`. Create the Canvas once, then a panel, then the controls inside the panel.
- Text creation detects TextMeshPro: `TextMeshProUGUI` when TMP is installed, the legacy `Text` otherwise. The response says which one was created.
- `ui_create_*` skills and batch items take `parentEntityId`, which outranks `parent`. Unlike a parent name that does not match, it never falls back to the first Canvas.
- `ui_align_selected`, `ui_distribute_selected` and the selection-based layout act on the UI elements selected in the Hierarchy. Select them first.
- For world-space gameplay UI, build the Canvas here, then convert it for XR with `xr_setup_ui_canvas` if needed.
- To delete a UI node, use the [gameobject](../gameobject/MODULE.md) module.

## Pitfalls
- `ui_add_canvas`, `ui_create_label`, `ui_create_checkbox` and `ui_set_color` do not exist. Use `ui_create_canvas`, `ui_create_text`, `ui_create_toggle`, and `component_set_property` on `Image` or `Text` (or the dedicated UI property skills).
- Do not confuse UGUI (`ui`) with UI Toolkit (`uitoolkit`). UXML, USS and UIDocument belong to `uitoolkit`.

## Skills Overview

| Skill | Purpose |
|-------|---------|
| [`ui_create_canvas`](reference/ui_create_canvas.md) | Create a Canvas |
| [`ui_create_panel`](reference/ui_create_panel.md) | Create a panel container |
| [`ui_create_button`](reference/ui_create_button.md) | Create a Button |
| [`ui_create_text`](reference/ui_create_text.md) | Create a text label |
| [`ui_create_image`](reference/ui_create_image.md) | Create an Image |
| [`ui_create_inputfield`](reference/ui_create_inputfield.md) | Create an InputField |
| [`ui_create_slider`](reference/ui_create_slider.md) | Create a Slider |
| [`ui_create_toggle`](reference/ui_create_toggle.md) | Create a Toggle |
| [`ui_create_dropdown`](reference/ui_create_dropdown.md) | Create a Dropdown |
| [`ui_create_scrollview`](reference/ui_create_scrollview.md) | Create a ScrollRect hierarchy |
| [`ui_create_rawimage`](reference/ui_create_rawimage.md) | Create a RawImage |
| [`ui_create_scrollbar`](reference/ui_create_scrollbar.md) | Create a Scrollbar |
| [`ui_create_batch`](reference/ui_create_batch.md) | Create several UI elements in one call |
| [`ui_find_all`](reference/ui_find_all.md) | Find UI elements in the scene (read-only) |
| [`ui_set_text`](reference/ui_set_text.md) | Update text content |
| [`ui_set_rect`](reference/ui_set_rect.md) | Set RectTransform size, position and offsets |
| [`ui_get_rect_transform`](reference/ui_get_rect_transform.md) | Read full RectTransform data |
| [`ui_set_rect_transform`](reference/ui_set_rect_transform.md) | Set full RectTransform data |
| [`ui_set_rect_transform_batch`](reference/ui_set_rect_transform_batch.md) | Set full RectTransform data for several elements |
| [`ui_set_anchor`](reference/ui_set_anchor.md) | Apply an anchor preset |
| [`ui_layout_children`](reference/ui_layout_children.md) | Lay out children vertically, horizontally or in a grid |
| [`ui_align_selected`](reference/ui_align_selected.md) | Align the selected UI elements |
| [`ui_distribute_selected`](reference/ui_distribute_selected.md) | Distribute the selected UI elements |
| [`ui_set_image`](reference/ui_set_image.md) | Set Image type, fill and sprite |
| [`ui_add_layout_element`](reference/ui_add_layout_element.md) | Add LayoutElement size constraints |
| [`ui_add_canvas_group`](reference/ui_add_canvas_group.md) | Add a CanvasGroup |
| [`ui_add_mask`](reference/ui_add_mask.md) | Add a Mask or RectMask2D |
| [`ui_add_outline`](reference/ui_add_outline.md) | Add an Outline or Shadow effect |
| [`ui_configure_selectable`](reference/ui_configure_selectable.md) | Configure transitions, navigation and colours |

## Workflow
1. Create a Canvas.
2. Group related controls in panels.
3. Create the controls with `ui_create_batch`.
4. Set anchors and layout groups before hand-placing children.

## Example

```
curl -s "http://localhost:<port>/skill/ui_create_canvas?expectProject=<project>" -d '{"name":"MainMenu"}'
curl -s "http://localhost:<port>/skill/ui_create_panel?expectProject=<project>" -d '{"name":"MenuPanel","parent":"MainMenu","a":0.65}'
curl -s "http://localhost:<port>/skill/ui_set_anchor?expectProject=<project>" -d '{"name":"MenuPanel","preset":"MiddleCenter"}'
curl -s "http://localhost:<port>/skill/ui_layout_children?expectProject=<project>" -d '{"name":"MenuPanel","layoutType":"Vertical","spacing":12}'
```

## Errors

Transport-level codes (COMPILING, RATE_LIMIT and others): [error codes](../../references/protocol-error-codes.md).

| Error | Trigger | Fix |
|---|---|---|
| `TARGET_NOT_FOUND` | The parent, UI element, RectTransform, text or image component, or sprite asset was not found. | Locate the element with `ui_find_all` or `gameobject_find`; check sprite paths with `asset_find`. |
| `SEMANTIC_INVALID` | An unknown anchor preset, layout type or alignment, or a name with path separators. | Use a valid value from the error (for example `MiddleCenter`, `Vertical`, `TopLeft`) and retry. |
| `SKILL_ERROR` | A selection-based operation had too few UI elements selected. | Select the required elements in the Hierarchy first. |
