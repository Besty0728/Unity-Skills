---
name: unity-shadergraph-review
description: "Shader Graph review checklist — vetting a graph proposal or AI-generated editing plan for topology, blackboard inputs, and node/slot id correctness. Shader Graph 审查清单(核查图方案或 AI 生成的编辑计划:拓扑、黑板输入、节点/槽位 id 正确性)。"
type: reference
---

# Review Checklist

Sub-doc of [shadergraph-design](./SKILL.md). Use this when reviewing a Shader Graph proposal or an AI-generated editing plan.

## Topology

- Does the plan clearly separate blackboard inputs from local constants?
- Are node ids / slot ids expected to come from `shadergraph_get_structure`, not guessed names?
- Is the graph chain actually expressible with the current supported node subset?

## Blackboard

- Are material-facing values represented as graph properties instead of buried constants?
- If `PropertyNode` is used, does the plan explicitly require the property to exist first?
- Are keywords justified, or are they being added without a real variant reason?

## Dataflow

- Are UV transforms upstream of texture sampling?
- Are channel split/combine steps only used when necessary?
- Is normal workflow using `NormalUnpackNode` / `NormalStrengthNode` instead of vague generic math guidance?

## Cost / Maintainability

- Does the plan warn about `BranchNode` cost when branches are introduced?
- Is SubGraph usage justified by reuse or API clarity, rather than aesthetic over-modularization?
- Are any claims about SRP Batcher, variants, or pipeline behavior kept narrowly scoped and evidence-based?

## Version Safety

- Does the advice avoid assuming Unity 2022.3 has graph templates?
- Does it stay inside the cross-version node overlap?
- Does it avoid unsupported areas such as Master Stack edits, Target edits, or SubGraph output restructuring?

## Reject The Plan If

- It invents node or slot ids
- It recommends unsupported nodes as if current skills can build them
- It tells the agent to edit output structure, target setup, or arbitrary internal fields
- It treats stale memory as more trustworthy than the live graph structure or validated source paths
