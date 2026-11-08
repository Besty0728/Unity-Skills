---
name: unity-skills-index
description: Index of all Unity Skills modules with per-module mode labels (SA/FA/Mixed). Use to find which module covers a task before loading its doc.
---

# Unity Skills: Module Index

Module docs. Start with [../SKILL.md](../SKILL.md) for mode switching and schema-first rules.

> **Multi-instance**: For version-specific projects, call `unity_skills.set_unity_version(...)` first.
> **Schema-first**: Exact signatures come from `GET /skills/recommend?intent=<words>&includeSchema=true&topN=3&wire=v2` (`unity_skills.find_skills(intent, top_n=3, include_schema=True, wire="v2")`) or from `GET /skills/schema?names=a,b&wire=v2` when the name is known. The full `GET /skills/schema` is about 707 KB. Module docs add workflow guidance and guardrails.
> **Split modules**: gameobject, component, batch, script, material, editor, cinemachine, particle, workflow, addressables, asset, behavior, camera, event, light, prefab, qframework, terrain, ui keep shared rules and the skill list in `SKILL.md`, and each skill's parameter table, item fields and return shape in `reference/<skill_name>.md` (one small file per skill). Read the reference file only when the schema's parameter notes are not enough.

## REST modules

Mode labels are guidance only: `SA` means the module's skills mostly run directly in all three modes; `FA` means they mostly need a user grant under Approval; `Mixed` means check the per-skill `mode`; a trailing `*` means the module has auto-forbidden skills (Delete, Play Mode, Domain Reload, high risk). The authoritative per-skill mode is in `GET /skills?full=1`, or `GET /skills/schema?category=<Category>`. Bare `GET /skills` is the brief directory and carries no `mode`.

| Module | Mode | Description | Batch Support |
|--------|:----:|-------------|---------------|
| [gameobject](./gameobject/SKILL.md) | FA* | Object create/move/parent | Yes |
| [component](./component/SKILL.md) | Mixed* | Component add/remove/configure | Yes |
| [material](./material/SKILL.md) | FA | Material property edits | Yes |
| [light](./light/SKILL.md) | FA | Light create/configure | Yes |
| [particle](./particle/SKILL.md) | Mixed | Built-in ParticleSystem (Shuriken) create/configure: typed module patches, renderer, bursts, sub emitters, static diagnosis (not VFX Graph) | Yes |
| [prefab](./prefab/SKILL.md) | Mixed* | Prefab create/apply/spawn, asset queries, component/node edits and atomic batches | Yes |
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
| [yooasset](./yooasset/SKILL.md) | Mixed* | YooAsset 2.3.15+ / 3.0.4+ hot-update: build bundles, Collector CRUD, BuildReport asset/dependency analysis, PlayMode runtime validation, Reporter/Debugger tools, AssetArtScanner (2.x only) | Yes |
| [dotween](./dotween/SKILL.md) | Mixed* | DOTween Pro DOTweenAnimation editor-time configuration (add/batch/stagger/tune) | Yes |
| [primetween](./primetween/SKILL.md) | Mixed* | PrimeTween Free inspection, factory discovery, and runtime tween/sequence script generation | No |
| [behavior](./behavior/SKILL.md) | Mixed | Unity Behavior graph assets, agents, blackboard variables (com.unity.behavior, reflection-based) | Yes |
| [hybridclr](./hybridclr/SKILL.md) | Mixed* | HybridCLR hot-update settings, codegen, DLL compile/copy pipeline (com.code-philosophy.hybridclr, reflection-based) | Yes |
| [qframework](./qframework/SKILL.md) | Mixed* | QFramework editor automation: architecture-layer codegen, ViewController/UIKit panel codegen, ResKit AssetBundle mark/build, architecture scan, API doc query (no UPM package, reflection-based) | Yes |

## Advisory modules

Documentation only. These modules define no REST skills. Each description matches the first lines of its doc.

Official Unity manual URLs for a topic, to fetch or hand to the user: [references](../references/README.md). Grep one `##` section; never read a whole index file.

