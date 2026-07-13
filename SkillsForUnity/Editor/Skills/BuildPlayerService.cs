using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills
{
    internal static class BuildPlayerService
    {
        private const int DiagnosticLimit = 50;

        internal static object Start(string outputPath, string target, string[] scenes, bool development, bool overwrite)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return new { error = "Cannot build while Unity is in or entering Play Mode." };
            if (ServerAvailabilityHelper.IsCompilationInProgress())
                return new { error = "Cannot build while Unity is compiling or updating assets." };

            var active = BatchPersistence.ListJobs(100).FirstOrDefault(job =>
                job != null && string.Equals(job.kind, "build_player", StringComparison.OrdinalIgnoreCase) &&
                !IsTerminal(job.status));
            if (active != null)
                return new { error = $"Another player build is already active: {active.jobId} ({active.currentStage ?? active.status})." };

            if (!TryResolveTarget(target, out var buildTarget, out var targetError))
                return new { error = targetError };
            var targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget);
            if (targetGroup == BuildTargetGroup.Unknown || !BuildPipeline.IsBuildTargetSupported(targetGroup, buildTarget))
                return new { error = $"Build target '{buildTarget}' is not supported by this Unity installation. Install its platform module first." };

            if (!TryResolveScenes(scenes, out var resolvedScenes, out var scenesError))
                return new { error = scenesError };
            if (!TryResolveOutputPath(outputPath, buildTarget, overwrite, out var resolvedOutput, out var outputError))
                return new { error = outputError };
            if (!ValidateDirtyScenes(resolvedScenes, out var dirtyError))
                return new { error = dirtyError };

            var metadata = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["target"] = buildTarget.ToString(),
                ["targetGroup"] = targetGroup.ToString(),
                ["scenes"] = resolvedScenes,
                ["outputPath"] = resolvedOutput,
                ["development"] = development,
                ["overwrite"] = overwrite,
                ["buildStarted"] = false,
            };
            var job = AsyncJobService.CreateJob(
                "build_player", "queued", $"Player build queued for {buildTarget}.", false,
                metadata: metadata,
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["platform"] = buildTarget.ToString(),
                    ["outputPath"] = resolvedOutput,
                    ["scenes"] = resolvedScenes,
                });
            return new
            {
                success = true,
                status = "accepted",
                jobId = job.jobId,
                kind = job.kind,
                platform = buildTarget.ToString(),
                outputPath = resolvedOutput,
                scenes = resolvedScenes,
            };
        }

        internal static void Process(BatchJobRecord job)
        {
            if (job == null || IsTerminal(job.status))
                return;

            if (GetBool(job, "buildStarted"))
            {
                AsyncJobService.FailJob(job.jobId,
                    "Player build was interrupted by an Editor or domain reload and was not retried.",
                    "failed_interrupted", job.resultData);
                return;
            }

            if (!string.Equals(job.currentStage, "scheduled", StringComparison.OrdinalIgnoreCase))
            {
                Transition(job, "running", "scheduled", 5,
                    "Player build accepted; BuildPipeline will start on the next Editor update.", "build_scheduled");
                return;
            }

            if (!Enum.TryParse(GetString(job, "target"), out BuildTarget target))
            {
                AsyncJobService.FailJob(job.jobId, "Persisted build target is invalid.", "failed_validation", job.resultData);
                return;
            }

            var scenes = GetStrings(job, "scenes");
            var outputPath = GetString(job, "outputPath");
            var options = GetBool(job, "development") ? BuildOptions.Development : BuildOptions.None;
            job.metadata["buildStarted"] = true;
            Transition(job, "running", "building", 10, $"Building {target} player.", "build_started");
            EventChannelService.Publish("job_started", new { jobId = job.jobId, kind = job.kind, stage = "building" });

            try
            {
                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = target,
                    options = options,
                });
                var data = BuildResultData(report, scenes, outputPath, target);
                if (report == null || report.summary.result != BuildResult.Succeeded)
                {
                    var resultName = report == null ? "Unknown" : report.summary.result.ToString();
                    AsyncJobService.FailJob(job.jobId, $"Player build finished with result {resultName}.", "failed_build", data);
                    return;
                }

                AsyncJobService.CompleteJob(job.jobId,
                    $"Player build succeeded for {target}: {outputPath}", data);
            }
            catch (Exception ex)
            {
                var data = job.resultData ?? new Dictionary<string, object>();
                data["exceptionType"] = ex.GetType().Name;
                AsyncJobService.FailJob(job.jobId, $"Player build failed: {ex.Message}", "failed_build", data);
            }
        }

        internal static bool TryResolveTarget(string value, out BuildTarget target, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
                return true;
            }

            var aliases = new Dictionary<string, BuildTarget>(StringComparer.OrdinalIgnoreCase)
            {
                ["macos"] = BuildTarget.StandaloneOSX,
                ["osx"] = BuildTarget.StandaloneOSX,
                ["windows64"] = BuildTarget.StandaloneWindows64,
                ["win64"] = BuildTarget.StandaloneWindows64,
                ["linux64"] = BuildTarget.StandaloneLinux64,
                ["android"] = BuildTarget.Android,
                ["webgl"] = BuildTarget.WebGL,
                ["ios"] = BuildTarget.iOS,
            };
            if (aliases.TryGetValue(value.Trim(), out target) ||
                Enum.TryParse(value.Trim(), true, out target))
                return true;

            error = $"Unknown build target '{value}'. Use a BuildTarget enum name or macOS/windows64/linux64/android/webgl/iOS.";
            return false;
        }

        internal static bool TryResolveOutputPath(string value, BuildTarget target, bool overwrite,
            out string resolved, out string error)
        {
            resolved = null;
            error = null;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var relative = string.IsNullOrWhiteSpace(value) ? DefaultRelativeOutput(target) : value.Trim();
            resolved = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(projectRoot, relative));

            var rootWithSeparator = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                error = "outputPath must resolve inside the Unity project directory.";
                return false;
            }

            var relativeResolved = resolved.Substring(rootWithSeparator.Length)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var firstSegment = relativeResolved.Split(Path.DirectorySeparatorChar)[0];
            var forbidden = new[] { "Assets", "Packages", "Library", "ProjectSettings", "UserSettings", "Temp", "Logs" };
            if (string.IsNullOrWhiteSpace(relativeResolved) || forbidden.Any(x => string.Equals(x, firstSegment, StringComparison.OrdinalIgnoreCase)))
            {
                error = "outputPath cannot be the project root or reside under Assets, Packages, Library, ProjectSettings, UserSettings, Temp, or Logs.";
                return false;
            }

            if (!overwrite && (File.Exists(resolved) || Directory.Exists(resolved)))
            {
                error = $"Build output already exists: {resolved}. Pass overwrite=true to let Unity update it.";
                return false;
            }
            return true;
        }

        internal static bool TryResolveScenes(string[] requested, out string[] scenes, out string error)
        {
            error = null;
            scenes = requested == null || requested.Length == 0
                ? EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray()
                : requested.Where(scene => !string.IsNullOrWhiteSpace(scene)).Select(scene => scene.Replace('\\', '/')).Distinct().ToArray();
            if (scenes.Length == 0)
            {
                error = "No scenes were provided and Build Settings has no enabled scenes.";
                return false;
            }
            foreach (var scene in scenes)
            {
                if (!scene.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    !scene.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(Path.GetFullPath(scene)))
                {
                    error = $"Build scene must be an existing Assets/*.unity file: {scene}";
                    return false;
                }
            }
            return true;
        }

        private static bool ValidateDirtyScenes(string[] scenes, out string error)
        {
            var requested = new HashSet<string>(scenes, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty && requested.Contains(scene.path))
                {
                    error = $"Build scene has unsaved changes: {scene.path}. Save it before building.";
                    return false;
                }
            }
            error = null;
            return true;
        }

        private static string DefaultRelativeOutput(BuildTarget target)
        {
            var product = SanitizeFileName(string.IsNullOrWhiteSpace(PlayerSettings.productName) ? "Player" : PlayerSettings.productName);
            var name = target == BuildTarget.StandaloneOSX ? product + ".app"
                : target == BuildTarget.StandaloneWindows || target == BuildTarget.StandaloneWindows64 ? product + ".exe"
                : target == BuildTarget.Android ? product + ".apk"
                : product;
            return Path.Combine("Builds", target.ToString(), name);
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static Dictionary<string, object> BuildResultData(BuildReport report, string[] scenes, string outputPath, BuildTarget target)
        {
            var data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["platform"] = target.ToString(), ["outputPath"] = outputPath, ["scenes"] = scenes,
            };
            if (report == null) return data;
            var summary = report.summary;
            data["result"] = summary.result.ToString();
            data["totalErrors"] = summary.totalErrors;
            data["totalWarnings"] = summary.totalWarnings;
            data["totalSize"] = summary.totalSize;
            data["totalTimeSeconds"] = summary.totalTime.TotalSeconds;
            data["buildGuid"] = summary.guid.ToString();
            data["diagnostics"] = report.steps.SelectMany(step => step.messages)
                .Where(message => message.type == LogType.Error || message.type == LogType.Exception || message.type == LogType.Warning)
                .Take(DiagnosticLimit)
                .Select(message => new { type = message.type.ToString(), message = message.content }).ToArray();
            return data;
        }

        private static void Transition(BatchJobRecord job, string status, string stage, int progress, string summary, string code)
        {
            job.status = status;
            job.currentStage = stage;
            job.progressStage = stage;
            job.progress = progress;
            job.resultSummary = summary;
            job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.logs.Add(new BatchJobLogEntry { timestamp = job.updatedAt, level = "info", stage = stage, message = summary, code = code });
            job.progressEvents.Add(new BatchJobProgressEvent { timestamp = job.updatedAt, progress = progress, stage = stage, description = summary });
            BatchPersistence.UpsertJob(job);
            BatchPersistence.FlushIfDirty();
        }

        private static string GetString(BatchJobRecord job, string key) =>
            job.metadata != null && job.metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
        private static bool GetBool(BatchJobRecord job, string key) =>
            job.metadata != null && job.metadata.TryGetValue(key, out var value) &&
            (value is bool flag ? flag : bool.TryParse(value?.ToString(), out var parsed) && parsed);
        private static string[] GetStrings(BatchJobRecord job, string key)
        {
            if (job.metadata == null || !job.metadata.TryGetValue(key, out var value) || value == null) return Array.Empty<string>();
            if (value is string[] array) return array;
            if (value is IEnumerable<object> items) return items.Select(item => item?.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
            return Array.Empty<string>();
        }
        private static bool IsTerminal(string status) => status == "completed" || status == "failed" || status == "cancelled";
    }
}

// Producer:Betsy
