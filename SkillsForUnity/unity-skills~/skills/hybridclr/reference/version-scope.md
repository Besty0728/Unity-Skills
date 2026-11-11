# Version scope and reflection anchors


## Version scope

- **Supported range**: `com.code-philosophy.hybridclr` `>=8.12.0 <10.0.0`. Anything else returns `unsupported_package_version` (see the rules in MODULE.md).
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

## Upstream AI skills (`Data~/skills`)

From 2026-10 the upstream package ships eight `hybridclr-*` AI skills (concept and troubleshooting guides) under `Data~/skills/` on its `main` branch. Treat them as reference only: they are not part of tagged releases such as `v9.0.3`, and their menu names, paths and supported-Unity statements can lag the code (for example `HybridCLRData/...` where 9.x uses `Library/HybridCLR`, `Generate/AotDlls` where the menu is `Generate/StrippedAotDlls`). They do not replace this module: for paths, versions and Unity support trust the live output of `hybridclr_get_paths` and `hybridclr_install_status`.


## Reflection anchors

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
