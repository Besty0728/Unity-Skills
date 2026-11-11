# Unity Testability Advisor

Documentation only: no REST skills. Use when deciding what logic stays in Unity-facing classes and what moves into pure C#.

## When to use
- Improving testability
- Extracting logic for unit tests
- Planning a test strategy

## Review questions
- Can the rule or algorithm run without `Transform`, `GameObject`, or scene state?
- Can config be injected instead of read through static globals?
- Can runtime decisions move to a plain C# class called from a thin `MonoBehaviour`?
- Does this need PlayMode coverage, or is EditMode enough?

## Output format
- Logic that should move to pure C#
- Logic that should stay Unity-facing
- Suggested seams/interfaces
- Candidate EditMode tests
- Candidate PlayMode tests

## Pitfalls
- Do not force test seams everywhere if the script is tiny and scene-bound.
- Prefer a few meaningful seams over abstraction for its own sake.
