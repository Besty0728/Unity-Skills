using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Golden assertions for /skills/recommend: a handful of fixed intents must rank their corresponding core skill in the top three.
    ///
    /// Deliberately does not assert the full top-N list -- the list shifts as the registry gains/loses entries and the synonym table is tuned, pinning it down would only create
    /// unrelated failures. What's pinned down is "this intent must find this skill" -- that's what ranking is actually for.
    ///
    /// Telemetry is disabled during the test: <c>GetRecommendationHealth</c> docks a skill's score based on its error rate within a 7-day window,
    /// and leftover local telemetry data would make the same intent rank differently on different machines.
    /// </summary>
    [TestFixture]
    public class SkillRecommendGoldenTests
    {
        private SurfaceProfileKind _savedProfile;
        private bool _savedTelemetry;

        [SetUp]
        public void SetUp()
        {
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedTelemetry = SkillTelemetryService.Enabled;
            // recommend goes through VisibleSkills; a non-full profile would hide the expected skill entirely.
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillTelemetryService.Enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillTelemetryService.Enabled = _savedTelemetry;
        }

        [TestCase("material+color", "material_set_color")]
        [TestCase("create+prefab", "prefab_create")]
        [TestCase("run+test", "test_run")]
        public void Recommend_FixedIntent_RanksExpectedSkillInTopThree(string intent, string expected)
        {
            Assume.That(SkillRouter.HasSkill(expected), Is.True,
                $"{expected} 未注册（可选包缺失？），该意图的 golden 断言无从检验。");

            var results = Recommend($"?intent={intent}&topN=10");
            var topThree = results.Take(3).ToArray();

            Assert.That(topThree, Does.Contain(expected),
                $"意图 '{intent}' 应把 {expected} 排进前三，实际 top-10: {string.Join(", ", results)}");
        }

        /// <summary>
        /// A golden case for read/write alignment. This exact intent originally drove the scoring adjustment: <c>camera_set_properties</c> completely dominated
        /// <c>camera_get_properties</c>, because a setter's description inevitably mentions the properties the reader returns, and this
        /// setter happens to mention more of them. An intent that is unambiguously "read"-shaped must not lead with a write skill.
        ///
        /// <para>Asserts a property of the top result rather than its name. Naming the winner would also pin down the dictionary-order tie-break for ties
        /// -- two skills tie on score and it's ultimately <c>get</c> &lt; <c>set</c> that decides the winner. That's real behavior but not what this test
        /// is meant to cover; a future rename would turn it red for the wrong reason.</para>
        /// </summary>
        [Test]
        public void Recommend_UnambiguouslyReadIntent_LeadsWithAReadOnlySkill()
        {
            const string intent = "read+current+camera+properties+inspect+fov+clear+flags+values";
            var results = Recommend($"?intent={intent}&topN=10");

            Assert.That(results, Is.Not.Empty, "The intent matched nothing; the assertion would be vacuous.");
            Assert.That(SkillRouter.TryGetSkill(results[0], out var top), Is.True);
            Assert.That(top.ReadOnly, Is.True,
                $"A read-shaped intent led with the write skill '{results[0]}'. top-10: {string.Join(", ", results)}");
        }

        /// <summary>
        /// This adjustment must be visible in <c>matchedOn</c>, not only reflected in the ranking. If the rank changes but the response can't explain
        /// why, nobody can debug it -- and scoring changes are exactly where silent regressions love to hide, because the output still looks plausible.
        /// </summary>
        [Test]
        public void Recommend_ReadIntentBonus_IsAuditableFromTheResponse()
        {
            var response = JObject.Parse(SkillRouter.GetRecommendations(
                "?intent=read+current+camera+properties+inspect+fov+clear+flags+values&topN=10"));
            var entries = ((JArray)response["results"]).Cast<JObject>().ToArray();
            Assume.That(entries, Is.Not.Empty);

            foreach (var entry in entries)
            {
                var name = entry["name"].ToString();
                Assert.That(SkillRouter.TryGetSkill(name, out var info), Is.True);

                var markers = (entry["matchedOn"] as JArray)?.Select(m => m.ToString()).ToArray()
                              ?? Array.Empty<string>();

                // Read-only skills get the bonus; write skills must not be tagged with this marker. The write penalty won't appear here -- it only applies to
                // "read-only skills under a write-shaped intent", and this intent isn't write-shaped.
                Assert.That(markers.Contains("intent:read+3"), Is.EqualTo(info.ReadOnly),
                    $"{name} (readOnly={info.ReadOnly}) carries matchedOn=[{string.Join(" ", markers)}]. " +
                    "The read bonus must be recorded on exactly the skills that received it.");
                Assert.That(markers, Does.Not.Contain("intent:write-1"),
                    $"{name}: a read-shaped intent must not apply the write penalty.");
            }
        }

        /// <summary>
        /// Mirror case: an intent that is unambiguously "write"-shaped must still find the write skill. The read bonus exists to correct mis-ranking,
        /// not to push write skills away -- that's exactly why the -1 tweak on read-only skills is deliberately kept small.
        /// </summary>
        [Test]
        public void Recommend_WriteIntent_StillRanksTheWriteSkillFirst()
        {
            var results = Recommend("?intent=add+Rigidbody+component+to+GameObject&topN=10");

            Assume.That(SkillRouter.HasSkill("component_add"), Is.True);
            Assert.That(results.Take(3).ToArray(), Does.Contain("component_add"),
                $"A write-shaped intent must reach the write skill. top-10: {string.Join(", ", results)}");
        }

        [TestCase("get+light+color+intensity", "light_get_info")]
        [TestCase("add+Rigidbody+component+to+GameObject", "component_add")]
        public void Recommend_NaturalLanguageIntent_RanksExpectedSkillInTopThree(string intent, string expected)
        {
            // Multi-word, sentence-shaped intents -- this is the form agents actually send, as opposed to the two-keyword probes above.
            Assume.That(SkillRouter.HasSkill(expected), Is.True,
                $"{expected} is not registered (missing optional package?).");

            var results = Recommend($"?intent={intent}&topN=10");

            Assert.That(results.Take(3).ToArray(), Does.Contain(expected),
                $"Intent '{intent}' should rank {expected} in the top three. top-10: {string.Join(", ", results)}");
        }

        /// <summary>
        /// Sample-category skills are teaching copies of the real gameobject_* / camera_* skills; their short names would grab all the name-substring
        /// bonus. They should still be reachable, but should only surface when the intent actually contains sample/demo/example.
        /// </summary>
        [Test]
        public void Recommend_IntentWithoutSampleWords_DoesNotLeadWithASampleSkill()
        {
            var results = Recommend("?intent=move+object+to+position&topN=5");
            Assume.That(results, Is.Not.Empty);

            Assert.That(SkillRouter.TryGetSkill(results[0], out var top), Is.True);
            Assert.That(top.Category, Is.Not.EqualTo(SkillCategory.Sample),
                $"'{results[0]}' is a Sample skill. top-5: {string.Join(", ", results)}");
        }

        [Test]
        public void Recommend_IntentNamingSamples_StillReachesThem()
        {
            // The downweighting must be conditional, not a ban -- a user deliberately looking for demo skills must still be able to find them.
            var sampleSkills = new HashSet<string>(
                SkillRouter.GetAllSkillsSnapshot()
                    .Where(s => s.Category == SkillCategory.Sample)
                    .Select(s => s.Name),
                StringComparer.Ordinal);
            Assume.That(sampleSkills, Is.Not.Empty, "No Sample skills registered.");

            var results = Recommend("?intent=sample+demo+example+cube&topN=10");

            Assert.That(results.Intersect(sampleSkills, StringComparer.Ordinal), Is.Not.Empty,
                $"An intent naming samples reached none of them. top-10: {string.Join(", ", results)}");
        }

        /// <summary>
        /// Scoring adjustments must not disturb the sort key: score descending, then semanticScore, then name -- asserted over a wide enough result set,
        /// so a comparator regression can't hide in a short list.
        /// </summary>
        [Test]
        public void Recommend_ResultsAreSortedByScoreThenSemanticThenName()
        {
            var entries = ((JArray)JObject.Parse(SkillRouter.GetRecommendations("?intent=create&topN=50"))["results"])
                .Select(r => (name: r["name"].ToString(),
                              score: r["score"].Value<int>(),
                              semantic: r["semanticScore"].Value<int>()))
                .ToArray();
            Assume.That(entries.Length, Is.GreaterThan(1));

            for (int i = 1; i < entries.Length; i++)
            {
                var previous = entries[i - 1];
                var current = entries[i];

                bool ordered =
                    previous.score > current.score ||
                    (previous.score == current.score && previous.semantic > current.semantic) ||
                    (previous.score == current.score && previous.semantic == current.semantic &&
                     string.CompareOrdinal(previous.name, current.name) <= 0);

                Assert.That(ordered, Is.True,
                    $"Sort key violated at #{i}: {previous.name}(score={previous.score},sem={previous.semantic}) " +
                    $"before {current.name}(score={current.score},sem={current.semantic}).");
            }
        }

        [Test]
        public void Recommend_TiedScores_AreOrderedByNameOrdinal()
        {
            // The stable key for ties (the ThenBy(Name, Ordinal) added in #4). Without it, same-score skills appear in reflection
            // discovery order -- an order that differs across projects and across domain reloads -- so the same intent would
            // produce different rankings for no reason.
            var response = JObject.Parse(SkillRouter.GetRecommendations("?intent=create&topN=50"));
            var entries = ((JArray)response["results"])
                .Select(r => (name: r["name"].ToString(),
                              score: r["score"].Value<int>(),
                              semantic: r["semanticScore"].Value<int>()))
                .ToArray();

            Assert.That(entries, Is.Not.Empty, "意图 'create' 一个技能都没命中，测试是空的。");

            var tieGroups = entries
                .GroupBy(e => (e.score, e.semantic))
                .Where(g => g.Count() > 1)
                .ToArray();

            Assert.That(tieGroups, Is.Not.Empty,
                "没有任何并列分组，这条测试无从检验稳定键 —— 换一个命中面更宽的意图。");

            foreach (var group in tieGroups)
            {
                var names = group.Select(e => e.name).ToArray();
                Assert.That(names, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal).ToArray()),
                    $"score={group.Key.score} 的并列组未按名字字典序排列: {string.Join(", ", names)}");
            }
        }

        [Test]
        public void Recommend_IsDeterministicAcrossRepeatedCalls()
        {
            const string query = "?intent=create+material&topN=20";
            Assert.That(SkillRouter.GetRecommendations(query),
                Is.EqualTo(SkillRouter.GetRecommendations(query)),
                "同一个意图连续两次必须逐字节一致。");
        }

        [Test]
        public void Recommend_MissingIntent_ReportsMissingParam()
        {
            var response = JObject.Parse(SkillRouter.GetRecommendations("?topN=5"));

            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("MISSING_PARAM"));
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.RetryFixAndRetry));
        }

        private static string[] Recommend(string query)
        {
            var response = JObject.Parse(SkillRouter.GetRecommendations(query));
            Assert.That(response["errorCode"], Is.Null,
                $"recommend 返回了错误: {response.ToString(Newtonsoft.Json.Formatting.None)}");
            return ((JArray)response["results"]).Select(r => r["name"].ToString()).ToArray();
        }
    }
}

// Producer:Betsy
