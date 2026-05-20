---
name: unity-testability
description: "Unity testability advisor. Use when users want to improve testability, isolate logic from MonoBehaviour, or plan EditMode/PlayMode tests. Triggers: testability, unit test, how to test, write tests, editmode test, playmode test, isolate logic, mock, 怎么测试, 写测试, 可测试, 测试性, 单元测试, 逻辑分离."
---

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

**Mode**: Documentation only — guidance for AI under all three operating modes; no REST skills to gate.

- Do not force test seams everywhere if the script is tiny and scene-bound.
- Prefer a few meaningful seams over abstraction for its own sake.
