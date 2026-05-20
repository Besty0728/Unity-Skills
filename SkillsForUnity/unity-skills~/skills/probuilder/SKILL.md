---
name: unity-probuilder
description: "ProBuilder mesh modeling. Use when users want to create ProBuilder shapes, extrude faces, bevel edges, subdivide meshes, or perform procedural mesh operations. Triggers: ProBuilder, mesh modeling, extrude, bevel, subdivide, 建模, 拉伸, 倒角, 细分. Requires com.unity.probuilder package."
---

# Unity ProBuilder Skills

Use this module for editable ProBuilder meshes, not regular primitive GameObjects. It is best for blockout, level geometry, and procedural mesh refinement.

> **Requires**: `com.unity.probuilder` package.
> **Batch-first**: For scene blockout or level generation, prefer `probuilder_create_batch` when creating `2+` shapes.

## Guardrails

**Mode**: SkillMode.FullAuto (default — requires grant under Approval mode)

> Some skills (Delete / PlayMode / Reload / high-risk) are auto-forbidden in Approval/Auto modes — only Bypass can run them.

**DO NOT** (common hallucinations):
- `probuilder_create_mesh` does not exist -> use `probuilder_create_shape`
- `probuilder_edit_face` does not exist -> use the specific face skills such as `probuilder_extrude_faces`, `probuilder_delete_faces`, `probuilder_merge_faces`
- `probuilder_set_material` and `probuilder_set_face_material` are different -> whole object vs selected faces
- Regular meshes do not become ProBuilder meshes automatically
- Mesh rebuild calls (`ToMesh()` + `Refresh()`) are already handled by the skills. Do not invent a manual rebuild step

**Routing**:
- For ordinary primitive objects without editable topology -> use `gameobject_create`
- For material asset creation or shader work -> use `material`
- For large blockout generation -> combine this module with `material` and `light`, but keep geometry creation here

## Object Targeting

Most edit/query skills accept one of:
- `name`
- `instanceId`
- `path`

Prefer `instanceId` when multiple scene objects share the same name.

## Skills

### Create and Batch

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `probuilder_create_shape` | Create a parametric ProBuilder shape | `shape`, `name?`, `x/y/z?`, `sizeX/Y/Z?`, `rotX/Y/Z?` |
| `probuilder_create_batch` | Create multiple shapes in one call | `items`, `defaultParent?` |

Supported `shape` values: `Cube`, `Sphere`, `Cylinder`, `Cone`, `Torus`, `Prism`, `Arch`, `Pipe`, `Stairs`, `Door`, `Plane`.

### Face and Edge Editing

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `probuilder_extrude_faces` | Extrude selected faces | target, `faceIndexes?`, `distance?`, `method?` |
| `probuilder_delete_faces` | Delete faces by index | target, `faceIndexes` |
| `probuilder_merge_faces` | Merge faces into one | target, `faceIndexes?` |
| `probuilder_flip_normals` | Reverse face direction | target, `faceIndexes?` |
| `probuilder_detach_faces` | Split faces from shared vertices | target, `faceIndexes?`, `deleteSourceFaces?` |
| `probuilder_bevel_edges` | Chamfer edges | target, `edgeIndexes?`, `amount?` |
| `probuilder_extrude_edges` | Extrude edges outward | target, `edgeIndexes`, `distance?`, `extrudeAsGroup?` |
| `probuilder_bridge_edges` | Bridge two edges with new face | target, `edgeA`, `edgeB`, `allowNonManifold?` |

### Mesh, Vertex, UV, Material

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `probuilder_subdivide` | Add detail by subdivision | target, `faceIndexes?` |
| `probuilder_conform_normals` | Make normals point consistently outward | target, `faceIndexes?` |
| `probuilder_move_vertices` | Offset vertices by delta | target, `vertexIndexes`, `deltaX/Y/Z?` |
| `probuilder_set_vertices` | Set absolute vertex positions | target, `vertices` |
| `probuilder_get_vertices` | Query vertex positions | target, `vertexIndexes?`, `verbose?` |
| `probuilder_weld_vertices` | Merge close vertices | target, `vertexIndexes`, `radius?` |
| `probuilder_project_uv` | Box-project UVs | target, `faceIndexes?`, `channel?` |
| `probuilder_set_face_material` | Assign material to faces | target, `faceIndexes?`, `materialPath?`, `submeshIndex?` |
| `probuilder_set_material` | Assign whole-object material or quick color | target, `materialPath?`, `r/g/b/a?` |
| `probuilder_combine_meshes` | Merge multiple meshes | `names` |

### Query and Pivot

| Skill | Use | Key parameters |
|-------|-----|----------------|
| `probuilder_get_info` | Get face/vertex/material stats | target |
| `probuilder_center_pivot` | Center or reposition pivot | target, `worldX/Y/Z?` |

## Blockout Workflow

1. Create major volumes with `probuilder_create_batch`.
2. Verify traversal and gameplay scale before adding detail.
3. Refine surfaces with face/edge operations.
4. Use vertex edits for ramps, slopes, and irregular silhouettes.
5. Apply quick colors or real materials after layout stabilizes.

## Minimal Example

```python
import unity_skills

unity_skills.call_skill("probuilder_create_batch", items=[
    {"shape": "Cube", "name": "Ground", "sizeX": 20, "sizeY": 0.5, "sizeZ": 12, "y": -0.25},
    {"shape": "Cube", "name": "Platform_A", "sizeX": 3, "sizeY": 0.3, "sizeZ": 3, "x": 4, "y": 1.5},
    {"shape": "Cube", "name": "Ramp", "sizeX": 3, "sizeY": 1, "sizeZ": 5, "x": 8, "y": 0.5}
])

unity_skills.call_skill("probuilder_move_vertices",
    name="Ramp",
    vertexIndexes="4,5",
    deltaY=-0.8
)

unity_skills.call_skill("probuilder_set_material",
    name="Platform_A",
    r=0.2, g=0.6, b=1.0
)
```

## Important Notes

1. ProBuilder objects keep editable topology through `ProBuilderMesh`.
2. Use `probuilder_get_info` before face edits and `probuilder_get_vertices` before vertex edits.
3. MeshCollider on physics-driven props must be convex.
4. Quick color assignment is fine for prototype passes; use material assets for production.
5. Package missing errors are expected if ProBuilder is not installed.

## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
Load `MODELING_REFERENCE.md` for scene design heuristics, furniture decomposition, detailed examples, and extended modeling tips.
