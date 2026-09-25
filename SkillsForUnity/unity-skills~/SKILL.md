---
name: unity-skills
description: Automate the Unity Editor through a local REST API — scripts, scenes, prefabs, assets, materials, lighting, tests and hundreds of other Editor operations. Use when the user wants to actually operate the Unity Editor from chat — create, modify or batch-edit GameObjects/scripts/scenes/assets, or run Editor automation — in any language. Not needed for conceptual Unity Q&A that touches no Editor state — read the matching advisory doc under skills/ instead. 当用户要从对话里实际操作 Unity 编辑器（创建/修改/批量编辑/运行测试）时使用，任何语言均可触发；纯概念问答无需本协议。
compatibility: Unity 2022.3+/6000.x with the UnitySkills package (REST on localhost:8090-8100); Python 3 for the bundled client
---

# Unity Skills

> Docs are English: match requests by meaning in any language, and reply in the user's language.

## Route first

- **Automate** — the user asked you to act, or it needs traversal, search, batch, exact numbers or many objects: drive the Editor via REST.
- **Guide** — the user wants to do it themselves, or `surfaceProfile` is `guide`: name menus and Inspector fields per [SKILL_GUIDE.md](SKILL_GUIDE.md).
- **Advise** — a design question that changes nothing: answer from the matching advisory doc ([scriptdesign](skills/scriptdesign/SKILL.md), [architecture](skills/architecture/SKILL.md), [patterns](skills/patterns/SKILL.md), all in the [module index](skills/SKILL.md)), no REST calls.

## Target the right Editor

Ports `8090`–`8100` go first come, first served: add `?expectProject=<project>` (productName or folder name) or the exact `?expectInstance=<instanceId>` to every write. A different Editor answers 409 `INSTANCE_MISMATCH` and runs nothing.

`GET /health` (exempt from the check) has `currentMode`, `surfaceProfile`, `dryRunPolicy`, `instanceId`: probe it once in the same shell command as your first request, adding no other call, then only after a refused write, 409, 503/refused connection or odd mode. `bypass` and `auto` run writes directly, but under `auto` confirm ≥5-object batches, prefab apply, scene-level, asset-overwriting or irreversible changes with the user first (probe alone before those); `approval` gates `FullAuto` writes behind single-shot grants.

During a domain reload the port answers 503 or refuses for seconds while its `~/.unity_skills/registry.json` entry reads `reloading`: retry it, never another instance.

## Quick reference

Every skill is `POST /skill/<name>` with JSON args:
`curl -s "http://localhost:<port>/skill/gameobject_set_transform?expectProject=<project>" -d '{"name":"Crate","posX":4,"posY":1.5,"posZ":-2}'`

Target objects by `name` or the exact `path`/`instanceId` (likewise `parentPath`, `childPath`); skip lookup calls — a wrong name fails the call and returns candidates.

| Call |
|---|
| `gameobject_create {name, primitiveType, parentName, x,y,z, rotX,rotY,rotZ, scaleX,scaleY,scaleZ, space}` — x/y/z, rot local to the parent unless `space:"world"`; scale local |
| `gameobject_set_transform {name, posX,posY,posZ, rotX,rotY,rotZ, scaleX,scaleY,scaleZ, localPosX,localPosY,localPosZ}` — pos, rot world; scale, localPos local; omitted axes kept |
| `gameobject_find {name, useRegex, component}` · `gameobject_get_info {name}` |
| `gameobject_rename {name, newName}` · `gameobject_duplicate {name}` |
| `gameobject_set_parent {childName, parentName}` · `gameobject_set_active {name, active}` |
| `gameobject_delete {name}` (dryRun first) |
| `component_add {name, componentType}` |
| `component_set_property {name, componentType, propertyName, value}` — vectors, colours `"1,2,3"`; references `referenceName`/`referencePath`/`assetPath` |
| `component_get_properties {name, componentType}` |
| `material_assign {name, materialPath}` |
| `light_set_properties {name, r,g,b, intensity, range, spotAngle, shadows}` |
| `script_create {scriptName, folder, content}` — full source; dryRun first; wait below |
| `scene_save {}` (dryRun first) |

