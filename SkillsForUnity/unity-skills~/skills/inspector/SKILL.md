---
name: unity-inspector
description: Advise on Unity Inspector authoring UX. 为 Unity Inspector 编写体验提供建议。
---

## Triggers
- Designing component Inspector display
- Organizing serialized fields
- Adding tooltips/headers
- 设计组件在 Inspector 的呈现、组织序列化字段、添加提示/分组

# Unity Inspector Design

Use this skill when scripts need to be easier to author, configure, and review in the Inspector.

## Guardrails

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).

- Prefer `[SerializeField] private` over unnecessary public fields.
- Do not over-decorate with attributes when simple naming suffices.

## Default Rules

- Use `[Header]`, `[Tooltip]`, `[Space]`, `[Range]`, `[Min]`, `[TextArea]` when they clarify authoring intent.
- Use `[RequireComponent]` for mandatory sibling dependencies.
- Use `[CreateAssetMenu]` for config/data assets that designers should create directly.
- Use `OnValidate` only for lightweight editor-time validation and normalization.
- Use `SerializeReference` only when polymorphic serialized data is genuinely needed.

## Inspector Quality Checklist

- Are defaults safe?
- Are required references obvious?
- Are fields grouped by responsibility?
- Are tuning values constrained?
- Are debug-only fields separated from authoring fields?
- Will another person understand this script from the Inspector alone?

## Output Format

- Field exposure strategy
- Recommended attributes
- Validation rules
- Authoring UX improvements
- Over-design to avoid
