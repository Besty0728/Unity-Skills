### component_remove
Remove one component from a GameObject.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | - | GameObject name |
| `instanceId` | int | No* | - | Instance ID |
| `path` | string | No* | - | Hierarchy path |
| `componentType` | string | Yes | - | Component type to remove |
| `componentIndex` | int | No | 0 | Index into the components of that type when 2+ exist on the same object |

*At least one identifier required

**Returns**: `{success, gameObject, removed}` (`removed` is the requested `componentType` string). A component that another one requires (`RequireComponent`) is refused: remove the dependent first. NeverInSemi: runs only under Bypass or an Allowlist hit.
