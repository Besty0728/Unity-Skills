using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// SurfaceProfile 三档的可见性与拦截语义。
    ///
    /// 两条纪律贯穿全文件：
    /// 1. 一切计数在运行时推导，绝不写死数字 —— 注册数随安装的可选包变化，干净 CI 工程上
    ///    guide/noSceneAuthoring 的隐藏条数和本地开发机上不是同一个值。推导走
    ///    <see cref="IsExpectedHidden"/>，那是对四条排除规则的测试侧独立重述，故意不调
    ///    <see cref="SkillsSurfaceProfile.IsExcluded(SkillRouter.SkillInfo)"/>：与被测实现同源
    ///    会变成同义反复，某条规则消失时期望与实际一起变小，断言照样通过。
    /// 2. 只断言结构字段（errorCode / details.manualDoc / details.surfaceProfile / retryStrategy /
    ///    authorization.blockedBy）和文档路径这类关键子串。hint 是给 agent 读的自然语言，措辞会调。
    ///
    /// EditorPrefs 卫生：<c>UnitySkills_SurfaceProfile</c> 按 Unity 版本全局共享、不分工程，所以
    /// setup 保存原值、teardown 还原，每个测试自己显式设置所需档位与模式。
    /// </summary>
    [TestFixture]
    public class SkillsSurfaceProfileTests
    {
        /// <summary>
        /// 拦截探针用的定位符：注册表里保证不存在这个名字的对象/场景/资源。选它是为了在
        /// 「档位没拦住」这个失败分支里，技能顶多回一个 NOT_FOUND，而不是真的改了工程。
        /// </summary>
        private const string AbsentTarget = "__unity_skills_surface_probe_absent__";

        /// <summary>
        /// 逃生口名单的测试侧独立副本 —— 刻意不引用实现里的 <c>_alwaysHiddenSkillNames</c>。
        /// 引用它就等于让期望值跟着实现走，名单被清空时两边一起变空。
        /// </summary>
        private static readonly string[] AlwaysHiddenSkillNames = { "editor_execute_menu" };

        /// <summary>
        /// 五个 guide 档隐藏分类各一个写技能探针。每个都刻意选了
        /// <c>SkillPlanningService</c> 里没有语义 planner 的技能 —— 有 planner 的（gameobject_create、
        /// component_add、material_create、scene_save…）会在语义校验阶段就因目标不存在返回
        /// SEMANTIC_INVALID，那比档位闸门更早，测出来的就不是档位了。
        /// </summary>
        private static readonly (SkillCategory category, string skill, string args)[] WriteProbes =
        {
            (SkillCategory.GameObject, "gameobject_set_active",
                "{\"name\":\"" + AbsentTarget + "\",\"active\":true}"),
            (SkillCategory.Component, "component_set_enabled",
                "{\"name\":\"" + AbsentTarget + "\",\"componentType\":\"BoxCollider\",\"enabled\":true}"),
            (SkillCategory.Material, "material_set_color",
                "{\"path\":\"Assets/" + AbsentTarget + ".mat\",\"r\":1,\"g\":0,\"b\":0,\"a\":1}"),
            (SkillCategory.Scene, "scene_unload",
                "{\"sceneName\":\"" + AbsentTarget + "\"}"),
            (SkillCategory.Sample, "set_object_position",
                "{\"objectName\":\"" + AbsentTarget + "\",\"x\":0,\"y\":0,\"z\":0}"),
        };

        /// <summary>同五个分类里的只读技能 —— 档位拿掉的是动手能力，不是看的能力。</summary>
        private static readonly (SkillCategory category, string skill, string args)[] ReadProbes =
        {
            (SkillCategory.GameObject, "gameobject_find", "{\"name\":\"" + AbsentTarget + "\"}"),
            (SkillCategory.Component, "component_list", "{\"name\":\"" + AbsentTarget + "\"}"),
            (SkillCategory.Material, "material_get_properties",
                "{\"path\":\"Assets/" + AbsentTarget + ".mat\"}"),
            (SkillCategory.Scene, "scene_get_info", "{}"),
            (SkillCategory.Sample, "find_objects_by_name", "{\"name\":\"" + AbsentTarget + "\"}"),
        };

        private SurfaceProfileKind _savedProfile;
        private SkillsOperatingMode _savedMode;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedMode = SkillsModeManager.CurrentMode;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            // 不假设当前是 Bypass：干净 CI 工程上默认是 Auto。需要放行的测试自己设。
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.CurrentMode = _savedMode;
            GameObjectFinder.InvalidateCache();
        }

        // ---------- 可见集推导 ----------

        [TestCase(SurfaceProfileKind.Full)]
        [TestCase(SurfaceProfileKind.Guide)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring)]
        public void BriefVisibleCount_EqualsRegistryMinusDerivedHiddenSet(SurfaceProfileKind profile)
        {
            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered();

            // 期望值走测试侧独立推述的四条规则（见 IsExpectedHidden）。不调权威重载，
            // 否则规则被删时期望与实际一起变小，等式照样成立。
            var hiddenSkills = registry.Where(s => IsExpectedHidden(s, profile)).ToArray();
            int expectedVisible = registry.Length - hiddenSkills.Length;

            SkillsSurfaceProfile.Current = profile;
            var brief = JObject.Parse(SkillRouter.GetBrief());

            Assert.That(brief["totalSkills"]?.Value<int>(), Is.EqualTo(expectedVisible),
                $"{profile} 档的 brief 可见数应为「全集 {registry.Length} − 推导隐藏 {hiddenSkills.Length}」。");

            var listedNames = ((JObject)brief["modules"]).Properties()
                .SelectMany(p => ((JArray)p.Value).Select(n => n.ToString()))
                .ToArray();
            Assert.That(listedNames.Length, Is.EqualTo(expectedVisible),
                "totalSkills 与真正列出来的名字数必须一致。");

            var leaked = hiddenSkills
                .Select(s => s.Name)
                .Intersect(listedNames, StringComparer.Ordinal)
                .ToArray();
            Assert.That(leaked, Is.Empty,
                $"{profile} 档的目录里泄漏了被隐藏的写技能: {string.Join(", ", leaked.Take(10))}");
        }

        /// <summary>
        /// 规则 2（逃生口按名字隐藏）的具体后果守卫。计数等式只说「总数对得上」，这条盯的是
        /// <c>editor_execute_menu</c> 这一个名字确实从目录里消失、且硬调会被拦。
        ///
        /// 它是"万能钥匙"：菜单项能触达档位想收回的一切写操作，而它的分类（Editor）不在也不该在
        /// 任何隐藏集里。所以没有这条规则，其余每一条排除都只是装饰 —— 被 gameobject_create 拦住的
        /// agent 可以转头执行 "GameObject/Create Empty"。
        /// </summary>
        [TestCase(SurfaceProfileKind.Guide)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring)]
        public void EscapeHatchSkill_IsHiddenUnderEveryNonFullProfile(SurfaceProfileKind profile)
        {
            const string escapeHatch = "editor_execute_menu";
            Assume.That(SkillRouter.HasSkill(escapeHatch), Is.True, $"{escapeHatch} 未注册。");

            Assert.That(SkillsSurfaceProfile.IsAlwaysHiddenSkill(escapeHatch), Is.True,
                $"{escapeHatch} 应登记在 _alwaysHiddenSkillNames 里。");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            Assert.That(BriefSkillNames(), Does.Contain(escapeHatch),
                "前置条件：full 档下这个技能本该可见，否则下面的消失不能说明任何事。");

            SkillsSurfaceProfile.Current = profile;
            Assert.That(BriefSkillNames(), Does.Not.Contain(escapeHatch),
                $"{profile} 档的目录仍列出了 {escapeHatch} —— 逃生口没被堵上。");

            // 菜单路径刻意指向不存在的项：闸门若失效，最坏也只是 ExecuteMenuItem 找不到目标，
            // 而不是真的替用户点了一个菜单。
            var response = JObject.Parse(SkillRouter.Execute(escapeHatch,
                "{\"menuPath\":\"__UnitySkills/NoSuchMenuItemForTests\"}"));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                $"{profile} 档下 {escapeHatch} 应被拦住，实收: {response.ToString(Newtonsoft.Json.Formatting.None)}");

            // 逃生口的分类是 Editor，没有也不该有 manual-* 文档：手册教的是「怎么手动建 GameObject」，
            // 而这里被收回的是「执行任意菜单项」，没有一份文档能对应。所以 manualDoc 必须是 null
            // 而不是硬塞一个不相干的路径 —— 指错文档比不给文档更糟。
            var details = response["details"];
            Assert.That(details?["manualDoc"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                $"{escapeHatch} 的拒绝载荷不该给出 manual 文档（分类 Editor 没有对应手册）。");
            Assert.That(details?["hint"]?.ToString(), Is.Not.Null.And.Not.Empty,
                "没有文档可指时，hint 仍必须说明该怎么办（让用户改档位），不能留空。");
        }

        /// <summary>
        /// smoke 探测的档位保护。<c>test_smoke_skills</c> 直接 <c>Method.Invoke</c> 被探测的技能，
        /// 完全绕开 Execute，所以也绕开档位闸门 —— 它取的是过滤后快照这件事本身就是承重的安全边界。
        /// 若有人把它改回 Unfiltered，这个只读探测就会变成一次批量执行，跑的正好是用户设档位要
        /// 收回的那批写技能。这条断言守住那个边界。
        /// </summary>
        [TestCase(SurfaceProfileKind.Guide)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring)]
        public void SmokeProbe_NeverReportsSkillsHiddenByCurrentProfile(SurfaceProfileKind profile)
        {
            Assume.That(SkillRouter.HasSkill("test_smoke_skills"), Is.True, "test_smoke_skills 未注册。");

            // gameobject_ 前缀刻意选在一个两档都会隐藏的分类上，且把探测面缩到十几个技能：
            // runAsync=false 会对每个入选技能跑一次 dryRun，全量跑没必要也更慢。
            const string prefix = "gameobject_";
            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            Assume.That(registry, Is.Not.Empty, $"注册表里没有 {prefix}* 技能。");

            var expectedHidden = registry.Where(s => IsExpectedHidden(s, profile))
                .Select(s => s.Name).ToArray();
            var expectedVisible = registry.Where(s => !IsExpectedHidden(s, profile))
                .Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.That(expectedHidden, Is.Not.Empty,
                $"{profile} 档没有隐藏任何 {prefix}* 技能，这条测试会是空的。");
            Assert.That(expectedVisible, Is.Not.Empty,
                $"{prefix}* 全部被隐藏，无法区分「过滤生效」和「探测本身返回空」。");

            SkillsSurfaceProfile.Current = profile;

            // executeReadOnly=false → 全部走 dryRun，不真的调用任何技能。
            // runAsync=false → 同步返回带 results 的名单；默认的 runAsync=true 只返回 jobId，
            // 那样什么名字都读不到（第一版就是这么写的，被自己的非空守卫拦下了）。
            var response = JObject.Parse(SkillRouter.Execute("test_smoke_skills",
                "{\"nameContains\":\"" + prefix + "\",\"executeReadOnly\":false," +
                "\"includeMutating\":true,\"runAsync\":false}"));

            Assume.That(response["errorCode"], Is.Null,
                "test_smoke_skills 在此宿主上不可用: " + response["errorCode"]);

            // 成功响应把技能自己的载荷包在 result 下（BuildSuccessEnvelope），不是平铺在顶层。
            var resultArray = response["result"]?["results"] as JArray;
            Assert.That(resultArray, Is.Not.Null,
                "取不到 result.results 数组 —— 成功信封的形状变了。顶层键: " +
                string.Join(", ", response.Properties().Select(p => p.Name)));

            var reported = resultArray
                .Select(r => r["skill"]?.ToString())
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.That(reported, Is.Not.Empty, "smoke 结果里没解析到任何技能名，断言会是空的。");

            var leaked = reported.Intersect(expectedHidden, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.That(leaked, Is.Empty,
                $"{profile} 档下 smoke 探测报出了被隐藏的技能: {string.Join(", ", leaked.Take(10))}。" +
                "BuildSmokeRequest 必须用 GetAllSkillsSnapshot（已过滤）—— 它直接 Method.Invoke，" +
                "绕开档位闸门，改成 Unfiltered 就会把只读探测变成对这些写技能的批量执行。");

            // 正面钉住：可见的那批必须一个不少地出现，否则「没泄漏」也可能只是探测整体返回空。
            Assert.That(reported, Is.EqualTo(expectedVisible),
                $"{profile} 档下 {prefix}* 的探测名单应与推导的可见集完全一致。");
        }

        /// <summary>
        /// 规则 4 的独立守卫：一个自称 MutatesScene 的写技能就是场景创作，无论它住在哪个模块。
        /// 这条规则才是关掉 Netcode / Behavior 这些不在分类清单里的模块的东西，而分类清单本身
        /// 永远列不全 —— 所以这里从元数据推导，不看清单。
        /// </summary>
        [Test]
        public void NoSceneAuthoring_HidesEveryWriteDeclaringMutatesScene()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
            var visible = BriefSkillNames();

            var survivors = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => !s.ReadOnly && s.MutatesScene)
                .Select(s => s.Name)
                .Intersect(visible, StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(survivors, Is.Empty,
                "这些写技能声明了 MutatesScene 却在 noSceneAuthoring 档下仍然可见 —— " +
                $"一个叫「不碰场景」的档位放它们过去是自相矛盾: {string.Join(", ", survivors.Take(15))}");
        }

        [Test]
        public void NoSceneAuthoring_HidesStrictSupersetOfGuide()
        {
            var guide = SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.Guide);
            var noScene = SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.NoSceneAuthoring);

            Assert.That(guide, Is.Not.Null.And.Not.Empty);
            Assert.That(noScene, Is.Not.Null);
            Assert.That(guide.IsSubsetOf(noScene), Is.True,
                "guide 隐藏的分类必须全在 noSceneAuthoring 里 —— 否则「更严的档」会放开更宽的档拦住的东西。" +
                $"仅 guide 有: {string.Join(", ", guide.Except(noScene))}");
            Assert.That(noScene.Count, Is.GreaterThan(guide.Count),
                "noSceneAuthoring 的范围应当明确更宽。");
        }

        /// <summary>
        /// 量级下限。上面那条「可见数 == 全集 − 推导隐藏数」两边同源，所以它对一类回归是瞎的：
        /// 若 _guideHidden 被清空，或者一批写技能被误标成 ReadOnly，等式照样成立而档位实际什么
        /// 都不再隐藏。这里用下限而不是等值 —— 等值（59/326）会在有人只是新增了一个模块时
        /// 报「档位坏了」，那是为错误的理由失败。
        /// </summary>
        [TestCase(SurfaceProfileKind.Guide, 40)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring, 200)]
        public void HiddenWriteCount_StaysAboveFloor(SurfaceProfileKind profile, int floor)
        {
            Assert.That(SkillsSurfaceProfile.HiddenCategories(profile), Is.Not.Null.And.Not.Empty,
                $"{profile} 档的隐藏分类集为空。");

            int hiddenWrites = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Count(s => IsExpectedHidden(s, profile));

            Assert.That(hiddenWrites, Is.GreaterThanOrEqualTo(floor),
                $"{profile} 档只隐藏了 {hiddenWrites} 个写技能（下限 {floor}）。要么隐藏集缩了，" +
                $"要么一批写技能被误标成 ReadOnly —— 后者会让档位形同虚设而计数等式仍然成立。");
        }

        /// <summary>
        /// guide 档每个隐藏分类都必须有 manual-* 文档。这是「拒绝要可执行」的结构前提：
        /// 没文档的拒绝只能让 agent 干等用户改设置，而 guide 档的全部价值是让它转做讲解。
        /// 从隐藏集推导，所以有人给 guide 加第六个分类却没配文档时会立刻响铃。
        /// </summary>
        [Test]
        public void EveryGuideHiddenCategory_ShipsAManualDoc()
        {
            var missing = SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.Guide)
                .Where(c => string.IsNullOrEmpty(SkillsSurfaceProfile.ManualDocFor(c)))
                .Select(c => c.ToString())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.That(missing, Is.Empty,
                $"guide 档隐藏了这些分类但没有对应的 manual-* 文档: {string.Join(", ", missing)}。" +
                "要么补文档并在 ManualDocFor 里登记，要么这个分类不该进 guide 档。");
        }

        [Test]
        public void FullProfile_HidesNothing()
        {
            Assert.That(SkillsSurfaceProfile.HiddenCategories(SurfaceProfileKind.Full), Is.Null,
                "full 档必须返回 null，让热路径整段跳过 per-skill 过滤。");
            Assert.That(SkillsSurfaceProfile.IsFull, Is.True);
            Assert.That(SkillRouter.GetAllSkillsSnapshot().Length,
                Is.EqualTo(SkillRouter.GetAllSkillsSnapshotUnfiltered().Length));
        }

        // ---------- SURFACE_EXCLUDED ----------

        [Test]
        public void GuideProfile_HiddenWriteSkills_AnswerSurfaceExcluded()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            int checked_ = 0;
            foreach (var (category, skill, args) in WriteProbes)
            {
                if (!SkillRouter.TryGetSkill(skill, out var info)) continue;
                Assert.That(info.Category, Is.EqualTo(category),
                    $"探针 {skill} 的分类变了（现为 {info.Category}），测试选点需要跟着更新。");
                Assert.That(info.ReadOnly, Is.False, $"探针 {skill} 应当是写技能。");

                var response = JObject.Parse(SkillRouter.Execute(skill, args));

                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                    $"guide 档下 {skill}（{category} 写）应被档位拦住，实收: {response.ToString(Newtonsoft.Json.Formatting.None)}");
                Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.Abort),
                    $"{skill}: 档位拦截无令牌可取，只能 abort。");
                Assert.That(response["details"]?["surfaceProfile"]?.ToString(),
                    Is.EqualTo(SkillsSurfaceProfile.WireGuide));
                Assert.That(response["details"]?["category"]?.ToString(), Is.EqualTo(category.ToString()));
                Assert.That(response["details"]?["userControlled"]?.Value<bool>(), Is.True,
                    "必须明说这是用户的设置，否则 agent 会当成 bug 反复重试。");
                checked_++;
            }

            Assert.That(checked_, Is.EqualTo(WriteProbes.Length),
                "五个隐藏分类的探针技能应当全部注册在案。");
        }

        [Test]
        public void GuideProfile_ManualDocMapping_IsCorrectPerCategory()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            var expected = new Dictionary<SkillCategory, string>
            {
                { SkillCategory.GameObject, "skills/manual-gameobject/SKILL.md" },
                { SkillCategory.Component, "skills/manual-component/SKILL.md" },
                { SkillCategory.Material, "skills/manual-material/SKILL.md" },
                { SkillCategory.Scene, "skills/manual-scene/SKILL.md" },
                // Sample 的写就是换了名字的 GameObject authoring，所以复用 gameobject 的手册，
                // 而不是留 agent 无文档可读。
                { SkillCategory.Sample, "skills/manual-gameobject/SKILL.md" },
            };

            foreach (var (category, skill, args) in WriteProbes)
            {
                if (!SkillRouter.TryGetSkill(skill, out _)) continue;

                Assert.That(SkillsSurfaceProfile.ManualDocFor(category), Is.EqualTo(expected[category]),
                    $"{category} 的 manual 文档映射不对。");

                var details = JObject.Parse(SkillRouter.Execute(skill, args))["details"];
                Assert.That(details?["manualDoc"]?.ToString(), Is.EqualTo(expected[category]),
                    $"{skill} 的拒绝载荷里 manualDoc 不对。");
                Assert.That(details?["hint"]?.ToString(), Does.Contain(expected[category]),
                    $"{skill} 的 hint 必须把文档路径写进去（只查子串，不查整句措辞）。");
            }
        }

        [Test]
        public void SurfaceExcluded_IsNotLiftedByBypassMode()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            var (_, skill, args) = FirstRegisteredWriteProbe();
            var response = JObject.Parse(SkillRouter.Execute(skill, args));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                "Bypass 授予的是用户已经委托出去的权限；档位表达的是用户不希望被尝试的操作。" +
                "前者不能抬起后者。");
        }

        [Test]
        public void SurfaceExcluded_IsNotLiftedByAllowlistHit()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var (_, skill, args) = FirstRegisteredWriteProbe();

            bool addedByTest = false;
            try
            {
                addedByTest = SkillsModeManager.AddToAllowlist(skill);
                Assert.That(SkillsModeManager.IsInAllowlist(skill), Is.True, "前置条件：探针需在白名单里。");

                SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
                SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

                var response = JObject.Parse(SkillRouter.Execute(skill, args));
                Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                    "Bypass + 白名单双重命中依然不该绕过档位 —— 唯一的出路是用户把档位调回 full。");
            }
            finally
            {
                if (addedByTest) SkillsModeManager.RemoveFromAllowlist(skill);
            }
        }

        [Test]
        public void ReadOnlySkills_InHiddenCategories_StayCallable()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;

            int checked_ = 0;
            foreach (var (category, skill, args) in ReadProbes)
            {
                if (!SkillRouter.TryGetSkill(skill, out var info)) continue;
                Assert.That(info.ReadOnly, Is.True, $"探针 {skill} 应当是只读技能。");

                var response = JObject.Parse(SkillRouter.Execute(skill, args));

                // 目标不存在时回 NOT_FOUND 是正常的；这里只要求它不是被档位拦下的。
                Assert.That(response["errorCode"]?.ToString(), Is.Not.EqualTo("SURFACE_EXCLUDED"),
                    $"{skill}（{category} 只读）被档位拦了 —— 看不了场景的 AI 也教不了手动步骤。");
                checked_++;
            }

            Assert.That(checked_, Is.EqualTo(ReadProbes.Length), "五个只读探针应当全部注册在案。");
        }

        [Test]
        public void HiddenSkills_AreAlsoAbsentFromRecommend()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var hiddenNames = ExpectedHiddenSkillNames(SurfaceProfileKind.Guide);

            // 用被隐藏分类自己的名字做意图，最大化「如果没过滤就一定会命中」的概率。
            var recommend = JObject.Parse(SkillRouter.GetRecommendations("?intent=material+color&topN=50"));
            var recommended = ((JArray)recommend["results"]).Select(r => r["name"].ToString()).ToArray();

            Assert.That(recommended.Intersect(hiddenNames, StringComparer.Ordinal), Is.Empty,
                "recommend 也走 VisibleSkills，不该推荐一个调用即 SURFACE_EXCLUDED 的技能。");
        }

        [Test]
        public void Chain_OmitsHiddenProducers()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var fullProducers = ChainProducers("?output=instanceId&maxDepth=3");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var guideProducers = ChainProducers("?output=instanceId&maxDepth=3");
            var hiddenWrites = ExpectedHiddenSkillNames(SurfaceProfileKind.Guide);

            Assert.That(guideProducers.Intersect(hiddenWrites, StringComparer.Ordinal), Is.Empty,
                "被隐藏的 producer 会让 agent 走一条第一步就 SURFACE_EXCLUDED 的链。");
            // 这条链在 full 档下本来就含被隐藏的写技能（gameobject_create 等产出 instanceId），
            // 否则上面那句是空断言。
            Assert.That(fullProducers.Intersect(hiddenWrites, StringComparer.Ordinal), Is.Not.Empty,
                "前置条件：instanceId 链在 full 档下应当含至少一个 guide 档会隐藏的 producer。");
        }

        // ---------- 缓存重建 ----------

        [Test]
        public void ProfileSwitch_RebuildsBriefCache_AndChangesEtag()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            var fullBrief = SkillRouter.GetBrief();
            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills", string.Empty, out var fullJson, out var fullEtag),
                Is.True, "full 档下 brief 缓存应已建立。");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var guideBrief = SkillRouter.GetBrief();
            Assert.That(SkillRouter.TryGetCachedGetResponse("/skills", string.Empty, out var guideJson, out var guideEtag),
                Is.True, "切档后 brief 缓存应已重建。");

            int fullTotal = JObject.Parse(fullBrief)["totalSkills"].Value<int>();
            int guideTotal = JObject.Parse(guideBrief)["totalSkills"].Value<int>();

            Assert.That(guideTotal, Is.LessThan(fullTotal),
                "切到 guide 后可见数必须下降，否则缓存没重建。");
            Assert.That(guideJson, Is.Not.EqualTo(fullJson));
            Assert.That(guideEtag, Is.Not.EqualTo(fullEtag),
                "ETag 必须跟着变 —— 否则客户端的 If-None-Match 会拿到一份已经不成立的 304。");
        }

        // SkillsGuideMode 是 2.7 保留的兼容 shim，类级带 [Obsolete]，而本测试是它唯一的调用点。
        // 这里显式压掉 CS0618 而不是删掉断言：shim 存在的全部理由就是让只认布尔开关的老客户端
        // 继续读到正确的值，那条映射没人守就会在下次重构里悄悄失真。
