# NGO 2.5+ features

Added in Netcode for GameObjects **2.5.0** (verified against the package CHANGELOG, PR #3518):

- `AttachableBehaviour` and `AttachableNode` form an alternate "attach" parenting system that avoids the classic `NetworkObject` parenting pitfalls.
- `ComponentController` network-synchronises the enabled or disabled state of a list of components.

On NGO 2.0 to 2.4.x the types do not exist yet, so every skill below returns a structured `requires 2.5.0+` error instead of a compile error. The types are detected by reflection, not by a version symbol. Call `netcode_version` first to confirm.

| Skill | Purpose | Key parameters |
|-------|---------|----------------|
| `netcode_version` | Installed NGO version and 2.5+ feature availability | none |
| `netcode_attachable_info` | Audit the scene's AttachableBehaviour, AttachableNode and ComponentController distribution | `includeInactive?` |
| `netcode_attachable_add` | Attach an `AttachableBehaviour` to a GameObject nested under a NetworkObject | target, `autoDetach?` (`None`, `OnAttachNodeDestroy`, `OnDespawn`, `OnOwnershipChange`; comma-separated) |
| `netcode_attachable_node_add` | Attach an `AttachableNode`, the socket an AttachableBehaviour attaches to | target, `detachOnDespawn?` |
| `netcode_component_controller_add` | Attach a `ComponentController` to a GameObject | target, `startEnabled?` |
| `netcode_component_controller_configure` | Set `StartEnabled` and/or fill the synchronised component list from target GameObjects, as dragging a GameObject onto the Components field does | target, `targetPaths?`, `clearExisting?`, `startEnabled?` |

Notes:
- An `AttachableBehaviour` lives on a child GameObject nested under a NetworkObject's hierarchy, not on the NetworkObject's own GameObject.
- An `AttachableNode` belongs to a different NetworkObject than the AttachableBehaviours attaching to it.
- `Attach()` and `Detach()` are runtime calls that need both instances spawned. They are not exposed as skills; call them from NetworkBehaviour code.
- `targetPaths` accepts whole GameObjects. NGO's `OnValidate()`, which this skill invokes, expands each one into every eligible child component with a public `bool enabled` property, skipping `NetworkBehaviour`, `NetworkObject` and `NetworkManager`. If `OnValidate()` is missing or throws, the response carries `warning` and the entries stay unexpanded.
