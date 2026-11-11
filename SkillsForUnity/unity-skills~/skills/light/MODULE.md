# Unity Light Skills

Create, configure and inspect lights, probe groups and reflection probes. Parameters come from the schema (`GET /skills/schema?category=Light&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). Per-skill parameter tables: `reference/<skill_name>.md`.

## When to use
- Adding or tuning lights
- Setting up scene lighting, probes or reflection captures
- Batch-enabling or disabling lights

## Rules
- Batch first: use the `*_batch` skill when operating on 2 or more lights.
- Target a light by `entityId` (preferred; the only id on Unity 6000.4+), `name`, `instanceId` (older Unity) or `path`. An id that names nothing is an error, never a fallback to the name.
- Query skills (`light_get_info`, `light_get_properties`, `light_find_all`, `light_get_lightmap_settings`) are `SemiAuto` and run in every mode.
- The writers (`light_create`, `light_set_properties*`, `light_set_enabled*`, `light_add_probe_group`, `light_add_reflection_probe`) are `FullAuto`: under Approval they need a grant.
- No skill here deletes a light. Use `gameobject_delete` on its GameObject.
- Colour channels are 0-1, not 0-255.

## Pitfalls
- `shadows` values are the enum members `None`, `Hard` and `Soft`. Input is case-insensitive, but responses echo the capitalised form, so compare against `Soft`.
- `range` applies to Point and Spot lights only; `spotAngle` to Spot only. Other types list them in `skipped`, not as errors.
- `light_set_properties_batch` and `light_set_enabled_batch` are all-or-nothing: one bad item rolls back the whole call.

## Routing
- Lightmap baking settings → `light_get_lightmap_settings`.
- Reflection probes → `light_add_reflection_probe`; light probe groups → `light_add_probe_group`.

## Light types

| Type | Description | Use case |
|------|-------------|----------|
| `Directional` | Parallel rays, no position | Sun, moon |
| `Point` | Omnidirectional from a point | Torches, bulbs |
| `Spot` | Cone-shaped beam | Flashlights, spotlights |
| `Area` | Rectangle or disc (baked only) | Windows, soft lights |

## Skills Overview

| Skill | Batch version | Purpose |
|-------|---------------|---------|
| [`light_create`](reference/light_create.md) | - | Create a light GameObject |
| [`light_set_properties`](reference/light_set_properties.md) | [`light_set_properties_batch`](reference/light_set_properties_batch.md) | Colour, intensity, range, spot angle, shadows |
| [`light_set_enabled`](reference/light_set_enabled.md) | [`light_set_enabled_batch`](reference/light_set_enabled_batch.md) | Enable or disable a light |
| [`light_get_info`](reference/light_get_info.md) | - | Detailed light information |
| [`light_get_properties`](reference/light_get_properties.md) | - | Alias of `light_get_info` |
| [`light_find_all`](reference/light_find_all.md) | - | List the lights in the open scenes |
| [`light_add_probe_group`](reference/light_add_probe_group.md) | - | Light Probe Group, optional grid |
| [`light_add_reflection_probe`](reference/light_add_reflection_probe.md) | - | Reflection Probe at a position |
| [`light_get_lightmap_settings`](reference/light_get_lightmap_settings.md) | - | Lightmap baking settings |

**DO NOT** (common hallucinations):
- `light_add` does not exist: use `light_create`.
- `light_set_color` and `light_set_intensity` do not exist: use `light_set_properties`.
- `light_set_shadow` does not exist: use `light_set_properties` with `shadows`.
- `light_delete` does not exist: use `gameobject_delete`.
