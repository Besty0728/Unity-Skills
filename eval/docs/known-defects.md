# Known plugin defects (UnitySkills v2.9.0, 58d3339d)

Recorded only, not fixed in P0. When an attempt's failure matches a signature here and the
transcript shows the subject's request was within the documented contract, the attempt is
labelled `product_defect` in review and kept out of capability statistics (end-to-end tables
keep it).

| ID | Defect | Evidence | Signature |
|---|---|---|---|
| P1 | For skills without a native `entityId` parameter (component_*, material_*, prefab_*, event_*, light_*, camera_*, ui_*, animator_*), the router rewrites `entityId` to a path or name, so same-name objects are silently mis-targeted | `SkillRouter.Validation.cs:174-209`, `SkillRouter.cs:256-275` | Request passes `entityId`; the change lands on a different same-name object |
| P2 | `pageOffset`/`pageLimit` only take effect with `verbose=false`; the protocol doc says explicit paging always applies | `SkillRouter.Execute.cs:210-234,376-457`; `references/protocol-operating-mode.md:91` | Paged request ignored and the subject trusts it |
| P3 | `gameobject_find` / `scene_find_objects` truncate at `limit` (default 50) without a total or flag | `GameObjectSkills.cs:466-597`; probe `search_limits` | Reported set equals a 50-item response |
| P4 | `prefab_get_overrides` returns counts only (description says "list") and has no path parameter | `PrefabSkills.cs:237-343` | — |
| P5 | `event_add_listener` describes Object arguments the implementation does not support | `EventSkills.cs:76-211` | Object-arg listener request rejected |
| P6 | `material_create` hint recommends `asset_save`, which does not exist | `MaterialSkills.cs:430` | Subject calls `asset_save` after the hint |
| P7 | Missing and null object references both read as `"null"` | `SerializedPropertySkillUtility.cs:347-410` | — |
| P8 | `physics_create_material` fails with `INTERNAL Invalid path` when the save folder does not exist (`GenerateUniqueAssetPath` runs before the folder is created) | `PhysicsSkills.cs:330-340`; probe `so_reader` | Request with a new folder path fails |
| P9 | `component_get_serialized_properties` omits non-visible serialized fields such as `HingeJoint.m_ConnectedBody` (`ListProperties` uses `NextVisible`) | `SerializedPropertySkillUtility.cs:33-59`; probe `refs` | Subject cannot read back a joint target |
| P10 | `batch_query_*` `namePattern` is case-insensitive; the docs only say "regex" | `BatchSkills.cs:20,1086,1222` | Case variants included in a reported set |
| P11 | `batch_query_*` count is capped at `queryJson.limit` (default 500) without a truncation flag | `BatchSkills.cs:40,1273` | — |
| P12 | `prefab_find_instances` only sees active objects and counts children of instances as instances | `PrefabSkills.cs:444-447` | — |
| P13 | `event_get_listeners` reports target names only; same-name targets cannot be told apart | `EventSkills.cs:58-66` | — |
| P14 (unconfirmed) | Vector/Quaternion/Color descriptions use culture-sensitive interpolation while Float uses invariant G9 | `SerializedPropertySkillUtility.cs:365,803-808` | Non-English locale only |
