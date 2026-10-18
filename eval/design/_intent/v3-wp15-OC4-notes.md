# OC4 renderer audit — draft handoff

OC4 is an ops/challenge task, case version 1, with a 30-minute budget. It requires URP, bypass mode and a dedicated project, with no additional UPM packages. This draft was developed without Unity, HTTP or network access. No shared template, runner, plugin or fake-editor file was changed.

## Fixture and seeded variants

The fixture contains exactly 240 GameObjects with Transform and MeshRenderer components. There is deliberately no MeshFilter or geometry: the subject is the renderer's serialized materials array, not visual output. All objects are descendants of one named group under R. Beneath the group are 5–8 top-level branches, each a chain of 2–4 objects. Renderers are distributed among different depths. Some entire branches and some individual renderer objects are inactive.

The fixture creates 2–4 asset folders under A, 2–3 Standard materials, 2–3 URP/Lit materials, one additional URP/Lit asset designated Fallback by its exact path in the prompt, and one disposable material. Every renderer starts with 4–6 shuffled slots. Each has at least one Standard slot, one slot referencing the disposable material, one explicitly cleared slot, and one other assigned slot. The remaining slots vary. The surviving materials all occur in the arrays, including Fallback. All material and object names are unique seeded opaque names; the Fallback designation comes from the prompt's asset path.

Objects, components, materials and array writes use batch skills, in chunks of 50. The run scene is saved while the disposable material exists; `asset_delete` then deletes that asset through the plugin, and the runner saves the run scene before snapshotting. This is intended to leave missing references rather than empty references. Snapshot refuses the fixture if any renderer lacks the assigned, missing and none states, or if a requested shader silently fell back to another shader. There are no project-setting changes to restore; teardown returns normally and the runner owns R/A cleanup.

`ctx.rng` controls folder/material/object names, folder assignment, material creation order, shader population, branch count and depth, renderer parent placement, creation order, transforms, array lengths, slot ordering, extra slots, material choices and activation. The required count 240, semantic shader names, component types and implementation chunk size are fixed. Offline test seeds are 7607 (public/default) and 88117 (the second test variant, absent from the manifest); they have rearranged structure and names and identical solution verdicts. Repeating a seed with a different run ID produces the same normalized fingerprint and layout in the model. No verifier expectation hard-codes a seeded name, path, branch distribution or slot count.

## C0 and acceptance

Handles contain only object identities and paths, material paths and the designated material path needed to render the prompt. C0 data contains material path/identity bindings. C0 digests cover original GameObject information, Transform serialized fields, non-material MeshRenderer fields, array sizes, each slot's original reference token, material shader names, and material asset signatures. A token is assigned plus an identity, none, or missing; only its digest is stored. There are no stored classifications, expected replacements or reported counts.

The private verifier reconstructs original classifications from the slot digests, material identities and original shader digests. It reads every original renderer's complete final materials array independently of any query and independently of the agent's answer. Empty and missing references remain separate even when both serialize to the text `null`. Material identity is tested, so another asset with the same shader cannot substitute for Fallback. All original objects, Transform fields, other renderer fields, array sizes and existing material assets are also compared with C0. Asset signatures use read-only probes plus verifylib's YAML/GUID helpers, not direct file opens in the private module. IDs pass through verifylib `object_id`, `same_id` and `address` as applicable.

Every condition below is explicit in both prompt languages or in the runner's G prefix. Critical assertions are limited to explicit prohibitions and scope/scene protection. The only report-based assertion is worth 0.5 points.

| ID | Weight | Critical | Requirements | Condition |
|---|---:|---|---|---|
| A1 | 2 | false | R1, R2 | Every original Standard slot uses the exact Fallback asset. |
| A2 | 2 | false | R1, R3 | Every original missing slot uses the exact Fallback asset. |
| A3 | 1 | false | R4 | Original empty slots remain empty at their original indexes. |
| A4 | 1.5 | false | R4 | Other assigned slots retain their original identities and indexes. |
| A5 | 1 | true | R5 | Objects, hierarchy, components, other properties and array sizes are unchanged. |
| A6 | 1 | true | R6 | Existing material assets, their paths and GUIDs are unchanged. |
| A7 | 0.5 | false | R7, G8 | Six integer counts describe the original state; completion is a boolean. |
| A8 | 0.5 | true | G1, G2 | Scope remains within R/A and no other scene is touched. |
| A9 | 0.5 | false | G4 | No direct file writes under A bypass the skills. |

## Skill-surface traps and solutions

