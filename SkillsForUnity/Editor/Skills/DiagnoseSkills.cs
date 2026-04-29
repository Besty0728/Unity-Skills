using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// Single-call diagnostic snapshot for AI agents to triage Editor state without
    /// chaining 4–5 individual skills (console, compile, workflow, server, jobs).
    /// </summary>
    public static class DiagnoseSkills
    {
        [UnitySkill("unity_diagnose", "Aggregated Editor health snapshot — console errors, compile state, recent workflow tasks, recent jobs, server stats. Call this FIRST when triaging problems.",
            Category = SkillCategory.Debug,
            Operation = SkillOperation.Analyze | SkillOperation.Query,
            Tags = new[] { "diagnose", "health", "console", "workflow", "compile", "triage" },
            Outputs = new[] { "summary", "compile", "console", "workflow", "server", "recentJobs" },
            ReadOnly = true)]
        public static object Diagnose(int errorLimit = 20, bool includeWarnings = true, bool includeRecentJobs = true)
        {
            errorLimit = Mathf.Clamp(errorLimit, 1, 200);

            var compile = new
            {
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
            };

            object console;
            try
            {
                console = ConsoleSkills.ConsoleGetLogs(includeWarnings ? "All" : "Error", null, errorLimit);
            }
            catch (Exception ex)
            {
                console = new { error = $"Failed to read console: {ex.Message}" };
            }

            object workflow;
            try
            {
                var hist = WorkflowManager.History;
                var recent = hist?.tasks?
                    .OrderByDescending(t => t.timestamp)
                    .Take(5)
                    .Select(t => new
                    {
                        id = t.id,
                        tag = t.tag,
                        description = t.description,
                        timestamp = t.timestamp,
                        formattedTime = t.GetFormattedTime(),
                        snapshotCount = t.snapshots?.Count ?? 0,
                        sessionId = t.sessionId,
                    })
                    .ToArray();

                workflow = new
                {
                    activeTaskCount = hist?.tasks?.Count ?? 0,
                    undoneTaskCount = hist?.undoneStack?.Count ?? 0,
                    recentTasks = recent,
                };
            }
            catch (Exception ex)
            {
                workflow = new { error = $"Failed to read workflow history: {ex.Message}" };
            }

            var server = new
            {
                running = SkillsHttpServer.IsRunning,
                port = SkillsHttpServer.Port,
                queuedRequests = SkillsHttpServer.QueuedRequests,
                totalProcessed = SkillsHttpServer.TotalProcessed,
                instanceId = RegistryService.InstanceId,
                projectName = RegistryService.ProjectName,
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                requireConfirmation = ConfirmationTokenService.RequireConfirmation,
            };

            object recentJobs = null;
            if (includeRecentJobs)
            {
                try
                {
                    var jobs = BatchPersistence.ListJobs(10);
                    recentJobs = jobs.Select(j => new
                    {
                        jobId = j.jobId,
                        kind = j.kind,
                        status = j.status,
                        progress = j.progress,
                        currentStage = j.currentStage,
                        updatedAt = j.updatedAt,
                        error = j.error,
                    }).ToArray();
                }
                catch
                {
                    // Best-effort — leave null if persistence layer hiccups.
                }
            }

            int errorCount = ReadIntMember(console, "errors");
            int warningCount = ReadIntMember(console, "warnings");

            string hint;
            if (!server.running)
                hint = "REST server is not running. Start via Window > UnitySkills > Start Server.";
            else if (compile.isCompiling)
                hint = "Unity is currently compiling. Retry after compilation finishes.";
            else if (errorCount > 0)
                hint = $"{errorCount} console error(s) detected — inspect 'console.logs' for stacks.";
            else if (warningCount > 0)
                hint = $"No errors but {warningCount} warning(s) present.";
            else
                hint = "No issues detected.";

            var summary = new
            {
                healthy = server.running && !compile.isCompiling && errorCount == 0,
                consoleErrorCount = errorCount,
                consoleWarningCount = warningCount,
                isCompiling = compile.isCompiling,
                serverRunning = server.running,
                hint,
            };

            return new
            {
                summary,
                compile,
                console,
                workflow,
                server,
                recentJobs,
            };
        }

        /// <summary>Read an int property/field from anonymous-typed objects without throwing.</summary>
        private static int ReadIntMember(object source, string memberName)
        {
            if (source == null || string.IsNullOrEmpty(memberName)) return 0;
            try
            {
                var type = source.GetType();
                var prop = type.GetProperty(memberName);
                if (prop != null && prop.GetValue(source) is int p) return p;
                var field = type.GetField(memberName);
                if (field != null && field.GetValue(source) is int f) return f;
            }
            catch
            {
                // Ignore — diagnostic helper must not throw on best-effort reads.
            }
            return 0;
        }
    }
}
