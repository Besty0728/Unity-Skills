---
name: unity-skills
description: Operate the Unity Editor through a local REST API — create, edit and batch-edit GameObjects, components, scripts, scenes, prefabs, assets and materials, run tests — and answer Unity design questions (architecture, script design, patterns) from bundled advisory docs. Use for any Unity Editor task or Unity design question, in any language.
compatibility: Unity 2022.3+/6000.x with the UnitySkills package (REST on localhost:8090-8100); Python 3 for the optional client
---

# Unity Skills

> Docs are English-only: match requests by meaning in any language; reply in the user's language.

## Route first

- **Automate** — the user asked you to act, or it needs traversal, search, batch, exact numbers or many objects: drive the Editor via REST.
- **Guide** — the user wants to do it themselves, or `surfaceProfile` is `guide`: name menus and Inspector fields per [SKILL_GUIDE.md](SKILL_GUIDE.md).
- **Advise** — a design question that changes nothing: answer from its advisory doc ([scriptdesign](skills/scriptdesign/SKILL.md), [architecture](skills/architecture/SKILL.md), [patterns](skills/patterns/SKILL.md), all in the [module index](skills/SKILL.md)), no REST calls.

## Target the right Editor

Ports `8090`–`8100` go first come, first served: add `?expectProject=<project>` (productName or folder name) or the exact `?expectInstance=<instanceId>` to every write; a different Editor runs nothing (409 `INSTANCE_MISMATCH`).

