---
name: unity-smart
description: "Smart Agentic Skills for scene querying, layout, and auto-binding."
---

# Unity Smart Skills

## Skills

### smart_scene_query
Find objects based on component property values (SQL-like).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `componentName` | string | Yes | - | Component type (Light, Camera, MeshRenderer) |
| `propertyName` | string | Yes | - | Property to query (intensity, enabled, etc.) |
| `op` | string | No | "==" | ==, !=, >, <, >=, <=, contains |
| `value` | string | No | null | Value to compare |
| `limit` | int | No | 50 | Max results |

**Example**:
```python
# Find all lights with intensity > 2
call_skill("smart_scene_query", componentName="Light", propertyName="intensity", op=">", value="2")
```

---

### smart_scene_layout
Organize selected objects into a layout.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `layoutType` | string | No | "Linear" | Linear, Grid, Circle, Arc |
| `axis` | string | No | "X" | X, Y, Z, -X, -Y, -Z |
| `spacing` | float | No | 2.0 | Space between items (or radius) |
| `columns` | int | No | 3 | For Grid layout |
| `arcAngle` | float | No | 180 | For Arc layout (degrees) |
| `lookAtCenter` | bool | No | false | Rotate to face center |

**Example**:
```python
# Arrange selected objects in a circle
call_skill("smart_scene_layout", layoutType="Circle", spacing=5)
```

---

### smart_reference_bind
Auto-fill a List/Array field with matching objects.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `targetName` | string | Yes | - | Target GameObject |
| `componentName` | string | Yes | - | Component on target |
| `fieldName` | string | Yes | - | Field to fill |
| `sourceTag` | string | No | null | Find by tag |
| `sourceName` | string | No | null | Find by name contains |
| `appendMode` | bool | No | false | Append instead of replace |

**Example**:
```python
# Fill GameManager.spawns with all SpawnPoint tagged objects
call_skill("smart_reference_bind", targetName="GameManager", componentName="GameController", fieldName="spawns", sourceTag="SpawnPoint")
```
