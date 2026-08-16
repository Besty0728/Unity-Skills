---
name: unity-skills-guide
description: Guidance mode for unity-skills. Teach manual Unity Editor steps instead of calling REST skills. Use when /health returns guideMode:true and the user wants simple one-off Editor actions (create one object, tweak one color, add one component, etc.) that are faster done by hand than through automation. The root SKILL.md points here when guideMode is enabled.
---

# Unity Skills — Guidance Mode

This document is the active protocol when `GET /health` reports `guideMode:true`. In this mode the AI acts as an **instructor**, not an operator: it explains how to perform a task and gives precise manual steps in the Unity Editor. It does **not** initiate write-style REST skill calls unless the user explicitly asks for automation or the task clearly exceeds the boundaries below.

## When to guide vs. when to automate

Use this rule for anything not explicitly listed in the task boundary table:

**If the user can finish it in the Editor with ≤3 clicks/drags → document guidance; if it needs traversal, search, batch, exact numeric values, or consistency across many objects → automation.**

| Guidance (do not call REST) | Automation (fall back to SKILL.md) |
|---|---|
| Create 1 Cube / Sphere / empty GameObject | Create 5+ objects or hierarchies |
| Move / rotate / scale 1–2 objects | Batch transform adjustments |
| Toggle active, change a single tag / layer | Rule-based batch tag / layer changes |
| Change one material color | Batch material replacement / property-driven changes |
| Add a single common component | Programmatic component configuration / reflection-driven properties |
| Drag 1 Prefab instance into the scene | Prefab variant / nested editing, batch apply overrides |
| Explain simple scene browsing | Complex analysis, compile checks, test runs |

## How to give guidance

Present steps in this order and format:

1. **Menu path** — write it in **English Unity UI** as the canonical form (for example `GameObject > 3D Object > Cube`). The AI should restate the path in the user's language and in the same language as their Unity Editor UI.
2. **Inspector field names** — use the canonical English labels (for example `Transform > Position`, `Mesh Renderer > Materials`). If the user's Editor is localized, restate the localized label.
3. **Keyboard shortcuts** — give the default English-layout shortcut (for example `W` for Move, `Ctrl+D` for duplicate). Mention that localized keyboards may differ.

Route common topics to the corresponding manual advisories:

- Creating and organizing GameObjects / hierarchy / basic transforms → [`skills/manual-gameobject/SKILL.md`](skills/manual-gameobject/SKILL.md)
- Adding and configuring components in the Inspector → [`skills/manual-component/SKILL.md`](skills/manual-component/SKILL.md)
- Creating and editing materials / changing colors → [`skills/manual-material/SKILL.md`](skills/manual-material/SKILL.md)
- Navigating scenes, saving, and simple scene operations → [`skills/manual-scene/SKILL.md`](skills/manual-scene/SKILL.md)

Read-only skills such as `scene_get_hierarchy` may be used to help explain what is already in the scene. Under the default Approval operating mode, even read-only skills can require a grant before they execute; mention this if you invoke one.

## Fallback protocol

If the user explicitly asks for automation, or the task clearly falls outside the guidance boundary table, stop using this document and switch back to the full automation protocol:

**[SKILL.md](SKILL.md)**

## Anti-hallucination rules

| Situation | Rule |
|---|---|
| Parameters are uncertain | **dryRun first** — never guess parameters from a skill name or description. |
| A skill name does not appear in schema results | Do not invent it. Trust only the names returned by `/skills/recommend`, `/skills?brief=1`, or `/skills/schema`. |
| A skill call fails | Read the error response's `suggestedFixes`. If it points to a module document, read that document before retrying. |
