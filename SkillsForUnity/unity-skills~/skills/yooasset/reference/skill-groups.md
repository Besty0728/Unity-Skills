# YooAsset Skill Groups

Every `yooasset_*` skill (40), grouped by area. Parameters come from the schema: `GET /skills/schema?category=YooAsset`.

## Environment
| Skill | Purpose |
|-------|---------|
| `yooasset_check_installed` | Probe the package version, `apiLine`, `supportedRange` and the pipeline classes present (works without the package) |

## Build pipeline
| Skill | Purpose |
|-------|---------|
| `yooasset_build_bundles` | Build bundles with `ScriptableBuildPipeline` or `RawFileBuildPipeline` |
| `yooasset_simulate_build` | Run the simulate builder for `EditorSimulateMode` (virtual bundles, no files) |
| `yooasset_get_default_paths` | Return the build output root and the StreamingAssets root |
| `yooasset_get_build_settings` | Read the saved Builder settings for a package and pipeline |
| `yooasset_set_build_settings` | Save Builder settings as the UI defaults |
| `yooasset_open_builder_window` | Open the Builder window |
| `yooasset_open_collector_window` | Open the Collector window |

## YooAsset tools
| Skill | Purpose |
|-------|---------|
| `yooasset_open_reporter_window` | Open the Reporter window |
| `yooasset_open_debugger_window` | Open the Debugger window |
| `yooasset_open_assetart_scanner_window` | Open the AssetArt Scanner window (2.x only) |
| `yooasset_list_assetart_scanners` | List AssetArt scanner configurations (2.x only) |
| `yooasset_run_assetart_scanner` | Run one scanner, optionally saving its report (2.x only) |
| `yooasset_run_all_assetart_scanners` | Run all scanners, optionally filtered (2.x only) |
| `yooasset_import_assetart_scanner_config` | Import an AssetArt scanner JSON config (2.x only) |
| `yooasset_export_assetart_scanner_config` | Export an AssetArt scanner JSON config (2.x only) |

## Runtime validation
| Skill | Purpose |
|-------|---------|
| `yooasset_runtime_validate_package` | Start an async PlayMode job that initializes `EditorSimulateMode`, optionally loads one asset and checks the downloader |
| `yooasset_runtime_get_validation_result` | Poll the validation job |
| `yooasset_runtime_cleanup` | Remove finished validation jobs; optionally destroy YooAssets or leave Play Mode |

## Collector configuration
| Skill | Purpose |
|-------|---------|
| `yooasset_list_collector_packages` | List the Package, Group and Collector tree |
| `yooasset_list_collector_rules` | List the registered rule classes |
| `yooasset_create_collector_package` | Create a Package |
| `yooasset_create_collector_group` | Create a Group |
| `yooasset_add_collector` | Add a collector to a Group |
| `yooasset_save_collector_config` | Persist the collector setting asset |
| `yooasset_modify_collector_settings` | Change global collector settings |
| `yooasset_modify_collector_package` | Change Package name, description and options |
| `yooasset_remove_collector_package` | Remove a Package (delete) |
| `yooasset_modify_collector_group` | Change a Group's name, description, active rule and tags |
| `yooasset_remove_collector_group` | Remove a Group (delete) |
| `yooasset_modify_collector` | Change a collector found by `collectPath` |
| `yooasset_remove_collector` | Remove a collector found by `collectPath` (delete) |

## Build report analysis
| Skill | Purpose |
|-------|---------|
| `yooasset_load_build_report` | Read the report summary and totals |
| `yooasset_list_report_bundles` | List bundles with filter, sort and paging |
| `yooasset_get_bundle_detail` | Detail for one bundle, with its dependencies and contents |
| `yooasset_list_report_assets` | List assets with filter, sort and paging |
| `yooasset_get_asset_detail` | Detail for one asset, by path or address |
| `yooasset_get_dependency_graph` | Compact bundle and asset dependency graph |
| `yooasset_compare_build_reports` | Compare two reports |
| `yooasset_list_independ_assets` | List assets no main asset references (cleanup candidates) |
