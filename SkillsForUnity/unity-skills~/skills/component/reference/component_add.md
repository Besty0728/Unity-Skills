### component_add
Add a component to a GameObject.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | GameObject name |
| `instanceId` | int | No* | Instance ID (preferred) |
| `path` | string | No* | Hierarchy path |
| `componentType` | string | Yes | Component type name |

*At least one identifier required (`entityId` is accepted too)

**Returns**: `{success, gameObject, entityId, instanceId, component, fullTypeName}` with the resolved type, so the response already confirms the add; `component_list` is only needed to inspect other components. A type marked `[DisallowMultipleComponent]` that is already present adds nothing and returns `{warning, gameObject, entityId, instanceId, component, fullTypeName, alreadyPresent: true}`. An unknown type fails with `availableTypes` (similar names) and a hint.

Common types: physics `Rigidbody`, `BoxCollider`, `SphereCollider`, `CapsuleCollider`, `MeshCollider`, `CharacterController`; rendering `MeshRenderer`, `SkinnedMeshRenderer`, `SpriteRenderer`, `LineRenderer`, `TrailRenderer`; audio `AudioSource`, `AudioListener`; UI `Canvas`, `Image`, `Text` (legacy), `Button`. For physics, add colliders before the Rigidbody.
