---
name: unity-skills
description: Automate the Unity Editor through a local REST API — create and edit scripts, build scenes and prefabs, manage assets/materials/lighting, run tests, and drive hundreds of Editor operations across modules. Use when the user wants to actually operate the Unity Editor from chat — create or modify GameObjects/scripts/scenes/assets, batch-edit, or run Editor automation — in any language. Not needed for conceptual Unity Q&A that touches no Editor state — read the matching advisory doc under skills/ instead. 当用户要从对话里实际操作 Unity 编辑器（创建/修改/批量编辑/运行测试）时使用，任何语言均可触发；纯概念问答无需本协议。
compatibility: Requires Unity Editor 2022.3+/6000.x with the UnitySkills package (local REST server on localhost:8090-8100); Python 3 for the bundled client
---

# Unity Skills

> Module and reference docs are English-only. Match requests to modules by meaning in any language, and always reply in the user's language.

## Route first: automate, or guide the user?

- **Automate** — you call REST skills and drive the Editor yourself. Stay here when the user asked you to do it, or the task needs traversal, search, batch, exact numeric values, or consistency across many objects.
- **Guide** — you name the menus and Inspector fields and write nothing through REST. Switch to [SKILL_GUIDE.md](SKILL_GUIDE.md) (guidance boundary table + `manual-*` routing) when the user asked how to do it themselves, **or** `/health` reports a `surfaceProfile` other than `full`.

## First Contact Checklist

Before the first skill call in a session:

1. **`GET /health`** — discover the server (ports `8090`–`8100`); read `currentMode` (`"approval"` / `"auto"` / `"bypass"`), `panelApprovalRequired`, `pendingCount`, and `surfaceProfile` (`full` / `guide` / `noSceneAuthoring`). `surfaceProfileHint` is text only when the profile is not `full`, else null.
2. **Branch on `currentMode`**: under `approval` the first write to any `FullAuto` skill returns `MODE_RESTRICTED` and needs a grant; under `auto`/`bypass` writes execute directly (self-assess risk under `auto`). Grants are single-shot; the permanent form is the user-managed Allowlist. Gates, grant protocol and mode table → [operating mode](references/protocol-operating-mode.md).
3. **`GET /skills/meta`** — once per session: the constants shared by every skill (`categories`, `operationTypes`, `reservedBodyParameters`, `workflowTrackedSkills`, `schemaVersion`, `defaults`). Read them here, not out of each manifest, then start discovery.

## Schema: pick the cheapest layer

Server-cached (ETag/304), off the main thread; send `Accept-Encoding: gzip`.

| Layer | Endpoint | Size | Use when |
|---|---|---|---|
| **Default: start here** | `GET /skills/recommend?intent=...&includeSchema=true` | 4–14 KB | One intent; scored candidates + schemas. `topN` caps how many (default 10), `wire=v2` about halves it. |
| directory + category | `GET /skills` then `GET /skills/schema?category=<Category>` | ~19 KB + 13–44 KB | Touches one or two areas. |
| summary | `GET /skills?summary=1` | ~143 KB | Exploratory / cross-module, or cheaper layers left you unsure. |
| full schema | `GET /skills/schema` | ~618 KB | Rare; many modules' signatures at once. |

Bare `GET /skills` is the **brief directory** — names by category only; the old full listing needs `?full=1`.

Module docs and the scoped schema are complements: schema has exact signatures; the module doc has guardrails, return shapes and traps schema omits. On an unfamiliar module read both.

## Wire format v2 (`?wire=v2`)

`?wire=v2` slims per-skill entries on `?full=1`, `/skills/schema` (full or scoped), a filtered `/skills`, and recommend — never bare `GET /skills` (always the brief directory). v1 is default:

- A **`flags`** array replaces v1's six booleans and adds v2-only `longRunning`: `readOnly`, `tracksWorkflow`, `mutatesScene`, `mutatesAssets`, `mayTriggerReload`, `mayEnterPlayMode`, `longRunning`. An absent flag is false.
- **Omitted means default**: `riskLevel` appears only when not `"low"`, `supportsDryRun` only when `false`, null members are dropped — an absent key never reads as null.
- Every v2 response carries a **`defaults`** block — take the rule from the payload, don't memorize it.

