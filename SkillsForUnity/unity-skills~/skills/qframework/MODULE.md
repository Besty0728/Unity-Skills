# Unity QFramework Skills

Editor automation for [QFramework](https://github.com/liangxiegame/QFramework): codegen, UIKit, ResKit and LocaleKit tooling, and read-only architecture and API-doc scans. Parameters come from the schema (`GET /skills/schema?category=QFramework&wire=v2`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

QFramework has no UPM package. It installs either as the Toolkits `unitypackage` or as a single `QFramework.cs`. Every skill resolves its types by reflection at call time, so the module compiles in any state. `qframework_get_status` works always; the others return `MISSING_PACKAGE` when their anchor type cannot be resolved. Anchor types and version scope: [reflection-anchors](REFLECTION_ANCHORS.md). Architecture design rules are advisory: load [qframework-design](../qframework-design/MODULE.md) before writing or reviewing architecture code.

## When to use
- Generating architecture-layer code: Architecture, System, Model, Command, Utility, Query
- Generating bound view code for a `ViewController`, or UIKit panel code for a UI prefab
- Checking install status, scanning architecture types, or searching QFramework API docs
- Marking, building or clearing ResKit AssetBundles, or reading and writing ResKit build options
- Reading or writing UIKit settings, or LocaleKit locale and language defines

## Rules
- Call `qframework_get_status` first. `installKind` is `toolkits`, `coreOnly` or `none`. On `coreOnly`, the architecture, UIKit, ResKit and LocaleKit skills still return `MISSING_PACKAGE`, because those anchors ship only with Toolkits.
- Eight read-only skills run in every mode. The six writers (`qframework_mark_asset_bundle`, `_batch`, `qframework_set_uikit_settings`, `qframework_set_reskit_build_options`, `qframework_set_editor_locale`, `qframework_set_language_defines`) need a grant under Approval.
- Refused under Approval and Auto; Bypass or allowlist only, and do not attempt a grant: the four codegen skills (`MayTriggerReload`), `qframework_build_asset_bundles` (high risk, blocks the Editor for the build) and `qframework_clear_asset_bundles` (deletes outright, irreversible).
- Two-phase codegen: the view-controller and UI-panel skills write files and return `pendingCompile: true` before compilation. The component attaches only after the next domain reload. Poll compile status (`script_get_compile_feedback` or `debug_get_errors`) before inspecting the GameObject or prefab; a 503 or busy response in that window is expected.
- The Designer-file key differs by skill: `expectedDesignerScriptPath` for the view controller, `expectedDesignerPath` for the UI panel.
- `qframework_set_language_defines` and `qframework_set_uikit_settings.assemblyNamesToSearch` each replace the whole list; languages or assemblies left out are dropped.

## Skills Overview

| Group | Skills | Purpose |
|-------|--------|---------|
| Status and discovery | [`qframework_get_status`](reference/qframework_get_status.md), [`qframework_scan_architecture`](reference/qframework_scan_architecture.md), [`qframework_query_api_docs`](reference/qframework_query_api_docs.md) | Install state; architecture types; API documentation search |
| Architecture codegen | [`qframework_list_architecture_code_types`](reference/qframework_list_architecture_code_types.md), [`qframework_preview_architecture_code`](reference/qframework_preview_architecture_code.md), [`qframework_generate_architecture_code`](reference/qframework_generate_architecture_code.md), [`qframework_generate_architecture_code_batch`](reference/qframework_generate_architecture_code_batch.md) | Preview and generate architecture-layer code |
| UIKit codegen and settings | [`qframework_generate_view_controller_code`](reference/qframework_generate_view_controller_code.md), [`qframework_generate_ui_panel_code`](reference/qframework_generate_ui_panel_code.md), [`qframework_get_uikit_settings`](reference/qframework_get_uikit_settings.md), [`qframework_set_uikit_settings`](reference/qframework_set_uikit_settings.md) | Bound view and panel code; UIKit project settings |
| ResKit AssetBundles | [`qframework_mark_asset_bundle`](reference/qframework_mark_asset_bundle.md), [`qframework_mark_asset_bundle_batch`](reference/qframework_mark_asset_bundle_batch.md), [`qframework_list_asset_bundle_marks`](reference/qframework_list_asset_bundle_marks.md), [`qframework_get_reskit_build_options`](reference/qframework_get_reskit_build_options.md), [`qframework_set_reskit_build_options`](reference/qframework_set_reskit_build_options.md), [`qframework_build_asset_bundles`](reference/qframework_build_asset_bundles.md), [`qframework_clear_asset_bundles`](reference/qframework_clear_asset_bundles.md) | Mark, build, clear bundles; build options |
| LocaleKit | [`qframework_set_editor_locale`](reference/qframework_set_editor_locale.md), [`qframework_set_language_defines`](reference/qframework_set_language_defines.md) | Editor locale; language defines |

**DO NOT** (these do not exist; do not invent them):
- `qframework_audio_*`: no AudioKit skills. `AudioKit.Settings` throws outside Play Mode.
- `qframework_fsm_*`, `qframework_singleton_*`, `qframework_pool_*`, `qframework_table_*`: runtime toolkits with no editor entry point.
- `qframework_install`: install the `unitypackage` or `QFramework.cs` by hand.
- `qframework_group_*` and `qframework_entry_*`: those belong to [addressables](../addressables/MODULE.md).
- Batch variants for UIKit settings, ResKit build options or LocaleKit: only architecture codegen and AssetBundle marking have them.

**Routing**: architecture design (four-layer responsibilities, Command, Query, Event, BindableProperty, IOCContainer vs Toolkits IOCKit) → [qframework-design](../qframework-design/MODULE.md). General Addressables authoring → [addressables](../addressables/MODULE.md).
