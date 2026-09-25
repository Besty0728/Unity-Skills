# Windows shells: PowerShell and Git Bash

The REST API is the same on every OS; only the client shell changes. This page gives the root doc's calls in PowerShell and lists the traps that silently break them. Replace `8090` with your instance's port.

## Which shell you are in

- **Git Bash** (Claude Code on Windows runs its commands there): the root doc's `curl` one-liners work unchanged.
- **PowerShell** (the default in many other hosts): use `Invoke-RestMethod` as below. Check the version with `$PSVersionTable.PSVersion` — Windows PowerShell 5.1 and PowerShell 7 differ in the traps.

## Traps

1. **`curl` is not curl in Windows PowerShell 5.1.** It is an alias of `Invoke-WebRequest` with different parameters; call `curl.exe` explicitly. PowerShell 7 has no such alias.
2. **JSON in arguments to native programs** (`curl.exe`, `python`): Windows PowerShell 5.1 does not escape embedded double quotes for the program, so `curl.exe -d '{"name":"A"}'` arrives as `{name:A}`; there you must write `\"` inside the body. PowerShell 7.3+ passes the quotes as written, and the same `\"` then breaks the JSON. Avoid the difference: use `Invoke-RestMethod` (a cmdlet, no native argument passing), `curl.exe -d "@body.json"`, or the Python CLI's `--params-file`.
3. **Body encoding.** Without a charset, `Invoke-RestMethod` does not send the body as UTF-8 before PowerShell 7.4 (7.4+ defaults to UTF-8), so non-ASCII names such as `"name":"箱子"` are mangled. Always pass `-ContentType 'application/json; charset=utf-8'`.
4. **BOM.** Windows PowerShell 5.1 `Set-Content -Encoding UTF8` writes a UTF-8 BOM (PowerShell 7 `utf8` does not). The server and the Python CLI (`--params-file`, `--batch`) accept both.
5. **Asset paths always use `/`** (`Assets/Scenes/Main.unity`), on every OS. `Join-Path` and `Resolve-Path` produce `\`; convert before sending.

## PowerShell equivalents

Probe the server:

```powershell
$u = 'http://localhost:8090'
Invoke-RestMethod "$u/health"
```

One call:

```powershell
Invoke-RestMethod -Method Post -Uri "$u/skill/gameobject_create" -ContentType 'application/json; charset=utf-8' -Body '{"name":"Crate","primitiveType":"Cube","x":1}'
```

DryRun first (deletes, high risk, reloads — see the root doc):

```powershell
Invoke-RestMethod -Method Post -Uri "$u/skill/gameobject_delete?mode=dryRun&wire=v2" -ContentType 'application/json; charset=utf-8' -Body '{"name":"Crate"}'
```

Batch (one request, `$ref` works as in the root doc):

```powershell
$steps = '{"steps":[{"skill":"gameobject_create","args":{"name":"Crate","primitiveType":"Cube"}},{"skill":"component_add","args":{"name":"Crate","componentType":"Rigidbody"}}]}'
Invoke-RestMethod -Method Post -Uri "$u/skills/batch" -ContentType 'application/json; charset=utf-8' -Body $steps
```

A script you wrote yourself, then refresh and wait out compile and domain reload. The server is briefly unreachable during the reload, so retry the wait:

```powershell
Set-Content -Path 'Assets/Scripts/Spin.cs' -Value $source -Encoding UTF8
$r = Invoke-RestMethod -Method Post -Uri "$u/skill/asset_refresh" -ContentType 'application/json; charset=utf-8' -Body '{}'
if ($r.result.compileTriggered) {
    for ($i = 0; $i -lt 30; $i++) {
        try { $job = Invoke-RestMethod "$u$($r.result.waitUrl)" -TimeoutSec 150; break } catch { Start-Sleep -Seconds 2 }
    }
    $job.status   # completed, or failed with errors in resultData.compilation
}
```

Responses are objects: read `$r.status`, `$r.result.<field>`, `$r.errorCode` directly instead of piping through `ConvertFrom-Json`.

`curl.exe` in Windows PowerShell 5.1 only, quotes escaped as trap 2 describes:

```powershell
curl.exe -s -X POST "http://localhost:8090/skill/gameobject_find" -d '{\"name\":\"Crate\"}'
```

## Python CLI with a parameter file

Write the parameters with any tool (encoding with or without BOM), then call; `key=value` arguments override keys from the file:

```powershell
Set-Content -Path params.json -Value '{"name":"Crate","primitiveType":"Cube"}' -Encoding UTF8
python unity_skills.py --params-file params.json gameobject_create
python unity_skills.py --params-file params.json gameobject_create name=Crate2
```

## Sources

- `curl` alias in Windows PowerShell 5.1: [curl on Windows](https://learn.microsoft.com/windows/curl/)
- Native argument passing, `$PSNativeCommandArgumentPassing` (7.3+): [about_Parsing](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_parsing#passing-arguments-that-contain-quote-characters)
- Request body encoding: [Invoke-RestMethod (5.1) `-ContentType`](https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/invoke-restmethod?view=powershell-5.1), [Invoke-RestMethod (7.x): UTF-8 default from 7.4](https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/invoke-restmethod)
- BOM from `-Encoding UTF8`: [about_Character_Encoding](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_character_encoding)
