using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// /skills/recommend 的 golden 断言：几个固定意图必须把对应的核心技能排进前三。
    ///
    /// 刻意不断言完整的 top-N 名单 —— 名单会随注册表增删和同义词表调整而变，钉死它只会制造
    /// 无关失败。钉住的是「这个意图必须找到这个技能」，那才是排序真正的用途。
    ///
    /// 遥测在测试期间关闭：<c>GetRecommendationHealth</c> 会按 7 天窗口内的错误率给技能扣分，
    /// 本机残留的遥测数据会让同一个意图在不同机器上排出不同结果。
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
            // recommend 走 VisibleSkills，非 full 档会把期望技能整个藏掉。
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
        /// 读/写对齐的 golden。当初正是这个意图促成了打分调整：<c>camera_set_properties</c> 完全压过
        /// <c>camera_get_properties</c>，因为 setter 的描述必然会提到 reader 返回的那些属性，而这个
        /// setter 恰好提得更多。一个明确是"读"形态的意图，首位不能是写类技能。
        ///
        /// <para>断言的是首位结果的属性而非它的名字。点名赢家会连并列时的字典序 tie-break 一起钉死
        /// ——两个技能分数打平，最终靠 <c>get</c> &lt; <c>set</c> 分出胜负。那是真实行为但不是本测试
        /// 要管的事，将来一次改名会让它以错误的理由变红。</para>
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
        /// 这项调整必须在 <c>matchedOn</c> 里看得见，不能只体现在排序上。名次变了而响应又解释不出
        /// 原因，就没人能调试它——而打分改动恰恰是静默回归最爱藏身的地方，因为输出看起来依然合理。
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

                // 只读技能拿加分，写类技能不得被打上这个标记。写惩罚在这里不会出现——它只作用于
                // "写形态意图下的只读技能"，而本意图不是写形态。
                Assert.That(markers.Contains("intent:read+3"), Is.EqualTo(info.ReadOnly),
                    $"{name} (readOnly={info.ReadOnly}) carries matchedOn=[{string.Join(" ", markers)}]. " +
                    "The read bonus must be recorded on exactly the skills that received it.");
                Assert.That(markers, Does.Not.Contain("intent:write-1"),
                    $"{name}: a read-shaped intent must not apply the write penalty.");
            }
        }

        /// <summary>
        /// 镜像用例：明确是"写"形态的意图仍然必须找到写类技能。读加分的目的是纠正错排，
        /// 不是把写类技能推远——只读技能上那 -1 的微调正因如此才刻意压得很小。
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
            // 多词、句子形态的意图——这才是 agent 真正会发的形式，区别于上面那些双关键词探针。
            Assume.That(SkillRouter.HasSkill(expected), Is.True,
                $"{expected} is not registered (missing optional package?).");

            var results = Recommend($"?intent={intent}&topN=10");

            Assert.That(results.Take(3).ToArray(), Does.Contain(expected),
                $"Intent '{intent}' should rank {expected} in the top three. top-10: {string.Join(", ", results)}");
        }

        /// <summary>
        /// Sample 类技能是真实 gameobject_* / camera_* 技能的教学副本，名字短，会把名称子串加分
        /// 全部拿走。它们仍然可达，但只在意图里真的出现 sample/demo/example 时才该上榜。
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
            // 降权必须是有条件的，不能等于封禁——用户主动找演示技能时必须能找到。
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
        /// 打分调整不得动摇排序键：score 降序，再 semanticScore，再 name——在足够宽的结果集上断言，
        /// 免得比较器回归藏在短名单里看不出来。
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
            // 并列项的稳定键（#4 加的 ThenBy(Name, Ordinal)）。没有它，同分技能按反射发现顺序
            // 出场 —— 那个顺序在不同工程、不同 domain reload 之间都不一样，同一个意图会无理由
            // 给出不同排名。
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
