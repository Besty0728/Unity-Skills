---
name: unity-component
description: Manage GameObject components
---

## Triggers
- Attaching or removing components
- Copying components between objects
- Toggling components
- Reading/writing serialized fields
- 挂载或移除组件、在对象间复制组件、开关组件、读写序列化字段

# Unity Component Skills

Add, remove, copy, toggle and edit components. Parameters come from the schema (recommend `includeSchema=true` or `/skills/schema?names=`), which notes the non-obvious ones — never from a skill's name. Execution, dryRun and verification rules: root [SKILL.md](../../SKILL.md).

## Operating Mode
`component_list`, `component_get_properties`, `component_get_serialized_properties` are `SemiAuto` (read-only); every writer is `FullAuto`. `component_remove` / `component_remove_batch` (`Operation.Delete`) are NeverInSemi: `MODE_FORBIDDEN` under Approval/Auto, run only under Bypass or an Allowlist hit → [operating mode](../../references/protocol-operating-mode.md).

## Shared rules

- **Targets**: single-object skills take `name`, `instanceId` or `path` (plus `entityId`, resolved to the path), matched like GameObjects: an ambiguous fuzzy name returns `TARGET_NOT_FOUND` with `candidates`, non-exact matches are listed in `resolutionNotes`. Batch items take only `name` / `instanceId` / `path`: an `entityId` item key is ignored (named in `warnings`), so the item finds no target. `component_copy*` use `source*` / `target*` identifiers.
- **`componentType`** (required): the class name — `Rigidbody`, `BoxCollider`, `Namespace.ClassName` for custom scripts. Unity, UI, TextMeshPro, Cinemachine and Input System namespaces are searched first, then a case-insensitive scan that takes the first matching type; the response's `component` / `fullTypeName` show what was resolved.
- **Two naming schemes**: `component_set_property` / `component_get_properties` use C# member names (`mass`, `isKinematic`; matched case-insensitively, private fields writable too). `component_get_serialized_properties` / `component_set_serialized_property` use SerializedProperty paths (`m_Mass`, `items.Array.data[0]`; a bare `mass` is retried as `m_Mass`). To convert, drop `m_` and lowercase the first letter; an `m_` name sent to `component_set_property` fails with "Property/field not found" and lists the writable members.
- **Value source** (`component_set_property`, its batch, `batch_preview_set_property`): exactly one of `assetPath` (project asset) > `referencePath` (scene object by path) / `referenceName` (by name, first match) > `value`, checked in that order; a lower one sent alongside is silently ignored. Sending none writes the type's default (0 / false / null).
- **`value` encoding**: numbers and bools (`true`/`false`, `1`/`0`, `yes`/`no`, `on`/`off`; others rejected) as JSON or strings; enums by member name, case-insensitive; vectors, Quaternion (3 values = euler, 4 = xyzw), Rect (`x,y,w,h`) and Bounds (`cx,cy,cz,sx,sy,sz`) as comma strings (`"1,2,3"`); colours as `"r,g,b[,a]"` (0–1), `#RRGGBB[AA]` or a name (red, green, blue, white, black, yellow, cyan, magenta, gray/grey, clear); LayerMask as a layer name or an integer mask. The JSON object form (`{"x":1,"y":2,"z":3}`, `{"r":1,"g":0,"b":0}`) works only for Vector2/3/4 and Color/Color32, with every component required (colour `a` defaults to 1; an explicit `null` counts as missing); partial objects, unknown keys and non-numbers are rejected naming the expected keys.
- **Read-back**: `component_set_property` returns `valueSet` read from the component after the write, plus `valueRequested` only when the setter clamped or normalised it; `component_set_serialized_property` re-reads after applying. No follow-up read needed.
- **Batches**: all-or-nothing — one failed item reverts the whole call (`rolledBack:true`, other items `reverted:true`); unknown item keys are named in `warnings`.

## Skills Overview

Use a `*_batch` skill for 2+ objects or several properties (one call instead of N).

| Single | Batch |
|---|---|
| `component_add` | `component_add_batch` |
| `component_remove` | `component_remove_batch` |
| `component_set_property` | `component_set_property_batch` |
| `component_set_serialized_property` | `component_set_serialized_property_batch` |

No batch: `component_list` (types and enabled state), `component_get_properties` (C# members and values), `component_get_serialized_properties` (Inspector paths), `component_copy`, `component_copy_exact` (verifies every serialized field), `component_set_enabled` (Behaviour/Renderer/Collider on or off — not `component_set_property`).

Per-skill tables, item fields and return shapes: `reference/<skill_name>.md` — rarely needed, the schema notes the parameters.

**DO NOT** (common hallucinations):
- `component_create` / `component_get` do not exist → use `component_add` (add) and `component_get_properties` (read)
- `component_find` does not exist → use `component_list` to list components on an object

**Elsewhere**: a new C# component → `script_create`, wait for compilation, then `component_add`.

## Exact Signatures

Names, parameters, defaults and required flags come from `GET /skills/schema?category=Component` (or `?names=a,b`), not this file.

## Common Errors

| Error | Trigger → fix |
|---|---|
| `TARGET_NOT_FOUND` | Object, component type, component on the object, member or asset missing → `component_list`, `component_get_properties`, `asset_find`, `gameobject_find`. |
| `MISSING_PARAM` | e.g. `componentType`, `propertyName`, `propertyPath` → supply it; the error (or a dryRun) lists all parameters. |
| `SEMANTIC_INVALID` | A `value` the target type rejects, `componentIndex` out of range, a component another one still requires (`requiredBy`: remove those first), or a failed batch item → fix per the message / `results`. |
| `SKILL_ERROR` | Read-only property → pick a writable member. |

Transport codes (`COMPILING`, `RATE_LIMIT`, ...) → [error codes](../../references/protocol-error-codes.md).
