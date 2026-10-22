# WP15 — OC1 / OC3 design notes (wp15-oc-a)

Status 2026-10-17: drafted offline in worktree `eval-v3`; lint 0 errors, selftest3 dry-run lists the cases, a
synthetic-world smoke test of both verifiers passes (not committed, `/tmp/oc-smoke/smoke.py`). Live URP gate pending.
Nothing committed; the shared `pack/manifest.json` is not rebuilt yet (OC7 and T* ports by other members are
mid-flight and make `build_pack_manifest` fail); a temp pack at `/tmp/oc-pack` (basic six + OC1 + OC3) passes
`build_pack_manifest`, `lint_pack` (0/0), `check_eval_pack` (0/0) and `selftest3 --dry-run`.

Note for the gate: `selftest3 --task` is `nargs="*"`, so write `--task OC1 OC3` (a repeated `--task` keeps only the
last one).

## OC1 — nested prefab muzzle (ops / challenge, 20 min, caseVersion 1)

### Fixture (seed-driven)
- `A/<Prefabs/Vehicles|Armory|Units>/{Barrel,Turret,Tank}.prefab`, built bottom-up from one scene hierarchy with
  `prefab_create` so Turret nests Barrel and Tank nests Turret. Seeded structure: the Turret node sits at `Turret` or
  `Hull/Turret` inside Tank; the Barrel node at `Barrel` or `Mount/Barrel` inside Turret (prompt gives the latter as
  `{barrelNode}`). Barrel has children `Tube` and `Tip` (Tip is a mild distractor: Muzzle must be a direct child).
- `R/<Garrison|Motorpool|Depot>/<Bay_x>/<instance>`: two Tank instances and one Turret instance, seeded names,
  bays and order. Instances sit at local (0,0,0) under their bay so `prefab_get_overrides` reports no root
  transform entries (the plugin counts root position as an override; the bays keep the override lists clean).
- One Tank instance (seeded) carries a Barrel `localEulerAngles` override (seeded angle) and an added AudioSource
  on its Turret node. The verifier derives it from C0 (the instance with non-zero override counts).
- Muzzle pose is a pure function of the seed (`muzzle_pose(seed)` duplicated in task.py and the private module):
  position (0, y, z), Euler one of (0,0,0)/(90,0,0)/(0,90,0), scale 1 or 0.5. Handles stay identities/paths.

