---
name: unity-skills-index
description: "Index of all Unity Skills modules — 38 functional modules (SA/FA) + 14 advisory modules. Browse available skills, check mode requirements, and find the right module. Triggers: module list, skill index, browse skills, find module, 模块列表, 技能索引, 查找模块."
---

# Unity Skills - Module Index

This folder contains detailed documentation for each skill module. For quick reference, see the parent [SKILL.md](../SKILL.md).

> **Multi-Instance**: When user specifies Unity version (e.g. "Unity 6", "2022"), call `unity_skills.set_unity_version("6")` before operations. See parent SKILL.md.

## Modules

> **Mode Reminder**: In Semi-Auto (default), only modules marked `SA` are active.
> Modules marked `FA` require Full-Auto mode. See parent SKILL.md for mode switching.

| Module | Mode | Description | Batch Support |
|--------|:----:|-------------|---------------|
| [gameobject](./gameobject/SKILL.md) | FA | Create, transform, parent GameObjects | Yes (9 batch skills) |
| [component](./component/SKILL.md) | FA | Add, remove, configure components | Yes (3 batch skills) |
| [material](./material/SKILL.md) | FA | Materials, colors, emission, textures | Yes (4 batch skills) |
| [light](./light/SKILL.md) | FA | Lighting setup and configuration | Yes (2 batch skills) |
| [prefab](./prefab/SKILL.md) | FA | Prefab creation and instantiation | Yes (1 batch skill) |
| [asset](./asset/SKILL.md) | SA | Asset import, organize, search | Yes (3 batch skills) |
| [ui](./ui/SKILL.md) | FA | Canvas and UI element creation | Yes (1 batch skill) |
| [uitoolkit](./uitoolkit/SKILL.md) | FA | UI Toolkit UXML/USS/UIDocument | No |
| [script](./script/SKILL.md) | SA | C# script creation and search | Yes (1 batch skill) |
| [scene](./scene/SKILL.md) | SA | Scene loading, saving, hierarchy | No |
| [editor](./editor/SKILL.md) | SA | Play mode, selection, undo/redo | No |
| [animator](./animator/SKILL.md) | FA | Animation controllers and parameters | No |
| [shader](./shader/SKILL.md) | FA | Shader creation and listing | No |
| [console](./console/SKILL.md) | SA | Log capture and debugging | No |
| [validation](./validation/SKILL.md) | FA | Project validation and cleanup | No |
| [importer](./importer/SKILL.md) | FA | Texture/Audio/Model import settings | Yes (3 batch skills) |
| [cinemachine](./cinemachine/SKILL.md) | FA | Virtual cameras and cinematics | No |
| [probuilder](./probuilder/SKILL.md) | FA | ProBuilder mesh modeling (requires package) | No |
| [xr](./xr/SKILL.md) | FA | XR Interaction Toolkit (requires package, reflection-based) | No |
| [terrain](./terrain/SKILL.md) | FA | Terrain creation and painting | No |
| [physics](./physics/SKILL.md) | FA | Raycasts, overlaps, gravity | No |
| [navmesh](./navmesh/SKILL.md) | FA | Navigation mesh baking | No |
| [timeline](./timeline/SKILL.md) | FA | Timeline and cutscenes | No |
| [workflow](./workflow/SKILL.md) | SA | Undo history and snapshots | No |
| [cleaner](./cleaner/SKILL.md) | FA | Find unused/duplicate assets | No |
| [smart](./smart/SKILL.md) | FA | Query, layout, auto-bind | No |
| [perception](./perception/SKILL.md) | SA | Scene analysis and summary | No |
| [camera](./camera/SKILL.md) | FA | Scene View camera control | No |
| [event](./event/SKILL.md) | FA | UnityEvent listeners | No |
| [package](./package/SKILL.md) | FA | Package Manager operations | No |
| [project](./project/SKILL.md) | FA | Project info and settings | No |
| [profiler](./profiler/SKILL.md) | FA | Performance statistics | No |
| [optimization](./optimization/SKILL.md) | FA | Asset optimization | No |
| [sample](./sample/SKILL.md) | FA | Basic test skills | No |
| [debug](./debug/SKILL.md) | SA | Error checking and diagnostics | No |
| [test](./test/SKILL.md) | FA | Unity Test Runner | No |
| [bookmark](./bookmark/SKILL.md) | FA | Scene View bookmarks | No |
| [history](./history/SKILL.md) | FA | Undo/redo history | No |
| [scriptableobject](./scriptableobject/SKILL.md) | FA | ScriptableObject management | No |

