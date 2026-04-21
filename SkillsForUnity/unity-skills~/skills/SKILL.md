---
name: unity-skills-index
description: "Index of all Unity Skills modules — 44 functional modules (SA/FA) + 15 advisory modules. Browse available skills, check mode requirements, and find the right module. Triggers: module list, skill index, browse skills, find module, 模块列表, 技能索引, 查找模块."
---

# Unity Skills - Module Index

Module docs. Start with [../SKILL.md](../SKILL.md) for mode switching and schema-first rules.

> **Multi-instance**: For version-specific projects, call `unity_skills.set_unity_version(...)` first.
> **Schema-first**: Use `GET /skills/schema` or `unity_skills.get_skill_schema()` for exact signatures. Load module docs for workflow guidance and guardrails.

## Modules

> **Mode reminder**: `SA` modules are available in Semi-Auto by default. `FA` modules require Full-Auto mode.

| Module | Mode | Description | Batch Support |
|--------|:----:|-------------|---------------|
| [gameobject](./gameobject/SKILL.md) | FA | Object create/move/parent | Yes |
| [component](./component/SKILL.md) | FA | Component add/remove/configure | Yes |
| [material](./material/SKILL.md) | FA | Material property edits | Yes |
| [light](./light/SKILL.md) | FA | Light create/configure | Yes |
| [prefab](./prefab/SKILL.md) | FA | Prefab create/apply/spawn | Yes |
| [asset](./asset/SKILL.md) | SA | Asset refresh/find/info | Yes |
| [batch](./batch/SKILL.md) | FA | Batch and async jobs | Built-in |
| [ui](./ui/SKILL.md) | FA | UGUI Canvas/UI creation | Yes |
| [uitoolkit](./uitoolkit/SKILL.md) | FA | UXML/USS/UIDocument | No |
| [script](./script/SKILL.md) | SA | Script create/read/update | Yes |
| [scene](./scene/SKILL.md) | SA | Scene load/save/query | No |
| [editor](./editor/SKILL.md) | SA | Play/select/undo/redo | No |
| [animator](./animator/SKILL.md) | FA | Animator controllers | No |
| [shader](./shader/SKILL.md) | FA | Shader create/list | No |
| [console](./console/SKILL.md) | SA | Log capture/filter | No |
| [validation](./validation/SKILL.md) | FA | Broken reference checks | No |
| [importer](./importer/SKILL.md) | FA | Texture/audio/model import | Yes |
| [cinemachine](./cinemachine/SKILL.md) | FA | VCam operations | No |
| [probuilder](./probuilder/SKILL.md) | FA | ProBuilder mesh edits | No |
| [xr](./xr/SKILL.md) | FA | XRI setup | No |
| [terrain](./terrain/SKILL.md) | FA | Terrain create/paint | No |
| [physics](./physics/SKILL.md) | FA | Raycast/overlap/gravity | No |
| [navmesh](./navmesh/SKILL.md) | FA | NavMesh bake/query | No |
| [timeline](./timeline/SKILL.md) | FA | Timeline tracks/clips | No |
| [workflow](./workflow/SKILL.md) | SA | Task snapshots/undo | No |
| [cleaner](./cleaner/SKILL.md) | FA | Unused/duplicate assets | No |
| [smart](./smart/SKILL.md) | FA | Query/layout/auto-bind | No |
| [perception](./perception/SKILL.md) | SA | Scene/project analysis | No |
| [camera](./camera/SKILL.md) | FA | Scene View camera | No |
| [event](./event/SKILL.md) | FA | UnityEvent wiring | No |
| [package](./package/SKILL.md) | FA | UPM install/query | No |
| [project](./project/SKILL.md) | FA | Project info/settings | No |
| [profiler](./profiler/SKILL.md) | FA | Perf statistics | No |
| [optimization](./optimization/SKILL.md) | FA | Asset optimization | No |
| [sample](./sample/SKILL.md) | FA | Demo/test skills | No |
| [debug](./debug/SKILL.md) | SA | Compile/system diagnostics | No |
| [test](./test/SKILL.md) | FA | Unity Test Runner | No |
| [bookmark](./bookmark/SKILL.md) | FA | Scene View bookmarks | No |
| [history](./history/SKILL.md) | FA | Undo/redo history | No |
| [scriptableobject](./scriptableobject/SKILL.md) | FA | ScriptableObject assets | No |
| [netcode](./netcode/SKILL.md) | FA | Netcode for GameObjects setup, prefabs, lifecycle, host/server/client | Yes |
| [yooasset](./yooasset/SKILL.md) | Both | YooAsset hot-update: build bundles, Collector config, BuildReport analysis | Yes |
| [dotween](./dotween/SKILL.md) | FA | DOTween Pro DOTweenAnimation editor-time configuration (add/batch/stagger/tune) | Yes |

## Advisory Design Modules

These modules provide design guidance only.

| Module | Description |
|--------|-------------|
| [project-scout](./project-scout/SKILL.md) | Inspect existing project |
| [architecture](./architecture/SKILL.md) | Plan system boundaries |
| [adr](./adr/SKILL.md) | Record tradeoffs |
| [performance](./performance/SKILL.md) | Review hot paths |
| [asmdef](./asmdef/SKILL.md) | Plan asmdef deps |
| [blueprints](./blueprints/SKILL.md) | Small-game blueprints |
| [script-roles](./script-roles/SKILL.md) | Assign class roles |
| [scene-contracts](./scene-contracts/SKILL.md) | Define scene wiring |
| [testability](./testability/SKILL.md) | Extract testable logic |
| [patterns](./patterns/SKILL.md) | Choose patterns |
| [async](./async/SKILL.md) | Choose async model |
| [inspector](./inspector/SKILL.md) | Design authoring UX |
| [scriptdesign](./scriptdesign/SKILL.md) | Review script structure |
| [netcode-design](./netcode-design/SKILL.md) | Netcode source-anchored rules (lifecycle/ownership/RPC/variables/spawn/scene/transport/pitfalls) |
| [yooasset-design](./yooasset-design/SKILL.md) | YooAsset v2.3.18 source-anchored rules (init/playmode/handles/loading/update/filesystem/build/pitfalls) |
| [addressables-design](./addressables-design/SKILL.md) | Addressables dual-version (1.22.3 Unity 2022 / 2.9.1 Unity 6) source-anchored rules (init/handles/loading/scene/update/download/assetref/pitfalls) with migration table |
| [unitask-design](./unitask-design/SKILL.md) | UniTask 2.5.10 source-anchored rules (basics/playerloop/cancellation/composition/conversion/asyncenumerable/triggers/pitfalls) |
| [dotween-design](./dotween-design/SKILL.md) | DOTween 1.3.015 source-anchored rules (basics/tween/sequence/shortcuts/ease/lifetime/integration/pitfalls) |

## Batch-First Rule

When a Full-Auto task touches `2+` objects, prefer `*_batch` skills over repeated single-item calls.

## Skill Naming Convention

Skills follow `<module>_<action>` or `<module>_<action>_batch`.
Use schema to verify the exact prefix list.
Special: `scene_analyze`, `hierarchy_describe`, `project_stack_detect` → `perception`; `job_*` → `batch`.
If a skill name does not match a valid prefix or a schema result, do not invent it.