The concept card's shorthand needs one precision: current `batch_query_gameobjects` defaults to query limit 500 and sample limit 20. Thus 240 renderers do not exceed the default match cap; they do exceed the default returned sample. The response spells the total `totalMatched`, not `totalCount`. `truncated` describes the match cap, while `count` can exceed the length of `objects` because of the separate sample cap. The reference first uses a limit/sample of 100, then raises both after examining the response. Separate negatives cover ignoring either cap. The prompts require complete coverage even when queries return a limited list or sample; they make no false claim about a particular default limit.

`material_assign` writes only slot 0. The working route reads the serialized array and writes each eligible `m_Materials.Array.data[index]` with `component_set_serialized_property_batch`, exact `assetPath` and `objectType="Material"`. Batch item schemas lack `entityId`, so batch writers use exact hierarchy paths. Single-object readers use verifylib locators; `SkillRouter.Schema.cs` provides the synthetic `entityId` locator for older signatures carrying `instanceId` and `path`. The supplied b00ef55e delta's strict locator/type behavior is compatible with the explicit path and Material asset binding used here.

The reference inventories all renderers, obtains all original slot classifications before changing anything, and batches the replacements. The report contains original renderer/slot counts and four disjoint slot-class counts. Alternatives are `hierarchy_single_writes` (walk children and use single property writes), `find_and_batch` (use `gameobject_find` and its total/truncation fields), and `save_run_scene` (reference route followed by `scene_save`). All three strict-pass in the offline model; saving the run scene is allowed by G2.

Each negative deliberately isolates one defect; where needed it audits all original slots first so its report remains correct and does not add A7 to its failed set.

| Negative | Exact failed set | Reason |
|---|---|---|
| only_active_branches | A1, A2 | Looks complete on visible objects but leaves inactive branches/objects unrepaired. |
| first_query_limit | A1, A2 | Repairs only the first 100 query matches and ignores truncation. |
| first_query_sample | A1, A2 | Repairs only the returned 20-object sample despite 240 matched objects. |
| material_assign_only | A1, A2 | Repairs eligible slot 0 entries but misses eligible higher indexes. |
| conflate_null | A3 | Also fills the originally empty slots. |
| skip_missing | A2 | Repairs only Standard slots. |
| replace_valid_slots | A4 | Overwrites the other assigned materials after the proper repair. |
| same_shader_other_asset | A1, A2 | Uses a different URP/Lit asset instead of the exact Fallback asset. |
| resize_array | A5 | Appends one slot after correct repair. |
| edit_standard_shader | A6 | Edits a Standard material asset after correct repair. |
| inaccurate_counts | A7 | Increments one reported original classification count. |
| outside_root | A8 | Creates a helper outside R after correct repair. |
| direct_file | A9 | Writes a new text file directly under A after correct repair. |

## Confirmed plugin declarations

The following authored calls and their keyword names were checked against the C# declarations, batch item types, corresponding module SKILL.md files and shipped protocol references. The offline client additionally rejects undeclared top-level keywords and unsupported component-batch item keywords. Runtime tests include both legacy instance IDs and Unity 6000.4-style entity IDs with zero instance IDs.

All source paths below are relative to `SkillsForUnity/Editor/Skills/`.

| Source | Skills and parameters used |
|---|---|
| AssetSkills.cs | `asset_create_folder(folderPath)`, `asset_delete(assetPath)`, `asset_get_info(assetPath)` |
| MaterialSkills.cs | `material_create_batch(items)` with name/shaderName/savePath items; `material_get_properties(path)`; `material_assign(materialPath, instanceId/entityId)`; `material_set_shader(path, shaderName)` |
| ComponentSkills.cs | `component_add_batch(items)` with path/componentType; `component_get_serialized_properties(componentType, includeChildren, includeHidden, limit, instanceId/entityId)`; `component_set_serialized_property(componentType, propertyPath, value or assetPath, objectType, path or instanceId/entityId)`; `component_set_serialized_property_batch(items)` with path/componentType/propertyPath/value/assetPath/objectType |
| GameObjectSkills.cs | `gameobject_create(name, parentPath, x, y, z, rotY)`; `gameobject_create_batch(items)` with the same item fields; `gameobject_get_info(instanceId/entityId)`; `gameobject_find(component, limit)`; `gameobject_set_active(path, active)`; `gameobject_delete(instanceId/entityId)` only in the verifier-robustness test |
| BatchSkills.cs | `batch_query_gameobjects(queryJson, sampleLimit)`; query keys componentType/includeInactive/limit |
| SceneSkills.cs | `scene_save()` |

## Offline coverage and gate status

