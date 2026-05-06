using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Newtonsoft.Json;

#if YOO_ASSET
using YooAsset;
using YooAsset.Editor;
#endif

namespace UnitySkills
{
    /// <summary>
    /// YooAsset Editor-side skills — build pipeline orchestration, Collector configuration,
    /// and build-report analysis. Requires com.tuyoogame.yooasset (2.3.15+).
    ///
    /// `yooasset_check_installed` works WITHOUT the package via reflection; every other skill
    /// returns a NoYooAsset() hint when the package is missing. All API calls anchor to YooAsset
    /// 2.3.18 Editor source — see yooasset-design advisory module for the design contract.
    /// </summary>
    public static class YooAssetSkills
    {
        private enum RuntimeValidationStatus
        {
            Queued,
            Running,
            Completed,
            Failed
        }

        private enum RuntimeValidationStage
        {
            Queued,
            WaitingPlayMode,
            InitializeYooAssets,
            WaitInitialize,
            WaitPackageVersion,
            WaitManifest,
            LoadAsset,
            WaitAsset,
            CheckDownloader,
            WaitDownloader,
            Cleanup,
            WaitDestroy,
            Completed,
            Failed
        }

        private sealed class RuntimeValidationJob
        {
            public string JobId;
            public string PackageName;
            public string AssetLocation;
            public bool RestoreEditMode;
            public bool StartedPlayMode;
            public bool Cleanup;
            public bool CheckDownloader;
            public int DownloadingMaxNumber;
            public int FailedTryAgain;
            public RuntimeValidationStatus Status = RuntimeValidationStatus.Queued;
            public RuntimeValidationStage Stage = RuntimeValidationStage.Queued;
            public int Progress;
            public string Error;
            public Dictionary<string, object> Result = new Dictionary<string, object>();
#if YOO_ASSET
            public ResourcePackage Package;
            public InitializationOperation InitializeOperation;
            public RequestPackageVersionOperation RequestVersionOperation;
            public UpdatePackageManifestOperation UpdateManifestOperation;
            public AssetHandle AssetHandle;
            public ResourceDownloaderOperation Downloader;
            public DestroyOperation DestroyOperation;
#endif
        }

        private static readonly Dictionary<string, RuntimeValidationJob> RuntimeValidationJobs =
            new Dictionary<string, RuntimeValidationJob>(StringComparer.OrdinalIgnoreCase);
        private static bool _runtimeValidationUpdateHooked;
        private const string RuntimeValidationJobsPrefKey = "UnitySkills_YooAsset_RuntimeValidationJobs_v1";

        private sealed class RuntimeValidationJobState
        {
            public string JobId;
            public string PackageName;
            public string AssetLocation;
            public bool RestoreEditMode;
            public bool StartedPlayMode;
            public bool Cleanup;
            public bool CheckDownloader;
            public int DownloadingMaxNumber;
            public int FailedTryAgain;
            public string Status;
            public string Stage;
            public int Progress;
            public string Error;
            public Dictionary<string, object> Result;
        }

        private static string ToPayloadValue(RuntimeValidationStatus status)
        {
            return status switch
            {
                RuntimeValidationStatus.Queued => "queued",
                RuntimeValidationStatus.Running => "running",
                RuntimeValidationStatus.Completed => "completed",
                RuntimeValidationStatus.Failed => "failed",
                _ => status.ToString()
            };
        }

        private static RuntimeValidationStatus ParseStatus(string status)
        {
            return status switch
            {
                "running" => RuntimeValidationStatus.Running,
                "completed" => RuntimeValidationStatus.Completed,
                "failed" => RuntimeValidationStatus.Failed,
                _ => RuntimeValidationStatus.Queued
            };
        }

        private static string ToPayloadValue(RuntimeValidationStage stage)
        {
            return stage switch
            {
                RuntimeValidationStage.Queued => "queued",
                RuntimeValidationStage.WaitingPlayMode => "waiting_playmode",
                RuntimeValidationStage.InitializeYooAssets => "initialize_yooassets",
                RuntimeValidationStage.WaitInitialize => "wait_initialize",
                RuntimeValidationStage.WaitPackageVersion => "wait_package_version",
                RuntimeValidationStage.WaitManifest => "wait_manifest",
                RuntimeValidationStage.LoadAsset => "load_asset",
                RuntimeValidationStage.WaitAsset => "wait_asset",
                RuntimeValidationStage.CheckDownloader => "check_downloader",
                RuntimeValidationStage.WaitDownloader => "wait_downloader",
                RuntimeValidationStage.Cleanup => "cleanup",
                RuntimeValidationStage.WaitDestroy => "wait_destroy",
                RuntimeValidationStage.Completed => "completed",
                RuntimeValidationStage.Failed => "failed",
                _ => stage.ToString()
            };
        }

        private static RuntimeValidationStage ParseStage(string stage)
        {
            return stage switch
            {
                "initialize_yooassets" => RuntimeValidationStage.InitializeYooAssets,
                "wait_initialize" => RuntimeValidationStage.WaitInitialize,
                "wait_package_version" => RuntimeValidationStage.WaitPackageVersion,
                "wait_manifest" => RuntimeValidationStage.WaitManifest,
                "load_asset" => RuntimeValidationStage.LoadAsset,
                "wait_asset" => RuntimeValidationStage.WaitAsset,
                "check_downloader" => RuntimeValidationStage.CheckDownloader,
                "wait_downloader" => RuntimeValidationStage.WaitDownloader,
                "cleanup" => RuntimeValidationStage.Cleanup,
                "wait_destroy" => RuntimeValidationStage.WaitDestroy,
                "completed" => RuntimeValidationStage.Completed,
                "failed" => RuntimeValidationStage.Failed,
                "waiting_playmode" => RuntimeValidationStage.WaitingPlayMode,
                _ => RuntimeValidationStage.Queued
            };
        }

#if !YOO_ASSET
        private static object NoYooAsset() => new
        {
            error = "YooAsset package (com.tuyoogame.yooasset) is not installed or below 2.3.15. " +
                    "Install via Window > Package Manager > Add package from git URL > " +
                    "https://github.com/tuyoogame/YooAsset.git?path=Assets/YooAsset#2.3.18"
        };
#endif

        // ==================================================================================
        // A. Environment (1 skill) — works WITHOUT the YOO_ASSET define (pure reflection)
        // ==================================================================================

        [UnitySkill("yooasset_check_installed",
            "Report YooAsset installation status, runtime version, available Editor pipelines, and Collector subsystem availability. Runs with or without the package installed.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "package", "install", "check", "environment" },
            Outputs = new[] { "installed", "packageVersion", "runtimeAssembly", "editorAvailable", "availablePipelines", "hasCollectorSetting", "compileDefineSet" },
            ReadOnly = true)]
        public static object CheckInstalled()
        {
            var runtimeType = Type.GetType("YooAsset.YooAssets, YooAsset");
            if (runtimeType == null)
            {
                return new
                {
                    installed = false,
                    reason = "Runtime type 'YooAsset.YooAssets, YooAsset' is not resolvable. com.tuyoogame.yooasset is not installed.",
                    hint = "Install via Package Manager git URL: https://github.com/tuyoogame/YooAsset.git?path=Assets/YooAsset#2.3.18"
                };
            }

            var runtimeAssemblyVersion = runtimeType.Assembly.GetName().Version?.ToString();
            var editorType = Type.GetType("YooAsset.Editor.AssetBundleCollectorSettingData, YooAsset.Editor");
            var pipelineNames = new[] { "EditorSimulateBuildPipeline", "BuiltinBuildPipeline", "ScriptableBuildPipeline", "RawFileBuildPipeline" };
            var availablePipelines = pipelineNames
                .Where(n => Type.GetType($"YooAsset.Editor.{n}, YooAsset.Editor") != null)
                .ToArray();

            string packageVersion = null;
            try
            {
                var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(runtimeType.Assembly);
                packageVersion = pkgInfo?.version;
            }
            catch { /* best-effort; ignore */ }

            return new
            {
                installed = true,
                packageVersion,
                runtimeAssembly = runtimeAssemblyVersion,
                editorAvailable = editorType != null,
                availablePipelines,
                hasCollectorSetting = editorType != null,
                compileDefineSet =
#if YOO_ASSET
                    true,
#else
                    false,
#endif
                note = "compileDefineSet=false means YOO_ASSET symbol is not defined; other yooasset_* skills will return NoYooAsset() until Unity recompiles with the package active."
            };
        }

        // ==================================================================================
        // B. Build pipeline (5 skills)
        // ==================================================================================

        [UnitySkill("yooasset_build_bundles",
            "Build YooAsset bundles via ScriptableBuildPipeline or RawFileBuildPipeline. Writes bundles to BuildOutputRoot/<PackageName>/<Version>.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "build", "bundles", "pipeline" },
            Outputs = new[] { "success", "outputDirectory", "packageVersion", "errorInfo", "failedTask" },
            MutatesAssets = true, RiskLevel = "medium", SupportsDryRun = false,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object BuildBundles(
            string packageName,
            string packageVersion = "auto",
            string pipeline = "ScriptableBuildPipeline",
            string buildTarget = null,
            string compression = "LZ4",
            string fileNameStyle = "HashName",
            bool clearBuildCache = false,
            bool useAssetDependencyDB = true,
            bool verifyBuildingResult = true,
            bool replaceAssetPathWithAddress = false,
            bool stripUnityVersion = false,
            bool disableWriteTypeTree = false,
            bool trackSpriteAtlasDependencies = false,
            bool writeLinkXML = true,
            string cacheServerHost = "",
            int cacheServerPort = 0,
            string builtinShadersBundleName = "",
            string monoScriptsBundleName = "",
            bool includePathInHash = false,
            string buildinFileCopyOption = "None",
            string buildinFileCopyParams = "",
            bool enableLog = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName))
                return new { error = "packageName is required." };

            if (!Enum.TryParse<EBuildPipeline>(pipeline, out var eBp))
                return new { error = $"Unknown pipeline: {pipeline}. Available: {string.Join(", ", Enum.GetNames(typeof(EBuildPipeline)))}" };

            if (eBp == EBuildPipeline.EditorSimulateBuildPipeline)
                return new { error = "Use yooasset_simulate_build for EditorSimulateBuildPipeline." };

            BuildTarget target;
            if (string.IsNullOrEmpty(buildTarget))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
            }
            else if (!Enum.TryParse(buildTarget, true, out target))
            {
                return new { error = $"Unknown buildTarget: {buildTarget}. Available: {string.Join(", ", Enum.GetNames(typeof(BuildTarget)))}" };
            }

