---
name: unity-addressables
description: Manage Addressables groups, entries, profiles and content builds (com.unity.addressables, reflection-based)
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Checking whether Addressables is installed and configured
- Creating Addressables groups and adding assets to them
- Switching the active Addressables profile
- Building Addressables content
- 检查 Addressables 是否安装与配置、创建资源组并添加资源、切换激活 Profile、构建 Addressables 内容

# Unity Addressables Skills

Editor-side automation for [Addressables](https://docs.unity3d.com/Packages/com.unity.addressables@latest) — group CRUD, asset entry assignment, profile switching, and the content build. This is the **authoring** surface (the `AddressableAssetSettings` asset); runtime loading code is not a skill.

**This module holds zero direct references to the package.** Every call resolves through reflection against the `Unity.Addressables.Editor` assembly, so the UnitySkills Editor assembly compiles identically whether or not Addressables is installed — there is no scripting define to set and no recompile needed after installing.

> **Requires**: `com.unity.addressables` (API anchored to **2.x** Editor source; the 1.22.x API surface used here is identical).
> **Companion modules**: [addressables-design](../addressables-design/SKILL.md) for the runtime loading contract and the 1.22.3 / 2.9.1 migration table, [yooasset](../yooasset/SKILL.md) if the project ships hot updates through YooAsset instead, [package](../package/SKILL.md) to install the package.

## Guardrails

**Two independent preconditions.** Failing either returns a structured error, not a permission denial:
1. **Package installed.** Without it every skill except `addressables_check_installed` returns `errorCode: "MISSING_PACKAGE"` with `requiredPackage`, `docs` and install instructions.
2. **Settings asset created.** The package can be installed while `AddressableAssetSettings` does not yet exist. Every skill except `addressables_check_installed` then returns `errorCode: "TARGET_NOT_FOUND"` telling you to create it from **Window > Asset Management > Addressables > Groups**. There is deliberately no skill for this step — only the Groups window (or a human) can create the settings singleton.

**Always call `addressables_check_installed` first.** It is the only skill that works in both failure states, and its `installed` / `configured` pair tells you which precondition is missing.

**Operating Mode** (v1.9 three-tier):
- **SemiAuto** — `addressables_check_installed`, `addressables_group_list`, `addressables_profile_get` run directly in all three modes without a grant.
- **FullAuto** — `addressables_group_create`, `addressables_group_add_entry`, `addressables_profile_set`, `addressables_build` need a user grant under **Approval** (grant triggers one server-side execute returning the result); under **Auto** / **Bypass** they run directly with audit only.
- **Auto-forbidden** (NeverInSemi, `SkillOperation.Delete`) — `addressables_group_delete` returns `MODE_FORBIDDEN` under Approval and Auto. Only **Bypass** or a user-managed **Allowlist** entry can run it; never attempt a grant for it.

**DO NOT** (these skills do NOT exist — do not invent them):
- `addressables_group_remove_entry` / `addressables_entry_list` / `addressables_entry_set_address` — there is no entry-removal, entry-listing or address-rename skill. `addressables_group_list` reports only `entryCount` per group. Move an entry with `addressables_group_add_entry`; to inspect or remove individual entries use the Addressables Groups window.
- `addressables_profile_create` / `addressables_profile_set_variable` — profiles can be selected but not created or edited here.
- `addressables_group_set_schema` / `addressables_analyze` / `addressables_clean_build` — group schema editing, the Analyze rules and cache cleaning are not exposed.
- `addressables_settings_create` — see precondition 2; the settings asset cannot be created from a skill.

**Routing**:
- Addressables group / entry / profile authoring and content build → this module.
- Runtime `Addressables.LoadAssetAsync` handle lifecycle, `AssetReference` usage, catalog updates → [addressables-design](../addressables-design/SKILL.md) (documentation only; write the code yourself).
- Installing `com.unity.addressables` → `package_install` in [package](../package/SKILL.md).
- Building the actual player → `build_player` (Project module) or [unity-cli](../unity-cli/SKILL.md).

## Skills

### Environment (1)

### `addressables_check_installed`
Probe for the package and the settings asset. **The only skill that works without either.** Call it first.
**Parameters:** None
Returns `installed`, `configured`, `packageId`, and a `hint` naming the next action. When installed it adds `version` (from `PackageInfo.FindForAssembly`) and `settingsPath` (null when `configured` is false).

### Groups (4)

### `addressables_group_list`
List every group in the settings asset.
**Parameters:** None
Returns `count` plus `groups[]`, each entry carrying `name`, `guid`, `isDefault`, `readOnly` and `entryCount`. Use it to resolve a real group name before any group-scoped call.

### `addressables_group_create`
Create a group with the standard `BundledAssetGroupSchema` + `ContentUpdateGroupSchema` pair. Not set as the default group and not read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `groupName` | string | Yes | Desired group name. Rejected when blank. |

Returns `groupName` (**the name that actually landed on disk**), `requestedName`, `renamed`, `guid`, and a `note` when the two differ. See Critical Rule 1 — this skill renames on collision instead of failing.

### `addressables_group_add_entry`
Add an asset to a group by asset path, optionally under a custom address.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `assetPath` | string | Yes | Project-relative path, e.g. `Assets/Prefabs/Enemy.prefab`. Resolved via `AssetPathToGUID`; a miss returns `Asset not found`. |
| `groupName` | string | Yes | Target group. Matched **case-insensitively**; a miss returns `Group not found` with `addressables_group_list` as the suggested fix. |
| `address` | string | No | Custom address. Omit to keep the address Addressables assigns (the asset path for a new entry, the existing address for one being moved). |

Returns `assetPath`, `groupName`, `address` (the final address read back off the entry) and `guid`.

### `addressables_group_delete`
Remove a group from the settings asset. **Auto-forbidden — Bypass or Allowlist only.**

| Parameter | Type | Required | Description |
|---|---|---|---|
| `groupName` | string | Yes | Matched case-insensitively. The default group is refused (`Cannot delete default group`). |

Returns `groupName` and `deleted`. The group's entries are dropped with it; the underlying assets are untouched.

### Profiles (2)

### `addressables_profile_get`
Read the active profile and the full profile list.
**Parameters:** None
Returns `activeProfile` (the resolved name, falling back to the id when the name cannot be resolved), `activeProfileId`, and `profiles[]` — the names accepted by `addressables_profile_set`.

### `addressables_profile_set`
Switch the active profile by name.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `profileName` | string | Yes | Must be a name from `addressables_profile_get`. A miss returns `Profile not found` with `addressables_profile_get` as the suggested fix. |

Returns `activeProfile`, `profileId`, and `changed` (false when that profile was already active).

### Build (1)

### `addressables_build`
`AddressableAssetSettings.BuildPlayerContent()` for the **current** build target. Long-running and blocking — see Critical Rule 3.
**Parameters:** None
Returns `success`, `duration` (seconds), and `message`. On a build failure it returns `success: false` with `duration` and the build system's `error`; on an exception it returns `success: false` with `error` and `details`.

## Critical Rules (must read)

1. **`addressables_group_create` renames on collision — always use the returned `groupName`.** Addressables dedupes by appending a counter (`TestGroup` → `TestGroup1`) rather than failing, so the name you asked for is not necessarily the name on disk. The response reports `renamed: true`, the actual `groupName` and your `requestedName`. Feeding `requestedName` into a follow-up `addressables_group_add_entry` or `addressables_group_delete` is the single most common failure in this module.
2. **`addressables_group_add_entry` moves, it does not copy.** It calls `CreateOrMoveEntry`, so adding an asset that already belongs to another group relocates it. There is no skill to put one asset in two groups — Addressables does not allow it.
3. **`addressables_build` blocks the Editor main thread.** UnitySkills runs every skill on the main thread through a single queue, so `/health` and `/jobs` stall for the whole build. Raise the client timeout well past the expected build time; a socket timeout does **not** cancel the build. It is not an `AsyncJobService` job, so there is no `jobId` to poll.
4. **Nothing in this module is workflow-undoable.** Every mutating skill declares `TracksWorkflow = false`, so no pre-snapshot is taken and `workflow_*` undo cannot revert a created group, a moved entry, a profile switch or a build. Read the current state first (`addressables_group_list` / `addressables_profile_get`) if you need to restore it by hand.
5. **Group lookup is case-insensitive, collision-dedup is not.** `addressables_group_add_entry` and `addressables_group_delete` match `groupName` with `OrdinalIgnoreCase`, so `mygroup` finds `MyGroup`. `addressables_group_create` compares the returned name ordinally, so `renamed` is exact-case. Do not rely on casing to keep two groups distinct.
6. **Every mutation writes the settings asset immediately.** Each skill calls `EditorUtility.SetDirty` + `AssetDatabase.SaveAssets`, so changes hit `AddressableAssetSettings` on disk before the response returns — relevant if the file is under version control.
7. **`configured: false` is not an error state to retry.** It means the settings asset does not exist yet. Retrying any other skill will keep returning `TARGET_NOT_FOUND`; a human must create it from the Groups window first.

## Limitations

- **Authoring only.** Group creation, entry assignment, profile selection and the content build. Entry enumeration/removal, address renaming, group schema fields, profile variables, labels, the Analyze rules and content-update (`BuildContentUpdate` / `addressables_content.bin`) are all outside the module — use the Addressables Groups window.
- **Build target is implicit.** `addressables_build` builds for whatever `EditorUserBuildSettings.activeBuildTarget` currently is; there is no target parameter. Switch the target first if you need a different one.
- **Version drift is reported, not guessed.** Reflection targets are anchored to the Addressables 2.x Editor source. When a member cannot be resolved the skill returns a named error (`CreateGroup method not found on AddressableAssetSettings`, `Required Addressables group schema types were not found`, …) instead of failing silently.
- **No batch skills.** This module defines no `*_batch` variants; adding several assets means one `addressables_group_add_entry` call each.

## Exact Signatures

For authoritative parameter names, defaults, and return fields, query `GET /skills/schema?category=Addressables` or `unity_skills.get_skill_schema()`. This document is a routing / best-practice guide, not the signature source.
