### component_remove
Remove one component from a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | - | Instance ID |
| `path` | string | No* | - | Hierarchy path |
| `componentType` | string | Yes | - | Component type to remove |
| `componentIndex` | int | No | 0 | 0-based index into the components of that type when 2+ exist on the same object; out of range is rejected |

*At least one identifier required

**Returns**: `{success, gameObject, removed, remainingOfType}`: `removed` is the resolved type name, `remainingOfType` how many components of that type the object still has. A component that another one requires (`RequireComponent`, base types such as `Collider` included) is refused unless another remaining component still satisfies the requirement: `SEMANTIC_INVALID` with `requiredBy`; remove the dependent first. NeverInSemi: runs only under Bypass or an Allowlist hit.
