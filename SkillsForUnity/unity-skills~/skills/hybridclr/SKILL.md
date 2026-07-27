---
name: unity-hybridclr
description: Automate HybridCLR C# hot-update prebuild in the Unity Editor — read and write HybridCLRSettings, probe the il2cpp_plus install, compile hot-update DLLs, run the Generate/All pipeline or a single generation step, inspect AOTGenericReferences, and stage compiled DLLs into a YooAsset collector directory. Use when the user is working on C# hot update / HybridCLR — configuring hot-update assemblies, compiling hot-update dll, generating AOTGenericReferences or link.xml, diagnosing a broken HybridCLR setup, or wiring HybridCLR output into a YooAsset bundle build. 自动化 HybridCLR 的 C# 热更新预构建(读写 HybridCLRSettings、检测 il2cpp_plus 安装状态、编译热更 dll、执行 Generate/All 或单步生成、查看 AOTGenericReferences、把产物拷进 YooAsset 收集目录);当用户配置热更程序集、编译热更 dll、生成 AOTGenericReferences/link.xml、排查热更环境问题,或要把 HybridCLR 产物接进 YooAsset 打包链路时使用。
---

# Unity HybridCLR Skills

Editor-side automation for [HybridCLR](https://hybridclr.doc.code-philosophy.com/) — the zero-cost native C# hot-update solution for IL2CPP. Covers settings CRUD, installation probing, hot-update assembly compilation, the prebuild generation pipeline, and artifact staging for a YooAsset bundle build.

**This module holds zero direct references to the package.** Every call resolves through reflection against the `HybridCLR.Editor` assembly, so the UnitySkills Editor assembly compiles identically whether or not HybridCLR is installed — there is no scripting define to set and no recompile needed after installing.

> **Requires**: `com.code-philosophy.hybridclr` (API anchored to **8.12.0**), IL2CPP scripting backend, Unity 2019.4+ (2022.3+ recommended).
> **Companion modules**: [yooasset](../yooasset/SKILL.md) for shipping the compiled DLLs as bundles, [yooasset-design](../yooasset-design/SKILL.md) for the runtime loading contract, [asmdef](../asmdef/SKILL.md) for assembly-boundary design.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): the seven read-only skills — `hybridclr_status`, `hybridclr_install_status`, `hybridclr_get_paths`, `hybridclr_settings_get`, `hybridclr_validate_setup`, `hybridclr_get_hotupdate_dlls`, `hybridclr_aot_generic_refs` — are `SemiAuto` and run directly. `hybridclr_settings_set` and `hybridclr_copy_hotupdate_dlls` are FullAuto: on `MODE_RESTRICTED`, run the grant protocol.
- **Auto-forbidden** (NeverInSemi, `RiskLevel="high"`): `hybridclr_compile_dlls`, `hybridclr_generate_all`, `hybridclr_generate_step`. Reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`. This is deliberate — all three block the Editor main thread for minutes.
- When the package is missing, every skill except `hybridclr_status` returns a `MISSING_PACKAGE` error with install instructions. When the package is present but a reflected member cannot be resolved (version drift), the skill returns `MISSING_PACKAGE` naming the exact member instead of throwing.

**DO NOT** (common hallucinations):
- `hybridclr_install` / `hybridclr_init` — do NOT exist. Installing il2cpp_plus clones two git repos and copies the entire editor il2cpp tree; it is a multi-minute network operation with no cancellation and is intentionally not a skill. Run it from **HybridCLR/Installer...** in the Editor, or via unity-cli (see Limitations). `hybridclr_install_status` only *reports* the state.
- `hybridclr_build_player` — NOT in this module. Building the player is `build_player` (Project module) or unity-cli. HybridCLR hooks the build via its own `IPreprocessBuildWithReport` processors; nothing extra is needed from this module.
- `hybridclr_load_metadata` / `hybridclr_load_assembly` — runtime APIs (`HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly`, `System.Reflection.Assembly.Load`) belong in game code, not the Editor REST surface. Write them yourself.
- Do NOT call `hybridclr_generate_all` before every bundle rebuild — see the workflow section below. It is a **pre-player-build** step, not a per-hot-update step.
- Do NOT pass `extension=".dll"` to `hybridclr_copy_hotupdate_dlls`. Unity would treat the staged files as managed plugins and try to load them into the Editor domain; the skill rejects it.

**Routing**:
- HybridCLR settings, install probing, DLL compilation, generation pipeline, artifact staging → this module.
- Packing the staged DLLs into bundles, collector configuration, build reports → [yooasset](../yooasset/SKILL.md).
- Runtime hot-update loading code (`YooAssets.LoadAssetAsync` → `Assembly.Load` → `RuntimeApi.LoadMetadataForAOTAssembly`) → write it yourself using [yooasset-design](../yooasset-design/SKILL.md).
- Deciding which assemblies belong on the hot-update side → [asmdef](../asmdef/SKILL.md).
- Actual player build → `build_player` or [unity-cli](../unity-cli/SKILL.md).

## Skills

### Environment (3)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_status` | Reflection probe — the ONLY skill that works without the package. Reports installation, package version, `enable`, scripting backend, hot-update + AOT-patch assembly lists, and which generated artifacts exist. **Call this first.** | (none) |
| `hybridclr_install_status` | il2cpp_plus local install state via `InstallerController`: whether libil2cpp is patched, installed vs package version, expected hybridclr / il2cpp_plus branches, Unity compatibility. | (none) |
| `hybridclr_get_paths` | Resolve every input/output path for a build target — HybridCLRData root, local il2cpp dir, hot-update DLL dir, stripped AOT dir, link.xml, AOTGenericReferences.cs. Use to wire outputs into a YooAsset collector. | `buildTarget?` |

