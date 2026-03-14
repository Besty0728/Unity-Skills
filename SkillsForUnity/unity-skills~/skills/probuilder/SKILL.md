---
name: unity-probuilder
description: "ProBuilder mesh modeling. Use when users want to create ProBuilder shapes, extrude faces, bevel edges, subdivide meshes, or perform procedural mesh operations. Triggers: ProBuilder, mesh modeling, extrude, bevel, subdivide, 建模, 拉伸, 倒角, 细分. Requires com.unity.probuilder package."
---

# Unity ProBuilder Skills

> **Requires**: `com.unity.probuilder` package (5.x+). If not installed, all skills return an install prompt.

## Skills Overview

| Skill | Category | Description |
|-------|----------|-------------|
| `probuilder_create_shape` | Create | Create parametric ProBuilder shape |
| `probuilder_extrude_faces` | Face | Extrude faces along normals |
| `probuilder_delete_faces` | Face | Delete faces by index |
| `probuilder_merge_faces` | Face | Merge multiple faces into one |
| `probuilder_flip_normals` | Face | Flip face normal direction |
| `probuilder_detach_faces` | Face | Detach faces (split from mesh) |
| `probuilder_set_face_material` | Face | Assign material to specific faces |
| `probuilder_bevel_edges` | Edge | Bevel (chamfer) edges |
| `probuilder_extrude_edges` | Edge | Extrude edges outward (walls, rails) |
| `probuilder_bridge_edges` | Edge | Bridge two edges with a face (doorways) |
| `probuilder_subdivide` | Mesh | Subdivide mesh or selected faces |
| `probuilder_conform_normals` | Mesh | Make normals point consistently outward |
| `probuilder_combine_meshes` | Mesh | Combine multiple meshes into one |
| `probuilder_move_vertices` | Vertex | Move vertices by delta (ramps, slopes) |
| `probuilder_set_vertices` | Vertex | Set absolute vertex positions |
| `probuilder_get_vertices` | Query | Get vertex positions by index |
| `probuilder_weld_vertices` | Vertex | Weld nearby vertices within radius |
| `probuilder_project_uv` | UV | Box-project UVs onto faces |
| `probuilder_get_info` | Query | Get mesh info (vertices, faces, edges, materials) |
| `probuilder_center_pivot` | Transform | Center or reposition mesh pivot |
| `probuilder_create_batch` | Batch | Batch create multiple shapes (level design) |
| `probuilder_set_material` | Material | Set material on entire mesh (color shortcut) |

---

## Shape Creation

