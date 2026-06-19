---
name: unity-inspector
description: Advises on Unity Inspector authoring UX — SerializeField usage, Tooltip/Header organization, validation, and custom Inspector display. Use when designing how a component appears in the Inspector, organizing serialized fields, adding tooltips/headers, or improving authoring UX, even if the user just says "Inspector怎么设计" or "字段怎么显示". 为 Unity Inspector 编写体验提供建议(SerializeField 用法、Tooltip/Header 组织、校验、自定义 Inspector 显示);当用户要设计组件在 Inspector 的呈现、组织序列化字段、添加提示/分组或改善编辑体验时使用。
---

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