            var version = (packageVersion == "auto" || string.IsNullOrEmpty(packageVersion))
                ? DateTime.UtcNow.ToString("yyyyMMddHHmm")
                : packageVersion;

            if (!Enum.TryParse<EFileNameStyle>(fileNameStyle, out var eFns))
                return new { error = $"Unknown fileNameStyle: {fileNameStyle}. Available: HashName, BundleName, BundleName_HashName" };

            BuildParameters buildParameters;
            IBuildPipeline iPipeline;
            int buildBundleType;

            if (eBp == EBuildPipeline.ScriptableBuildPipeline)
            {
                if (!Enum.TryParse<ECompressOption>(compression, out var eCompress))
                    return new { error = $"Unknown compression: {compression}. Available: Uncompressed, LZMA, LZ4" };

                var sbp = new ScriptableBuildParameters
                {
                    CompressOption = eCompress,
                    ReplaceAssetPathWithAddress = replaceAssetPathWithAddress,
                    StripUnityVersion = stripUnityVersion,
                    DisableWriteTypeTree = disableWriteTypeTree,
                    TrackSpriteAtlasDependencies = trackSpriteAtlasDependencies,
                    WriteLinkXML = writeLinkXML,
                    CacheServerHost = string.IsNullOrWhiteSpace(cacheServerHost) ? null : cacheServerHost,
                    CacheServerPort = cacheServerPort,
                    BuiltinShadersBundleName = string.IsNullOrWhiteSpace(builtinShadersBundleName) ? null : builtinShadersBundleName,
                    MonoScriptsBundleName = string.IsNullOrWhiteSpace(monoScriptsBundleName) ? null : monoScriptsBundleName
                };
                buildParameters = sbp;
                iPipeline = new ScriptableBuildPipeline();
                buildBundleType = (int)EBuildBundleType.AssetBundle;
            }
            else if (eBp == EBuildPipeline.RawFileBuildPipeline)
            {
                buildParameters = new RawFileBuildParameters
                {
                    IncludePathInHash = includePathInHash
                };
                iPipeline = new RawFileBuildPipeline();
                buildBundleType = (int)EBuildBundleType.RawBundle;
            }
            else // BuiltinBuildPipeline
            {
                return new { error = "BuiltinBuildPipeline is legacy and no longer recommended; use ScriptableBuildPipeline." };
            }

            buildParameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            buildParameters.BuildPipeline = eBp.ToString();
            buildParameters.BuildBundleType = buildBundleType;
            buildParameters.BuildTarget = target;
            buildParameters.PackageName = packageName;
            buildParameters.PackageVersion = version;
            buildParameters.FileNameStyle = eFns;
            buildParameters.ClearBuildCacheFiles = clearBuildCache;
            buildParameters.UseAssetDependencyDB = useAssetDependencyDB;
            buildParameters.VerifyBuildingResult = verifyBuildingResult;
            if (!Enum.TryParse<EBuildinFileCopyOption>(buildinFileCopyOption, out var eCopy))
                return new { error = $"Unknown buildinFileCopyOption: {buildinFileCopyOption}. Available: {string.Join(", ", Enum.GetNames(typeof(EBuildinFileCopyOption)))}" };
            buildParameters.BuildinFileCopyOption = eCopy;
            buildParameters.BuildinFileCopyParams = buildinFileCopyParams ?? string.Empty;

            BuildResult result;
            try
            {
                result = iPipeline.Run(buildParameters, enableLog);
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name };
            }

            return new
            {
                success = result.Success,
                packageName,
                packageVersion = version,
                pipeline = eBp.ToString(),
                buildTarget = target.ToString(),
                outputDirectory = result.OutputPackageDirectory,
                errorInfo = result.ErrorInfo,
                failedTask = result.FailedTask,
                reportPath = result.Success ? Path.Combine(result.OutputPackageDirectory, $"{packageName}_{version}.report") : null
            };
#endif
        }

        [UnitySkill("yooasset_simulate_build",
            "Run the EditorSimulateBuildPipeline for a package — produces a virtual bundle map without writing real bundles. Use for EditorSimulateMode during development.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "simulate", "editor", "pipeline" },
            Outputs = new[] { "success", "packageRootDirectory" },
            RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object SimulateBuild(string packageName)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName))
                return new { error = "packageName is required." };

            try
            {
                var param = new PackageInvokeBuildParam(packageName)
                {
                    BuildPipelineName = EBuildPipeline.EditorSimulateBuildPipeline.ToString()
                };
                var result = AssetBundleSimulateBuilder.SimulateBuild(param);
                return new
                {
                    success = true,
                    packageName,
                    packageRootDirectory = result.PackageRootDirectory
                };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name };
            }
