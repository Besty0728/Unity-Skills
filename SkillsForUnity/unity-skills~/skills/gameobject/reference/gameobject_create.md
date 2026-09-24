### gameobject_create
Create one GameObject (primitive or empty), optionally parented, placed, rotated and scaled in the same call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No | "" | Object name (an omitted name creates an unnamed object rather than failing) |
| `primitiveType` | string | No | null | Cube/Sphere/Capsule/Cylinder/Plane/Quad; null, `Empty` or `None` = empty object |
| `x`, `y`, `z` | float | No | 0 | Position: local to the parent, or world when `space` is `world` |
| `rotX`, `rotY`, `rotZ` | float | No | 0 | Euler rotation, same space as `x/y/z` |
| `scaleX`, `scaleY`, `scaleZ` | float | No | 1 | Local scale |
| `space` | string | No | "local" | `local` (localPosition/localEulerAngles; equals world without a parent) or `world` (applied after parenting) |
| `parentEntityId` | string | No | null | Parent entityId (Unity 6000.4+, preferred) |
| `parentName` | string | No | null | Parent object name |
| `parentInstanceId` | int | No | 0 | Parent instance ID |
| `parentPath` | string | No | null | Parent hierarchy path |

**Returns**: `{success, name, entityId, instanceId, path, parent, parentPath, position, localPosition, rotation, scale}`, read back from the Transform: `position` world, `localPosition` local, `rotation` world euler (Unity normalises to 0–360, so `-90` reads back as `270`), `scale` local. Without a parent, `parent` is `"(root)"` and `parentPath` null.

An unresolvable parent or an invalid `space` / `primitiveType` fails the call before anything is created.
