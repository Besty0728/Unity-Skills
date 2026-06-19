---
name: unity-adr
description: Helps record Unity architecture decisions (ADR) — compare options, weigh tradeoffs, and lock in a chosen approach with rationale. Use when choosing between technical approaches, comparing libraries or patterns, or documenting why a design decision was made, even if the user just asks "选哪个" or "用哪个方案好". 帮助记录 Unity 架构决策(ADR:技术选型、方案对比、权衡优缺点、固化决策与理由);当用户要在多个技术方案间抉择、对比库或模式、或记录某个设计决策的来龙去脉时使用。
---

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
