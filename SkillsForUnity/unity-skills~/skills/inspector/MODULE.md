# Unity Inspector Design

Documentation only: no REST skills. Use when scripts must be easier to author, configure and review in the Inspector.

## When to use
- Designing a component's Inspector display
- Organizing serialized fields
- Adding tooltips or headers

## Default rules
- Prefer `[SerializeField] private` over public fields.
- Use `[Header]`, `[Tooltip]`, `[Space]`, `[Range]`, `[Min]`, `[TextArea]` when they clarify authoring intent.
- Use `[RequireComponent]` for mandatory sibling dependencies.
- Use `[CreateAssetMenu]` for config/data assets designers create directly.
- Use `OnValidate` only for lightweight editor-time validation and normalization.
- Use `SerializeReference` only when polymorphic serialized data is genuinely needed.
- Do not over-decorate when simple naming suffices.

## Inspector quality checklist
- Are defaults safe?
- Are required references obvious?
- Are fields grouped by responsibility?
- Are tuning values constrained?
- Are debug-only fields separated from authoring fields?
- Will another person understand the script from the Inspector alone?

## Output format
- Field exposure strategy
- Recommended attributes
- Validation rules
- Authoring UX improvements
- Over-design to avoid
