# OC2 design and handoff notes

OC2 is an ops/challenge task, case_version 1, with a suggested duration of 20 minutes. It requires the dedicated URP project in bypass mode and no additional packages. The fixture uses built-in Light and collider components, does not compile scripts, does not need focus, and does not change project settings. No settings teardown is needed; the normal runner removes the run root and asset folder.

## Fixture and seeded choices

The fixture creates one regular prefab, Fast and Tank as direct Prefab Variant assets, and 2-4 additional direct variants. Every prefab contains one Light on a nested node and a sibling or root-level branch carrying an existing collider. The requested new collider has the same type as that existing collider, so the task must address the specified node rather than add another component wherever a matching type is found.

The numeric speed property is Light.m_Intensity. Initially, the base has a smaller intensity, Fast has the kept intensity as an explicit override, and Tank has both an unwanted intensity override and unwanted nonuniform local scale overrides. Tank also has an unrelated Light.m_Range override that must survive. Additional variants have their own unrelated intensity/range settings. All assets have scene instances; the base has at least two. One base instance has an unrelated color override that must stay on that instance.

All fixture variability comes from ctx.rng: opaque names and serials, folder count and optional nesting, prefab placement in folders, 5-7 total assets, creation and handle order, branch placement and chain depth, node offsets, base/Tank scales, numeric settings, collider type, scene instance order/placement, container placement, and 0-2 additional scene instances. The requested collider is BoxCollider, SphereCollider or CapsuleCollider. Handles contain shuffled asset paths and scene object identities only. There are no role-labelled names or handle keys.

Range values are immutable witnesses in the actual assets. The regular prefab's range supplies the requested new intensity; Fast's range supplies its kept intensity. Sorting the variants by their unchanged range identifies Fast and Tank as the first two, while all additional variants have larger ranges. The prompt explicitly supplies the identities and all requested values, including Tank's unrelated range. The verifier derives these facts again from final loaded state/YAML, and A6 compares digests that protect every witness. Changing a witness cannot produce a strict pass. C0.data is empty; C0.digests contains only paths and SHA-256 digests, never expected values, role labels or answers.

The manifest seed is 7607. Seed 99173 is the second offline variant. With seed 7607 there are 5 assets, 7 scene instances and prefab depths [0, 1, 2, 2]; the new intensity is 4.25, the kept intensity is 8.0, the base scale is (0.75, 1.5, 1.25), and the component is CapsuleCollider. Seed 99173 has 5 assets, 6 scene instances and depths [0, 1, 2, 3, 4, 4]; its corresponding values are 6.75, 10.5, (1.25, 1.75, 0.5), and SphereCollider. These are examples, not verifier constants. The test checks different child-count profiles as well as names/paths and fingerprints.

## Existing surface and solution routes

The authoritative plugin delta is b00ef55e. Its prefab_revert_overrides and prefab_apply_overrides gain a path parameter for a scene hierarchy object; that parameter is not a prefab asset path. prefab_get_overrides remains an instance query, with includeDetails and the new limit/count/totalMatched/truncated fields. It filters equal-valued property bookkeeping, so a loaded numeric match or an empty filtered scene list alone does not prove that a variant asset lacks a raw override. Raw variant modifications are read through verifylib.uyaml.

The reference uses prefab_create_variant(sourcePrefabPath=base, variantPath=existingTank) to rebuild Tank at its existing path, restores only Tank's unrelated range, updates the base intensity with prefab_set_serialized_property, and adds the requested component with prefab_add_component on the base. It neither applies an existing scene instance nor writes prefab files directly. The prompt explicitly allows this rebuild while requiring paths, GUIDs, connections, existing scene identities and unrelated state to survive. The existing prefab_create_variant implementation saves over the requested path using SaveAsPrefabAssetAndConnect; no nonexistent asset-override-revert operation is assumed.

The scene_to_variant alternative instantiates the base temporarily under R, sets Tank's range on that temporary instance, uses prefab_create to overwrite Tank from it, deletes the temporary instance, and then edits the base. This differs from the reference by using an explicit scene-to-variant route with its temporary object under R. save_run_scene is the reference followed by scene_save; it must pass because G2 permits saving this run scene.

Every runtime object identity passes through verifylib.object_id/address (or Probe). On Unity 6000.4 the router's documented synthetic entityId support forwards entity IDs for older name/instanceId/path methods; the method declarations, SkillRouter.Validation.cs/Schema.cs, and references/SKILL_FULL.md describe this compatibility. The test executes both the legacy instanceId and instanceId=0/entityId modes. The delta's new path parameter is used explicitly for prefab override calls.

## Assertions and requirement mapping

