---
name: unity-adr
description: Record Unity architecture decisions (ADR) with rationale. 记录 Unity 架构决策(ADR)与理由。
---

## Triggers
- Choosing between technical approaches
- Comparing libraries or patterns
- Documenting design rationale
- 技术方案选型、库/模式对比、记录设计决策来龙去脉

# Unity ADR

Use this when architecture choices may be revisited later or when multiple plausible options exist.

## Output Format

- Decision
- Context
- Options considered
- Chosen option
- Why this option won
- Consequences
- Revisit triggers

## Example Use Cases

- Coroutine vs UniTask
- Direct reference vs event-driven communication
- ScriptableObject config vs in-scene authoring
- One assembly vs multiple `asmdef`
- Runtime logic in `MonoBehaviour` vs pure C# service

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Keep ADRs short.
- Record only decisions that materially affect code generation or architecture direction.
