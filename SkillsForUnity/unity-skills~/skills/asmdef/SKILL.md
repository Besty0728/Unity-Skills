---
name: unity-asmdef
description: Advises on Unity assembly definitions (asmdef) — module boundaries, dependency graphs, editor/runtime/test splits, and faster compile times. Use when planning asmdef layout, untangling assembly dependencies, speeding up compilation, or splitting editor and runtime code, even if the user just says "编译太慢" or "程序集怎么分". 为 Unity 程序集定义(asmdef)提供建议(模块边界、依赖关系、editor/runtime/test 拆分、加快编译);当用户要规划 asmdef 结构、理顺程序集依赖、加速编译或拆分编辑器与运行时代码时使用。
---

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
