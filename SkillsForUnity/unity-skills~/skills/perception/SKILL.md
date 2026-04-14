---
name: unity-perception
description: "Scene understanding and analysis. Use when users want to get a summary, overview, dependency report, or export of the current scene state. Triggers: scene summary, analyze, overview, statistics, count, export report, 场景摘要, Unity分析, Unity概览, Unity统计, 导出报告, 依赖分析."
---

# Unity Perception Skills

## Guardrails

**Mode**: Semi-Auto (available by default)

**DO NOT** (common hallucinations):
- `perception_analyze` does not exist → use `scene_summarize` or `script_analyze`
- `perception_scan` / `perception_describe` do not exist
- `scene_context` ≠ `editor_get_context`: `scene_context` exports full hierarchy+components, `editor_get_context` returns current selection+editor state
- `scene_analyze` / `scene_health_check` / `scene_contract_validate` / `scene_component_stats` / `scene_find_hotspots` / `project_stack_detect` belong to `perception`, not `scene` or `project`

**Routing**:
- For current editor state (selection, play mode) → use `editor` module's `editor_get_context`
- For object search → use `scene_find_objects` (scene module) or `gameobject_find` (gameobject module, Full-Auto)
- For script dependency analysis → `script_dependency_graph` (this module)

## Skills

### scene_analyze
Analyze the active scene and project context in one pass.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `topComponentsLimit` | int | No | 10 | Max components to include in stats |
| `issueLimit` | int | No | 100 | Max findings returned |

**Returns:** `summary`, `stats`, `findings`, `warnings`, `recommendations`, `suggestedNextSkills`

---

### scene_health_check
Run a read-only health report for the active scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `issueLimit` | int | No | 100 | Max findings returned |
| `deepHierarchyThreshold` | int | No | 8 | Depth threshold for hierarchy hotspot |
| `largeChildCountThreshold` | int | No | 25 | Child-count threshold for large-group hotspot |

**Returns:** `summary`, `findings`, `hotspots`, `suggestedNextSkills`

---

### scene_contract_validate
Validate default scene conventions such as `Systems` / `Managers` / `Gameplay` / `UIRoot`, plus optional tag/layer requirements.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `requiredRootsJson` | string | No | built-in defaults | JSON string array of required root names |
| `requiredTagsJson` | string | No | `[]` | JSON string array of required tags |
| `requiredLayersJson` | string | No | `[]` | JSON string array of required layers |
| `requireEventSystemForUi` | bool | No | true | Require EventSystem when UGUI is present |

**Returns:** `summary`, `findings`, `checkedRoots`, `checkedTags`, `checkedLayers`

---

### project_stack_detect
Detect project render pipeline, input path, UI route, major packages, and common folder conventions.

No parameters.

**Returns:** `unityVersion`, `renderPipeline`, `input`, `ui`, `packages`, `tests`, `projectFolders`, `projectProfile`

---

### scene_component_stats
Get extended component and infrastructure statistics for the active scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `topComponentsLimit` | int | No | 15 | Max components to include |

**Returns:** `stats`, `keyFacilities`, `topComponents`

---

### scene_find_hotspots
Find deep hierarchies, large child groups, duplicate-name clusters, and empty-node hotspots.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `deepHierarchyThreshold` | int | No | 8 | Depth threshold |
| `largeChildCountThreshold` | int | No | 25 | Child-count threshold |
| `maxResults` | int | No | 20 | Max hotspots returned |

**Returns:** `hotspotCount`, `hotspots`

---

### scene_summarize
Get a structured summary of the current scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeComponentStats` | bool | No | true | Count component types |
| `topComponentsLimit` | int | No | 10 | Max components to list |

**Returns**:
```json
{
  "sceneName": "Main",
  "stats": {
    "totalObjects": 156,
    "activeObjects": 142,
    "rootObjects": 12,
    "maxHierarchyDepth": 5,
    "lights": 3,
    "cameras": 2,
    "canvases": 1
  },
  "topComponents": [{"component": "MeshRenderer", "count": 45}, ...]
}
```

---

### hierarchy_describe
Get a text tree of the scene hierarchy.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 5 | Max tree depth |
| `includeInactive` | bool | No | false | Include inactive objects |
| `maxItemsPerLevel` | int | No | 20 | Limit per level |

**Returns**:
```
Scene: Main
────────────────────────────────────────
► Main Camera 📷
► Directional Light 💡
► Environment
  ├─ Ground ▣
  ├─ Trees
    ├─ Tree_001 ▣
    ├─ Tree_002 ▣
