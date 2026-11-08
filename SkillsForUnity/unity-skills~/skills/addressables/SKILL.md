---
name: unity-addressables
description: Manage Addressables groups, entries, profiles and content builds (com.unity.addressables, reflection-based)
---

# Unity Addressables Skills

Editor-side authoring for [Addressables](https://docs.unity3d.com/Packages/com.unity.addressables@latest): group CRUD, entry assignment, profile switching and the content build, all on the `AddressableAssetSettings` asset. Runtime loading code is not a skill. Parameters come from the schema (`GET /skills/schema?names=<skill>&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

## When to use
- Checking whether Addressables is installed and configured
- Creating groups and adding assets to them
- Switching the active profile
- Building Addressables content

## Rules
- Call `addressables_check_installed` first. It is the only skill that works without the package or the settings asset; its `installed` / `configured` pair names the missing precondition.
- Without the package, every other skill returns `MISSING_PACKAGE` (suggested fix `package_install`; installing is the user's call). Without the settings asset, it returns `TARGET_NOT_FOUND`. Only a person can create the settings asset, from Window > Asset Management > Addressables > Groups; retrying does not help.
- Members resolve by reflection against the 2.x editor source. 1.22.3 is not verified here, and the two versions differ: see the matrix in [addressables-design](../addressables-design/SKILL.md). A missing member returns a named error such as `CreateGroup method not found on AddressableAssetSettings`; read `version` from `addressables_check_installed`.
- `addressables_group_create` renames on collision (`TestGroup` becomes `TestGroup1`): use the returned `groupName`, never the requested one.
- `addressables_group_add_entry` moves an asset (`CreateOrMoveEntry`), it does not copy it. One asset cannot sit in two groups.
- `addressables_build` builds for `EditorUserBuildSettings.activeBuildTarget`; there is no target parameter, so switch the target first. It runs on the Editor main thread and is not an async job: `/health` and `/jobs` stall until it ends, and a client timeout does not cancel the build.
- Nothing here is workflow-undoable (`TracksWorkflow = false`). Each mutation saves the settings asset to disk immediately. To restore state by hand, read `addressables_group_list` / `addressables_profile_get` first.
- Group names match case-insensitively; collision dedupe is ordinal, so casing does not keep two groups apart.
- `configured: false` is a state, not an error to retry.
- Runtime `Addressables.LoadAssetAsync` handles, `AssetReference` and catalog updates: [addressables-design](../addressables-design/SKILL.md). Player build: the Project module's `build_player`, or [unity-cli](../unity-cli/SKILL.md).

## Pitfalls
- `addressables_group_remove_entry`, `addressables_entry_list` and `addressables_entry_set_address` do not exist. `addressables_group_list` reports only `entryCount`; inspect or remove individual entries in the Groups window.
- `addressables_profile_create` and `addressables_profile_set_variable` do not exist: profiles can be selected, not created or edited.
- `addressables_group_set_schema`, `addressables_analyze`, `addressables_clean_build` and `addressables_settings_create` do not exist.
- No `*_batch` variants: add several assets with one `addressables_group_add_entry` call each.

## Skills Overview

| Skill | Purpose |
|---|---|
| [`addressables_check_installed`](reference/addressables_check_installed.md) | Probe the package and the settings asset; call first |
| [`addressables_group_list`](reference/addressables_group_list.md) | List groups with `entryCount`; resolve real group names |
| [`addressables_group_create`](reference/addressables_group_create.md) | Create a standard bundled group (may be renamed) |
| [`addressables_group_add_entry`](reference/addressables_group_add_entry.md) | Add or move an asset into a group, optional address |
| [`addressables_group_delete`](reference/addressables_group_delete.md) | Remove a group (default group refused) |
| [`addressables_profile_get`](reference/addressables_profile_get.md) | Read the active profile and the profile names |
| [`addressables_profile_set`](reference/addressables_profile_set.md) | Switch the active profile by name |
| [`addressables_build`](reference/addressables_build.md) | Build content for the active build target (blocking) |
