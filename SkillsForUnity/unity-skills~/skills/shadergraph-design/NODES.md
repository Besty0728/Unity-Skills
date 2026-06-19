---
name: unity-shadergraph-nodes
description: "Supported Shader Graph node subset validated on Unity 2022.3 (SG14) and Unity 6 — the editable nodes the skills can reliably execute. Shader Graph 支持的可编辑节点子集(在 Unity 2022.3 SG14 与 Unity 6 验证:技能可可靠执行的节点)。"
type: reference
---

# Supported Nodes

Sub-doc of [shadergraph-design](./SKILL.md). Only recommend recipes that stay inside this set if you expect the current skills to execute them.

## Editable Subset

Validated in both Unity 2022.3 / SG14 and Unity 6 current Graphics:

- `PropertyNode`
- `BooleanNode`
- `ColorNode`
- `Vector1Node`
- `Vector2Node`
- `Vector3Node`
- `Vector4Node`
- `SampleTexture2DNode`
- `SamplerStateNode`
- `UVNode`
- `TilingAndOffsetNode`
- `SplitNode`
- `CombineNode`
- `AddNode`
- `SubtractNode`
- `MultiplyNode`
- `DivideNode`
- `LerpNode`
- `OneMinusNode`
- `SaturateNode`
- `ClampNode`
- `RemapNode`
- `BranchNode`
- `NormalUnpackNode`
- `NormalStrengthNode`
- `PositionNode`
- `NormalVectorNode`
- `ViewDirectionNode`

Unity 6 only in current live validation:

- `AppendVectorNode`

## Whitelisted Settings

Only these fields are writable through Stage 2:

| Node | Writable settings |
|------|-------------------|
| `PropertyNode` | `propertyReferenceName` |
| `BooleanNode` | `value` |
| `ColorNode` | `value` |
| `Vector1Node` | `value` |
| `Vector2Node` | `value` |
| `Vector3Node` | `value` |
| `Vector4Node` | `value` |
| `UVNode` | `channel` |
| `PositionNode` | `space` |
| `NormalVectorNode` | `space` |
| `ViewDirectionNode` | `space` |

Other nodes support add/move/connect/disconnect/remove only.

## Design Reading Of The Subset

- Use `PropertyNode` for blackboard-driven graphs. It keeps graph inputs explicit and portable.
- Use constant nodes (`BooleanNode`, `ColorNode`, `Vector1-4Node`) when the value is authoring-time local, not graph API surface.
- Use `UVNode` + `TilingAndOffsetNode` for basic texture address flow.
- Use `SampleTexture2DNode` + `SamplerStateNode` when sampling behavior needs to be explicit.
- Use `SplitNode` and `CombineNode` for channel packing/unpacking in graphs that must stay portable across Unity 2022.3 and Unity 6.
- Use `AppendVectorNode` for the same job only when you explicitly accept a Unity 6-only graph path.
- Use arithmetic nodes for small composable chains instead of branching too early.
- Use `BranchNode` only when the semantic gain is worth the runtime branch cost.
- Use `NormalUnpackNode` and `NormalStrengthNode` for normal workflows; do not fake it with generic math when the intent is actually "normal map decode".
- Use `PositionNode`, `NormalVectorNode`, `ViewDirectionNode` only after choosing the right space explicitly.

## What Not To Recommend As Editable Today

- Target changes
- Context / Block edits
- Master Stack restructuring
- SubGraph output slot restructuring
- Arbitrary internal fields on unsupported nodes