## Everything else: one discovery call

`GET /skills/recommend?intent=<words>&includeSchema=true&topN=3&wire=v2` → top 3 skills with exact schemas (~1–4 KB); several needs → one more `&intent=<words>` each, same call. Name known → `GET /skills/schema?names=a,b&wire=v2`. Wider layers, v2 fields, constants → [discovery](references/protocol-discovery.md).

## Execute

Several steps, one call: `POST /skills/batch?expectProject=<project>` `{"steps":[{"skill":"gameobject_create","args":{"name":"Rig"}},{"skill":"gameobject_create","args":{"name":"Arm","parentName":"Rig"}}]}` — ≤50 steps; a step may name objects earlier steps created, or use `{"$ref":"$0.instanceId"}`; the first failure skips the rest unless `continueOnError:true`; `?mode=transactional` = all-or-nothing → [batch](skills/batch/SKILL.md).

**dryRun.** If this table, recommend or schema gave you the signature this session, execute directly: a failed validation runs nothing and returns the dryRun report. DryRun first (`?mode=dryRun&wire=v2`) for deletes, `riskLevel` high, `mayTriggerReload`/`mayEnterPlayMode`, approval mode, non-transactional multi-step batches, or when unsure; under a `dryRunPolicy`, execute with the returned `?dryRunToken=`. `valid:true` = no validation errors (`warnings` never block; the target may still be missing).

Never invent skill or parameter names: use this table, recommend or schema. On failure read `suggestedFixes`; open a module doc only when a response names one.

## Verify from the read-back

A successful write returns the state read back from the Editor (e.g. world `position`, `valueSet`) — that is the verification; `resolutionNotes` flags a non-exact name match. Use `*_get_info` only for async results or missing fields. Script writes, and `asset_refresh` after you write a `.cs` yourself (`compileTriggered:true`), return `waitUrl`: one GET waits out compile+reload → `status:"completed"`, or `failed` with errors in `resultData.compilation`. One command:
`u=http://localhost:<port>; c="$u/skill/script_create?expectProject=<project>"; b='{"scriptName":"Spin","content":"<source>"}'; s='{"steps":[{"skill":"component_add","args":{"name":"Crate","componentType":"Spin"}},{"skill":"component_set_property","args":{"name":"Crate","componentType":"Spin","propertyName":"speed","value":"9"}}]}'; curl -s "$c&mode=dryRun" -d "$b" | grep -q '"valid":true' && J=$(curl -s "$c" -d "$b" | tee /dev/stderr | grep -o '/jobs/[^"]*=90') && curl -s --retry 20 --retry-connrefused --retry-all-errors --retry-delay 2 "$u$J" && curl -s "$u/skills/batch?expectProject=<project>" -d "$s"`

## Errors

| Code | Meaning → action |
|---|---|
| `SURFACE_EXCLUDED` | The user's `surfaceProfile` hides it → do the rest, never retry or route around it ([profiles](references/protocol-operating-mode.md#surface-profile)). |
| `MISSING_PARAM` / `TARGET_NOT_FOUND` | Bad or unresolvable arguments → fix from `details` / locate the target (`gameobject_find`). |
| `MISSING_PACKAGE` | Tell the user (dryRun `missingPackages`, recommend `unavailable` show it early); installing is their call. |
| `MODE_RESTRICTED` / `MODE_FORBIDDEN` | Grant / bypass or Allowlist → [operating mode](references/protocol-operating-mode.md). |
| other | [error codes](references/protocol-error-codes.md) |

Compile status, events, analytics → [observability](references/protocol-observability.md); closed Editor + opt-in Unity CLI → [unity-cli](references/protocol-unity-cli.md).

`805` skills in `54` categories; requests time out after `15 minutes`.
