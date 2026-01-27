---
name: unity-shader
description: "Create and manage shaders in Unity Editor."
---

# Unity Shader Skills

Work with shaders - create shader files, read source code, and list available shaders.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `shader_create` | Create shader file |
| `shader_read` | Read shader source |
| `shader_list` | List all shaders |

---

## Skills

### shader_create
Create a shader file from template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderName` | string | Yes | - | Shader name (e.g., "Custom/MyShader") |
| `savePath` | string | Yes | - | Save path |
| `template` | string | No | "Unlit" | Template type |

**Templates**:
| Template | Description |
|----------|-------------|
| `Unlit` | Basic unlit shader |
| `Standard` | PBR surface shader |
| `Transparent` | Alpha blended |

### shader_read
Read shader source code.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderPath` | string | Yes | Shader asset path |

**Returns**: `{success, path, content}`

### shader_list
List all shaders in project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filter` | string | No | null | Name filter |
| `limit` | int | No | 100 | Max results |

**Returns**: `{success, count, shaders: [{name, path}]}`

---

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
print(source['content'])

# List all custom shaders
shaders = unity_skills.call_skill("shader_list", filter="Custom")
for shader in shaders['shaders']:
    print(f"{shader['name']}: {shader['path']}")
```

## Best Practices

1. Use consistent shader naming (Category/Name)
2. Organize shaders in dedicated folder
3. Start with templates, modify as needed
4. Test shaders in different lighting conditions
5. Consider mobile compatibility for builds
