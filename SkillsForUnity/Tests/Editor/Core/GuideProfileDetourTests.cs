using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The profile gate for "carried write operations": for both entry points, whether a write happens
    /// is decided by the payload, not by their own metadata, so the metadata rules (the four covered by <see cref="SkillsSurfaceProfileTests"/>) are blind to them.
    ///
    /// <list type="bullet">
    /// <item><c>batch_execute</c> / <c>batch_retry_failed</c> execute the operation recorded in
    /// confirmToken / report, while the token-minting <c>batch_preview_*</c> are all ReadOnly — rule 1
    /// keeps them visible (rightly so, since preview is exactly what AI explanation needs under the guide
    /// profile), so under the guide profile the chain "preview for a token → execute" can actually rename, write component properties, swap materials, or delete objects.</item>
    /// <item><c>workflow_undo_task</c> / <c>redo</c> / <c>revert</c> / <c>session_undo</c> replay the
    /// write operation recorded in the task snapshot.</item>
    /// </list>
    ///
    /// Both live in the Workflow category — no profile hides it (nor should it: job_*, report_* are all in there too).
    /// noSceneAuthoring turns them off via rule 4 (MutatesScene ⇒ hidden); what guide withdraws is the
    /// category, not this flag, so only an execution-time check remains to turn them off. This is exactly the check pinned down here: guide refuses, full runs unchanged, nsa still hides them entirely.
    ///
    /// EditorPrefs hygiene: <c>UnitySkills_SurfaceProfile</c> and the operating mode are both machine-level
    /// keys shared globally per Unity version, not per project, so setup saves the original value and teardown restores it; each test sets its own profile explicitly.
    /// Workflow history and batch tokens are both redirected to a temp directory / deleted when done; tests never write into the user's real history.
    /// </summary>
    [TestFixture]
    public class GuideProfileDetourTests
    {
        private const string ProbeName = "GuideDetourProbe";

        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;
        private string _tempRoot;
        private readonly List<string> _mintedTokens = new List<string>();
        private readonly List<string> _seededReports = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedMode = SkillsModeManager.CurrentMode;
            // The profile must be set explicitly: a clean CI project defaults to full, a local dev machine may be on any profile. The mode must allow it through,
            // otherwise what blocks before the profile gate is MODE_*, and the test would not be testing the profile.
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            // Batch execution calls BeginSession (writes workflow history), and the undo/redo tests also need to push tasks into history.
            // All of it is redirected to a temp file and deleted when done — the user's real history is never touched by this file.
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsGuideDetour_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            WorkflowManager.OverrideHistoryFilePathForTests = Path.Combine(_tempRoot, "workflow_history.json");
            WorkflowFileStore.OverrideStoreRootForTests = Path.Combine(_tempRoot, "workflow_files");
            WorkflowManager.ResetStateForTests();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var token in _mintedTokens)
                BatchPersistence.RemovePreview(token);
            _mintedTokens.Clear();
            if (_seededReports.Count > 0)
            {
                // Reports have no delete entry point (real reports only age out past a 100-item cap), so we strip it directly from state and persist.
                BatchPersistence.State.reports.RemoveAll(r => _seededReports.Contains(r.reportId));
                BatchPersistence.Save();
                _seededReports.Clear();
            }

            WorkflowManager.AbortTask();
            WorkflowManager.ResetStateForTests();
            WorkflowManager.OverrideHistoryFilePathForTests = null;
            WorkflowFileStore.OverrideStoreRootForTests = null;
            try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }

            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.CurrentMode = _savedMode;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- batch: preview -> token -> execute ----------

        /// <summary>
        /// Preview stays usable under guide (it is read-only, and also what explanation needs), but the token minted from it cannot be executed,
        /// and the object really is unchanged — asserting the error code alone isn't enough: a refusal payload can appear after the write has already landed.
        /// </summary>
        [Test]
        public void GuideProfile_PreviewStaysOpen_ButExecutingItsTokenIsRefused()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var preview = SuccessPayload("batch_preview_rename", RenameArgs());
            var token = MintedToken(preview);

            var response = Envelope("batch_execute", ExecuteArgs(token));
            AssertCarriedWriteRefusal(response, SkillCategory.GameObject, "rename");

            Assert.That(GameObject.Find(ProbeName), Is.Not.Null,
                "档位报了 SURFACE_EXCLUDED，对象却还是被改名了 —— 检查跑在写之后就等于没跑。");
        }

        /// <summary>
        /// The preview must itself announce that "execute will refuse". A preview that only hands over a confirmToken would make an agent read the execution-time
        /// wall as a bug, then go look for the same write operation in another module — exactly the behavior the profile is meant to prevent.
        /// </summary>
        [Test]
        public void GuideProfile_PreviewPayload_AnnouncesThatExecuteWillRefuse()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var preview = SuccessPayload("batch_preview_rename", RenameArgs());
            MintedToken(preview);

            var notice = preview["surfaceExclusion"];
            Assert.That(notice, Is.Not.Null,
                "guide 档下的预览没有任何拒绝预告: " + preview.ToString(Formatting.None));
            Assert.That(notice["blockedBy"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"));
            Assert.That(notice["blockedSkill"]?.ToString(), Is.EqualTo("batch_execute"),
                "预告必须点名会拒的那个技能，否则 agent 不知道该停在哪一步。");
            Assert.That(notice["surfaceProfile"]?.ToString(), Is.EqualTo(SkillsSurfaceProfile.WireGuide));
            Assert.That(notice["category"]?.ToString(), Is.EqualTo(nameof(SkillCategory.GameObject)));
            Assert.That(notice["manualDoc"]?.ToString(),
                Is.EqualTo(SkillsSurfaceProfile.ManualDocFor(SkillCategory.GameObject)));
            Assert.That(notice["hint"]?.ToString(),
                Does.Contain(SkillsSurfaceProfile.ManualDocFor(SkillCategory.GameObject)),
                "hint 必须带上手册路径（只查子串，措辞会调）。");
        }

        /// <summary>
        /// Zero behavior change under full: the same chain runs through to completion, and the preview payload should not even carry the surfaceExclusion
        /// key (it's "field not added" rather than "field added as null" — the skill payload's serialization settings would otherwise write out nulls).
        /// </summary>
        [Test]
        public void FullProfile_SameChain_RunsAndPayloadCarriesNoExclusionField()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            var preview = SuccessPayload("batch_preview_rename", RenameArgs());
            Assert.That(preview.Property("surfaceExclusion"), Is.Null,
                "full 档的预览载荷多了一个键，破坏了「full 档零变化」: " + preview.ToString(Formatting.None));

            var token = MintedToken(preview);
            var response = Envelope("batch_execute", ExecuteArgs(token));
            Assert.That(response["errorCode"], Is.Null,
                "full 档下执行失败了: " + response.ToString(Formatting.None));
            Assert.That(GameObject.Find("Ren_" + ProbeName), Is.Not.Null,
                "full 档下改名应当真的落地，否则上面那条 guide 拒绝测的可能只是链本身不通。");
        }

        /// <summary>
        /// Refusal does not consume the token: once the user switches the profile back to full, the same token should work immediately, without another preview run.
        /// This is also a behavioral assertion that "the check happens before RemovePreview".
        /// </summary>
        [Test]
        public void RefusedToken_SurvivesTheRefusal_AndRunsOnceProfileGoesBackToFull()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var token = MintedToken(SuccessPayload("batch_preview_rename", RenameArgs()));
            AssertCarriedWriteRefusal(Envelope("batch_execute", ExecuteArgs(token)),
                SkillCategory.GameObject, "rename");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var response = Envelope("batch_execute", ExecuteArgs(token));

            Assert.That(response["errorCode"], Is.Null,
                "被拒的令牌在档位放开后失效了 —— 说明拒绝路径把它消费掉了: " +
                response.ToString(Formatting.None));
            Assert.That(GameObject.Find("Ren_" + ProbeName), Is.Not.Null);
        }

        /// <summary>
        /// The kind -> category mapping must be driven by what the operation actually writes, because the category decides which manual gets handed to the agent.
        /// Observed through the preview's public notice, without touching private methods.
        /// </summary>
        [Test]
        public void GuideProfile_SetPropertyKind_IsClassifiedAsComponentWrite()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var args = new JObject
            {
                ["queryJson"] = ProbeQuery(),
                ["componentType"] = "Transform",
                ["propertyName"] = "localPosition",
                ["value"] = "1,2,3",
            }.ToString(Formatting.None);

            var preview = SuccessPayload("batch_preview_set_property", args);
            var token = MintedToken(preview);

            Assert.That(preview["surfaceExclusion"]?["category"]?.ToString(),
                Is.EqualTo(nameof(SkillCategory.Component)),
                "写组件属性应按 Component 分类（manual-component 才是能教这一步的手册）。");

            var response = Envelope("batch_execute", ExecuteArgs(token));
            AssertCarriedWriteRefusal(response, SkillCategory.Component, "set_property");
            Assert.That(GameObject.Find(ProbeName).transform.localPosition, Is.EqualTo(Vector3.zero),
                "属性被写进去了。");
        }

        /// <summary>
        /// The second batch entry point: <c>batch_retry_failed</c> goes through the same batch executor, just via a reportId instead of a token,
        /// so the same question must be asked of the kind recorded in the report.
        /// </summary>
        [Test]
        public void GuideProfile_BatchRetryFailed_IsRefusedForAWithdrawnKind()
        {
            var reportId = SeedFailedRenameReport();

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var refused = Envelope("batch_retry_failed",
                new JObject { ["reportId"] = reportId, ["runAsync"] = false }.ToString(Formatting.None));
            AssertCarriedWriteRefusal(refused, SkillCategory.GameObject, "rename");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var allowed = Envelope("batch_retry_failed",
                new JObject { ["reportId"] = reportId, ["runAsync"] = false }.ToString(Formatting.None));
            Assert.That(allowed["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                "full 档不该拦任何东西: " + allowed.ToString(Formatting.None));
        }

        /// <summary>
        /// The existing shutoff for noSceneAuthoring must not regress: that profile hides <c>batch_execute</c> entirely via rule 4,
        /// so it should not even appear in the directory, and the refusal should come from the routing gate (payload nested under details) rather than the execution-time check.
        /// </summary>
        [Test]
        public void NoSceneAuthoring_StillHidesBatchExecuteEntirely()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;

            Assert.That(BriefSkillNames(), Does.Not.Contain("batch_execute"),
                "nsa 档的目录里仍然列着 batch_execute —— 规则 4 的既有关闭退化了。");

            var response = Envelope("batch_execute", ExecuteArgs("no_such_token_at_all"));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"));
            Assert.That(response["details"]?["surfaceProfile"]?.ToString(),
                Is.EqualTo(SkillsSurfaceProfile.WireNoSceneAuthoring),
                "nsa 档下这个技能应当在闸门就被拦住（载荷带 details），而不是走到执行期检查 —— " +
                "后者意味着它已经进了方法体，令牌若有效就只差一步。");
        }

        // ---------- workflow: snapshot replay ----------

        /// <summary>
        /// All four replay entry points must refuse a task containing a scene object snapshot. Name each one individually rather than testing just one:
        /// <c>workflow_revert_task</c> is a forwarding alias, and if it were allowed to reuse the forwarding target's name for the refusal, the agent would read it as
        /// "the alias is fine, the target is the problem" — which invites a retry.
        /// </summary>
        [Test]
        public void GuideProfile_EverySnapshotReplayEntryPoint_RefusesASceneTask()
        {
            var probe = CreateProbe();

            var undoTaskId = SeedTask(SceneSnapshot(probe));
            var revertTaskId = SeedTask(SceneSnapshot(probe));
            var sessionId = "guide-detour-session";
            SeedTask(new[] { SceneSnapshot(probe) }, sessionId);
            var redoTaskId = SeedUndoneTask(SceneSnapshot(probe));

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            AssertCarriedWriteRefusal(
                Envelope("workflow_undo_task", new JObject { ["taskId"] = undoTaskId }.ToString(Formatting.None)),
                SkillCategory.GameObject, "scene_object_snapshot");
            AssertCarriedWriteRefusal(
                Envelope("workflow_revert_task", new JObject { ["taskId"] = revertTaskId }.ToString(Formatting.None)),
                SkillCategory.GameObject, "scene_object_snapshot");
            AssertCarriedWriteRefusal(
                Envelope("workflow_session_undo", new JObject { ["sessionId"] = sessionId }.ToString(Formatting.None)),
                SkillCategory.GameObject, "scene_object_snapshot");
            AssertCarriedWriteRefusal(
                Envelope("workflow_redo_task", new JObject { ["taskId"] = redoTaskId }.ToString(Formatting.None)),
                SkillCategory.GameObject, "scene_object_snapshot");

            Assert.That(WorkflowManager.History.tasks.Any(t => t.id == undoTaskId), Is.True,
                "被拒的任务不该从历史里消失 —— 档位调回 full 后它必须还能撤销。");
        }

        /// <summary>
        /// The other half of precision: guide only withdraws GameObject / Component / Material / Scene / Sample; writes to scripts and
        /// ordinary assets are work it leaves to the AI. So tasks that only touch this class of asset must still be undoable — blanket-hiding
        /// these skills would also strip the safety net from the writes guide still allows.
        /// </summary>
        [Test]
        public void GuideProfile_AssetOnlyTask_IsStillUndoable()
        {
            var taskId = SeedTask(AssetSnapshot("Assets/__guide_detour_absent__/Probe.asset"));
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var response = Envelope("workflow_undo_task",
                new JObject { ["taskId"] = taskId }.ToString(Formatting.None));

            // The snapshot points at a nonexistent asset, so the undo itself will fail; the only thing this test cares about is that it wasn't blocked by the profile.
            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                "只含资源快照的任务被 guide 档拦了 —— 撤销脚本/资源改动是 guide 档仍然允许的操作。");
        }

        /// <summary>
        /// Asset snapshots are classified by extension, only for the kinds the profile actually withdraws: <c>.mat</c> is material authoring.
        /// This test is paired with the previous one — without it, "asset snapshots are allowed" could pass by "everything is allowed".
        /// </summary>
        [Test]
        public void GuideProfile_MaterialAssetTask_IsRefused()
        {
            var taskId = SeedTask(AssetSnapshot("Assets/__guide_detour_absent__/Probe.mat"));
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            AssertCarriedWriteRefusal(
                Envelope("workflow_undo_task", new JObject { ["taskId"] = taskId }.ToString(Formatting.None)),
                SkillCategory.Material, "material_asset_snapshot");
        }

        /// <summary>
        /// Editor/project setting snapshots are not scene authoring and should not be caught by this check — they don't even have an assetPath,
        /// which is exactly the class the "empty assetPath => scene object" criterion is most prone to misjudging.
        /// </summary>
        [Test]
        public void GuideProfile_SettingSnapshotTask_IsNotMistakenForSceneAuthoring()
        {
            var taskId = SeedTask(new ObjectSnapshot
            {
                globalObjectId = "GlobalObjectId_V1-0-00000000000000000000000000000000-0-0",
                objectName = "EditorSetting",
                typeName = "PlayerSettings",
                type = SnapshotType.Setting,
                settingKey = "guide-detour-probe",
            });

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var response = Envelope("workflow_undo_task",
                new JObject { ["taskId"] = taskId }.ToString(Formatting.None));

            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                "设置快照被当成场景对象拦下了 —— 判据要先排除 Setting 类型。");
        }

        /// <summary>
        /// An unknown taskId must still report "not found", and must not be preempted by this check into SURFACE_EXCLUDED:
        /// hiding a mistyped id behind the policy wall would make an agent go change the profile instead of fixing the argument.
        /// </summary>
        [Test]
        public void GuideProfile_UnknownTaskId_StillReportsNotFound()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var response = Envelope("workflow_undo_task",
                new JObject { ["taskId"] = "no_such_task_id" }.ToString(Formatting.None));

            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                "不存在的任务不该报成被档位收回。");
        }

        // ---------- the refusal payload itself ----------

        /// <summary>
        /// The refusal message must not be reclassified by <see cref="SkillErrorClassifier"/>. Beyond the skill's self-reported errorCode,
        /// if suggestedFixes isn't self-reported, the classifier fills it in from the message text — words like "missing" / "not found" /
        /// "invalid" appearing in the message would hand the agent a "supply a missing argument and retry" suggestion, exactly the opposite of what the refusal means to convey.
        /// This is also why the operation identity (kind / snapshot type) goes through a field rather than being inserted into the message text: kind already has
        /// <c>fix_missing_scripts</c>。
        /// </summary>
        [Test]
        public void RejectionPayload_IsNotReclassifiedByMessageText()
        {
            CreateProbe();
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var token = MintedToken(SuccessPayload("batch_preview_rename", RenameArgs()));
            var response = Envelope("batch_execute", ExecuteArgs(token));

            var message = response["error"]?.ToString();
            Assert.That(message, Is.Not.Null.And.Not.Empty);

            var classification = SkillErrorClassifier.Classify(message);
            Assert.That(classification.Code, Is.EqualTo(SkillErrorCode.SkillError),
                $"拒绝文案被分类器认成 {classification.Code}，它会顺带塞进不相干的 suggestedFixes。" +
                $"文案: {message}");
            Assert.That(response["suggestedFixes"], Is.Null,
                "档位拒绝没有可执行的修复动作（唯一出路是用户改设置），不该带 suggestedFixes: " +
                response.ToString(Formatting.None));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.Abort));
        }

        // ---------- helpers ----------

        private static GameObject CreateProbe()
        {
            var probe = new GameObject(ProbeName);
            GameObjectFinder.InvalidateCache();
            return probe;
        }

        private static string ProbeQuery() =>
            new JObject { ["name"] = ProbeName }.ToString(Formatting.None);

        private static string RenameArgs() => new JObject
        {
            ["queryJson"] = ProbeQuery(),
            ["mode"] = "prefix",
            ["prefix"] = "Ren_",
        }.ToString(Formatting.None);

        /// <summary>runAsync=false lets the job spin to completion within the call, skipping the wait for EditorApplication.update.</summary>
        private static string ExecuteArgs(string token) => new JObject
        {
            ["confirmToken"] = token,
            ["runAsync"] = false,
        }.ToString(Formatting.None);

        private static JObject Envelope(string skill, string args) =>
            JObject.Parse(SkillRouter.Execute(skill, args));

        /// <summary>A skill's own payload sits under result in the success envelope, not at the top level.</summary>
        private static JObject SuccessPayload(string skill, string args)
        {
            var response = Envelope(skill, args);
            Assert.That(response["errorCode"], Is.Null,
                $"{skill} 失败了: {response.ToString(Formatting.None)}");

            var payload = response["result"] as JObject;
            Assert.That(payload, Is.Not.Null,
                "成功信封的形状变了 —— 期望技能载荷在 result 下。顶层键: " +
                string.Join(", ", response.Properties().Select(p => p.Name)));
            return payload;
        }

        private string MintedToken(JObject preview)
        {
            var token = preview["confirmToken"]?.ToString();
            Assert.That(token, Is.Not.Null.And.Not.Empty,
                "预览没有铸出 confirmToken: " + preview.ToString(Formatting.None));
            Assert.That(preview["executableCount"]?.Value<int>() ?? 0, Is.GreaterThan(0),
                "前置条件：预览必须匹配到可执行项，否则 batch_execute 会因空预览而拒，测的就不是档位了。");

            _mintedTokens.Add(token);
            return token;
        }

        /// <summary>
        /// Structural assertion for an execution-time refusal. Fields are flattened at the top level rather than nested under details — the router's
        /// pass-through for skill errors forwards a skill's self-reported unknown fields as-is, but drops its self-reported details; this assertion pins down that constraint too.
        /// </summary>
        private static void AssertCarriedWriteRefusal(JObject response, SkillCategory category, string operation)
        {
            var dump = response.ToString(Formatting.None);
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"), dump);
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.Abort), dump);
            Assert.That(response["surfaceProfile"]?.ToString(), Is.EqualTo(SkillsSurfaceProfile.CurrentWire), dump);
            Assert.That(response["category"]?.ToString(), Is.EqualTo(category.ToString()), dump);
            Assert.That(response["operation"]?.ToString(), Is.EqualTo(operation),
                "载荷必须点明是哪个操作被收回了 —— 文案里刻意不插它，字段是唯一的出口。" + dump);
            Assert.That(response["userControlled"]?.Value<bool>(), Is.True,
                "必须明说这是用户的设置，否则 agent 会当成 bug 反复重试。" + dump);
            Assert.That(response["manualDoc"]?.ToString(),
                Is.EqualTo(SkillsSurfaceProfile.ManualDocFor(category)), dump);
            Assert.That(response["hint"]?.ToString(), Is.Not.Null.And.Not.Empty, dump);
        }

        private static string[] BriefSkillNames()
        {
            var modules = (JObject)JObject.Parse(SkillRouter.GetBrief())["modules"];
            return modules.Properties()
                .SelectMany(p => ((JArray)p.Value).Select(n => n.ToString()))
                .ToArray();
        }

        // ---------- fixture data ----------

        /// <summary>
        /// Scene object snapshot: an empty assetPath is exactly what WorkflowManager records for a scene object
        /// (AssetDatabase.GetAssetPath returns an empty string for a scene object).
        /// </summary>
        private static ObjectSnapshot SceneSnapshot(GameObject target) => new ObjectSnapshot
        {
            globalObjectId = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(target).ToString(),
            objectName = target.name,
            typeName = nameof(GameObject),
            type = SnapshotType.Modified,
            originalJson = UnityEditor.EditorJsonUtility.ToJson(target),
            objectReferencesCaptured = true,
        };

        /// <summary>
        /// Asset snapshot. Deliberately omits fileHash / base64 with a path that does not exist: RestoreModifiedSnapshot returns false outright when it
        /// can't resolve the object and has no backup bytes, so the allow-path test never actually touches any file.
        /// </summary>
        private static ObjectSnapshot AssetSnapshot(string assetPath) => new ObjectSnapshot
        {
            globalObjectId = "GlobalObjectId_V1-1-00000000000000000000000000000000-0-0",
            objectName = Path.GetFileNameWithoutExtension(assetPath),
            typeName = "Object",
            type = SnapshotType.Modified,
            assetPath = assetPath,
            objectReferencesCaptured = true,
        };

        private static string SeedTask(params ObjectSnapshot[] snapshots) => SeedTask(snapshots, null);

        /// <summary>
        /// Push a task directly into history. Going through the real skill entry point plus a hand-built snapshot is more controllable than really
        /// making a change and then undoing it: this file tests "does replay get blocked", not replay itself, which is already covered by WorkflowPersistenceTests.
        /// The history file is already redirected to a temp directory in setup.
        /// </summary>
        private static string SeedTask(IEnumerable<ObjectSnapshot> snapshots, string sessionId)
        {
            var task = NewTask(snapshots, sessionId);
            WorkflowManager.History.tasks.Add(task);
            return task.id;
        }

        private static string SeedUndoneTask(params ObjectSnapshot[] snapshots)
        {
            var task = NewTask(snapshots, null);
            WorkflowManager.GetUndoneStack().Add(task);
            return task.id;
        }

        private static WorkflowTask NewTask(IEnumerable<ObjectSnapshot> snapshots, string sessionId) => new WorkflowTask
        {
            id = "guide_detour_" + Guid.NewGuid().ToString("N").Substring(0, 8),
            tag = "guide-detour-tests",
            description = "seeded by GuideProfileDetourTests",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            sessionId = sessionId,
            snapshots = snapshots.ToList(),
        };

        /// <summary>
        /// A rename report containing only a failed item, for batch_retry_failed to use. operation is non-empty,
        /// so CanRetryFromReport lets it through to execution time, where the profile check is the first gate on that chain.
        /// </summary>
        private string SeedFailedRenameReport()
        {
            var report = new BatchReportRecord
            {
                reportId = "gd" + Guid.NewGuid().ToString("N").Substring(0, 6),
                kind = "rename",
                status = "completed",
                createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                operation = new Dictionary<string, object> { ["mode"] = "prefix", ["prefix"] = "Ren_" },
            };
            report.items.Add(new BatchReportItemRecord
            {
                targetName = ProbeName,
                action = "rename",
                status = "failed",
                reason = "seeded",
            });

            BatchPersistence.UpsertReport(report);
            _seededReports.Add(report.reportId);
            return report.reportId;
        }
    }
}

// Producer:Betsy
