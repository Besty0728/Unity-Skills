# YooAsset Quick Start

Each step is one `POST /skill/<name>?expectProject=<project>` call. Replace `<port>` and `<project>`.

## 0. Probe the installation

```
curl -s "http://localhost:<port>/skill/yooasset_check_installed?expectProject=<project>" -d '{}'
```

Works even when the package is missing. The response includes `installed`, `packageVersion`, `apiLine` (`"2.x"`, `"3.x"` or null), `supportedRange` and `versionSupported`.

## 1. Configure collectors

```
curl -s "http://localhost:<port>/skill/yooasset_list_collector_rules?expectProject=<project>" -d '{"ruleKind":"all"}'
curl -s "http://localhost:<port>/skill/yooasset_create_collector_package?expectProject=<project>" -d '{"packageName":"DefaultPackage"}'
curl -s "http://localhost:<port>/skill/yooasset_create_collector_group?expectProject=<project>" -d '{"packageName":"DefaultPackage","groupName":"UI","activeRule":"EnableGroup","assetTags":"ui"}'
curl -s "http://localhost:<port>/skill/yooasset_add_collector?expectProject=<project>" -d '{"packageName":"DefaultPackage","groupName":"UI","collectPath":"Assets/GameRes/UI","collectorType":"MainAssetCollector","addressRule":"AddressByFileName","packRule":"PackDirectory","filterRule":"CollectAll"}'
curl -s "http://localhost:<port>/skill/yooasset_modify_collector_package?expectProject=<project>" -d '{"packageName":"DefaultPackage","enableAddressable":true,"supportExtensionless":true,"locationToLower":false}'
curl -s "http://localhost:<port>/skill/yooasset_save_collector_config?expectProject=<project>" -d '{"fixErrors":true}'
```

## 2. Simulate build

Fast, with no file I/O:

```
curl -s "http://localhost:<port>/skill/yooasset_simulate_build?expectProject=<project>" -d '{"packageName":"DefaultPackage"}'
```

## 3. Real build

```
curl -s "http://localhost:<port>/skill/yooasset_build_bundles?expectProject=<project>" -d '{"packageName":"DefaultPackage","packageVersion":"1.0.0","pipeline":"ScriptableBuildPipeline","compression":"LZ4","fileNameStyle":"HashName","verifyBuildingResult":true}'
```

The response includes `outputDirectory`. The report is `<outputDirectory>/<PackageName>_<PackageVersion>.report`; see [build-and-reports](build-and-reports.md).

## 4. Analyze the report

```
curl -s "http://localhost:<port>/skill/yooasset_list_report_bundles?expectProject=<project>" -d '{"reportPath":"<report>","sortBy":"size","limit":20}'
curl -s "http://localhost:<port>/skill/yooasset_list_independ_assets?expectProject=<project>" -d '{"reportPath":"<report>"}'
```

## 5. Optional PlayMode validation

This enters Play Mode and runs asynchronously. Poll the job, then clean up.

```
curl -s "http://localhost:<port>/skill/yooasset_runtime_validate_package?expectProject=<project>" -d '{"packageName":"DefaultPackage","assetLocation":"ui/example","restoreEditMode":true,"cleanup":true}'
curl -s "http://localhost:<port>/skill/yooasset_runtime_get_validation_result?expectProject=<project>" -d '{"jobId":"<jobId>"}'
```
