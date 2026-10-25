using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The P1a checkpoint API: the <c>_checkpoint</c> envelope key on execute, dryRun and batches, <c>workflow_checkpoint</c>,
    /// the task and session checkpoints, and the checkpoint rows of <c>workflow_list</c>.
    /// </summary>
    [TestFixture]
    public class WorkflowCheckpointApiTests
    {
        private const string Agent = "checkpoint-api-probe";
        private const long T0 = 1_700_000_000_000;

        private string _tempRoot;
        private bool _autoCheckpoints;
        private int _idleGapSeconds;
        private bool _autoCleanEnabled;

        [SetUp]
        public void SetUp()
        {
            _autoCheckpoints = WorkflowCheckpointConfig.AutoCheckpoints;
            _idleGapSeconds = WorkflowCheckpointConfig.IdleGapSeconds;
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;
            WorkflowCheckpointConfig.AutoCheckpoints = true;
            WorkflowCheckpointConfig.IdleGapSeconds = WorkflowCheckpointConfig.DefaultIdleGapSeconds;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsCheckpointApi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));
            WorkflowLifecycle.ReplaceSpansForTests(Array.Empty<(long, long)>());
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            WorkflowLifecycle.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowCheckpointConfig.AutoCheckpoints = _autoCheckpoints;
            WorkflowCheckpointConfig.IdleGapSeconds = _idleGapSeconds;
            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
        }

        [Test]
        public void CheckpointKey_NonStringIsATypeMismatchInExecuteAndDryRun()
        {
            const string body = "{\"name\":\"CpBadType\",\"_checkpoint\":5}";
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                var executed = JObject.Parse(SkillRouter.Execute("gameobject_create", body));
                var previewed = JObject.Parse(SkillRouter.DryRun("gameobject_create", body));

                Assert.That(executed["errorCode"]?.ToString(), Is.EqualTo("TYPE_MISMATCH"), executed.ToString(Formatting.None));
                Assert.That(previewed["valid"]?.Value<bool>(), Is.False, previewed.ToString(Formatting.None));
                Assert.That(previewed.ToString(Formatting.None), Does.Contain("\"parameter\":\"_checkpoint\""));
            }
        }

        [Test]
        public void CheckpointKey_OnATrackedCall_LeavesAnExplicitCheckpointAtItsStep()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                var response = JObject.Parse(SkillRouter.Execute("gameobject_create", "{\"name\":\"CpLabelled\",\"_checkpoint\":\"before x\"}"));
                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
            }

            var step = WorkflowManager.History.tasks.Last();
            var checkpoint = WorkflowManager.GetCheckpoints().Single(c => c.label == "before x");
            Assert.That(checkpoint.id, Is.EqualTo("c" + step.seq));
            Assert.That(checkpoint.seq, Is.EqualTo(step.seq));
            Assert.That(checkpoint.kind, Is.EqualTo(WorkflowCheckpoint.KindExplicit));
        }

        [Test]
        public void CheckpointKey_OnAFailedCall_LeavesNoCheckpointAndNoPendingLabel()
        {
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                var failed = JObject.Parse(SkillRouter.Execute("gameobject_set_transform",
                    "{\"name\":\"CpNoSuchObject\",\"x\":1,\"_checkpoint\":\"lost\"}"));
                Assert.That(failed["errorCode"], Is.Not.Null, failed.ToString(Formatting.None));

                JObject.Parse(SkillRouter.Execute("gameobject_create", "{\"name\":\"CpAfterFailure\"}"));
            }

            Assert.That(WorkflowManager.GetCheckpoints().Any(c => c.label == "lost"), Is.False);
        }

        [Test]
        public void WorkflowCheckpoint_AddsRenamesAndRejectsAnUnknownId()
        {
            JObject added;
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                added = JObject.Parse(SkillRouter.Execute("workflow_checkpoint", "{\"label\":\"before lighting\"}"));
            }
            var id = added["result"]?["id"]?.ToString();
            Assert.That(id, Is.Not.Null, added.ToString(Formatting.None));
            Assert.That(WorkflowManager.FindCheckpoint(id).label, Is.EqualTo("before lighting"));

            JObject renamed;
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                renamed = JObject.Parse(SkillRouter.Execute("workflow_checkpoint", "{\"label\":\"renamed\",\"at\":\"" + id + "\"}"));
            }
            Assert.That(renamed["result"]?["id"]?.ToString(), Is.EqualTo(id), renamed.ToString(Formatting.None));
            Assert.That(WorkflowManager.FindCheckpoint(id).label, Is.EqualTo("renamed"));

            JObject unknown;
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                unknown = JObject.Parse(SkillRouter.Execute("workflow_checkpoint", "{\"label\":\"x\",\"at\":\"c999999\"}"));
            }
            Assert.That(unknown["errorCode"]?.ToString(), Is.EqualTo("NOT_FOUND"), unknown.ToString(Formatting.None));
            Assert.That(unknown["error"]?.ToString(), Does.Contain("workflow_list"));
        }

        [Test]
        public void CheckpointSkills_ReturnOnlyDeclaredOutputs()
        {
            var attribute = typeof(WorkflowSkills).GetMethod("WorkflowCheckpointMark").GetCustomAttribute<UnitySkillAttribute>();
            var result = JObject.FromObject(WorkflowSkills.WorkflowCheckpointMark("outputs probe"));
            foreach (var property in result.Properties())
                Assert.That(attribute.Outputs, Does.Contain(property.Name), "workflow_checkpoint returns an undeclared key: " + property.Name);

            var taskAttribute = typeof(WorkflowSkills).GetMethod("WorkflowTaskStart").GetCustomAttribute<UnitySkillAttribute>();
            Assert.That(taskAttribute.Outputs, Does.Contain("checkpoint"));
            Assert.That(JObject.FromObject(WorkflowSkills.WorkflowTaskStart("outputs probe"))["checkpoint"], Is.Not.Null);
            WorkflowManager.AbortTask();
        }

        [Test]
        public void TaskAndSessionStarts_AddTheirOwnCheckpoints()
        {
            var task = JObject.FromObject(WorkflowSkills.WorkflowTaskStart("CpTask"));
            var taskCheckpoint = WorkflowManager.FindCheckpoint(task["checkpoint"]?.ToString());
            Assert.That(taskCheckpoint, Is.Not.Null);
            Assert.That(taskCheckpoint.kind, Is.EqualTo(WorkflowCheckpoint.KindTask));
            Assert.That(taskCheckpoint.label, Is.EqualTo("CpTask"));
            WorkflowManager.AbortTask();

            var session = JObject.FromObject(WorkflowSkills.WorkflowSessionStart(null));
            var sessionCheckpoint = WorkflowManager.FindCheckpoint(session["checkpoint"]?.ToString());
            Assert.That(sessionCheckpoint, Is.Not.Null);
            Assert.That(sessionCheckpoint.kind, Is.EqualTo(WorkflowCheckpoint.KindSession));
            Assert.That(sessionCheckpoint.label, Is.EqualTo("Session"));
            WorkflowManager.AbortTask();
        }

        [Test]
        public void WorkflowList_InterleavesCheckpointsAroundTheirSteps()
        {
            var go = new GameObject("CheckpointListProbe");
            var first = RecordAt(T0, go, auto: true);
            WorkflowManager.AddCheckpoint(Agent, WorkflowCheckpoint.KindExplicit, "mid");
            var second = RecordAt(T0 + 1_000, go, auto: true);
            var third = RecordAt(T0 + 31_000, go, auto: true);

            Assert.That(RowIds(ListRows("{}")), Is.EqualTo(new[]
            {
                "s" + third.seq, "c" + third.seq, "s" + second.seq, "c" + second.seq, "s" + first.seq, "c" + first.seq,
            }));
            Assert.That(RowIds(ListRows("{\"before\":\"c" + second.seq + "\"}")), Is.EqualTo(new[] { "s" + first.seq, "c" + first.seq }));
            Assert.That(RowIds(ListRows("{\"before\":\"s" + third.seq + "\"}")), Is.EqualTo(new[]
            {
                "c" + third.seq, "s" + second.seq, "c" + second.seq, "s" + first.seq, "c" + first.seq,
            }));

            var page = ListResult("{\"limit\":2}");
            Assert.That(RowIds((JArray)page["rows"]), Is.EqualTo(new[] { "s" + third.seq, "c" + third.seq }));
            Assert.That(page["more"]?.ToString(), Is.EqualTo("c" + third.seq), page.ToString(Formatting.None));
        }

        [Test]
        public void WorkflowList_CheckpointsViewSummarisesTheStepsEachCheckpointCovers()
        {
            var go = new GameObject("CheckpointViewProbe");
            var first = RecordAt(T0, go, auto: true);
            WorkflowManager.AddCheckpoint(Agent, WorkflowCheckpoint.KindExplicit, "mid");
            var second = RecordAt(T0 + 1_000, go, auto: true);
            var third = RecordAt(T0 + 31_000, go, auto: true);

            var rows = ListRows("{\"view\":\"checkpoints\"}");
            Assert.That(RowIds(rows), Is.EqualTo(new[] { "c" + third.seq, "c" + second.seq, "c" + first.seq }));

            string middle = rows[1].ToString();
            Assert.That(middle, Does.StartWith("c" + second.seq + " "));
            Assert.That(middle, Does.Contain("\"mid\""));
            Assert.That(middle, Does.EndWith(": probe_write Probe (1)"));
        }

        [Test]
        public void WorkflowList_ThirtyRowsStayWithinTheByteBudget()
        {
            var go = new GameObject("CheckpointBudgetProbe");
            for (int i = 0; i < 30; i++)
                RecordAt(T0 + i * 1_000L, go, auto: false);

            var rows = ListRows("{}");
            Assert.That(rows.Count, Is.EqualTo(30));
            Assert.That(JsonConvert.SerializeObject(rows).Length, Is.LessThanOrEqualTo(1800));
        }

        [Test]
        public void BatchCheckpoint_IsAcceptedAndItsTasksShareOneGroup()
        {
            const string body = "{\"_checkpoint\":\"before batch\",\"steps\":[" +
                                "{\"skill\":\"gameobject_create\",\"args\":{\"name\":\"CpBatchA\"}}," +
                                "{\"skill\":\"gameobject_create\",\"args\":{\"name\":\"CpBatchB\"}}]}";
            var (status, json) = ProcessRequest("POST", "/skills/batch", "", body);
            Assert.That(status, Is.EqualTo(200), json);

            var tasks = WorkflowManager.History.tasks;
            var batchTasks = tasks.Skip(tasks.Count - 2).ToList();
            Assert.That(batchTasks.Select(t => t.group).Distinct().ToList(), Is.EqualTo(new[] { "b" + batchTasks[0].seq }));
            Assert.That(WorkflowManager.GetCheckpoints().Single(c => c.label == "before batch").seq, Is.EqualTo(batchTasks[0].seq));
            Assert.That(WorkflowManager.GetCheckpoints().Count(c => c.seq == batchTasks[1].seq), Is.EqualTo(0),
                "The batch's second task gets no automatic checkpoint.");
        }

        [Test]
        public void BatchCheckpoint_NonStringIsRejected()
        {
            const string body = "{\"_checkpoint\":7,\"steps\":[{\"skill\":\"gameobject_create\",\"args\":{\"name\":\"CpBatchBad\"}}]}";
            var (status, json) = ProcessRequest("POST", "/skills/batch", "", body);
            Assert.That(status, Is.EqualTo(400), json);
            Assert.That(WorkflowManager.History.tasks, Is.Empty);
        }

        private static JArray ListRows(string body) => (JArray)ListResult(body)["rows"];

        private static JObject ListResult(string body)
        {
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                var response = JObject.Parse(SkillRouter.Execute("workflow_list", body));
                Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
                return (JObject)response["result"];
            }
        }

        private static List<string> RowIds(JArray rows) =>
            rows.Select(row => row.ToString().Split(' ')[0]).ToList();

        /// <summary>Records one step at <paramref name="atMs"/>; the caller is <see cref="Agent"/>.</summary>
        private static WorkflowTask RecordAt(long atMs, GameObject go, bool auto)
        {
            WorkflowManager.NowUnixMsForTests = () => atMs;
            using (SkillsAuditLog.BeginRequestContext(-1, Agent, true))
            {
                var task = auto ? WorkflowManager.BeginAutoTask("probe_write", "probe_write", "Probe") : WorkflowManager.BeginTask("probe_write", "probe_write");
                WorkflowManager.SnapshotObject(go.transform);
                go.transform.position += Vector3.right; // An unchanged snapshot is pruned, and the step would not be recorded.
                WorkflowManager.EndTask();
                WorkflowManager.NoteAgentCall();
                return task;
            }
        }

        /// <summary>Drives the real main-thread handler (SkillsHttpServer.ProcessJob), as SkillsBatchWireTests does.</summary>
        private static (int StatusCode, string ResponseJson) ProcessRequest(string httpMethod, string path, string query, string body)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, httpMethod);
            jobType.GetField("Path").SetValue(job, path);
            jobType.GetField("QueryString").SetValue(job, query);
            jobType.GetField("Body").SetValue(job, body);
            jobType.GetField("StatusCode").SetValue(job, 200);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            return ((int)jobType.GetField("StatusCode").GetValue(job), (string)jobType.GetField("ResponseJson").GetValue(job));
        }
    }
}

// Producer:Betsy
