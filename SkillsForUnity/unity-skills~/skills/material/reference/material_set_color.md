### material_set_color
Set material color with optional HDR intensity.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `entityId` | string | No* | - | GameObject entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No* | 0 | GameObject instance ID |
| `path` | string | No* | - | Material asset path |
| `r`, `g`, `b` | float | No | 1 | Color (0-1) |
| `a` | float | No | 1 | Alpha |
| `propertyName` | string | No | auto-detect | Color property |
| `intensity` | float | No | 1.0 | HDR intensity (>1 for bloom) |

**Returns**: `{success, target, color, intensity, propertyUsed, hdrEnabled, materialPath}`. Auto-detection tries the pipeline's property first, then `_BaseColor`, `_Color`, `_TintColor`, `_EmissionColor`, and **`propertyUsed` reports which one it actually wrote** — check it before concluding a colour "didn't apply". An explicit `propertyName` that the shader doesn't have (or that names a non-colour property like `_Metallic`) is rejected with `validValues`; one that's missing but is `_BaseColor`/`_Color`/`_MainColor`/`_TintColor` falls back to whichever of that family exists (`propertyRequested` + `warnings` report it). It never falls back to `_EmissionColor` for an explicit request.
