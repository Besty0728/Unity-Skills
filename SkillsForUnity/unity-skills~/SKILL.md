---
name: unity-skills
description: Automate the Unity Editor through a local REST API — create and edit scripts, build scenes and prefabs, manage assets/materials/lighting, run tests, and drive hundreds of Editor operations across modules. Use when the user wants to actually operate the Unity Editor from chat — create or modify GameObjects/scripts/scenes/assets, batch-edit, or run Editor automation — in any language. Not needed for conceptual Unity Q&A that touches no Editor state — read the matching advisory doc under skills/ instead. 当用户要从对话里实际操作 Unity 编辑器（创建/修改/批量编辑/运行测试）时使用，任何语言均可触发；纯概念问答无需本协议。
compatibility: Requires Unity Editor 2022.3+/6000.x with the UnitySkills package (local REST server on localhost:8090-8100); Python 3 for the bundled client
---

# Unity Skills

> All module and reference docs below are English-only. Match the user's request to modules by meaning, regardless of the user's language, and always reply in the user's language.
> Pure conceptual Unity Q&A that touches no Editor state → do not load this protocol; read the matching advisory doc under `skills/` instead.

## First Contact Checklist

Before the first skill call in a session:

1. **`GET /health`** — discover the server (ports `8090`–`8100`) and read `currentMode` (`"approval"` / `"auto"` / `"bypass"`), `panelApprovalRequired`, and `pendingCount`.
2. **Branch on `currentMode`**: under `approval`, the first write call to any `FullAuto` skill returns `MODE_RESTRICTED` and you must run the grant protocol before it executes; under `auto`/`bypass`, writes execute directly (self-assess risk under `auto`). Full protocol and mode table: see "Operating Mode" → "Boot Handshake" below.
3. Only then proceed to skill discovery (below) and calls.

## Schema: pick the cheapest layer

All layers are server-cached with ETag/304 and served off the main thread; send `Accept-Encoding: gzip`.

| Layer | Endpoint | Size | Use when |
|---|---|---|---|
| **Default: start from `GET /skills/recommend?intent=...&includeSchema=true`** | | ~2–5 KB | Specific intent; returns scored candidates with parameter schemas. |
| brief + category | `GET /skills?brief=1` then `GET /skills/schema?category=<Category>` | ~19 KB + 13–44 KB | Task touches one or two areas. |
| summary | `GET /skills?summary=1` | ~143 KB | Exploratory / cross-module / cheaper layers left you unsure. |
| full | `GET /skills/schema` | ~618 KB | Rare; many modules' exact signatures at once. |

## dryRun gate and anti-hallucination rules

Before executing any skill whose exact parameters you don't already hold, dryRun it: `POST /skill/<name>?mode=dryRun`. Iterate until `valid: true`, then execute without `?mode=dryRun`.

| Rule | Requirement |
|---|---|
| Uncertain parameters | Must dryRun first. Never guess parameters from a skill name. |
| Skill name mismatch | If the name does not appear in schema/recommend results, do not invent it. |
| Call failure | Read `suggestedFixes`; when pointed to a module doc, actually read it. |

## Operating Mode

Three server-side permission gates: **Approval** (first FullAuto call needs a grant), **Auto** (executes directly, self-assess risky batches), **Bypass** (executes directly). Grants are single-shot per call; permanent bypass is the user-managed Allowlist. Details → [operating mode](references/protocol-operating-mode.md).

## Module routing by category

Load the matching module `SKILL.md` for guardrails and minimal examples; use schema/recommend for exact signatures, not module docs.

| Category | Modules |
|---|---|
| GameObject & Scene | `gameobject`, `scene`, `prefab`, `component`, `terrain`, `navmesh` |
| UI | `ui`, `uitoolkit` |
| Rendering & URP | `material`, `shader`, `shadergraph`, `light`, `graphics`, `urp`, `volume`, `decal`, `postprocess` |
| Animation & Camera | `animator`, `timeline`, `cinemachine`, `camera` |
| Physics & Behavior | `physics`, `behavior` |
| Scripting & Testing | `script`, `test`, `debug`, `console`, `validation`, `smart` |
| Assets & Packages | `asset`, `importer`, `package`, `scriptableobject` |
| Tween & Modeling | `dotween`, `primetween`, `probuilder` |
| Networking | `netcode` |
| Hot-update & Bundles | `hybridclr`, `yooasset` |
| Editor & Workflow | `editor`, `project`, `profiler`, `workflow`, `batch`, `cleaner`, `event`, `optimization`, `perception`, `sample` |
| XR | `xr` |
| Advisory & design docs | `architecture`, `patterns`, `performance`, `*-design` (8), and more → see index |
| Manual tasks (docs-only) | `manual-gameobject`, `manual-component`, `manual-material`, `manual-scene` |
| Full index (all 79 modules) | → [module index](skills/SKILL.md) |

## guideMode

If `/health` returns `guideMode: true`, read [SKILL_GUIDE.md](SKILL_GUIDE.md) and give manual steps for simple tasks instead of calling REST.

## Error codes quick reference

| Code | Meaning | Pointer |
|---|---|---|
| `MODE_RESTRICTED` | Approval mode: FullAuto skill needs a user grant. | Grant protocol → [operating mode](references/protocol-operating-mode.md) |
| `MODE_FORBIDDEN` | Skill is `NeverInSemi`; needs Bypass or Allowlist. | Details → [operating mode](references/protocol-operating-mode.md) |
| `MISSING_PARAM` | Required parameter omitted. | Get schema via dryRun; full list → [error codes](references/protocol-error-codes.md) |
| `TARGET_NOT_FOUND` | GameObject / asset / component not found. | Locate first; full list → [error codes](references/protocol-error-codes.md) |
| `COMPILING` / `RATE_LIMIT` / `QUEUE_FULL` / `SERVER_STOPPED` | Transient; Python client auto-retries. | Full list → [error codes](references/protocol-error-codes.md) |

## Observability and Unity CLI pointers

- Compilation status, events, analytics → [observability](references/protocol-observability.md)
- Unity CLI cold start (opt-in, v2.3+) → [unity-cli](references/protocol-unity-cli.md)

Current snapshot: `784` REST skills, `54` functional source modules, `79` module documentation directories (`50` REST/module docs + `29` advisory docs), Unity `2022.3+`, default timeout `15 minutes`.

Python helper: `unity-skills/scripts/unity_skills.py`
