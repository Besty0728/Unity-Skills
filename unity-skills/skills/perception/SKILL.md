---
name: unity-perception
description: "Scene Understanding Skills - Help AI quickly perceive project state."
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
