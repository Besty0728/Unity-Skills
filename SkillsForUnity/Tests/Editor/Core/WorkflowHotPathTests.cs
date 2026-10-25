using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The 3.0 P0 workflow changes: no cleanup inside EndTask (which only appends to the history log), dedupe before
    /// capture, the session caches of asset GlobalObjectIds and source file hashes, entity ids in snapshots (the only
    /// same-session id on 6000.4+), agent attribution with late binding, per-agent idle-gap groups,
    /// the compact workflow_list, the bookkeeping time moved from the response to telemetry, and the batch fixes (a
    /// transactional rollback removes what Undo put back; an async job records each chunk as its own task).
    /// </summary>
    [TestFixture]
    public class WorkflowHotPathTests
    {
        private const string AssetRoot = "Assets/Temp/WorkflowHotPathTests";
        private string _tempRoot;
        private bool _autoCleanEnabled;
        private bool _autoCheckpoints;
        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;

        [SetUp]
        public void SetUp()
        {
            _autoCleanEnabled = WorkflowAutoCleanConfig.Enabled;
            WorkflowAutoCleanConfig.Enabled = false;
            // Checkpoint rows are covered by WorkflowCheckpointApiTests; these tests pin the step rows and the byte budget.
            _autoCheckpoints = WorkflowCheckpointConfig.AutoCheckpoints;
            WorkflowCheckpointConfig.AutoCheckpoints = false;
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsWorkflowHotPath_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideRootForTests(Path.Combine(_tempRoot, "workflow"));

            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(AssetRoot))
                AssetDatabase.CreateFolder("Assets/Temp", "WorkflowHotPathTests");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, AssetRoot + "/WorkflowHotPathTestScene.unity"), Is.True);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            WorkflowManager.AbortTask();
            WorkflowManager.OverrideRootForTests(null);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(AssetRoot)) AssetDatabase.DeleteAsset(AssetRoot);
            AssetDatabase.Refresh();
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            WorkflowAutoCleanConfig.Enabled = _autoCleanEnabled;
            WorkflowCheckpointConfig.AutoCheckpoints = _autoCheckpoints;
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.CurrentMode = _savedMode;
        }

        [Test]
        public void EndTask_OnlyAppends_NeverCleansUpInline()
        {
            var go = new GameObject("HotPathAppendProbe");
            int cleanupsBefore = WorkflowManager.CleanupRunsForTests;
            string log = Path.Combine(_tempRoot, "workflow", "log.jsonl");
            string previous = string.Empty;

            for (int i = 0; i < 3; i++)
            {
                WorkflowManager.BeginTask("append-probe", "test");
                WorkflowManager.SnapshotObject(go.transform);
                Touch(go.transform);
                WorkflowManager.EndTask();
                string current = File.ReadAllText(log);
                Assert.That(current, Does.StartWith(previous), "A write only appends; it never rewrites what the log holds.");
                previous = current;
            }

            Assert.That(previous.Split('\n').Count(line => line.Length > 0), Is.EqualTo(3), "One record per task.");
            Assert.That(previous, Does.Contain("append-probe"));
            Assert.That(WorkflowManager.CleanupRunsForTests, Is.EqualTo(cleanupsBefore),
                "Eviction and GC read whole segments and the blob store; they run deferred, never inside EndTask.");
        }

        [Test]
        public void SnapshotObject_RepeatInOneTask_CapturesNothingTheSecondTime()
        {
            var go = new GameObject("HotPathDedupeProbe");
            WorkflowManager.BeginTask("dedupe-probe", "test");
            int before = WorkflowManager.GlobalObjectIdComputationsForTests;

            WorkflowManager.SnapshotObject(go.transform);
            int afterFirst = WorkflowManager.GlobalObjectIdComputationsForTests;
            WorkflowManager.SnapshotObject(go.transform);

            Assert.That(afterFirst, Is.GreaterThan(before), "Precondition: the first snapshot computes a GlobalObjectId.");
            Assert.That(WorkflowManager.GlobalObjectIdComputationsForTests, Is.EqualTo(afterFirst),
                "A repeat snapshot is recognized by entity id before any capture work.");
            Assert.That(WorkflowManager.CurrentTask.snapshots, Has.Count.EqualTo(1));
            Assert.That(WorkflowManager.CurrentTask.snapshots[0].entityId, Is.EqualTo(UnityObjectIdUtility.GetEntityId(go.transform)));
        }

        [Test]
        public void CreatedSnapshots_RepeatInOneTask_CaptureNothingTheSecondTime()
        {
            var go = new GameObject("HotPathCreatedProbe");
            var light = go.AddComponent<Light>();
            WorkflowManager.BeginTask("created-probe", "test");

            WorkflowManager.SnapshotCreatedGameObject(go);
            WorkflowManager.SnapshotCreatedComponent(light);
            int afterFirst = WorkflowManager.GlobalObjectIdComputationsForTests;
            WorkflowManager.SnapshotCreatedGameObject(go);
            WorkflowManager.SnapshotCreatedComponent(light);

            Assert.That(WorkflowManager.GlobalObjectIdComputationsForTests, Is.EqualTo(afterFirst),
                "A repeat registration is recognized by entity id before the GlobalObjectId and hierarchy capture.");
            Assert.That(WorkflowManager.CurrentTask.snapshots, Has.Count.EqualTo(2));
        }

        [Test]
        public void PersistentAssetGlobalObjectId_IsComputedOncePerSession()
        {
            var material = CreateMaterial("HotPathCache.mat");

            WorkflowManager.BeginTask("cache-first", "test");
            WorkflowManager.SnapshotObject(material);
            WorkflowManager.EndTask();

            WorkflowManager.BeginTask("cache-second", "test");
            int beforeSecond = WorkflowManager.GlobalObjectIdComputationsForTests;
            WorkflowManager.SnapshotObject(material);
            Assert.That(WorkflowManager.GlobalObjectIdComputationsForTests, Is.EqualTo(beforeSecond),
                "The material and the shader it references were resolved by the first task and are cached.");
            WorkflowManager.EndTask();

            WorkflowManager.InvalidateGlobalObjectIdCache();
            WorkflowManager.BeginTask("cache-third", "test");
            int beforeThird = WorkflowManager.GlobalObjectIdComputationsForTests;
            WorkflowManager.SnapshotObject(material);
            Assert.That(WorkflowManager.GlobalObjectIdComputationsForTests, Is.GreaterThan(beforeThird),
                "An invalidated cache computes again.");
        }

        [Test]
        public void StoringAnUnchangedSourceFileAgain_HashesItOnce()
        {
            string assetPath = AssetRoot + "/HotPathHash.txt";
            Assert.That(WorkflowFileStore.TryGetSafeAssetFullPath(assetPath, out string fullPath), Is.True);
            try
            {
                // Not imported (so no .meta), and older than the racy window, as a file that has sat on disk is.
                File.WriteAllText(fullPath, "workflow hash cache probe");
                File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddMinutes(-5));

                int before = WorkflowFileStore.FileHashComputationsForTests;
                string first = WorkflowFileStore.StoreFile(assetPath, move: false);
                int afterFirst = WorkflowFileStore.FileHashComputationsForTests;
                string second = WorkflowFileStore.StoreFile(assetPath, move: false);

                Assert.That(afterFirst - before, Is.EqualTo(1));
                Assert.That(second, Is.EqualTo(first));
                Assert.That(WorkflowFileStore.FileHashComputationsForTests, Is.EqualTo(afterFirst),
                    "An unchanged file (same length and write time) is not read again.");

                File.WriteAllText(fullPath, "workflow hash cache probe, edited");
                string third = WorkflowFileStore.StoreFile(assetPath, move: false);
                Assert.That(third, Is.Not.EqualTo(first));
                Assert.That(WorkflowFileStore.FileHashComputationsForTests, Is.GreaterThan(afterFirst));
            }
            finally
            {
                File.Delete(fullPath);
            }
        }

        [Test]
        public void UnsavedSceneGlobalObjectId_IsRecognizedInBothForms()
        {
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId("GlobalObjectId_V1-0-00000000000000000000000000000000-0-0"), Is.True);
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId("GlobalObjectId_V1-2-00000000000000000000000000000000-1153896829-0"), Is.True,
                "The 2022.3 form: a scene-object type and a local id, but no scene GUID.");
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId("GlobalObjectId_V1-2-9f3c5e1a0b7d4c2e8f6a1b3c5d7e9f01-1153896829-0"), Is.False);
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId("setting:physics.gravity"), Is.False);
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId(null), Is.False);
        }

        [Test]
        public void UnsavedSceneObject_IsRestoredThroughItsEntityId()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("HotPathUnsavedProbe");

            WorkflowManager.BeginTask("unsaved-move", "test");
            WorkflowManager.SnapshotObject(go.transform);
            var snapshot = WorkflowManager.CurrentTask.snapshots.Single();
            Assert.That(WorkflowManager.IsUnsavedSceneGlobalObjectId(snapshot.globalObjectId), Is.True,
                "Precondition: an object of a never-saved scene has a GlobalObjectId with no asset GUID: " + snapshot.globalObjectId);
            Assert.That(snapshot.entityId, Is.EqualTo(UnityObjectIdUtility.GetEntityId(go.transform)));
            go.transform.position = new Vector3(5f, 0f, 0f);
            WorkflowManager.EndTask();

            var task = WorkflowManager.History.tasks.Last();
            // The legacy instance id is 0 on 6000.4+; clearing it here takes the entity-id path on every version.
            task.snapshots.Single().objectInstanceId = 0;
            var result = WorkflowManager.UndoTask(task.DisplayId);

            Assert.That(result.success, Is.True, result.details.FirstOrDefault()?.error);
            Assert.That(go.transform.position, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void TaskFromAnotherEditorSession_NeverResolvesByEntityId()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("HotPathOtherSessionProbe");

            WorkflowManager.BeginTask("other-session-move", "test");
            WorkflowManager.SnapshotObject(go.transform);
            go.transform.position = new Vector3(5f, 0f, 0f);
            WorkflowManager.EndTask();

            var task = WorkflowManager.History.tasks.Last();
            task.idSession = "an-earlier-editor-session";
            var result = WorkflowManager.UndoTask(task.DisplayId);

            Assert.That(result.success, Is.False,
                "Ids from an earlier editor session may now name an unrelated object, so they must not be used.");
            Assert.That(go.transform.position, Is.EqualTo(new Vector3(5f, 0f, 0f)));
        }

        [Test]
        public void PartialUndo_KeepsTheStepWhole_AndRecordsTheRestore()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var first = new GameObject("HotPathSplitFirst");
            var second = new GameObject("HotPathSplitSecond");

            WorkflowManager.BeginTask("split-move", "test");
            WorkflowManager.SnapshotObject(first.transform);
            WorkflowManager.SnapshotObject(second.transform);
            first.transform.position = new Vector3(1f, 0f, 0f);
            second.transform.position = new Vector3(2f, 0f, 0f);
            WorkflowManager.EndTask();
            var task = WorkflowManager.History.tasks.Last();

            // Undo runs newest first; an unresolvable first snapshot fails the undo after the second has been restored.
            var firstSnapshot = task.snapshots[0];
            string firstEntityId = firstSnapshot.entityId;
            int firstInstanceId = firstSnapshot.objectInstanceId;
            firstSnapshot.entityId = null;
            firstSnapshot.objectInstanceId = 0;
            var partial = WorkflowManager.UndoTask(task.DisplayId);
            firstSnapshot.entityId = firstEntityId;
            firstSnapshot.objectInstanceId = firstInstanceId;

            Assert.That(partial.success, Is.False);
            Assert.That(partial.failed, Is.EqualTo(1));
            Assert.That(second.transform.position, Is.EqualTo(Vector3.zero));
            Assert.That(WorkflowManager.History.tasks.Single(t => t.id == task.id).snapshots, Has.Count.EqualTo(2),
                "The step is not split: it keeps every snapshot.");
            Assert.That(WorkflowManager.GetUndoableRestores(), Has.Count.EqualTo(1));
        }

        [Test]
        public void EndTask_AttributesSeqAgentAndIdleGapGroups()
        {
            var go = new GameObject("HotPathGroupProbe");

            var t1 = Record("AgentA", 0, go, auto: true);
            var t2 = Record("AgentA", 10_000, go, auto: true);
            var t3 = Record("AgentB", 12_000, go, auto: true);
            var t4 = Record("AgentA", 50_000, go, auto: true);
            var t5 = Record("AgentA", 55_000, go, auto: false);
            var t6 = Record("AgentA", 60_000, go, auto: true);

            Assert.That(new[] { t1.seq, t2.seq, t3.seq, t4.seq, t5.seq, t6.seq }, Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }));
            Assert.That(t1.agent, Is.EqualTo("AgentA"));
            Assert.That(t3.agent, Is.EqualTo("AgentB"));
            Assert.That(t1.group, Is.EqualTo("g1"));
            Assert.That(t2.group, Is.EqualTo("g1"), "10 s after AgentA's last call: same run.");
            Assert.That(t3.group, Is.EqualTo("g3"), "Another agent has its own runs.");
            Assert.That(t4.group, Is.EqualTo("g4"), "40 s after AgentA's last call: a new run.");
            Assert.That(t5.group, Is.EqualTo("g5"), "An explicit task is a run of its own.");
            Assert.That(t6.group, Is.EqualTo("g6"), "workflow_task_end closed the explicit task's run.");
        }

        [Test]
        public void HeaderGuessAgent_IsFoldedToUnknown_ThenBoundOnceTheResolverAnswers()
        {
            Assert.That(WorkflowManager.NormalizeAgent("curl"), Is.EqualTo(EditorChangeTrackerService.AgentUnknown));
            Assert.That(WorkflowManager.NormalizeAgent("Python"), Is.EqualTo(EditorChangeTrackerService.AgentUnknown));
            Assert.That(WorkflowManager.NormalizeAgent("Unknown"), Is.EqualTo(EditorChangeTrackerService.AgentUnknown));
            Assert.That(WorkflowManager.NormalizeAgent("Unknown(Go-http-client/1.1)"), Is.EqualTo(EditorChangeTrackerService.AgentUnknown));
            Assert.That(WorkflowManager.NormalizeAgent("Codex"), Is.EqualTo("Codex"));

            var answers = new Dictionary<int, string>();
            WorkflowManager.AgentLookupForTests = (int port, out string agentId) => answers.TryGetValue(port, out agentId);
            var go = new GameObject("HotPathLateAgentProbe");

            WorkflowTask task;
            using (SkillsAuditLog.BeginRequestContext(50123, "curl", false))
            {
                task = WorkflowManager.BeginAutoTask("probe_write", "probe_write", "Probe");
                WorkflowManager.SnapshotObject(go.transform);
                Touch(go.transform);
                WorkflowManager.EndTask();
            }
            Assert.That(task.agent, Is.EqualTo(EditorChangeTrackerService.AgentUnknown),
                "Before the resolver answers, the header guess 'curl' must not become an agent key of its own.");

            // The resolver lands for both connections of the same agent; its next call lists its own rows.
            answers[50123] = "ClaudeCode";
            answers[50124] = "ClaudeCode";
            JObject result;
            using (SkillsAuditLog.BeginRequestContext(50124, "curl", false))
                result = JObject.FromObject(WorkflowSkills.WorkflowList(agent: "self"));

            var rows = result["rows"].Values<string>().ToArray();
            Assert.That(result["agent"]?.ToString(), Is.EqualTo("ClaudeCode"));
            Assert.That(task.agent, Is.EqualTo("ClaudeCode"));
            Assert.That(rows, Has.Length.EqualTo(1), string.Join("\n", rows));
            Assert.That(rows[0], Does.StartWith(task.DisplayId + " ").And.Not.Contain("@"));
        }

        [Test]
        public void WorkflowList_Rows_AreNewestFirst_WithOtherAgentsMarked()
        {
            var go = new GameObject("HotPathListProbe");
            Record("AgentA", 0, go, auto: true, target: "Player");
            Record("AgentA", 10_000, go, auto: true, target: "Player");
            Record("AgentB", 12_000, go, auto: true, target: "Floor");

            JObject result;
            using (SkillsAuditLog.BeginRequestContext(-1, "AgentA", true))
                result = JObject.FromObject(WorkflowSkills.WorkflowList());

            var rows = result["rows"].Values<string>().ToArray();
            Assert.That(result["agent"]?.ToString(), Is.EqualTo("AgentA"));
            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(3));
            Assert.That(rows, Has.Length.EqualTo(3), string.Join("\n", rows));
            Assert.That(rows[0], Does.StartWith("s3 ").And.Contain("Floor (1)").And.EndWith("@AgentB"));
            Assert.That(rows[1], Does.StartWith("s2 ").And.Not.Contain("@"));
            Assert.That(rows[2], Does.StartWith("s1 ").And.Contain("Player (1)"));
            Assert.That(result["more"], Is.Null);
        }

        [Test]
        public void WorkflowList_PagesInSeqOrder_AfterAnUndoAndRedo()
        {
            var go = new GameObject("HotPathRedoOrderProbe");
            for (int i = 0; i < 2; i++)
                Record("AgentA", i * 1000L, go, auto: true, target: "Player");
            Assert.That(WorkflowManager.UndoTask("s2").success, Is.True);
            Assert.That(WorkflowManager.RedoTask("s2").success, Is.True);
            // Recorded after the undo and the redo: a later step on the same object would be a conflict of the undo.
            Record("AgentA", 2000L, go, auto: true, target: "Player");
            // Steps sort at 2 * seq + 1 and checkpoints at 2 * position, so the undo and the redo (each with its guard) lead the list.
            var newest = WorkflowManager.History.tasks.Select(task => (Key: 2 * task.seq + 1, Id: task.DisplayId))
                .Concat(WorkflowManager.History.checkpoints.Select(checkpoint => (Key: 2 * checkpoint.seq, Id: checkpoint.id)))
                .OrderByDescending(entry => entry.Key).Select(entry => entry.Id).ToArray();

            JObject result;
            using (SkillsAuditLog.BeginRequestContext(-1, "AgentA", true))
                result = JObject.FromObject(WorkflowSkills.WorkflowList(limit: 2));

            var rows = result["rows"].Values<string>().ToArray();
            Assert.That(rows[0], Does.StartWith(newest[0] + " "));
            Assert.That(rows[1], Does.StartWith(newest[1] + " "));
            Assert.That(result["more"]?.ToString(), Is.EqualTo(newest[1]));
        }

        [Test]
        public void WorkflowList_ThirtyLongRows_StayWithinAnHonestBudget_AndPage()
        {
            var go = new GameObject("HotPathBudgetProbe");
            // Among the longest tracked skill names (median 23 chars, longest 47), with a target cut at the 32-char cap.
            const string skill = "scriptableobject_set_serialized_property";
            string target = SkillRouter.DescribeWorkflowTarget(new JObject { ["name"] = "Environment/Lighting/DirectionalLightRig" });
            Assert.That(target, Has.Length.EqualTo(32));
            for (int i = 0; i < 31; i++)
                Record("AgentA", i * 1000L, go, auto: true, target: target, tag: skill);

            JObject result;
            using (SkillsAuditLog.BeginRequestContext(-1, "AgentA", true))
                result = JObject.FromObject(WorkflowSkills.WorkflowList());

            var rows = result["rows"].Values<string>().ToArray();
            Assert.That(rows, Has.Length.EqualTo(30));
            // "s<5 digits> HH:mm:ss <skill> <target> (<2 digits>)": a row like this is about 95 bytes, plus " @<agent>" for
            // another agent's. A median skill with a short target is about half that.
            int rowBudget = "s99999 00:00:00 ".Length + skill.Length + 1 + target.Length + " (99)".Length;
            Assert.That(rows.Max(row => row.Length), Is.LessThanOrEqualTo(rowBudget), rows[0]);
            int bytes = System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(result, Formatting.None));
            Assert.That(bytes, Is.LessThanOrEqualTo(30 * (rowBudget + 3) + 100), "30 long rows stay under about 3 KB.");
            Assert.That(result["more"]?.ToString(), Is.EqualTo("s2"));

            JObject next;
            using (SkillsAuditLog.BeginRequestContext(-1, "AgentA", true))
                next = JObject.FromObject(WorkflowSkills.WorkflowList(before: "s2"));
            var nextRows = next["rows"].Values<string>().ToArray();
            Assert.That(nextRows, Has.Length.EqualTo(1), string.Join("\n", nextRows));
            Assert.That(nextRows[0], Does.StartWith("s1 "));
            Assert.That(next["more"], Is.Null);
        }

        [Test]
        public void WorkflowList_ObjectsFormat_KeepsTheLegacyShape()
        {
            var go = new GameObject("HotPathObjectsProbe");
            Record("AgentA", 0, go, auto: true, target: "Player");
            Record("AgentA", 1000, go, auto: true, target: "Player");

            var result = JObject.FromObject(WorkflowSkills.WorkflowList(format: "objects"));
            var history = (JArray)result["history"];

            Assert.That(result["count"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(history, Has.Count.EqualTo(2));
            foreach (var key in new[] { "id", "tag", "description", "time", "changes" })
                Assert.That(history[0][key], Is.Not.Null, key);
            Assert.That(history[0]["seq"]?.ToString(), Is.EqualTo("s1"), "Objects stay oldest first.");
            Assert.That(history[0]["group"]?.ToString(), Is.EqualTo("g1"), "The group stays available to scripts.");
            Assert.That(result["rows"], Is.Null);
        }

        [Test]
        public void WorkflowList_RejectsAnUnknownFormat()
        {
            var result = JObject.FromObject(WorkflowSkills.WorkflowList(format: "table"));
            Assert.That(result["errorCode"]?.ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        [Test]
        public void DescribeWorkflowTarget_NamesTheFirstTargetAndCountsItems()
        {
            Assert.That(SkillRouter.DescribeWorkflowTarget(new JObject { ["name"] = "Player", ["assetPath"] = "Assets/x.mat" }), Is.EqualTo("Player"));
            Assert.That(SkillRouter.DescribeWorkflowTarget(new JObject { ["materialPath"] = "Assets/Mats/Floor.mat" }), Is.EqualTo("Assets/Mats/Floor.mat"));
            Assert.That(SkillRouter.DescribeWorkflowTarget(new JObject { ["items"] = "[{\"name\":\"A\"},{\"name\":\"B\"}]" }), Is.EqualTo("x2"));
            Assert.That(SkillRouter.DescribeWorkflowTarget(new JObject { ["name"] = new string('n', 40) }), Has.Length.EqualTo(32).And.EndWith("..."));
            Assert.That(SkillRouter.DescribeWorkflowTarget(new JObject { ["value"] = 3 }), Is.Null);
        }

        [Test]
        public void TrackedWrite_ResponseCarriesNoWorkflowTime_TheTelemetryHandOffDoes()
        {
            var response = JObject.Parse(SkillRouter.Execute("gameobject_create",
                new JObject { ["name"] = "HotPathEnvelopeProbe" }.ToString(Formatting.None)));

            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), response.ToString(Formatting.None));
            Assert.That(response.Property("workflowEndMs"), Is.Null);
            Assert.That(SkillRouter.ConsumeLastWorkflowEndMs(), Is.Not.Null);
            Assert.That(SkillRouter.ConsumeLastWorkflowEndMs(), Is.Null, "The value is handed off once.");

            var task = WorkflowManager.History.tasks.Last();
            Assert.That(task.tag, Is.EqualTo("gameobject_create"));
            Assert.That(task.target, Is.EqualTo("HotPathEnvelopeProbe"));
            Assert.That(task.agent, Is.EqualTo(EditorChangeTrackerService.AgentUnknown));

            SkillRouter.Execute("gameobject_create", new JObject { ["name"] = "HotPathEnvelopeProbe2" }.ToString(Formatting.None));
            SkillRouter.Execute("no_such_skill_hot_path_probe", "{}");
            Assert.That(SkillRouter.ConsumeLastWorkflowEndMs(), Is.Null,
                "An unknown skill returns before anything runs and must not hand on the previous call's time.");
        }

        [Test]
        public void TelemetryRow_CarriesWfMs_OnlyWhenGiven()
        {
            var withTime = JObject.Parse(SkillTelemetryService.BuildLine(new SkillTelemetryService.PendingRecord
            {
                Ts = "2026-10-22T00:00:00.000Z", Skill = "probe", FallbackAgentId = "tests", Mode = "execute", Ok = true,
                DurationMs = 3, WorkflowMs = 7,
            }));
            var without = JObject.Parse(SkillTelemetryService.BuildLine(new SkillTelemetryService.PendingRecord
            {
                Ts = "2026-10-22T00:00:00.000Z", Skill = "probe", FallbackAgentId = "tests", Mode = "execute", Ok = true,
                DurationMs = 3,
            }));

            Assert.That(withTime["wfMs"]?.Value<long>(), Is.EqualTo(7));
            Assert.That(withTime["ms"]?.Value<long>(), Is.EqualTo(3));
            Assert.That(without.Property("wfMs"), Is.Null);
        }

        [Test]
        public void TransactionalBatchRollback_DropsTheTasksOfRolledBackSceneSteps()
        {
            long firstSeq = WorkflowManager.NextTaskSeq;
            var steps = new JArray
            {
                new JObject { ["skill"] = "gameobject_create", ["args"] = new JObject { ["name"] = "HotPathTxProbe" } },
                new JObject { ["skill"] = "gameobject_set_active", ["args"] = new JObject { ["name"] = "HotPathTxDoesNotExist", ["active"] = false } },
            };

            var result = SkillsHttpServer.ExecuteBatchCore(steps, new JObject(), continueOnError: false, dryRun: false, transactional: true, agentId: "tests");

            Assert.That(result["rolledBack"]?.Value<bool>(), Is.True, result.ToString(Formatting.None));
            Assert.That(WorkflowManager.NextTaskSeq, Is.GreaterThan(firstSeq), "Precondition: the first step recorded a task.");
            Assert.That(WorkflowManager.History.tasks.Where(task => task.seq >= firstSeq), Is.Empty,
                "The rolled-back step's task would offer an undo of a change that no longer exists.");
        }

        [Test]
        public void TransactionalDryRun_LeavesEarlierTasksAlone()
        {
            var go = new GameObject("HotPathTxDryRunProbe");
            RecordExplicit("before-dry-run", go);
            var steps = new JArray
            {
                new JObject { ["skill"] = "gameobject_create", ["args"] = new JObject { ["name"] = "HotPathTxDryRunCreate" } },
                new JObject { ["skill"] = "no_such_skill_hot_path_probe", ["args"] = new JObject() },
            };

            var result = SkillsHttpServer.ExecuteBatchCore(steps, new JObject(), continueOnError: false, dryRun: true, transactional: true, agentId: "tests");

            Assert.That(result["failed"]?.Value<int>(), Is.GreaterThan(0), "Precondition: " + result.ToString(Formatting.None));
            Assert.That(WorkflowManager.History.tasks.Select(task => task.tag), Is.EqualTo(new[] { "before-dry-run" }));
        }

        [Test]
        public void RollBackBatch_KeepsDiskWrites_AndTrimsTheTaskAlreadyOpen()
        {
            var go = new GameObject("HotPathRollbackProbe");
            var other = new GameObject("HotPathRollbackOther");
            var material = CreateMaterial("HotPathRollback.mat");

            // A batch run while a task is open records into it; the rollback takes back only what the batch added.
            WorkflowManager.BeginTask("outer", "test");
            WorkflowManager.SnapshotObject(go.transform);
            var mark = WorkflowManager.MarkBatchStart();
            WorkflowManager.SnapshotObject(other.transform);
            WorkflowManager.SnapshotObject(material);
            WorkflowManager.RollBackBatch(mark);
            Assert.That(WorkflowManager.CurrentTask.snapshots.Select(s => s.objectName),
                Is.EqualTo(new[] { go.transform.name, material.name }));
            WorkflowManager.EndTask();

            // A step's own task that wrote a scene object and an asset keeps the asset half.
            mark = WorkflowManager.MarkBatchStart();
            RecordExplicit("mixed-step", other.transform, material);
            WorkflowManager.RollBackBatch(mark);
            var mixed = WorkflowManager.History.tasks.Single(task => task.tag == "mixed-step");
            Assert.That(mixed.snapshots.Select(s => s.objectName), Is.EqualTo(new[] { material.name }));
        }

        [Test]
        public void RecordedStep_KeepsAnUndoneRestoreListed()
        {
            var go = new GameObject("HotPathRedoStackProbe");
            RecordExplicit("undone-before-batch", go);
            Assert.That(WorkflowManager.UndoTask(WorkflowManager.History.tasks.Last().DisplayId).success, Is.True);
            Assert.That(WorkflowManager.GetUndoableRestores(), Has.Count.EqualTo(1));

            var mark = WorkflowManager.MarkBatchStart();
            RecordExplicit("scene-step", go);
            Assert.That(WorkflowManager.GetUndoableRestores(), Has.Count.EqualTo(1),
                "A new step does not retire an undone restore: it stays listed until it is redone.");
            WorkflowManager.RollBackBatch(mark);

            Assert.That(WorkflowManager.History.tasks.Where(task => task.tag == "scene-step"), Is.Empty);
            Assert.That(WorkflowManager.GetUndoableRestores(), Has.Count.EqualTo(1));
        }

        [Test]
        public void AsyncBatchJob_RecordsEachChunkAsItsOwnTask_UnderTheJobsAgentAndGroup()
        {
            string jobId = null;
            string batchDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "batch")).FullName;
            using (BatchPersistence.BeginTestScope(batchDir))
            {
                try
                {
                    new GameObject("HotPathJobProbeA");
                    new GameObject("HotPathJobProbeB");
                    var preview = Payload(SkillRouter.Execute("batch_preview_rename", new JObject
                    {
                        ["queryJson"] = new JObject { ["name"] = "HotPathJobProbe" }.ToString(Formatting.None),
                        ["mode"] = "prefix",
                        ["prefix"] = "Ren_",
                    }.ToString(Formatting.None)));
                    Assert.That(preview["executableCount"]?.Value<int>(), Is.EqualTo(2), preview.ToString(Formatting.None));

                    JObject accepted;
                    using (SkillsAuditLog.BeginRequestContext(-1, "JobOwner", true))
                    {
                        accepted = Payload(SkillRouter.Execute("batch_execute", new JObject
                        {
                            ["confirmToken"] = preview["confirmToken"]?.ToString(),
                            ["runAsync"] = true,
                            ["chunkSize"] = 1,
                        }.ToString(Formatting.None)));
                    }
                    jobId = accepted["jobId"]?.ToString();
                    Assert.That(jobId, Is.Not.Null.And.Not.Empty, accepted.ToString(Formatting.None));
                    Assert.That(BatchJobService.Get(jobId).agentId, Is.EqualTo("JobOwner"), "Resolved inside the batch_execute request.");

                    // A task open while a chunk runs is set aside untouched, not handed the job's changes.
                    var outer = WorkflowManager.BeginTask("outer", "test");
                    BatchJobService.Pump(jobId);
                    Assert.That(WorkflowManager.CurrentTask, Is.SameAs(outer));
                    Assert.That(outer.snapshots, Is.Empty);
                    WorkflowManager.AbortTask();

                    // Another agent's write is its own task, and a chunk pumped inside its request is still the job's.
                    using (SkillsAuditLog.BeginRequestContext(-1, "Bystander", true))
                    {
                        SkillRouter.Execute("gameobject_create", new JObject { ["name"] = "HotPathBystander" }.ToString(Formatting.None));
                        BatchJobService.Pump(jobId);
                    }
                    Assert.That(WorkflowManager.IsRecording, Is.False, "No task may stay open across frames.");

                    var job = BatchJobService.Get(jobId);
                    Assert.That(job.status, Is.EqualTo("completed"));
                    var jobTasks = WorkflowManager.History.tasks.Where(task => task.sessionId == job.relatedWorkflowId).ToList();
                    Assert.That(jobTasks, Has.Count.EqualTo(2), "One task per chunk, sharing the job's workflow id.");
                    Assert.That(jobTasks.Select(task => task.agent), Is.All.EqualTo("JobOwner"));
                    Assert.That(jobTasks.Select(task => task.group), Is.All.EqualTo("j" + jobId));
                    var bystander = WorkflowManager.History.tasks.Single(task => task.tag == "gameobject_create");
                    Assert.That(bystander.sessionId, Is.Null);
                    Assert.That(bystander.agent, Is.EqualTo("Bystander"));
                }
                finally
                {
                    // A job left unfinished by a failed assertion must not run on against the real job table.
                    BatchJobService.NotifyCancelled(jobId);
                }
            }
        }

        /// <summary>One tracked call by <paramref name="agent"/> at <paramref name="atMs"/> on the test clock.</summary>
        private static WorkflowTask Record(string agent, long atMs, GameObject go, bool auto, string target = null, string tag = "probe_write")
        {
            WorkflowManager.NowUnixMsForTests = () => atMs;
            using (SkillsAuditLog.BeginRequestContext(-1, agent, true))
            {
                var task = auto ? WorkflowManager.BeginAutoTask(tag, tag, target) : WorkflowManager.BeginTask(tag, tag);
                WorkflowManager.SnapshotObject(go.transform);
                Touch(go.transform);
                WorkflowManager.EndTask();
                WorkflowManager.NoteAgentCall();
                return task;
            }
        }

        private static void RecordExplicit(string tag, GameObject go) => RecordExplicit(tag, go.transform);

        private static void RecordExplicit(string tag, params UnityEngine.Object[] targets)
        {
            WorkflowManager.BeginTask(tag, tag);
            foreach (var target in targets)
            {
                WorkflowManager.SnapshotObject(target);
                if (target is Transform transform)
                    Touch(transform);
            }
            WorkflowManager.EndTask();
        }

        // A step must change what it snapshots, or the unchanged snapshot is pruned and the step is not recorded at all.
        private static void Touch(Transform transform) => transform.position += Vector3.right;

        private static Material CreateMaterial(string fileName)
        {
            var shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null, "No test shader found.");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, AssetRoot + "/" + fileName);
            return material;
        }

        private static JObject Payload(string json)
        {
            var response = JObject.Parse(json);
            Assert.That(response["errorCode"], Is.Null, response.ToString(Formatting.None));
            return (JObject)response["result"];
        }
    }
}

// Producer:Betsy