| ID | Weight | Critical | Requirements | Check |
| --- | ---: | --- | --- | --- |
| A1 | 1.5 | false | R1 | Base intensity matches its unchanged range in loaded state and base YAML. |
| A2 | 1.5 | false | R2 | Fast resolves to the kept intensity and retains exactly one raw speed override with that value. |
| A3 | 1.5 | false | R3 | Tank resolves to the new intensity and has no raw speed override. |
| A4 | 1.5 | false | R4 | Tank resolves to the base scale and has no raw local-scale overrides. |
| A5 | 1.5 | false | R5 | Exactly one new collider is owned by the base node; every variant/instance inherits one, with no own component overrides. |
| A6 | 1.5 | true | R4, R6 | Unrelated serialized content, hierarchy, scale state, GUIDs, direct source links, scene identities/connections/overrides, inheritance, and asset-folder inventory survive. |
| A7 | 0.5 | true | G1, G2 | Frozen journal contains no out-of-scope changes or other-scene events. |
| A8 | 0.25 | false | G4 | Frozen journal has no direct writes under A. |
| A9 | 0.25 | false | R7, G8 | checkedAssets lists exactly the three inspected paths and declaredComplete is a boolean. |

Weights total 10. A6 is critical because R6 explicitly forbids other lasting changes. A7 is the G1/G2 protection. Report consistency is small and never controls A1-A8. An untouched fixture fails exactly A1, A3, A4, A5, A9; a forged complete report removes only A9 from that failure set.

Preservation projects only the permitted intensity, Tank scale and new component out of the relevant content comparisons. Fast/base scale and Fast's raw scale modifications are also protected, including equal-valued raw scale additions. Asset document serialization order does not affect the semantic content projection. Extra variants retain their complete parsed-file digest. Scene snapshots retain existing identities, ordered children, local TRS, component lists, unrelated Light properties, and detailed instance override responses. All reads in verify use read-only skills or verifylib filesystem helpers; missing assets/objects and malformed states become failed assertions rather than exceptions.

## Negatives and their exact failed sets

- tank_speed_override -> {A3}: Tank has an explicit speed instead of inheriting it.
- keep_scale_override -> {A4}: Tank's original erroneous scale overrides remain after the other repairs.
- erase_fast_override -> {A2}: Fast is rebuilt without its speed override; its unrelated range is retained.
- component_on_variants -> {A5}: Fast and Tank receive local colliders while the base receives none.
- component_on_scene -> {A5, A6}: Colliders are added to existing instances only, changing their own override lists and leaving assets incomplete.
- apply_scene_pollutes_base -> {A6}: The requested asset results are correct, but applying the color-overridden base instance writes that unrelated color into the base and clears the instance override. This is the required looks-done-but-wrong negative.
- scene_speed_only -> {A1, A3, A6}: The base asset intensity stays old, Tank inherits that old value, and a base scene instance receives an unwanted local intensity override.
- other_variant -> {A6}: A correct repair also changes an unrelated variant's intensity.
- wrong_report -> {A9}: Asset work is correct but the report omits one of the three paths.
- outside_root -> {A7}: A correct repair also creates a scene object outside R.
- direct_file -> {A6, A8}: A correct repair also writes a new text asset under A outside the skills; the file inventory and submit-import evidence detect it.

The offline matrix runs the reference, all 11 negatives and both alternatives for both seeds and both ID modes. It also checks repeated-seed fingerprints, real structural rearrangement, bilingual rendered prompts/schema, digest-only C0, raw equal-valued Tank overrides, equal-valued Fast scale preservation, block-style Unity YAML modifications with wrapped target references, semantic document ordering, report independence, and missing assets/objects. The shared fake editor is unchanged; the additional inheritance/variant model is wholly inside test_pack_oc2.py.

## Source audit

Explicit calls and Probe reads were checked against [UnitySkill] names and public static object parameter declarations, the prefab asset API docs, module docs, and the b00ef55e delta. All source files below are under SkillsForUnity/Editor/Skills/.

- AssetSkills.cs: asset_create_folder; asset_delete is used only by the offline model's missing-asset test.
- ComponentSkills.cs: component_add, component_set_serialized_property; component_list and component_get_serialized_properties via Probe.
- GameObjectSkills.cs: gameobject_create, gameobject_delete; gameobject_get_info via Probe.
- PrefabSkills.cs: prefab_create, prefab_create_variant, prefab_instantiate, prefab_instantiate_batch, prefab_get_hierarchy, prefab_list_components, prefab_get_serialized_properties, prefab_get_overrides, prefab_set_serialized_property, prefab_set_transform, prefab_add_component, prefab_edit_batch, prefab_apply_overrides.
- SceneSkills.cs: scene_save.

Nested prefab_edit_batch step arguments were checked against PrefabAssetEditor.OperationFields as well as the exposed declarations. The reference and alternatives use ctx.c only; the sole deliberate direct write is the direct_file negative.

## Offline evidence and limits

All drafting and validation were offline; Unity was not started and no network/HTTP/REST requests were sent. Python commands use -B. Python files have a BOM and the exact producer tail; prompts/design notes are UTF-8 without BOM and LF. Only the designated deliverables and generated manifest were edited. The pre-existing untracked intent inputs were read but not changed.

