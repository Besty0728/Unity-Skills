---
name: unity-qframework
description: Editor automation for QFramework (github.com/liangxiegame/QFramework) — architecture-layer code generation (Architecture/System/Model/Command/Utility/Query via ArchitectureCodeGenerator), ViewController and UIKit panel code generation bound to a GameObject/prefab, UIKit project settings, ResKit AssetBundle marking/build/clear, ResKit SimulationMode and build options, runtime architecture scanning (IArchitecture/ISystem/IModel/ICommand/IQuery/IController), QFramework's built-in API-documentation-attribute query, and LocaleKit editor-locale / language-define configuration. QFramework has no UPM package — detection is reflection-only against whichever install form is present (Toolkits unitypackage or single-file QFramework.cs) — so every skill except qframework_get_status returns MISSING_PACKAGE when the required anchor type cannot be resolved.
---

> **Before calling any skill in this module:** if you are about to call a skill with parameters guessed from its name or description, STOP — read this file (or fetch its schema via `GET /skills/recommend?includeSchema=true`) first. If you already have the parameter definitions from recommend/schema, you may proceed straight to dryRun.

## Triggers
- Generating QFramework architecture-layer code — Architecture/System/Model/Command/Utility/Query classes
- Generating bound view code for a QFramework `ViewController` component, or UIKit panel code for a UI prefab
- Checking QFramework installation status, scanning `IArchitecture`/`ISystem`/`IModel`/`ICommand`/`IQuery`/`IController` implementations, or searching QFramework's built-in API documentation attributes
- Marking, building or clearing QFramework ResKit AssetBundles, or reading/writing ResKit build options (SimulationMode, append-hash, auto-generate-class)
- Reading/writing UIKit project settings (namespace, script/prefab dirs) or LocaleKit editor locale / language defines
- 生成 QFramework 架构层代码、绑定 ViewController/UIKit 面板代码生成、检查安装状态与架构扫描、ResKit AssetBundle 标记与构建、UIKit/LocaleKit 设置读写

# Unity QFramework Skills

