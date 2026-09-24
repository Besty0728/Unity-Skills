### component_set_enabled
Enable or disable a component: Behaviour (including 2D colliders), Renderer or Collider.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | null | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | null | Hierarchy path |
| `componentType` | string | Yes | - | Component type to enable/disable |
| `enabled` | bool | No | true | Whether to enable or disable |

*At least one identifier required

**Returns**: `{success, gameObject, componentType, enabled, isActiveAndEnabled?}`: `componentType` is the resolved type name and `enabled` is read back from the component; `isActiveAndEnabled` (Behaviours only) is false while the GameObject or a parent is inactive, whatever `enabled` says. A type without an `enabled` switch fails ("does not have an enabled property").
