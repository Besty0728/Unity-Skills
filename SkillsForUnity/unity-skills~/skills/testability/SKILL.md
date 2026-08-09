---
name: unity-testability
description: Advise on Unity testability. 为 Unity 可测试性提供建议。
---

## Triggers
- Improving testability
- Extracting logic for unit tests
- Planning a test strategy
- 提升可测试性、为单元测试抽取逻辑、规划测试策略

# Unity Testability Advisor

Use this skill when deciding what logic should remain in Unity-facing classes and what should move into pure C# code.

## Review Questions

- Can the rule/algorithm run without `Transform`, `GameObject`, or scene state?
- Can config be injected instead of read through static globals?
- Can runtime decisions be moved to a plain C# class and called from a thin `MonoBehaviour`?
- Does this need PlayMode coverage, or is EditMode enough?

## Output Format

- Logic that should move to pure C#
- Logic that should stay Unity-facing
- Suggested seams/interfaces
- Candidate EditMode tests
- Candidate PlayMode tests

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not force test seams everywhere if the script is tiny and scene-bound.
- Prefer a few meaningful seams over abstraction for its own sake.