### Traps
- Root-relative `gameObjectPath` for the asset API (`Mount/Barrel`), not the scene path.
- Adding Muzzle on the scene instances "looks done" (visible everywhere) but is an instance override.
- `prefab_apply` on a Tank instance writes into Tank.prefab (and drags the existing overrides along).
- Editing Barrel.prefab also makes Muzzle appear everywhere; the YAML check (Muzzle must be a GameObject document in
  Turret.prefab whose Transform's father is the stripped Transform of the Barrel PrefabInstance) separates it.
- `prefab_set_transform` axes are local Euler; `rotX/Y/Z` of the scene skill are world.
- `prefab_get_overrides` needs `includeDetails` to see what the carrier instance has; the answer asks for the total.

### Assertions
A1 placement + YAML attachment (2.0) · A2 pose and Transform-only (1.5) · A3 Barrel/Tank files unchanged (1.5,
critical) · A4 other Turret nodes unchanged, still one nested Barrel instance (1.0) · A5 override lists/counts and the
carrier's Barrel pose unchanged (1.5) · A6 scene instances show Barrel/Muzzle with the pose (1.0) · A7 container
structure minus Muzzle and the asset listing unchanged (0.5, critical) · A8 answer consistent with the state (0.5) ·
A9 G1/G2 (0.25, critical) · A10 G4 (0.25).
A4 drops the root node's name and local pose from the comparison so the `apply_turret_instance` alternative (which
may push the instance root's values into the asset) is not punished. A8 compares the reported Muzzle rows with every
object called Muzzle under the three instances (wherever they sit), so a misplaced Muzzle is charged once (A1/A6),
not twice.

### Negatives (expected failure sets)
| name | why it fails |
|---|---|
| scene_instances | Muzzle created on each instance: no asset node (A1, A2), each override list gains addedGameObject (A5) |
| tank_prefab | Muzzle added inside Tank.prefab: Turret lacks it (A1, A2), Tank changed (A3), Turret instance shows none (A6) |
| apply_tank | Muzzle on the carrier instance then `prefab_apply`: Tank changed (A3), carrier overrides applied away (A5), other Tank gains AudioSource (A7), Turret instance shows none (A6), A1/A2 |
| wrong_parent | Muzzle under the Turret root: A1, A2 (no node at Barrel/Muzzle), A6 |
| wrong_pose | swapped position, scale+1: A2, A6 |
| with_component | a BoxCollider on Muzzle: A2 (a Light would make URP add UniversalAdditionalLightData to every instance: A5, A8) |
| revert_overrides | `prefab_revert_overrides` on the carrier: A5 (lists, Barrel pose) and A7 (AudioSource gone from the structure) |
| extra_object | a Spare object under the container: A7 |
| move_dome | another Turret node moved in the asset: A4 |
| barrel_prefab | Muzzle inside Barrel.prefab: A1 (not attached in Turret's YAML), A3 |
| wrong_report | overrideCount off by one: A8 |
| outside_root | stray scene-root object: A9 |
| direct_file | a file written under A: A7 (listing) and A10 |

Alternatives: `edit_batch` (`prefab_edit_batch` create + set_transform), `apply_turret_instance` (scene Muzzle under
the standalone Turret instance, then `prefab_apply` of that instance, which has no other override), `save_run_scene`.

### Not verifiable offline (live gate will tell)
- Whether `prefab_create` from a child object of a scene hierarchy nests the already-connected Barrel instance as
  expected and whether `prefab_instantiate_batch` instances under bays really report zero overrides.
- `apply_tank`: `prefab_apply` first pushes genuine property overrides into source objects, then
  `ApplyPrefabInstance`; the predicted end state (Tank.prefab changed, carrier clean, other Tank gains the
  AudioSource) is what the set {A1,A2,A3,A5,A6,A7} assumes. If Unity refuses part of it, the set changes.
  Live 2022.3 URP (2026-10-20): as predicted, plus A9 from a plugin bug: the journal recorded the Barrel pose written
  into Tank.prefab as a property change with no scene and no assetPath, which the scope check could not place under A
  (OC2's apply_scene_pollutes_base hit the same, as A7). EditorChangeTrackerService now adds assetPath to property
  changes of objects inside an asset.
- `apply_turret_instance`: assumes Unity does not apply the instance root name; A4 tolerates root name/pose changes.
- `m_LocalEulerAnglesHint` entries may or may not appear in the carrier's override list; C0 and the final read come
  from the same editor, so equality holds either way.
- Prefab asset edits import the saved file inside the skill (REST depth), so `direct_writes` should stay empty.

Expected difficulty: challenge, 25-55% for top models (the asset API and the override semantics are the gates).

## OC3 — loot table batch wiring (ops / challenge, 30 min, caseVersion 1)

### Fixture (seed-driven; compiles two scripts at build time, like v1 T1 — approved by the lead with constraints)
- `A/Scripts/{LootTable,Loot}.cs` in the task namespace `UnitySkillsEval.Tasks.OC3` (`LootTable : ScriptableObject
  {int tier; string label}`, `Loot : MonoBehaviour {LootTable table; float dropChance = 1f}`) via
  `script_create_batch`, then `wait_job` + `wait_idle`. Type and field names are fixed across seeds (only assets,
  folders and objects are seed-named). Teardown's `asset_delete` of the run folder causes the second reload (runner
  waits). `needs_focus = True`.
- Residue guard before the scripts are written: `script_list(folder="Assets", filter="Loot")` must list no script
  named `Loot` or `LootTable`, and `scriptableobject_list_types(filter="LootTable")` no type named `LootTable`;
  otherwise the build raises a clear RuntimeError instead of compiling a duplicate. After the build the fixture also
  proves its own types were used: every LootTable `.asset` must carry `m_Script.guid` of `A/Scripts/LootTable.cs`
  (`scriptableobject_create` resolves `typeName` by simple name), and every base prefab must carry exactly the
  intended number of Loot components of `A/Scripts/Loot.cs`'s GUID.
- R5 tells the agent the two scripts under `{scripts}` are part of the setup and must not be modified; C0 stores
  sha256 of each script's text (`script_read`, a read-only skill) and B4 (critical) compares; B7 additionally fails
  on any journal file record of a `.cs` under A.
- `A/<LootTables|Drops|Tables>/LootTable_{Common,Rare,Elite,Bonus}.asset` plus a second `LootTable_Common.asset`
  under `A/<Archive|Old|Backup>` (tier 0, label "Archived").
- `A/<Enemies|Monsters|Foes>/<zone>/<Common|Rare|Elite>[/<sub>]/<name>.prefab`: 33 base enemy prefabs (Capsules with
  one Loot), 3 zones, two tier folders with a sub-folder (`Rare/Bosses`), one tier folder nested inside another tier
  folder (e.g. `Ruins/Rare/Elite`, holding at least two prefabs), one prefab with two Loot components, two prefabs
  without Loot (`Chest`, `Totem`), three variants (one in its base's folder, one in a folder of another tier, one
  anywhere; their bases are never pre-wired), seven pre-wired tables (4 right, 2 wrong tier, 1 archive decoy) and
  five seeded `dropChance` values.
- Handles carry only paths (enemies/loot folders, the four table paths, all 38 prefab paths, the two scripts); C0
  stores full and masked YAML digests per file (`table`, `m_RootOrder`, `m_LocalEulerAnglesHint` masked), the Loot
  component count per prefab, table GUIDs, the Loot script GUID and the asset listing. Roles (variant, two-Loot,
  decoy) are derived from the files at verify time.

### Traps
- Tier = nearest Common/Rare/Elite ancestor (the nested tier folder breaks "first segment" readings).
- Two `LootTable_Common` assets: searching by name finds both; only the loot folder's one counts (GUID on disk).
- Two Loot components on one prefab: `componentIndex` 1 -> Bonus table.
- Variants must not be written (an asset edit on a variant stores an override in the variant file), even when the
  variant sits in a folder of another tier; they inherit from the base.
- Pre-wired wrong references must be corrected (filling only empty ones is not enough).
- 34 writes fit one `prefab_set_serialized_property_batch` (limit 50); `prefab_set_property` has no componentIndex.

### Assertions
B1 fraction of base prefabs whose first Loot references the tier table by GUID (3.5) · B2 further Loot components
reference Bonus (1.0) · B3 variant files unchanged (1.5, critical) · B4 masked digests of base prefabs, full digests of
the other prefabs and of every `.asset`, table GUIDs, the two scripts' text hashes and the file set unchanged (1.5,
critical) · B5 answer rows equal the real references of every base Loot component, variants listed, GUID of the loot
folder's Common table (1.0) · B6 G1/G2 (0.75, critical) · B7 G4 plus no `.cs` file record under A (0.75).

### Negatives
| name | why it fails |
|---|---|
| by_name | Common tier wired to the archive asset: B1 (15 of 33 wrong), B5 (reported GUID is the archive's) |
| variants_too | variants written as well: B3 |
| variant_instead_of_base | one base skipped, its variant written: B1, B3 |
| skip_prewired | only empty tables filled, the 3 wrong ones stay: B1 |
| both_loots_tier | second Loot gets the tier table: B2 |
| outermost_tier | first tier folder from the top: B1 (prefabs in the nested tier folder) |
| edit_dropchance | a dropChance changed: B4 |
| wrong_report | two rows' tables swapped: B5 |
| outside_root | stray scene-root object: B6 |
| direct_file | a file written under A: B4 (listing) and B7 |

Alternatives: `single_calls` (one `prefab_set_serialized_property` per component), `legacy_mixed`
(`prefab_set_property` with `assetReferencePath` for index 0, the indexed API for the rest), `save_run_scene`.

### Not verifiable offline
- Build-time compile through `script_create_batch` -> `wait_job` -> `wait_idle` on the runner's client (v1 T1 did the
  same with `script_create`); the residue guard and the post-build GUID checks make a clash with project types a
  clear build failure rather than a wrong fixture.
- Whether Unity's resave of a base prefab (`SaveAsPrefabAsset` after the agent's edit) keeps every other YAML field
  byte-identical; `m_RootOrder` and `m_LocalEulerAnglesHint` are already masked, more keys may need masking if the
  reference fails B4 at the gate.
- `prefab_set_property` (legacy) with `assetReferencePath` on a custom ScriptableObject field.
- The YAML reader (`verifylib.uyaml`) on real 2022.3/6000.x prefab and variant files; the smoke test used
  hand-written documents in the same shape.

Expected difficulty: challenge, 20-50% (enumeration across 38 files, variant detection and the GUID distinction).

## Open items for the lead
- Shared manifest rebuild: blocked by other members' incomplete task folders (OC7 without prompts, T5 without
  solutions at the time of writing); wp11-tasks asked for the hand-off.
- `SkillsForUnity/unity-skills~/scripts/__pycache__` exists in the worktree (not from my runs, all with `-B`).