### probuilder_create_shape
Create a ProBuilder primitive shape with parametric size and position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shape` | string | No | "Cube" | Shape type (see below) |
| `name` | string | No | auto | GameObject name |
| `x/y/z` | float | No | 0 | World position |
| `sizeX/sizeY/sizeZ` | float | No | 1 | Shape dimensions |
| `rotX/rotY/rotZ` | float | No | 0 | Euler rotation |

**Available shapes**: `Cube`, `Sphere`, `Cylinder`, `Cone`, `Torus`, `Prism`, `Arch`, `Pipe`, `Stairs`, `Door`, `Plane`

**Returns**: `{success, name, instanceId, shape, position, size, vertexCount, faceCount}`

---

## Face Operations

### probuilder_extrude_faces
Extrude faces outward or inward along normals.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices, e.g. `"0,1,2"` |
| `distance` | float | No | 0.5 | Extrusion distance (negative = inward) |
| `method` | string | No | "FaceNormal" | `IndividualFaces` / `FaceNormal` / `VertexNormal` |

**Extrude methods**:
- `IndividualFaces` — each face extrudes independently, creating gaps between
- `FaceNormal` — faces extrude as a group along the averaged normal
- `VertexNormal` — faces extrude as a group along individual vertex normals (smoother)

**Returns**: `{success, name, extrudedFaceCount, method, distance, totalFaces, totalVertices}`

---

### probuilder_delete_faces
Delete faces from a ProBuilder mesh by index.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | Yes | — | Comma-separated face indices, e.g. `"0,1,2"` |

**Returns**: `{success, name, deletedCount, remainingFaces, remainingVertices}`

---

### probuilder_merge_faces
Merge multiple faces into a single face.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices (min 2) |

**Returns**: `{success, name, mergedFromCount, totalFaces, totalVertices}`

---

### probuilder_flip_normals
Flip face normals (reverses winding order).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |

**Returns**: `{success, name, flippedCount}`

---

### probuilder_detach_faces
Detach faces from a mesh — split shared vertices so faces can move independently.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |
| `deleteSourceFaces` | bool | No | false | Delete the original faces after detaching |

**Returns**: `{success, name, detachedFaceCount, deleteSourceFaces, totalFaces, totalVertices}`

---

### probuilder_set_face_material
Assign a material to specific faces of a ProBuilder mesh.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |
| `materialPath` | string | No** | — | Material asset path |
| `submeshIndex` | int | No** | -1 | Submesh index directly |

**Provide either `materialPath` or `submeshIndex`.

**Returns**: `{success, name, affectedFaces, materialCount}`

---

## Edge Operations

### probuilder_bevel_edges
Bevel (chamfer) edges to create smooth transitions.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `edgeIndexes` | string | No | all | Vertex index pairs, e.g. `"0-1,2-3"` |
| `amount` | float | No | 0.2 | Bevel size (0–1, relative to edge length) |

**Returns**: `{success, name, beveledEdgeCount, newFaceCount, amount, totalFaces, totalVertices}`

---

### probuilder_extrude_edges
Extrude edges outward to create walls, rails, or flanges.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `edgeIndexes` | string | Yes | — | Vertex index pairs, e.g. `"0-1,2-3"` |
| `distance` | float | No | 0.5 | Extrusion distance |
| `extrudeAsGroup` | bool | No | true | Extrude edges as a connected group |
| `enableManifoldExtrude` | bool | No | false | Allow non-manifold extrusion |

**Returns**: `{success, name, extrudedEdgeCount, newEdgeCount, distance, totalFaces, totalVertices}`

---

### probuilder_bridge_edges
Bridge two edges with a new face. Use to create doorways, windows, or connect geometry.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `edgeA` | string | Yes | — | First edge, e.g. `"0-1"` |
| `edgeB` | string | Yes | — | Second edge, e.g. `"4-5"` |
| `allowNonManifold` | bool | No | false | Allow non-manifold geometry |

**Returns**: `{success, name, bridgedEdge, totalFaces, totalVertices}`

---

## Mesh Operations

### probuilder_subdivide
Subdivide a mesh or selected faces (adds detail by splitting faces).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |

**Returns**: `{success, name, totalFaces, totalVertices}`

---

### probuilder_conform_normals
Make all face normals point consistently outward. Fixes inverted faces after complex edits.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |

**Returns**: `{success, name, status, notification, faceCount}`

---

## Query & Transform

### probuilder_get_info
Get detailed information about a ProBuilder mesh.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |

**Returns**: `{name, instanceId, isProBuilder, vertexCount, faceCount, edgeCount, triangleCount, shapeType, position, bounds, materials[], submeshFaceCounts[]}`

---

### probuilder_center_pivot
Center the pivot point or move it to a specific world position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | GameObject name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `worldX/worldY/worldZ` | float | No | center | Target world position (omit all for center) |

**Returns**: `{success, name, pivot}`

---

## Example: Build a Table

```python
import unity_skills

# Create table top
unity_skills.call_skill("probuilder_create_shape",
    shape="Cube", name="TableTop",
    sizeX=2, sizeY=0.1, sizeZ=1, y=1)

# Create 4 legs
for i, (lx, lz) in enumerate([(-0.8, -0.4), (0.8, -0.4), (-0.8, 0.4), (0.8, 0.4)]):
    unity_skills.call_skill("probuilder_create_shape",
        shape="Cylinder", name=f"Leg_{i}",
        sizeX=0.08, sizeY=1, sizeZ=0.08,
        x=lx, y=0.5, z=lz)

# Bevel the table top edges for a softer look
unity_skills.call_skill("probuilder_bevel_edges",
    name="TableTop", amount=0.15)

# Assign wood material to all faces
unity_skills.call_skill("probuilder_set_face_material",
    name="TableTop", materialPath="Assets/Materials/Wood.mat")
