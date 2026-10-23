# Windows shells: PowerShell and Git Bash

The REST API is the same on every OS; only the client shell changes. This page gives the root doc's calls in PowerShell and lists the traps that silently break them.

## Which shell you are in

- **Git Bash** (Claude Code on Windows runs its commands there): the root doc's `curl` one-liners work unchanged.
- **PowerShell** (the default in many other hosts): use `Invoke-RestMethod` as below. Check `$PSVersionTable.PSVersion`: Windows PowerShell 5.1 and PowerShell 7 differ in the traps.

## Traps

1. **`curl` is not curl in Windows PowerShell 5.1.** It is an alias of `Invoke-WebRequest`; call `curl.exe`. PowerShell 7 has no alias.
2. **JSON in arguments to native programs** (`curl.exe`, `python`): Windows PowerShell 5.1 drops the embedded double quotes (`{"name":"A"}` arrives as `{name:A}`), PowerShell 7.3+ keeps them, so no one quoting works on both. Use `Invoke-RestMethod` (a cmdlet, no native argument passing), `curl.exe -d "@body.json"`, or the Python CLI's `--params-file`.
3. **Body encoding.** Send the body as UTF-8 bytes (the helper below does): before PowerShell 7.4 a string body is not reliably UTF-8, so `"name":"箱子"` can arrive mangled. Keep `charset=utf-8` in the `-ContentType` too.
4. **`$` in double quotes expands.** `$ref` values such as `$0.entityId` must be single-quoted (or the whole JSON body in single quotes).
5. **BOM.** Windows PowerShell 5.1 `Set-Content -Encoding UTF8` writes a BOM (PowerShell 7 does not). The server and the Python CLI accept both.
6. **Asset paths use `/`** (`Assets/Scenes/Main.unity`) on every OS; `Join-Path` and `Resolve-Path` produce `\`, so convert.

## PowerShell equivalents of the root doc

Setup once per session: the port from `/health` probing (`8090`-`8100`) and the project name go in every write's `expectProject`.

```powershell
$u = 'http://localhost:8090'
$q = '?expectProject=' + [uri]::EscapeDataString('MyProject')
function Invoke-Skill($path, $body) {
    $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-RestMethod -Method Post -Uri "$u$path" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
}
Invoke-RestMethod "$u/health"   # currentMode, surfaceProfile, dryRunPolicy, instanceId
```

One call, targeting by `name` (or the exact `path` / `entityId`):

```powershell
Invoke-Skill "/skill/gameobject_set_transform$q" @{ name = 'Crate'; posX = 4; posY = 1.5; posZ = -2 }
```

DryRun first (deletes, high risk, reloads, approval mode; see the root doc):

```powershell
Invoke-Skill "/skill/gameobject_delete$q&mode=dryRun&wire=v2" @{ name = 'Crate' }
```

Batch, up to 50 steps in one request; `@{ '$ref' = '$0.entityId' }` reads step 0's result. `-Depth` must cover `steps` > step > `args` (the default 2 silently flattens it):

```powershell
$steps = @{ steps = @(
    @{ skill = 'gameobject_create'; args = @{ name = 'Rig' } },
    @{ skill = 'component_add'; args = @{ entityId = @{ '$ref' = '$0.entityId' }; componentType = 'Rigidbody' } }
) }
Invoke-Skill "/skills/batch$q" $steps
```

Add `&mode=dryRun&wire=v2` to preview, `&mode=transactional` for all-or-nothing.

A script: dryRun, create, then one GET that waits out compile and domain reload. During the reload the port refuses or answers 503, so the wait is retried:

```powershell
$source = @'
using UnityEngine;
public class Spin : MonoBehaviour { public float speed = 9f; void Update() { transform.Rotate(0, speed * Time.deltaTime, 0); } }
'@
$body = @{ scriptName = 'Spin'; content = $source }
if ((Invoke-Skill "/skill/script_create$q&mode=dryRun" $body | ConvertTo-Json -Depth 10 -Compress) -match '"valid":true') {
    $r = Invoke-Skill "/skill/script_create$q" $body
    $wait = [regex]::Match(($r | ConvertTo-Json -Depth 10 -Compress), '/jobs/[^"]*=90').Value
    for ($i = 0; $i -lt 30; $i++) {
        try { $job = Invoke-RestMethod "$u$wait" -TimeoutSec 150; if (-not $job.waitTimedOut) { break } }
        catch { Start-Sleep -Seconds 2 }
    }
    $job.status   # completed, or failed with errors in resultData.compilation
}
```

For a `.cs` you wrote with your own file tools, run `Invoke-Skill "/skill/asset_refresh$q" '{}'` and wait on its `waitUrl` the same way when `compileTriggered` is true.

Responses are objects: read `$r.status`, `$r.errorCode`, `$r.suggestedFixes` directly instead of piping through `ConvertFrom-Json`.

## Python CLI with a parameter file

```powershell
Set-Content -Path params.json -Value '{"name":"Crate","primitiveType":"Cube"}' -Encoding UTF8
python unity_skills.py --params-file params.json gameobject_create
```

`key=value` arguments after the skill name override keys from the file.

## Sources

- `curl` alias: [curl on Windows](https://learn.microsoft.com/windows/curl/); native argument quoting: [about_Parsing](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_parsing#passing-arguments-that-contain-quote-characters)
- Body encoding: [Invoke-RestMethod (5.1)](https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/invoke-restmethod?view=powershell-5.1), [Invoke-RestMethod (7.x)](https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/invoke-restmethod)
- BOM: [about_Character_Encoding](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_character_encoding)
