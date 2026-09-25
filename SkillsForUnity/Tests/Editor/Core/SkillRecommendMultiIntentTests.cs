using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Several intents in one /skills/recommend call (a repeated <c>intent=</c>): each ranked exactly as it would be alone,
    /// with the single-intent response untouched. Also pins the <c>unavailable</c> mark on candidates that cannot run here.
    /// Telemetry is off for the same reason as in <see cref="SkillRecommendGoldenTests"/>: its penalty would make rankings machine-dependent.
    /// </summary>
    [TestFixture]
    public class SkillRecommendMultiIntentTests
    {
        private SurfaceProfileKind _savedProfile;
        private bool _savedTelemetry;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedTelemetry = SkillTelemetryService.Enabled;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillTelemetryService.Enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillTelemetryService.Enabled = _savedTelemetry;
        }

        [TestCase("")]
        [TestCase("&includeSchema=true")]
        [TestCase("&includeSchema=true&wire=v2")]
        public void EachIntent_IsRankedAsIfAlone(string options)
        {
            var multi = JObject.Parse(SkillRouter.GetRecommendations(
                "?intent=create+cube&intent=set+material+color&topN=3" + options));

            var intents = multi["intents"] as JArray;
            Assert.That(intents, Is.Not.Null, multi.ToString(Formatting.None));
            Assert.That(intents.Select(i => (string)i["intent"]), Is.EqualTo(new[] { "create+cube", "set+material+color" }));
            Assert.That(multi["topN"]?.Value<int>(), Is.EqualTo(3));

            foreach (var entry in intents)
            {
                var single = JObject.Parse(SkillRouter.GetRecommendations("?intent=" + (string)entry["intent"] + "&topN=3" + options));
                Assert.That(JToken.DeepEquals(entry["results"], single["results"]), Is.True,
                    $"'{entry["intent"]}' ranked differently inside a multi-intent call.");
                Assert.That(entry["totalMatches"]?.Value<int>(), Is.EqualTo(single["totalMatches"]?.Value<int>()));
            }

            if (options.Contains("wire=v2"))
                Assert.That((string)multi["wire"], Is.EqualTo("v2"), "A v2 answer must say so, multi-intent or not.");
        }

        [Test]
        public void SingleIntent_BytesAreUnchanged_ThroughEitherEntryPoint()
        {
            const string query = "?intent=create+cube&topN=5&includeSchema=true";
            var viaQuery = SkillRouter.GetRecommendations(query);

            Assert.That(JObject.Parse(viaQuery)["intents"], Is.Null, "One intent keeps the single-intent shape.");
            Assert.That(SkillRouter.GetRecommendationsMulti(new[] { "create+cube" }, query), Is.EqualTo(viaQuery));
        }

        [Test]
        public void RepeatedOrBlankIntents_CollapseToOne()
        {
            var single = SkillRouter.GetRecommendations("?intent=create+cube&topN=3");

            Assert.That(SkillRouter.GetRecommendations("?intent=create+cube&intent=CREATE+CUBE&topN=3"), Is.EqualTo(single));
            Assert.That(SkillRouter.GetRecommendations("?intent=&intent=create+cube&topN=3"), Is.EqualTo(single));

            var none = JObject.Parse(SkillRouter.GetRecommendations("?intent=&intent=%20&topN=3"));
            Assert.That(none["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
        }

        [Test]
        public void CandidateWithAMissingPackage_IsMarkedUnavailable()
        {
            // Ignore rather than Assume: an inconclusive test makes Unity -runTests exit 2, and a project with every
            // optional package installed (opt6000, the optional-packages workflow) has nothing to probe here.
            if (PackageManagerHelper.InstalledPackages == null)
                Assert.Ignore("The package list is still loading; nothing is known to be missing.");
            var candidate = SkillRouter.GetAllSkillsSnapshot()
                .Where(s => s.RequiresPackages != null && s.RequiresPackages.Any(id => !PackageManagerHelper.IsPackageInstalled(id)))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate == null)
                Assert.Ignore("Every declared package is installed here.");

            var response = JObject.Parse(SkillRouter.GetRecommendations("?intent=" + candidate.Name.Replace('_', '+') + "&topN=50"));
            var entry = ((JArray)response["results"]).FirstOrDefault(r => (string)r["name"] == candidate.Name);
            Assume.That(entry, Is.Not.Null, $"{candidate.Name} did not rank for its own name.");

            Assert.That((string)entry["unavailable"], Is.EqualTo("missing_package"), entry.ToString(Formatting.None));
            var missing = ((JArray)entry["missingPackages"]).Select(t => t.ToString()).ToArray();
            Assert.That(missing, Is.Not.Empty);
            Assert.That(missing.All(id => candidate.RequiresPackages.Contains(id)), Is.True);
        }

        [Test]
        public void RunnableCandidates_CarryNoAvailabilityKeys()
        {
            var response = JObject.Parse(SkillRouter.GetRecommendations("?intent=create+cube&topN=10"));
            foreach (var entry in ((JArray)response["results"]).Cast<JObject>())
            {
                if (!SkillRouter.TryGetSkill((string)entry["name"], out var skill) ||
                    (skill.RequiresPackages != null && skill.RequiresPackages.Length > 0))
                    continue;

                Assert.That(entry.Property("unavailable"), Is.Null, $"{entry["name"]}: the v1 entry shape must stay as it was.");
                Assert.That(entry.Property("missingPackages"), Is.Null);
            }
        }
    }
}

// Producer:Betsy