```

---

## Batch & Level Design Skills

### probuilder_create_batch
Batch create multiple ProBuilder shapes in one call. Essential for level design — create entire scenes efficiently.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `items` | string | Yes | — | JSON array of shape configs |
| `defaultParent` | string | No | null | Default parent for all items |

**Item properties**: `shape`, `name`, `x`, `y`, `z`, `sizeX`, `sizeY`, `sizeZ`, `rotX`, `rotY`, `rotZ`, `parent`, `materialPath`

```python
# Create a parkour course in one call
unity_skills.call_skill("probuilder_create_batch", items=[
    {"shape": "Cube", "name": "Ground", "sizeX": 30, "sizeY": 0.5, "sizeZ": 10, "y": -0.25},
    {"shape": "Cube", "name": "Platform_1", "sizeX": 3, "sizeY": 0.3, "sizeZ": 3, "x": 5, "y": 2},
    {"shape": "Cube", "name": "Platform_2", "sizeX": 2, "sizeY": 0.3, "sizeZ": 2, "x": 9, "y": 3.5},
    {"shape": "Stairs", "name": "Stairs_1", "sizeX": 2, "sizeY": 3, "sizeZ": 4, "x": -3},
    {"shape": "Cylinder", "name": "Pillar_1", "sizeX": 0.5, "sizeY": 4, "sizeZ": 0.5, "x": 12, "y": 2},
    {"shape": "Arch", "name": "Arch_1", "sizeX": 4, "sizeY": 3, "sizeZ": 1, "x": 15}
])
```

---

## Vertex Editing Skills

### probuilder_weld_vertices
Weld (merge) nearby vertices within a radius threshold. Cleans up mesh after complex edits.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Mesh name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `vertexIndexes` | string | Yes | — | Comma-separated vertex indices |
| `radius` | float | No | 0.01 | Merge radius (vertices within this distance are welded) |

**Returns**: `{success, name, inputVertexCount, weldedVertexCount, radius, totalVertices}`

---

### probuilder_move_vertices
Move vertices by a delta offset. Use to transform cubes into ramps, slopes, or custom shapes.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Mesh name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `vertexIndexes` | string | Yes | — | Comma-separated vertex indices |
| `deltaX/deltaY/deltaZ` | float | No | 0 | Movement offset |

**Tip**: Use `probuilder_get_vertices` first to identify which vertices to move. For a default Cube, vertices 4-7 are typically the top face.

```python
# Create a ramp: move top-right vertices of a Cube forward
unity_skills.call_skill("probuilder_create_shape", shape="Cube", name="Ramp", sizeX=3, sizeY=1, sizeZ=5)
verts = unity_skills.call_skill("probuilder_get_vertices", name="Ramp")
# Move top vertices down on one side to create slope
unity_skills.call_skill("probuilder_move_vertices", name="Ramp", vertexIndexes="...", deltaY=-0.8)
```

### probuilder_set_vertices
Set absolute positions for specific vertices.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | No* | Mesh name |
| `instanceId` | int | No* | Instance ID |
| `path` | string | No* | Hierarchy path |
| `vertices` | string | Yes | JSON array of `{index, x, y, z}` |

### probuilder_get_vertices
Query vertex positions (all or by index). Use before vertex edits to understand mesh topology.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Mesh name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `vertexIndexes` | string | No | all | Comma-separated indices to query |
| `verbose` | bool | No | true | Return all vertices (false = summary for large meshes) |

---

## UV Operations

### probuilder_project_uv
Box-project UVs onto faces for proper texture mapping.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Mesh name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `faceIndexes` | string | No | all | Comma-separated face indices |
| `channel` | int | No | 0 | UV channel (0=primary, 1=lightmap, 2-3=custom) |

**Returns**: `{success, name, projectedFaceCount, channel, method}`

---

## Mesh Optimization

### probuilder_combine_meshes
Combine multiple ProBuilder meshes into one for optimization.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `names` | string | No | selected | Comma-separated mesh names, or "selected" for Selection |

### probuilder_set_material
Set material on an entire ProBuilder mesh. Supports asset path or quick RGB color for prototyping.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `name` | string | No* | — | Mesh name |
| `instanceId` | int | No* | 0 | Instance ID |
| `path` | string | No* | — | Hierarchy path |
| `materialPath` | string | No** | — | Material asset path |
| `r/g/b/a` | float | No** | 0.5 | Quick color (creates runtime material) |

**Provide either `materialPath` or `r/g/b` color values.

```python
# Quick color for prototyping
unity_skills.call_skill("probuilder_set_material", name="Platform_1", r=0.2, g=0.6, b=1)
unity_skills.call_skill("probuilder_set_material", name="Ramp", r=0.8, g=0.4, b=0.1)
```

---

## Example: Parkour Level

```python
import unity_skills