#endif
        }

        [UnitySkill("yooasset_get_default_paths",
            "Return the default build output directory (under <projectPath>/Bundles) and the StreamingAssets root YooAsset ships bundles into.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "path", "build", "streamingassets" },
            Outputs = new[] { "defaultBuildOutputRoot", "streamingAssetsRoot" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object GetDefaultPaths()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            return new
            {
                defaultBuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot(),
                streamingAssetsRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot()
            };
#endif
        }

        [UnitySkill("yooasset_get_build_settings",
            "Read YooAsset AssetBundle Builder EditorPrefs for a package and pipeline.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "build", "settings", "query" },
            Outputs = new[] { "packageName", "pipeline", "compression", "fileNameStyle" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object GetBuildSettings(string packageName, string pipeline = null)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName)) return new { error = "packageName is required." };
            var buildPipeline = string.IsNullOrEmpty(pipeline)
                ? AssetBundleBuilderSetting.GetPackageBuildPipeline(packageName)
                : pipeline;
            return new
            {
                packageName,
                pipeline = buildPipeline,
                compression = AssetBundleBuilderSetting.GetPackageCompressOption(packageName, buildPipeline).ToString(),
                fileNameStyle = AssetBundleBuilderSetting.GetPackageFileNameStyle(packageName, buildPipeline).ToString(),
                buildinFileCopyOption = AssetBundleBuilderSetting.GetPackageBuildinFileCopyOption(packageName, buildPipeline).ToString(),
                buildinFileCopyParams = AssetBundleBuilderSetting.GetPackageBuildinFileCopyParams(packageName, buildPipeline),
                encryptionServicesClassName = AssetBundleBuilderSetting.GetPackageEncyptionServicesClassName(packageName, buildPipeline),
                manifestProcessServicesClassName = AssetBundleBuilderSetting.GetPackageManifestProcessServicesClassName(packageName, buildPipeline),
                manifestRestoreServicesClassName = AssetBundleBuilderSetting.GetPackageManifestRestoreServicesClassName(packageName, buildPipeline),
                clearBuildCache = AssetBundleBuilderSetting.GetPackageClearBuildCache(packageName, buildPipeline),
                useAssetDependencyDB = AssetBundleBuilderSetting.GetPackageUseAssetDependencyDB(packageName, buildPipeline)
            };
#endif
        }

        [UnitySkill("yooasset_set_build_settings",
            "Persist YooAsset AssetBundle Builder EditorPrefs for a package and pipeline.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "build", "settings", "modify" },
            Outputs = new[] { "success", "packageName", "pipeline" },
            MutatesAssets = false, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object SetBuildSettings(
            string packageName,
            string pipeline = "ScriptableBuildPipeline",
            string compression = "LZ4",
            string fileNameStyle = "HashName",
            string buildinFileCopyOption = "None",
            string buildinFileCopyParams = "",
            string encryptionServicesClassName = null,
            string manifestProcessServicesClassName = null,
            string manifestRestoreServicesClassName = null,
            bool clearBuildCache = false,
            bool useAssetDependencyDB = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName)) return new { error = "packageName is required." };
            if (!Enum.TryParse<EBuildPipeline>(pipeline, out _))
                return new { error = $"Unknown pipeline: {pipeline}." };
            if (!Enum.TryParse<ECompressOption>(compression, out var eCompress))
                return new { error = $"Unknown compression: {compression}." };
            if (!Enum.TryParse<EFileNameStyle>(fileNameStyle, out var eFileNameStyle))
                return new { error = $"Unknown fileNameStyle: {fileNameStyle}." };
            if (!Enum.TryParse<EBuildinFileCopyOption>(buildinFileCopyOption, out var eCopy))
                return new { error = $"Unknown buildinFileCopyOption: {buildinFileCopyOption}." };

            AssetBundleBuilderSetting.SetPackageBuildPipeline(packageName, pipeline);
            AssetBundleBuilderSetting.SetPackageCompressOption(packageName, pipeline, eCompress);
            AssetBundleBuilderSetting.SetPackageFileNameStyle(packageName, pipeline, eFileNameStyle);
            AssetBundleBuilderSetting.SetPackageBuildinFileCopyOption(packageName, pipeline, eCopy);
            AssetBundleBuilderSetting.SetPackageBuildinFileCopyParams(packageName, pipeline, buildinFileCopyParams ?? string.Empty);
            AssetBundleBuilderSetting.SetPackageClearBuildCache(packageName, pipeline, clearBuildCache);
            AssetBundleBuilderSetting.SetPackageUseAssetDependencyDB(packageName, pipeline, useAssetDependencyDB);
            if (encryptionServicesClassName != null)
                AssetBundleBuilderSetting.SetPackageEncyptionServicesClassName(packageName, pipeline, encryptionServicesClassName);
            if (manifestProcessServicesClassName != null)
                AssetBundleBuilderSetting.SetPackageManifestProcessServicesClassName(packageName, pipeline, manifestProcessServicesClassName);
            if (manifestRestoreServicesClassName != null)
                AssetBundleBuilderSetting.SetPackageManifestRestoreServicesClassName(packageName, pipeline, manifestRestoreServicesClassName);
            return new { success = true, packageName, pipeline };
#endif
        }

        [UnitySkill("yooasset_open_builder_window",
            "Open the YooAsset 'AssetBundle Builder' Editor window (menu: YooAsset/AssetBundle Builder).",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "window", "builder", "editor" },
            Outputs = new[] { "opened" },
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object OpenBuilderWindow()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            bool opened = EditorApplication.ExecuteMenuItem("YooAsset/AssetBundle Builder");
            return new { opened, menuPath = "YooAsset/AssetBundle Builder" };
#endif
        }

        [UnitySkill("yooasset_open_collector_window",
            "Open the YooAsset 'AssetBundle Collector' Editor window (menu: YooAsset/AssetBundle Collector).",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "window", "collector", "editor" },
            Outputs = new[] { "opened" },
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object OpenCollectorWindow()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            bool opened = EditorApplication.ExecuteMenuItem("YooAsset/AssetBundle Collector");
            return new { opened, menuPath = "YooAsset/AssetBundle Collector" };
#endif
        }

        [UnitySkill("yooasset_open_reporter_window",
            "Open the YooAsset AssetBundle Reporter window.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "window", "reporter", "tool" },
            Outputs = new[] { "opened" },
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object OpenReporterWindow()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            bool opened = EditorApplication.ExecuteMenuItem("YooAsset/AssetBundle Reporter");
            return new { opened, menuPath = "YooAsset/AssetBundle Reporter" };
#endif
        }

        [UnitySkill("yooasset_open_debugger_window",
            "Open the YooAsset AssetBundle Debugger window.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "window", "debugger", "tool" },
            Outputs = new[] { "opened" },
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object OpenDebuggerWindow()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            bool opened = EditorApplication.ExecuteMenuItem("YooAsset/AssetBundle Debugger");
            return new { opened, menuPath = "YooAsset/AssetBundle Debugger" };
#endif
        }

        [UnitySkill("yooasset_open_assetart_scanner_window",
            "Open the YooAsset AssetArt Scanner window.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "window", "assetart", "scanner", "tool" },
            Outputs = new[] { "opened" },
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object OpenAssetArtScannerWindow()
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var type = Type.GetType("YooAsset.Editor.AssetArtScannerWindow, YooAsset.Editor");
            if (type == null) return new { error = "AssetArtScannerWindow type not found." };
            var method = type.GetMethod("OpenWindow", BindingFlags.Public | BindingFlags.Static);
            method?.Invoke(null, null);
            return new { opened = method != null, windowType = type.FullName };
#endif
        }

        [UnitySkill("yooasset_list_assetart_scanners",
            "List YooAsset AssetArtScanner configurations.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "assetart", "scanner", "list" },
            Outputs = new[] { "scannerCount", "scanners" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListAssetArtScanners(string keyword = null)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var scanners = AssetArtScannerSettingData.Setting.Scanners
                .Where(s => string.IsNullOrEmpty(keyword) || s.CheckKeyword(keyword))
                .Select(s => new
                {
                    guid = s.ScannerGUID,
                    name = s.ScannerName,
                    desc = s.ScannerDesc,
                    schema = s.ScannerSchema,
                    saveDirectory = s.SaveDirectory,
                    collectorCount = s.Collectors?.Count ?? 0,
                    whiteListCount = s.WhiteList?.Count ?? 0
                }).ToArray();
            return new { scannerCount = scanners.Length, scanners };
#endif
        }

        [UnitySkill("yooasset_run_assetart_scanner",
            "Run one YooAsset AssetArtScanner and optionally save its report file.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "assetart", "scanner", "run" },
            Outputs = new[] { "success", "scannerGUID", "reportSaved" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RunAssetArtScanner(string scannerGUID, string saveDirectory = null)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(scannerGUID)) return new { error = "scannerGUID is required." };
            ScannerResult result;
            try { result = AssetArtScannerSettingData.Scan(scannerGUID); }
            catch (Exception ex) { return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name }; }
            bool reportSaved = false;
            if (!string.IsNullOrEmpty(saveDirectory) && result != null && result.Report != null)
            {
                result.SaveReportFile(saveDirectory);
                reportSaved = true;
            }
            return new
            {
                success = result != null && result.Succeed,
                scannerGUID,
                error = result?.ErrorInfo,
                errorStack = result?.ErrorStack,
                reportSaved,
                saveDirectory
            };
#endif
        }

        [UnitySkill("yooasset_run_all_assetart_scanners",
            "Run all YooAsset AssetArtScanners, optionally filtered by keyword.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "assetart", "scanner", "run", "all" },
            Outputs = new[] { "success", "scannerCount" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RunAllAssetArtScanners(string keyword = null)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            try
            {
                if (string.IsNullOrEmpty(keyword)) AssetArtScannerSettingData.ScanAll();
                else AssetArtScannerSettingData.ScanAll(keyword);
                return new { success = true, scannerCount = AssetArtScannerSettingData.Setting.Scanners.Count, keyword };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name };
            }
#endif
        }

        [UnitySkill("yooasset_import_assetart_scanner_config",
            "Import YooAsset AssetArtScanner JSON config.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "assetart", "scanner", "config", "import" },
            Outputs = new[] { "success", "configPath" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ImportAssetArtScannerConfig(string configPath)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(configPath)) return new { error = "configPath is required." };
            if (!File.Exists(configPath)) return new { error = $"Config file not found: {configPath}" };
            try
            {
                AssetArtScannerConfig.ImportJsonConfig(configPath);
                return new { success = true, configPath, scannerCount = AssetArtScannerSettingData.Setting.Scanners.Count };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name };
            }
#endif
        }

        [UnitySkill("yooasset_export_assetart_scanner_config",
            "Export YooAsset AssetArtScanner JSON config.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "assetart", "scanner", "config", "export" },
            Outputs = new[] { "success", "configPath" },
            MutatesAssets = false, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ExportAssetArtScannerConfig(string configPath)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(configPath)) return new { error = "configPath is required." };
            try
            {
                AssetArtScannerConfig.ExportJsonConfig(configPath);
                return new { success = true, configPath };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, exceptionType = ex.GetType().Name };
            }
#endif
        }

        [UnitySkill("yooasset_runtime_validate_package",
            "Start a PlayMode runtime validation job for YooAsset EditorSimulateMode initialization, asset load/release, downloader status, and cleanup.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "runtime", "playmode", "validate", "load" },
            Outputs = new[] { "jobId", "status", "packageName" },
            MayEnterPlayMode = true, SupportsDryRun = false, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RuntimeValidatePackage(
            string packageName,
            string assetLocation = null,
            bool restoreEditMode = true,
            bool cleanup = true,
            bool checkDownloader = true,
            int downloadingMaxNumber = 4,
            int failedTryAgain = 1)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName)) return new { error = "packageName is required." };
            var job = new RuntimeValidationJob
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 8),
                PackageName = packageName,
                AssetLocation = assetLocation,
                RestoreEditMode = restoreEditMode,
                Cleanup = cleanup,
                CheckDownloader = checkDownloader,
                DownloadingMaxNumber = Math.Max(1, downloadingMaxNumber),
                FailedTryAgain = Math.Max(0, failedTryAgain),
                Status = RuntimeValidationStatus.Queued,
                Stage = RuntimeValidationStage.WaitingPlayMode,
                Progress = 0
            };
            RuntimeValidationJobs[job.JobId] = job;
            PersistRuntimeValidationJobs();
            EnsureRuntimeValidationUpdateHooked();
            return new
            {
                jobId = job.JobId,
                status = ToPayloadValue(job.Status),
                stage = ToPayloadValue(job.Stage),
                packageName,
                assetLocation,
                restoreEditMode,
                cleanup,
                checkDownloader
            };
#endif
        }

        [UnitySkill("yooasset_runtime_get_validation_result",
            "Get the current status/result for a YooAsset runtime validation job.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "runtime", "playmode", "validation", "result" },
            Outputs = new[] { "jobId", "status", "stage", "result" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RuntimeGetValidationResult(string jobId)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(jobId)) return new { error = "jobId is required." };
            if (!RuntimeValidationJobs.TryGetValue(jobId, out var job))
                return new { error = $"Runtime validation job '{jobId}' not found." };
            return new
            {
                jobId = job.JobId,
                packageName = job.PackageName,
                status = ToPayloadValue(job.Status),
                stage = ToPayloadValue(job.Stage),
                progress = job.Progress,
                lastError = job.Error,
                result = job.Result
            };
#endif
        }

        [UnitySkill("yooasset_runtime_cleanup",
            "Clean completed YooAsset runtime validation jobs and optionally force YooAssets cleanup / exit Play Mode.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Execute,
            Tags = new[] { "yooasset", "runtime", "cleanup", "playmode" },
            Outputs = new[] { "success", "removedJobs" },
            MayEnterPlayMode = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RuntimeCleanup(string jobId = null, bool forceYooAssetsDestroy = false, bool exitPlayMode = false)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var removed = 0;
            if (string.IsNullOrEmpty(jobId))
            {
                removed = RuntimeValidationJobs.Count;
                RuntimeValidationJobs.Clear();
            }
            else if (RuntimeValidationJobs.Remove(jobId))
            {
                removed = 1;
            }
            PersistRuntimeValidationJobs();

            if (forceYooAssetsDestroy && Application.isPlaying && YooAssets.Initialized)
                YooAssets.Destroy();
            if (exitPlayMode && EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            return new { success = true, removedJobs = removed, forceYooAssetsDestroy, exitPlayMode };
#endif
        }

        // ==================================================================================
        // C. Collector configuration (6 skills)
        // ==================================================================================

        [UnitySkill("yooasset_list_collector_packages",
            "List all collector packages with their groups and collectors. Set verbose=true for full group + collector trees.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "collector", "package", "list" },
            Outputs = new[] { "packageCount", "packages" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListCollectorPackages(bool verbose = false)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var setting = AssetBundleCollectorSettingData.Setting;
            var packages = setting.Packages.Select(p =>
            {
                var collectorCount = p.Groups.Sum(g => g.Collectors.Count);
                var allTags = p.GetAllTags();
                if (!verbose)
                {
                    return (object)new
                    {
                        name = p.PackageName,
                        desc = p.PackageDesc,
                        groupCount = p.Groups.Count,
                        collectorCount,
                        allTags,
                        enableAddressable = p.EnableAddressable,
                        supportExtensionless = p.SupportExtensionless,
                        locationToLower = p.LocationToLower,
                        includeAssetGUID = p.IncludeAssetGUID,
                        autoCollectShaders = p.AutoCollectShaders,
                        ignoreRule = p.IgnoreRuleName
                    };
                }
                return new
                {
                    name = p.PackageName,
                    desc = p.PackageDesc,
                    groupCount = p.Groups.Count,
                    collectorCount,
                    allTags,
                    enableAddressable = p.EnableAddressable,
                    supportExtensionless = p.SupportExtensionless,
                    locationToLower = p.LocationToLower,
                    includeAssetGUID = p.IncludeAssetGUID,
                    autoCollectShaders = p.AutoCollectShaders,
                    ignoreRule = p.IgnoreRuleName,
                    groups = p.Groups.Select(g => new
                    {
                        name = g.GroupName,
                        desc = g.GroupDesc,
                        activeRule = g.ActiveRuleName,
                        assetTags = g.AssetTags,
                        collectors = g.Collectors.Select(c => new
                        {
                            collectPath = c.CollectPath,
                            collectorType = c.CollectorType.ToString(),
                            addressRule = c.AddressRuleName,
                            packRule = c.PackRuleName,
                            filterRule = c.FilterRuleName,
                            assetTags = c.AssetTags,
                            userData = c.UserData
                        }).ToArray()
                    }).ToArray()
                };
            }).ToArray();

            return new
            {
                packageCount = packages.Length,
                showPackageView = setting.ShowPackageView,
                uniqueBundleName = setting.UniqueBundleName,
                packages
            };
#endif
        }

        [UnitySkill("yooasset_list_collector_rules",
            "List available Address / Pack / Filter / Active / Ignore rule classes registered in the AssetBundleCollectorSettingData. Set ruleKind=<all|addressRule|packRule|filterRule|activeRule|ignoreRule>.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Query,
            Tags = new[] { "yooasset", "collector", "rule", "list" },
            Outputs = new[] { "activeRules", "addressRules", "packRules", "filterRules", "ignoreRules" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListCollectorRules(string ruleKind = "all")
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var kind = (ruleKind ?? "all").ToLowerInvariant();
            var active = AssetBundleCollectorSettingData.GetActiveRuleNames().Select(r => new { className = r.ClassName, displayName = r.DisplayName }).ToArray();
            var address = AssetBundleCollectorSettingData.GetAddressRuleNames().Select(r => new { className = r.ClassName, displayName = r.DisplayName }).ToArray();
            var pack = AssetBundleCollectorSettingData.GetPackRuleNames().Select(r => new { className = r.ClassName, displayName = r.DisplayName }).ToArray();
            var filter = AssetBundleCollectorSettingData.GetFilterRuleNames().Select(r => new { className = r.ClassName, displayName = r.DisplayName }).ToArray();
            var ignore = AssetBundleCollectorSettingData.GetIgnoreRuleNames().Select(r => new { className = r.ClassName, displayName = r.DisplayName }).ToArray();

            switch (kind)
            {
                case "activerule":  return new { activeRules = active };
                case "addressrule": return new { addressRules = address };
                case "packrule":    return new { packRules = pack };
                case "filterrule":  return new { filterRules = filter };
                case "ignorerule":  return new { ignoreRules = ignore };
                default:
                    return new { activeRules = active, addressRules = address, packRules = pack, filterRules = filter, ignoreRules = ignore };
            }
#endif
        }

        [UnitySkill("yooasset_create_collector_package",
            "Create a new collector package. Fails if a package with the same name already exists (unless allowDuplicate=true).",
            TracksWorkflow = true,
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Create,
            Tags = new[] { "yooasset", "collector", "package", "create" },
            Outputs = new[] { "success", "packageName" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object CreateCollectorPackage(string packageName, bool allowDuplicate = false)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName))
                return new { error = "packageName is required." };

            var setting = AssetBundleCollectorSettingData.Setting;
            if (!allowDuplicate && setting.Packages.Any(p => p.PackageName == packageName))
                return new { error = $"Package '{packageName}' already exists. Pass allowDuplicate=true to override the check." };

            var pkg = AssetBundleCollectorSettingData.CreatePackage(packageName);
            AssetBundleCollectorSettingData.SaveFile();
            return new
            {
                success = true,
                packageName = pkg.PackageName,
                groupCount = pkg.Groups.Count
            };
#endif
        }

        [UnitySkill("yooasset_create_collector_group",
            "Create a new group inside an existing collector package. Validates activeRule against the registered IActiveRule list.",
            TracksWorkflow = true,
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Create,
            Tags = new[] { "yooasset", "collector", "group", "create" },
            Outputs = new[] { "success", "packageName", "groupName" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object CreateCollectorGroup(
            string packageName,
            string groupName,
            string groupDesc = "",
            string activeRule = "EnableGroup",
            string assetTags = "")
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName)) return new { error = "packageName is required." };
            if (string.IsNullOrEmpty(groupName))   return new { error = "groupName is required." };

            var setting = AssetBundleCollectorSettingData.Setting;
            var pkg = setting.Packages.FirstOrDefault(p => p.PackageName == packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found. Use yooasset_create_collector_package first." };

            if (!AssetBundleCollectorSettingData.HasActiveRuleName(activeRule))
                return new { error = $"Unknown activeRule: {activeRule}. Use yooasset_list_collector_rules ruleKind=activeRule to list valid names." };

            if (pkg.Groups.Any(g => g.GroupName == groupName))
                return new { error = $"Group '{groupName}' already exists in package '{packageName}'." };

            var group = AssetBundleCollectorSettingData.CreateGroup(pkg, groupName);
            group.GroupDesc = groupDesc;
            group.ActiveRuleName = activeRule;
            group.AssetTags = assetTags;
            AssetBundleCollectorSettingData.SaveFile();

            return new
            {
                success = true,
                packageName = pkg.PackageName,
                groupName = group.GroupName,
                activeRule = group.ActiveRuleName
            };
#endif
        }

        [UnitySkill("yooasset_add_collector",
            "Add an AssetBundleCollector to an existing group. Validates collectPath via AssetDatabase and each rule via its registered name.",
            TracksWorkflow = true,
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Create,
            Tags = new[] { "yooasset", "collector", "create", "add" },
            Outputs = new[] { "success", "packageName", "groupName", "collectPath" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object AddCollector(
            string packageName,
            string groupName,
            string collectPath,
            string collectorType = "MainAssetCollector",
            string addressRule = "AddressByFileName",
            string packRule = "PackDirectory",
            string filterRule = "CollectAll",
            string assetTags = "",
            string userData = "")
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(packageName)) return new { error = "packageName is required." };
            if (string.IsNullOrEmpty(groupName))   return new { error = "groupName is required." };
            if (string.IsNullOrEmpty(collectPath)) return new { error = "collectPath is required (Asset path to a folder or single asset)." };

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(collectPath) == null)
                return new { error = $"collectPath '{collectPath}' does not resolve to a valid asset. Use an Asset path such as 'Assets/Resources/Prefabs'." };

            if (!Enum.TryParse<ECollectorType>(collectorType, out var eType))
                return new { error = $"Unknown collectorType: {collectorType}. Available: MainAssetCollector, StaticAssetCollector, DependAssetCollector." };

            if (!AssetBundleCollectorSettingData.HasAddressRuleName(addressRule))
                return new { error = $"Unknown addressRule: {addressRule}." };
            if (!AssetBundleCollectorSettingData.HasPackRuleName(packRule))
                return new { error = $"Unknown packRule: {packRule}." };
            if (!AssetBundleCollectorSettingData.HasFilterRuleName(filterRule))
                return new { error = $"Unknown filterRule: {filterRule}." };

            var setting = AssetBundleCollectorSettingData.Setting;
            var pkg = setting.Packages.FirstOrDefault(p => p.PackageName == packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found." };
            var group = pkg.Groups.FirstOrDefault(g => g.GroupName == groupName);
            if (group == null) return new { error = $"Group '{groupName}' not found in package '{packageName}'." };

            var collector = new AssetBundleCollector
            {
                CollectPath = collectPath,
                CollectorGUID = AssetDatabase.AssetPathToGUID(collectPath),
                CollectorType = eType,
                AddressRuleName = addressRule,
                PackRuleName = packRule,
                FilterRuleName = filterRule,
                AssetTags = assetTags,
                UserData = userData
            };
            AssetBundleCollectorSettingData.CreateCollector(group, collector);
            AssetBundleCollectorSettingData.SaveFile();

            return new
            {
                success = true,
                packageName = pkg.PackageName,
                groupName = group.GroupName,
                collectPath = collector.CollectPath,
                collectorType = collector.CollectorType.ToString(),
                addressRule, packRule, filterRule
            };
#endif
        }

        [UnitySkill("yooasset_save_collector_config",
            "Persist the AssetBundleCollectorSetting ScriptableObject to disk; optionally run FixFile() first to repair dangling rule names.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "collector", "save", "persist" },
            Outputs = new[] { "saved", "fixed", "isDirty" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object SaveCollectorConfig(bool fixErrors = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            bool fixedApplied = false;
            if (fixErrors)
            {
                AssetBundleCollectorSettingData.FixFile();
                fixedApplied = true;
            }
            AssetBundleCollectorSettingData.SaveFile();
            return new
            {
                saved = true,
                fixed_ = fixedApplied,
                isDirty = AssetBundleCollectorSettingData.IsDirty
            };
#endif
        }

        [UnitySkill("yooasset_modify_collector_settings",
            "Modify global AssetBundleCollectorSetting flags such as package view and unique bundle names.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "collector", "settings", "modify" },
            Outputs = new[] { "success", "showPackageView", "uniqueBundleName" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ModifyCollectorSettings(bool showPackageView = false, bool uniqueBundleName = false, bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            AssetBundleCollectorSettingData.ModifyShowPackageView(showPackageView);
            AssetBundleCollectorSettingData.ModifyUniqueBundleName(uniqueBundleName);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, showPackageView, uniqueBundleName, saved = save };
#endif
        }

        [UnitySkill("yooasset_modify_collector_package",
            "Modify package-level collector options: description, addressable mode, extensionless support, GUID inclusion, shader auto-collection, and ignore rule.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "collector", "package", "modify" },
            Outputs = new[] { "success", "packageName" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ModifyCollectorPackage(
            string packageName,
            string packageDesc = null,
            string newPackageName = null,
            bool enableAddressable = false,
            bool supportExtensionless = true,
            bool locationToLower = false,
            bool includeAssetGUID = false,
            bool autoCollectShaders = true,
            string ignoreRule = "NormalIgnoreRule",
            bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var pkg = FindCollectorPackage(packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found." };
            if (!AssetBundleCollectorSettingData.HasIgnoreRuleName(ignoreRule))
                return new { error = $"Unknown ignoreRule: {ignoreRule}." };
            if (!string.IsNullOrWhiteSpace(newPackageName) && newPackageName != packageName)
            {
                if (AssetBundleCollectorSettingData.Setting.Packages.Any(p => p.PackageName == newPackageName))
                    return new { error = $"Package '{newPackageName}' already exists." };
                pkg.PackageName = newPackageName;
            }
            if (packageDesc != null) pkg.PackageDesc = packageDesc;
            pkg.EnableAddressable = enableAddressable;
            pkg.SupportExtensionless = supportExtensionless;
            pkg.LocationToLower = locationToLower;
            pkg.IncludeAssetGUID = includeAssetGUID;
            pkg.AutoCollectShaders = autoCollectShaders;
            pkg.IgnoreRuleName = ignoreRule;
            AssetBundleCollectorSettingData.ModifyPackage(pkg);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new
            {
                success = true,
                packageName = pkg.PackageName,
                packageDesc = pkg.PackageDesc,
                enableAddressable,
                supportExtensionless,
                locationToLower,
                includeAssetGUID,
                autoCollectShaders,
                ignoreRule,
                saved = save
            };
#endif
        }

        [UnitySkill("yooasset_remove_collector_package",
            "Remove a collector package by name.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Delete,
            Tags = new[] { "yooasset", "collector", "package", "remove", "delete" },
            Outputs = new[] { "success", "packageName" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RemoveCollectorPackage(string packageName, bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var pkg = FindCollectorPackage(packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found." };
            AssetBundleCollectorSettingData.RemovePackage(pkg);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, packageName, saved = save };
#endif
        }

        [UnitySkill("yooasset_modify_collector_group",
            "Modify a collector group name, description, active rule, or tags.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "collector", "group", "modify" },
            Outputs = new[] { "success", "packageName", "groupName" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ModifyCollectorGroup(
            string packageName,
            string groupName,
            string newGroupName = null,
            string groupDesc = null,
            string activeRule = "EnableGroup",
            string assetTags = null,
            bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var pkg = FindCollectorPackage(packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found." };
            var group = FindCollectorGroup(pkg, groupName);
            if (group == null) return new { error = $"Group '{groupName}' not found in package '{packageName}'." };
            if (!AssetBundleCollectorSettingData.HasActiveRuleName(activeRule))
                return new { error = $"Unknown activeRule: {activeRule}." };
            if (!string.IsNullOrWhiteSpace(newGroupName) && newGroupName != groupName)
            {
                if (pkg.Groups.Any(g => g.GroupName == newGroupName))
                    return new { error = $"Group '{newGroupName}' already exists in package '{packageName}'." };
                group.GroupName = newGroupName;
            }
            if (groupDesc != null) group.GroupDesc = groupDesc;
            if (assetTags != null) group.AssetTags = assetTags;
            group.ActiveRuleName = activeRule;
            AssetBundleCollectorSettingData.ModifyGroup(pkg, group);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, packageName, groupName = group.GroupName, activeRule, assetTags = group.AssetTags, saved = save };
#endif
        }

        [UnitySkill("yooasset_remove_collector_group",
            "Remove a collector group from a package.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Delete,
            Tags = new[] { "yooasset", "collector", "group", "remove", "delete" },
            Outputs = new[] { "success", "packageName", "groupName" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RemoveCollectorGroup(string packageName, string groupName, bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var pkg = FindCollectorPackage(packageName);
            if (pkg == null) return new { error = $"Package '{packageName}' not found." };
            var group = FindCollectorGroup(pkg, groupName);
            if (group == null) return new { error = $"Group '{groupName}' not found in package '{packageName}'." };
            AssetBundleCollectorSettingData.RemoveGroup(pkg, group);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, packageName, groupName, saved = save };
#endif
        }

        [UnitySkill("yooasset_modify_collector",
            "Modify an existing AssetBundleCollector matched by collectPath.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Modify,
            Tags = new[] { "yooasset", "collector", "modify" },
            Outputs = new[] { "success", "packageName", "groupName", "collectPath" },
            MutatesAssets = true, RiskLevel = "low",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ModifyCollector(
            string packageName,
            string groupName,
            string collectPath,
            string newCollectPath = null,
            string collectorType = "MainAssetCollector",
            string addressRule = "AddressByFileName",
            string packRule = "PackDirectory",
            string filterRule = "CollectAll",
            string assetTags = null,
            string userData = null,
            bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var groupResult = ResolveCollectorGroup(packageName, groupName, out var pkg, out var group);
            if (groupResult != null) return groupResult;
            var collector = FindCollector(group, collectPath);
            if (collector == null) return new { error = $"Collector '{collectPath}' not found in group '{groupName}'." };
            var targetPath = string.IsNullOrWhiteSpace(newCollectPath) ? collectPath : newCollectPath;
            var validationError = ValidateCollectorArguments(targetPath, collectorType, addressRule, packRule, filterRule, out var eType);
            if (validationError != null) return validationError;
            collector.CollectPath = targetPath;
            collector.CollectorGUID = AssetDatabase.AssetPathToGUID(targetPath);
            collector.CollectorType = eType;
            collector.AddressRuleName = addressRule;
            collector.PackRuleName = packRule;
            collector.FilterRuleName = filterRule;
            if (assetTags != null) collector.AssetTags = assetTags;
            if (userData != null) collector.UserData = userData;
            AssetBundleCollectorSettingData.ModifyCollector(group, collector);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, packageName = pkg.PackageName, groupName = group.GroupName, collectPath = collector.CollectPath, saved = save };
#endif
        }

        [UnitySkill("yooasset_remove_collector",
            "Remove an AssetBundleCollector matched by collectPath.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Delete,
            Tags = new[] { "yooasset", "collector", "remove", "delete" },
            Outputs = new[] { "success", "packageName", "groupName", "collectPath" },
            MutatesAssets = true, RiskLevel = "medium",
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object RemoveCollector(string packageName, string groupName, string collectPath, bool save = true)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var groupResult = ResolveCollectorGroup(packageName, groupName, out var pkg, out var group);
            if (groupResult != null) return groupResult;
            var collector = FindCollector(group, collectPath);
            if (collector == null) return new { error = $"Collector '{collectPath}' not found in group '{groupName}'." };
            AssetBundleCollectorSettingData.RemoveCollector(group, collector);
            if (save) AssetBundleCollectorSettingData.SaveFile();
            return new { success = true, packageName = pkg.PackageName, groupName = group.GroupName, collectPath, saved = save };
#endif
        }

        // ==================================================================================
        // D. Build report analysis (4 skills)
        // ==================================================================================

        [UnitySkill("yooasset_load_build_report",
            "Load a BuildReport JSON file and return its Summary (build metadata + totals). Use yooasset_list_report_bundles or yooasset_get_bundle_detail for deeper dives.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "build", "analyze" },
            Outputs = new[] { "summary", "bundleCount", "assetCount", "independAssetCount" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object LoadBuildReport(string reportPath)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(reportPath)) return new { error = "reportPath is required." };
            if (!File.Exists(reportPath))         return new { error = $"Report file not found: {reportPath}" };

            BuildReport report;
            try
            {
                report = BuildReport.Deserialize(File.ReadAllText(reportPath));
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to deserialize report: {ex.Message}" };
            }

            var s = report.Summary;
            return new
            {
                reportPath,
                bundleCount = report.BundleInfos?.Count ?? 0,
                assetCount = report.AssetInfos?.Count ?? 0,
                independAssetCount = report.IndependAssets?.Count ?? 0,
                summary = new
                {
                    yooVersion = s.YooVersion,
                    unityVersion = s.UnityVersion,
                    buildDate = s.BuildDate,
                    buildSeconds = s.BuildSeconds,
                    buildTarget = s.BuildTarget.ToString(),
                    buildPipeline = s.BuildPipeline,
                    packageName = s.BuildPackageName,
                    packageVersion = s.BuildPackageVersion,
                    packageNote = s.BuildPackageNote,
                    compressOption = s.CompressOption.ToString(),
                    fileNameStyle = s.FileNameStyle.ToString(),
                    enableAddressable = s.EnableAddressable,
                    supportExtensionless = s.SupportExtensionless,
                    replaceAssetPathWithAddress = s.ReplaceAssetPathWithAddress,
                    disableWriteTypeTree = s.DisableWriteTypeTree,
                    useAssetDependencyDB = s.UseAssetDependencyDB,
                    totals = new
                    {
                        assetFiles = s.AssetFileTotalCount,
                        mainAssets = s.MainAssetTotalCount,
                        bundles = s.AllBundleTotalCount,
                        bundlesSize = s.AllBundleTotalSize,
                        encryptedBundles = s.EncryptedBundleTotalCount,
                        encryptedBundlesSize = s.EncryptedBundleTotalSize
                    }
                }
            };
#endif
        }

        [UnitySkill("yooasset_list_report_bundles",
            "List bundles from a BuildReport JSON with paging + filtering. Sort by size / name / refCount / dependCount.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "bundle", "list", "analyze" },
            Outputs = new[] { "total", "returned", "items" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListReportBundles(
            string reportPath,
            string filterEncrypted = null,
            string filterTag = null,
            string sortBy = "size",
            int limit = 100,
            int offset = 0)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(reportPath)) return new { error = "reportPath is required." };
            if (!File.Exists(reportPath))         return new { error = $"Report file not found: {reportPath}" };

            BuildReport report;
            try { report = BuildReport.Deserialize(File.ReadAllText(reportPath)); }
            catch (Exception ex) { return new { error = $"Failed to deserialize report: {ex.Message}" }; }

            IEnumerable<ReportBundleInfo> bundles = report.BundleInfos ?? new List<ReportBundleInfo>();

            if (!string.IsNullOrEmpty(filterEncrypted) && bool.TryParse(filterEncrypted, out var wantEnc))
                bundles = bundles.Where(b => b.Encrypted == wantEnc);

            if (!string.IsNullOrEmpty(filterTag))
                bundles = bundles.Where(b => b.Tags != null && b.Tags.Contains(filterTag));

            switch ((sortBy ?? "size").ToLowerInvariant())
            {
                case "name":        bundles = bundles.OrderBy(b => b.BundleName); break;
                case "refcount":    bundles = bundles.OrderByDescending(b => b.ReferenceBundles?.Count ?? 0); break;
                case "dependcount": bundles = bundles.OrderByDescending(b => b.DependBundles?.Count ?? 0); break;
                default:            bundles = bundles.OrderByDescending(b => b.FileSize); break;
            }

            var materialized = bundles.ToArray();
            var page = materialized.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToArray();

            return new
            {
                reportPath,
                total = materialized.Length,
                returned = page.Length,
                offset,
                limit,
                sortBy,
                items = page.Select(b => new
                {
                    bundleName = b.BundleName,
                    fileName = b.FileName,
                    fileSize = b.FileSize,
                    fileHash = b.FileHash,
                    fileCRC = b.FileCRC,
                    encrypted = b.Encrypted,
                    tags = b.Tags,
                    dependBundleCount = b.DependBundles?.Count ?? 0,
                    referenceBundleCount = b.ReferenceBundles?.Count ?? 0,
                    bundleContentCount = b.BundleContents?.Count ?? 0
                }).ToArray()
            };
#endif
        }

        [UnitySkill("yooasset_get_bundle_detail",
            "Return full ReportBundleInfo for a single bundle — dependBundles, referenceBundles, and the per-asset BundleContents list.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "bundle", "detail", "dependency" },
            Outputs = new[] { "bundleName", "fileSize", "dependBundles", "referenceBundles", "bundleContents" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object GetBundleDetail(string reportPath, string bundleName)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(reportPath)) return new { error = "reportPath is required." };
            if (string.IsNullOrEmpty(bundleName)) return new { error = "bundleName is required." };
            if (!File.Exists(reportPath))         return new { error = $"Report file not found: {reportPath}" };

            BuildReport report;
            try { report = BuildReport.Deserialize(File.ReadAllText(reportPath)); }
            catch (Exception ex) { return new { error = $"Failed to deserialize report: {ex.Message}" }; }

            ReportBundleInfo info;
            try { info = report.GetBundleInfo(bundleName); }
            catch (Exception ex) { return new { error = ex.Message }; }

            return new
            {
                bundleName = info.BundleName,
                fileName = info.FileName,
                fileSize = info.FileSize,
                fileHash = info.FileHash,
                fileCRC = info.FileCRC,
                encrypted = info.Encrypted,
                tags = info.Tags,
                dependBundles = info.DependBundles,
                referenceBundles = info.ReferenceBundles,
                bundleContents = info.BundleContents?.Select(a => new
                {
                    assetPath = a.AssetPath,
                    assetGUID = a.AssetGUID,
                    fileExtension = a.FileExtension
                }).ToArray()
            };
#endif
        }

        [UnitySkill("yooasset_list_report_assets",
            "List assets from a YooAsset BuildReport with paging and filters.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "asset", "list", "analyze" },
            Outputs = new[] { "total", "returned", "items" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListReportAssets(
            string reportPath,
            string filterBundle = null,
            string filterTag = null,
            string search = null,
            string sortBy = "path",
            int limit = 100,
            int offset = 0)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var reportError = TryLoadReport(reportPath, out var report);
            if (reportError != null) return reportError;
            IEnumerable<ReportAssetInfo> assets = report.AssetInfos ?? new List<ReportAssetInfo>();
            if (!string.IsNullOrEmpty(filterBundle))
                assets = assets.Where(a => a.MainBundleName == filterBundle);
            if (!string.IsNullOrEmpty(filterTag))
                assets = assets.Where(a => a.AssetTags != null && a.AssetTags.Contains(filterTag));
            if (!string.IsNullOrEmpty(search))
                assets = assets.Where(a =>
                    (a.AssetPath != null && a.AssetPath.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (a.Address != null && a.Address.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            switch ((sortBy ?? "path").ToLowerInvariant())
            {
                case "size": assets = assets.OrderByDescending(a => a.MainBundleSize); break;
                case "bundle": assets = assets.OrderBy(a => a.MainBundleName); break;
                case "dependcount": assets = assets.OrderByDescending(a => a.DependAssets?.Count ?? 0); break;
                default: assets = assets.OrderBy(a => a.AssetPath); break;
            }
            var materialized = assets.ToArray();
            var page = materialized.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToArray();
            return new
            {
                reportPath,
                total = materialized.Length,
                returned = page.Length,
                offset,
                limit,
                items = page.Select(a => new
                {
                    address = a.Address,
                    assetPath = a.AssetPath,
                    assetGUID = a.AssetGUID,
                    assetTags = a.AssetTags,
                    mainBundleName = a.MainBundleName,
                    mainBundleSize = a.MainBundleSize,
                    dependAssetCount = a.DependAssets?.Count ?? 0,
                    dependBundleCount = a.DependBundles?.Count ?? 0
                }).ToArray()
            };
#endif
        }

        [UnitySkill("yooasset_get_asset_detail",
            "Return full ReportAssetInfo for an asset path or address from a YooAsset BuildReport.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "asset", "detail", "dependency" },
            Outputs = new[] { "assetPath", "mainBundleName", "dependAssets", "dependBundles" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object GetAssetDetail(string reportPath, string assetPath = null, string address = null)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var reportError = TryLoadReport(reportPath, out var report);
            if (reportError != null) return reportError;
            var asset = (report.AssetInfos ?? new List<ReportAssetInfo>()).FirstOrDefault(a =>
                (!string.IsNullOrEmpty(assetPath) && a.AssetPath == assetPath) ||
                (!string.IsNullOrEmpty(address) && a.Address == address));
            if (asset == null) return new { error = "Asset not found. Provide assetPath or address from yooasset_list_report_assets." };
            return new
            {
                address = asset.Address,
                assetPath = asset.AssetPath,
                assetGUID = asset.AssetGUID,
                assetTags = asset.AssetTags,
                mainBundleName = asset.MainBundleName,
                mainBundleSize = asset.MainBundleSize,
                dependBundles = asset.DependBundles,
                dependAssets = asset.DependAssets?.Select(a => new
                {
                    assetPath = a.AssetPath,
                    assetGUID = a.AssetGUID,
                    fileExtension = a.FileExtension
                }).ToArray()
            };
#endif
        }

        [UnitySkill("yooasset_get_dependency_graph",
            "Build a compact dependency graph from a YooAsset BuildReport for bundle and asset analysis.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "graph", "dependency", "analyze" },
            Outputs = new[] { "nodes", "edges" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object GetDependencyGraph(string reportPath, string rootBundle = null, string rootAssetPath = null, int maxNodes = 200)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var reportError = TryLoadReport(reportPath, out var report);
            if (reportError != null) return reportError;
            var nodes = new Dictionary<string, object>(StringComparer.Ordinal);
            var edges = new List<object>();
            void AddNode(string id, string kind, string label)
            {
                if (nodes.Count >= Math.Max(1, maxNodes) || nodes.ContainsKey(id)) return;
                nodes[id] = new { id, kind, label };
            }
            foreach (var bundle in report.BundleInfos ?? new List<ReportBundleInfo>())
            {
                if (!string.IsNullOrEmpty(rootBundle) && bundle.BundleName != rootBundle &&
                    !(bundle.DependBundles?.Contains(rootBundle) ?? false) &&
                    !(bundle.ReferenceBundles?.Contains(rootBundle) ?? false))
                    continue;
                AddNode($"bundle:{bundle.BundleName}", "bundle", bundle.BundleName);
                foreach (var dep in bundle.DependBundles ?? new List<string>())
                {
                    AddNode($"bundle:{dep}", "bundle", dep);
                    edges.Add(new { from = $"bundle:{bundle.BundleName}", to = $"bundle:{dep}", relation = "dependsOnBundle" });
                }
                foreach (var asset in bundle.BundleContents ?? new List<YooAsset.Editor.AssetInfo>())
                {
                    if (!string.IsNullOrEmpty(rootAssetPath) && asset.AssetPath != rootAssetPath) continue;
                    AddNode($"asset:{asset.AssetPath}", "asset", asset.AssetPath);
                    edges.Add(new { from = $"bundle:{bundle.BundleName}", to = $"asset:{asset.AssetPath}", relation = "containsAsset" });
                }
            }
            foreach (var asset in report.AssetInfos ?? new List<ReportAssetInfo>())
            {
                if (!string.IsNullOrEmpty(rootAssetPath) && asset.AssetPath != rootAssetPath) continue;
                AddNode($"asset:{asset.AssetPath}", "asset", asset.AssetPath);
                AddNode($"bundle:{asset.MainBundleName}", "bundle", asset.MainBundleName);
                edges.Add(new { from = $"asset:{asset.AssetPath}", to = $"bundle:{asset.MainBundleName}", relation = "mainBundle" });
                foreach (var dep in asset.DependAssets ?? new List<YooAsset.Editor.AssetInfo>())
                {
                    AddNode($"asset:{dep.AssetPath}", "asset", dep.AssetPath);
                    edges.Add(new { from = $"asset:{asset.AssetPath}", to = $"asset:{dep.AssetPath}", relation = "dependsOnAsset" });
                }
            }
            return new { reportPath, nodeCount = nodes.Count, edgeCount = edges.Count, nodes = nodes.Values.ToArray(), edges = edges.ToArray() };
#endif
        }

        [UnitySkill("yooasset_compare_build_reports",
            "Compare two YooAsset BuildReport files and summarize bundle/asset additions, removals, and size changes.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "compare", "diff", "analyze" },
            Outputs = new[] { "bundleDiff", "assetDiff" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object CompareBuildReports(string oldReportPath, string newReportPath, int limit = 100)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            var oldError = TryLoadReport(oldReportPath, out var oldReport);
            if (oldError != null) return oldError;
            var newError = TryLoadReport(newReportPath, out var newReport);
            if (newError != null) return newError;
            var oldBundles = (oldReport.BundleInfos ?? new List<ReportBundleInfo>()).ToDictionary(b => b.BundleName, b => b, StringComparer.Ordinal);
            var newBundles = (newReport.BundleInfos ?? new List<ReportBundleInfo>()).ToDictionary(b => b.BundleName, b => b, StringComparer.Ordinal);
            var oldAssets = new HashSet<string>((oldReport.AssetInfos ?? new List<ReportAssetInfo>()).Select(a => a.AssetPath), StringComparer.Ordinal);
            var newAssets = new HashSet<string>((newReport.AssetInfos ?? new List<ReportAssetInfo>()).Select(a => a.AssetPath), StringComparer.Ordinal);
            var changedBundles = newBundles.Keys.Intersect(oldBundles.Keys)
                .Select(name => new { name, oldSize = oldBundles[name].FileSize, newSize = newBundles[name].FileSize, delta = newBundles[name].FileSize - oldBundles[name].FileSize })
                .Where(x => x.delta != 0)
                .OrderByDescending(x => Math.Abs(x.delta))
                .Take(Math.Max(1, limit))
                .ToArray();
            return new
            {
                oldReportPath,
                newReportPath,
                bundleDiff = new
                {
                    added = newBundles.Keys.Except(oldBundles.Keys).Take(limit).ToArray(),
                    removed = oldBundles.Keys.Except(newBundles.Keys).Take(limit).ToArray(),
                    changed = changedBundles
                },
                assetDiff = new
                {
                    added = newAssets.Except(oldAssets).Take(limit).ToArray(),
                    removed = oldAssets.Except(newAssets).Take(limit).ToArray()
                },
                totalSizeDelta = (newReport.Summary?.AllBundleTotalSize ?? 0) - (oldReport.Summary?.AllBundleTotalSize ?? 0)
            };
#endif
        }

        [UnitySkill("yooasset_list_independ_assets",
            "List IndependAssets from a BuildReport — assets not referenced by any other asset, candidates for cleanup.",
            Category = SkillCategory.YooAsset, Operation = SkillOperation.Analyze,
            Tags = new[] { "yooasset", "report", "independent", "orphan", "cleanup" },
            Outputs = new[] { "total", "returned", "items" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.tuyoogame.yooasset" })]
        public static object ListIndependAssets(string reportPath, int limit = 100, int offset = 0)
        {
#if !YOO_ASSET
            return NoYooAsset();
#else
            if (string.IsNullOrEmpty(reportPath)) return new { error = "reportPath is required." };
            if (!File.Exists(reportPath))         return new { error = $"Report file not found: {reportPath}" };

            BuildReport report;
            try { report = BuildReport.Deserialize(File.ReadAllText(reportPath)); }
            catch (Exception ex) { return new { error = $"Failed to deserialize report: {ex.Message}" }; }

            var all = report.IndependAssets ?? new List<ReportIndependAsset>();
            var page = all.Skip(Math.Max(0, offset)).Take(Math.Max(1, limit)).ToArray();

            return new
            {
                reportPath,
                total = all.Count,
                returned = page.Length,
                offset,
                limit,
                items = page.Select(a => new
                {
                    assetPath = a.AssetPath,
                    assetGUID = a.AssetGUID,
                    assetType = a.AssetType,
                    fileSize = a.FileSize
                }).ToArray()
            };
#endif
        }

#if YOO_ASSET
        [InitializeOnLoadMethod]
        private static void RestoreRuntimeValidationJobsAfterReload()
        {
            try
            {
                RestoreRuntimeValidationJobs();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError("[UnitySkills] YooAssetSkills restore-after-reload failed: " + ex);
            }
        }

        private static void PersistRuntimeValidationJobs()
        {
            if (RuntimeValidationJobs.Count == 0)
            {
                EditorPrefs.DeleteKey(RuntimeValidationJobsPrefKey);
                return;
            }

            var states = RuntimeValidationJobs.Values.Select(job => new RuntimeValidationJobState
            {
                JobId = job.JobId,
                PackageName = job.PackageName,
                AssetLocation = job.AssetLocation,
                RestoreEditMode = job.RestoreEditMode,
                StartedPlayMode = job.StartedPlayMode,
                Cleanup = job.Cleanup,
                CheckDownloader = job.CheckDownloader,
                DownloadingMaxNumber = job.DownloadingMaxNumber,
                FailedTryAgain = job.FailedTryAgain,
                Status = ToPayloadValue(job.Status),
                Stage = ToPayloadValue(job.Stage),
                Progress = job.Progress,
                Error = job.Error,
                Result = job.Result
            }).ToArray();
            EditorPrefs.SetString(RuntimeValidationJobsPrefKey, JsonConvert.SerializeObject(states));
        }

        private static void RestoreRuntimeValidationJobs()
        {
            if (!EditorPrefs.HasKey(RuntimeValidationJobsPrefKey) || RuntimeValidationJobs.Count > 0)
                return;

            RuntimeValidationJobState[] states;
            try
            {
                states = JsonConvert.DeserializeObject<RuntimeValidationJobState[]>(
                    EditorPrefs.GetString(RuntimeValidationJobsPrefKey));
            }
            catch
            {
                EditorPrefs.DeleteKey(RuntimeValidationJobsPrefKey);
                return;
            }

            if (states == null || states.Length == 0)
                return;

            foreach (var state in states)
            {
                if (string.IsNullOrEmpty(state.JobId) || string.IsNullOrEmpty(state.PackageName))
                    continue;

                RuntimeValidationJobs[state.JobId] = new RuntimeValidationJob
                {
                    JobId = state.JobId,
                    PackageName = state.PackageName,
                    AssetLocation = state.AssetLocation,
                    RestoreEditMode = state.RestoreEditMode,
                    StartedPlayMode = state.StartedPlayMode,
                    Cleanup = state.Cleanup,
                    CheckDownloader = state.CheckDownloader,
                    DownloadingMaxNumber = Math.Max(1, state.DownloadingMaxNumber),
                    FailedTryAgain = Math.Max(0, state.FailedTryAgain),
                    Status = ParseStatus(state.Status),
                    Stage = ParseStage(state.Stage),
                    Progress = state.Progress,
                    Error = state.Error,
                    Result = state.Result ?? new Dictionary<string, object>()
                };
            }

            if (RuntimeValidationJobs.Values.Any(job => job.Status != RuntimeValidationStatus.Completed && job.Status != RuntimeValidationStatus.Failed))
                EnsureRuntimeValidationUpdateHooked();
        }

        private static AssetBundleCollectorPackage FindCollectorPackage(string packageName)
        {
            return AssetBundleCollectorSettingData.Setting.Packages
                .FirstOrDefault(p => p.PackageName == packageName);
        }

        private static AssetBundleCollectorGroup FindCollectorGroup(AssetBundleCollectorPackage package, string groupName)
        {
            return package?.Groups.FirstOrDefault(g => g.GroupName == groupName);
        }

        private static AssetBundleCollector FindCollector(AssetBundleCollectorGroup group, string collectPath)
        {
            return group?.Collectors.FirstOrDefault(c => c.CollectPath == collectPath);
        }

        private static object ResolveCollectorGroup(
            string packageName,
            string groupName,
            out AssetBundleCollectorPackage package,
            out AssetBundleCollectorGroup group)
        {
            package = FindCollectorPackage(packageName);
            group = null;
            if (package == null) return new { error = $"Package '{packageName}' not found." };
            group = FindCollectorGroup(package, groupName);
            if (group == null) return new { error = $"Group '{groupName}' not found in package '{packageName}'." };
            return null;
        }

        private static object ValidateCollectorArguments(
            string collectPath,
            string collectorType,
            string addressRule,
            string packRule,
            string filterRule,
            out ECollectorType eType)
        {
            eType = ECollectorType.None;
            if (string.IsNullOrEmpty(collectPath)) return new { error = "collectPath is required." };
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(collectPath) == null)
                return new { error = $"collectPath '{collectPath}' does not resolve to a valid asset." };
            if (!Enum.TryParse<ECollectorType>(collectorType, out eType))
                return new { error = $"Unknown collectorType: {collectorType}. Available: MainAssetCollector, StaticAssetCollector, DependAssetCollector." };
            if (!AssetBundleCollectorSettingData.HasAddressRuleName(addressRule))
                return new { error = $"Unknown addressRule: {addressRule}." };
            if (!AssetBundleCollectorSettingData.HasPackRuleName(packRule))
                return new { error = $"Unknown packRule: {packRule}." };
            if (!AssetBundleCollectorSettingData.HasFilterRuleName(filterRule))
                return new { error = $"Unknown filterRule: {filterRule}." };
            return null;
        }

        private static object TryLoadReport(string reportPath, out BuildReport report)
        {
            report = null;
            if (string.IsNullOrEmpty(reportPath)) return new { error = "reportPath is required." };
            if (!File.Exists(reportPath)) return new { error = $"Report file not found: {reportPath}" };
            try
            {
                report = BuildReport.Deserialize(File.ReadAllText(reportPath));
                return null;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to deserialize report: {ex.Message}" };
            }
        }

        private static void EnsureRuntimeValidationUpdateHooked()
        {
            if (_runtimeValidationUpdateHooked) return;
            EditorApplication.update += ProcessRuntimeValidationJobs;
            _runtimeValidationUpdateHooked = true;
        }

        private static void ProcessRuntimeValidationJobs()
        {
            foreach (var job in RuntimeValidationJobs.Values.ToArray())
            {
                if (job.Status == RuntimeValidationStatus.Completed || job.Status == RuntimeValidationStatus.Failed)
                    continue;

                try
                {
                    ProcessRuntimeValidationJob(job);
                }
                catch (Exception ex)
                {
                    FailRuntimeValidationJob(job, ex.Message, ex.GetType().Name);
                }
            }

            if (RuntimeValidationJobs.Values.All(j => j.Status == RuntimeValidationStatus.Completed || j.Status == RuntimeValidationStatus.Failed))
            {
                EditorApplication.update -= ProcessRuntimeValidationJobs;
                _runtimeValidationUpdateHooked = false;
            }
        }

        private static void ProcessRuntimeValidationJob(RuntimeValidationJob job)
        {
            if (job.Stage == RuntimeValidationStage.WaitingPlayMode)
            {
                job.Status = RuntimeValidationStatus.Running;
                job.Progress = 5;
                if (!EditorApplication.isPlaying)
                {
                    job.StartedPlayMode = true;
                    job.Status = RuntimeValidationStatus.Running;
                    PersistRuntimeValidationJobs();
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        EditorApplication.isPlaying = true;
                    return;
                }
                job.Stage = RuntimeValidationStage.InitializeYooAssets;
            }

            if (!EditorApplication.isPlaying)
                return;

            if (job.Stage == RuntimeValidationStage.InitializeYooAssets)
            {
                job.Progress = 15;
                if (!YooAssets.Initialized)
                    YooAssets.Initialize();

                var simulateResult = EditorSimulateModeHelper.SimulateBuild(job.PackageName);
                var package = YooAssets.ContainsPackage(job.PackageName)
                    ? YooAssets.GetPackage(job.PackageName)
                    : YooAssets.CreatePackage(job.PackageName);
                YooAssets.SetDefaultPackage(package);
                var parameters = new EditorSimulateModeParameters
                {
                    AutoUnloadBundleWhenUnused = true,
                    EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(simulateResult.PackageRootDirectory)
                };
                job.Package = package;
                job.Result["packageRootDirectory"] = simulateResult.PackageRootDirectory;
                job.InitializeOperation = package.InitializeAsync(parameters);
                job.Stage = RuntimeValidationStage.WaitInitialize;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitInitialize)
            {
                job.Progress = 35;
                if (!job.InitializeOperation.IsDone) return;
                if (job.InitializeOperation.Status != EOperationStatus.Succeed)
                {
                    FailRuntimeValidationJob(job, job.InitializeOperation.Error, "InitializationFailed");
                    return;
                }
                job.Result["initialized"] = true;
                job.RequestVersionOperation = job.Package.RequestPackageVersionAsync();
                job.Stage = RuntimeValidationStage.WaitPackageVersion;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitPackageVersion)
            {
                job.Progress = 45;
                if (!job.RequestVersionOperation.IsDone) return;
                if (job.RequestVersionOperation.Status != EOperationStatus.Succeed)
                {
                    FailRuntimeValidationJob(job, job.RequestVersionOperation.Error, "RequestPackageVersionFailed");
                    return;
                }

                job.Result["packageVersion"] = job.RequestVersionOperation.PackageVersion;
                job.UpdateManifestOperation = job.Package.UpdatePackageManifestAsync(job.RequestVersionOperation.PackageVersion);
                job.Stage = RuntimeValidationStage.WaitManifest;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitManifest)
            {
                job.Progress = 50;
                if (!job.UpdateManifestOperation.IsDone) return;
                if (job.UpdateManifestOperation.Status != EOperationStatus.Succeed)
                {
                    FailRuntimeValidationJob(job, job.UpdateManifestOperation.Error, "UpdateManifestFailed");
                    return;
                }

                job.Result["manifestUpdated"] = true;
                job.Result["packageValid"] = job.Package.PackageValid;
                job.Stage = RuntimeValidationStage.LoadAsset;
                return;
            }

            if (job.Stage == RuntimeValidationStage.LoadAsset)
            {
                job.Progress = 55;
                var location = job.AssetLocation;
                if (string.IsNullOrEmpty(location))
                {
                    var first = job.Package.GetAllAssetInfos().FirstOrDefault(a => a != null && !a.IsInvalid);
                    location = first?.Address;
                    if (string.IsNullOrEmpty(location)) location = first?.AssetPath;
                }

                if (string.IsNullOrEmpty(location))
                {
                    job.Result["assetLoadSkipped"] = true;
                    job.Stage = RuntimeValidationStage.CheckDownloader;
                    return;
                }

                job.Result["assetLocation"] = location;
                job.Result["locationValid"] = job.Package.CheckLocationValid(location);
                if (!(bool)job.Result["locationValid"])
                {
                    FailRuntimeValidationJob(job, $"Location is invalid: {location}", "InvalidLocation");
                    return;
                }

                job.AssetHandle = job.Package.LoadAssetAsync(location);
                job.Stage = RuntimeValidationStage.WaitAsset;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitAsset)
            {
                if (!job.AssetHandle.IsDone) return;
                if (job.AssetHandle.Status != EOperationStatus.Succeed)
                {
                    FailRuntimeValidationJob(job, job.AssetHandle.LastError, "AssetLoadFailed");
                    return;
                }
                job.Result["assetLoaded"] = job.AssetHandle.AssetObject != null;
                job.Result["assetName"] = job.AssetHandle.AssetObject ? job.AssetHandle.AssetObject.name : null;
                job.Result["assetType"] = job.AssetHandle.AssetObject ? job.AssetHandle.AssetObject.GetType().Name : null;
                job.AssetHandle.Release();
                job.Result["assetHandleReleased"] = true;
                job.Stage = RuntimeValidationStage.CheckDownloader;
                return;
            }

            if (job.Stage == RuntimeValidationStage.CheckDownloader)
            {
                job.Progress = 70;
                if (!job.CheckDownloader)
                {
                    job.Result["downloadSkipped"] = true;
                    job.Stage = RuntimeValidationStage.Cleanup;
                    return;
                }
                job.Downloader = job.Package.CreateResourceDownloader(job.DownloadingMaxNumber, job.FailedTryAgain);
                job.Result["downloadTotalCount"] = job.Downloader.TotalDownloadCount;
                job.Result["downloadTotalBytes"] = job.Downloader.TotalDownloadBytes;
                job.Downloader.BeginDownload();
                job.Stage = RuntimeValidationStage.WaitDownloader;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitDownloader)
            {
                if (!job.Downloader.IsDone) return;
                job.Result["downloadStatus"] = job.Downloader.Status.ToString();
                job.Result["downloadError"] = job.Downloader.Error;
                if (job.Downloader.Status != EOperationStatus.Succeed)
                {
                    FailRuntimeValidationJob(job, job.Downloader.Error, "DownloaderFailed");
                    return;
                }
                job.Stage = RuntimeValidationStage.Cleanup;
                return;
            }

            if (job.Stage == RuntimeValidationStage.Cleanup)
            {
                job.Progress = 85;
                if (!job.Cleanup)
                {
                    CompleteRuntimeValidationJob(job);
                    return;
                }
                job.DestroyOperation = job.Package.DestroyAsync();
                job.Stage = RuntimeValidationStage.WaitDestroy;
                return;
            }

            if (job.Stage == RuntimeValidationStage.WaitDestroy)
            {
                if (!job.DestroyOperation.IsDone) return;
                job.Result["destroyStatus"] = job.DestroyOperation.Status.ToString();
                YooAssets.RemovePackage(job.Package);
                YooAssets.Destroy();
                CompleteRuntimeValidationJob(job);
            }
        }

        private static void CompleteRuntimeValidationJob(RuntimeValidationJob job)
        {
            job.Progress = 100;
            job.Stage = RuntimeValidationStage.Completed;
            job.Status = RuntimeValidationStatus.Completed;
            job.Result["completedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            PersistRuntimeValidationJobs();
            if (job.RestoreEditMode && job.StartedPlayMode && EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        private static void FailRuntimeValidationJob(RuntimeValidationJob job, string error, string errorType)
        {
            job.Status = RuntimeValidationStatus.Failed;
            job.Stage = RuntimeValidationStage.Failed;
            job.Error = error;
            job.Result["errorType"] = errorType;
            job.Result["failedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            try
            {
                job.AssetHandle?.Release();
                if (job.Package != null && job.Package.InitializeStatus != EOperationStatus.None)
                    job.Package.DestroyAsync();
                if (YooAssets.Initialized)
                    YooAssets.Destroy();
            }
            catch { /* best-effort cleanup */ }
            PersistRuntimeValidationJobs();
            if (job.RestoreEditMode && job.StartedPlayMode && EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        private static object SafeCall(Func<object> func)
        {
            try { return func(); }
            catch (Exception ex) { return $"ERROR: {ex.Message}"; }
        }
#endif
    }
}
