---
name: skill-creator
description: Guide for authoring user-defined custom UnitySkills skills inside your own Unity project - a [UnitySkill] C# method plus the conventions that let agents and the server discover and gate it
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Writing your own custom skill for your project
- Extending what agents can do in this specific Unity project
- Learning the [UnitySkill] metadata contract and discovery rules
- 为自己的工程编写自定义技能、扩展 Agent 在本工程可调用的能力

# Skill Creator (advisory)

**Audience**: an agent (or developer) adding a **custom skill to your own Unity project**, not to the UnitySkills plugin source. A custom skill is a `public static` method marked `[UnitySkill]` compiled inside your project's Editor code; the server discovers it automatically — you never edit the plugin.

## Where the C# must live

Put the file under `Assets/` in an Editor-only location (e.g. `Assets/Editor/UnitySkillsCustom/MySkills.cs`, or any asmdef whose platform is Editor). The plugin's editor asmdef has `autoReferenced: true`, so `Assembly-CSharp-Editor` and ordinary user Editor asmdefs see the public API without adding references.

- **Never** put custom skill code inside the package (`Packages/com.besty.unity-skills/`) or `Library/PackageCache/`: package updates and self-update replace that whole tree and your file is wiped.
- The class need not be a MonoBehaviour; a plain `static class` is fine.

## Compile and discovery loop

After you write the C# (through `script_create` with the full `content`, or by any editor), Unity recompiles and performs a domain reload. The router re-runs `TypeCache.GetMethodsWithAttribute<UnitySkillAttribute>()` on the next request, so the new skill is registered automatically — there is no restart, no list to update.

Recommended agent flow:
1. Check the name is free: `GET /skills?q=<prefix>` or read `GET /skills/schema?names=<cand>&wire=v2`.
2. `script_create { scriptName, folder: "Assets/...", content: <full source> }` — returns `{ path, jobId, waitUrl }`.
3. Wait for compile: `GET /jobs/<jobId>?wait=90` (retry through the reload); inspect `resultData.compilation.errors`. `script_get_compile_feedback(scriptPath)` gives on-demand diagnostics.
4. Confirm registration: `GET /skills/schema?names=<your_skill>&wire=v2` should return your skill with its description and parameters.
5. First real call with `?mode=dryRun`.

Note: `script_create` is `MayTriggerReload` + high-risk — it is **NeverInSemi**: it needs Bypass mode or a panel Allowlist entry; ask the user rather than attempting a grant.

## Naming rules (read this before choosing a name)

- Skill names are `snake_case` verbs like `gameobject_create`. A user custom skill **must be prefixed** (e.g. `my_`, `acme_`): `my_apply_team_branding`, not `apply_branding`.
- On a name collision the **later-registered** declaration wins, and registration order is not guaranteed — a name that matches a built-in can shadow it (or be shadowed). The registry logs a warning naming both declaring types (`Skill name conflict: '<name>' declared by both <A> and <B>; using the latter.`). Always check the name is free first.
- Provide a matching `xxx_batch` variant when per-item looping makes sense.

## The metadata contract (the attribute)

Fill `[UnitySkill]` truthfully — the router, permission gating, schema, and agents all read it:

```csharp
[UnitySkill("my_greet",
    "First sentence is what the agent reads; mention parameters.",
    Category = SkillCategory.Uncategorized,                 // user skills cannot add enum values
    Operation = SkillOperation.Read,                        // Create | Read | Update | Delete (combine with |)
    Tags = new[] { "greeting", "demo" },                    // improves /skills/recommend matching
    Outputs = new[] { "message" },                          // MUST be a subset of what you actually return
    TracksWorkflow = false, MutatesScene = false,           // honest risk flags (see below)
    RiskLevel = "low")]                                     // low | medium | high
public static object MyGreet(
    [SkillParam("Who to greet")] string name = "world") { ... }
```

