using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillsHttpServer
    {
        // ===== Expected instance (listener thread) =====

        internal const string ExpectInstanceQueryKey = "expectInstance";
        internal const string ExpectProjectQueryKey = "expectProject";
        private const string ExpectInstanceHeader = "X-Expect-Instance";
        private const string ExpectProjectHeader = "X-Expect-Project";

        /// <summary>
        /// The Editor a request says it is meant for. Instance is an exact instanceId (unique per project path);
        /// Project is a productName or a project folder name (productName alone can repeat across projects). Both
        /// compare case-insensitively, and when both are given both must hold.
        /// </summary>
        internal sealed class InstanceExpectation
        {
            public string Instance;
            public string Project;

            public bool Matches(string instanceId, string projectName, string projectPath)
            {
                if (!string.IsNullOrEmpty(Instance) &&
                    !string.Equals(Instance, instanceId, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.IsNullOrEmpty(Project) &&
                    !string.Equals(Project, projectName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Project, ProjectFolderName(projectPath), StringComparison.OrdinalIgnoreCase))
                    return false;

                return true;
            }

            public string Describe()
            {
                if (string.IsNullOrEmpty(Project)) return $"instance '{Instance}'";
                if (string.IsNullOrEmpty(Instance)) return $"project '{Project}'";
                return $"instance '{Instance}' in project '{Project}'";
            }
        }

        private static string ProjectFolderName(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath))
                return null;
            try { return System.IO.Path.GetFileName(projectPath.TrimEnd('/', '\\')); }
            catch (ArgumentException) { return null; }
        }

        /// <summary>
        /// /health (and its / alias) never checks an expectation — discovery must always be able to read who is
        /// serving — and neither does a CORS preflight, which carries no custom headers.
        /// </summary>
        private static bool IsInstanceCheckExempt(string httpMethod, string path) =>
            string.Equals(httpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase) ||
            path == "/" ||
            string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the caller's expected instance from the query string (expectInstance / expectProject) and the
        /// X-Expect-Instance / X-Expect-Project headers; a non-blank query value wins over the header of the same kind.
        /// Header values may be percent-encoded (the server's own X-Unity-Project header is, for non-ASCII names).
        /// Returns null when the request names no expectation, the common case, which costs one substring search.
        /// Pure string work, safe on the listener thread.
        /// </summary>
        internal static InstanceExpectation ReadInstanceExpectation(string rawQuery, string headerInstance, string headerProject)
        {
            string instance = DecodeExpectationHeader(headerInstance);
            string project = DecodeExpectationHeader(headerProject);

            if (!string.IsNullOrEmpty(rawQuery) && rawQuery.IndexOf("expect", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var qs = SkillRouter.ParseQueryString(rawQuery);
                if (qs.TryGetValue(ExpectInstanceQueryKey, out var queryInstance) && !string.IsNullOrWhiteSpace(queryInstance))
                    instance = queryInstance.Trim();
                if (qs.TryGetValue(ExpectProjectQueryKey, out var queryProject) && !string.IsNullOrWhiteSpace(queryProject))
                    project = queryProject.Trim();
            }

            if (string.IsNullOrEmpty(instance) && string.IsNullOrEmpty(project))
                return null;
            return new InstanceExpectation { Instance = instance, Project = project };
        }

        private static string DecodeExpectationHeader(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var trimmed = value.Trim();
            return trimmed.IndexOf('%') >= 0 ? Uri.UnescapeDataString(trimmed) : trimmed;
        }

        /// <summary>
        /// The 409 INSTANCE_MISMATCH body: who is actually serving, what was expected, every other live registered
        /// instance (with its reloading/stopped status), and one suggested resend per instance that satisfies the
        /// expectation. Pure; the caller supplies the registry view.
        /// </summary>
        internal static string BuildInstanceMismatchResponse(InstanceExpectation expected, string instanceId,
            string projectName, string projectPath, int port, IList<RegistryService.InstanceInfo> others)
        {
            var instances = new List<object>();
            var fixes = new List<SuggestedFix>();
            foreach (var other in others ?? Array.Empty<RegistryService.InstanceInfo>())
            {
                if (other == null)
                    continue;

                string status = RegistryService.IsRunningStatus(other.status)
                    ? RegistryService.StatusRunning
                    : other.status.ToLowerInvariant();
                instances.Add(new
                {
                    instanceId = other.id,
                    projectName = other.name,
                    projectPath = other.path,
                    port = other.port,
                    status,
                });

                if (!expected.Matches(other.id, other.name, other.path))
                    continue;

                string target = $"port {other.port} (instanceId {other.id}, project {other.name})";
                fixes.Add(new SuggestedFix
                {
                    action = "retry",
                    args = new { port = other.port, url = $"http://localhost:{other.port}", instanceId = other.id },
                    reason = status == RegistryService.StatusRunning
                        ? $"Resend to {target}."
                        : $"Resend to {target} — it is {status} right now, so wait a few seconds and retry there, not here.",
                });
            }

            if (fixes.Count == 0)
            {
                fixes.Add(new SuggestedFix
                {
                    action = "find_target",
                    reason = $"No other registered UnitySkills server matches {expected.Describe()}. Open that project in Unity and start its server, or drop the expectation if {projectName} ({instanceId}) is the Editor you want.",
                });
            }

            return SkillErrorResponse.Build(
                SkillErrorCode.InstanceMismatch,
                $"This server is {projectName} ({instanceId}) at {projectPath}; you expected {expected.Describe()}. Nothing was executed.",
                details: new
                {
                    server = new { instanceId, projectName, projectPath, port },
                    expected = new { instanceId = expected.Instance, project = expected.Project },
                    instances,
                },
                suggestedFixes: fixes,
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>Identity snapshot and connection the listener hands to the mismatch responder.</summary>
        private sealed class InstanceMismatchState
        {
            public HttpListenerContext Context;
            public InstanceExpectation Expected;
            public string InstanceId;
            public string ProjectName;
            public string ProjectPath;
            public int Port;
            public string RequestId;
            public string AgentId;
        }

        /// <summary>ThreadPool responder for a refused expectation: registry read, 409 write, quota release. Zero Unity API.</summary>
        private static void InstanceMismatchCallback(object state)
        {
            if (!(state is InstanceMismatchState mismatch))
                return;

            try
            {
                List<RegistryService.InstanceInfo> others;
                try { others = RegistryService.GetLiveInstances(); }
                catch { others = new List<RegistryService.InstanceInfo>(); }

                string json = BuildInstanceMismatchResponse(mismatch.Expected, mismatch.InstanceId,
                    mismatch.ProjectName, mismatch.ProjectPath, mismatch.Port, others);
                WriteRawJsonResponse(mismatch.Context, mismatch.RequestId, mismatch.AgentId, 409, json);
            }
            catch
            {
                CloseContextSafely(mismatch.Context);
            }
            finally
            {
                ReleasePendingSlot();
            }
        }
    }
}

// Producer:Betsy
