# HybridCLR × YooAsset workflow

HybridCLR produces the DLLs and YooAsset ships them. The staging step in between is `hybridclr_copy_hotupdate_dlls`, which renames `.dll` to `.bytes` so Unity imports them as `TextAsset` (a bare `.dll` under `Assets/` would be treated as a managed plugin).

Each call below is `POST /skill/<name>?expectProject=<project>`; shown with `u=http://localhost:<port>/skill` and `p=expectProject=<project>`.

## One-time setup (per project, and again after any AOT-side code change)

```bash
u=http://localhost:<port>/skill; p="expectProject=<project>"
curl -s "$u/hybridclr_settings_set?$p" -d '{"enable":true,"hotUpdateAssemblyDefinitions":["Assets/HotUpdate/HotUpdate.asmdef"]}'
curl -s "$u/hybridclr_validate_setup?$p" -d '{}'          # fix every `errors` entry before continuing
curl -s "$u/hybridclr_generate_all?$p" -d '{}'            # minutes; domain reload follows
curl -s "$u/hybridclr_aot_generic_refs?$p" -d '{}'
# 8.x only: copy the generated patchedAOTAssemblyList into the settings.
# 9.x has no such setting: PatchedAOTAssemblyList itself drives
# hybridclr_copy_hotupdate_dlls(includeAotAssemblies=true).
curl -s "$u/hybridclr_settings_set?$p" -d '{"patchAOTAssemblies":["<from aot_generic_refs>"]}'   # 8.x only
# then build the player normally (build_player / unity-cli)
```

## Iterative hot-update loop

Run this whenever hot-update C# changes. No `generate_all`, no player rebuild.

1. Edit the hot-update C# (`script_*` skills). It must live in an assembly listed in `hotUpdateAssemblyDefinitions`.
2. Compile and verify:
   `curl -s "$u/hybridclr_compile_dlls?$p" -d '{"buildTarget":"Android"}'`, then `hybridclr_get_hotupdate_dlls` with the same `buildTarget`; `missing` must be empty.
3. Stage into the YooAsset collector directory:
   `curl -s "$u/hybridclr_copy_hotupdate_dlls?$p" -d '{"destination":"Assets/HotUpdateDlls","buildTarget":"Android","extension":".bytes","includeAotAssemblies":true,"clearDestination":true}'`
4. Pack with the [yooasset](../../yooasset/SKILL.md) module. Add the collector once, then rebuild bundles on each change.

## Where the boundary sits

Runtime loading is game code you write, not a skill. Load the `.bytes` `TextAsset` through YooAsset, then call `RuntimeApi.LoadMetadataForAOTAssembly(aotBytes, HomologousImageMode.SuperSet)` for each AOT metadata DLL **before** `Assembly.Load(hotUpdateBytes)`. Order matters. See [yooasset-design](../../yooasset-design/SKILL.md) for the handle-lifecycle rules.

## When to re-run `generate_all`

Only when AOT-side code changes (anything outside the hot-update asmdefs), when you add or remove a hot-update assembly, or before shipping a new player build. Generic instantiations reachable only from new hot-update code also require it; that is what `AOTGenericReferences.cs` records.

## Freeze the AOT metadata DLLs per shipped player

Stripped AOT DLLs are per build target, and a fresh `Generate` output can differ slightly from the DLLs of the player already in users' hands. This matters for `HomologousImageMode.Consistent`; `SuperSet` tolerates it. Stage them again with `includeAotAssemblies=true` when you ship a new player, not on every hot update.
