---
name: unity-perception
description: "Scene understanding and analysis. Use when users want to get a summary, overview, dependency report, or export of the current scene state. Triggers: scene summary, analyze, overview, statistics, count, export report, 场景摘要, Unity分析, Unity概览, Unity统计, 导出报告, 依赖分析."
---

# Unity Perception Skills

## Skills

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
  ]
}
```

---

### scene_export_report
Export complete scene structure and script dependency report as markdown file. Includes: hierarchy tree (built-in components name only, user scripts marked with `*`), user script fields with values, C# code-level dependencies (`GetComponent<T>`, `FindObjectOfType<T>`, `SendMessage`, field references), and merged dependency graph with risk ratings.

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
3. **Code Dependencies** — C# source analysis: `GetComponent<T>`, `FindObjectOfType<T>`, `SendMessage`, field type references between user MonoBehaviours
4. **Dependency Graph** — merged serialized + code references, risk rating per object/script

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
