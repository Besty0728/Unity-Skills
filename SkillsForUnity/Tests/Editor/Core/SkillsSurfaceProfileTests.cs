using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Visibility and interception semantics for the three SurfaceProfile tiers.
    ///
    /// Two disciplines run through this file:
    /// 1. Every count is derived at run time, never hardcoded -- registration counts vary with
    ///    installed optional packages, so guide/noSceneAuthoring hidden counts differ between a clean
    ///    CI project and a local dev machine. Derivation goes through <see cref="IsExpectedHidden"/>,
    ///    a test-side independent restatement of the four exclusion rules, deliberately not calling
    ///    <see cref="SkillsSurfaceProfile.IsExcluded(SkillRouter.SkillInfo)"/>: sharing the implementation under test would make it tautological -- a rule disappearing would shrink expected and actual together and the assertion would still pass.
    /// 2. Only structural fields are asserted (errorCode / details.manualDoc / details.surfaceProfile /
    ///    retryStrategy / authorization.blockedBy) and key substrings of doc paths. The hint is natural-language prose meant for an agent to read; its wording is free to change.
    ///
    /// EditorPrefs hygiene: <c>UnitySkills_SurfaceProfile</c> is shared globally per Unity version, not
    /// per project, so setup saves the original value and teardown restores it; each test explicitly sets the profile and mode it needs.
    /// </summary>
    [TestFixture]
    public class SkillsSurfaceProfileTests
    {
        /// <summary>
        /// The locator used for interception probes: guaranteed not to exist as an object/scene/asset
        /// in the registry, so in the "profile failed to block" failure branch the skill answers at worst NOT_FOUND rather than actually mutating the project.
        /// </summary>
        private const string AbsentTarget = "__unity_skills_surface_probe_absent__";

        /// <summary>
        /// A test-side independent copy of the escape-hatch list -- deliberately does not reference the
        /// implementation's <c>_alwaysHiddenSkillNames</c>, since referencing it would let the expected value go empty right along with the list if it were ever cleared.
        /// </summary>
        private static readonly string[] AlwaysHiddenSkillNames = { "editor_execute_menu" };

        /// <summary>
        /// One write-skill probe per hidden category across the five guide tiers. Each was chosen among
        /// skills with no semantic planner in <c>SkillPlanningService</c> -- ones with a planner
        /// (gameobject_create, component_add, material_create, scene_save…) would already return
        /// SEMANTIC_INVALID during semantic validation for a missing target, firing earlier than the profile gate and thus not actually testing the profile.
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

        /// <summary>The read-only skills in the same five categories -- the profile withdraws the ability to act, not the ability to see.</summary>
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
            // Don't assume the current mode is Bypass: a clean CI project defaults to Auto. Tests that need it unblocked set it themselves.
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

        // ---------- visible-set derivation ----------

        [TestCase(SurfaceProfileKind.Full)]
        [TestCase(SurfaceProfileKind.Guide)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring)]
        public void BriefVisibleCount_EqualsRegistryMinusDerivedHiddenSet(SurfaceProfileKind profile)
        {
            var registry = SkillRouter.GetAllSkillsSnapshotUnfiltered();

            // Expected values go through the test-side independent restatement of the four rules (see
            // IsExpectedHidden). Not calling the authoritative overload -- otherwise a deleted rule would shrink expected and actual together, and the equality would still hold.
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
        /// Guards the concrete consequence of Rule 2 (escape hatches hidden by name). The count equality
        /// only says "the totals add up"; this one watches that <c>editor_execute_menu</c> specifically disappears from the catalogue, and that calling it directly is blocked.
        ///
        /// It's the "master key": the menu-item skill can reach every write operation a profile wants
        /// to withdraw, and its category (Editor) isn't and shouldn't be in any hidden set. So without
        /// this rule every other exclusion is decorative -- an agent blocked from gameobject_create could just turn around and execute "GameObject/Create Empty" instead.
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

            // The menu path deliberately points at a nonexistent item: if the gate fails, the worst
            // case is ExecuteMenuItem not finding its target, not actually clicking a menu on the user's behalf.
            var response = JObject.Parse(SkillRouter.Execute(escapeHatch,
                "{\"menuPath\":\"__UnitySkills/NoSuchMenuItemForTests\"}"));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"),
                $"{profile} 档下 {escapeHatch} 应被拦住，实收: {response.ToString(Newtonsoft.Json.Formatting.None)}");

            // The escape hatch's category is Editor, which has no manual-* doc and shouldn't -- the manual
            // teaches "how to manually create a GameObject," while what's withdrawn here is "execute an
            // arbitrary menu item," which no doc matches. So manualDoc must be null, not a misleading path stuffed in -- pointing at the wrong doc is worse than no doc.
            var details = response["details"];
            Assert.That(details?["manualDoc"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                $"{escapeHatch} 的拒绝载荷不该给出 manual 文档（分类 Editor 没有对应手册）。");
            Assert.That(details?["hint"]?.ToString(), Is.Not.Null.And.Not.Empty,
                "没有文档可指时，hint 仍必须说明该怎么办（让用户改档位），不能留空。");
        }

        /// <summary>
        /// Profile protection for smoke probing. <c>test_smoke_skills</c> calls <c>Method.Invoke</c>
        /// directly on the probed skill, bypassing Execute entirely and thus the profile gate too --
        /// drawing from the filtered snapshot is itself the load-bearing safety boundary here. If
        /// switched back to Unfiltered, this read-only probe would become a batch execution of exactly the write skills the user's profile withdraws. This assertion guards that boundary.
        /// </summary>
        [TestCase(SurfaceProfileKind.Guide)]
        [TestCase(SurfaceProfileKind.NoSceneAuthoring)]
        public void SmokeProbe_NeverReportsSkillsHiddenByCurrentProfile(SurfaceProfileKind profile)
        {
            Assume.That(SkillRouter.HasSkill("test_smoke_skills"), Is.True, "test_smoke_skills 未注册。");

            // The gameobject_ prefix is deliberately chosen in a category both profiles hide, and the
            // probe surface is narrowed to a dozen-odd skills: runAsync=false runs a dryRun per selected skill, and running the full set isn't needed and would be slower.
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

            // executeReadOnly=false -> everything runs as dryRun, no skill is actually invoked.
            // runAsync=false -> a synchronous response with a results list; the default runAsync=true
            // only returns a jobId, from which no names could be read (that's how v1 was written, caught by its own non-empty guard).
            var response = JObject.Parse(SkillRouter.Execute("test_smoke_skills",
                "{\"nameContains\":\"" + prefix + "\",\"executeReadOnly\":false," +
                "\"includeMutating\":true,\"runAsync\":false}"));

            Assume.That(response["errorCode"], Is.Null,
                "test_smoke_skills 在此宿主上不可用: " + response["errorCode"]);

            // A successful response wraps the skill's own payload under result (BuildSuccessEnvelope), not flat at the top level.
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

            // Pinned positively: the visible set must all show up without exception, or "no leak" could just mean the probe returned nothing at all.
            Assert.That(reported, Is.EqualTo(expectedVisible),
                $"{profile} 档下 {prefix}* 的探测名单应与推导的可见集完全一致。");
        }

        /// <summary>
        /// Independent guard for Rule 4: a write skill that declares itself MutatesScene is scene
        /// authoring, no matter which module it lives in. This rule is what turns off modules like
        /// Netcode / Behavior that aren't in the category list -- which can never be exhaustive -- so this is derived from metadata, not read off the list.
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
        /// A magnitude floor. The assertion above ("visible == full minus derived-hidden") is blind to
        /// one class of regression because both sides share a source: if _guideHidden were cleared, or
        /// a batch of write skills got mistagged ReadOnly, the equality would still hold while the profile
        /// actually hides nothing anymore. A floor rather than an exact value is used here -- an exact value (59/326) would report "profile broken" the moment someone merely adds a module, failing for the wrong reason.
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
        /// Every hidden category in the guide tier must ship a manual-* doc. This is the structural
        /// precondition for "a rejection must be actionable": a rejection with no doc just leaves the agent
        /// waiting on the user, and the guide tier's entire value is letting it pivot to explaining instead. Derived from the hidden set, so it rings the moment someone adds a sixth category without configuring its doc.
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
                // Sample's write is just GameObject authoring under a different name, so it reuses
                // the gameobject manual rather than leaving the agent with no doc to read.
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

                // NOT_FOUND is normal when the target doesn't exist; this only requires that it wasn't blocked by the profile.
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

            // Use the hidden category's own name as the intent, to maximize the odds it would hit if not filtered.
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
            // This chain, under full, already includes hidden write skills (gameobject_create etc.
            // produce instanceId); otherwise the assertion above would be vacuous.
            Assert.That(fullProducers.Intersect(hiddenWrites, StringComparer.Ordinal), Is.Not.Empty,
                "前置条件：instanceId 链在 full 档下应当含至少一个 guide 档会隐藏的 producer。");
        }

        // ---------- cache rebuild ----------

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

        // SkillsGuideMode is a compatibility shim kept for 2.7, marked [Obsolete] at the class level,
        // and this test is its only caller. CS0618 is explicitly suppressed here instead of deleting
        // the assertion: the shim's entire reason to exist is letting old clients that only understand a boolean toggle keep reading the correct value -- that mapping would silently drift if nothing watched it.
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

            // Write direction: assigning true selects guide; assigning false only clears guide, and
            // never downgrades noSceneAuthoring to full -- a boolean can't express that state, and silently loosening a scope the user deliberately narrowed is this shim's most dangerous failure mode.
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

        // ---------- dryRun authorization preview ----------

        [Test]
        public void DryRun_OnHiddenSkill_ReportsSurfaceExcluded_ButIsItselfNotBlocked()
        {
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;

            var (category, skill, args) = FirstRegisteredWriteProbe();
            var dry = JObject.Parse(SkillRouter.DryRun(skill, args));

            // dryRun itself is never blocked by the profile: previewing a hidden skill is exactly how an agent learns what setting the user would need to change.
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
        /// All skill names listed in the brief catalogue at the current profile.
        /// </summary>
        private static string[] BriefSkillNames()
        {
            var modules = (JObject)JObject.Parse(SkillRouter.GetBrief())["modules"];
            return modules.Properties()
                .SelectMany(p => ((JArray)p.Value).Select(n => n.ToString()))
                .ToArray();
        }

        /// <summary>
        /// A test-side **independent restatement** of the exclusion rules, deliberately not calling
        /// <see cref="SkillsSurfaceProfile.IsExcluded(SkillRouter.SkillInfo)"/>.
        ///
        /// Calling that authoritative overload would make the assertion share a source with the
        /// implementation under test, turning it tautological: if a rule vanished from IsExcludedCore,
        /// expected and actual would shrink together and the equality would still hold. This copy costs upkeep -- it must be updated when the product adds a fifth rule -- but that ringing bell is exactly what we want.
        ///
        /// Independence stops at the level of rule structure: category membership still comes from
        /// <see cref="SkillsSurfaceProfile.HiddenCategories"/>, because copying thirty-odd category names
        /// into the test would only create pointless maintenance friction, and additions/removals to the category set itself are intentional and don't need a test to catch them.
        ///
        /// Takes profile explicitly instead of reading <c>Current</c>, so it has no "must switch profile first" ordering trap.
        /// </summary>
        private static bool IsExpectedHidden(SkillRouter.SkillInfo skill, SurfaceProfileKind profile)
        {
            // Rule 0: the full tier hides nothing.
            if (profile == SurfaceProfileKind.Full) return false;
            // Rule 1: read-only is never hidden -- the profile withdraws the ability to act, not the ability to see.
            if (skill.ReadOnly) return false;
            // Rule 2: escape hatches are hidden by name (master-key skills that category rules can't express).
            if (AlwaysHiddenSkillNames.Contains(skill.Name, StringComparer.Ordinal)) return true;
            // Rule 3: the category falls in this tier's hidden set.
            var hidden = SkillsSurfaceProfile.HiddenCategories(profile);
            if (hidden != null && hidden.Contains(skill.Category)) return true;
            // Rule 4: noSceneAuthoring additionally hides any write skill that declares itself MutatesScene, regardless of module.
            return profile == SurfaceProfileKind.NoSceneAuthoring && skill.MutatesScene;
        }

        /// <summary>Skill names expected to be hidden at the given profile, following the independent restatement above.</summary>
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
