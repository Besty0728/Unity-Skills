---
name: unity-asmdef
description: Advise on Unity assembly definitions (asmdef)
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Planning asmdef layout
- Untangling assembly dependencies
- Speeding up compilation
- Splitting editor/runtime/test code
- 规划 asmdef 结构、理顺程序集依赖、加速编译、拆分编辑器/运行时代码

# Unity asmdef Advisor

Use this skill when the project is large enough that compile boundaries and dependency direction matter.

## Recommend Only When Worth It

`asmdef` is usually worth discussing when:

- the project has multiple domains/systems
- editor code and runtime code are mixed
- compile times are becoming noticeable
- tests should be isolated cleanly

## Output Format

- Whether `asmdef` is justified now
- Proposed assemblies
- Allowed dependency direction
- Editor/runtime/test split
- Migration steps
- Risks or churn to avoid

## Default Guidance

- Prefer a few meaningful assemblies over many tiny ones.
- Split editor code from runtime first.
- Keep the dependency graph directional and shallow.

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Do not introduce `asmdef` fragmentation for a tiny prototype.
- Do not create circular dependencies or force everything through a shared dumping-ground assembly.