| Module | Description |
|--------|-------------|
| [project-scout](./project-scout/SKILL.md) | Reconnoiter an existing Unity project (version, packages, asmdef, folders, conventions) before proposing changes. |
| [architecture](./architecture/SKILL.md) | Gameplay and system architecture advice: module boundaries, scene/bootstrap layering, data ownership, execution order and update guards. |
| [adr](./adr/SKILL.md) | Record a Unity architecture decision (ADR) with options, rationale and revisit triggers. |
| [performance](./performance/SKILL.md) | High-signal review of Unity performance red flags: Update cadence, hot-path lookups, allocations, pooling, hidden runtime costs. |
| [asmdef](./asmdef/SKILL.md) | Advise on Unity assembly definitions (asmdef): module boundaries, compile speed, editor/runtime/test split. |
| [blueprints](./blueprints/SKILL.md) | Starter architecture blueprints for small games (platformer, shooter, runner, puzzle, tower defense, clicker, card). |
| [script-roles](./script-roles/SKILL.md) | Decide a class's role (MonoBehaviour, ScriptableObject, plain C# service, presenter, installer) before generating scripts. |
| [scene-contracts](./scene-contracts/SKILL.md) | Define explicit Unity scene composition contracts: required objects, component dependencies, bootstrap and reference wiring. |
| [testability](./testability/SKILL.md) | Decide what logic leaves MonoBehaviour for pure C# seams, and plan EditMode vs PlayMode tests. |
| [patterns](./patterns/SKILL.md) | Choose among ScriptableObject, events, interfaces, state machine, object pool and service-layer designs; justify each pattern against simpler options. |
| [async](./async/SKILL.md) | Choose among Update, coroutines, UniTask and timers for runtime work; define cleanup and cancellation ownership. |
| [inspector](./inspector/SKILL.md) | Design Inspector authoring UX: SerializeField exposure, Header/Tooltip organization, validation and attributes. |
| [scriptdesign](./scriptdesign/SKILL.md) | Design review of gameplay scripts: responsibility, role choice, coupling, lifecycle, field ownership (authoring, composition, runtime-mutable), testability. |
| [netcode-design](./netcode-design/SKILL.md) | Source-anchored design rules for Netcode for GameObjects 2.x. |
| [yooasset-design](./yooasset-design/SKILL.md) | Source-anchored design rules for YooAsset v2.3.18, with a 3.x delta sheet. |
| [addressables-design](./addressables-design/SKILL.md) | Source-anchored design rules for Unity Addressables 1.22.3/2.9.1. |
| [unitask-design](./unitask-design/SKILL.md) | Source-anchored design rules for UniTask 2.5.10: PlayerLoop timing, cancellation, composition, conversion, async enumerables, triggers and pitfalls. |
| [dotween-design](./dotween-design/SKILL.md) | Source-anchored design rules for DOTween 1.3.015: tween lifetime, Safe Mode, Sequences, shortcuts, Ease and UniTask bridging. |
| [primetween-design](./primetween-design/SKILL.md) | Source-anchored design rules for PrimeTween 1.4.6: factories, handles, sequences, callbacks, lifetime and integration. |
| [shadergraph-design](./shadergraph-design/SKILL.md) | Source-anchored Shader Graph design rules for Unity 2022.3 and Unity 6 (ShaderGraph 14.0.12 / Unity 6 Graphics package). |
| [pico-design](./pico-design/SKILL.md) | Doc-anchored design rules for PICO Unity Integration SDK v3.4.0 — XR/MR/platform services, version diffs 2.x-3.4, hallucination shields. |
| [qframework-design](./qframework-design/SKILL.md) | Source-anchored design rules for QFramework v1.0.257 — layers, CQRS, BindableProperty, UIKit/ResKit/ActionKit, pitfalls. |
| [yaml-editing](./yaml-editing/SKILL.md) | Last-resort guidance for safely hand-editing Unity YAML. |
| [unity-cli](./unity-cli/SKILL.md) | Experimental Unity CLI on the bound project (opt-in via Library/UnitySkills/cli_config.json): cold start with the Editor closed, headless test, run and build, exit codes, JSON contract, and the allowlist. |
| [manual-gameobject](./manual-gameobject/SKILL.md) | Manually create GameObjects, organize the Hierarchy, and adjust Transforms using Unity Editor UI. For clicks-and-drag workflows that do not need REST automation. |
| [manual-component](./manual-component/SKILL.md) | Manually add, configure, reorder, and copy components on GameObjects using Unity Editor UI. For one-off Inspector workflows that do not need REST automation. |
| [manual-material](./manual-material/SKILL.md) | Manually create and edit Materials and assign them to objects using Unity Editor UI. For one-off material tweaks that do not need REST automation. |
| [manual-scene](./manual-scene/SKILL.md) | Manually navigate, save, and manage scenes using Unity Editor UI. For simple Scene view workflows that do not need REST automation. |
| [skill-creator](./skill-creator/SKILL.md) | Guide for authoring user-defined custom UnitySkills skills inside your own Unity project - a [UnitySkill] C# method plus the conventions that let agents and the server discover and gate it. |
| [evaluation](./evaluation/SKILL.md) | Run the UnitySkills evaluation set (scored benchmark tasks) on the user's own Unity project. Use only when the user explicitly asks to run, resume or continue a UnitySkills evaluation, or pastes an evaluation session prompt; never for ordinary Unity work. |

## Batch-First Rule

When a task touches `2+` objects in Auto / Bypass mode (or after a successful grant under Approval), prefer `*_batch` skills over repeated single-item calls.

## Skill Naming Convention

Skills follow `<module>_<action>` or `<module>_<action>_batch`. Use the schema to verify the exact prefix list.
Special: `scene_analyze`, `hierarchy_describe`, `project_stack_detect` belong to `perception`; `job_*` belongs to `batch`.
If a skill name does not match a valid prefix or a schema result, do not invent it.
