# Limitations


- **No install skill.** `InstallerController.InstallDefaultHybridCLR()` is reachable by reflection but deliberately not exposed: it clones two git repos over the network and copies the editor's entire il2cpp tree, with no progress reporting or cancellation, and would hold the main-thread queue for the whole time. Use the **HybridCLR/Installer...** window, or unity-cli in batch mode.
- **Synchronous execution.** These operations are not `AsyncJobService` jobs — HybridCLR's commands are synchronous main-thread Unity APIs with no step boundaries to pump, so there is no `jobId` to poll. Treat them as long blocking calls.
- **Workflow undo is partial for generation.** `hybridclr_settings_set` restores fully. `hybridclr_compile_dlls` and `hybridclr_copy_hotupdate_dlls` restore the affected directory from a file backup under `Library/UnitySkills/HybridCLRBackups/`. `hybridclr_generate_all` and `hybridclr_generate_step` restore only the two Assets-side artifacts (`link.xml`, `AOTGenericReferences.cs`) plus the hot-update DLL directory — intermediates under the HybridCLR data dir (`HybridCLRData/` on 8.x, `Library/HybridCLR` on 9.x), including the generated C++ in the local il2cpp copy, are rebuildable and deliberately out of scope. Re-run the pipeline rather than relying on undo for those.
- **Backups keep 5 generations per label.** Older undo steps for `compile_dlls` / `copy_hotupdate_dlls` degrade to "added files removed, previous files not restored". `Library/` is not version-controlled, so backups do not survive a Library wipe.
- **No control over build processors.** HybridCLR's `IPreprocessBuildWithReport` / `IPostprocessBuildWithReport` hooks (`CheckSettings`, `CopyStrippedAOTAssemblies`, `PatchScriptingAssemblyList`, the Xcode source injectors) run automatically during the player build and are not configurable from this module.
- **Version drift is reported, not guessed.** Reflection targets are anchored to 8.12.0 (8.x) and 9.0.3 (9.x). A release outside `>=8.12.0 <10.0.0` is refused with `unsupported_package_version`; inside the range, a member that cannot be resolved returns `SKILL_ERROR` naming it, rather than silently doing nothing.
- **Constructing `InstallerController` logs a line.** Upstream 9.x prints its matched version entry to the Console each time the controller is created, so `hybridclr_status` / `hybridclr_install_status` leave one upstream log line per call.
- **`hybridclr_aot_generic_refs` parses generated text, not IL.** `patchedAOTAssemblyList` is read exactly from the `PatchedAOTAssemblyList` initializer, but `genericTypeCount` counts comment lines in the generator's `// {{ AOT generic types` block and is an approximation. Pass `includeContent=true` when you need the file verbatim.

## unity-cli fallback

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

Running these headless avoids the main-thread blocking problem entirely — see [unity-cli](../../unity-cli/MODULE.md). Installation has no parameterless menu method; it must go through the Installer window.
