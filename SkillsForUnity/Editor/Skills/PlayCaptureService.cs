using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static class PlayCaptureService
    {
        private const int EnterTimeoutSeconds = 60;
        private const int ScreenshotTimeoutSeconds = 10;

        private sealed class CapturedError
        {
            public string logType;
            public string message;
            public string stackTrace;
            public int count;
        }

        internal static object Start(int durationSeconds, bool captureScreenshot, string screenshotFilename, int maxErrors)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return new { error = "Play capture can only start from Edit Mode." };
            if (ServerAvailabilityHelper.IsCompilationInProgress())
                return new { error = "Cannot start Play capture while Unity is compiling or updating assets." };

            var active = BatchPersistence.ListJobs(100).FirstOrDefault(job =>
                job != null && string.Equals(job.kind, "play_capture", StringComparison.OrdinalIgnoreCase) &&
                !IsTerminal(job.status));
            if (active != null)
                return new { error = $"Another Play capture is already active: {active.jobId} ({active.currentStage ?? active.status})." };

            if (durationSeconds < 1 || durationSeconds > 300)
                return new { error = "durationSeconds must be between 1 and 300." };
            if (maxErrors < 1 || maxErrors > 500)
                return new { error = "maxErrors must be between 1 and 500." };
            var filename = ResolveScreenshotFilename(screenshotFilename);
            var job = AsyncJobService.CreateJob(
                "play_capture", "entering_play_mode", $"Entering Play Mode for a {durationSeconds}s runtime observation.", true,
                metadata: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["durationSeconds"] = durationSeconds,
                    ["captureScreenshot"] = captureScreenshot,
                    ["screenshotFilename"] = filename,
                    ["maxErrors"] = maxErrors,
                    ["enterRequestedUtcTicks"] = DateTime.UtcNow.Ticks,
                    ["startedPlayMode"] = true,
                    ["playCaptureStage"] = "entering_play_mode",
                },
                resultData: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["healthy"] = true,
                    ["durationSeconds"] = durationSeconds,
                    ["observedSeconds"] = 0.0,
                    ["errorCount"] = 0,
                    ["uniqueErrorCount"] = 0,
                    ["errors"] = new List<CapturedError>(),
                    ["errorsTruncated"] = false,
                    ["stoppedEarly"] = false,
                    ["screenshotPath"] = null,
                });
            job.status = "running";
            BatchPersistence.UpsertJob(job);
            BatchPersistence.FlushIfDirty();
            EditorApplication.isPlaying = true;
            return new { success = true, status = "accepted", jobId = job.jobId, kind = job.kind, durationSeconds, captureScreenshot };
        }

        internal static void Process(BatchJobRecord job)
        {
            if (job == null || IsTerminal(job.status)) return;

            var stage = ResolvePersistedStage(job);
            if (stage == "entering_play_mode" || stage == "domain_reload_recovery")
            {
                if (EditorApplication.isPlaying)
                {
                    if (GetLong(job, "observeStartedUtcTicks") <= 0)
                        job.metadata["observeStartedUtcTicks"] = DateTime.UtcNow.Ticks;
                    Transition(job, "running", "observing", 20, "Play Mode entered; collecting runtime errors.", "play_capture_observing");
                    return;
                }

                var enteredAt = GetLong(job, "enterRequestedUtcTicks");
                if (enteredAt > 0 && DateTime.UtcNow.Ticks - enteredAt > EnterTimeoutSeconds * TimeSpan.TicksPerSecond)
                    AsyncJobService.FailJob(job.jobId, "Unity did not enter Play Mode before the timeout.", "failed_enter_play_mode", job.resultData);
                return;
            }

            if (stage == "observing")
            {
                var started = GetLong(job, "observeStartedUtcTicks");
                var elapsed = started > 0 ? TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - started)).TotalSeconds : 0;
                job.resultData["observedSeconds"] = Math.Round(elapsed, 3);
                if (!EditorApplication.isPlaying)
                {
                    job.resultData["stoppedEarly"] = true;
                    Complete(job, "Play Mode was stopped before the requested observation duration.");
                    return;
                }

                if (elapsed < GetInt(job, "durationSeconds", 10)) return;

                if (GetBool(job, "captureScreenshot"))
                {
                    var relative = "Assets/Screenshots/" + ResolveScreenshotFilename(GetString(job, "screenshotFilename"));
                    var absolute = Path.GetFullPath(relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(absolute));
                    try
                    {
                        ScreenCapture.CaptureScreenshot(absolute);
                        job.metadata["screenshotPath"] = relative.Replace('\\', '/');
                        job.metadata["screenshotRequestedUtcTicks"] = DateTime.UtcNow.Ticks;
                        Transition(job, "running", "waiting_screenshot", 85, "Runtime observation complete; waiting for Game View screenshot.", "play_capture_screenshot");
                    }
                    catch (Exception ex)
                    {
                        EditorApplication.isPlaying = false;
                        AsyncJobService.FailJob(job.jobId, $"Game View screenshot failed: {ex.Message}", "failed_screenshot", job.resultData);
                    }
                    return;
                }

                BeginExit(job);
                return;
            }

            if (stage == "waiting_screenshot")
            {
                var relative = GetString(job, "screenshotPath");
                var requestedAt = GetLong(job, "screenshotRequestedUtcTicks");
                if (!string.IsNullOrWhiteSpace(relative) && File.Exists(Path.GetFullPath(relative)))
                {
                    job.resultData["screenshotPath"] = relative;
                    AssetDatabase.ImportAsset(relative, ImportAssetOptions.ForceUpdate);
                    BeginExit(job);
                    return;
                }
                if (requestedAt > 0 && DateTime.UtcNow.Ticks - requestedAt > ScreenshotTimeoutSeconds * TimeSpan.TicksPerSecond)
                {
                    EditorApplication.isPlaying = false;
                    AsyncJobService.FailJob(job.jobId, "Game View screenshot was not written before the timeout.", "failed_screenshot", job.resultData);
                }
                return;
            }

            if (stage == "exiting_play_mode" && !EditorApplication.isPlaying)
                Complete(job, "Runtime observation completed and Play Mode exited.");
        }

        internal static void ProcessPlayModeJob(BatchJobRecord job)
        {
            if (job == null || IsTerminal(job.status)) return;
            if (EditorApplication.isPlaying)
            {
                AsyncJobService.CompleteJob(job.jobId, "Unity entered Play Mode.",
                    new Dictionary<string, object> { ["mode"] = "playing" });
                return;
            }
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - job.startedAt > EnterTimeoutSeconds)
                AsyncJobService.FailJob(job.jobId, "Unity did not enter Play Mode before the timeout.", "failed_enter_play_mode");
        }

        internal static void NotifyCancelled(BatchJobRecord job)
        {
            if (job != null && string.Equals(job.kind, "play_capture", StringComparison.OrdinalIgnoreCase) &&
                GetBool(job, "startedPlayMode") && EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
        }

        internal static void RecordRuntimeError(string message, string stackTrace, LogType type)
        {
            if (!EditorApplication.isPlaying || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert))
                return;
            var job = BatchPersistence.ListJobs(100).FirstOrDefault(candidate => candidate != null &&
                string.Equals(candidate.kind, "play_capture", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(ResolvePersistedStage(candidate), "observing", StringComparison.OrdinalIgnoreCase) &&
                !IsTerminal(candidate.status));
            if (job == null) return;

            RecordError(job, message, stackTrace, type);
        }

        internal static void RecordError(BatchJobRecord job, string message, string stackTrace, LogType type)
        {
            if (job == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            var errors = ReadErrors(job);
            var normalizedMessage = Truncate(message, 2000);
            var normalizedStack = FirstLines(stackTrace, 12);
            var total = GetResultInt(job, "errorCount") + 1;
            var existing = errors.FirstOrDefault(error => error.logType == type.ToString() && error.message == normalizedMessage && error.stackTrace == normalizedStack);
            if (existing != null) existing.count++;
            else if (total <= GetInt(job, "maxErrors", 50))
                errors.Add(new CapturedError { logType = type.ToString(), message = normalizedMessage, stackTrace = normalizedStack, count = 1 });
            else job.resultData["errorsTruncated"] = true;
            if (total > GetInt(job, "maxErrors", 50))
                job.resultData["errorsTruncated"] = true;

            job.resultData["healthy"] = false;
            job.resultData["errorCount"] = total;
            job.resultData["uniqueErrorCount"] = errors.Count;
            job.resultData["errors"] = errors;
            BatchPersistence.UpsertJob(job);
        }

        internal static string ResolvePersistedStage(BatchJobRecord job)
        {
            if (job == null) return string.Empty;
            return string.Equals(job.currentStage, "domain_reload_recovery", StringComparison.OrdinalIgnoreCase)
                ? GetString(job, "playCaptureStage") ?? "entering_play_mode"
                : job.currentStage ?? string.Empty;
        }

        private static void BeginExit(BatchJobRecord job)
        {
            Transition(job, "running", "exiting_play_mode", 95, "Runtime observation complete; exiting Play Mode.", "play_capture_exit");
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }

        private static void Complete(BatchJobRecord job, string summary)
        {
            var errors = ReadErrors(job);
            job.resultData["healthy"] = GetResultInt(job, "errorCount") == 0;
            job.resultData["uniqueErrorCount"] = errors.Count;
            job.resultData["errors"] = errors;
            AsyncJobService.CompleteJob(job.jobId, summary, job.resultData);
        }

        private static List<CapturedError> ReadErrors(BatchJobRecord job)
        {
            try
            {
                if (job.resultData != null && job.resultData.TryGetValue("errors", out var value) && value != null)
                    return JToken.FromObject(value).ToObject<List<CapturedError>>() ?? new List<CapturedError>();
            }
            catch { }
            return new List<CapturedError>();
        }

        private static string ResolveScreenshotFilename(string filename)
        {
            filename = Path.GetFileName(string.IsNullOrWhiteSpace(filename) ? $"play-capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png" : filename);
            return Path.ChangeExtension(filename, ".png");
        }

        private static void Transition(BatchJobRecord job, string status, string stage, int progress, string summary, string code)
        {
            job.metadata["playCaptureStage"] = stage;
            job.status = status; job.currentStage = stage; job.progressStage = stage; job.progress = progress;
            job.resultSummary = summary; job.updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            job.logs.Add(new BatchJobLogEntry { timestamp = job.updatedAt, level = "info", stage = stage, message = summary, code = code });
            job.progressEvents.Add(new BatchJobProgressEvent { timestamp = job.updatedAt, progress = progress, stage = stage, description = summary });
            BatchPersistence.UpsertJob(job); BatchPersistence.FlushIfDirty();
        }

        private static string GetString(BatchJobRecord job, string key) => job.metadata != null && job.metadata.TryGetValue(key, out var value) ? value?.ToString() : null;
        private static int GetInt(BatchJobRecord job, string key, int fallback) => int.TryParse(GetString(job, key), out var value) ? value : fallback;
        private static long GetLong(BatchJobRecord job, string key) => long.TryParse(GetString(job, key), out var value) ? value : 0;
        private static bool GetBool(BatchJobRecord job, string key) => bool.TryParse(GetString(job, key), out var value) && value;
        private static int GetResultInt(BatchJobRecord job, string key) => job.resultData != null && job.resultData.TryGetValue(key, out var value) && int.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
        private static bool IsTerminal(string status) => status == "completed" || status == "failed" || status == "cancelled";
        private static string Truncate(string value, int limit) => string.IsNullOrEmpty(value) || value.Length <= limit ? value : value.Substring(0, limit);
        private static string FirstLines(string value, int maxLines)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var lines = value.Split('\n');
            return string.Join("\n", lines.Take(maxLines));
        }
    }
}

// Producer:Betsy
