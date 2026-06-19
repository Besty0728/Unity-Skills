---
name: unity-shadergraph-versions
description: "Shader Graph version matrix — the validated Unity/ShaderGraph versions, kept to stop version hallucination. Shader Graph 版本矩阵(已验证的 Unity/ShaderGraph 版本,用于杜绝版本幻觉)。"
type: reference
---

# Version Matrix

Sub-doc of [shadergraph-design](./SKILL.md). This file exists to stop version hallucination.

## Validated Versions

Current live validation environments used by this repo:

- `localhost:8090` = Unity 6
- `localhost:8091` = Unity 2022.3
- All portability claims below are constrained by these two real environments plus the checked source trees

| Area | Unity 2022.3 / ShaderGraph 14.0.12 | Unity 6 / current Graphics |
|------|------------------------------------|----------------------------|
| `GraphData.AddNode / RemoveNode / Connect / RemoveEdge` | Present | Present |
| `AbstractMaterialNode.drawState.position` | Present | Present |
| `AbstractMaterialNode.GetInputSlots/GetOutputSlots` | Present | Present |
| `SlotReference(node, slotId)` shape | Present | Present |
| Current 28-node shared editing subset | Present | Present |
| `AppendVectorNode` | Not exposed by the installed SG14 package | Present |
| `GraphTemplates/` package directory | Commonly absent | May exist |
| Current skill strategy | Blank fallback + reflection write | Template copy when available + reflection write |

## Concrete Differences That Matter

### 1. Template availability differs

- Unity 2022.3 package `com.unity.shadergraph@14.0.12` often does not expose `GraphTemplates/`.
- Unity 6 package under Graphics may include package templates.
- Consequence: creation advice must not assume named templates are portable. Query first; otherwise fall back to blank graph.

### 2. Editing primitives are stable enough across both versions

The current implementation depends on these shared editor types:
- `GraphData`
- `AbstractMaterialNode`
- `SlotReference`

These are the reason Stage 2 can support low-level node operations on both versions without hard-linking to internal assemblies.

### 3. Guidance must stay inside the cross-version overlap

The current shared node subset is intentionally limited to nodes confirmed in both runtime environments. `AppendVectorNode` exists in the Unity 6 Graphics source but is not exposed by the installed 2022.3 ShaderGraph 14 package used in live validation. If a proposal needs it, say the graph becomes version-specific.

## Practical Portability Rules

- Prefer simple arithmetic/dataflow chains if you expect the graph to move between Unity 2022.3 and Unity 6.
- Keep sampler logic explicit with `SamplerStateNode` when you care about deterministic authoring.
- Keep blackboard property references stable. `PropertyNode` binding depends on the actual graph property reference name.
- Treat template-dependent setup as optional. Treat node/slot structure returned by `shadergraph_get_structure` as authoritative.
