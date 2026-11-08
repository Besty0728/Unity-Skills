### ui_configure_selectable
Configure Selectable properties (transition, colors, navigation) on a UI element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `transition` | string | No | null | None, ColorTint, SpriteSwap or Animation (Selectable.Transition, case-insensitive). |
| `interactable` | bool | No | null | Interactable |
| `navigationMode` | string | No | null | None, Horizontal, Vertical, Automatic or Explicit (Navigation.Mode, case-insensitive). |
| `normalR` | float | No | null | normalR/G/B/A, highlightedR/G/B/A, pressedR/G/B/A, disabledR/G/B/A: 0-1 floats; each omitted channel keeps the ColorBlock's current value. |
| `normalG` | float | No | null | Normal g |
| `normalB` | float | No | null | Normal b |
| `normalA` | float | No | null | Normal a |
| `highlightedR` | float | No | null | Highlighted r |
| `highlightedG` | float | No | null | Highlighted g |
| `highlightedB` | float | No | null | Highlighted b |
| `highlightedA` | float | No | null | Highlighted a |
| `pressedR` | float | No | null | Pressed r |
| `pressedG` | float | No | null | Pressed g |
| `pressedB` | float | No | null | Pressed b |
| `pressedA` | float | No | null | Pressed a |
| `disabledR` | float | No | null | Disabled r |
| `disabledG` | float | No | null | Disabled g |
| `disabledB` | float | No | null | Disabled b |
| `disabledA` | float | No | null | Disabled a |
| `colorMultiplier` | float | No | null | Color multiplier |
| `fadeDuration` | float | No | null | Fade duration |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