#pragma warning disable 618
        [Test]
        public void DeprecatedGuideModeBoolean_MapsOnlyToGuideProfile()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
            Assert.That(SkillsSurfaceProfile.CurrentWire,
                Is.EqualTo(SkillsSurfaceProfile.WireNoSceneAuthoring));
            Assert.That(SkillsGuideMode.Enabled, Is.False,
                "弃用的 guideMode 别名只在 guide 档为真 —— noSceneAuthoring 读成 true 会骗老客户端。");

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            Assert.That(SkillsSurfaceProfile.CurrentWire, Is.EqualTo(SkillsSurfaceProfile.WireGuide));
            Assert.That(SkillsGuideMode.Enabled, Is.True);

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            Assert.That(SkillsGuideMode.Enabled, Is.False);

            // 写方向：赋 true 选中 guide；赋 false 只清 guide，绝不把 noSceneAuthoring 降级成 full
            // —— 布尔表达不了那个状态，静默放宽用户特意收窄的范围是这个 shim 最危险的失效方式。
            SkillsSurfaceProfile.Current = SurfaceProfileKind.NoSceneAuthoring;
            SkillsGuideMode.Enabled = false;
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.NoSceneAuthoring),
                "对 noSceneAuthoring 赋 Enabled=false 不该把档位放宽到 full。");

            SkillsGuideMode.Enabled = true;
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Guide));
            SkillsGuideMode.Enabled = false;
            Assert.That(SkillsSurfaceProfile.Current, Is.EqualTo(SurfaceProfileKind.Full),
                "从 guide 赋 false 应回到 full。");
        }
