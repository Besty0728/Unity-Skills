using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// asset_refresh reports compileTriggered and, when its refresh started a script compilation, returns the same
    /// jobId/waitUrl a script write does; that job settles on the first compilation finishing after the refresh. The
    /// refresh, the compile state, Play Mode and the last compilation result all go through test seams, so no test
    /// imports a script or starts a real compilation.
    /// </summary>
    [TestFixture]
    public class AssetRefreshCompileJobTests
    {
        private const string WaitUrlPattern = @"^/jobs/[0-9a-f]{8}\?wait=90$";

        private readonly List<string> _jobIds = new List<string>();

        [SetUp]
        public void SetUp()
        {
            AssetSkills.RefreshOverrideForTests = () => { };
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = false;
            AsyncJobService.PlayModeOverrideForTests = false;
            CompilationResultService.LastResultJsonOverrideForTests = string.Empty;
        }

        [TearDown]
        public void TearDown()
        {
            AssetSkills.RefreshOverrideForTests = null;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = null;
            AsyncJobService.PlayModeOverrideForTests = null;
            CompilationResultService.LastResultJsonOverrideForTests = null;
            foreach (var jobId in _jobIds)
                BatchPersistence.RemoveJob(jobId);
            _jobIds.Clear();
            BatchPersistence.FlushIfDirty();
        }

        // ---------- the refresh response ----------

        [Test]
        public void NothingChanged_ReportsCompileTriggeredFalse_AndCreatesNoJob()
        {
            var jobsBefore = JobIds();

            var result = Refresh();

            Assert.That(result.Keys, Is.EquivalentTo(new[] { "success", "message", "compileTriggered" }));
            Assert.That(result["message"], Is.EqualTo("Asset database refreshed"));
            Assert.That(result["compileTriggered"], Is.False);
            Assert.That(JobIds(), Is.EquivalentTo(jobsBefore), "A refresh that compiles nothing must not leave a job behind.");
        }

        [Test]
        public void ScriptImportedDuringTheRefresh_ReturnsAProjectCompileJob()
        {
            AssetSkills.RefreshOverrideForTests = () => ScriptDomainImportCapture.Record(
                new[] { "Assets/Art/Crate.png", "Assets\\Scripts\\Spin.cs" }, null, null, null);

            var result = Refresh();

            Assert.That(result["compileTriggered"], Is.True);
            Assert.That(result["message"], Is.EqualTo("Asset database refreshed"));
            Assert.That(result["status"], Is.EqualTo("accepted"));
            var jobId = (string)result["jobId"];
            Assert.That(result["waitUrl"], Is.EqualTo(AsyncJobService.BuildWaitUrl(jobId)));
            Assert.That((string)result["waitUrl"], Does.Match(WaitUrlPattern));
            Assert.That(result["scriptChanges"], Is.EqualTo(new[] { "Assets/Scripts/Spin.cs" }));
            Assert.That(result.ContainsKey("scriptChangesTruncated"), Is.False);
            Assert.That(result.ContainsKey("serverAvailability"), Is.True);

            var job = AsyncJobService.Get(jobId);
            Assert.That(job.kind, Is.EqualTo("compile"));
            Assert.That(job.metadata["operation"], Is.EqualTo("asset_refresh"));
            Assert.That(job.metadata["diagnosticsScope"], Is.EqualTo("project"));
            Assert.That(job.metadata["scriptPaths"], Is.EqualTo(new[] { "Assets/Scripts/Spin.cs" }));

            var snapshot = JObject.Parse(GetJobsResponse(jobId, internalProbe: false));
            Assert.That(snapshot["jobId"]?.ToString(), Is.EqualTo(jobId));
            Assert.That(snapshot["kind"]?.ToString(), Is.EqualTo("compile"));
            Assert.That(snapshot["terminal"]?.Value<bool>(), Is.False);
        }

        [Test]
        public void CompilationStartedInsideTheRefresh_ReturnsAJob()
        {
            AssetSkills.RefreshOverrideForTests = CompilationResultService.RecordCompilationStartForTests;

            var result = Refresh();

            Assert.That(result["compileTriggered"], Is.True);
            Assert.That(result.ContainsKey("jobId"), Is.True);
            Assert.That(result.ContainsKey("scriptChanges"), Is.False, "No file was reported, so there is no list to show.");
        }

        [Test]
        public void CompilationQueuedBehindTheRefresh_ReturnsAJobThatWaits()
        {
            AssetSkills.RefreshOverrideForTests = () => ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;

            var result = Refresh();

            Assert.That(result["compileTriggered"], Is.True);
            Assert.That(AsyncJobService.Get((string)result["jobId"]).status, Is.EqualTo("waiting_domain_reload"));
        }

        [Test]
        public void ScriptChanges_AreCappedAtTwenty_WithTheRestCounted()
        {
            var paths = Enumerable.Range(0, 25).Select(i => $"Assets/Scripts/Gen{i}.cs").ToArray();
            AssetSkills.RefreshOverrideForTests = () => ScriptDomainImportCapture.Record(paths);

            var result = Refresh();

            Assert.That((string[])result["scriptChanges"], Is.EqualTo(paths.Take(20).ToArray()));
            Assert.That(result["scriptChangesTruncated"], Is.EqualTo(5));
            Assert.That((string[])AsyncJobService.Get((string)result["jobId"]).metadata["scriptPaths"], Has.Length.EqualTo(20));
        }

        [Test]
        public void Outputs_AreReturnedByTheirBranch_AndTheSkillStaysAllowedOutsideBypass()
        {
            Assert.That(SkillRouter.TryGetSkill("asset_refresh", out var skill), Is.True);

            var quiet = Refresh();
            AssetSkills.RefreshOverrideForTests = () => ScriptDomainImportCapture.Record(new[] { "Assets/Scripts/Spin.cs" });
            var compiling = Refresh();

            // jobId/waitUrl are advertised but only exist once a refresh starts a compile.
            Assert.That(skill.Outputs, Is.SubsetOf(quiet.Keys.Union(compiling.Keys)));
            Assert.That(skill.Outputs, Is.SubsetOf(compiling.Keys));
            Assert.That(quiet.Keys, Does.Not.Contain("jobId").And.Not.Contain("waitUrl"));
            Assert.That(skill.MayTriggerReload, Is.False,
                "MayTriggerReload would make asset_refresh MODE_FORBIDDEN in auto and approval modes.");
            Assert.That(SkillsModeManager.IsForbiddenInSemi(skill), Is.False);
        }

        [Test]
        public void Capture_KeepsScriptDomainPathsOnly_AndOnlyWhileOpen()
        {
            ScriptDomainImportCapture.Record(new[] { "Assets/Before.cs" });

            var outer = ScriptDomainImportCapture.Begin();
            string[] inner;
            using (var innerCapture = ScriptDomainImportCapture.Begin())
            {
                ScriptDomainImportCapture.Record(
                    new[] { "Assets/A.CS", "Assets/Tex.png" }, new[] { "Assets/Old.asmdef" }, null, new[] { "Assets\\A.cs" });
                inner = innerCapture.Paths;
            }
            ScriptDomainImportCapture.Record(new[] { "Assets/Plugins/csc.rsp" });
            outer.Dispose();
            ScriptDomainImportCapture.Record(new[] { "Assets/After.cs" });

            Assert.That(inner, Is.EqualTo(new[] { "Assets/A.CS", "Assets/Old.asmdef" }));
            Assert.That(outer.Paths, Is.EqualTo(new[] { "Assets/A.CS", "Assets/Old.asmdef", "Assets/Plugins/csc.rsp" }));
        }

        // ---------- the project-scope compile job ----------

        [Test]
        public void ProjectJob_NewerResultWithErrors_FailsWithProjectDiagnostics()
        {
            var job = StartSettledJob(DateTime.UtcNow.AddSeconds(-60).Ticks);
            SetLastResult(DateTime.UtcNow,
                ("Assets\\Scripts\\Spin.cs", 7, "Assets/Scripts/Spin.cs(7,5): error CS1002: ; expected"),
                ("Assets/Scripts/Other.cs", 3, "Assets/Scripts/Other.cs(3,1): error CS0246: The type or namespace name 'Foo' could not be found"));

            AsyncJobService.Pump(job.jobId);

            Assert.That(job.status, Is.EqualTo("failed"));
            Assert.That(job.currentStage, Is.EqualTo("failed_compile"));
            var compilation = Compilation(job);
            Assert.That(compilation["scope"]?.ToString(), Is.EqualTo("project"));
            Assert.That(compilation["compiled"]?.Value<bool>(), Is.True);
            Assert.That(compilation["hasErrors"]?.Value<bool>(), Is.True);
            Assert.That(compilation["errorCount"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(compilation["scriptPaths"]?.ToObject<string[]>(), Is.EqualTo(new[] { "Assets/Scripts/Spin.cs" }));
            var first = (JObject)compilation["errors"][0];
            Assert.That(first.Properties().Select(p => p.Name), Is.EquivalentTo(new[] { "type", "message", "file", "line" }),
                "Same entry shape as a script job's diagnostics.");
            Assert.That(first["file"]?.ToString(), Is.EqualTo("Assets/Scripts/Spin.cs"));
            Assert.That(first["line"]?.Value<int>(), Is.EqualTo(7));

            var probed = JObject.Parse(GetJobsResponse(job.jobId, internalProbe: true));
            Assert.That(probed["status"]?.ToString(), Is.EqualTo("failed"));
            Assert.That(probed["resultData"]?["compilation"]?["errorCount"]?.Value<int>(), Is.EqualTo(2),
                "The waitUrl's terminal answer carries the diagnostics.");
        }

        [Test]
        public void ProjectJob_CleanCompileAlreadyReloaded_Completes()
        {
            var domainLoaded = new DateTime(CompilationResultService.DomainLoadedUtcTicks, DateTimeKind.Utc);
            var job = StartSettledJob(domainLoaded.AddSeconds(-20).Ticks);
            SetLastResult(domainLoaded.AddSeconds(-10));

            AsyncJobService.Pump(job.jobId);

            Assert.That(job.status, Is.EqualTo("completed"));
            var compilation = Compilation(job);
            Assert.That(compilation["compiled"]?.Value<bool>(), Is.True);
            Assert.That(compilation["hasErrors"]?.Value<bool>(), Is.False);
            Assert.That(compilation["errorCount"]?.Value<int>(), Is.EqualTo(0));
            Assert.That(compilation["finishedAtUtc"]?.ToString(), Is.EqualTo(domainLoaded.AddSeconds(-10).ToString("o")));
        }

        [Test]
        public void ProjectJob_CleanCompileStillAheadOfItsReload_WaitsForTheReload_Bounded()
        {
            var job = StartSettledJob(DateTime.UtcNow.AddSeconds(-60).Ticks);
            SetLastResult(DateTime.UtcNow);

            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("waiting_domain_reload"),
                "The compile finished after this domain loaded, so its new types are not loaded yet.");
            Assert.That(job.currentStage, Is.EqualTo("reload_pending"));

            job.metadata["reloadWaitSince"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("completed"), "Unity skips the reload when no assembly changed; the wait must end.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ProjectJob_NoNewerResult_WaitsOutTheGrace_ThenReportsNoCompilation(bool hasOlderResult)
        {
            var job = StartSettledJob(DateTime.UtcNow.Ticks, ageSeconds: 2);
            if (hasOlderResult)
                SetLastResult(DateTime.UtcNow.AddMinutes(-5), ("Assets/Old.cs", 1, "an earlier error"));

            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("running"), "Still inside the grace period.");
            Assert.That(job.currentStage, Is.EqualTo("stabilizing"));

            job.startedAt -= 10;
            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("completed"),
                "A compilation from before the refresh describes an earlier state and must not fail this job.");
            var compilation = Compilation(job);
            Assert.That(compilation["compiled"]?.Value<bool>(), Is.False);
            Assert.That(compilation["hasErrors"]?.Value<bool>(), Is.False);
            Assert.That((JArray)compilation["errors"], Is.Empty);
        }

        [Test]
        public void ProjectJob_InPlayMode_TheGraceWaitsForPlayModeToEnd()
        {
            var job = StartSettledJob(DateTime.UtcNow.Ticks);
            AsyncJobService.PlayModeOverrideForTests = true;

            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("waiting_domain_reload"));
            Assert.That(job.currentStage, Is.EqualTo("waiting_play_mode_exit"));

            AsyncJobService.PlayModeOverrideForTests = false;
            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("running"), "The grace restarts when Play Mode ends, which is when Unity compiles.");

            job.metadata["graceAnchor"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10;
            AsyncJobService.Pump(job.jobId);
            Assert.That(job.status, Is.EqualTo("completed"));
        }

        [Test]
        public void ProjectJob_WhileUnityCompiles_KeepsWaiting()
        {
            var job = StartSettledJob(DateTime.UtcNow.Ticks);
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;

            AsyncJobService.Pump(job.jobId);

            Assert.That(job.status, Is.EqualTo("waiting_domain_reload"));
            Assert.That(job.currentStage, Is.EqualTo("compiling"));
        }

        [TestCase("")]
        [TestCase("{not json")]
        [TestCase("{\"finishedAtUtc\":\"yesterday\",\"success\":true}")]
        public void LastOutcome_IsNullWithoutAParsableResult(string json)
        {
            CompilationResultService.LastResultJsonOverrideForTests = json;

            Assert.That(CompilationResultService.GetLastOutcome(), Is.Null);
        }

        // ---------- script jobs are untouched ----------

        [Test]
        public void ScriptJob_KeepsItsMetadata_AndIgnoresTheProjectResult()
        {
            var job = AsyncJobService.StartScriptMutationJob("script_delete", "Assets/Scripts/Gone.cs",
                checkCompile: false, diagnosticLimit: 20, supportsDiagnostics: false);
            _jobIds.Add(job.jobId);
            job.startedAt -= 10;
            SetLastResult(DateTime.UtcNow, ("Assets/Scripts/Other.cs", 1, "an unrelated error"));

            Assert.That(job.metadata.Keys,
                Is.EquivalentTo(new[] { "operation", "scriptPath", "checkCompile", "diagnosticLimit", "supportsDiagnostics" }));
            AsyncJobService.Pump(job.jobId);

            Assert.That(job.status, Is.EqualTo("completed"));
            Assert.That(job.resultData.Keys, Is.EquivalentTo(new[] { "path", "operation" }));
        }

        [Test]
        public void ScriptJob_StillReportsPerScriptDiagnostics()
        {
            // A name no console entry can mention, so the per-script scan deterministically finds nothing.
            var path = $"Assets/Scripts/R5Probe{Guid.NewGuid():N}.cs";
            var job = AsyncJobService.StartScriptMutationJob("script_create", path, checkCompile: true, diagnosticLimit: 20);
            _jobIds.Add(job.jobId);
            job.startedAt -= 10;
            SetLastResult(DateTime.UtcNow, ("Assets/Scripts/Other.cs", 1, "an unrelated error"));

            AsyncJobService.Pump(job.jobId);

            Assert.That(job.status, Is.EqualTo("completed"));
            var compilation = Compilation(job);
            Assert.That(compilation.Properties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "scriptPath", "isCompiling", "hasErrors", "errorCount", "errors", "nextAction" }));
            Assert.That(compilation["scriptPath"]?.ToString(), Is.EqualTo(path));
        }

        // ---------- helpers ----------

        private Dictionary<string, object> Refresh()
        {
            var result = (Dictionary<string, object>)AssetSkills.AssetRefresh();
            if (result.TryGetValue("jobId", out var jobId))
                _jobIds.Add((string)jobId);
            return result;
        }

        /// <summary>A refresh job aged past the one-second stabilization and, by default, the grace period.</summary>
        private BatchJobRecord StartSettledJob(long refreshStartedUtcTicks, int ageSeconds = 10)
        {
            var job = AsyncJobService.StartRefreshCompileJob(new[] { "Assets/Scripts/Spin.cs" }, refreshStartedUtcTicks);
            _jobIds.Add(job.jobId);
            job.startedAt -= ageSeconds;
            return job;
        }

        /// <summary>Stands in for CompilationResultService's stored result, in the shape OnCompilationFinished writes.</summary>
        private static void SetLastResult(DateTime finishedUtc, params (string File, int Line, string Message)[] errors)
        {
            CompilationResultService.LastResultJsonOverrideForTests = new JObject
            {
                ["finishedAtUtc"] = finishedUtc.ToString("o"),
                ["durationMs"] = 1200,
                ["success"] = errors.Length == 0,
                ["errorCount"] = errors.Length,
                ["warningCount"] = 0,
                ["errors"] = new JArray(errors.Select(e => new JObject
                {
                    ["file"] = e.File,
                    ["line"] = e.Line,
                    ["column"] = 5,
                    ["message"] = e.Message,
                    ["assembly"] = "Assembly-CSharp"
                })),
                ["warnings"] = new JArray(),
                ["truncated"] = false
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JObject Compilation(BatchJobRecord job) => (JObject)JObject.FromObject(job.resultData)["compilation"];

        private static string[] JobIds() => BatchPersistence.ListJobs(100).Select(j => j.jobId).ToArray();

        /// <summary>Runs GET /jobs/{id} through SkillsHttpServer.ProcessJob, optionally flagged as a long-poll probe.</summary>
        private static string GetJobsResponse(string jobId, bool internalProbe)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var request = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(request, "GET");
            jobType.GetField("Path").SetValue(request, "/jobs/" + jobId);
            jobType.GetField("QueryString").SetValue(request, "?wait=5");
            jobType.GetField("StatusCode").SetValue(request, 200);
            jobType.GetField("IsInternalProbe").SetValue(request, internalProbe);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { request });

            Assert.That((int)jobType.GetField("StatusCode").GetValue(request), Is.EqualTo(200));
            return (string)jobType.GetField("ResponseJson").GetValue(request);
        }
    }
}

// Producer:Betsy
