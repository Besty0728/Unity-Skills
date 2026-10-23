---
name: unity-material
description: Edit material and shader properties across Built-in/URP/HDRP
---

## Triggers
- Changing how a surface looks
- Tweaking material parameters
- Swapping shaders
- 调整物体外观、修改材质参数、切换 Shader

# Unity Material Skills

Create, assign and edit materials: colours, emission, textures, float/int/vector properties, keywords, shaders, render queue, GI flags. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?names=`), which notes the non-obvious ones — never from a skill's name. Execution, dryRun and verification rules: root [SKILL.md](../../SKILL.md).

## Operating Mode
`material_get_properties` and `material_get_keywords` are `SemiAuto` (read-only); every writer (create, assign, duplicate, every `material_set_*` and the `*_batch` variants) is `FullAuto`: under Approval each needs a user grant (one server-side execution returns the result); under Auto / Bypass they run directly. No Delete, PlayMode, Reload or high-risk skills here (no NeverInSemi); to delete a material asset use the `asset` module → [operating mode](../../references/protocol-operating-mode.md).

## Shared rules

- **Batch first**: use `*_batch` for 2+ objects/materials.
- **Targeting a GameObject edits the material asset, not that one object.** There is no per-object instance: every `material_set_*` resolves a GameObject to `renderer.sharedMaterial` and writes the asset on disk — an asset mutation, not a scene edit, whatever a skill's declared flags say. A fresh primitive carries the built-in `Default-Material`, so `material_set_color(name="Cube")` recolours every object still using it. Target by GameObject name only after confirming that object owns an unshared material (`material_get_properties` → `materialPath` names the asset that would be written). Safest: write by asset path (`path="Assets/.../X.mat"`).
- **Safe route for "make this object red"** — create, colour the asset by path, then assign:

```python
unity_skills.call_skill("material_create", name="Red", savePath="Assets/Materials")
unity_skills.call_skill("material_set_color", path="Assets/Materials/Red.mat", r=1, g=0, b=0)
unity_skills.call_skill("material_assign", name="Cube", materialPath="Assets/Materials/Red.mat")
```

- **Object targeting**: most single-object skills accept `name`, `entityId`, `instanceId` or `path`. In `material_set_*` / `material_get_*`, `path` is either a GameObject hierarchy path or a material asset path (`Assets/...` or ending `.mat` is treated as an asset). In `material_assign`, `path` is a GameObject hierarchy path only; the material goes in `materialPath`.
- **Colour values** are 0–1, not 0–255. HDR/bloom: `intensity` > 1.
- **Render pipeline**: skills auto-detect and adapt. Pipeline-specific property names differ, so only pass `propertyName` when auto-detection picked the wrong one, and trust the setter's `propertyUsed` over your own guess.

| Pipeline | Default Shader | Color Property | Texture Property |
|----------|---------------|----------------|------------------|
| Built-in | Standard | `_Color` | `_MainTex` |
| URP | Universal Render Pipeline/Lit | `_BaseColor` | `_BaseMap` |
| HDRP | HDRP/Lit | `_BaseColor` | `_BaseColorMap` |

- **URP Lit exposes both `_BaseColor` and a legacy `_Color`; only `_BaseColor` is authoritative.** `material_get_properties` lists both, but writing `_BaseColor` does not update `_Color`, so a verification that reads `_Color` reports the old colour and looks like a failed write.
- **Shader property names**: check the Inspector or read them from `material_get_properties`.
- **Batch semantics**: a rejected item fails the whole batch and rolls back the others (`rolledBack:true`, `results[i].reverted:true`).
- **Persist materials as assets** (`savePath`) for reuse; one created without it is lost on editor restart.

## Skills Overview

| Single | Batch |
|---|---|
| `material_create` | `material_create_batch` |
| `material_assign` | `material_assign_batch` |
| `material_set_color` | `material_set_colors_batch` |
| `material_set_emission` | `material_set_emission_batch` |

No batch: `material_set_texture`, `material_set_texture_offset` / `material_set_texture_scale` (tiling), `material_set_float` / `material_set_int` / `material_set_vector`, `material_set_keyword` (enable/disable shader keywords), `material_set_render_queue`, `material_set_shader`, `material_set_gi_flags`, `material_get_properties` / `material_get_keywords` (query), `material_duplicate`.

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `material_set_metallic` / `material_set_smoothness` do not exist → use `material_set_float` with `propertyName="_Metallic"` or `"_Glossiness"` (Standard) / `"_Smoothness"` (URP)
- `material_set_color` r/g/b/a range is **0–1**, not 0–255
- `material_set_property` does not exist → use the specific setter: `material_set_float`, `material_set_int`, `material_set_vector`, `material_set_color`
- `material_get_color` does not exist → use `material_get_properties` (returns all properties including colors)

Routing: shader change → `material_set_shader`; texture tiling → `material_set_texture_scale` / `material_set_texture_offset`.

## Exact Signatures

Names, parameters, defaults and returns come from `GET /skills/schema?category=Material` (or `?names=a,b`), not this file.

## Common Errors

| Error | Trigger → fix |
|---|---|
| `TARGET_NOT_FOUND` | Material asset, GameObject/renderer, shader, texture or property missing (`Material asset not found`, `No Renderer component found`, `Shader not found`) → verify with `asset_find`, `gameobject_find`, `material_get_properties`. |
| `MISSING_PARAM` | e.g. `materialPath`, `sourcePath`, `texturePath`, `propertyName`, `keyword`, `shaderName` → supply it; `mode=dryRun` lists all parameters. |
| `SEMANTIC_INVALID` | Unrecognized GI flag, invalid asset path, a property the shader does not use, or a `propertyName` not declared as a colour → correct per the allowed range/enum/path in the error. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