► Canvas 🖼
  ├─ StartButton 🔘
```

---

### script_analyze
Analyze a MonoBehaviour script's public API.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptName` | string | Yes | - | Script class name |
| `includePrivate` | bool | No | false | Include non-public members |

**Returns**:
```json
{
  "script": "PlayerController",
  "fields": [{"name": "speed", "type": "float", "isSerializable": true}],
  "properties": [{"name": "IsGrounded", "type": "bool", "canWrite": false}],
  "methods": [{"name": "Jump", "returnType": "void", "parameters": ""}],
  "unityCallbacks": ["Start", "Update", "OnCollisionEnter"]
}
```

---

### scene_spatial_query
Find objects within a radius of a point, or near another object.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | float | No | 0 | Center X coordinate |
| `y` | float | No | 0 | Center Y coordinate |
| `z` | float | No | 0 | Center Z coordinate |
| `radius` | float | No | 10 | Search radius |
| `nearObject` | string | No | - | Find near this object instead of coordinates |
| `componentFilter` | string | No | - | Only include objects with this component |
| `maxResults` | int | No | 50 | Max results to return |

**Returns**:
```json
{
  "center": {"x": 0, "y": 0, "z": 0},
  "radius": 10,
  "totalFound": 5,
  "results": [{"name": "Enemy", "path": "Enemies/Enemy", "distance": 3.2, "position": {"x": 1, "y": 0, "z": 3}}]
}
```

---

### scene_materials
Get an overview of all materials and shaders used in the current scene.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `includeProperties` | bool | No | false | Include shader property list |

**Returns**:
```json
{
  "totalMaterials": 12,
  "totalShaders": 4,
  "shaders": [{"shader": "Standard", "materialCount": 5, "materials": [{"name": "Ground", "userCount": 3}]}]
}
```

---

### scene_context
Generate a comprehensive scene snapshot for AI coding assistance (hierarchy, components, script fields, references, UI layout).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 10 | Max hierarchy depth to traverse |
| `maxObjects` | int | No | 200 | Max objects to export |
| `rootPath` | string | No | - | Only export a subtree (e.g. "Canvas/MainPanel") |
| `includeValues` | bool | No | false | Include serialized field values |
| `includeReferences` | bool | No | true | Include cross-object references |
| `includeCodeDeps` | bool | No | false | Include C# code-level dependency edges (regex-based) |

**Returns**:
```json
{
  "sceneName": "Main",
  "totalObjects": 156,
  "exportedObjects": 85,
  "truncated": true,
  "objects": [
    {
      "path": "Canvas/MainPanel/StartButton",
      "name": "StartButton",
      "active": true,
      "tag": "Untagged",
      "layer": "UI",
      "components": [
        {"type": "RectTransform", "props": {"anchoredPosition": "(120, -50)", "sizeDelta": "(200, 60)"}},
        {"type": "Button", "props": {"interactable": true, "transition": "ColorTint"}},
        {"type": "PlayerUIController", "kind": "MonoBehaviour", "fields": {"speed": {"type": "Float", "value": 5.5}, "target": {"type": "GameObject", "value": "Player/Body"}}}
      ],
      "children": ["Canvas/MainPanel/StartButton/Text"]
    }
  ],
  "references": [
    {"from": "Canvas/MainPanel/StartButton:PlayerUIController.target", "to": "Player/Body"}
  ],
  "codeDependencies": [
    {"from": "PlayerUIController.Start", "to": "HealthSystem", "type": "GetComponent", "detail": "GetComponent<HealthSystem>()"}
  ]
}
```

---

### scene_export_report
Export complete scene structure and script dependency report as markdown file. Includes: hierarchy tree (built-in components name only, user scripts marked with `*`), user script fields with values, deep C# code-level dependencies (10 patterns: `GetComponent<T>`, `FindObjectOfType<T>`, `SendMessage`, field references, Singleton access, static member access, `new T()`, generic type args, inheritance/interface, `typeof`/`is`/`as` type checks), and merged dependency graph with risk ratings.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `savePath` | string | No | "Assets/Docs/SceneReport.md" | Output file path |
| `maxDepth` | int | No | 10 | Max hierarchy depth |
| `maxObjects` | int | No | 500 | Max objects to export |

