### ui_set_image
Set Image properties (type, fillMethod, fillAmount, preserveAspect, sprite).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | Target GameObject name |
| `instanceId` | int | No* | 0 | Target instance ID |
| `path` | string | No* | null | Target hierarchy path |
| `type` | string | No | null | Simple, Sliced, Tiled or Filled (Image.Type, case-insensitive). |
| `fillMethod` | string | No | null | Horizontal, Vertical, Radial90, Radial180 or Radial360 (case-insensitive); only affects rendering when type=Filled. |
| `fillAmount` | float | No | null | Fill amount |
| `fillClockwise` | bool | No | null | Fill clockwise |
| `fillOrigin` | int | No | null | Meaning depends on fillMethod: an index (0-3) into that method's own Origin enum (e.g. OriginHorizontal for Horizontal). |
| `preserveAspect` | bool | No | null | Preserve aspect |
| `spritePath` | string | No | null | Sprite asset path |
| `pixelsPerUnitMultiplier` | float | No | null | Pixels per unit multiplier |
| `entityId` | string | No* | null | Target entity ID (preferred; the only id on Unity 6000.4+) |

\* Target by one of `name`, `entityId`, `instanceId` or `path`.
