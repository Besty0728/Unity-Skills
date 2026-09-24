# Protocol: Discovery and Wire Format

> The root `SKILL.md` "Everything else: one discovery call" is the condensed version of this page. Most tasks need one recommend call, or none when the root quick reference already has the skill.

## Default: one recommend call

`GET /skills/recommend?intent=<words>&includeSchema=true&topN=3&wire=v2` returns scored candidates with their parameter schemas.

- `topN` caps the candidates (default `10`, clamped `1`–`50`). Measured over 8 intents, `topN=3` cuts the answer by about 68% against the default, `wire=v2` by about 14%, both together by about 72%; a `topN=3&wire=v2` answer is typically 1–4 KB.
- Several needs in one task: repeat `intent=` (`?intent=move+object&intent=light+color&includeSchema=true&topN=3&wire=v2`). Each intent is ranked on its own with the same `topN` / `includeSchema` / `wire`, and the answer becomes `{topN, includeSchema, …, intents:[{intent, expandedKeywords, totalMatches, results}]}`. A single distinct intent keeps the single-intent shape `{intent, expandedKeywords, topN, includeSchema, totalMatches, results}`.
- A result carries `name`, `description`, `category`, `score`, `semanticScore`, `confidence`, `matchedOn`, optional `telemetry` / `warnings`, and `schema` when `includeSchema=true`. `unavailable` (with `missingPackages`) marks a skill whose optional package is not installed: calling it answers `MISSING_PACKAGE`, so the marker already settles availability (while the package list is still loading, nothing is marked).
- Skills hidden by the surface profile are left out of the ranking. Under a profile other than `full` the envelope adds `surfaceProfile` and `surfaceProfileHint`: a skill missing from the results may be hidden rather than nonexistent.
- Skills are named `module_verb` (`<module>_<action>`, `_batch` for the multi-object twin), so the prefix is the routing key. The [module index](../skills/SKILL.md) lists every module, including the docs-only ones (`manual-*`, `*-design`, `unity-cli`).

## Exact signatures by name

`GET /skills/schema?names=a,b&wire=v2` returns only the named skills (~2–3 KB for two or three). The answer says `filtered:true`; a server that predates the filter ignores `names=` and returns the whole schema, so check it. Selector-like keys the filter grammar does not have (`skill`, `skills`, `name`, `id`, …) answer 400 `UNKNOWN_PARAM` pointing at `names=` / `q=`.

## Wider layers

| Layer | Endpoint | Size | Use when |
|---|---|---|---|
| directory | `GET /skills` | ~21 KB | Names by category only, to locate a module. `?full=1` is the complete listing (~707 KB). |
| category | `GET /skills/schema?category=<Category>&wire=v2` | ~11–14 KB for GameObject / Component (v1 ~25–29 KB) | The task stays in one or two areas. |
| summary | `GET /skills?summary=1` | ~180 KB | Exploratory or cross-module work, when the cheaper layers left you unsure. |
| full schema | `GET /skills/schema` | ~707 KB (v2 ~523 KB) | Rare: many modules' signatures at once. |

All layers are server-cached with ETag / `304`; send `Accept-Encoding: gzip`. The sizes were measured on the 805-skill build and grow with the skill count and parameter notes.

## Wire format v2 (`?wire=v2`)

`?wire=v2` slims `?full=1`, `/skills/schema` (full, `category=` or `names=`), a filtered `/skills`, `/skills/recommend`, `/skills/meta`, dryRun and `/skills/batch` — never bare `GET /skills`. v1 stays the default.

- A **`flags`** array replaces v1's six booleans and adds v2-only `longRunning`: `readOnly`, `tracksWorkflow`, `mutatesScene`, `mutatesAssets`, `mayTriggerReload`, `mayEnterPlayMode`, `longRunning`. An absent flag is false.
- **Omitted means default**: `riskLevel` appears only when it is not `"low"`, `supportsDryRun` only when it is `false`, and null members are dropped, so an absent key is never null.
- Every v2 response carries a **`defaults`** block (`{"riskLevel":"low","supportsDryRun":true}`) and a `metaUrl`; take the rule from the payload.
- A v2 dryRun keeps every verdict field (`valid`, `validation`, `impact`, `authorization`, `steps`, `changes`) and slims the echo: `skill` shrinks to name, category, operation, mode, riskLevel, longRunning and flags, and `parameters` lists only what you sent plus required parameters still missing.
- On `/skills/batch`, a dry run returns v2 step payloads; executed steps return the skills' own results either way.
- Measured savings: about 52–55% on category schemas, 26% on the full schema, 14% on recommend, 91% on `/skills/meta`.

## Session constants: `GET /skills/meta`

`GET /skills/meta?wire=v2` (~1 KB) holds the constants every skill shares: `categories`, `operationTypes`, `reservedBodyParameters` (`verbose`, `offset`, `limit`, `pageOffset`, `pageLimit`, `_confirm`, accepted by every skill), `schemaVersion` and `defaults`. The v1 answer (~11 KB) also inlines `workflowTrackedSkills`, which v2 leaves to each entry's `tracksWorkflow` flag. Fetch it when you need these lists; nothing requires it before other calls.

## Python client

`unity_skills.find_skills(intent, top_n=3, include_schema=True, wire="v2")` is the root doc's default call (the function's own defaults are `top_n=10`, `include_schema=False`, v1). `unity_skills.get_skill_schema()` fetches the **full** schema; prefer recommend or a scoped `GET /skills/schema`. `unity_skills.get_meta()` caches `/skills/meta` for the session.
