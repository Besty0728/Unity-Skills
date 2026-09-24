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

**Returns**: `{success, gameObject, componentType, enabled}`. A type without an `enabled` switch fails ("does not have an enabled property").
