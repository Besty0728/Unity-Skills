---
name: unity-skills-index
description: "Index of all Unity Skills modules. See parent SKILL.md for complete API reference."
---

# Unity Skills - Module Index

This folder contains detailed documentation for each skill module. For quick reference, see the parent [SKILL.md](../SKILL.md).

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
| [script](./script/SKILL.md) | C# script creation and search | Yes (1 batch skill) |
| [scene](./scene/SKILL.md) | Scene loading, saving, hierarchy | No |
| [editor](./editor/SKILL.md) | Play mode, selection, undo/redo | No |
| [animator](./animator/SKILL.md) | Animation controllers and parameters | No |
| [shader](./shader/SKILL.md) | Shader creation and listing | No |
| [console](./console/SKILL.md) | Log capture and debugging | No |
| [validation](./validation/SKILL.md) | Project validation and cleanup | No |
| [importer](./importer/SKILL.md) | Texture/Audio/Model import settings | Yes (3 batch skills) |

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

## Total Skills: 117+

- Single-object skills: ~80
- Batch skills: ~27
- Query/utility skills: ~10
