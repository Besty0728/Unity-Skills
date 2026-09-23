using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// GET /skills and /skills/schema: the exact-name filter (?names=a,b) and the selector-like unknown-key rejection.
    /// Cache-busting or telemetry keys (?nonce=) are still stripped silently -- see SkillRouterFilterCacheTests.
    /// </summary>
    [TestFixture]
    public class SkillFilterNamesTests
    {
        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void Names_ReturnsExactlyTheRequestedSkills_OnSchemaAndManifest()
        {
            foreach (var query in new[] { "?names=component_set_property,light_set_properties", "?names=component_set_property, light_set_properties&wire=v2" })
            {
                var schema = JObject.Parse(SkillRouter.GetFilteredSchema(query, out var schemaError));
                Assert.That(schemaError, Is.False, query);
                var names = ((JArray)schema["skills"]).Select(s => s["name"].ToString()).OrderBy(n => n).ToArray();
                Assert.That(names, Is.EqualTo(new[] { "component_set_property", "light_set_properties" }), query);
                Assert.That(schema["filtered"].Value<bool>(), Is.True, query);
                Assert.That(schema["filters"]["names"], Is.Not.Null, query);

                var manifest = JObject.Parse(SkillRouter.GetFilteredManifest(query, out var manifestError));
                Assert.That(manifestError, Is.False, query);
                Assert.That(((JArray)manifest["skills"]).Count, Is.EqualTo(2), query);
            }
        }

        [Test]
        public void Names_UnknownName_YieldsEmptyFilteredSet_NotAnError()
        {
            var schema = JObject.Parse(SkillRouter.GetFilteredSchema("?names=no_such_skill_xyz", out var isError));
            Assert.That(isError, Is.False);
            Assert.That(((JArray)schema["skills"]).Count, Is.EqualTo(0));
            Assert.That(schema["totalSkills"].Value<int>(), Is.EqualTo(0));
        }

        [Test]
        public void Names_Blank_IsRejectedLikeOtherBlankFilters()
        {
            var body = JObject.Parse(SkillRouter.GetFilteredSchema("?names=", out var isError));
            Assert.That(isError, Is.True);
            Assert.That(body["errorCode"].ToString(), Is.EqualTo("SEMANTIC_INVALID"));
        }

        [TestCase("?skill=component_set_property")]
        [TestCase("?name=component_set_property&wire=v2")]
        [TestCase("?category=Component&skillName=component_set_property")]
        public void SelectorLikeUnknownKey_IsRejectedWithUnknownParam(string query)
        {
            var body = JObject.Parse(SkillRouter.GetFilteredSchema(query, out var isError));
            Assert.That(isError, Is.True, query);
            Assert.That(body["errorCode"].ToString(), Is.EqualTo("UNKNOWN_PARAM"), query);
            Assert.That(body["details"]["validKeys"], Is.Not.Null, query);
            Assert.That(body["error"].ToString(), Does.Contain("names="), query);
        }

        [Test]
        public void OtherUnknownKeys_AreStillStrippedSilently()
        {
            var withNonce = SkillRouter.GetFilteredSchema("?category=Light&nonce=abc", out var error1);
            var plain = SkillRouter.GetFilteredSchema("?category=Light", out var error2);
            Assert.That(error1, Is.False);
            Assert.That(error2, Is.False);
            Assert.That(withNonce, Is.EqualTo(plain));
        }

        [Test]
        public void SelectorLikeUnknownKey_BypassesTheFastPathCache()
        {
            // The HTTP-thread fast path must step aside so the 400 body is minted on the main thread, never a cached 200 catalog.
            var served = SkillRouter.TryGetCachedGetResponse("/skills/schema", "?skill=component_set_property", out var json, out _);
            Assert.That(served, Is.False);
            Assert.That(json, Is.Null);
        }
    }
}

// Producer:Betsy