`GET /health` has `currentMode`, `surfaceProfile`, `dryRunPolicy`, `instanceId`: probe it once in the same shell command as your first request (no extra call), then only after a refused write, 409, 503/refused connection or odd mode. `bypass` runs every write directly. Permission, not dryRun: `auto` and `approval` refuse skills whose schema has `approvalBehavior` `forbid` (deletes, reloads, play mode, high-risk writes such as `scene_save`) with `MODE_FORBIDDEN`; only the user lifts it (bypass or Allowlist). `auto` runs other writes directly, but confirm ≥5-object batches, prefab apply, scene-level, asset-overwriting or irreversible changes with the user first (probe alone before those); `approval` gates `FullAuto` writes behind single-shot grants (`MODE_RESTRICTED`; permanent: the user's Allowlist) → [operating mode](references/protocol-operating-mode.md).

During a domain reload the port answers 503 or refuses for seconds while its `~/.unity_skills/registry.json` entry reads `reloading`: retry it, never another instance.

## Quick reference

Every skill is `POST /skill/<name>` with JSON args:
`curl -s "http://localhost:<port>/skill/gameobject_set_transform?expectProject=<project>" -d '{"name":"Crate","posX":4,"posY":1.5,"posZ":-2}'`

Target objects by `name` or the exact `path`/`entityId` (likewise `parentPath`, `childPath`); skip lookup calls: a wrong name fails and returns candidates.

| Call |
|---|
| `gameobject_create {name, primitiveType, parentName, x/y/z, rotX/Y/Z, scaleX/Y/Z, space}` — x/y/z, rot local to the parent unless `space:"world"`; scale local |
| `gameobject_set_transform {name, posX/Y/Z, rotX/Y/Z, scaleX/Y/Z, localPosX/Y/Z}` — pos, rot world; scale, localPos local; omitted axes kept |
| `gameobject_find {name, useRegex, component}` · `gameobject_get_info {name}` |
| `batch_query_gameobjects {queryJson, sampleLimit}` — every X under Y = one call, not per-object reads: queryJson `{"underPath":"Y","namePattern":"^Slot_","includeInactive":true}`; `sampleLimit` (20) caps `objects` |
| `gameobject_set_parent {childName, parentName}` · `gameobject_set_active {name, active}` |
| `gameobject_delete {name}` · `asset_delete {assetPath}` (dryRun first) |
| `component_add {name, componentType}` |
| `component_set_property {name, componentType, propertyName, value}` — vectors, colours `"1,2,3"`; references `referenceName`/`referencePath`/`assetPath` |
| `component_get_properties {name, componentType}` |
| `material_assign {name, materialPath}` · `scene_save {}` |
| `script_create {scriptName, folder, content}` — full source; executes directly; wait below |

## Everything else: one discovery call

`GET /skills/recommend?intent=<words>&includeSchema=true&topN=3&wire=v2` → top 3 skills with exact schemas; several needs → one more `&intent=<words>` each, same call. Name known → `GET /skills/schema?names=a,b&wire=v2`. Wider layers, v2 fields, constants → [discovery](references/protocol-discovery.md).

## Execute

Several steps, one call: `POST /skills/batch?expectProject=<project>` `{"steps":[{"skill":"gameobject_create","args":{"name":"Rig"}},{"skill":"gameobject_create","args":{"name":"Arm","parentName":"Rig"}}]}` — ≤50 steps; a step may use objects/components earlier steps add, by name or `{"$ref":"$0.entityId"}`; the first failure skips the rest unless `continueOnError:true`; `?mode=transactional` = all-or-nothing → [batch](skills/batch/SKILL.md).

**dryRun.** If this table, recommend or schema gave you the signature this session, execute directly: a failed validation runs nothing and returns the dryRun report. Script writes (`script_create`/`script_append`/`script_replace`) execute directly too. DryRun first (`?mode=dryRun&wire=v2`) only for deletes (`operation` has `Delete`: any `*_delete*`/`*_remove*`, asset/folder cleanup), every other `mayTriggerReload` skill, non-transactional multi-step batches, approval mode or when unsure.

Never invent skill or parameter names. On failure read `suggestedFixes`; open a module doc only when a response names one, or before writing C# against NGO, YooAsset, Addressables, UniTask, DOTween, PrimeTween, ShaderGraph, PICO or QFramework: read its `*-design` doc ([module index](skills/SKILL.md)) first, since its API may postdate your training.

**Undo**: `workflow_restore {"to":"last"}` or an id/label from `workflow_list` → [workflow](skills/workflow/SKILL.md).

## Verify from the read-back

A successful write returns the Editor's read-back (e.g. world `position`, `valueSet`): that is the verification. Never follow writes with per-object `*_get_info`/`*_get_properties`; re-read only async results or missing fields. `resolutionNotes` flags a guessed or ambiguous target. Script writes, and `asset_refresh` after writing a `.cs` yourself (`compileTriggered:true`), return `waitUrl` (already pinned): one GET waits out compile+reload. One command:
`u=http://localhost:<port>; c="$u/skill/script_create?expectProject=<project>"; b='{"scriptName":"Spin","content":"<source>"}'; s='{"steps":[{"skill":"component_add","args":{"name":"Crate","componentType":"Spin"}},{"skill":"component_set_property","args":{"name":"Crate","componentType":"Spin","propertyName":"speed","value":"9"}}]}'; J=$(curl -s "$c" -d "$b" | tee /dev/stderr | grep -o '/jobs/[^"]*') && curl -s --retry 20 --retry-all-errors --retry-delay 2 "$u$J" && curl -s "$u/skills/batch?expectProject=<project>&mode=transactional" -d "$s"`

## Surface profile and errors

`surfaceProfile` is the user's panel choice; `guide` and `noSceneAuthoring` hide some writes, which answer `SURFACE_EXCLUDED`. It is a boundary: do the rest of the task, never retry or route around it ([profiles](references/protocol-operating-mode.md#surface-profile)).

`MISSING_PACKAGE`: tell the user; installing is their call. Other codes → [error codes](references/protocol-error-codes.md).

Compile status, events, analytics → [observability](references/protocol-observability.md); closed Editor + opt-in Unity CLI → [unity-cli](references/protocol-unity-cli.md).

Windows (PowerShell / Git Bash): [windows](references/windows.md) rewrites these calls; or use `scripts/unity_skills.py`.
