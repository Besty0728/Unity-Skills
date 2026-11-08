---
name: unity-adr
description: Record Unity architecture decisions (ADR) with rationale
---

# Unity ADR

Documentation only: no REST skills. Use when architecture choices may be revisited later or several plausible options exist.

## When to use
- Choosing between technical approaches
- Comparing libraries or patterns
- Documenting design rationale

## Output format
- Decision
- Context
- Options considered
- Chosen option
- Why this option won
- Consequences
- Revisit triggers

## Example use cases
- Coroutine vs UniTask
- Direct reference vs event-driven communication
- ScriptableObject config vs in-scene authoring
- One assembly vs multiple `asmdef`
- Runtime logic in `MonoBehaviour` vs pure C# service

## Pitfalls
- Keep ADRs short.
- Record only decisions that materially affect code generation or architecture direction.
