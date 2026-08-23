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
    /// 档位的「携带式写操作」关口：两个入口的写操作由载荷而不是由自身元数据决定，所以元数据规则
    /// （<see cref="SkillsSurfaceProfileTests"/> 覆盖的四条）对它们是瞎的。
    ///
    /// <list type="bullet">
    /// <item><c>batch_execute</c> / <c>batch_retry_failed</c> 执行的是 confirmToken / report 里
    /// 记着的操作，而铸令牌的 <c>batch_preview_*</c> 全是 ReadOnly —— 规则 1 保它们可见（应该的，
    /// 预览正是 guide 档下 AI 讲解所需），于是 guide 档下「预览拿令牌 → 执行」这条链能真的改名、
    /// 写组件属性、换材质、删对象。</item>
    /// <item><c>workflow_undo_task</c> / <c>redo</c> / <c>revert</c> / <c>session_undo</c> 重放的是
    /// 任务快照里记着的写操作。</item>
    /// </list>
    ///
    /// 两者都住在 Workflow 分类 —— 没有任何档位隐藏它（也不该：job_*、report_* 都在里面）。
    /// noSceneAuthoring 靠规则 4（MutatesScene ⇒ 隐藏）关掉它们，guide 收回的是分类而不是这个
    /// 标志，所以只剩执行期检查能关。这里钉的就是那层检查：guide 拒、full 照跑、nsa 仍然整只隐藏。
    ///
    /// EditorPrefs 卫生：<c>UnitySkills_SurfaceProfile</c> 与操作模式都是按 Unity 版本全局共享、
    /// 不分工程的机器级键，所以 setup 存原值、teardown 还原，每条测试自己显式设档。
    /// 工作流历史与批处理令牌都改指临时目录 / 用完即删，测试不往用户真实历史里写东西。
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
            // 档位必须显式设：干净 CI 工程上默认 full，本地开发机上可能任意档。模式必须放行，
            // 否则拦在档位闸门之前的是 MODE_*，测出来的不是档位。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            // 批处理执行会 BeginSession（写工作流历史），undo/redo 测试还要往历史里塞任务。
            // 全部改指临时文件，跑完删掉 —— 用户的真实历史不参与本文件。
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
                // 报告没有删除入口（真实报告只按 100 条上限淘汰），所以直接从状态里摘掉再落盘。
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

        // ---------- batch: 预览 → 令牌 → 执行 ----------

        /// <summary>
        /// guide 档下预览仍然可用（它是只读的，也是讲解所需），但用它铸出的令牌执行不了，
        /// 而且对象真的没被改 —— 只断言错误码不够：拒绝载荷可以出现在写已经落地之后。
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
        /// 预览必须自己说出「execute 会拒」。只给一个 confirmToken 的预览会让 agent 把执行期的墙
        /// 读成 bug，然后去别的模块找同样的写操作 —— 那正是档位要避免的行为。
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
        /// full 档零行为变化：同一条链照跑到底，而且预览载荷里连 surfaceExclusion 这个键都不该出现
        /// （是「不加字段」而不是「加个 null」—— 技能载荷的序列化设置会把 null 写出来）。
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
        /// 拒绝不吃掉令牌：用户把档位调回 full 之后，同一个令牌应当直接可用，不必再跑一轮预览。
        /// 这条也是「检查发生在 RemovePreview 之前」的行为化断言。
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
        /// kind → 分类的映射必须按操作真正写的东西给，因为分类决定了递给 agent 的是哪本手册。
        /// 通过预览的公开预告观察，不碰私有方法。
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
        /// 第二个批处理入口：<c>batch_retry_failed</c> 走的是同一批执行器，只是经 reportId 而非令牌，
        /// 所以同一个问题必须问 report 里记的 kind。
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
        /// noSceneAuthoring 的既有关闭不能回退：那一档靠规则 4 把 <c>batch_execute</c> 整只隐藏，
        /// 所以它连目录里都不该出现，拒绝也应当来自路由闸门（载荷嵌在 details 下）而不是执行期检查。
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

        // ---------- workflow: 快照重放 ----------

        /// <summary>
        /// 四个重放入口都必须拒绝含场景对象快照的任务。逐个点名而不是只测一个：
        /// <c>workflow_revert_task</c> 是转发别名，若让它复用被转发方的名字做拒绝，agent 读到的是
        /// 「别名没问题、目标有问题」，那是在邀请它重试。
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
        /// 精度的另一半：guide 档只收回 GameObject / Component / Material / Scene / Sample，
        /// 脚本和普通资源的写是它留给 AI 的活。因此只碰这类资源的任务必须仍然能撤销 —— 一刀切隐藏
        /// 这几个技能，等于把 guide 档仍然允许的那批写操作的安全网也一起拿掉。
        /// </summary>
        [Test]
        public void GuideProfile_AssetOnlyTask_IsStillUndoable()
        {
            var taskId = SeedTask(AssetSnapshot("Assets/__guide_detour_absent__/Probe.asset"));
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var response = Envelope("workflow_undo_task",
                new JObject { ["taskId"] = taskId }.ToString(Formatting.None));

            // 快照指向不存在的资源，所以撤销本身会失败；这里唯一在意的是它不是被档位拦下的。
            Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                "只含资源快照的任务被 guide 档拦了 —— 撤销脚本/资源改动是 guide 档仍然允许的操作。");
        }

        /// <summary>
        /// 资源快照按扩展名分类，只对档位真的收回的那几种：<c>.mat</c> 是材质创作。
        /// 这条与上一条成对 —— 少了它，「资源快照放行」可以靠「什么都放行」通过。
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
        /// 编辑器/工程设置快照不是场景创作，不该被这层检查误伤 —— 它们连 assetPath 都没有，
        /// 正是「空 assetPath ⇒ 场景对象」这个判据最容易误判的一类。
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
        /// 未知 taskId 必须仍然回「找不到」，不能被这层检查抢答成 SURFACE_EXCLUDED：
        /// 把打错的 id 藏在政策墙后面，agent 会去改档位而不是改参数。
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

        // ---------- 拒绝载荷本身 ----------

        /// <summary>
        /// 拒绝文案不能被 <see cref="SkillErrorClassifier"/> 二次归类。技能自报的 errorCode 之外，
        /// suggestedFixes 若没自报就由分类器按消息文本填 —— 消息里出现 "missing" / "not found" /
        /// "invalid" 这类词，会给 agent 递上「补个参数再重试」的建议，正好和拒绝要传达的相反。
        /// 这也是操作标识（kind / 快照类型）走字段而不是插进文案的原因：kind 里就有
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

        /// <summary>runAsync=false 让作业在调用内自旋推进完成，省掉等 EditorApplication.update。</summary>
        private static string ExecuteArgs(string token) => new JObject
        {
            ["confirmToken"] = token,
            ["runAsync"] = false,
        }.ToString(Formatting.None);

        private static JObject Envelope(string skill, string args) =>
            JObject.Parse(SkillRouter.Execute(skill, args));

        /// <summary>技能自己的载荷在成功信封的 result 下，不在顶层。</summary>
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
        /// 执行期拒绝的结构断言。字段平铺在顶层而不是嵌在 details 下 —— 路由的技能错误直通会原样
        /// 转发技能自报的未知字段，却会丢掉技能自报的 details；这条断言同时钉住那个约束。
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

        // ---------- 夹具数据 ----------

        /// <summary>
        /// 场景对象快照：assetPath 为空正是 WorkflowManager 记录场景对象时的样子
        /// （AssetDatabase.GetAssetPath 对场景对象返回空串）。
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
        /// 资源快照。刻意不给 fileHash / base64 且路径不存在：RestoreModifiedSnapshot 解析不到对象
        /// 又没有备份字节时直接返回 false，所以放行分支的测试不会真的动任何文件。
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
        /// 直接往历史里塞任务。走真技能入口 + 造好的快照，比先真改再撤销更可控：本文件测的是
        /// 「重放会不会被拦」，而不是重放本身，后者已有 WorkflowPersistenceTests 覆盖。
        /// 历史文件在 setup 里已改指临时目录。
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
        /// 一份只含失败项的 rename 报告，供 batch_retry_failed 使用。operation 非空，
        /// 所以 CanRetryFromReport 会放行到执行期，档位检查才是那条链上第一道关。
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