### Settings (2)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_settings_get` | Read every `HybridCLRSettings` field, plus the **resolved** assembly lists `SettingsUtil` derives from asmdef assets and raw names (this is what the pipeline actually uses). | (none) |
| `hybridclr_settings_set` | Write settings and persist to `ProjectSettings/HybridCLRSettings.asset`. Only the parameters you pass change; the full prior object is snapshotted for workflow undo. | `enable?`, `useGlobalIl2cpp?`, `hotUpdateAssemblies?`, `hotUpdateAssemblyDefinitions?`, `preserveHotUpdateAssemblies?`, `patchAOTAssemblies?`, `externalHotUpdateAssemblyDirs?`, `hotUpdateDllCompileOutputRootDir?`, `strippedAOTDllOutputRootDir?`, `outputLinkFile?`, `outputAOTGenericReferenceFile?`, `maxGenericReferenceIteration?`, `maxMethodBridgeGenericIteration?`, `hybridclrRepoURL?`, `il2cppPlusRepoURL?` |

### Diagnostics (1)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_validate_setup` | Pre-flight check: package present, `enable`, IL2CPP backend, libil2cpp patched and version-matched, hot-update assemblies configured and resolvable, `patchAOTAssemblies` populated, link.xml / AOTGenericReferences.cs present, hot-update DLLs compiled. Returns categorised `errors` (blocking) and `warnings` (advisory), each with a concrete fix. | `buildTarget?` |

### Compile & generate (3) — blocking, high risk
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_compile_dlls` | `CompileDllCommand.CompileDll(target, developmentBuild)` — compile hot-update assemblies into `HybridCLRData/HotUpdateDlls/<target>`. Prior output is backed up for workflow undo. | `buildTarget?`, `developmentBuild=false` |
| `hybridclr_generate_all` | `PrebuildCommand.GenerateAll()` — the full pipeline for the **active** build target: compile_dll → il2cpp_def → link_xml → aot_dlls → method_bridge → aot_generic_reference. Rewrites C# under `Assets/`, so a domain reload follows. | (none) |
| `hybridclr_generate_step` | Run one pipeline step: `il2cpp_def`, `link_xml`, `aot_dlls`, `method_bridge`, `aot_generic_reference`, `clean_il2cpp_cache`. Assumes hot-update DLLs are already compiled. | `step`, `buildTarget?` |

### Artifacts (3)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_get_hotupdate_dlls` | List compiled DLL artifacts with size + UTC timestamp, reconciled against the configured assembly list — surfaces `missing` and `unexpected` entries. | `buildTarget?` |
| `hybridclr_copy_hotupdate_dlls` | Stage compiled DLLs (optionally the stripped AOT DLLs too) into a directory under `Assets/`, renamed to an importable extension so a YooAsset collector can pack them. Destination files are backed up for workflow undo. | `destination`, `buildTarget?`, `extension=".bytes"`, `assemblies?`, `includeAotAssemblies=false`, `clearDestination=false` |
| `hybridclr_aot_generic_refs` | Read generated `AOTGenericReferences.cs` — timestamp, declared `PatchedAOTAssemblyList`, and an `inSync` comparison against `settings.patchAOTAssemblies` with `missingFromSettings` / `extraInSettings` diffs. | `includeContent=false` |

## HybridCLR × YooAsset workflow