## Advisory Design Modules

These modules provide architecture and coding guidance — **available in both Semi-Auto and Full-Auto modes**.
They contain no REST skills; load them on demand when the user asks for design advice, refactoring guidance, pattern selection, or better script quality.

| Module | Description | When To Use |
|--------|-------------|-------------|
| [project-scout](./project-scout/SKILL.md) | Inspect current project baseline and constraints | Before proposing architecture changes in an existing project |
| [architecture](./architecture/SKILL.md) | Overall mini-game/system architecture planning | Before creating lots of gameplay scripts or when defining module boundaries |
| [adr](./adr/SKILL.md) | Short architecture decision records | When tradeoffs need to stay stable across multiple turns |
| [performance](./performance/SKILL.md) | Unity-specific performance red-flag review | When reviewing hot paths, Update usage, allocations, pooling, scene lookups |
| [asmdef](./asmdef/SKILL.md) | Assembly boundary and dependency planning | When project scale justifies clearer compile-time boundaries |
| [blueprints](./blueprints/SKILL.md) | Minimal architecture blueprints for common mini-games | When starting a new small game or gameplay vertical slice |
| [script-roles](./script-roles/SKILL.md) | Decide class roles before code generation | Before batch-creating gameplay scripts |
| [scene-contracts](./scene-contracts/SKILL.md) | Scene composition and reference contract planning | When defining required scene objects, bootstrap, and validation |
| [testability](./testability/SKILL.md) | Testability and pure-C# extraction guidance | When deciding what logic should be isolated from Unity APIs |
| [patterns](./patterns/SKILL.md) | Pattern selection for SO, events, interfaces, pooling, states | When deciding whether a pattern is justified |
| [async](./async/SKILL.md) | Async and lifecycle strategy | When choosing between `Update`, coroutine, UniTask, timers, cleanup |
| [inspector](./inspector/SKILL.md) | Inspector-facing API and serialization guidance | When designing fields, attributes, validation, authoring UX |
| [scriptdesign](./scriptdesign/SKILL.md) | Script-level quality review | When generating or reviewing scripts for coupling/performance/maintainability |
| [xr](./xr/SKILL.md) | XR development workflow guidance | When setting up VR/AR interactions, teleportation, grab, or XR rig |

## Batch-First Rule

> When operating on **2 or more objects**, ALWAYS use `*_batch` skills instead of calling single-object skills multiple times.

**Example - Creating 10 cubes:**

```python
# BAD: 10 API calls
for i in range(10):
    unity_skills.call_skill("gameobject_create", name=f"Cube_{i}", primitiveType="Cube", x=i)

# GOOD: 1 API call
unity_skills.call_skill("gameobject_create_batch",
    items=[{"name": f"Cube_{i}", "primitiveType": "Cube", "x": i} for i in range(10)]
)
```

## Coverage Summary

- Unity REST skills: 513
- Advisory design modules: 14
- Core runtime modules: 38
- Total documented module folders: 52

## Skill Naming Convention

All skills follow `<module>_<action>` or `<module>_<action>_batch` pattern.
Valid module prefixes: `gameobject`, `component`, `material`, `light`, `prefab`, `asset`, `ui`, `uitoolkit`, `script`, `scene`, `editor`, `animator`, `shader`, `console`, `validation`, `importer`, `cinemachine`, `probuilder`, `xr`, `terrain`, `physics`, `navmesh`, `timeline`, `workflow`, `cleaner`, `smart`, `perception`, `camera`, `event`, `package`, `project`, `profiler`, `optimize`, `sample`, `debug`, `test`, `bookmark`, `history`, `scriptableobject`.
Special: `scene_summarize`, `scene_analyze`, `scene_health_check`, `scene_contract_validate`, `scene_component_stats`, `scene_find_hotspots`, `hierarchy_describe`, `script_analyze`, `script_dependency_graph`, `project_stack_detect` belong to `perception` module despite their prefix.
If a skill name doesn't start with a valid prefix listed above, **it does not exist** — do not hallucinate it.
