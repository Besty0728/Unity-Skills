---
name: unity-asmdef
description: Advise on Unity assembly definitions (asmdef)
---

# Unity asmdef Advisor

Documentation only: no REST skills. Use when the project is large enough that compile boundaries and dependency direction matter.

## When to use
- Planning asmdef layout
- Untangling assembly dependencies
- Speeding up compilation
- Splitting editor, runtime and test code

## Recommend only when worth it
`asmdef` is usually worth discussing when the project has multiple domains, editor and runtime code are mixed, compile times are noticeable, or tests should be isolated.

## Output format
- Whether `asmdef` is justified now
- Proposed assemblies
- Allowed dependency direction
- Editor/runtime/test split
- Migration steps
- Risks or churn to avoid

## Default guidance
- Prefer a few meaningful assemblies over many tiny ones.
- Split editor code from runtime first.
- Keep the dependency graph directional and shallow.

## Pitfalls
- Do not fragment assemblies for a tiny prototype.
- Do not create circular dependencies or force everything through a shared dumping-ground assembly.
