---
name: unity-shader
description: "Shader creation and management. Use when users want to create or inspect shaders. Triggers: shader, HLSL, ShaderLab, Unlit, Standard, custom shader, 着色器, 创建Shader."
---

# Unity Shader Skills

Work with shaders - create shader files, read source code, and list available shaders.

## Guardrails

**Mode**: Full-Auto required

**DO NOT** (common hallucinations):
- `shader_set_property` does not exist → use `material_set_float`/`material_set_color`/etc. on the material, not the shader
- `shader_apply` / `shader_assign` do not exist → use `material_set_shader` to change a material's shader
- `shader_get_properties` returns shader **property definitions** (name/type/range), not current values → for material instance values use `material_get_properties`
- Shader names are case-sensitive and path-like: `"Standard"`, `"Universal Render Pipeline/Lit"`, not `"standard"` or `"URP Lit"`

**Routing**:
- For material property changes → use `material` module
- For shader keyword control → `material_set_keyword` (material module)
- For global shader keywords → `shader_set_global_keyword` (this module)

## Skills Overview

| Skill | Description |
|-------|-------------|
| `shader_create` | Create shader file |
| `shader_read` | Read shader source |
| `shader_list` | List all shaders |
| `shader_find` | Find shader by name |
| `shader_delete` | Delete shader file |
| `shader_get_properties` | Get shader properties |
| `shader_check_errors` | Check shader for compilation errors |
| `shader_get_keywords` | Get shader keyword list |
| `shader_get_variant_count` | Get shader variant count for performance analysis |
| `shader_create_urp` | Create a URP shader from template |
| `shader_set_global_keyword` | Enable or disable a global shader keyword |

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
| `searchName` | string | Yes | Shader name to find |

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
| `shaderNameOrPath` | string | Yes | Shader name or shader asset path |

**Returns**: `{success, properties: [{name, type, description}]}`

### `shader_check_errors`
Check shader for compilation errors.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderNameOrPath` | string | Yes | - | Shader name or asset path (e.g., "Custom/MyShader" or "Assets/Shaders/My.shader") |

**Returns:** `{ shaderName, hasErrors, messageCount }`

### `shader_get_keywords`
Get shader keyword list.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderNameOrPath` | string | Yes | - | Shader name or asset path (e.g., "Custom/MyShader" or "Assets/Shaders/My.shader") |

**Returns:** `{ shaderName, keywordCount, keywords: [{ name, type }] }`

### `shader_get_variant_count`
Get shader variant count for performance analysis.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderNameOrPath` | string | Yes | - | Shader name or asset path (e.g., "Custom/MyShader" or "Assets/Shaders/My.shader") |

**Returns:** `{ shaderName, subshaderCount, totalPasses }`

### `shader_create_urp`
Create a URP shader from template (type: Unlit or Lit).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderName` | string | Yes | - | Shader name (e.g., "Custom/MyURPShader") |
| `savePath` | string | Yes | - | Save path (e.g., "Assets/Shaders/MyURP.shader") |
| `type` | string | No | "Unlit" | Template type: "Unlit" or "Lit" |

**Returns:** `{ success, shaderName, path, type }`

### `shader_set_global_keyword`
Enable or disable a global shader keyword.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `keyword` | string | Yes | - | Global shader keyword name |
| `enabled` | bool | Yes | - | true to enable, false to disable |

**Returns:** `{ success, keyword, enabled }`

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

---
## Exact Signatures

Exact names, parameters, defaults, and returns are defined by `GET /skills/schema` or `unity_skills.get_skill_schema()`, not by this file.
