### terrain_generate_perlin
Generate natural-looking terrain from Perlin noise. Recommended for a whole-terrain base.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scale` | float | No | 20.0 | Noise scale (lower = larger features) |
| `heightMultiplier` | float | No | 0.3 | Height intensity (0-1) |
| `octaves` | int | No | 4 | Detail layers (more = more detail) |
| `persistence` | float | No | 0.5 | Amplitude decrease per octave |
| `lacunarity` | float | No | 2.0 | Frequency increase per octave |
| `seed` | int | No | 0 | Random seed (0 = random) |
| `name` | string | No | null | Terrain name |
| `entityId` | string | No | null | Terrain entity ID (preferred; the only id on Unity 6000.4+) |
| `instanceId` | int | No | 0 | Terrain instance ID |
| `path` | string | No | null | Terrain hierarchy path |

**Returns**: `{success, resolution, scale, heightMultiplier, octaves, persistence, lacunarity, seed}`

```
curl -s "http://localhost:<port>/skill/terrain_generate_perlin?expectProject=<project>" -d '{"scale":25,"heightMultiplier":0.4,"octaves":4,"seed":12345}'
```
