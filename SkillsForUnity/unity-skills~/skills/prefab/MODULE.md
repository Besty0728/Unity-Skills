# Unity Prefab Skills

Scene prefab instances, prefab assets and variants. Parameters come from the schema (`GET /skills/schema?category=Prefab&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Parameters: `reference/<skill_name>.md`.

## Rules
- Batch first: use `prefab_instantiate_batch` for 2 or more instances, and the `_batch` variant of an asset edit for several assets.
- Instance skills target `entityId` (preferred), `name`, `instanceId` or `path`. Instance writes stay on the instance until `prefab_apply` or `prefab_apply_overrides`.
- Asset edits take `prefabPath` and a root-relative `gameObjectPath` (empty means the root), and save only that asset. Variant and nested edits stay overrides; they never change the base prefab.
- Duplicate sibling names are rejected with candidates and `siblingIndex`. Component indices are zero-based among components of exactly the requested type (`prefab_list_components` reports them).
- `prefab_edit_batch` runs ordered edits, all or nothing: `steps` holds 1–50 operations, each one of the 12 prefab asset writes; no reads or scene skills.
- Reference fields take a project asset, a node inside this prefab, or an explicit clear; never a scene object.
- Operating mode: instance and asset reads are `SemiAuto`; writes are `FullAuto`. Component and node deletion and `prefab_edit_batch` carry Delete or high metadata: refused under Approval and Auto.
- DryRun does not load prefab contents; a step that depends on an earlier one is validated when it runs.

## Pitfalls
- Root deletion, reparenting or duplication, transform replacement or removal, cycles, script and bookkeeping property writes, and Unity-invalid inherited structure changes are rejected.
- `prefab_set_property` keeps its legacy parameters; new code uses `prefab_set_serialized_property`.

## Routing
- Components on a scene instance → `component` module, then `prefab_apply`.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`prefab_create`](reference/prefab_create.md) | Create a prefab from a scene object |
| [`prefab_apply`](reference/prefab_apply.md) | Apply instance changes to the source |
| [`prefab_unpack`](reference/prefab_unpack.md) | Break the prefab connection |
| [`prefab_get_overrides`](reference/prefab_get_overrides.md) | Count or list instance overrides |
| [`prefab_revert_overrides`](reference/prefab_revert_overrides.md) | Revert an instance's overrides |
| [`prefab_apply_overrides`](reference/prefab_apply_overrides.md) | Apply an instance's overrides to the source |
| [`prefab_create_variant`](reference/prefab_create_variant.md) | Create a variant of a prefab asset |
| [`prefab_find_instances`](reference/prefab_find_instances.md) | Find instance roots in the scene |
| [`prefab_set_property`](reference/prefab_set_property.md) | Legacy one-property asset write |
| [`prefab_get_info`](reference/prefab_get_info.md) | Read one node of a prefab asset |
| [`prefab_get_hierarchy`](reference/prefab_get_hierarchy.md) | Read the node tree of an asset |
| [`prefab_list_components`](reference/prefab_list_components.md) | List components per node |
| [`prefab_get_serialized_properties`](reference/prefab_get_serialized_properties.md) | Read a component's serialized properties |
| [`prefab_edit_batch`](reference/prefab_edit_batch.md) | Ordered asset edits, all or nothing |
| [`prefab_instantiate`](reference/prefab_instantiate.md), [`prefab_instantiate_batch`](reference/prefab_instantiate_batch.md) | Instantiate several prefabs in one call |
| [`prefab_add_component`](reference/prefab_add_component.md), [`prefab_add_component_batch`](reference/prefab_add_component_batch.md) | Add a component to a node |
| [`prefab_remove_component`](reference/prefab_remove_component.md), [`prefab_remove_component_batch`](reference/prefab_remove_component_batch.md) | Remove a component |
| [`prefab_set_component_enabled`](reference/prefab_set_component_enabled.md), [`prefab_set_component_enabled_batch`](reference/prefab_set_component_enabled_batch.md) | Enable or disable a component |
| [`prefab_set_serialized_property`](reference/prefab_set_serialized_property.md), [`prefab_set_serialized_property_batch`](reference/prefab_set_serialized_property_batch.md) | Write one serialized property |
| [`prefab_create_gameobject`](reference/prefab_create_gameobject.md), [`prefab_create_gameobject_batch`](reference/prefab_create_gameobject_batch.md) | Create a node |
| [`prefab_delete_gameobject`](reference/prefab_delete_gameobject.md), [`prefab_delete_gameobject_batch`](reference/prefab_delete_gameobject_batch.md) | Delete a node (refused under Approval) |
| [`prefab_duplicate_gameobject`](reference/prefab_duplicate_gameobject.md), [`prefab_duplicate_gameobject_batch`](reference/prefab_duplicate_gameobject_batch.md) | Duplicate a node subtree |
| [`prefab_rename_gameobject`](reference/prefab_rename_gameobject.md), [`prefab_rename_gameobject_batch`](reference/prefab_rename_gameobject_batch.md) | Rename a node |
| [`prefab_set_transform`](reference/prefab_set_transform.md), [`prefab_set_transform_batch`](reference/prefab_set_transform_batch.md) | Set a node's transform |
| [`prefab_set_parent`](reference/prefab_set_parent.md), [`prefab_set_parent_batch`](reference/prefab_set_parent_batch.md) | Reparent a node |
| [`prefab_set_active`](reference/prefab_set_active.md), [`prefab_set_active_batch`](reference/prefab_set_active_batch.md) | Set a node's active state |
| [`prefab_set_sibling_index`](reference/prefab_set_sibling_index.md), [`prefab_set_sibling_index_batch`](reference/prefab_set_sibling_index_batch.md) | Reorder a node among its siblings |


**DO NOT** (common hallucinations):
- `prefab_create_from_object` does not exist: use `prefab_create`.
- `prefab_spawn` does not exist: use `prefab_instantiate`.
- `prefab_edit` and `prefab_modify` do not exist: use `prefab_edit_batch`.
- `prefab_save` does not exist: use `prefab_apply`.

