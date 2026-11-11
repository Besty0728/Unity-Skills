# Unity HybridCLR Skills

Editor-side automation for [HybridCLR](https://hybridclr.doc.code-philosophy.com/), the native C# hot-update solution for IL2CPP: settings, install probing, hot-update DLL compilation, the prebuild generation pipeline and artifact staging for a YooAsset build. Parameters come from the schema (`GET /skills/schema?category=HybridCLR&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

This module has no `###` skill sections on purpose; long prose lives in the topic files under [reference/](reference/).

## When to use
- Configuring hot-update assemblies and HybridCLR settings
- Compiling hot-update DLLs or running the generation pipeline
- Generating `AOTGenericReferences.cs` / `link.xml`
- Diagnosing the HybridCLR setup
- Staging hot-update output for a YooAsset bundle build

## Rules
- Requires `com.code-philosophy.hybridclr` `>=8.12.0 <10.0.0` and the IL2CPP backend. Unity support follows the installed release: see [version scope](reference/version-scope.md).
- Only `hybridclr_status` works without the package; every other skill returns `MISSING_PACKAGE` with install instructions. Detection is pure reflection, so no recompile is needed after installing.
- An out-of-range package version makes every skill, status included, return `SKILL_ERROR` with `reason: "unsupported_package_version"`. Changing the package version is the user's decision.
- `hybridclr_compile_dlls`, `hybridclr_generate_all` and `hybridclr_generate_step` are NeverInSemi and block the Editor main thread for minutes: `/health` and `/jobs` stall too. Raise the client timeout to at least 10 minutes; a socket timeout does not cancel them. They are synchronous, with no `jobId`.
- Hot-update code must live in an asmdef listed in `hotUpdateAssemblyDefinitions` (or a name in `hotUpdateAssemblies`). Code outside those assemblies is AOT and needs a new player build.
- Array parameters replace, they do not merge: read `hybridclr_settings_get` first and pass the full intended array.
- Run `hybridclr_validate_setup` before generating: `errors` block hot update; `warnings` are normal early in setup.
- Workflow undo is partial for generation: `hybridclr_settings_set` restores fully, the generation steps restore only the Assets-side artifacts and the hot-update DLL directory. Re-run the pipeline instead of relying on undo.
- Read the [critical rules](reference/critical-rules.md) before the first generation or settings write.

## Pitfalls
- `hybridclr_install` and `hybridclr_init` do not exist. `hybridclr_install_status` only reports. Install from the HybridCLR/Installer window or through unity-cli ([limitations](reference/limitations.md)).
- `hybridclr_build_player` is not in this module: use `build_player` or unity-cli.
- `hybridclr_load_metadata` and `hybridclr_load_assembly` do not exist: write the runtime loading in game code.
- Do not run `hybridclr_generate_all` before every bundle rebuild; it is a pre-player-build step ([workflow](reference/workflow.md)).
- Do not pass `extension=".dll"` to `hybridclr_copy_hotupdate_dlls`; the skill rejects it, because Unity would load staged DLLs as managed plugins.
- On 9.x, do not pass `patchAOTAssemblies`, `hotUpdateDllCompileOutputRootDir` or `strippedAOTDllOutputRootDir` to `hybridclr_settings_set`, and do not run `clean_il2cpp_cache`; on 8.x, do not run `method_bridge_empty`. The call returns `SEMANTIC_INVALID` and writes nothing.

## Skills Overview

| Skill | Purpose |
|---|---|
| `hybridclr_status` | Reflection probe and `apiLine` (`8.x` / `9.x`); call first |
| `hybridclr_install_status` | Local il2cpp_plus install state and supported Unity versions |
| `hybridclr_get_paths` | Resolve input and output paths for a build target |
| `hybridclr_settings_get` | Read `HybridCLRSettings` and the resolved assembly lists |
| `hybridclr_settings_set` | Write settings (only the passed parameters change) |
| `hybridclr_validate_setup` | Pre-flight check: `errors` (blocking) and `warnings` (advisory) |
| `hybridclr_compile_dlls` | Compile hot-update assemblies into the target's DLL directory (blocking) |
| `hybridclr_generate_all` | Full prebuild pipeline for the active build target (blocking) |
| `hybridclr_generate_step` | One pipeline step (step names as parameter values: il2cpp def, link xml, AOT DLLs, method bridge, AOT generic reference, plus clean IL2CPP cache on 8.x or method bridge empty on 9.0.2+) |
| `hybridclr_get_hotupdate_dlls` | List compiled DLLs, reconciled with the configured list (`missing`, `unexpected`) |
| `hybridclr_copy_hotupdate_dlls` | Stage DLLs (optionally AOT DLLs) into `Assets/` with a `.bytes` extension |
| `hybridclr_aot_generic_refs` | Read generated `AOTGenericReferences.cs` and its AOT list |

## Topics

- [workflow](reference/workflow.md): one-time setup, the hot-update loop, the YooAsset boundary
- [critical rules](reference/critical-rules.md): settings, paths, array semantics, main-thread blocking
- [version scope](reference/version-scope.md): 8.x vs 9.x differences, reflection anchors, upstream AI skills
- [limitations](reference/limitations.md): no install skill, undo scope, unity-cli fallback menu table
