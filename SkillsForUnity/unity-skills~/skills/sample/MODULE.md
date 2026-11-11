# Unity Sample Skills

Demo skills for smoke-testing the REST API. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Testing REST server reachability
- Smoke-testing connectivity with a first call

## Rules
- Demo and test skills only; do not use them for production work.
- Real GameObject operations belong to the [gameobject](../gameobject/MODULE.md) module.

## Pitfalls
- `sample_create`, `sample_hello` and `sample_ping` do not exist. The module's skills are the ones listed below, with no `sample_` prefix.
- `create_cube` and `create_sphere` are simplified primitives; prefer `gameobject_create`.

## Skills

### create_cube
Create a cube primitive.
**Parameters:** `x`, `y`, `z`, `name`

### create_sphere
Create a sphere primitive.
**Parameters:** `x`, `y`, `z`, `name`

### delete_object
Delete an object by name.
**Parameters:** `objectName`

### find_objects_by_name
Find objects whose name contains a string.
**Parameters:** `nameContains` (`name` is accepted as a compatibility alias)

### set_object_position
Set an object's position.
**Parameters:** `objectName`, `x`, `y`, `z`

### set_object_rotation
Set an object's rotation (Euler angles).
**Parameters:** `objectName`, `x`, `y`, `z`

### set_object_scale
Set an object's scale.
**Parameters:** `objectName`, `x`, `y`, `z`

### get_scene_info
Get current scene information.
**Parameters:** none.
