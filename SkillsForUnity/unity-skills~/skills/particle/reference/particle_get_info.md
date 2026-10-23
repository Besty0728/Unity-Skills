### particle_get_info
Summarize one ParticleSystem: playback state, system seed settings, a Main summary, every module's enabled flag, a renderer summary and the burst count.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `entityId` | string | No* | null | Target (preferred; the only id on Unity 6000.4+) |
| `name` | string | No* | null | Target by name |
| `instanceId` | int | No* | 0 | Target by instance id (older Unity) |
| `path` | string | No* | null | Target by hierarchy path |
| `includeModules` | bool | No | false | false (default): summary; true: the full readback of every module. |

*Exactly one locator (`entityId`, `name`, `path` or `instanceId`) identifies the target; an id that names nothing is an error, never a fallback to the name.

**Returns**: `{success, name, entityId, instanceId, path, isPrefabInstance, playback, system, main, modules, renderer, burstCount, detail}`. With `includeModules=true` `main`, `modules` and `renderer` carry every supported field in the shape `particle_configure` accepts (`renderer` is `null` when the object has no renderer); `detail` says `full` or points to `includeModules=true` / `particle_get_module`.
