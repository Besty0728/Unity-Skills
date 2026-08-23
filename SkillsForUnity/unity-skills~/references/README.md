# references/ — what this directory actually is

Two unrelated kinds of files live here. Check which kind you are opening before you read it.

## 1. Protocol documents — `protocol-*.md`

These are the sunk detail of the root `../SKILL.md`, not background reading. Open one when the root doc (or an error response) points at it:

| File | Read it when |
|---|---|
| `protocol-error-codes.md` | You got an `errorCode` the root doc's quick table doesn't cover, or you are writing your own client and need the full code / `retryStrategy` list. |
| `protocol-operating-mode.md` | You hit `MODE_RESTRICTED` / `MODE_FORBIDDEN`, or need the grant protocol, the mode table, or the Allowlist rules. |
| `protocol-observability.md` | You need to close the loop after a mutation — compilation status, the `GET /events` long-poll, analytics. |
| `protocol-unity-cli.md` | The user opted into Unity CLI cold start and you must launch a closed Editor. Per-command detail lives in `../skills/unity-cli/SKILL.md`. |

`../SKILL.md` stays small by living off these four; treat them as its chapters.

## 2. Unity manual URL indexes — everything else

`index.md` plus 18 topic files (`other.md`, `shaders.md`, `xr.md`, `physics.md`, …) are a flat **URL index** into `docs.unity3d.com`: each entry is `## <page title>` followed by `**URL:** <link>` — no code, no method signatures, no parameter tables. Read one only when you need the **official doc URL** for a topic, to fetch it or hand the link to the user. Start from `index.md`, which lists every category with its page count, then open the one file you need.

`SKILL_FULL.md` belongs to this group in spirit: it is the complete reference-manual archive of the protocol. The daily entry point is `../SKILL.md`; reach for the archive only when you need a section the slim root doc no longer carries.

**Size warning** — grep or fetch a specific `##` section rather than reading a whole file:

| File | Size |
|---|---|
| `other.md` | ~214 KB (1755 pages — catch-all topic bucket, largest by far) |
| `shaders.md` | ~90 KB |
| `xr.md` | ~46 KB |
| `SKILL_FULL.md` | ~30 KB |
| `3d.md` / `physics.md` | ~16 KB each |
| `2d.md` | ~13 KB |
| everything else | under 8 KB |

## Neither kind answers "what are this skill's parameters?"

For exact skill names, parameters, defaults and returns, use the schema endpoints (`GET /skills/schema?category=<Category>`, `POST /skill/<name>?mode=dryRun`) — see "Schema: pick the cheapest layer" in `../SKILL.md`. Nothing in this directory is a substitute for the schema.
