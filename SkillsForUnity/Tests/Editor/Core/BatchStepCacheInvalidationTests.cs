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
    /// Selective invalidation of the GameObjectFinder cache inside a batch step loop (a zero-coverage gap left by #2).
    ///
    /// All steps in one batch share a single POST job, so ProcessJobQueue's per-request invalidation never runs
    /// between steps; the step loop adds its own invalidation. #2 changed it to invalidate only after write
    /// steps — read-only steps are contractually side-effect-free, so clearing the cache after one would mean
    /// every subsequent read-only step rebuilds the scene index for nothing.
    ///
    /// Cache validity is read via reflection on <c>GameObjectFinder._cacheValid</c>. There's no public
    /// observation point, and a workaround behavioral observation (create an object the cache can't see, then
    /// query) would conflate two separate things: "is the cache valid" and "does the query go through the
    /// cache". A field rename will make this test fail loudly and point at why — an acceptable coupling.
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
            // The step must actually execute, so the mode must let it through; the profile must be full,
            // otherwise a write step gets caught in Execute by SURFACE_EXCLUDED and the cache-invalidation line
            // is never reached (and it's correct for the intercepted path to not invalidate the cache).
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
            // When name resolution can't find a registered skill, take the conservative branch: better a
            // needless invalidation than assuming an unknown call has no side effects.
            new GameObject("BatchCacheProbe");
            PrimeFinderCache();

            RunBatch("__no_such_skill_at_all__", "{}");

            Assert.That(IsCacheValid(), Is.False,
                "名字解析不到技能的 step 必须按写 step 处理。");
        }

        [Test]
        public void WriteStepFollowingReadStep_IsVisibleToLaterSteps()
        {
            // The three tests above watch internal state; this one watches the reason that state exists: within
            // the same batch, a later step must be able to find an object created by an earlier step.
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
        /// Makes GameObjectFinder build its scene index. Goes through the public find-by-path entry point,
        /// not a private method — the result doesn't matter, what matters is that it calls GetOrBuildSceneCache internally.
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
