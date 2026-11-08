---
name: unity-probuilder
description: Model editable meshes with ProBuilder for blockout
---

# Unity ProBuilder Skills

Editable ProBuilder meshes, not ordinary primitive GameObjects; best for blockout, level geometry and procedural mesh refinement. Parameters come from the schema (`GET /skills/schema?category=ProBuilder&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Spatial heuristics, furniture and extended examples: [reference/modeling.md](reference/modeling.md).

## When to use
- Blocking out levels, rooms or props
- Building or editing ProBuilder geometry, or prototyping shapes in the editor

## Rules
- Batch first: use `probuilder_create_batch` when creating 2 or more shapes.
- Blockout order: major volumes first, then check traversal and scale, then face edits, vertex edits, and finally colours or materials.
- Target by `entityId` (the only id on Unity 6000.4+), `name`, `instanceId` or `path`. Prefer `entityId` when several objects share a name.
- Create and modify skills are `FullAuto`: under Approval they need a grant. Queries (`probuilder_get_info`, `probuilder_get_vertices`) run directly.
- `probuilder_combine_meshes` is a Delete operation: refused under Approval and Auto. `probuilder_delete_faces` is only `Modify` and is grantable.
- Without `com.unity.probuilder`, every skill returns a package-missing diagnostic instead of running; tell the user.
- Run `probuilder_get_info` before face edits and `probuilder_get_vertices` before vertex edits.
- Mesh rebuilds (`ToMesh` and `Refresh`) are handled by the skills; do not add a manual rebuild step.
- Give a MeshCollider on physics-driven props a convex mesh.

## Pitfalls
- Regular meshes do not become ProBuilder meshes automatically.
- `probuilder_set_material` assigns one material or quick colour to the whole object; `probuilder_set_face_material` assigns to selected faces.

## Routing
- Ordinary primitives without editable topology → `gameobject_create`.
- Material assets and shaders → `material` module. Lights → `light` module; keep geometry creation here.

## Skills Overview

| Skill | Purpose |
|-------|---------|
| `probuilder_create_shape` / `probuilder_create_batch` | Parametric shape (`Cube`, `Sphere`, `Cylinder`, `Cone`, `Torus`, `Prism`, `Arch`, `Pipe`, `Stairs`, `Door`, `Plane`), one or many |
| `probuilder_extrude_faces` | Extrude selected faces |
| `probuilder_delete_faces` | Delete faces by index |
| `probuilder_merge_faces` | Merge faces into one |
| `probuilder_flip_normals` | Reverse face direction |
| `probuilder_detach_faces` | Split faces from shared vertices |
| `probuilder_bevel_edges` | Chamfer edges |
| `probuilder_extrude_edges` | Extrude edges outward |
| `probuilder_bridge_edges` | Bridge two edges with a new face |
| `probuilder_subdivide` | Add detail by subdivision |
| `probuilder_conform_normals` | Make normals point consistently outward |
| `probuilder_move_vertices` | Offset vertices by a delta |
| `probuilder_set_vertices` | Set absolute vertex positions |
| `probuilder_get_vertices` | Query vertex positions |
| `probuilder_weld_vertices` | Merge close vertices |
| `probuilder_project_uv` | Box-project UVs |
| `probuilder_set_face_material` | Assign a material to selected faces |
| `probuilder_set_material` | Assign a whole-object material or quick colour |
| `probuilder_combine_meshes` | Merge several meshes (Delete metadata) |
| `probuilder_get_info` | Face, vertex and material statistics |
| `probuilder_center_pivot` | Centre or reposition the pivot |

**DO NOT** (common hallucinations):
- `probuilder_create_mesh` does not exist: use `probuilder_create_shape`.
- `probuilder_edit_face` does not exist: use `probuilder_extrude_faces`, `probuilder_delete_faces`, `probuilder_merge_faces` and the other face skills.
