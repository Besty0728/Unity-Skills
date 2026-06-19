---
name: unity-shadergraph-recipes
description: "Shader Graph recipes limited to the supported node subset — proven node chains like the BaseColor texture-sample chain. 限定在受支持节点子集内的 Shader Graph 配方(经验证的节点链,如 BaseColor 贴图采样链)。"
type: reference
---

# Recipes

Sub-doc of [shadergraph-design](./SKILL.md). Every recipe below is intentionally limited to the current supported node subset.

## 1. BaseColor Texture Sample Chain

Use when:
- You need albedo/base color sampling with UV transform

Chain:
- `PropertyNode` (`Texture2D`)
- `SamplerStateNode` if sampling state should be explicit
- `UVNode`
- `TilingAndOffsetNode`
- `SampleTexture2DNode`

Guidance:
- Keep UV transform upstream of sampling.
- If only one texture uses the transformed UV, localize the chain; if many use it, consider a SubGraph later.

## 2. Normal Map Decode Chain

Use when:
- You have a tangent-space normal texture and want decoded normal output

Chain:
- `PropertyNode` (`Texture2D`)
- `UVNode`
- `TilingAndOffsetNode` if needed
- `SampleTexture2DNode`
- `NormalUnpackNode`
- `NormalStrengthNode`

Guidance:
- Do not replace `NormalUnpackNode` with generic math in advice. The intent is clearer and matches actual shadergraph semantics.

## 3. Masked Lerp Blend

Use when:
- One value blends between A and B using a grayscale mask

Chain:
- `PropertyNode` or constant nodes for A/B
- `PropertyNode` or sampled texture for mask
- `LerpNode`

Optional helpers:
- `SaturateNode` before `LerpNode.t`
- `OneMinusNode` if the mask needs inversion

## 4. UV Tiling/Offset Chain

Use when:
- A sampled texture needs user-controlled scale/offset

Chain:
- `UVNode`
- `PropertyNode` / `Vector2Node` for tiling
- `PropertyNode` / `Vector2Node` for offset
- `TilingAndOffsetNode`

Guidance:
- Prefer blackboard properties if artists are expected to tweak these values from materials.

## 5. Constant-Driven Color/Intensity

Use when:
- A graph needs a simple tunable tint or scalar multiplier

Chain:
- `ColorNode` or `PropertyNode`
- `Vector1Node` or float property
- `MultiplyNode`

Guidance:
- If the value should appear in materials, use a property.
- If it is only a local helper constant, use a constant node.

## 6. ViewDirection Edge / Fresnel-Style Base Chain

Use when:
- You want a lightweight camera-facing rim-style factor within the current subset

Base chain:
- `NormalVectorNode`
- `ViewDirectionNode`
- `Dot` is not in the current whitelist, so the full classic Fresnel chain is not fully editable by Stage 2 skills

Advice rule:
- You may discuss the concept, but you must say the current editable subset cannot build the full canonical Fresnel chain yet because key nodes such as `Dot Product` are outside scope.
- Do not pretend the current skills can finish this graph automatically.

## 7. Branch Toggle Chain

Use when:
- A boolean needs to choose one of two values

Chain:
- `BooleanNode` or boolean property
- `BranchNode`
- Optional upstream math on true/false paths

Guidance:
- Call out cost explicitly. `BranchNode` is not a free readability primitive; it can become a runtime branch depending on compilation and usage.
