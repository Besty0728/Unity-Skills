using System.Reflection;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Covers the P0 fix that bounds SkillRouter's per-query filtered manifest/schema cache:
    /// unrecognized query keys (typos, cache-busting nonces, client tracking params) are stripped
    /// before they reach the cache key, and the cache hard-caps at MaxCacheEntries and
    /// self-clears instead of growing without bound.
    ///
    /// SkillRouter's cache fields are private and process-global (no test-only reset hook), so
    /// this fixture reads the live field via reflection for the growth assertion rather than
    /// asserting an absolute count — other tests in the same run may have already populated
    /// unrelated entries.
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
            // Prime the shared key once so its entry (if any) already exists before measuring.
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
            // "tags" is a recognized filter key with an unbounded value domain, so distinct tag
            // values each mint a real cache entry — enough of them drives the cache past its
            // internal cap and exercises the Count>=cap -> Clear() eviction path.
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
