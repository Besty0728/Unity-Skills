# YooAsset Collector Configuration

A Collector configuration is a tree of Packages, then Groups, then Collectors. The collector skills edit that tree.

## Rules

- Rule names are class names, not display names. Run `yooasset_list_collector_rules` to get valid values, such as `EnableGroup`, `AddressByFileName`, `PackDirectory`, `CollectAll` and `NormalIgnoreRule`.
- `yooasset_add_collector` checks `collectPath` with `AssetDatabase.LoadAssetAtPath`. The path must start with `Assets/`. Absolute paths and `../` paths fail.
- Create skills mark the setting dirty. Modify and remove skills take `save=true` by default. For bulk edits pass `save=false`, then finish with `yooasset_save_collector_config`.
- `yooasset_save_collector_config` writes the setting asset: `AssetBundleCollectorSetting.asset` on 2.x, `BundleCollectorSetting.asset` on 3.x. Its `fixErrors=true` default repairs dangling rule references with `FixFile()`.
- `yooasset_create_collector_package` fails on a duplicate name unless `allowDuplicate=true`.
- `yooasset_modify_collector` finds the collector by `collectPath`. `yooasset_remove_collector` does the same.
- `yooasset_remove_collector_package`, `yooasset_remove_collector_group` and `yooasset_remove_collector` are delete operations. Approval and Auto refuse them.
- `yooasset_open_builder_window` and `yooasset_open_collector_window` open the window of the matching line (`AssetBundle Builder` or `Bundle Builder`, `AssetBundle Collector` or `Bundle Collector`).

## Skills

| Skill | Purpose |
|-------|---------|
| `yooasset_list_collector_packages` | List the Packages, Groups and Collectors tree (`verbose=true` for full detail) |
| `yooasset_list_collector_rules` | List registered Active, Address, Pack, Filter and Ignore rule classes |
| `yooasset_create_collector_package` | Create a Collector Package |
| `yooasset_create_collector_group` | Create a Group in a Package |
| `yooasset_add_collector` | Add a collector to a Group |
| `yooasset_save_collector_config` | Persist the collector setting asset |
| `yooasset_modify_collector_settings` | Change global collector settings |
| `yooasset_modify_collector_package` | Rename or describe a Package and set Package options |
| `yooasset_remove_collector_package` | Remove a Package (delete) |
| `yooasset_modify_collector_group` | Rename or describe a Group and change its active rule and tags |
| `yooasset_remove_collector_group` | Remove a Group (delete) |
| `yooasset_modify_collector` | Change a collector found by `collectPath` |
| `yooasset_remove_collector` | Remove a collector found by `collectPath` (delete) |
| `yooasset_open_collector_window` | Open the Collector window |
