# Unity Terrain Skills

Create terrain and edit its heightmap and texture layers. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Creating or editing terrain
- Sculpting or smoothing the heightmap
- Painting terrain texture layers

## Rules
- Sculpt and paint operations need an existing Terrain in the scene. Create one with `terrain_create`.
- Heights and positions are normalized (0-1) unless a skill says it takes pixel indexes (`terrain_set_heights_batch` `startX` / `startZ`).
- Every skill except `terrain_create` picks its terrain with `name`, `entityId`, `instanceId` or `path`; use `entityId` where the Editor supports it.
- For the terrain material use the [material](../material/MODULE.md) module. To place objects on the terrain use the [gameobject](../gameobject/MODULE.md) module.
- To remove a terrain, delete its asset with `asset_delete`.

## Pitfalls
- `terrain_set_texture` does not exist. Use `terrain_paint_texture` with a layer index.
- `terrain_add_tree` and `terrain_add_grass` do not exist. Trees and grass need the Unity Terrain tools or custom scripts.
- `terrain_set_size` does not exist. Terrain dimensions are set at creation, in `terrain_create`.
- `terrain_import_heightmap` and `terrain_set_heights` do not exist. Use `terrain_set_heights_batch` with a 2D array indexed `[z][x]` of values 0-1.

## Skills Overview

| Skill | Purpose |
|-------|---------|
| [`terrain_create`](reference/terrain_create.md) | Create a Terrain with TerrainData |
| [`terrain_get_info`](reference/terrain_get_info.md) | Get size, resolution and layers |
| [`terrain_get_height`](reference/terrain_get_height.md) | Get the height at a world position |
| [`terrain_set_height`](reference/terrain_set_height.md) | Set the height at normalized coordinates |
| [`terrain_set_heights_batch`](reference/terrain_set_heights_batch.md) | Set heights over a rectangular region |
| [`terrain_add_hill`](reference/terrain_add_hill.md) | Add a smooth hill (recommended) |
| [`terrain_generate_perlin`](reference/terrain_generate_perlin.md) | Generate terrain from Perlin noise (recommended) |
| [`terrain_smooth`](reference/terrain_smooth.md) | Smooth heights to reduce sharp edges (recommended) |
| [`terrain_flatten`](reference/terrain_flatten.md) | Flatten a region to a target height (recommended) |
| [`terrain_paint_texture`](reference/terrain_paint_texture.md) | Paint a texture layer at a position |

## Workflow

Terrain operations support workflow undo. Start a session with `workflow_session_start`, run the terrain skills, then `workflow_session_end`. Undo the whole session with `workflow_session_undo`. See the [workflow](../workflow/MODULE.md) module.

## Example

Quick terrain: create it, then generate its base shape with Perlin noise, then add a hill:

```
curl -s "http://localhost:<port>/skill/terrain_create?expectProject=<project>" -d '{"name":"MyTerrain","width":200,"length":200,"height":50}'
curl -s "http://localhost:<port>/skill/terrain_generate_perlin?expectProject=<project>" -d '{"scale":20,"heightMultiplier":0.4,"octaves":5}'
curl -s "http://localhost:<port>/skill/terrain_add_hill?expectProject=<project>" -d '{"normalizedX":0.3,"normalizedZ":0.6,"radius":0.2,"height":0.5}'
```
