### gameobject_set_parent
Set, change or clear an object's parent.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `childEntityId` | string | No* | Child entity ID (Unity 6000.4+, preferred) |
| `childName` | string | No* | Child object name |
| `childInstanceId` | int | No* | Child instance ID |
| `childPath` | string | No* | Child hierarchy path |
| `parentEntityId` | string | No | Parent entity ID (Unity 6000.4+, preferred) |
| `parentName` | string | No | Parent object name (empty string = unparent) |
| `parentInstanceId` | int | No | Parent instance ID |
| `parentPath` | string | No | Parent hierarchy path |

*At least one child identifier required; omit every parent identifier to unparent

**Returns**: `{success, child, childEntityId, parent, parentEntityId, parentPath, newPath, position, localPosition}` (`parent` is `"(root)"` after unparenting; `position`/`localPosition` are read back from the child's transform after the reparent). A parent given but not found fails the call. A child that is a nested (non-root) part of a Prefab instance can't be reparented outside that instance — Unity doesn't support it as an override — and the call fails with a structured error instead of silently leaving the child where it was.