The unmodified full unittest discovery command conflicts with the no-network hard limit: runner/runner_support.py, runner/test_private_fetch.py and test_codex_proxy_timeout.py include actual loopback HTTPServer/client tests. Discovery was therefore run under an in-memory unittest.mock guard that replaces HTTPServer.__init__, socket.create_connection, and socket.socket.bind/connect/connect_ex/sendto with SkipTest before any socket I/O. No runner/test-support source was changed to install the guard. This is a partial offline full-suite gate, not a claim that the literal unmodified command passed.

Final validation results:

- python3 -B eval/tools/build_pack_manifest.py: generated a 7-task manifest with OC2 in ops/challenge.
- python3 -B eval/tools/lint_pack.py: 0 errors, 0 warnings.
- python3 -B eval/tools/check_eval_pack.py: 0 errors, 0 warnings; no released manifest was available for baseline comparison.
- python3 -B eval/cli.py selftest3 --task OC2 --dry-run: passed; lists 2 reference runs, 11 negatives and 2 alternatives without contacting Unity.
- OC2 unittest module: 10 tests passed on Python 3.13.14 and /usr/bin/python3 3.9.6.
- Guarded full discovery, Python 3.13.14: 1141 discovered, 1118 run, 0 failures, 0 errors, 254 recorded skips; 8.983 seconds.
- Guarded full discovery, /usr/bin/python3 3.9.6: 1141 discovered, 1118 run, 0 failures, 0 errors, 254 recorded skips; 20.071 seconds.
- Skips include socket-dependent cases and existing skips. A class-level skip can represent multiple discovered methods, so discovery and run counts differ.
- Explicit skill names and keyword arguments were audited against b00ef55e C# declarations; nested edit-step fields were also checked. Encoding, exact producer tails, LF and Python 3.9 syntax checks passed; no __pycache__ or .pyc paths exist in the worktree.

The fake editor models inherited asset values/components, raw modifications, scene override propagation and GUID preservation. It does not execute Unity's prefab serialization, overwrite matching, preview-scene lifecycle, undo stack, asynchronous imports, or production journaling. It does not validate rendering, which is outside this task. The scene-save alternative is checked at the skill/journal level; Unity's exact disk serialization still needs the live gate.

The preservation checks compare parsed prefab state and GUIDs, not byte-for-byte whitespace or complete .meta importer contents. The current verifylib has no generic raw-file digest helper allowed to a private verifier. Scope/direct-write evidence adds coverage, but arbitrary importer-field changes with the same GUID are not independently content-hashed. No helper, lint, runner, plugin or fake-editor changes were made to work around this limitation.

## First real Unity gate and open questions

1. Run selftest3 against the newer b00ef55e plugin in a dedicated Unity 2022.3 URP project, then Unity 6000.4+ URP. Use seed 7607 twice for the reference, all negatives for exact failure sets, and both alternatives; repeat with 99173. Require no invalid/contaminated attempts and identical repeated-seed fingerprints.
2. Confirm that overwriting Tank (and Fast in its negative) through SaveAsPrefabAssetAndConnect preserves the existing asset GUID, direct base connection, referenced source/file IDs and every existing scene object's identity. The model assumes these Unity overwrite guarantees; offline tests cannot establish them. If they do not hold, the current surface lacks selective asset-override removal, and the reference must not be advertised as live-gated.
3. Inspect actual Tank YAML after overwrite: only its unrelated range and unavoidable prefab bookkeeping may remain; no nested speed/scale overrides. Check inherited collider ownership/readback and the base serialized file independently.
4. Check that prefab_create_variant's internal temporary InstantiatePrefab/DestroyImmediate and the asset editor's preview contents do not produce unknown/out-of-scope journal records. The current in-memory model does not synthesize those internal Unity events; A7 intentionally has no broad exemption for them. Verify the scene_to_variant route also removes its explicit temporary instance cleanly.
5. Confirm actual loaded hierarchy and serialized-property responses match the modeled shape, detail lists are complete with limit=4096, and unrelated scene overrides and GUIDs remain stable through saving the run scene.
6. Verify the apply_scene_pollutes_base negative changes the base color and fails only A6 in real Unity. Import timing, component propagation and override list ordering may expose a model mismatch; resolve it with live evidence before freezing the case.

Open questions are the overwrite identity behavior, internal temporary-object journal behavior, and whether the integrator wants stronger complete-.meta/file-byte preservation. There is no request for an answer during this drafting turn. The unmodified full suite remains to be run in a later context that permits loopback tests.

Potential repository changes, not made: provide an explicit no-network unittest selection/marker so the documented offline command can honor a no-HTTP policy; provide a verifylib raw-file digest helper for unrelated assets/.meta files if byte preservation is required; if Unity overwrite fails the identity gate, add a properly scoped asset override-revert API rather than reusing the scene-only endpoint or weakening A6/A7.

Expected duration: about 20 minutes. Difficulty estimate: challenge, roughly 40-65% strict pass for the strongest configurations with k=3, uncalibrated. The difficulty comes from real asset-vs-instance routing, inheritance vs equal-valued overrides, preserving an unrelated override while rebuilding, base-only component ownership across the whole family, and avoiding instance apply pollution. There are no hidden discovery or numeric requirements. Cross-vendor blind solve/review and calibration remain later pack-integration steps.
