---
name: unity-hybridclr
description: Automate HybridCLR C# hot-update prebuild
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may call it directly.

## Triggers
- Configuring hot-update assemblies
- Compiling hot-update DLLs
- Generating AOTGenericReferences/link.xml
- Diagnosing HybridCLR setup
- Wiring output into YooAsset builds
- 配置热更程序集、编译热更 DLL、生成 AOTGenericReferences/link.xml、排查 HybridCLR 环境、把产物接进 YooAsset 打包链路

# Unity HybridCLR Skills

Editor-side automation for [HybridCLR](https://hybridclr.doc.code-philosophy.com/) — the zero-cost native C# hot-update solution for IL2CPP. Covers settings CRUD, installation probing, hot-update assembly compilation, the prebuild generation pipeline, and artifact staging for a YooAsset bundle build.

**This module holds zero direct references to the package.** Every call resolves through reflection against the `HybridCLR.Editor` assembly, so the UnitySkills Editor assembly compiles identically whether or not HybridCLR is installed — there is no scripting define to set and no recompile needed after installing.

**Two API lines, one module.** `hybridclr_unity` 8.x and 9.x expose the same editor assembly but differ in namespaces, settings fields and paths. The module detects the line (`apiLine` in `hybridclr_status` / `hybridclr_install_status`) and adapts; the skill names and parameters are the same on both. See [Version Scope](#version-scope) for what differs.

> **Requires**: `com.code-philosophy.hybridclr` **>=8.12.0 <10.0.0** (reflection anchors: **8.12.0** for the 8.x line, **9.0.3** for the 9.x line), IL2CPP scripting backend. Unity support follows the installed release: 8.x covers 2019.4+ through 6000.x, 9.x only 2022.3, 6000.0 and 6000.3 (`hybridclr_install_status` reports `supportedUnityVersions` and `unityVersionSupported`).
> **Companion modules**: [yooasset](../yooasset/SKILL.md) for shipping the compiled DLLs as bundles, [yooasset-design](../yooasset-design/SKILL.md) for the runtime loading contract, [asmdef](../asmdef/SKILL.md) for assembly-boundary design.

## Guardrails

**Operating Mode** (v1.9 three-tier):
- **Approval** (default): the seven read-only skills — `hybridclr_status`, `hybridclr_install_status`, `hybridclr_get_paths`, `hybridclr_settings_get`, `hybridclr_validate_setup`, `hybridclr_get_hotupdate_dlls`, `hybridclr_aot_generic_refs` — are `SemiAuto` and run directly. `hybridclr_settings_set` and `hybridclr_copy_hotupdate_dlls` are FullAuto: on `MODE_RESTRICTED`, run the grant protocol.
- **Auto-forbidden** (NeverInSemi, `RiskLevel="high"`): `hybridclr_compile_dlls`, `hybridclr_generate_all`, `hybridclr_generate_step`. Reachable only under Bypass mode or via a user-managed Allowlist entry; the grant flow returns `MODE_FORBIDDEN`. This is deliberate — all three block the Editor main thread for minutes.
- When the package is missing, every skill except `hybridclr_status` returns a `MISSING_PACKAGE` error with install instructions. When it is installed outside `>=8.12.0 <10.0.0`, every skill (status included) returns `SKILL_ERROR` with `reason: "unsupported_package_version"`, `installedVersion` and `supportedRange`, and an `ask_user` fix: changing the package version is the user's decision, do not do it yourself. When the package is in range but a reflected member cannot be resolved (a later release moved it), the skill returns `SKILL_ERROR` naming the exact member and the `apiLine`; it is never `MISSING_PACKAGE`, and installing again will not help.

**DO NOT** (common hallucinations):
- `hybridclr_install` / `hybridclr_init` — do NOT exist. Installing il2cpp_plus clones two git repos and copies the entire editor il2cpp tree; it is a multi-minute network operation with no cancellation and is intentionally not a skill. Run it from **HybridCLR/Installer...** in the Editor, or via unity-cli (see Limitations). `hybridclr_install_status` only *reports* the state.
- `hybridclr_build_player` — NOT in this module. Building the player is `build_player` (Project module) or unity-cli. HybridCLR hooks the build via its own `IPreprocessBuildWithReport` processors; nothing extra is needed from this module.
- `hybridclr_load_metadata` / `hybridclr_load_assembly` — runtime APIs (`HybridCLR.RuntimeApi.LoadMetadataForAOTAssembly`, `System.Reflection.Assembly.Load`) belong in game code, not the Editor REST surface. Write them yourself.
- Do NOT call `hybridclr_generate_all` before every bundle rebuild — see the workflow section below. It is a **pre-player-build** step, not a per-hot-update step.
- Do NOT pass `extension=".dll"` to `hybridclr_copy_hotupdate_dlls`. Unity would treat the staged files as managed plugins and try to load them into the Editor domain; the skill rejects it.
- Do NOT pass `patchAOTAssemblies`, `hotUpdateDllCompileOutputRootDir` or `strippedAOTDllOutputRootDir` to `hybridclr_settings_set` on 9.x, and do not run `generate_step clean_il2cpp_cache` there (or `method_bridge_empty` on 8.x). That line has no such field or step: the call is rejected with `SEMANTIC_INVALID` and nothing is written.

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
| `hybridclr_status` | Reflection probe — the ONLY skill that works without the package. Reports installation, package version, `apiLine` (`8.x` / `9.x`), `enable`, scripting backend, hot-update assembly list, the AOT-patch list (`patchAOTAssemblies` on 8.x, null on 9.x) and the generated `patchedAOTAssemblyList`, `installerError`, and which generated artifacts exist. **Call this first.** | (none) |
| `hybridclr_install_status` | il2cpp_plus local install state via `InstallerController`: whether libil2cpp is patched, installed vs package version, expected hybridclr / il2cpp_plus branches (tags on 9.x), `supportedUnityVersions` from the package's `Data~/hybridclr_version.json`, `unityVersionSupported`. A release that has no entry for the running Unity version (the installer throws `NotSupportedException`) is reported as `unityVersionSupported=false` with `installerError`, not as "not installed". | (none) |
| `hybridclr_get_paths` | Resolve every input/output path for a build target — HybridCLR data root, local il2cpp dir, hot-update DLL dir, stripped AOT dir, link.xml, AOTGenericReferences.cs. Use to wire outputs into a YooAsset collector. | `buildTarget?` |

### Settings (2)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_settings_get` | Read every `HybridCLRSettings` field, plus the **resolved** assembly lists `SettingsUtil` derives from asmdef assets and raw names (this is what the pipeline actually uses). Fields the installed line no longer declares read as `null` and are listed in `unsupportedFields`; 9.x adds `linkPreserveLevel`. | (none) |
| `hybridclr_settings_set` | Write settings and persist to `ProjectSettings/HybridCLRSettings.asset`. Only the parameters you pass change; the full prior object is snapshotted for workflow undo. On 9.x, passing `patchAOTAssemblies`, `hotUpdateDllCompileOutputRootDir` or `strippedAOTDllOutputRootDir` returns `SEMANTIC_INVALID` with the replacement, and nothing is written. | `enable?`, `useGlobalIl2cpp?`, `hotUpdateAssemblies?`, `hotUpdateAssemblyDefinitions?`, `preserveHotUpdateAssemblies?`, `patchAOTAssemblies?`, `externalHotUpdateAssemblyDirs?`, `hotUpdateDllCompileOutputRootDir?`, `strippedAOTDllOutputRootDir?`, `outputLinkFile?`, `outputAOTGenericReferenceFile?`, `maxGenericReferenceIteration?`, `maxMethodBridgeGenericIteration?`, `hybridclrRepoURL?`, `il2cppPlusRepoURL?` |

### Diagnostics (1)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_validate_setup` | Pre-flight check: package present, `enable`, IL2CPP backend, Unity version supported by the release, libil2cpp patched and version-matched, hot-update assemblies configured and resolvable, `patchAOTAssemblies` populated (8.x only), link.xml / AOTGenericReferences.cs present, hot-update DLLs compiled. Returns categorised `errors` (blocking) and `warnings` (advisory), each with a concrete fix. | `buildTarget?` |

### Compile & generate (3) — blocking, high risk
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_compile_dlls` | `CompileDllCommand.CompileDll(target, developmentBuild)` — compile hot-update assemblies into the per-target hot-update directory (`hybridclr_get_paths`). Prior output is backed up for workflow undo. | `buildTarget?`, `developmentBuild=false` |
| `hybridclr_generate_all` | `PrebuildCommand.GenerateAll()` — the full pipeline for the **active** build target: compile_dll → il2cpp_def → link_xml → (9.0.2+: method_bridge_empty →) aot_dlls → method_bridge → aot_generic_reference. The result's `steps` lists what the installed release runs. Rewrites C# under `Assets/`, so a domain reload follows. | (none) |
| `hybridclr_generate_step` | Run one pipeline step: `il2cpp_def`, `link_xml`, `aot_dlls`, `method_bridge`, `aot_generic_reference`, plus `clean_il2cpp_cache` (**8.x only**) or `method_bridge_empty` (**9.0.2+ only**). A step of the other line, or an unknown one, returns `SEMANTIC_INVALID` with the `validValues` of the installed line. Assumes hot-update DLLs are already compiled. | `step`, `buildTarget?` |

### Artifacts (3)
| Skill | Purpose | Key Parameters |
|-------|---------|----------------|
| `hybridclr_get_hotupdate_dlls` | List compiled DLL artifacts with size + UTC timestamp, reconciled against the configured assembly list — surfaces `missing` and `unexpected` entries. | `buildTarget?` |
| `hybridclr_copy_hotupdate_dlls` | Stage compiled DLLs (optionally the stripped AOT DLLs too) into a directory under `Assets/`, renamed to an importable extension so a YooAsset collector can pack them. Destination files are backed up for workflow undo. With `includeAotAssemblies`, the AOT DLLs come from `patchAOTAssemblies` on 8.x and from the generated `PatchedAOTAssemblyList` on 9.x; if that list does not exist yet the call fails and says to run `hybridclr_generate_all` — it never reports success while copying no AOT DLL silently. The result's `aotAssemblies` shows `source`, `requested` and `staged`. | `destination`, `buildTarget?`, `extension=".bytes"`, `assemblies?`, `includeAotAssemblies=false`, `clearDestination=false` |
| `hybridclr_aot_generic_refs` | Read generated `AOTGenericReferences.cs` — timestamp and declared `PatchedAOTAssemblyList`. On 8.x also an `inSync` comparison against `settings.patchAOTAssemblies` with `missingFromSettings` / `extraInSettings` diffs; on 9.x those comparison fields are null (the setting is gone and the list is the source of truth). | `includeContent=false` |

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
# 8.x only: record the generated list in the settings. 9.x has no such setting:
# PatchedAOTAssemblyList itself drives hybridclr_copy_hotupdate_dlls(includeAotAssemblies=True).
if u.call_skill("hybridclr_status")["apiLine"] == "8.x":
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

**Freeze the AOT metadata DLLs per shipped player.** Stripped AOT DLLs are per build target, and a fresh `Generate` output can differ slightly from the DLLs of the player already in users' hands (this matters for `HomologousImageMode.Consistent`; `SuperSet` tolerates it). Stage them again with `includeAotAssemblies=True` when you ship a new player, not on every hot update.

## Critical Rules (must read)

1. **`hybridclr_status` is the only skill that works without the package.** Every other skill returns `MISSING_PACKAGE`. Unlike the YooAsset module there is no compile define — detection is pure reflection, so no recompile is needed after installing.
2. **`hybridclr_compile_dlls`, `hybridclr_generate_all` and `hybridclr_generate_step` block the Editor main thread.** UnitySkills runs all skills on the main thread through a single queue, so `/health` and `/jobs` also stall for the duration. Raise the client timeout to at least 10 minutes; a socket timeout does **not** cancel the operation.
3. **`generate_all` and `generate_step step="aot_dlls"` run a scripts-only `BuildPipeline.BuildPlayer` internally** into a temp project (`HybridCLRData/StrippedAOTDllsTempProj/<target>` on 8.x, `Temp/HybridCLR/StrippedAOTDllsTempProj/<target>` on 9.x). Both refuse to start when `BuildPipeline.isBuildingPlayer` is true. The step temporarily flips `EditorUserBuildSettings.buildScriptsOnly` and the platform "export project" flags, restoring them in a `finally` — an Editor crash mid-step can leave them changed.
4. **The AOT metadata list is `PatchedAOTAssemblyList`.** On 8.x, `patchAOTAssemblies` must match it: run `hybridclr_aot_generic_refs` after any generation and copy `missingFromSettings` into `hybridclr_settings_set(patchAOTAssemblies=[...])`; `inSync=false` means generic instantiations will throw at runtime. The generated list carries `.dll` suffixes and the setting does not; the skill compares on the stem, so pass either form. On 9.x the setting is gone: load the generated list's DLLs at runtime and stage them with `hybridclr_copy_hotupdate_dlls(includeAotAssemblies=true)`. Upstream notes the list is conservative — load all of it when memory allows, otherwise start with `mscorlib` and add a DLL when a new AOT generic error appears.
5. **Array parameters replace, they do not merge.** `hybridclr_settings_set(hotUpdateAssemblies=["A"])` discards any previously configured names. Read with `hybridclr_settings_get` first and pass the full intended array.
6. **`HybridCLRSettings` is not an AssetDatabase asset.** It is serialized to `ProjectSettings/HybridCLRSettings.asset` (8.x: `InternalEditorUtility.SaveToSerializedFileAndForget`; 9.x: a `ScriptableSingleton` with a `[FilePath]`), so it will not appear in `asset_*` skills, and Unity's undo stack cannot revert the file — only the workflow snapshot can. The snapshot covers exactly the fields the installed line declares, including `linkPreserveLevel` on 9.x.
7. **Path fields use two different bases.** `outputLinkFile` and `outputAOTGenericReferenceFile` are relative to `Assets/` (default `HybridCLRGenerate/...` on 8.x, `HybridCLR/Generate/...` on 9.x). On 8.x, `hotUpdateDllCompileOutputRootDir` and `strippedAOTDllOutputRootDir` are relative to the project directory; 9.x has no such settings and fixes both output roots under the HybridCLR data dir. `hybridclr_get_paths` returns the resolved absolute paths on both lines, so never hardcode them.
8. **The upstream field is misspelled `externalHotUpdateAssembliyDirs`.** The skill parameter is spelled correctly (`externalHotUpdateAssemblyDirs`) and maps to it; `hybridclr_settings_get` returns the upstream spelling under `settings`.
9. **Hot-update code must live in an asmdef listed in `hotUpdateAssemblyDefinitions`** (or a name in `hotUpdateAssemblies`). Code outside those assemblies is AOT and cannot be hot-updated — changing it requires a new player build.
10. **`hybridclr_validate_setup` distinguishes `errors` from `warnings`.** Errors block hot update entirely (no IL2CPP, unsupported Unity version, libil2cpp unpatched, no hot-update assemblies). Warnings are staleness signals (DLLs not compiled yet, `patchAOTAssemblies` empty on 8.x) that are normal early in setup.

## Limitations

- **No install skill.** `InstallerController.InstallDefaultHybridCLR()` is reachable by reflection but deliberately not exposed: it clones two git repos over the network and copies the editor's entire il2cpp tree, with no progress reporting or cancellation, and would hold the main-thread queue for the whole time. Use the **HybridCLR/Installer...** window, or unity-cli in batch mode.
- **Synchronous execution.** These operations are not `AsyncJobService` jobs — HybridCLR's commands are synchronous main-thread Unity APIs with no step boundaries to pump, so there is no `jobId` to poll. Treat them as long blocking calls.
- **Workflow undo is partial for generation.** `hybridclr_settings_set` restores fully. `hybridclr_compile_dlls` and `hybridclr_copy_hotupdate_dlls` restore the affected directory from a file backup under `Library/UnitySkills/HybridCLRBackups/`. `hybridclr_generate_all` and `hybridclr_generate_step` restore only the two Assets-side artifacts (`link.xml`, `AOTGenericReferences.cs`) plus the hot-update DLL directory — intermediates under the HybridCLR data dir (`HybridCLRData/` on 8.x, `Library/HybridCLR` on 9.x), including the generated C++ in the local il2cpp copy, are rebuildable and deliberately out of scope. Re-run the pipeline rather than relying on undo for those.
- **Backups keep 5 generations per label.** Older undo steps for `compile_dlls` / `copy_hotupdate_dlls` degrade to "added files removed, previous files not restored". `Library/` is not version-controlled, so backups do not survive a Library wipe.
- **No control over build processors.** HybridCLR's `IPreprocessBuildWithReport` / `IPostprocessBuildWithReport` hooks (`CheckSettings`, `CopyStrippedAOTAssemblies`, `PatchScriptingAssemblyList`, the Xcode source injectors) run automatically during the player build and are not configurable from this module.
- **Version drift is reported, not guessed.** Reflection targets are anchored to 8.12.0 (8.x) and 9.0.3 (9.x). A release outside `>=8.12.0 <10.0.0` is refused with `unsupported_package_version`; inside the range, a member that cannot be resolved returns `SKILL_ERROR` naming it, rather than silently doing nothing.
- **Constructing `InstallerController` logs a line.** Upstream 9.x prints its matched version entry to the Console each time the controller is created, so `hybridclr_status` / `hybridclr_install_status` leave one upstream log line per call.
- **`hybridclr_aot_generic_refs` parses generated text, not IL.** `patchedAOTAssemblyList` is read exactly from the `PatchedAOTAssemblyList` initializer, but `genericTypeCount` counts comment lines in the generator's `// {{ AOT generic types` block and is an approximation. Pass `includeContent=true` when you need the file verbatim.

### unity-cli fallback

Every generation entry point is a `[MenuItem]`-annotated public static parameterless method, so anything this module cannot do is reachable through `-executeMethod` in batch mode. The class names are the same on both lines; only the namespace root differs — `HybridCLR.Editor.Commands.*` on 8.x, `HybridCLR.Commands.*` on 9.x:

| Menu | Class.method (under the namespace root above) |
|------|-----------------------------------------------|
| HybridCLR/Generate/All | `PrebuildCommand.GenerateAll` |
| HybridCLR/CompileDll/ActiveBuildTarget | `CompileDllCommand.CompileDllActiveBuildTarget` |
| HybridCLR/Generate/LinkXml | `LinkGeneratorCommand.GenerateLinkXml` |
| HybridCLR/Generate/Il2CppDef | `Il2CppDefGeneratorCommand.GenerateIl2CppDef` |
| HybridCLR/Generate/AOTDlls (8.x) · HybridCLR/Generate/StrippedAotDlls (9.x) | `StripAOTDllCommand.GenerateStripedAOTDlls` |
| HybridCLR/Generate/MethodBridgeAndReversePInvokeWrapper | `MethodBridgeGeneratorCommand.GenerateMethodBridgeAndReversePInvokeWrapper` |
| HybridCLR/Generate/AOTGenericReference | `AOTReferenceGeneratorCommand.CompileAndGenerateAOTGenericReference` |

Running these headless avoids the main-thread blocking problem entirely — see [unity-cli](../unity-cli/SKILL.md). Installation has no parameterless menu method; it must go through the Installer window.

## Reflection Anchors

Every skill resolves these against the `HybridCLR.Editor` assembly, trying the namespace root `HybridCLR.` first (9.x) and `HybridCLR.Editor.` second (8.x). Names below are relative to that root, e.g. `SettingsUtil` is `HybridCLR.SettingsUtil` on 9.x and `HybridCLR.Editor.SettingsUtil` on 8.x. Verified against hybridclr_unity 8.12.0 (8.x; the Editor sources of 8.15.0 differ only in `InstallerController`) and 9.0.3 (9.x; 9.0.0 differs only in `GenerateEmptyMethodBridgeAndReversePInvokeWrapper`).

| Skill | Reflected target |
|-------|------------------|
| any (line probe) | `apiLine` = `9.x` when `HybridCLR.SettingsUtil` resolves, `8.x` when `HybridCLR.Editor.SettingsUtil` does |
| `hybridclr_status` | `SettingsUtil.Enable`, `SettingsUtil.HotUpdateAssemblyNamesExcludePreserved`, `SettingsUtil.AOTAssemblyNames` (**8.x only**), `Installer.InstallerController` |
| `hybridclr_install_status` | `Installer.InstallerController` — `HasInstalledHybridCLR()`, `PackageVersion`, `InstalledLibil2cppVersion`, `HybridclrLocalVersion`, `Il2cppPlusLocalVersion`, `LocalVersionFile`; `MajorVersion` (present in 8.12.0 and 9.x, absent in 8.15.0; `unityMajorVersion` falls back to the editor's own major version); **8.x only**: `GetCompatibleType()` (on 9.x `compatibility` is derived from `unityVersionSupported`), `GetCurrentUnityVersionMinCompatibleVersionStr()`. The constructor throws `NotSupportedException` on a Unity version with no manifest entry (9.x, 8.15.0+). Supported Unity versions are read from the package's `Data~/hybridclr_version.json` |
| `hybridclr_get_paths` | `SettingsUtil.{ProjectDir, HybridCLRDataDir, LocalUnityDataDir, LocalIl2CppDir, GeneratedCppDir}`, `GetHotUpdateDllsOutputDirByTarget(BuildTarget)`, `GetAssembliesPostIl2CppStripDir(BuildTarget)`; **8.x only**: `HotUpdateDllsRootOutputDir` |
| `hybridclr_settings_get` / `_set` | `SettingsUtil.HybridCLRSettings` (the settings object on both lines) public fields + the static `Settings.HybridCLRSettings.Save()`; resolved lists via `SettingsUtil.HotUpdateAssemblyNames{Exclude,Include}Preserved` and `HotUpdateAssemblyFilesIncludePreserved`. Removed on 9.x: `patchAOTAssemblies`, `hotUpdateDllCompileOutputRootDir`, `strippedAOTDllOutputRootDir`; new on 9.x: `linkPreserveLevel` |
| `hybridclr_compile_dlls` | `Commands.CompileDllCommand.CompileDll(BuildTarget, bool)` |
| `hybridclr_generate_all` | `Commands.PrebuildCommand.GenerateAll()` |
| `hybridclr_generate_step` | `Commands.Il2CppDefGeneratorCommand.GenerateIl2CppDef()`, `Commands.LinkGeneratorCommand.GenerateLinkXml(BuildTarget)`, `Commands.StripAOTDllCommand.GenerateStripedAOTDlls(BuildTarget)`, `Commands.MethodBridgeGeneratorCommand.GenerateMethodBridgeAndReversePInvokeWrapper(BuildTarget)`, `Commands.AOTReferenceGeneratorCommand.GenerateAOTGenericReference(BuildTarget)`; **8.x only**: `Commands.MethodBridgeGeneratorCommand.CleanIl2CppBuildCache()`; **9.0.2+ only**: `Commands.MethodBridgeGeneratorCommand.GenerateEmptyMethodBridgeAndReversePInvokeWrapper(BuildTarget)` |
| `hybridclr_get_hotupdate_dlls` / `_copy_hotupdate_dlls` | `SettingsUtil.GetHotUpdateDllsOutputDirByTarget`, `GetAssembliesPostIl2CppStripDir`, `HotUpdateAssemblyFilesIncludePreserved`; **8.x only**: `AOTAssemblyNames` (on 9.x the generated `PatchedAOTAssemblyList` is parsed instead) |
| `hybridclr_aot_generic_refs` | `HybridCLRSettings.outputAOTGenericReferenceFile`; the `PatchedAOTAssemblyList` initializer and `// {{ AOT generic types` block of the generated file |

Note `SettingsUtil.HotUpdateAssemblyNamesIncludePreserved` **throws** when a name appears in both `hotUpdateAssemblies` and `preserveHotUpdateAssemblies`. The skills catch it and surface the message in `resolveErrors` / `hotUpdateAssemblyResolveError` rather than failing the call.

## Version Scope

- **Supported range**: `com.code-philosophy.hybridclr` `>=8.12.0 <10.0.0`. Anything else returns `unsupported_package_version` (see Guardrails).
- **8.x line** (anchored to **8.12.0**; 8.13–8.15 work the same for everything this module touches). **9.x line** (anchored to **9.0.3**; 9.0.0–9.0.3 are the same for everything this module touches, except that `method_bridge_empty` appeared in 9.0.2).

| Area | 8.x | 9.x |
|------|-----|-----|
| Type namespace | `HybridCLR.Editor.*` | `HybridCLR.*` (assembly still `HybridCLR.Editor`) |
| Settings object | `HybridCLRSettings.Instance` (reached through `SettingsUtil.HybridCLRSettings`) | `ScriptableSingleton` (same `SettingsUtil.HybridCLRSettings` entry point) |
| Settings fields | includes `patchAOTAssemblies`, `hotUpdateDllCompileOutputRootDir`, `strippedAOTDllOutputRootDir` | those three removed; `linkPreserveLevel` added (`PreserveAllOfDeclaringType` / `PreserveExactMember`) |
| Data dir | `<project>/HybridCLRData` | `<project>/Library/HybridCLR` |
| Hot-update DLLs | `<root setting>/<target>`, default `HybridCLRData/HotUpdateDlls` | fixed: `Library/HybridCLR/HotUpdateDlls/<target>` |
| Stripped AOT DLLs | `<root setting>/<target>`, default `HybridCLRData/AssembliesPostIl2CppStrip` | fixed: `Library/HybridCLR/AssembliesPostIl2CppStrip/<target>` |
| AOT metadata list | `patchAOTAssemblies` setting, checked against `PatchedAOTAssemblyList` | generated `PatchedAOTAssemblyList` only |
| Pipeline | `clean_il2cpp_cache` step exists | `method_bridge_empty` step (9.0.2+), no `clean_il2cpp_cache` |
| Installer manifest | `Data~/hybridclr_version.json`, key `branch`, matches by major version (8.15.0+: highest entry not above the editor) | same file, key `tag`, matches major **and** minor exactly |
| Unity versions | 2019.4+ through 6000.x (the manifest of 8.15.0 lists 2019, 2020, 2021, 2022, 2022-tuanjie, 2023, 6000, 6000.3.x, 6000.5.x) | manifest of 9.0.3: 2022.3.x, 2022.3.x-tuanjie, 6000.0.x, 6000.3.x. Anything else (6000.1, 6000.2, 6000.4, 6000.5, 2023, 2021 and below) makes the installer throw, which this module reports as `unityVersionSupported=false` |

- **IL2CPP only.** Mono builds cannot use HybridCLR; `hybridclr_validate_setup` reports this as a blocking error.
- Runtime API (`RuntimeApi`, `HomologousImageMode`, `LoadImageErrorCode`) has the same public signature on both lines; the module does not touch it.
- This module is verified against Unity 2022.3 and 6000.3.

### Upstream AI skills (`Data~/skills`)

From 2026-10 the upstream package ships eight `hybridclr-*` AI skills (concept and troubleshooting guides) under `Data~/skills/` on its `main` branch. Treat them as reference only: they are not part of tagged releases such as `v9.0.3`, and their menu names, paths and supported-Unity statements can lag the code (for example `HybridCLRData/...` where 9.x uses `Library/HybridCLR`, `Generate/AotDlls` where the menu is `Generate/StrippedAotDlls`). They do not replace this module: for paths, versions and Unity support trust the live output of `hybridclr_get_paths` and `hybridclr_install_status`.

## Exact Signatures

For authoritative parameter names, defaults, and return fields, query `GET /skills/schema?category=HybridCLR` or `unity_skills.get_skill_schema()`. This document is a routing / best-practice guide, not the signature source.
