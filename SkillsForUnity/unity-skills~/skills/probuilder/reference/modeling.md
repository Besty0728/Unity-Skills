# ProBuilder modeling reference

Read when you need spatial design heuristics, furniture decomposition or extended modeling examples. The module [SKILL.md](../SKILL.md) keeps the routing and the skill summaries.

Calls use the batch endpoint so several steps run in one request: `POST /skills/batch?expectProject=<project>` with `{"steps":[{"skill":"...","args":{...}}]}`.

## Shape examples

Table top at 0.8 m height (1.2 x 0.6 m, 0.05 m thick), with four legs:

```json
{"steps":[
 {"skill":"probuilder_create_shape","args":{"shape":"Cube","name":"TableTop","sizeX":1.2,"sizeY":0.05,"sizeZ":0.6,"y":0.775}},
 {"skill":"probuilder_create_shape","args":{"shape":"Cylinder","name":"Leg_0","sizeX":0.04,"sizeY":0.75,"sizeZ":0.04,"x":-0.5,"y":0.375,"z":-0.22}},
 {"skill":"probuilder_create_shape","args":{"shape":"Cylinder","name":"Leg_1","sizeX":0.04,"sizeY":0.75,"sizeZ":0.04,"x":0.5,"y":0.375,"z":-0.22}},
 {"skill":"probuilder_create_shape","args":{"shape":"Cylinder","name":"Leg_2","sizeX":0.04,"sizeY":0.75,"sizeZ":0.04,"x":-0.5,"y":0.375,"z":0.22}},
 {"skill":"probuilder_create_shape","args":{"shape":"Cylinder","name":"Leg_3","sizeX":0.04,"sizeY":0.75,"sizeZ":0.04,"x":0.5,"y":0.375,"z":0.22}}
]}
```

Ramp: create a Cube, read its topology, then lower the far edge:

```json
{"steps":[
 {"skill":"probuilder_create_shape","args":{"shape":"Cube","name":"Ramp","sizeX":3,"sizeY":1,"sizeZ":5}},
 {"skill":"probuilder_get_vertices","args":{"name":"Ramp"}},
 {"skill":"probuilder_move_vertices","args":{"name":"Ramp","vertexIndexes":"4,5","deltaY":-0.8}}
]}
```

Call `probuilder_get_vertices` first to identify the topology before moving points; a batch cannot pick indices from its own read.

## Spatial planning

### Human scale

| Reference | Typical size | Use for |
|----------|--------------|---------|
| Standing person | 1.8 m tall | Doors >= 2.2 m, ceilings >= 2.5 m |
| Shoulder width | 0.5 m | Corridors >= 1.5 m, doors >= 0.9 m |
| Single step | 0.18 m rise, 0.28 m run | Stairs |
| Single room | 4 x 4 m | Interior planning |
| Story height | 3 m | Multi-story spaces |

### Gameplay scale

| Mechanic | Comfortable | Challenging | Usually impossible |
|----------|-------------|-------------|--------------------|
| Vertical jump | <= 1.0 m | 1.0-1.2 m | > 1.5 m |
| Horizontal gap | <= 2.5 m | 2.5-3.5 m | > 4 m |
| Step-up without jump | <= 0.3 m | - | > 0.5 m |
| Landing zone | >= 2 m | 1.5 m | < 1 m |

### Positioning

- `y` is the **centre** of a shape, not its bottom.
- A floor top surface at 0 with `sizeY=0.3` means `y = -0.15`.
- To stack B on A: `B.y = A.y + A.sizeY/2 + B.sizeY/2`.
- Pillars usually take `y = height/2`.

## Level design

1. Create a root object for the area.
2. Block out floors, walls and platforms first.
3. Check that every platform is reachable before adding detail.
4. Add ramps, stairs and bridges once core traversal reads clearly.
5. Colour by gameplay role, not at random.

| Pattern | Typical setup |
|--------|----------------|
| Floor | `sizeY` 0.2-0.5, wide X and Z |
| Wall | thin depth (0.2-0.3), tall Y |
| Pillar | `Cylinder`, small X and Z, `y = height/2` |
| Ramp | `Cube` plus vertex edits |
| Staircase | `Stairs`, `sizeY` = total rise, `sizeZ` = total run |
| Bridge | thin Cube aligned to the connected platforms |
| Room | floor, ceiling and four walls |

## Furniture

Props are rarely one box. Split them into visible structural parts.

| Furniture | Main parts |
|----------|------------|
| Desk | tabletop and four legs |
| Chair | seat, four legs and backrest |
| Shelf | two sides, shelves and optional back |
| Bed | mattress, frame and headboard |
| Monitor | screen, stand and base |

Desk assembly under a parent:

```json
{"steps":[
 {"skill":"gameobject_create","args":{"name":"Desk_0"}},
 {"skill":"probuilder_create_batch","args":{"defaultParent":"Desk_0","items":"[{\"shape\":\"Cube\",\"name\":\"Desk_0_Top\",\"sizeX\":1.2,\"sizeY\":0.04,\"sizeZ\":0.6,\"y\":0.73},{\"shape\":\"Cube\",\"name\":\"Desk_0_Leg_FL\",\"sizeX\":0.04,\"sizeY\":0.71,\"sizeZ\":0.04,\"x\":-0.55,\"y\":0.355,\"z\":0.25},{\"shape\":\"Cube\",\"name\":\"Desk_0_Leg_FR\",\"sizeX\":0.04,\"sizeY\":0.71,\"sizeZ\":0.04,\"x\":0.55,\"y\":0.355,\"z\":0.25},{\"shape\":\"Cube\",\"name\":\"Desk_0_Leg_BL\",\"sizeX\":0.04,\"sizeY\":0.71,\"sizeZ\":0.04,\"x\":-0.55,\"y\":0.355,\"z\":-0.25},{\"shape\":\"Cube\",\"name\":\"Desk_0_Leg_BR\",\"sizeX\":0.04,\"sizeY\":0.71,\"sizeZ\":0.04,\"x\":0.55,\"y\":0.355,\"z\":-0.25}]\"}}
]}
```

Typical sizes:

| Furniture | Typical size |
|----------|---------------|
| Office desk | 1.2 x 0.6 x 0.75 m |
| Dining table | 1.5 x 0.8 x 0.75 m |
| Chair | seat 0.45 x 0.45 x 0.45 m, total height 0.85 m |
| Bookshelf | 0.8 x 0.3 x 1.8 m |
| Bed | 0.9 x 2.0 x 0.45 m mattress area |
| Sofa | 2.0 x 0.8 x 0.45 m seat, 0.85 m back |

## Detail rules

- Use `probuilder_bevel_edges` only on chunky geometry; thin slabs bevel badly.
- Use face extrusion for lips, trims and ledges.
- Group multipart props under one parent for duplication and transforms.
- Use quick prototype colours before committing to material assets.

## Colour by gameplay role

| Role | RGB suggestion |
|------|----------------|
| Ground and floor | (0.4, 0.4, 0.4) |
| Walls | (0.6, 0.6, 0.65) |
| Platforms | (0.2, 0.5, 0.8) |
| Hazard or challenge | (0.9, 0.3, 0.1) |
| Ramps and slopes | (0.8, 0.6, 0.2) |
| Goal or finish | (0.1, 0.8, 0.2) |

## Mass production

Build one detailed template, then duplicate it with `gameobject_duplicate`, rename each copy with `gameobject_rename` and place it with `gameobject_set_transform`, using the `entityId` returned by each step (`$ref` steps in one batch).
