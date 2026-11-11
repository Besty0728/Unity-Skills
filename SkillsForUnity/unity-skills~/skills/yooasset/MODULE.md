# Unity YooAsset Skills

Editor-side automation for the YooAsset hot-update framework: build pipeline orchestration, Collector configuration, build report analysis, PlayMode validation, and the YooAsset editor windows. Parameters come from the schema (`GET /skills/schema?category=YooAsset`); execution and dryRun rules: root [SKILL.md](../../SKILL.md).

> **Requires** `com.tuyoogame.yooasset` 2.3.15+ on the 2.x line or 3.0.4+ on the 3.x line, and Unity 2022.3+. Versions outside both ranges are refused. See [version-scope](reference/version-scope.md).
>
> **Runtime code**: before writing any YooAsset runtime code, load [yooasset-design](../yooasset-design/MODULE.md). Its PlayMode, parameter-class and handle-lifecycle rules are strict.

## When to use
- Configuring collectors (packages, groups, collectors and rules)
- Building or simulating bundles
- Analyzing build reports
- Validating hot-update assets in PlayMode

## Rules
- Only `yooasset_check_installed` works without the package. Every other skill returns `MISSING_PACKAGE` with install instructions when the package is absent. Tell the user; installing is their call.
- Pipeline and parameter pairing is enforced. See [build-and-reports](reference/build-and-reports.md).
- Rule names are class names. Use `yooasset_list_collector_rules` to discover them. See [collectors](reference/collectors.md).
- `yooasset_runtime_validate_package` and `yooasset_runtime_cleanup` enter Play Mode, so Approval and Auto refuse them. The three collector delete skills are also refused under Approval and Auto.
- `yooasset_runtime_validate_package` is asynchronous. Poll `yooasset_runtime_get_validation_result`, and clean up with `yooasset_runtime_cleanup` when the Editor is left in a state you do not want.
- `yooasset_build_bundles` is not undo-tracked.
- Do not call the `yooasset_*assetart*` skills when `apiLine` is `"3.x"`.
- Skill and parameter names do not change between lines. Do not pass 3.x parameter names such as `bundledCopyOption`.

## Pitfalls
- `yooasset_initialize`, `yooasset_load_asset` and `yooasset_create_downloader` are not REST skills. The runtime APIs (`YooAssets.Initialize`, `LoadAssetAsync`, `RequestPackageVersionAsync`, `CreateResourceDownloader`) belong in game code.
- `yooasset_modify_group`, `yooasset_delete_collector` and `yooasset_open_scanner_window` do not exist. Use the schema names.
- `yooasset_install` is not a skill. Installing the package is a Package Manager action for the user.
- `yooasset_build_bundles` does not take `timeout`; the 2.x parameter was removed in 2.3.16.

## Skill Groups

Forty skills in seven groups: environment, build pipeline, YooAsset tools, runtime validation, collector configuration, and build report analysis. The full list with purposes is in [skill-groups](reference/skill-groups.md).

## Reference
- [skill-groups](reference/skill-groups.md): every skill with its purpose
- [version-scope](reference/version-scope.md): version ranges, parameter mapping, cross-line rules
- [build-and-reports](reference/build-and-reports.md): pipelines, report path, report analysis flow
- [collectors](reference/collectors.md): collector rules and save behavior
- [quick-start](reference/quick-start.md): the end-to-end flow as curl calls

## Errors

Transport-level codes: [error codes](../../references/protocol-error-codes.md). Version refusals come back as `SKILL_ERROR` with `reason: "unsupported_package_version"`.
