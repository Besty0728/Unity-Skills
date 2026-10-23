### particle_set_renderer
| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (or `name` / `path` / `instanceId`) |
| `configJson` | string | Yes | - | `{"schemaVersion": 1, "renderer": {...}}` |

`material` / `mesh` are asset references: `"Assets/FX/Fire.mat"`, `{"guid": ...}`, or built-ins `{"assetPath": "Resources/unity_builtin_extra", "name": "Default-ParticleSystem"}` / `{"assetPath": "Library/unity default resources", "name": "Cube"}`. `renderMode: "Mesh"` needs a mesh.
