# references/ — what this directory actually is

Every file here is a flat **URL index** into `docs.unity3d.com`, grouped by topic: each entry is just `## <page title>` followed by `**URL:** <link>` — no code, no method signatures, no parameter tables. Do not read one expecting API detail; read one when you need the **official doc URL** for a topic so you can fetch it (or hand the link to the user).

For exact skill parameter names/types/defaults/returns, use the schema endpoints (`/skills/schema`, `?mode=dryRun`) instead — see the "Schema" section in `../SKILL.md`. These files are a routing table to Unity's own manual, not a substitute for the schema.

**Size warning** — several files are large; grep or fetch a specific `##` section rather than reading the whole file:

| File | Size |
|---|---|
| `other.md` | ~214 KB (1755 pages — catch-all topic bucket, largest by far) |
| `shaders.md` | ~90 KB |
| `xr.md` | ~46 KB |
| `3d.md` / `physics.md` | ~16 KB each |
| `2d.md` | ~13 KB |
| everything else | under 8 KB |

`index.md` lists every category with its page count; start there to pick the right file before opening one.
