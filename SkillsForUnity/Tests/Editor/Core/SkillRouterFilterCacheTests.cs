using System.Reflection;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// 覆盖给 SkillRouter 按 query 分片的 manifest/schema 缓存加上界的 P0 修复：
    /// 不认识的 query key（拼写错误、防缓存 nonce、客户端追踪参数）在参与缓存键之前就被剥掉；
    /// 缓存在 MaxCacheEntries 处硬封顶并自清，而不是无限增长。
    ///
    /// SkillRouter 的缓存字段是 private 且进程级全局的（没有测试专用重置钩子），所以增长断言
    /// 走反射读实时字段、只比相对增量，不断言绝对条数——同一轮里别的用例可能已经写进无关条目。
    /// </summary>
    [TestFixture]
    public class SkillRouterFilterCacheTests
    {
        private static int GetFilteredOutputCacheCount()
        {
            var field = typeof(SkillRouter).GetField("_filteredOutputCache", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "_filteredOutputCache field must exist");
            var dict = field.GetValue(null);
            var countProp = dict.GetType().GetProperty("Count");
            return (int)countProp.GetValue(dict);
        }

        [Test]
        public void GetFilteredManifest_UnrecognizedQueryKey_ProducesIdenticalOutputToBaseline()
        {
            string baseline = SkillRouter.GetFilteredManifest("category=GameObject");
            string withNonce = SkillRouter.GetFilteredManifest("category=GameObject&nonce=probe-value-xyz");

            Assert.That(withNonce, Is.EqualTo(baseline),
                "An unrecognized query key must be stripped before filtering, producing byte-identical output.");
        }

        [Test]
        public void GetFilteredManifest_VaryingUnrecognizedKeyValues_DoNotMintNewCacheEntries()
        {
            // 先预热共享键，保证测量前它的条目（若有）已存在。
            SkillRouter.GetFilteredManifest("category=Camera");
            int before = GetFilteredOutputCacheCount();

            for (int i = 0; i < 5; i++)
                SkillRouter.GetFilteredManifest($"category=Camera&nonce={i}-{System.Guid.NewGuid():N}");

            int after = GetFilteredOutputCacheCount();

            Assert.That(after - before, Is.LessThanOrEqualTo(1),
                "Five distinct nonce values must resolve to the same stripped cache key " +
                "(category=Camera), not five separate entries.");
        }

        [Test]
        public void GetFilteredManifest_CacheReachesCap_ClearsInsteadOfThrowing()
        {
            // "tags" 是被识别的过滤键，取值域无上界，因此每个不同 tag 都会真的新建一条缓存；
            // 灌够数量即可把缓存顶过内部上限，走到 Count>=cap -> Clear() 的清空路径。
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 300; i++)
                {
                    string json = SkillRouter.GetFilteredManifest($"tags=synthetic_probe_tag_{i}");
                    Assert.That(json, Is.Not.Null.And.Not.Empty);
                }
            });
        }
    }
}

// Producer:Betsy
