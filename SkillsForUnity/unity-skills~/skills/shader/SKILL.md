---
name: unity-shader
description: Manage HLSL/ShaderLab .shader files
---

# Unity Shader Skills

Work with `.shader` HLSL/ShaderLab files: create from templates, read source, list, find, inspect keywords, properties and variants, check errors, delete, and toggle global keywords. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md). For node-based graphs use the [shadergraph](../shadergraph/SKILL.md) module.

## When to use
- Creating or editing handwritten shaders
- Listing or finding `.shader` files
- Inspecting properties and keywords

## Rules
- `shader_create` takes `template` as raw ShaderLab/HLSL source, written verbatim. It is not a preset name: `template="Standard"` writes the word `Standard` into the file. For a URP preset use `shader_create_urp` with `type` `"Unlit"` or `"Lit"`.
- Shader names are case-sensitive and path-like: `"Standard"` or `"Universal Render Pipeline/Lit"`, not `"standard"` or `"URP Lit"`.
- `shader_get_properties` returns property definitions (name, type, range), not current values. For a material's values use `material_get_properties`.
- Material property changes belong to the [material](../material/SKILL.md) module; per-material keywords use `material_set_keyword`. `shader_set_global_keyword` toggles a global keyword.

## Pitfalls
- `shader_set_property`, `shader_apply` and `shader_assign` do not exist. To change a material's shader use `material_set_shader`; to set a material value use `material_set_float`, `material_set_color` and the other `material_set_*` skills.

## Skills

### shader_create
Create a shader file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderName` | string | Yes | - | Name written into the `Shader "..."` declaration (e.g. "Custom/MyShader") |
| `savePath` | string | Yes | - | Save path (e.g. "Assets/Shaders/My.shader") |
| `template` | string | No | null | Full ShaderLab/HLSL source. When omitted, a built-in Unlit template (`_MainTex` and `_Color`, single CGPROGRAM pass) is used. There are no other built-in presets. |

### shader_create_urp
Create a URP shader from a template.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `shaderName` | string | Yes | - | Shader name (e.g. "Custom/MyURPShader") |
| `savePath` | string | Yes | - | Save path (e.g. "Assets/Shaders/MyURP.shader") |
| `type` | string | No | "Unlit" | Template type: "Unlit" or "Lit" |

**Returns**: `{success, shaderName, path, type}`

### shader_read
Read shader source code.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderPath` | string | Yes | Shader asset path |

**Returns**: `{path, lines, content}`

### shader_list
List the shaders in the project.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filter` | string | No | null | Name filter |
| `limit` | int | No | 100 | Max results |

**Returns**: `{count, shaders: [{path, name, propertyCount}]}`

### shader_find
Find a shader by name.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `searchName` | string | Yes | Shader name to find |

**Returns**: `{found, name, path}`

### shader_delete
Delete a shader file.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderPath` | string | Yes | Shader asset path |

### shader_get_properties
Get the properties defined in a shader.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderNameOrPath` | string | Yes | Shader name or asset path |

**Returns**: `{success, shaderName, propertyCount, properties: [{name, type, description}]}`

### shader_check_errors
Check a shader for compilation errors.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderNameOrPath` | string | Yes | Shader name or asset path (e.g. "Custom/MyShader" or "Assets/Shaders/My.shader") |

**Returns**: `{shaderName, hasErrors, messageCount}`

### shader_get_keywords
Get the keyword list of a shader.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderNameOrPath` | string | Yes | Shader name or asset path |

**Returns**: `{shaderName, keywordCount, keywords: [{name, type}]}`

### shader_get_variant_count
Get the shader variant count for performance analysis.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `shaderNameOrPath` | string | Yes | Shader name or asset path |

**Returns**: `{shaderName, subshaderCount, totalPasses}`

### shader_set_global_keyword
Enable or disable a global shader keyword.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `keyword` | string | Yes | Global shader keyword name |
| `enabled` | bool | Yes | `true` to enable, `false` to disable |

**Returns**: `{success, keyword, enabled}`