Editor-side automation for [QFramework](https://github.com/liangxiegame/QFramework) — architecture codegen, UIKit/ResKit tooling, LocaleKit configuration, and read-only architecture/API-doc scanning.

**QFramework has no UPM package.** It installs one of two ways: the **Toolkits** `unitypackage` (asmdef-based: `QFramework` / `QFramework.CoreKit` / `UIKit` / `UIKit.Editor` / `ResKit` / `ResKit.Editor` / `AudioKit`), or a **single-file** `QFramework.cs` dropped straight into `Assets/` (no asmdef, core architecture interfaces only, no Toolkits). This module holds zero direct references to either form — every call resolves through reflection against a fully-qualified type name, so the UnitySkills Editor assembly compiles identically regardless of what (if anything) is installed. `qframework_get_status` works in every state; every other skill returns `MISSING_PACKAGE` when its anchor type cannot be resolved.

> **Requires**: QFramework (no package id — install via the official `unitypackage` or `QFramework.cs`, API anchored to **v1.0.257**).
> **Companion module**: [qframework-design](../qframework-design/SKILL.md) for architecture design rules (four-layer responsibilities, Command/Query/Event/BindableProperty usage, IOCContainer vs. Toolkits IOCKit) — advisory only, no REST skills, load it before writing or reviewing QFramework architecture code.

## Guardrails

**Operating Mode** (three-tier):
- **SemiAuto** — the 8 `ReadOnly` skills run directly in all three modes without a grant: `qframework_get_status`, `qframework_list_architecture_code_types`, `qframework_preview_architecture_code`, `qframework_get_uikit_settings`, `qframework_list_asset_bundle_marks`, `qframework_get_reskit_build_options`, `qframework_scan_architecture`, `qframework_query_api_docs`.
- **FullAuto** (needs a user grant under Approval; runs directly under Auto/Bypass with audit) — the 6 write skills that are *not* NeverInSemi: `qframework_mark_asset_bundle`, `qframework_mark_asset_bundle_batch`, `qframework_set_uikit_settings`, `qframework_set_reskit_build_options`, `qframework_set_editor_locale`, `qframework_set_language_defines`.
- **Auto-forbidden** (NeverInSemi — returns `MODE_FORBIDDEN` under both Approval and Auto; only Bypass or a user-managed Allowlist entry can run these; never attempt a grant) — **6 skills**, triggered by two different metadata flags:
  - `MayTriggerReload=true` on all four codegen-to-disk skills: `qframework_generate_architecture_code`, `qframework_generate_architecture_code_batch`, `qframework_generate_view_controller_code`, `qframework_generate_ui_panel_code`.
  - `RiskLevel="high"` on `qframework_build_asset_bundles` (also `LongRunning=true`, `SupportsDryRun=false` — blocks the Editor main thread for the build duration) and `qframework_clear_asset_bundles` (also `Operation=Delete` — deletes `AssetBundles/` and `StreamingAssets/AssetBundles/` outright).

  This is stricter than a plain "write skill needs a grant" reading: the four codegen skills are not just gated behind a grant, they are unreachable under Approval **and** Auto — Bypass or Allowlist only.

**Two-phase code generation protocol.** `qframework_generate_view_controller_code` and `qframework_generate_ui_panel_code` write `.cs`/`.Designer.cs` files synchronously and return `pendingCompile: true` **before** compilation happens. The generated component is attached to the GameObject (view controller case) only later, by a `[DidReloadScripts]` callback that fires after the next domain reload. After calling either skill, poll compile status (`script_get_compile_feedback` / `debug_get_errors`) before re-inspecting the GameObject or prefab — a `503`/busy response from the service during that window is expected, not a failure. The two skills name their Designer-file output field differently — `expectedDesignerScriptPath` on the view-controller skill, `expectedDesignerPath` on the UI-panel skill — this is deliberate, not a typo; don't assume the same key works for both.

**This module declares no `RequiresPackages`.** QFramework has no real UPM package id, so tagging skills with `RequiresPackages=["QFramework"]` would only make `PackageManagerHelper.IsPackageInstalled("QFramework")` permanently return `false` and demote every QFramework skill `-5` in `/skills/recommend` scoring regardless of actual install state — [dotween](../dotween/SKILL.md) sets the same precedent for non-UPM distributions. The `MISSING_PACKAGE` gate is entirely reflection-driven: every skill (except `qframework_get_status`) probes its own anchor API (`QType(...)`) at call time and returns a structured `MISSING_PACKAGE` error naming the exact missing type/member when it can't resolve.

**Toolkits vs. core-only install.** `qframework_get_status.installKind` is `"toolkits"`, `"coreOnly"`, or `"none"`. On `"coreOnly"` (single-file `QFramework.cs`), architecture codegen, UIKit, ResKit and LocaleKit skills still report `MISSING_PACKAGE` — those anchor types only ship with the Toolkits.

## Skills

### Status & Discovery (3)

### `qframework_get_status`
Report install kind (Toolkits vs. core-only vs. none), detected assemblies, package version, editor locale, and ResKit SimulationMode. Works with or without QFramework installed — call this first.
**Parameters:** None
**Returns:** `installed`, `installKind`, `assemblies`, `version`, `editorLocaleIsCN`, `simulationMode`

### `qframework_scan_architecture`
Scan loaded assemblies (excluding QFramework's own assemblies and system assemblies) for non-abstract types implementing `IArchitecture` / `ISystem` / `IModel` / `ICommand`(`<T>`) / `IQuery<T>` / `IController`. Read-only type metadata inspection; does not enter Play Mode.
**Parameters:** None
**Returns:** `architectures`, `systems`, `models`, `commands`, `queries`, `controllers`, `totalCount`

### `qframework_query_api_docs`
Search QFramework's built-in API documentation attributes (`ClassAPIAttribute`/`MethodAPIAttribute`/`PropertyAPIAttribute` + `APIDescriptionCN`/`EN` + `APIExampleCode`) across loaded assemblies.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| search | string | No | null | Substring match against class full name, `DisplayMenuName`, or member names (case-insensitive) |
| groupName | string | No | null | Filter by `ClassAPIAttribute.GroupName` (exact, case-insensitive) |
| className | string | No | null | Filter by type name or `DisplayClassName` substring |
| limit | int | No | 50 | Max classes returned, clamped to [1, 500] |

**Returns:** `count`, `truncated`, `classes`

### Architecture CodeGen (4)

### `qframework_list_architecture_code_types`
List `ArchitectureCodeType` values (Architecture/System/Model/Command/Utility/Query) with whether each supports interface generation and its `Architecture` registration method name.
**Parameters:** None
**Returns:** `count`, `types`

### `qframework_preview_architecture_code`
Preview the architecture-layer code `ArchitectureCodeGenerator.CreatePreview` would generate for a name, without writing any file.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| codeType | string | Yes | - | `ArchitectureCodeType` name — Architecture/System/Model/Command/Utility/Query |
| inputName | string | Yes | - | Base name for the generated type |
| namespaceName | string | Yes | - | Namespace for the generated code |
| outputRoot | string | Yes | - | Output root passed to `CreatePreview` |
| generateInterface | bool | No | false | Also generate the matching interface (only meaningful where `SupportsInterfaceGeneration` is true) |

**Returns:** `isValid`, `error`, `codeType`, `className`, `namespaceName`, `assetPath`, `code`. On `isValid: false`, the response also carries a `hint` field — `ArchitectureCodeGenerator`'s own validation message (`error`) is QFramework's hardcoded upstream text and may be in Chinese (e.g. "请输入名字"); `hint` is a fixed English annotation pointing at which of `codeType`/`inputName`/`namespaceName`/`outputRoot` to check.

### `qframework_generate_architecture_code`
Generate architecture-layer code to disk via `CreatePreview` + `Generate`. Refuses to overwrite an existing file at the target path. **NeverInSemi** (`MayTriggerReload=true`) — Bypass or Allowlist only.

Same parameters as `qframework_preview_architecture_code` (`codeType`, `inputName`, `namespaceName`, `outputRoot` required; `generateInterface` optional, default `false`).
**Returns:** `success`, `error`, `assetPath`, `codeType`, `className`. Failure responses also add `hint`: the same upstream-validation annotation as `qframework_preview_architecture_code` when `CreatePreview` itself rejects the input, or a fixed note that QFramework's file-exists error means it refused to overwrite existing code when `Generate` fails after a valid preview.

### `qframework_generate_architecture_code_batch`
Generate multiple architecture-layer code files in one request. **NeverInSemi** (`MayTriggerReload=true`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| items | string | Yes | - | JSON array of `{codeType, name, namespaceName, outputRoot, generateInterface}` |

**Returns:** `totalItems`, `successCount`, `failCount`, `results`

### UIKit CodeGen & Settings (4)

### `qframework_generate_view_controller_code`
Generate the bound-view code for a GameObject's `ViewController` component via `CodeGenKit.Generate(IBindGroup)`. Writes `.cs`/`.Designer.cs` immediately and returns before compilation — see the two-phase protocol in Guardrails. **NeverInSemi** (`MayTriggerReload=true`). Target GameObject must already carry a `ViewController` component (or subclass) with `ScriptsFolder` and `ScriptName` set.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| name | string | No* | null | GameObject name (exact match first, then substring) |
| instanceId | int | No* | 0 | Legacy Unity instance ID (Unity < 6000.4) |
| path | string | No* | null | Hierarchy path, e.g. `Canvas/Panel/Target` |
| tag | string | No* | null | GameObject tag |
| componentType | string | No* | null | Find the first GameObject carrying this component type |
| entityId | string | No* | null | Unity EntityId (Unity 6000.4+, preferred) |

*At least one of the six is required. Lookup priority: `entityId > instanceId > path > name (exact, then substring) > tag > componentType`, per `GameObjectFinder.Find`.
**Returns:** `pendingCompile`, `error`, `expectedScriptPath`, `expectedDesignerScriptPath`, `className`, `namespaceName`, `scriptsFolder`

### `qframework_generate_ui_panel_code`
Generate UIKit panel + Designer code for a UI prefab via `UICodeGenerator.DoCreateCode`. The target must already be a regular (or variant) Prefab asset — QFramework silently no-ops for anything else, so this skill pre-validates and reports a real error instead. `UICodeGenerator` writes `<Panel>.cs` only if it doesn't already exist but always rewrites `<Panel>.Designer.cs`; this skill snapshots the Designer file either way (modified-before snapshot if it already existed, created-asset snapshot if it's new), so `workflow_*` undo covers both files. **NeverInSemi** (`MayTriggerReload=true`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| prefabPath | string | Yes | - | Project-relative path to a regular/variant Prefab asset, e.g. `Assets/Art/UIPrefab/LoginPanel.prefab` |

**Returns:** `pendingCompile`, `error`, `expectedScriptPath`, `expectedDesignerPath`, `prefabPath`, `uiScriptDir`, `uiPrefabDir`, `namespaceName`

### `qframework_get_uikit_settings`
Read UIKit project settings (`Assets/QFrameworkData/ProjectConfig/ProjectConfig.json`) — default namespace, UI script/prefab output directories, and the assembly names `UICodeGenerator` searches for bind types.
**Parameters:** None
**Returns:** `namespaceName`, `uiScriptDir`, `uiPrefabDir`, `assemblyNamesToSearch`, `isDefaultNamespace`

### `qframework_set_uikit_settings`
Write UIKit project settings and persist to `ProjectConfig.json`. Only the parameters you pass are changed.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| namespaceName | string | No | null | New default namespace |
| uiScriptDir | string | No | null | New UI script output directory |
| uiPrefabDir | string | No | null | New UI prefab output directory |
| assemblyNamesToSearch | string | No | null | JSON array of assembly names — **replaces** the existing list, does not merge |

**Returns:** `changed`, `namespaceName`, `uiScriptDir`, `uiPrefabDir`, `assemblyNamesToSearch`, `isDefaultNamespace`

### ResKit AssetBundle (7)

### `qframework_mark_asset_bundle`
Mark or unmark a project folder as a ResKit AssetBundle via `ResKitAssetsMenu.MarkAB`. `MarkAB` itself toggles — this skill checks the current state first, so calling it repeatedly with the same `marked` value is a no-op (`changed: false`).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| folderPath | string | Yes | - | Project folder path to mark/unmark |
| marked | bool | No | true | Desired mark state |

**Returns:** `path`, `marked`, `assetBundleName`, `changed`

### `qframework_mark_asset_bundle_batch`
Mark or unmark multiple folders in one request.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| items | string | Yes | - | JSON array of `{folderPath, marked}` |

**Returns:** `totalItems`, `successCount`, `failCount`, `results`

### `qframework_list_asset_bundle_marks`
List every AssetBundle name registered in `AssetDatabase` with the asset paths assigned to each — includes AssetBundle names assigned outside `ResKitAssetsMenu.MarkAB`, not only ResKit-marked ones.
**Parameters:** None
**Returns:** `count`, `assetBundles`

### `qframework_get_reskit_build_options`
Read ResKit build-time options: `SimulationMode` (`ResKitEditorAPI.SimulationMode`), and the two AssetBundle-build EditorPrefs toggles — append hash to bundle names, auto-generate the resource-name constant class.
**Parameters:** None
**Returns:** `simulationMode`, `appendHash`, `autoGenerateClass`

### `qframework_set_reskit_build_options`
Set ResKit build-time options. `simulationMode` goes through `ResKitEditorAPI.SimulationMode`; `appendHash`/`autoGenerateClass` have no public setter in QFramework and are written directly to the EditorPrefs keys `ResKitView` itself reads (`KEY_APPEND_HASH` / `KEY_AUTOGENERATE_CLASS`) — the only write path QFramework exposes for them.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| simulationMode | bool | No | null | `ResKitEditorAPI.SimulationMode` |
| appendHash | bool | No | null | Append hash to AssetBundle names |
| autoGenerateClass | bool | No | null | Auto-generate the resource-name constant class |

**Returns:** `changed`, `simulationMode`, `appendHash`, `autoGenerateClass`

### `qframework_build_asset_bundles`
Build AssetBundles for a build target via `BuildScript.BuildAssetBundles`. Output directory is fixed by QFramework to `AssetBundles/<platform>` at the project root, mirrored into `StreamingAssets/AssetBundles/<platform>` — not configurable. **BLOCKS the Editor main thread for the build duration.** **NeverInSemi** (`RiskLevel="high"`, `LongRunning=true`, `SupportsDryRun=false`).

`outputDir`/`streamingAssetsDir` are always computed from `EditorUserBuildSettings.activeBuildTarget`, never from the requested `buildTarget` — QFramework's own `BuildScript.BuildAssetBundles` derives its output path from a private helper that reads `activeBuildTarget` directly and ignores the `BuildTarget` argument it's called with; the argument only steers the internal `BuildPipeline.BuildAssetBundles` compile step. If you pass a `buildTarget` that differs from the currently active platform, the response adds a `warning` field spelling this out — the build still runs against your requested `buildTarget`, but the files land under the *active* platform's directory. Switch the active build target first if you need the paths and the build to agree.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| buildTarget | string | No | null (uses `EditorUserBuildSettings.activeBuildTarget`) | `UnityEditor.BuildTarget` enum name |

**Returns:** `success`, `error`, `buildTarget`, `outputDir`, `streamingAssetsDir`, `elapsedSeconds`. Adds `warning` (not in the declared Outputs — only present when `buildTarget` differs from the active platform) on success.

### `qframework_clear_asset_bundles`
Delete all built ResKit AssetBundle output via `ResKitEditorAPI.ForceClearAssetBundles` — removes `AssetBundles/` at the project root and `StreamingAssets/AssetBundles`. **`TracksWorkflow=false` and not reversible** — the delete happens at the filesystem level outside `AssetDatabase`, so there is no workflow snapshot to restore from; treat it as permanent. **NeverInSemi** (`Operation=Delete`, `RiskLevel="high"`).
**Parameters:** None
**Returns:** `success`, `error`, `clearedDirs`

### Locale (2)

### `qframework_set_editor_locale`
Set QFramework's editor UI locale via `LocaleKitEditor.IsCN` (backed by EditorPrefs key `EDITOR_CN`). Affects QFramework's own editor windows (ResKit, UIKit, etc.), not runtime localization.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| isCN | bool | Yes | - | `true` = Chinese, `false` = English |

**Returns:** `isCN`, `changed`

### `qframework_set_language_defines`
Replace the LocaleKit `LanguageDefineConfig.LanguageDefines` list (`Assets/QFrameworkData/LocaleKit/Resources/LanguageDefineConfig.asset`). Creates the asset if it does not exist yet. **Replaces the entire list** — languages omitted from `languages` are dropped, not kept.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| languages | string | Yes | - | JSON array of `QFramework.Language` enum names (mirrors `UnityEngine.SystemLanguage`), e.g. `["English","ChineseSimplified"]` |

**Returns:** `error`, `languages`, `assetPath`

## DO NOT

These do **not** exist in this module — do not invent them:
- `qframework_audio_*` — no AudioKit skills. `AudioKit.Settings` throws a `NullReferenceException` in pure editor context; it only initializes in Play Mode, so there is no safe editor-time surface to expose.
- `qframework_fsm_*` / `qframework_singleton_*` / `qframework_pool_*` / `qframework_table_*` — FSM, SingletonKit, PoolKit and TableKit are purely runtime toolkits with no editor-time entry point; nothing in them is reachable from an Editor-side REST skill.
- `qframework_install` — QFramework has no package manager entry to drive. Install the official `unitypackage` (Toolkits) or drop `QFramework.cs` into `Assets/` (core only) by hand; there is no automated install path.
- `qframework_group_*` / `qframework_entry_*` — that's the [addressables](../addressables/SKILL.md) module's Groups vocabulary, not QFramework's.
- No `_batch` variant for UIKit settings, ResKit build options, or Locale skills — only architecture codegen (`qframework_generate_architecture_code_batch`) and AssetBundle marking (`qframework_mark_asset_bundle_batch`) have batch forms.

**Routing**:
- Architecture/UIKit/ResKit/LocaleKit editor automation → this module.
- QFramework architecture design rules (four-layer responsibilities, Command/Query/Event/BindableProperty patterns, IOCContainer vs. Toolkits IOCKit) → [qframework-design](../qframework-design/SKILL.md) — advisory, no REST skills, load it before writing or reviewing architecture code.
- Runtime hot-update / bundle loading unrelated to QFramework's own AssetBundle marking → this module only covers ResKit's own build/mark/clear; general Addressables authoring is [addressables](../addressables/SKILL.md).

## Reflection Anchors

Every skill resolves these by fully-qualified name via `SkillsCommon.FindTypeByName`, which searches all loaded assemblies. Verified against QFramework v1.0.257 (liangxiegame/QFramework, 2026-08 snapshot).

**Known assemblies** (Toolkits install; `qframework_get_status.assemblies` reports whichever are loaded): `QFramework`, `QFramework.CoreKit`, `UIKit`, `UIKit.Editor`, `ResKit`, `ResKit.Editor`, `AudioKit`.

| Skill(s) | Reflected anchor type(s) |
|----------|---------------------------|
| `qframework_get_status` | `QFramework.IArchitecture` (core presence); `QFramework.ArchitectureCodeGenerator` / `QFramework.UIKitSettingData` / `QFramework.ResKitEditorAPI` (any one present ⇒ Toolkits) |
| `qframework_list_architecture_code_types`, `qframework_preview_architecture_code`, `qframework_generate_architecture_code`(`_batch`) | `QFramework.ArchitectureCodeGenerator`, `QFramework.ArchitectureCodeType` |
| `qframework_generate_view_controller_code` | `QFramework.ViewController`, `QFramework.CodeGenKit`, `QFramework.IBindGroup` |
| `qframework_generate_ui_panel_code` | `QFramework.UICodeGenerator`, `QFramework.UIKitSettingData` |
| `qframework_get_uikit_settings` / `_set_uikit_settings` | `QFramework.UIKitSettingData` (`.Load()` / `.Save()`) |
| `qframework_mark_asset_bundle`(`_batch`), `qframework_list_asset_bundle_marks` | `QFramework.ResKitAssetsMenu` (`Marked`, `MarkAB`) |
| `qframework_get_reskit_build_options` / `_set_reskit_build_options` | `QFramework.ResKitEditorAPI` (`SimulationMode`), `QFramework.ResKitView` (`KEY_APPEND_HASH`, `KEY_AUTOGENERATE_CLASS`) |
| `qframework_build_asset_bundles` | `QFramework.BuildScript` (`BuildAssetBundles(BuildTarget)`), `QFramework.AssetBundlePathHelper` (`GetPlatformForAssetBundles`) |
| `qframework_clear_asset_bundles` | `QFramework.ResKitEditorAPI` (`ForceClearAssetBundles`) |
| `qframework_scan_architecture` | `QFramework.IArchitecture`, `ISystem`, `IModel`, `ICommand`, `ICommand\`1`, `IQuery\`1`, `IController` |
| `qframework_query_api_docs` | `QFramework.ClassAPIAttribute`, `MethodAPIAttribute`, `PropertyAPIAttribute`, `APIDescriptionCNAttribute`, `APIDescriptionENAttribute`, `APIExampleCodeAttribute` |
| `qframework_set_editor_locale` | `QFramework.LocaleKitEditor` (`IsCN`) |
| `qframework_set_language_defines` | `QFramework.LanguageDefineConfig` (`Default`, `Save`), `QFramework.LanguageDefine`, `QFramework.Language` |

## Version Scope

- **Target**: QFramework **v1.0.257** (liangxiegame/QFramework, 2026-08 snapshot). All type names, member names and signatures above are taken from that source.
- **No package id.** QFramework ships as a `unitypackage` (Toolkits) or single-file `QFramework.cs`, not a UPM package — there is no version negotiation through Package Manager. `qframework_get_status.version` is read from `Assets/QFramework/Framework/PackageVersion.json`, which only exists on a standard Toolkits install layout; relocated or single-file installs report `version: null`.
- Older/newer versions still work for anything whose members resolve; unresolved members are reported by name via `MISSING_PACKAGE` (`error` names the exact anchor API), not silently skipped.

## Exact Signatures

For authoritative parameter names, defaults, and return fields, query `GET /skills/schema?category=QFramework` or `unity_skills.get_skill_schema()`. This document is a routing / best-practice guide, not the signature source.
