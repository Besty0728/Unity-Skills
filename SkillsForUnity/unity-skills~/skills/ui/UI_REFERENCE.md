# UGUI Reference

Extended UGUI examples and element and property details. The entry [MODULE.md](MODULE.md) keeps routing and core rules; per-skill parameters are in `reference/<skill_name>.md`.

## Efficient Menu Example

```
curl -s "http://localhost:<port>/skill/ui_create_canvas?expectProject=<project>" -d '{"name":"MainMenu"}'
curl -s "http://localhost:<port>/skill/ui_create_panel?expectProject=<project>" -d '{"name":"MenuPanel","parent":"MainMenu","a":0.65}'
curl -s "http://localhost:<port>/skill/ui_set_rect?expectProject=<project>" -d '{"name":"MenuPanel","width":300,"height":200}'
curl -s "http://localhost:<port>/skill/ui_create_batch?expectProject=<project>" -d '{"items":"[{\"type\":\"Button\",\"name\":\"StartBtn\",\"parent\":\"MenuPanel\",\"text\":\"Start\",\"width\":220,\"height\":44},{\"type\":\"Button\",\"name\":\"QuitBtn\",\"parent\":\"MenuPanel\",\"text\":\"Quit\",\"width\":220,\"height\":44}]"}'
curl -s "http://localhost:<port>/skill/ui_layout_children?expectProject=<project>" -d '{"name":"MenuPanel","layoutType":"Vertical","spacing":12}'
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

**ui_set_image**

Useful fields:
- `type`: `Simple`, `Sliced`, `Tiled`, `Filled`
- `fillMethod`: `Radial360`, `Radial180`, `Radial90`, `Horizontal`, `Vertical`
- `fillAmount`
- `fillClockwise`
- `preserveAspect`
- `spritePath`

**ui_add_layout_element**

Useful fields:
- `minWidth`, `minHeight`
- `preferredWidth`, `preferredHeight`
- `flexibleWidth`, `flexibleHeight`
- `ignoreLayout`
- `layoutPriority`

**ui_add_canvas_group**

Useful fields:
- `alpha`
- `interactable`
- `blocksRaycasts`
- `ignoreParentGroups`

**ui_add_mask**

Useful fields:
- `maskType`: `Mask` or `RectMask2D`
- `showMaskGraphic`

**ui_add_outline**

Useful fields:
- `effectType`: `Shadow` or `Outline` (case-insensitive); any other value is rejected
- `r/g/b/a`
- `distanceX`, `distanceY`
- `useGraphicAlpha`

**ui_configure_selectable**

Useful fields:
- `transition`
- `interactable`
- `navigationMode`
- `normalR/G/B/A`
- `highlightedR/G/B/A`
- `pressedR/G/B/A`
- `disabledR/G/B/A`
- `colorMultiplier`
- `fadeDuration`

Each of the four ColorBlock states takes an optional alpha (`normalA`, `highlightedA`, `pressedA`, `disabledA`) alongside its RGB channels — useful for a `disabledA` fade without restating the colour. **Every channel is independent and omitting one keeps the block's current value**, so `pressedA=0.5` alone dims the pressed state and leaves its hue untouched; you never have to resend all four numbers to change one.

Any single channel is enough to trigger the write. (Earlier versions tested only the four `*R` channels, so a call passing nothing but `normalG` was silently dropped along with the rest of the block — if you remember working around that, the workaround is no longer needed.) `transition` and `navigationMode` are parsed before anything is applied: an unknown value rejects the whole call with `SEMANTIC_INVALID` + `validValues` rather than committing the colours from the same call.

## Best Practices

1. Create the Canvas before child controls.
2. Use panels to separate logical UI groups.
3. Use meaningful names for later scripting access.
4. Prefer layout groups and anchors over manual pixel nudging when building reusable UI.
5. For 3D or VR-facing UI, convert the Canvas after creation rather than hand-building a world-space hierarchy from scratch.