The two modules split cleanly: **HybridCLR produces DLLs, YooAsset ships them.** The staging step in between is `hybridclr_copy_hotupdate_dlls`, which renames `.dll` → `.bytes` so Unity imports them as `TextAsset` (a bare `.dll` under `Assets/` would be treated as a managed plugin).

**One-time setup** (per project, and again after any AOT-side code change):

```python
u.call_skill("hybridclr_settings_set",
    enable=True,
    hotUpdateAssemblyDefinitions=["Assets/HotUpdate/HotUpdate.asmdef"])
u.call_skill("hybridclr_validate_setup")     # fix every `errors` entry before continuing
u.call_skill("hybridclr_generate_all")       # minutes; domain reload follows
refs = u.call_skill("hybridclr_aot_generic_refs")
u.call_skill("hybridclr_settings_set", patchAOTAssemblies=refs["patchedAOTAssemblyList"])
# then build the player normally (build_player / unity-cli)
```

**Iterative hot-update loop** (every time hot-update C# changes — no `generate_all`, no player rebuild):

```python
# 1. edit hot-update C# (script_* skills) — must live in a hotUpdateAssemblyDefinitions asmdef
# 2. compile
u.call_skill("hybridclr_compile_dlls", buildTarget="Android")
u.call_skill("hybridclr_get_hotupdate_dlls", buildTarget="Android")   # verify `missing` is empty

# 3. stage into the YooAsset collector directory
u.call_skill("hybridclr_copy_hotupdate_dlls",
    destination="Assets/HotUpdateDlls",
    buildTarget="Android",
    extension=".bytes",
    includeAotAssemblies=True,      # ships the stripped AOT metadata DLLs too
    clearDestination=True)

# 4. pack (yooasset module) — the collector only needs adding once
u.call_skill("yooasset_add_collector",
    packageName="DefaultPackage", groupName="HotUpdateCode",
    collectPath="Assets/HotUpdateDlls",
    addressRule="AddressByFileName", packRule="PackDirectory")
u.call_skill("yooasset_build_bundles",
    packageName="DefaultPackage", buildTarget="Android", packageVersion="auto")
```

**Where the boundary sits**: runtime loading is game code you write, not a skill. Load the `.bytes` `TextAsset` through YooAsset, then `RuntimeApi.LoadMetadataForAOTAssembly(aotBytes, HomologousImageMode.SuperSet)` for each AOT metadata DLL **before** `Assembly.Load(hotUpdateBytes)`. Order matters. See [yooasset-design](../yooasset-design/SKILL.md) for the handle-lifecycle rules.

**When to re-run `generate_all`**: only when AOT-side code changes (anything outside the hot-update asmdefs), when you add/remove a hot-update assembly, or before shipping a new player build. Generic instantiations reachable only from new hot-update code also require it — that is what `AOTGenericReferences.cs` records.

## Critical Rules (must read)

1. **`hybridclr_status` is the only skill that works without the package.** Every other skill returns `MISSING_PACKAGE`. Unlike the YooAsset module there is no compile define — detection is pure reflection, so no recompile is needed after installing.
2. **`hybridclr_compile_dlls`, `hybridclr_generate_all` and `hybridclr_generate_step` block the Editor main thread.** UnitySkills runs all skills on the main thread through a single queue, so `/health` and `/jobs` also stall for the duration. Raise the client timeout to at least 10 minutes; a socket timeout does **not** cancel the operation.
3. **`generate_all` and `generate_step step="aot_dlls"` run a scripts-only `BuildPipeline.BuildPlayer` internally** into `HybridCLRData/StrippedAOTDllsTempProj/<target>`. Both refuse to start when `BuildPipeline.isBuildingPlayer` is true. The step temporarily flips `EditorUserBuildSettings.buildScriptsOnly` and the platform "export project" flags, restoring them in a `finally` — an Editor crash mid-step can leave them changed.
4. **`patchAOTAssemblies` must match the generated `PatchedAOTAssemblyList`.** Run `hybridclr_aot_generic_refs` after any generation and copy `missingFromSettings` into `hybridclr_settings_set(patchAOTAssemblies=[...])`. `inSync=false` means generic instantiations will throw at runtime. The generated list carries `.dll` suffixes and the setting does not; the skill compares on the stem, so pass either form.
5. **Array parameters replace, they do not merge.** `hybridclr_settings_set(hotUpdateAssemblies=["A"])` discards any previously configured names. Read with `hybridclr_settings_get` first and pass the full intended array.
6. **`HybridCLRSettings` is not an AssetDatabase asset.** It is a `ScriptableObject` serialized to `ProjectSettings/HybridCLRSettings.asset` via `InternalEditorUtility.SaveToSerializedFileAndForget`, so it will not appear in `asset_*` skills, and Unity's undo stack cannot revert the file — only the workflow snapshot can.
7. **Path fields use two different bases.** `outputLinkFile` and `outputAOTGenericReferenceFile` are relative to `Assets/`; `hotUpdateDllCompileOutputRootDir` and `strippedAOTDllOutputRootDir` are relative to the project directory. `hybridclr_get_paths` returns both the raw and absolute forms.
8. **The upstream field is misspelled `externalHotUpdateAssembliyDirs`.** The skill parameter is spelled correctly (`externalHotUpdateAssemblyDirs`) and maps to it; `hybridclr_settings_get` returns the upstream spelling under `settings`.
9. **Hot-update code must live in an asmdef listed in `hotUpdateAssemblyDefinitions`** (or a name in `hotUpdateAssemblies`). Code outside those assemblies is AOT and cannot be hot-updated — changing it requires a new player build.
10. **`hybridclr_validate_setup` distinguishes `errors` from `warnings`.** Errors block hot update entirely (no IL2CPP, libil2cpp unpatched, no hot-update assemblies). Warnings are staleness signals (DLLs not compiled yet, `patchAOTAssemblies` empty) that are normal early in setup.

## Limitations

- **No install skill.** `InstallerController.InstallDefaultHybridCLR()` is reachable by reflection but deliberately not exposed: it clones two git repos over the network and copies the editor's entire il2cpp tree, with no progress reporting or cancellation, and would hold the main-thread queue for the whole time. Use the **HybridCLR/Installer...** window, or unity-cli in batch mode.
- **Synchronous execution.** These operations are not `AsyncJobService` jobs — HybridCLR's commands are synchronous main-thread Unity APIs with no step boundaries to pump, so there is no `jobId` to poll. Treat them as long blocking calls.
- **Workflow undo is partial for generation.** `hybridclr_settings_set` restores fully. `hybridclr_compile_dlls` and `hybridclr_copy_hotupdate_dlls` restore the affected directory from a file backup under `Library/UnitySkills/HybridCLRBackups/`. `hybridclr_generate_all` and `hybridclr_generate_step` restore only the two Assets-side artifacts (`link.xml`, `AOTGenericReferences.cs`) plus the hot-update DLL directory — intermediates under `HybridCLRData/` and generated C++ under `LocalIl2CppData-*/` are rebuildable and deliberately out of scope. Re-run the pipeline rather than relying on undo for those.
- **Backups keep 5 generations per label.** Older undo steps for `compile_dlls` / `copy_hotupdate_dlls` degrade to "added files removed, previous files not restored". `Library/` is not version-controlled, so backups do not survive a Library wipe.
- **No control over build processors.** HybridCLR's `IPreprocessBuildWithReport` / `IPostprocessBuildWithReport` hooks (`CheckSettings`, `CopyStrippedAOTAssemblies`, `PatchScriptingAssemblyList`, the Xcode source injectors) run automatically during the player build and are not configurable from this module.
- **Version drift is reported, not guessed.** Reflection targets are anchored to 8.12.0. If a member cannot be resolved the skill returns `MISSING_PACKAGE` naming it, rather than silently doing nothing.
- **`hybridclr_aot_generic_refs` parses generated text, not IL.** `patchedAOTAssemblyList` is read exactly from the `PatchedAOTAssemblyList` initializer, but `genericTypeCount` counts comment lines in the generator's `// {{ AOT generic types` block and is an approximation. Pass `includeContent=true` when you need the file verbatim.

### unity-cli fallback

Every generation entry point is a `[MenuItem]`-annotated public static parameterless method, so anything this module cannot do is reachable through `-executeMethod` in batch mode:

| Menu | `-executeMethod` target |
|------|-------------------------|
| HybridCLR/Generate/All | `HybridCLR.Editor.Commands.PrebuildCommand.GenerateAll` |
| HybridCLR/CompileDll/ActiveBuildTarget | `HybridCLR.Editor.Commands.CompileDllCommand.CompileDllActiveBuildTarget` |
| HybridCLR/Generate/LinkXml | `HybridCLR.Editor.Commands.LinkGeneratorCommand.GenerateLinkXml` |
| HybridCLR/Generate/Il2CppDef | `HybridCLR.Editor.Commands.Il2CppDefGeneratorCommand.GenerateIl2CppDef` |
| HybridCLR/Generate/AOTDlls | `HybridCLR.Editor.Commands.StripAOTDllCommand.GenerateStripedAOTDlls` |
| HybridCLR/Generate/MethodBridgeAndReversePInvokeWrapper | `HybridCLR.Editor.Commands.MethodBridgeGeneratorCommand.GenerateMethodBridgeAndReversePInvokeWrapper` |
| HybridCLR/Generate/AOTGenericReference | `HybridCLR.Editor.Commands.AOTReferenceGeneratorCommand.CompileAndGenerateAOTGenericReference` |

Running these headless avoids the main-thread blocking problem entirely — see [unity-cli](../unity-cli/SKILL.md). Installation has no parameterless menu method; it must go through the Installer window.

## Reflection Anchors

Every skill resolves these against the `HybridCLR.Editor` assembly. Verified against hybridclr_unity 8.12.0 source.

| Skill | Reflected target |
|-------|------------------|
| `hybridclr_status` | `SettingsUtil.Enable`, `SettingsUtil.HotUpdateAssemblyNamesExcludePreserved`, `SettingsUtil.AOTAssemblyNames`, `Installer.InstallerController` |
| `hybridclr_install_status` | `Installer.InstallerController` — `HasInstalledHybridCLR()`, `PackageVersion`, `InstalledLibil2cppVersion`, `MajorVersion`, `GetCompatibleType()`, `GetCurrentUnityVersionMinCompatibleVersionStr()`, `HybridclrLocalVersion`, `Il2cppPlusLocalVersion`, `LocalVersionFile` |
| `hybridclr_get_paths` | `SettingsUtil.{ProjectDir, HybridCLRDataDir, LocalUnityDataDir, LocalIl2CppDir, GeneratedCppDir, HotUpdateDllsRootOutputDir}`, `GetHotUpdateDllsOutputDirByTarget(BuildTarget)`, `GetAssembliesPostIl2CppStripDir(BuildTarget)` |
| `hybridclr_settings_get` / `_set` | `Settings.HybridCLRSettings.Instance` public fields + `HybridCLRSettings.Save()`; resolved lists via `SettingsUtil.HotUpdateAssemblyNames{Exclude,Include}Preserved` and `HotUpdateAssemblyFilesIncludePreserved` |
| `hybridclr_compile_dlls` | `Commands.CompileDllCommand.CompileDll(BuildTarget, bool)` |
| `hybridclr_generate_all` | `Commands.PrebuildCommand.GenerateAll()` |
| `hybridclr_generate_step` | `Commands.Il2CppDefGeneratorCommand.GenerateIl2CppDef()`, `Commands.LinkGeneratorCommand.GenerateLinkXml(BuildTarget)`, `Commands.StripAOTDllCommand.GenerateStripedAOTDlls(BuildTarget)`, `Commands.MethodBridgeGeneratorCommand.GenerateMethodBridgeAndReversePInvokeWrapper(BuildTarget)` and `.CleanIl2CppBuildCache()`, `Commands.AOTReferenceGeneratorCommand.GenerateAOTGenericReference(BuildTarget)` |
| `hybridclr_get_hotupdate_dlls` / `_copy_hotupdate_dlls` | `SettingsUtil.GetHotUpdateDllsOutputDirByTarget`, `GetAssembliesPostIl2CppStripDir`, `HotUpdateAssemblyFilesIncludePreserved`, `AOTAssemblyNames` |
| `hybridclr_aot_generic_refs` | `HybridCLRSettings.outputAOTGenericReferenceFile`; output shape from `AOT.GenericReferenceWriter.Write` |

Note `SettingsUtil.HotUpdateAssemblyNamesIncludePreserved` **throws** when a name appears in both `hotUpdateAssemblies` and `preserveHotUpdateAssemblies`. The skills catch it and surface the message in `resolveErrors` / `hotUpdateAssemblyResolveError` rather than failing the call.

## Version Scope

- **Target**: `com.code-philosophy.hybridclr` **8.12.0**. All type names, member names and signatures above are taken from that Editor source.
- **Minimum Unity**: HybridCLR itself supports 2019.4.0+ / 2020.3.0+ / 2021.3.0+ / 2022.3.0+ / 2023.2.0+ / 6000.0.0+ (6000.3.0+ on the 6000.3 branch); `hybridclr_install_status` reports the exact minimum for the running editor. This Skill module is verified against Unity 2022.3 and 6000.3.
- **IL2CPP only.** Mono builds cannot use HybridCLR; `hybridclr_validate_setup` reports this as a blocking error.
- Older/newer package versions still work for anything whose members resolve; unresolved members are reported by name, not silently skipped.

## Exact Signatures

For authoritative parameter names, defaults, and return fields, query `GET /skills/schema?category=HybridCLR` or `unity_skills.get_skill_schema()`. This document is a routing / best-practice guide, not the signature source.
