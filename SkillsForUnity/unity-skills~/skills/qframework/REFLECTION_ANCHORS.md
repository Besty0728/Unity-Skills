# Reflection anchors

Every skill resolves these by fully-qualified name via `SkillsCommon.FindTypeByName`, which searches all loaded assemblies. Verified against QFramework v1.0.257 (liangxiegame/QFramework, 2026-08 snapshot). Detail behind [MODULE.md](MODULE.md).

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

## Version scope

- **Target**: QFramework **v1.0.257** (liangxiegame/QFramework, 2026-08 snapshot). All type names, member names and signatures above are taken from that source.
- **No package id.** QFramework ships as a `unitypackage` (Toolkits) or single-file `QFramework.cs`, not a UPM package — there is no version negotiation through Package Manager. `qframework_get_status.version` is read from `Assets/QFramework/Framework/PackageVersion.json`, which only exists on a standard Toolkits install layout; relocated or single-file installs report `version: null`.
- Other versions still work for anything whose members resolve; unresolved members are reported by name via `MISSING_PACKAGE`, not skipped.
