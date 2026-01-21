---
name: unity-mcp-skill
description: A comprehensive collection of skills for AI-driven Unity Editor automation via REST API
---

# Unity MCP Skills Collection

AI-powered Unity Editor automation through REST API. This skill collection enables intelligent control of Unity Editor including GameObject manipulation, scene management, asset handling, and much more.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Claude / AI Agent                         │
│                    (Skill Consumer)                          │
└─────────────────────┬───────────────────────────────────────┘
                      │ HTTP REST API
                      ▼
┌─────────────────────────────────────────────────────────────┐
│                unity_skills.py Client                        │
│        (Python wrapper for skill invocation)                 │
└─────────────────────┬───────────────────────────────────────┘
                      │ HTTP POST to localhost:8080
                      ▼
┌─────────────────────────────────────────────────────────────┐
│             SkillsForUnity (Editor Plugin)                   │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │ HTTP Server │→ │Skill Router │→ │ [UnitySkill] Methods│  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

## Available Skill Modules

| Module | Skills | Description |
|--------|--------|-------------|
| [gameobject](./gameobject/SKILL.md) | 7 | Create, modify, find GameObjects |
| [component](./component/SKILL.md) | 5 | Add, remove, configure components |
| [scene](./scene/SKILL.md) | 6 | Scene loading, saving, management |
| [material](./material/SKILL.md) | 17 | Material creation, HDR emission, keywords |
| [light](./light/SKILL.md) | 5 | Lighting setup and configuration |
| [animator](./animator/SKILL.md) | 8 | Animation controller management |
| [ui](./ui/SKILL.md) | 10 | UI Canvas and element creation |
| [validation](./validation/SKILL.md) | 7 | Project validation and checking |
| [prefab](./prefab/SKILL.md) | 4 | Prefab creation and instantiation |
| [asset](./asset/SKILL.md) | 8 | Asset import, organize, search |
| [editor](./editor/SKILL.md) | 11 | Editor state, play mode, selection |
| [console](./console/SKILL.md) | 5 | Log capture and debugging |
| [script](./script/SKILL.md) | 4 | C# script creation and search |
| [shader](./shader/SKILL.md) | 3 | Shader creation and listing |

**Total: 100 Skills**

## Quick Start

```python
import unity_skills

# Create a simple object
unity_skills.call_skill("gameobject_create", 
    name="Player", 
    primitiveType="Cube"
)

# Add a component
unity_skills.call_skill("component_add",
    name="Player",
    componentType="Rigidbody"
)

# Create a light
unity_skills.call_skill("light_create",
    name="MainLight",
    lightType="Directional"
)

# Save the scene
unity_skills.call_skill("scene_save")
```

## Use Cases

### Game Development
- Rapid prototyping
- Level generation
- Automated testing
- Asset management

### Education
- Interactive Unity tutorials
- Step-by-step demonstrations
- Learning automation

### Productivity
- Batch operations
- Project validation
- Documentation generation

## Installation

1. Copy `SkillsForUnity` folder to your Unity project's `Assets/Editor/`
2. Place `unity_skills.py` in your Python project
3. Ensure Unity Editor is running with the plugin active
4. Start making skill calls!

## TextMeshPro Support

UI Skills 动态检测项目中是否安装了 TextMeshPro：

- **有 TMP**：自动使用 `TextMeshProUGUI` 组件
- **无 TMP**：自动回退到 Legacy `UnityEngine.UI.Text` 组件

返回值中 `usingTMP` 字段指示使用了哪种文本组件：

```json
{
  "success": true,
  "name": "MyText",
  "usingTMP": true
}
```

## Configuration

Default server: `http://localhost:8080`

Configure via environment variable:
```bash
export UNITY_MCP_URL=http://localhost:8080
```

## Error Handling

All skills return consistent response format:

```json
{
  "status": "success|error",
  "skill": "skill_name",
  "result": {
    "success": true,
    "message": "..."
  }
}
```

## Best Practices

1. **Check Editor State**: Verify Unity is not compiling before operations
2. **Use Undo**: Leverage undo/redo for safe experimentation
3. **Batch Operations**: Group related operations together
4. **Validate First**: Run validation skills to check project health
5. **Organize Assets**: Use consistent folder structures

## Contributing

See [README.md](../../README.md) for contribution guidelines.
