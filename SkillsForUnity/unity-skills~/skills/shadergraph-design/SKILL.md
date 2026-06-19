---
name: unity-shadergraph-design
description: Source-anchored Shader Graph design rules for Unity 2022.3 — graph structure, node subset, SubGraph boundaries, master stack, blackboard/keywords, recipes, and review. Use when building or reviewing Shader Graphs, structuring node chains or SubGraphs, laying out blackboard/keywords, or following URP/HDRP graph recipes, even if the user just says "连个shader graph" or "着色器节点". 为 Unity 2022.3 的 Shader Graph 提供源码锚定的设计规则(图结构、节点子集、SubGraph 边界、主节点堆栈、黑板/关键字、配方、审查);当用户要构建或审查 Shader Graph、组织节点链或 SubGraph、布置黑板/关键字、或参考 URP/HDRP 图配方时使用。
---

# ShaderGraph - Design Rules

Advisory module. Read this before giving Shader Graph guidance. The goal is to keep recommendations anchored to actual package/source behavior, not stale model memory.

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

## Source Scope

Validated against:
- Unity 2022.3 package source: `E:/CodeSpace/temp/shadergraph/com.unity.shadergraph@14.0.12`
- Unity 6 Graphics source: `E:/CodeSpace/temp/Graphics/Packages/com.unity.shadergraph`
- Runtime/editor behavior in this repo's ShaderGraph skills and dual-version test environments

Core anchors:
- `Editor/Data/Graphs/GraphData.cs`
- `Editor/Data/Nodes/AbstractMaterialNode.cs`
- `Editor/Data/Interfaces/Graph/SlotReference.cs`
- Specific node files under `Editor/Data/Nodes/...`

## When To Load

Load before:
- Designing a new Graph or SubGraph architecture
- Reviewing a proposed Shader Graph node chain
- Advising on blackboard properties, keywords, samplers, or SubGraph boundaries
- Suggesting changes to graphs through the constrained `shadergraph_*` node editing skills

## What This Module Assumes

- Graph editing is limited to the current safe node whitelist exposed by `shadergraph_list_supported_nodes`
- Guidance must stay inside what Unity 2022.3 and Unity 6 both support
- `shadergraph_get_structure` is the fact source for current node ids, slot ids, and live topology
- The practical overlap is 28 nodes across both versions; `AppendVectorNode` is currently Unity 6 only in live validation

## Sub-doc Routing

| Sub-doc | Read when |
|--------|-----------|
| [VERSIONS.md](./VERSIONS.md) | You need version differences or portability rules |
| [NODES.md](./NODES.md) | You need the supported node subset and editable fields |
| [RECIPES.md](./RECIPES.md) | You need patterns that the current skill subset can actually build |
| [PITFALLS.md](./PITFALLS.md) | You are reviewing a graph or suspect bad advice / hidden costs |
| [REVIEW.md](./REVIEW.md) | You want a checklist for judging a Shader Graph plan |

## Hard Rules

- Do not recommend nodes outside the current whitelist unless you clearly say the current skills cannot build them.
- Do not talk about "editing by node name"; the implementation uses serialized `nodeId` and `slotId`.
- Do not assume Unity 2022.3 has package graph templates. It commonly does not.
- Do not tell the agent to mutate Master Stack, Target, Context, Block, or SubGraph output structure. Stage 2 does not support that.
- For `PropertyNode`, create or verify the blackboard property first; the node binds a real property object, not just a string.
- Prefer small SubGraphs when reuse or porting matters, but keep them within the currently supported node subset if you expect the skills to edit them later.

When in doubt, cite the relevant source path or ask the runtime graph for structure first.
