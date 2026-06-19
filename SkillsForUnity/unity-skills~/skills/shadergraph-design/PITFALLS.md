---
name: unity-shadergraph-pitfalls
description: "Shader Graph pitfalls — common bad advice to reject before it ships, such as misusing PropertyNode reference names. Shader Graph 陷阱(在落地前需否决的常见错误建议,如误用 PropertyNode 的引用名)。"
type: reference
---

# Pitfalls

Sub-doc of [shadergraph-design](./SKILL.md). Use this to reject bad Shader Graph advice before it ships.

## 1. PropertyNode Is Not "Just A String"

Wrong:
- "Create a PropertyNode and set any reference name later."

Right:
- Create the graph property first, then bind `PropertyNode.propertyReferenceName` to an existing blackboard reference.

Why:
- The actual node binds a runtime property object. The current implementation resolves that object from the graph's property collection before assigning it.

## 2. Do Not Guess Node Ids Or Slot Ids

Wrong:
- "The first Add input is probably slot 0."

Right:
- Read `shadergraph_get_structure` and use the returned `nodeId` / `slotId`.

Why:
- Stable editing depends on the serialized/runtime ids, not display labels.

## 3. Default Value And Connection Are Mutually Exclusive

Wrong:
- "Set the slot default and keep the connection."

Right:
- If an input slot is connected, disconnect first; then set the default.

Why:
- Stage 2 explicitly rejects writing defaults on connected input slots.

## 4. Do Not Over-Promise Template Availability

Wrong:
- "Use the built-in Unlit template everywhere."

Right:
- Query templates first. On Unity 2022.3, blank fallback is often the real path.

## 5. Sampler Behavior Should Be Explicit When It Matters

Wrong:
- Assume texture sample state is always implicit and equivalent across pipelines/projects.

Right:
- If the review depends on sampling state, call out `SamplerStateNode` explicitly.

## 6. Branches Need Cost Warnings

Wrong:
- Recommend `BranchNode` as a purely visual organization tool.

Right:
- Say that branch-based graphs can introduce runtime branching cost depending on compilation and usage.

## 7. SubGraphs Are Not Free

Wrong:
- Split everything into SubGraphs immediately.

Right:
- Use SubGraphs for reuse, stability, or clearer API surfaces. Avoid turning tiny one-off math chains into needless indirection.

## 8. SRP Batcher / Variant Advice Must Stay Scoped

Wrong:
- Promise that any Shader Graph tweak is automatically SRP Batcher-safe or variant-cheap.

Right:
- Keep claims narrow. Blackboard properties, keywords, sampler usage, and pipeline target behavior all affect the real outcome.

## 9. URP And HDRP Are Not The Same Advice Surface

Wrong:
- Treat every graph recommendation as pipeline-agnostic.

Right:
- Say when the graph is generic math/dataflow advice versus when final material/target behavior depends on URP or HDRP target setup outside Stage 2 editing scope.

## 10. Do Not Suggest Unsupported Nodes As If They Are Editable

Wrong:
- Recommend `Dot Product`, `Normalize`, `Time`, or custom function nodes as if the current skills can build them.

Right:
- If a graph concept needs them, say the concept is valid but outside the current editable subset.
