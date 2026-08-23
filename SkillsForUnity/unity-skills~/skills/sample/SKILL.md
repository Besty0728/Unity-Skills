---
name: unity-sample
description: Sample and demo skills for API connectivity testing
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Testing REST server reachability
- Smoke-testing connectivity
- Trying a first call
- 测试 REST 服务可达性、冒烟测试连通性、试发第一个调用

# Sample Skills

Basic examples for testing the API.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): query skills (`get_scene_info`, `find_objects_by_name`) run directly. Creators/mutators (`create_cube`, `create_sphere`, `set_object_position`, `set_object_rotation`, `set_object_scale`) are FullAuto — on `MODE_RESTRICTED`, run the grant protocol.
- **Auto** / **Bypass**: SemiAuto and FullAuto run directly.
- Auto-forbidden in this module: `delete_object` (`SkillOperation.Delete`). It is reachable only under Bypass or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`.

**Surface profile**: this module's write skills count as GameObject authoring under another name, so both the `guide` and `noSceneAuthoring` profiles hide them and answer `SURFACE_EXCLUDED` — the exclusion cannot be bypassed by mode or Allowlist. Under `guide`, `details.manualDoc` points at `manual-gameobject`, which teaches these same primitive/transform steps by hand. The query skills stay available in every profile.

`SURFACE_EXCLUDED` has a second source beyond a hidden skill name: visible shell skills that replay a payload — `batch_execute` / `batch_retry_failed` (batch module) and the workflow undo/redo/revert skills (workflow module) — return the same code at execution time when the payload they are about to apply lands in a category the active profile withdraws, even though those shell skills are never themselves hidden.

**DO NOT** (common hallucinations):
- Sample skills are basic test/demo skills — do not use them for production work
- `sample_create` is a simplified version of `gameobject_create` — prefer the full gameobject module
- `sample_hello` / `sample_ping` are connectivity test skills only

**Routing**:
- For actual GameObject operations → use `gameobject` module
- For server health check → use Python helper's `unity_skills.health()`

## Skills

### create_cube
Create a cube primitive.
**Parameters:** `x`, `y`, `z`, `name`

### create_sphere
Create a sphere primitive.
**Parameters:** `x`, `y`, `z`, `name`

### delete_object
Delete object by name.
**Parameters:** `objectName`

### `find_objects_by_name`
Find objects containing string.
**Parameters:** `nameContains` (`name` is also accepted as a compatibility alias)

### `set_object_position`
Set object position.
**Parameters:** `objectName`, `x`, `y`, `z`

### `set_object_rotation`
Set object rotation.
**Parameters:** `objectName`, `x`, `y`, `z`

### `set_object_scale`
Set object scale.
**Parameters:** `objectName`, `x`, `y`, `z`

### `get_scene_info`
Get current scene information.
**Parameters:** None.

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.