# Unity Material Skills

Create, assign and edit materials. Parameters come from the schema (`GET /skills/schema?category=Material&wire=v2`), never from a skill's name; execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Changing how a surface looks: colour, emission, texture, tiling or offset
- Tweaking material float, int or vector properties, or shader keywords
- Swapping a material's shader or render queue
- Creating materials and assigning them to renderers

## Rules
- Use the `*_batch` variant for 2 or more objects or materials.
- Targeting a GameObject edits its material asset, not that one object: every `material_set_*` resolves the object to `renderer.sharedMaterial` and writes the asset on disk. A fresh primitive shares the built-in `Default-Material`, so `material_set_color(name="Cube")` recolours every object using it. Write by asset path (`path="Assets/.../X.mat"`) unless the object owns an unshared material; `material_get_properties` returns the `materialPath` that would be written.
- "Make this object red": `material_create` with `savePath`, `material_set_color` by `path`, then `material_assign` with `materialPath`.
- In `material_set_*` and `material_get_*`, `path` is a GameObject hierarchy path or a material asset path (`Assets/...` or `.mat`). In `material_assign`, `path` is a GameObject path only; the material goes in `materialPath`.
- Colour components are 0-1, not 0-255. HDR glow uses `intensity` above 1.
- Skills detect the render pipeline. Pass `propertyName` only when detection picks the wrong property, and trust the returned `propertyUsed`.
- Persist materials with `savePath`; a material created without it is lost on editor restart.
- A rejected item fails the whole batch and rolls back the others (`rolledBack:true`, `results[i].reverted:true`).
- Operating mode: `material_get_properties` and `material_get_keywords` are read-only; every writer is `FullAuto`. To delete a material asset, use the `asset` module.

| Pipeline | Default shader | Colour property | Texture property |
|----------|----------------|-----------------|------------------|
| Built-in | Standard | `_Color` | `_MainTex` |
| URP | Universal Render Pipeline/Lit | `_BaseColor` | `_BaseMap` |
| HDRP | HDRP/Lit | `_BaseColor` | `_BaseColorMap` |

## Pitfalls
- URP Lit exposes both `_BaseColor` and a legacy `_Color`; only `_BaseColor` is authoritative. A read of `_Color` after writing `_BaseColor` looks like a failed write.
- Shader property names differ per shader: read them from `material_get_properties`.
- Writes to a material assigned to many renderers change all of them.

## Skills Overview

| Single | Batch |
|---|---|
| `material_create` | `material_create_batch` |
| `material_assign` | `material_assign_batch` |
| `material_set_color` | `material_set_colors_batch` |
| `material_set_emission` | `material_set_emission_batch` |

No batch: `material_set_texture`, `material_set_texture_offset` / `material_set_texture_scale` (tiling), `material_set_float` / `material_set_int` / `material_set_vector`, `material_set_keyword` (enable or disable shader keywords), `material_set_render_queue`, `material_set_shader`, `material_set_gi_flags`, `material_get_properties` / `material_get_keywords` (read), `material_duplicate`.

Per-skill parameter tables and return shapes: `reference/<skill_name>.md`.

**DO NOT** (common hallucinations):
- `material_set_metallic` / `material_set_smoothness` do not exist: use `material_set_float` with `propertyName="_Metallic"` and `"_Glossiness"` (Standard) or `"_Smoothness"` (URP).
- `material_set_property` does not exist: use `material_set_float`, `material_set_int`, `material_set_vector` or `material_set_color`.
- `material_get_color` does not exist: use `material_get_properties`, which returns colours too.
- Shader changes go to `material_set_shader`; tiling goes to `material_set_texture_scale` or `material_set_texture_offset`.

## Common Errors

| Error | Trigger and fix |
|---|---|
| `TARGET_NOT_FOUND` | Material asset, renderer, shader, texture or property missing. Verify with `asset_find`, `gameobject_find` or `material_get_properties`. |
| `MISSING_PARAM` | e.g. `materialPath`, `sourcePath`, `texturePath`, `propertyName`, `keyword`, `shaderName`. Supply it; `mode=dryRun` lists all parameters. |
| `SEMANTIC_INVALID` | Unknown GI flag, invalid asset path, a property the shader does not use, or a `propertyName` not declared as a colour. Correct it per the range or enum in the error. |

Transport codes → [error codes](../../references/protocol-error-codes.md).