#pragma warning restore 618

        [Test]
        public void UnrecognizedWireValue_ResolvesToFull_NeverHidesSilently()
        {
            Assert.That(SkillsSurfaceProfile.TryParseWire("noSuchProfile", out var parsed), Is.False);
            Assert.That(parsed, Is.EqualTo(SurfaceProfileKind.Full),
                "打错的字或新版写的 pref 绝不能静默隐藏技能。");

            Assert.That(SkillsSurfaceProfile.TryParseWire("GUIDE", out var upper), Is.True);
            Assert.That(upper, Is.EqualTo(SurfaceProfileKind.Guide), "wire 解析应大小写不敏感。");
            Assert.That(SkillsSurfaceProfile.TryParseWire(" noSceneAuthoring ", out var padded), Is.True);
            Assert.That(padded, Is.EqualTo(SurfaceProfileKind.NoSceneAuthoring));
        }

        // ---------- dryRun 授权预览 ----------

        [Test]
        public void DryRun_OnHiddenSkill_ReportsSurfaceExcluded_ButIsItselfNotBlocked()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            var (category, skill, args) = FirstRegisteredWriteProbe();
            var dry = JObject.Parse(SkillRouter.DryRun(skill, args));

            // dryRun 本身从不被档位拦：预览被隐藏的技能，正是 agent 得知「用户要改什么设置」的途径。
            Assert.That(dry["status"]?.ToString(), Is.EqualTo("dryRun"),
                "dryRun 不该被档位拦下，它是只读预览。");
            Assert.That(dry["errorCode"], Is.Null);

            var auth = dry["authorization"];
            Assert.That(auth?["allowed"]?.Value<bool>(), Is.False);
            Assert.That(auth?["blockedBy"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                "Bypass 下也必须报 SURFACE_EXCLUDED，否则 agent 会被告知「可以跑」再撞墙。");
            Assert.That(auth?["surfaceProfile"]?.ToString(), Is.EqualTo(SkillsSurfaceProfile.WireGuide));
            Assert.That(auth?["hint"]?.ToString(),
                Does.Contain(SkillsSurfaceProfile.ManualDocFor(category)),
                "guide 档的 hint 必须给出可读的手册路径。");
        }

        // ---------- helpers ----------

        private static (SkillCategory category, string skill, string args) FirstRegisteredWriteProbe()
        {
            foreach (var probe in WriteProbes)
                if (SkillRouter.TryGetSkill(probe.skill, out _))
                    return probe;

            Assert.Fail("五个写探针技能一个都没注册，测试选点需要重新挑。");
            return default;
        }

        /// <summary>
        /// 当前档位下 brief 目录列出的全部技能名。
        /// </summary>
        private static string[] BriefSkillNames()
        {
            var modules = (JObject)JObject.Parse(SkillRouter.GetBrief())["modules"];
            return modules.Properties()
                .SelectMany(p => ((JArray)p.Value).Select(n => n.ToString()))
                .ToArray();
        }

        /// <summary>
        /// 测试侧对排除规则的**独立重述**，刻意不调
        /// <see cref="SkillsSurfaceProfile.IsExcluded(SkillRouter.SkillInfo)"/>。
        ///
        /// 调那个权威重载会让断言与被测实现同源，变成同义反复：某条规则从 IsExcludedCore 里消失时，
        /// 期望值和实际值一起变小，等式照样成立。这份副本的代价是产品新增第五条规则时它必须同步
        /// 更新 —— 那声响铃正是我们要的。
        ///
        /// 独立到规则结构一层为止：类别成员仍取
        /// <see cref="SkillsSurfaceProfile.HiddenCategories"/>，因为把三十多个类别名抄进测试只会
        /// 制造无意义的维护摩擦，而类别集合本身的增删是有意为之、不需要测试拦。
        ///
        /// 显式接收 profile 而不读 <c>Current</c>，所以它没有「必须先切档」的时序陷阱。
        /// </summary>
        private static bool IsExpectedHidden(SkillRouter.SkillInfo skill, SurfaceProfileKind profile)
        {
            // 规则 0：full 档不隐藏任何东西。
            if (profile == SurfaceProfileKind.Full) return false;
            // 规则 1：只读永不隐藏 —— 档位收回的是动手能力，不是看的能力。
            if (skill.ReadOnly) return false;
            // 规则 2：逃生口按名字隐藏（万能钥匙类技能，类别规则表达不了）。
            if (AlwaysHiddenSkillNames.Contains(skill.Name, StringComparer.Ordinal)) return true;
            // 规则 3：类别落在本档的隐藏集里。
            var hidden = SkillsSurfaceProfile.HiddenCategories(profile);
            if (hidden != null && hidden.Contains(skill.Category)) return true;
            // 规则 4：noSceneAuthoring 额外隐藏任何自称 MutatesScene 的写技能，不论模块。
            return profile == SurfaceProfileKind.NoSceneAuthoring && skill.MutatesScene;
        }

        /// <summary>给定档位下应被隐藏的技能名，走上面那份独立推述。</summary>
        private static HashSet<string> ExpectedHiddenSkillNames(SurfaceProfileKind profile)
        {
            return new HashSet<string>(
                SkillRouter.GetAllSkillsSnapshotUnfiltered()
                    .Where(s => IsExpectedHidden(s, profile))
                    .Select(s => s.Name),
                StringComparer.Ordinal);
        }

        private static string[] ChainProducers(string query)
        {
            var chain = JObject.Parse(SkillRouter.GetSkillChain(query));
            return ((JArray)chain["producers"]).Select(p => p["skill"].ToString()).ToArray();
        }
    }
}

// Producer:Betsy
