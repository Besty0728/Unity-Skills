### script_append
Insert lines into a script.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `scriptPath` | string | Yes | - | Script path |
| `content` | string | Yes | - | Content to insert |
| `atLine` | int | No | -1 | 0-based line index to insert before; the line count appends at the end; -1 or out of range = the default placement below |
| `checkCompile` | bool | No | true | Check compilation after append |
| `diagnosticLimit` | int | No | 20 | Max compile diagnostics |

Default placement: before the last line that is only `}` (at the end when there is none). When that brace closes a namespace, a member (method, field, property) goes before the closing brace of the namespace's last class instead, with a warning, since a member cannot sit directly in a namespace; a type, namespace or `using` stays at namespace level. Braces in strings, chars and comments are ignored; if the brace structure cannot be read the old placement is kept (`insertionScope: "legacy"`, with a warning).

**Returns**: `{success, status: "accepted", path, jobId, waitUrl, serverAvailability, insertedAtLine, insertionScope, warnings?}`: `insertedAtLine` is the 0-based line the content now starts on, `insertionScope` is `atLine`, `endOfFile`, `type:<Class>`, `namespace:<Ns>`, `block` or `legacy`. GET `waitUrl` for the compile result.
