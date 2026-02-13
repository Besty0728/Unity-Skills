---
name: unity-shader
description: "Shader creation and management. Use when users want to create or inspect shaders. Triggers: shader, HLSL, ShaderLab, Unlit, Standard, custom shader, 着色器, 创建Shader."
---

# Unity Shader Skills

Work with shaders - create shader files, read source code, and list available shaders.

## Skills Overview

| Skill | Description |
|-------|-------------|
| `shader_create` | Create shader file |
| `shader_read` | Read shader source |
| `shader_list` | List all shaders |
| `shader_find` | Find shader by name |
| `shader_delete` | Delete shader file |
| `shader_get_properties` | Get shader properties |

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

### shader_find
Find a shader by name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderName` | string | Yes | Shader name to find |

**Returns**: `{success, name, path, propertyCount}`

### shader_delete
Delete a shader file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderPath` | string | Yes | Shader asset path |

### shader_get_properties
Get all properties defined in a shader.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderName` | string | No* | Shader name |
| `shaderPath` | string | No* | Shader asset path |

**Returns**: `{success, properties: [{name, type, description}]}`

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
