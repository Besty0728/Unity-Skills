---
name: unity-probuilder-modeling
description: "Extended ProBuilder modeling reference — spatial design heuristics, furniture decomposition, and detailed modeling examples beyond the probuilder SKILL.md. ProBuilder 建模扩展参考(空间设计启发式、家具拆解、超出 probuilder SKILL.md 的详细建模示例)。"
type: reference
---

# ProBuilder Modeling Reference

Load this file when you need deeper spatial design heuristics, detailed furniture decomposition, or extended modeling examples. The main `SKILL.md` keeps only routing and key skill summaries.

## Shape Creation Example

### Table at 0.8m Height

```python
# Table top: 1.2x0.6m surface at 0.8m height, 0.05m thick
unity_skills.call_skill("probuilder_create_shape",
    shape="Cube", name="TableTop", sizeX=1.2, sizeY=0.05, sizeZ=0.6, y=0.775)

for i, (lx, lz) in enumerate([(-0.5,-0.22), (0.5,-0.22), (-0.5,0.22), (0.5,0.22)]):
    unity_skills.call_skill("probuilder_create_shape",
        shape="Cylinder", name=f"Leg_{i}", sizeX=0.04, sizeY=0.75, sizeZ=0.04,
        x=lx, y=0.375, z=lz)
```

## Vertex Editing Example

```python
unity_skills.call_skill("probuilder_create_shape", shape="Cube", name="Ramp", sizeX=3, sizeY=1, sizeZ=5)
verts = unity_skills.call_skill("probuilder_get_vertices", name="Ramp")
unity_skills.call_skill("probuilder_move_vertices", name="Ramp", vertexIndexes="4,5", deltaY=-0.8)
```

Use `probuilder_get_vertices` first to identify the correct topology before moving points.

## Spatial Planning Rules

### Human Scale Reference

| Reference | Typical size | Use for |
|----------|--------------|---------|
| Standing person | `1.8m` tall | Doors `>=2.2m`, ceilings `>=2.5m` |
| Shoulder width | `0.5m` | Corridors `>=1.5m`, doors `>=0.9m` |
| Single step | `0.18m` rise / `0.28m` run | Stairs |
| Single room | `4x4m` | Interior planning |
| Story height | `3m` | Multi-story spaces |

### Gameplay Scale Reference

| Mechanic | Comfortable | Challenging | Usually impossible |
|----------|-------------|-------------|--------------------|
| Vertical jump | `<=1.0m` | `1.0-1.2m` | `>1.5m` |
| Horizontal gap | `<=2.5m` | `2.5-3.5m` | `>4m` |
| Step-up without jump | `<=0.3m` | - | `>0.5m` |
| Landing zone | `>=2m` | `1.5m` | `<1m` |

### Positioning Rules

- `y` is the **center** of the shape, not its bottom.
- Floor top surface at `0` with `sizeY=0.3` means `y = -0.15`.
- To stack B on A: `B.y = A.y + A.sizeY/2 + B.sizeY/2`.
- Pillars should usually use `y = height/2`.

## Level Design Workflow

1. Create a root object for the overall area.
2. Block out floors, walls, and platforms first.
3. Verify every platform is reachable before adding decorative detail.
4. Use ramps, stairs, and bridges only after core traversal is readable.
5. Apply colors/materials by gameplay role, not random aesthetics.

## Common Modeling Patterns

| Pattern | Typical setup |
|--------|----------------|
| Floor | `sizeY = 0.2-0.5`, wide X/Z |
| Wall | thin depth (`0.2-0.3`), tall Y |
| Pillar | `Cylinder`, small X/Z, `y = height/2` |
| Ramp | `Cube` + vertex edits |
| Staircase | `Stairs`, `sizeY = total rise`, `sizeZ = total run` |
| Bridge | thin Cube, aligned to connected platforms |
| Room | floor + ceiling + 4 walls |

## Furniture Decomposition

Real props should rarely be a single box. Split them into visible structural parts.

| Furniture | Main parts |
|----------|------------|
| Desk | tabletop + 4 legs |
| Chair | seat + 4 legs + backrest |
| Shelf | 2 sides + shelves + optional back |
| Bed | mattress + frame + headboard |
| Monitor | screen + stand + base |

### Desk Assembly Example

```python
unity_skills.call_skill("gameobject_create", name="Desk_0")

unity_skills.call_skill("probuilder_create_batch", defaultParent="Desk_0", items=[
    {"shape":"Cube", "name":"Desk_0_Top", "sizeX":1.2, "sizeY":0.04, "sizeZ":0.6, "y":0.73},
    {"shape":"Cube", "name":"Desk_0_Leg_FL", "sizeX":0.04, "sizeY":0.71, "sizeZ":0.04, "x":-0.55, "y":0.355, "z":0.25},
    {"shape":"Cube", "name":"Desk_0_Leg_FR", "sizeX":0.04, "sizeY":0.71, "sizeZ":0.04, "x":0.55,  "y":0.355, "z":0.25},
    {"shape":"Cube", "name":"Desk_0_Leg_BL", "sizeX":0.04, "sizeY":0.71, "sizeZ":0.04, "x":-0.55, "y":0.355, "z":-0.25},
    {"shape":"Cube", "name":"Desk_0_Leg_BR", "sizeX":0.04, "sizeY":0.71, "sizeZ":0.04, "x":0.55,  "y":0.355, "z":-0.25}
])
```

### Typical Furniture Dimensions

| Furniture | Typical size |
|----------|---------------|
| Office desk | `1.2x0.6x0.75m` |
| Dining table | `1.5x0.8x0.75m` |
| Chair | seat `0.45x0.45x0.45m`, total height `0.85m` |
| Bookshelf | `0.8x0.3x1.8m` |
| Bed | `0.9x2.0x0.45m` mattress area |
| Sofa | `2.0x0.8x0.45m` seat, `0.85m` back |

## Detail Rules

- Use `bevel_edges` only on chunky geometry. Thin slabs often bevel badly.
- Use face extrusion to create lips, trims, and ledges.
- Group multipart props under a parent for duplication and transforms.
- Use quick prototype colors before committing to material assets.

## Color Coding by Gameplay Role

| Role | RGB suggestion |
|------|----------------|
| Ground/floor | `(0.4, 0.4, 0.4)` |
| Walls | `(0.6, 0.6, 0.65)` |
| Platforms | `(0.2, 0.5, 0.8)` |
| Hazard/challenge | `(0.9, 0.3, 0.1)` |
| Ramps/slopes | `(0.8, 0.6, 0.2)` |
| Goal/finish | `(0.1, 0.8, 0.2)` |

## Mass Production Pattern

Create one detailed template, then duplicate it:

```python
for i, (x, z) in enumerate(seat_positions):
    unity_skills.call_skill("gameobject_duplicate", name="Chair_Template",
        newName=f"Chair_{i}", x=x, z=z)
```
