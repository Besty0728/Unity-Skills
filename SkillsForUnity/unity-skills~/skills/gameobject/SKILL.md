---
name: unity-gameobject
description: Create and manipulate GameObjects
---

## Triggers
- Building or restructuring scene hierarchy
- Spawning or removing objects
- Adjusting transforms
- 搭建或调整场景层级、新建或删除物体、修改 Transform

# Unity GameObject Skills

Create, find, parent and transform scene objects. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?names=`), which notes the non-obvious ones — never from a skill's name. Execution, dryRun and verification rules: root [SKILL.md](../../SKILL.md).

## Operating Mode
`gameobject_find` / `gameobject_get_info` are `SemiAuto` (read-only); every other skill is `FullAuto`. `gameobject_delete` / `gameobject_delete_batch` (`Operation.Delete`) are NeverInSemi: `MODE_FORBIDDEN` under Approval/Auto, run only under Bypass or an Allowlist hit → [operating mode](../../references/protocol-operating-mode.md).

## Shared rules

- **Identifiers**: `entityId` (string; Unity 6000.4+, where `instanceId` reads `0`), `instanceId` (int, older Unity), `path` (`"Parent/Child"`, case-insensitive, optional scene-name prefix), `name`. Give at least one; precedence `entityId > instanceId > path > name`, and a stale id or missed path falls through to the next one given.
- **Name matching**: an exact name wins (the first in hierarchy order when several share it); otherwise a single whole-word or substring match is used. Several fuzzy matches return `TARGET_NOT_FOUND` with `candidates` (`path` + `entityId` each) instead of guessing. Non-exact resolutions are listed in top-level `resolutionNotes`.
- **Spaces**: `gameobject_create` `x/y/z` and `rotX/Y/Z` are local to the parent unless `space:"world"` (applied after parenting). `gameobject_set_transform` `posX/Y/Z` and `rotX/Y/Z` are world, `localPosX/Y/Z` local; it applies world position, then local position, then rotation, then scale, so local wins on a shared axis and omitted axes keep their value. `scaleX/Y/Z` is always `localScale`. RectTransform fields (`anchor*`, `pivot*`, `sizeDelta*`, `width`/`height`) are UI-only.
- **Read-back**: create, set_transform and their batches return the Transform read back — `position` (world), `localPosition`, `rotation` (world euler, 0–360), `scale` (local), as `gameobject_get_info` reports them; no follow-up read needed.
- **Batches** (`items` = JSON array): all-or-nothing. One failed item reverts the whole call (`rolledBack:true`, the other items `reverted:true`, error `SEMANTIC_INVALID`); fix the failed items and resend all. Unknown item keys are ignored and named in `warnings`. Items take the single skill's identifiers and fields; `gameobject_create_batch` items may parent to an item created earlier in the same call.

## Skills Overview

Use `*_batch` for 2+ objects (one call instead of N).

| Single | Batch |
|---|---|
| `gameobject_create` | `gameobject_create_batch` |
| `gameobject_delete` | `gameobject_delete_batch` |
| `gameobject_duplicate` (sibling `<name>_Copy`) | `gameobject_duplicate_batch` |
| `gameobject_rename` | `gameobject_rename_batch` |
| `gameobject_set_transform` | `gameobject_set_transform_batch` |
| `gameobject_set_active` | `gameobject_set_active_batch` |
| `gameobject_set_parent` (no parent id = unparent) | `gameobject_set_parent_batch` |
| - | `gameobject_set_layer_batch` |
| - | `gameobject_set_tag_batch` |
| `gameobject_set_sibling_index` (index clamped) | - |

Query (no batch): `gameobject_find` (name substring or regex, tag, layer, component), `gameobject_get_info` (transform, parent, children, components).

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `gameobject_move` / `gameobject_rotate` / `gameobject_set_scale` / `gameobject_set_position` do not exist → use `gameobject_set_transform`
- `gameobject_add_component` does not exist → use `component_add`
- `gameobject_get_transform` does not exist → use `gameobject_get_info`

**Elsewhere**: components → `component_*` skills; material or colour → `material_*` skills; `scene_find_objects` (SemiAuto) also searches objects.

## Exact Signatures

Names, parameters, defaults and required flags come from `GET /skills/schema?category=GameObject` (or `?names=a,b`), not this file.

## Common Errors

| Error | Fix |
|---|---|
| `TARGET_NOT_FOUND` (object, parent, child, layer) | Retry with a `candidates` entry's `path` / `entityId`, or locate it via `gameobject_find` / `scene_get_hierarchy`. |
| `MISSING_PARAM` | Supply it (e.g. `newName`, `items`); the error (or a dryRun) lists all parameters. |
| `SEMANTIC_INVALID` | Use an allowed value from the message (`primitiveType`, `space` local/world, a registered tag) or fix the failed `results` items. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