The self-contained `test_pack_oc4.py` subclasses the shared FakeEditor inside the new file only. It adds material identities, array resize/slot writes, separate missing/empty states, asset deletion effects, shader changes, component batches, inactive hierarchy queries and separate query/sample caps. Its tests block socket creation. It exercises the reference, every negative and every alternative for both test seeds and both ID formats; repeats fingerprint and full-layout comparisons; checks rearranged structure; and verifies untouched fixtures fail only A1/A2/A7 (A1/A2 with a correct original-state report). Malformed reports, a deleted renderer and truncated property reads fail assertions without crashing. Eight tests pass under both `python3` (3.13.14) and `/usr/bin/python3` (3.9.6).

Manifest generation, pack lint (zero errors and warnings), pack consistency checking and `selftest3 --task OC4 --dry-run` pass. The manifest is a scratch integration artifact and preserves the existing pack-wide version; only the integrator should choose the combined pack version.

The exact requested full-discovery command is deferred: several existing tests start loopback HTTP servers and issue HTTP requests (`runner_support.Bench`, private-fetch tests, Codex proxy tests and the end-to-end offline scenario fixture). Executing those conflicts with the task's explicit ban on all HTTP, including localhost. Shared tests were not changed to bypass the restriction. The integrator must run full discovery with both Python interpreters in an environment that permits those tests.

A conservative discovery subset passes on both Python 3.13.14 and 3.9.6: 844 tests each, with one existing platform skip, and socket creation blocked throughout test execution. This includes OC4 and all six basic-task in-memory tests. An initial subset selection also reached `e2e.test_offline.OfflineScenarios.setUpClass`; the socket guard stopped its server construction before any socket opened or HTTP request occurred. Excluding that fixture produced the green subset above. The subset was selected by an in-memory loader; no test source or runner module was edited.

To reproduce the subset, discover `eval/tests` but exclude these modules before import: `runner.test_pack_basic_flow`, `runner.test_private_flow`, `runner.test_runner_cli`, `runner.test_runner_fixture`, `runner.test_runner_flow`, `runner.test_runner_integration`, `runner.test_runner_rest`, `runner.test_runner_states`, `test_codex_proxy_timeout`, `test_codex_subject`, `test_selftest3`, `test_subject_timeout_cleanup`, `e2e.test_offline` and `e2e.test_live`. Also exclude `runner.test_runner_task_api.Unity6000_4Tests`, `runner.test_runner_paths.InstanceTests` and `runner.test_runner_lock.CliLockTests`; retain only `ClassifyTests` from `runner.test_private_fetch`. These class filters exclude 63 tests. Some pure tests are conservatively omitted with their transport/subject-launch modules. Execute the remainder inside a `unittest.mock.patch` of `socket.socket` that raises if called.

## Native limitations, open questions and first real gate

1. Confirm `Shader.Find("Standard")` resolves in each dedicated URP project. The fixture checks the returned shader and fails setup rather than accepting the material API's documented fallback.
2. Confirm deleting the disposable material with `asset_delete` leaves missing references across the runner's subsequent scene save on Unity 2022.3 and 6000.4+. The in-memory model represents this behavior; it cannot prove Unity's deletion/serialization semantics. Every renderer's snapshot checks distinct missing and none states before the agent starts.
3. Confirm assigning material-array references changes no unrelated native renderer/Transform bookkeeping and that `scene_save` keeps protected property and asset signatures stable. The model cannot validate native serialization, Undo, importer behavior or Unity's journal. Inspect A5/A6 stability and G2/G4 evidence for `save_run_scene` first.
4. Measure fixture build/snapshot and complete verifier time with 240 renderers. The fixture batches mutations and snapshot queries; the verifier deliberately reads every renderer. The offline model cannot predict real editor latency or workflow snapshot cost.
5. Run reference twice at seed 7607 and compare fingerprints, then repeat at seed 88117, with all 13 exact-negative checks and all three alternatives. Start on 2022.3 URP as required by the process; repeat the ID-sensitive/material-deletion cases on 6000.4+. Inspect an inactive renderer with eligible higher-index slots, an originally empty slot, an unchanged valid slot and the exact Fallback identity.
6. Run the other-provider public-only ambiguity review and blind solve, then the private assertion review. These review gates and empirical agent calibration remain outstanding; no other model or Unity editor was invoked in this draft.

Expected time is about 30 minutes. Estimated strongest-configuration k=3 pass rate is 35–65% (unmeasured), within the challenge target. Difficulty comes from complete inactive traversal, two independent result caps, original-state counting, null/missing distinction and exact per-index asset binding over 240 arrays. It does not depend on hidden object-role labels or guessing shader defaults.

No plugin, runner or template change appears necessary from the source review. The lead/integrator should reconcile the full-suite requirement with the no-HTTP drafting restriction and run the deferred full-suite and native gates. Any native finding should be resolved before freezing case version 1; pack-wide release/version decisions remain with the integrator.
