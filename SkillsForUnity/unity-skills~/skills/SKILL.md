---
name: unity-skills-index
description: Index of all Unity Skills modules with per-module mode labels (SA/FA/Mixed). Use to find which module covers a task before loading its doc.
---

## Triggers
- Browsing the module catalog
- Finding which module handles a task
- Checking mode requirements
- 浏览模块目录、查找某事由哪个模块处理、确认模式要求

# Unity Skills - Module Index

Module docs. Start with [../SKILL.md](../SKILL.md) for mode switching and schema-first rules.

> **Multi-instance**: For version-specific projects, call `unity_skills.set_unity_version(...)` first.
> **Schema-first**: Use `GET /skills/schema` or `unity_skills.get_skill_schema()` for exact signatures. Load module docs for workflow guidance and guardrails.

## Modules

> **Mode legend** (v1.9.0+, caller-facing — describes what the caller can do, not the C# attribute):
> - `SA` — module skills mostly run directly in **all three modes** (Approval / Auto / Bypass) without a grant.
> - `FA` — module skills mostly require **user grant** under Approval (single-shot one-step execution); under Auto / Bypass they run directly with audit only.
> - `Mixed` — module is split between SA and FA; check per-skill `mode` before calling (`GET /skills?full=1`, or the scoped `GET /skills/schema?category=<Category>` — bare `GET /skills` is the brief directory and carries no `mode`).
> - Suffix `*` — module contains auto-forbidden skills (Delete / Play Mode / Domain Reload / high-risk). These return `MODE_FORBIDDEN` under Approval and Auto; only **Bypass** runs them, **or** the user can permanently allow them via the Allowlist. Never attempt grant for them.
>
> Labels are guidance only; the per-skill `mode` field (`GET /skills?full=1` / `GET /skills/schema?category=<Category>`) is authoritative.

| Module | Mode | Description | Batch Support |
|--------|:----:|-------------|---------------|
| [gameobject](./gameobject/SKILL.md) | FA* | Object create/move/parent | Yes |
| [component](./component/SKILL.md) | Mixed* | Component add/remove/configure | Yes |
| [material](./material/SKILL.md) | FA | Material property edits | Yes |
| [light](./light/SKILL.md) | FA | Light create/configure | Yes |
| [prefab](./prefab/SKILL.md) | FA | Prefab create/apply/spawn | Yes |
| [asset](./asset/SKILL.md) | SA* | Asset refresh/find/info | Yes |
| [batch](./batch/SKILL.md) | SA | Batch and async jobs | Built-in |
| [ui](./ui/SKILL.md) | FA | UGUI Canvas/UI creation | Yes |
| [uitoolkit](./uitoolkit/SKILL.md) | Mixed* | UXML/USS/UIDocument | No |
| [script](./script/SKILL.md) | SA* | Script create/read/update | Yes |
| [scene](./scene/SKILL.md) | SA* | Scene load/save/query | No |
| [editor](./editor/SKILL.md) | SA* | Play/select/undo/redo/change journal | No |
| [animator](./animator/SKILL.md) | FA | Animator controllers | No |
| [shader](./shader/SKILL.md) | Mixed* | Shader create/list | No |
| [shadergraph](./shadergraph/SKILL.md) | Mixed* | Shader Graph create/inspect/blackboard edit/constrained node editing | No |
| [graphics](./graphics/SKILL.md) | Mixed | GraphicsSettings / QualitySettings / SRP assets | No |
| [volume](./volume/SKILL.md) | Mixed* | Volume / VolumeProfile / VolumeComponent | No |
| [postprocess](./postprocess/SKILL.md) | FA* | Modern URP/HDRP post-processing | No |
| [urp](./urp/SKILL.md) | Mixed* | URP asset / renderer / renderer features | No |
| [decal](./decal/SKILL.md) | Mixed* | URP Decal Projector workflow | Yes |
| [console](./console/SKILL.md) | SA | Log capture/filter | No |
| [validation](./validation/SKILL.md) | SA* | Broken reference checks | No |
| [importer](./importer/SKILL.md) | Mixed | Texture/audio/model import | Yes |
| [cinemachine](./cinemachine/SKILL.md) | FA* | VCam operations | No |
| [probuilder](./probuilder/SKILL.md) | FA* | ProBuilder mesh edits | No |
| [xr](./xr/SKILL.md) | FA | XRI setup | No |
| [terrain](./terrain/SKILL.md) | FA | Terrain create/paint | No |
| [physics](./physics/SKILL.md) | Mixed | Raycast/overlap/gravity | No |
| [navmesh](./navmesh/SKILL.md) | Mixed* | NavMesh bake/query | No |
| [timeline](./timeline/SKILL.md) | FA* | Timeline tracks/clips | No |
| [workflow](./workflow/SKILL.md) | SA* | Task snapshots/undo, batch orchestration | No |
| [cleaner](./cleaner/SKILL.md) | SA* | Unused/duplicate assets | No |
| [smart](./smart/SKILL.md) | FA* | Query/layout/auto-bind | No |
| [perception](./perception/SKILL.md) | SA | Scene/project analysis | No |
| [camera](./camera/SKILL.md) | FA | Scene View camera | No |
| [event](./event/SKILL.md) | Mixed* | UnityEvent wiring | No |
| [package](./package/SKILL.md) | Mixed* | UPM install/query | No |
| [project](./project/SKILL.md) | SA* | Project info/settings | No |
| [profiler](./profiler/SKILL.md) | SA | Perf statistics | No |
| [optimization](./optimization/SKILL.md) | Mixed | Asset optimization | No |
| [sample](./sample/SKILL.md) | Mixed* | Demo/test skills | No |
| [debug](./debug/SKILL.md) | SA* | Compile/system diagnostics | No |
| [test](./test/SKILL.md) | Mixed* | Unity Test Runner | No |
| [bookmark](./bookmark/SKILL.md) | SA | Scene View bookmarks | No |
| [history](./history/SKILL.md) | SA | Undo/redo history | No |
| [scriptableobject](./scriptableobject/SKILL.md) | Mixed* | ScriptableObject assets | No |
| [netcode](./netcode/SKILL.md) | Mixed* | Netcode for GameObjects setup, prefabs, lifecycle, host/server/client | Yes |
| [addressables](./addressables/SKILL.md) | Mixed* | Addressables authoring: group CRUD, asset entry assignment, profile switching, content build (com.unity.addressables, reflection-based) | No |
| [yooasset](./yooasset/SKILL.md) | Mixed* | YooAsset hot-update: build bundles, Collector CRUD, BuildReport asset/dependency analysis, PlayMode runtime validation, Reporter/Debugger/AssetArtScanner tools | Yes |
| [dotween](./dotween/SKILL.md) | Mixed* | DOTween Pro DOTweenAnimation editor-time configuration (add/batch/stagger/tune) | Yes |
| [primetween](./primetween/SKILL.md) | Mixed* | PrimeTween Free inspection, factory discovery, and runtime tween/sequence script generation | No |
| [behavior](./behavior/SKILL.md) | Mixed | Unity Behavior graph assets, agents, blackboard variables (com.unity.behavior, reflection-based) | Yes |
| [hybridclr](./hybridclr/SKILL.md) | Mixed* | HybridCLR hot-update settings, codegen, DLL compile/copy pipeline (com.code-philosophy.hybridclr, reflection-based) | Yes |
| [qframework](./qframework/SKILL.md) | Mixed* | QFramework editor automation: architecture-layer codegen, ViewController/UIKit panel codegen, ResKit AssetBundle mark/build, architecture scan, API doc query (no UPM package, reflection-based) | Yes |

## Advisory Design Modules

Documentation only — these modules define no REST skills.

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
| [yooasset-design](./yooasset-design/SKILL.md) | YooAsset v2.3.18 source-anchored rules (init/default-package shortcuts/playmode/handles/loading/update/filesystem/build/pitfalls) |
| [addressables-design](./addressables-design/SKILL.md) | Addressables dual-version (1.22.3 Unity 2022 / 2.9.1 Unity 6) source-anchored rules (init/handles/loading/scene/update/download/assetref/pitfalls) with migration table |
| [unitask-design](./unitask-design/SKILL.md) | UniTask 2.5.10 source-anchored rules (basics/playerloop/cancellation/composition/conversion/asyncenumerable/triggers/pitfalls) |
| [dotween-design](./dotween-design/SKILL.md) | DOTween 1.3.015 source-anchored rules (basics/tween/sequence/shortcuts/ease/lifetime/integration/pitfalls) |
| [primetween-design](./primetween-design/SKILL.md) | PrimeTween 1.4.6 source-anchored rules (factories/handles/sequences/cycles/callbacks/lifetime/integration) |
| [shadergraph-design](./shadergraph-design/SKILL.md) | ShaderGraph dual-version source-anchored rules (versions/node subset/recipes/pitfalls/review) |
| [pico-design](./pico-design/SKILL.md) | PICO Unity Integration SDK v3.4.0 doc-anchored rules (setup/rendering/interaction/MR/SecureMR/platform/API signatures/version diffs 2.x-3.4/pitfalls) |
| [qframework-design](./qframework-design/SKILL.md) | QFramework v1.0.257 source-anchored rules (layers/CQRS/BindableProperty/event tools/CodeGenKit+UIKit/ResKit/ActionKit+SingletonKit/data kits/pitfalls) |
| [yaml-editing](./yaml-editing/SKILL.md) | Safe hand-edit rules for serialized YAML (.unity/.prefab/.asset/.meta/ProjectSettings) when REST cannot reach — reference/fileID repair, .meta/GUID safety, ProjectSettings patch, merge conflict |
| [unity-cli](./unity-cli/SKILL.md) | Experimental Unity CLI on the bound project (opt-in via `Library/UnitySkills/cli_config.json`) — cold start with the Editor closed, headless test/run/build, exit codes, JSON/NDJSON contract, hard DO-NOT list |
| [manual-gameobject](./manual-gameobject/SKILL.md) | Manually create GameObjects, organize the Hierarchy, and adjust Transforms using Unity Editor UI |
| [manual-component](./manual-component/SKILL.md) | Manually add, configure, reorder, and copy components on GameObjects using Unity Editor UI |
| [manual-material](./manual-material/SKILL.md) | Manually create and edit Materials and assign them to objects using Unity Editor UI |
| [manual-scene](./manual-scene/SKILL.md) | Manually navigate, save, and manage scenes using Unity Editor UI |

## Batch-First Rule

When a task touches `2+` objects in Auto / Bypass mode (or after a successful grant under Approval), prefer `*_batch` skills over repeated single-item calls.

## Skill Naming Convention

Skills follow `<module>_<action>` or `<module>_<action>_batch`.
Use schema to verify the exact prefix list.
Special: `scene_analyze`, `hierarchy_describe`, `project_stack_detect` → `perception`; `job_*` → `batch`.
If a skill name does not match a valid prefix or a schema result, do not invent it.
