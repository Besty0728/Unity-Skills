### gameobject_delete
Delete one GameObject together with its children.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `entityId` | string | No* | Entity ID (Unity 6000.4+, preferred) |
| `name` | string | No* | Object name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |

*At least one identifier required

**Returns**: `{success, deleted}` (`deleted` = the object's name). NeverInSemi: runs only under Bypass or an Allowlist hit.
