using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// batch step 循环里的 GameObjectFinder 缓存选择性失效（#2 留下的零覆盖缺口）。
    ///
    /// 一个 batch 的所有 step 共用一个 POST job，所以 ProcessJobQueue 的每请求失效不会在 step
    /// 之间跑；step 循环自己补了一次。#2 把它改成只对写 step 失效 —— 只读 step 按契约无副作用，
    /// 让它清缓存等于每个只读 step 后面都白重建一次场景索引。
    ///
    /// 缓存有效性靠反射读 <c>GameObjectFinder._cacheValid</c>。没有公开的观察点，而绕道行为观察
    /// （造一个缓存看不见的对象再查）会把「缓存是否有效」和「查询是否走缓存」两件事搅在一起。
    /// 字段改名会让这里立刻响铃并指明原因，这是可以接受的耦合。
    /// </summary>
    [TestFixture]
    public class BatchStepCacheInvalidationTests
    {
        private static readonly FieldInfo CacheValidField =
            typeof(GameObjectFinder).GetField("_cacheValid", BindingFlags.NonPublic | BindingFlags.Static);

        private SkillsOperatingMode _savedMode;
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedMode = SkillsModeManager.CurrentMode;
            _savedProfile = SkillsSurfaceProfile.Current;
            // step 要真的执行，所以模式必须放行；档位必须 full，否则写 step 被 SURFACE_EXCLUDED
            // 拦在 Execute 里，缓存失效那一行根本走不到（拦截路径不该失效缓存，这也是对的）。
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            GameObjectFinder.InvalidateCache();
        }

        [Test]
        public void CacheValidField_IsReachable()
        {
            Assert.That(CacheValidField, Is.Not.Null,
                "未找到 GameObjectFinder._cacheValid —— 字段被改名/移除了，本文件的观察方式需要跟着改。");
        }

        [Test]
        public void ReadOnlyStep_LeavesFinderCacheValid()
        {
            new GameObject("BatchCacheProbe");
            PrimeFinderCache();

            var result = RunBatch("gameobject_find", "{\"name\":\"BatchCacheProbe\"}");

            Assert.That(result["executed"]?.Value<int>() ?? 0, Is.GreaterThan(0),
                $"只读 step 没有执行，无法检验失效行为: {result.ToString(Newtonsoft.Json.Formatting.None)}");
            Assert.That(IsCacheValid(), Is.True,
                "只读 step 按契约无副作用，不该把场景索引清掉 —— 那会让后面每个 step 重建一次。");
        }

        [Test]
        public void WriteStep_InvalidatesFinderCache()
        {
            new GameObject("BatchCacheProbe");
            PrimeFinderCache();

            var result = RunBatch("gameobject_create", "{\"name\":\"BatchCacheWriteProbe\"}");

            Assert.That(result["executed"]?.Value<int>() ?? 0, Is.GreaterThan(0),
                $"写 step 没有执行: {result.ToString(Newtonsoft.Json.Formatting.None)}");
            Assert.That(IsCacheValid(), Is.False,
                "写 step 之后缓存必须失效，否则同一个 batch 里后面的 step 找不到它刚创建的对象。");
        }

        [Test]
        public void UnknownSkillStep_StillInvalidatesFinderCache()
        {
            // 名字解析不到已注册技能时走保守分支：宁可白失效一次，也不能假设一个未知调用没副作用。
            new GameObject("BatchCacheProbe");
            PrimeFinderCache();

            RunBatch("__no_such_skill_at_all__", "{}");

            Assert.That(IsCacheValid(), Is.False,
                "名字解析不到技能的 step 必须按写 step 处理。");
        }

        [Test]
        public void WriteStepFollowingReadStep_IsVisibleToLaterSteps()
        {
            // 上面三条盯的是内部状态；这条盯的是那个状态存在的理由：同一个 batch 里，后面的
            // step 必须能找到前面 step 创建的对象。
            var steps = new JArray
            {
                new JObject { ["skill"] = "gameobject_find", ["args"] = new JObject { ["name"] = "Nothing" } },
                new JObject { ["skill"] = "gameobject_create", ["args"] = new JObject { ["name"] = "BatchLateProbe" } },
                new JObject { ["skill"] = "gameobject_find", ["args"] = new JObject { ["name"] = "BatchLateProbe" } },
            };

            var result = SkillsHttpServer.ExecuteBatchCore(
                steps, new JObject(), continueOnError: true, dryRun: false,
                transactional: false, agentId: "tests");

            var lastStep = ((JArray)result["results"]).Last();
            Assert.That(lastStep["status"]?.ToString(), Is.EqualTo("success"),
                $"最后一个查找 step 失败了: {lastStep.ToString(Newtonsoft.Json.Formatting.None)}");
            Assert.That(GameObject.Find("BatchLateProbe"), Is.Not.Null,
                "前置条件：写 step 应当真的创建了对象。");
        }

        // ---------- helpers ----------

        /// <summary>
        /// 让 GameObjectFinder 建起场景索引。走公开的按路径查找入口，不碰私有方法 ——
        /// 结果不重要，重要的是它内部会调 GetOrBuildSceneCache。
        /// </summary>
        private static void PrimeFinderCache()
        {
            GameObjectFinder.InvalidateCache();
            GameObjectFinder.FindByPath("BatchCacheProbe");
            Assert.That(IsCacheValid(), Is.True, "前置条件：缓存应已建立。");
        }

        private static bool IsCacheValid()
        {
            Assert.That(CacheValidField, Is.Not.Null, "GameObjectFinder._cacheValid 不可达。");
            return (bool)CacheValidField.GetValue(null);
        }

        private static JObject RunBatch(string skill, string argsJson)
        {
            var steps = new JArray
            {
                new JObject { ["skill"] = skill, ["args"] = JObject.Parse(argsJson) }
            };

            return SkillsHttpServer.ExecuteBatchCore(
                steps, new JObject(), continueOnError: true, dryRun: false,
                transactional: false, agentId: "tests");
        }
    }
}

// Producer:Betsy