## Execute: batch, dryRun gate, anti-hallucination rules

**`POST /skills/batch`** — up to 50 steps per call (`{"steps":[{"skill","args"}],"continueOnError":false}`; `?mode=dryRun` validates every step): the largest round-trip saving available → [batch](skills/batch/SKILL.md).

Before executing any skill whose exact parameters you don't already hold, dryRun it: `POST /skill/<name>?mode=dryRun`. Iterate until `valid: true`, then execute without `?mode=dryRun`. `valid: true` means the four `validation` error buckets are empty — `warnings` never block, and target existence is never checked. The top-level `authorization` (`{allowed, blockedBy, currentMode, allowlisted, hint}`, plus `surfaceProfile` on a `SURFACE_EXCLUDED` block) previews interception: `blockedBy` is `MODE_RESTRICTED`, `MODE_FORBIDDEN`, `SURFACE_EXCLUDED`, or null when the call would run — settle grants and exclusions there, not on the real call.

After a write, verify in the Editor, not from the echo: `*_get_info` / `*_get_properties`, or `find_objects_by_name`.

| Rule | Requirement |
|---|---|
| Uncertain parameters | Must dryRun first. Never guess parameters from a skill name. |
| Skill name mismatch | If the name does not appear in schema/recommend results, do not invent it. |
| Call failure | Read `suggestedFixes`; when pointed to a module doc, actually read it. |

## Surface profile

`surfaceProfile` says which slice of the skill surface the user exposed. You cannot change it — the user switches it in the UnitySkills panel. (`guideMode` is legacy; `surfaceProfile` is authoritative.)

| Profile | Hidden | What to do |
|---|---|---|
| `full` | nothing | Normal automation. |
| `guide` | write skills in GameObject, Component, Material, Scene and Sample | Give manual steps via [SKILL_GUIDE.md](SKILL_GUIDE.md) and the `manual-*` docs. Read-only skills there still work, as does every other module. |
| `noSceneAuthoring` | every scene-authoring write, incl. any `mutatesScene` skill | Do the rest of the task normally; if it genuinely needs scene authoring, say so and let the user switch back to `full`. |

Calling a hidden skill returns **`SURFACE_EXCLUDED`**, and the response names the document to read (or the profile to leave). It is a configuration boundary, not a failure: never retry it, never route around it through another module.

## Module routing

Every skill is named `module_verb`, so the prefix is the routing key. `GET /skills/recommend?intent=...` ranks candidates, or read the index — all 80 modules with mode labels, incl. docs-only ones (`manual-*`, `*-design`, `unity-cli`) that define no REST skills → [module index](skills/SKILL.md).

## Error codes quick reference

| Code | Meaning | Pointer |
|---|---|---|
| `MODE_RESTRICTED` / `MODE_FORBIDDEN` | Needs a user grant / needs Bypass or Allowlist. | [operating mode](references/protocol-operating-mode.md) |
| `SURFACE_EXCLUDED` | Hidden by the current `surfaceProfile`. | "Surface profile" above; read the doc named in the response. |
| `MISSING_PARAM` / `TARGET_NOT_FOUND` | Bad or unresolvable arguments. | dryRun for the schema; locate the target first. |
| any other code | — | [error codes](references/protocol-error-codes.md) — transient ones (`COMPILING`/`RATE_LIMIT`/`QUEUE_FULL`/`SERVER_STOPPED`) are auto-retried by the Python client. |

## Observability and Unity CLI pointers

Compilation status, events, analytics → [observability](references/protocol-observability.md). Unity CLI cold start (opt-in, v2.3+) → [unity-cli](references/protocol-unity-cli.md).

Current snapshot: `805` REST skills, `56` source files, `54` categories, `82` module documentation directories (`54` REST/module docs + `28` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`
