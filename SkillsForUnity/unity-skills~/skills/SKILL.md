---
name: unity-skills-index
description: "Index of all Unity Skills modules for browsing available modules and documentation."
---

# Unity Skills - Module Index

This folder contains detailed documentation for each skill module. For quick reference, see the parent [SKILL.md](../SKILL.md).

> **Multi-Instance**: When user specifies Unity version (e.g. "Unity 6", "2022"), call `unity_skills.set_unity_version("6")` before operations. See parent SKILL.md.

## Modules

| Module | Description | Batch Support |
|--------|-------------|---------------|
| [gameobject](./gameobject/SKILL.md) | Create, transform, parent GameObjects | Yes (9 batch skills) |
| [component](./component/SKILL.md) | Add, remove, configure components | Yes (3 batch skills) |
| [material](./material/SKILL.md) | Materials, colors, emission, textures | Yes (4 batch skills) |
| [light](./light/SKILL.md) | Lighting setup and configuration | Yes (2 batch skills) |
| [prefab](./prefab/SKILL.md) | Prefab creation and instantiation | Yes (1 batch skill) |
| [asset](./asset/SKILL.md) | Asset import, organize, search | Yes (3 batch skills) |
| [ui](./ui/SKILL.md) | Canvas and UI element creation | Yes (1 batch skill) |
| [uitoolkit](./uitoolkit/SKILL.md) | UI Toolkit UXML/USS/UIDocument | No |
| [script](./script/SKILL.md) | C# script creation and search | Yes (1 batch skill) |
| [scene](./scene/SKILL.md) | Scene loading, saving, hierarchy | No |
| [editor](./editor/SKILL.md) | Play mode, selection, undo/redo | No |
| [animator](./animator/SKILL.md) | Animation controllers and parameters | No |
| [shader](./shader/SKILL.md) | Shader creation and listing | No |
| [console](./console/SKILL.md) | Log capture and debugging | No |
| [validation](./validation/SKILL.md) | Project validation and cleanup | No |
| [importer](./importer/SKILL.md) | Texture/Audio/Model import settings | Yes (3 batch skills) |
| [cinemachine](./cinemachine/SKILL.md) | Virtual cameras and cinematics | No |
| [probuilder](./probuilder/SKILL.md) | ProBuilder mesh modeling (requires package) | No |
| [terrain](./terrain/SKILL.md) | Terrain creation and painting | No |
| [physics](./physics/SKILL.md) | Raycasts, overlaps, gravity | No |
| [navmesh](./navmesh/SKILL.md) | Navigation mesh baking | No |
| [timeline](./timeline/SKILL.md) | Timeline and cutscenes | No |
| [workflow](./workflow/SKILL.md) | Undo history and snapshots | No |
| [cleaner](./cleaner/SKILL.md) | Find unused/duplicate assets | No |
| [smart](./smart/SKILL.md) | Query, layout, auto-bind | No |
| [perception](./perception/SKILL.md) | Scene analysis and summary | No |
| [camera](./camera/SKILL.md) | Scene View camera control | No |
| [event](./event/SKILL.md) | UnityEvent listeners | No |
| [package](./package/SKILL.md) | Package Manager operations | No |
| [project](./project/SKILL.md) | Project info and settings | No |
| [profiler](./profiler/SKILL.md) | Performance statistics | No |
| [optimization](./optimization/SKILL.md) | Asset optimization | No |
| [sample](./sample/SKILL.md) | Basic test skills | No |
| [debug](./debug/SKILL.md) | Error checking and diagnostics | No |
| [test](./test/SKILL.md) | Unity Test Runner | No |
| [bookmark](./bookmark/SKILL.md) | Scene View bookmarks | No |
| [history](./history/SKILL.md) | Undo/redo history | No |
| [scriptableobject](./scriptableobject/SKILL.md) | ScriptableObject management | No |

## Advisory Design Modules

These modules provide architecture and coding guidance. They are optional and should be loaded when the user asks for design advice, refactoring guidance, pattern selection, or better script quality.

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

- Unity REST skills: 490
- Advisory design modules: 13
- Core runtime modules: 37
- Total documented module folders: 50