**Returns**:
```json
{
  "success": true,
  "savedTo": "Assets/Docs/SceneReport.md",
  "objectCount": 156,
  "userScriptCount": 5,
  "referenceCount": 12,
  "codeReferenceCount": 4
}
```

**Markdown output sections**:
1. **Hierarchy** — tree with component names, user scripts marked `*`
2. **Script Fields** — only user scripts (non-Unity namespace), with field values and reference targets
3. **Code Dependencies** — C# source analysis (comments stripped): `GetComponent<T>`, `FindObjectOfType<T>`, `SendMessage`, field references, inheritance (multi-class), static access (PascalCase only). Method-level location in `From` column.
4. **Dependency Graph** — table with columns: `From | To | Type | Source | Detail`. Source = `scene` (serialized reference) or `code` (source analysis). From shows `ClassName.MethodName` for code deps.

---

### scene_dependency_analyze
Analyze object dependency graph and impact of changes. Use ONLY when user explicitly asks about dependency/impact analysis, safe to delete/disable, refactoring impact, or reference checks.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `targetPath` | string | No | - | Analyze specific subtree (e.g. "Canvas/HUD") |
| `savePath` | string | No | - | Save analysis as markdown (e.g. "Assets/Docs/deps.md") |

**Returns**:
```json
{
  "sceneName": "Main",
  "totalReferences": 12,
  "objectsAnalyzed": 5,
  "analysis": [
    {
      "path": "Canvas/HUD/HealthBar",
      "risk": "medium",
      "dependedByCount": 3,
      "dependedBy": [
        {"source": "Player", "script": "PlayerController", "field": "healthUI", "fieldType": "Slider"}
      ],
      "dependsOnCount": 0,
      "dependsOn": null
    }
  ],
  "savedTo": "Assets/Docs/deps.md",
  "markdown": null
}
```

---

### script_dependency_graph
Given an entry script, return its N-hop dependency closure as structured JSON. Shows which scripts to read to understand or safely modify a feature.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptName` | string | Yes | - | Entry script class name |
| `maxHops` | int | No | 2 | Dependency traversal hops (bidirectional) |
| `includeDetails` | bool | No | true | Include fields and Unity callbacks per script |

**Returns**:
```json
{
  "success": true,
  "entryScript": "PlayerController",
  "totalScriptsReached": 5,
  "maxHops": 2,
  "scripts": [
    {
      "name": "PlayerController",
      "hop": 0,
      "kind": "MonoBehaviour",
      "baseClass": "MonoBehaviour",
      "filePath": "Assets/Scripts/PlayerController.cs",
      "dependsOn": ["HealthSystem", "InputManager"],
      "dependedBy": ["EnemyAI"],
      "fields": [{"name": "health", "type": "HealthSystem", "serializable": true}],
      "unityCallbacks": ["Start", "Update"]
    }
  ],
  "edges": [
    {"from": "PlayerController", "to": "HealthSystem", "type": "FieldReference", "detail": "field:HealthSystem"}
  ],
  "suggestedReadOrder": ["InputManager", "HealthSystem", "EnemyAI", "PlayerController"]
}
```

---

### scene_tag_layer_stats
Get Tag/Layer usage stats and find potential issues (untagged objects, unused layers).

No parameters.

**Returns**:
```json
{
  "success": true,
  "totalObjects": 156,
  "untaggedCount": 120,
  "tags": [{"tag": "Untagged", "count": 120}, {"tag": "Player", "count": 5}],
  "layers": [{"layer": "Default", "count": 140}, {"layer": "UI", "count": 16}],
  "emptyDefinedLayers": ["Water", "PostProcessing"]
}
```

---

### scene_performance_hints
Diagnose scene performance issues with prioritized actionable suggestions.

No parameters.

**Returns**:
```json
{
  "success": true,
  "hintCount": 2,
  "hints": [
    {"priority": 1, "category": "Lighting", "issue": "6 shadow-casting lights", "suggestion": "Reduce to ≤4 or use baked lighting", "fixSkill": "light_set_properties"},
    {"priority": 2, "category": "Batching", "issue": "150 non-static renderers", "suggestion": "Mark static objects with optimize_set_static_flags", "fixSkill": "optimize_set_static_flags"}
  ]
}
```
