# references/ — what this directory actually is

Protocol documents live here, plus one shell guide. Check which file you are opening before you read it.

## 1. Protocol documents — `protocol-*.md`

These are the sunk detail of the root `../SKILL.md`, not background reading. Open one when the root doc (or an error response) points at it:

| File | Read it when |
|---|---|
| `protocol-discovery.md` | The root doc's one recommend call is not enough: multi-intent response shape, `names=` / category / summary / full schema layers with sizes, reading `wire=v2` payloads (`flags`, omitted defaults), `GET /skills/meta`. |
| `protocol-error-codes.md` | You got an `errorCode` the root doc's quick table doesn't cover, or you are writing your own client and need the full code / `retryStrategy` list. |
| `protocol-operating-mode.md` | You hit `MODE_RESTRICTED` / `MODE_FORBIDDEN` / `SURFACE_EXCLUDED` / `INSTANCE_MISMATCH`, or need the grant protocol, the mode table, the Allowlist rules, the surface profiles, or the expected-instance and registry (`reloading`) rules. |
| `protocol-observability.md` | You need to close the loop after a mutation — the `GET /jobs/{id}?wait=` long poll, compilation status, the `GET /events` long-poll, analytics. |
| `protocol-unity-cli.md` | The user opted into Unity CLI cold start and you must launch a closed Editor. Per-command detail lives in `../skills/unity-cli/SKILL.md`. |
| `protocol-runtime-verification.md` | You verify behaviour in Play Mode: `component_get_methods` → `component_invoke_method` / `editor_run_scenario`, then the bounded compile → scenario → repair → rerun loop (`scripts/runtime_verification.py`). |

`../SKILL.md` stays small by living off these six; treat them as its chapters.

## 2. Shell guide — `windows.md`

[windows.md](windows.md): read it when your commands run in PowerShell (or Git Bash) on Windows: the root doc's calls rewritten for `Invoke-RestMethod`, and the quoting, encoding and `curl` alias traps that break them silently.

## Neither answers "what are this skill's parameters?"

For exact skill names, parameters, defaults and returns, use the root doc's quick reference or the schema endpoints (`GET /skills/recommend?intent=…&includeSchema=true`, `GET /skills/schema?names=…`) — see "Everything else: one discovery call" in `../SKILL.md` and `protocol-discovery.md`. Nothing in this directory is a substitute for the schema.