- **Category**: `SkillCategory` is a closed enum — custom skills cannot add values. Use the nearest existing category or `Uncategorized`. A skill in GameObject/Component/Material/Scene/Sample is hidden under the **Guide** surface profile unless it is a pure read; any writer with `MutatesScene = true` is hidden under **NoSceneAuthoring**. This is intentional: honest metadata, not a loophole to dodge.
- **Risk flags drive permission**: a skill declaring `Operation.Delete`, `MayEnterPlayMode`, `MayTriggerReload`, or `RiskLevel = "high"` is auto-classified **NeverInSemi** — it returns `MODE_FORBIDDEN` under Approval and Auto and only runs in Bypass or via the panel Allowlist. `Mode` defaults to `FullAuto`; declare `Mode = SkillMode.SemiAuto` **only** for a pure read with zero side effects. Fill these truthfully or your skill is gated wrong.
- **Outputs**: every entry must be a key the method actually returns; the schema contract tests check this.

## Writing the method body (public API only)

Only the **public** surface is available to project code. Use:

- `Validate.Required(x, "x")` — returns an error object to return early: `if (Validate.Required(x, "x") is object err) return err;`
- `GameObjectFinder.FindOrError(name, instanceId, path)` — never `GameObject.Find`.
- `SkillErrorResponse.Build(SkillErrorCode.X, message, ...)` for business errors (do `throw` only for truly unknown framework errors). `SkillErrorCodeExtensions` is internal — do not try to call it.
- Writers that mutate scene state should register `Undo` and call `WorkflowManager.SnapshotXxx(...)` when `TracksWorkflow = true`; batch with `BatchExecutor.Execute<TItem>(items, perItem, idFn)`.
- Return a structured object (anonymous or a DTO) — it is serialized with `SkillsCommon.JsonSettings`.

A complete minimal example:

```csharp
using UnitySkills;

public static class MyCustomSkills
{
    [UnitySkill("my_greet", "Return a greeting for a name.",
        Operation = SkillOperation.Read,
        Tags = new[] { "greeting" }, Outputs = new[] { "message" },
        MutatesScene = false, RiskLevel = "low")]
    public static object MyGreet([SkillParam("Who to greet")] string name = "world")
    {
        if (Validate.Required(name, "name") is object err) return err;
        return new { message = $"Hello, {name}!" };
    }
}
```

## How agents find it (no doc file required)

Agents do not read a markdown file to call your skill — they discover it through the same schema-first path used for built-ins:

- `GET /skills/recommend?intent=<words>&includeSchema=true&topN=3&wire=v2` — matches on name, Tags, and Description.
- `GET /skills/schema?names=my_greet&wire=v2` — full parameter schema (with each `[SkillParam]` note) when the name is known.

So the quality of `Description`, `Tags`, and every `[SkillParam]` **is** the documentation. Keep the first description sentence specific and parameter-aware.

## Optional long-form docs (where NOT to put them)

If a custom skill needs prose beyond the schema, put markdown in a file **inside your project** (e.g. `Assets/UnitySkillsCustom/README.md`) and point to it from the skill's Description, or as a sibling agent-skill folder **outside** the managed install tree (e.g. `<project>/.claude/skills/my-tool/SKILL.md`).

- **Never** write docs inside the installed `unity-skills` skill folder (e.g. `.claude/skills/unity-skills/skills/my-mod/SKILL.md`): install/update/auto-sync overwrites that tree, and **Uninstall deletes the whole install directory**, destroying anything inside it.

## Guardrails

- Do not add `SkillCategory` enum values, `SkillRouter` entries, or route-table rows — discovery is automatic.
- Do not reference `internal` plugin members (only public API compiles from your project).
- Do not shadow built-in names; always prefix.
- Do not put C# or docs inside the managed package or install tree (update/uninstall removes them).
- Do not fudge `MutatesScene` / risk flags to bypass gating; the surface profiles and permission modes depend on them being true.

> **Mode**: Documentation only — no REST skills to gate; load freely under any operating mode (Approval / Auto / Bypass).