# 1. Create level root
unity_skills.call_skill("gameobject_create", name="ParkourLevel")

# 2. Batch create all platforms
unity_skills.call_skill("probuilder_create_batch", defaultParent="ParkourLevel", items=[
    {"shape": "Cube", "name": "Ground", "sizeX": 50, "sizeY": 0.5, "sizeZ": 12},
    {"shape": "Cube", "name": "Wall_L", "sizeX": 50, "sizeY": 5, "sizeZ": 0.3, "z": -6, "y": 2.5},
    {"shape": "Cube", "name": "Wall_R", "sizeX": 50, "sizeY": 5, "sizeZ": 0.3, "z": 6, "y": 2.5},
    {"shape": "Stairs", "name": "Stairs_Start", "sizeX": 3, "sizeY": 2, "sizeZ": 4, "x": -20},
    {"shape": "Cube", "name": "Plat_1", "sizeX": 3, "sizeY": 0.3, "sizeZ": 3, "x": -12, "y": 2},
    {"shape": "Cube", "name": "Plat_2", "sizeX": 2, "sizeY": 0.3, "sizeZ": 2, "x": -8, "y": 3},
    {"shape": "Cube", "name": "Ramp_1", "sizeX": 2, "sizeY": 1, "sizeZ": 5, "x": -4, "y": 1},
    {"shape": "Cube", "name": "Bridge", "sizeX": 8, "sizeY": 0.2, "sizeZ": 1.5, "x": 4, "y": 4},
    {"shape": "Cylinder", "name": "Pillar_1", "sizeX": 0.6, "sizeY": 4, "sizeZ": 0.6, "x": 0, "y": 2},
    {"shape": "Cylinder", "name": "Pillar_2", "sizeX": 0.6, "sizeY": 4, "sizeZ": 0.6, "x": 8, "y": 2},
    {"shape": "Cube", "name": "Plat_End", "sizeX": 4, "sizeY": 0.3, "sizeZ": 4, "x": 15, "y": 5},
    {"shape": "Arch", "name": "Finish_Arch", "sizeX": 4, "sizeY": 3, "sizeZ": 1, "x": 20, "y": 5},
])

# 3. Turn Ramp_1 into actual ramp by moving top vertices
verts = unity_skills.call_skill("probuilder_get_vertices", name="Ramp_1")
# Identify top-front vertices and lower them to create slope
# (vertex indices depend on ProBuilder's Cube vertex layout)

# 4. Color everything for visual distinction
unity_skills.call_skill("probuilder_set_material", name="Ground", r=0.3, g=0.3, b=0.3)
unity_skills.call_skill("probuilder_set_material", name="Ramp_1", r=0.9, g=0.5, b=0.1)
unity_skills.call_skill("probuilder_set_material", name="Bridge", r=0.6, g=0.4, b=0.2)
unity_skills.call_skill("probuilder_set_material", name="Finish_Arch", r=1, g=0.8, b=0)
```

## Important Notes

1. **ProBuilder mesh ≠ regular mesh**: ProBuilder objects have a `ProBuilderMesh` component that maintains editable topology. Regular meshes won't work with these skills.
2. **Face indexes start at 0**: Use `probuilder_get_info` to check `faceCount` before operating.
3. **Vertex indexes**: Use `probuilder_get_vertices` to query positions before `probuilder_move_vertices` / `probuilder_set_vertices`.
4. **All modifications auto-rebuild**: Every skill calls `ToMesh()` + `Refresh()` internally — no manual rebuild needed.
5. **Undo support**: All modification skills register with Unity's Undo system and Workflow tracking.
6. **Quick color vs persistent material**: `probuilder_set_material` with `r/g/b` creates runtime materials for fast prototyping. Use `material_create` + `materialPath` for production.
7. **Package not installed**: All skills gracefully return `{error: "ProBuilder package not installed..."}` with install instructions.
8. **Batch-first for level design**: Use `probuilder_create_batch` when creating 2+ shapes — one API call instead of many.
