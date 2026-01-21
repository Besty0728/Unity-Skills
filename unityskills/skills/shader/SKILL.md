---
name: unity-shader
description: Create and manage shaders in Unity Editor via REST API
---

# Unity Shader Skills

Work with shaders - create shader files, read source code, and list available shaders.

## Capabilities

- Create shader files from templates
- Read shader source code
- List all shaders in project

## Skills Reference

| Skill | Description |
|-------|-------------|
| `shader_create` | Create shader file |
| `shader_read` | Read shader source |
| `shader_list` | List all shaders |

## Parameters

### shader_create

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderName` | string | Yes | - | Shader name |
| `savePath` | string | Yes | - | Save path |
| `template` | string | No | "Unlit" | Template type |

### shader_read

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderPath` | string | Yes | Shader asset path |

### shader_list

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filter` | string | No | null | Name filter |
| `limit` | int | No | 100 | Max results |

## Shader Templates

| Template | Description |
|----------|-------------|
| `Unlit` | Basic unlit shader |
| `Standard` | PBR surface shader |
| `Transparent` | Alpha blended |

## Example Usage

```python
import unity_skills

# Create an unlit shader
unity_skills.call_skill("shader_create",
    shaderName="Custom/MyUnlit",
    savePath="Assets/Shaders/MyUnlit.shader",
    template="Unlit"
)

# Create a surface shader
unity_skills.call_skill("shader_create",
    shaderName="Custom/MyPBR",
    savePath="Assets/Shaders/MyPBR.shader",
    template="Standard"
)

# Read shader source
source = unity_skills.call_skill("shader_read",
    shaderPath="Assets/Shaders/MyUnlit.shader"
)
print(source['result']['content'])

# List all custom shaders
shaders = unity_skills.call_skill("shader_list",
    filter="Custom"
)
for shader in shaders['result']['shaders']:
    print(f"{shader['name']}: {shader['path']}")

# List all Unity standard shaders
unity_shaders = unity_skills.call_skill("shader_list",
    filter="Standard"
)
```

## Response Format

```json
{
  "status": "success",
  "skill": "shader_list",
  "result": {
    "success": true,
    "count": 5,
    "shaders": [
      {
        "name": "Custom/MyUnlit",
        "path": "Assets/Shaders/MyUnlit.shader"
      },
      {
        "name": "Custom/MyPBR",
        "path": "Assets/Shaders/MyPBR.shader"
      }
    ]
  }
}
```

## Best Practices

1. Use consistent shader naming (Category/Name)
2. Organize shaders in dedicated folder
3. Start with templates, modify as needed
4. Test shaders in different lighting conditions
5. Consider mobile compatibility for builds
