using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The reload-duration advice: which sessions count as slowed down, how samples are kept, and where the snapshot
    /// is served (/health and editor_get_state).
    /// </summary>
    [TestFixture]
    public class DomainReloadStatsTests
    {
        private static DomainReloadStats.State Session(params long[] reloads)
        {
            var state = new DomainReloadStats.State();
            foreach (long ms in reloads)
                state = DomainReloadStats.Record(state, ms);
            return state;
        }

        private static long[] Repeat(long ms, int count) => Enumerable.Repeat(ms, count).ToArray();

        [Test]
        public void RecordKeepsTheFirstFiveAndTheLastThreeWithoutChangingTheInput()
        {
            var before = Session(1, 2, 3);
            var after = DomainReloadStats.Record(before, 4);
            Assert.AreEqual(3, before.count);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, before.recent);

            var state = Session(1, 2, 3, 4, 5, 6, 7, 8);
            Assert.AreEqual(4, after.count);
            Assert.AreEqual(8, state.count);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, state.first);
            CollectionAssert.AreEqual(new long[] { 6, 7, 8 }, state.recent);
        }

        [Test]
        public void SlowedDownSessionRecommendsARestart()
        {
            var snapshot = DomainReloadStats.Evaluate(Session(Repeat(1600, 5).Concat(Repeat(2000, 20)).Concat(new long[] { 16000, 17000, 18000 }).ToArray()));
            Assert.IsTrue(snapshot.restartRecommended);
            Assert.AreEqual(1600, snapshot.baselineMs);
            Assert.AreEqual(17000, snapshot.recentMs);
            Assert.AreEqual(18000, snapshot.lastMs);
            Assert.AreEqual(28, snapshot.count);
        }

        [Test]
        public void TooFewReloadsGiveNoAdvice()
        {
            var snapshot = DomainReloadStats.Evaluate(Session(1600, 1600, 1600, 1600, 1600, 30000, 30000, 30000, 30000));
            Assert.AreEqual(9, snapshot.count);
            Assert.IsFalse(snapshot.restartRecommended);
        }

        [Test]
        public void OneSlowReloadIsNotASlowedDownSession()
        {
            var snapshot = DomainReloadStats.Evaluate(Session(Repeat(1600, 12).Concat(new long[] { 1700, 25000, 1800 }).ToArray()));
            Assert.AreEqual(1800, snapshot.recentMs);
            Assert.IsFalse(snapshot.restartRecommended);
        }

        [Test]
        public void FastProjectsNeedTenSecondReloadsBeforeAdvice()
        {
            Assert.IsFalse(DomainReloadStats.Evaluate(Session(Repeat(1600, 12).Concat(Repeat(6000, 3)).ToArray())).restartRecommended);
            Assert.IsTrue(DomainReloadStats.Evaluate(Session(Repeat(1600, 12).Concat(Repeat(10000, 3)).ToArray())).restartRecommended);
        }

        [Test]
        public void SlowProjectsNeedThreeTimesTheirOwnBaseline()
        {
            Assert.IsFalse(DomainReloadStats.Evaluate(Session(Repeat(8000, 12).Concat(Repeat(20000, 3)).ToArray())).restartRecommended);
            Assert.IsTrue(DomainReloadStats.Evaluate(Session(Repeat(8000, 12).Concat(Repeat(24000, 3)).ToArray())).restartRecommended);
        }

        [Test]
        public void StoredStateRoundTripsAndBadStateStartsOver()
        {
            var state = Session(1600, 1700, 1800);
            var back = DomainReloadStats.ParseState(JsonConvert.SerializeObject(state));
            Assert.AreEqual(3, back.count);
            CollectionAssert.AreEqual(state.first, back.first);
            CollectionAssert.AreEqual(state.recent, back.recent);

            foreach (string bad in new[] { null, "", "not json", "{\"count\":-1}", "null" })
            {
                var fresh = DomainReloadStats.ParseState(bad);
                Assert.AreEqual(0, fresh.count, bad);
                Assert.IsNotNull(fresh.first, bad);
                Assert.IsNotNull(fresh.recent, bad);
            }
            Assert.AreEqual(0, DomainReloadStats.Evaluate(DomainReloadStats.ParseState("{\"count\":4}")).baselineMs);
        }

        [Test]
        public void HealthAndEditorStateServeTheSnapshot()
        {
            var server = typeof(SkillsHttpServer);
            JObject health;
            try
            {
                server.GetMethod("RefreshHealthSnapshot", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { true });
                var vitals = server.GetNestedType("HealthVitals", BindingFlags.NonPublic)
                    .GetMethod("FromSnapshot", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                health = JObject.Parse((string)server.GetMethod("BuildHealthJson", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new[] { vitals, (object)false }));
            }
            finally
            {
                server.GetField("_healthSnapshotDirty", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            }
            var state = JObject.FromObject(EditorSkills.EditorGetState());
            foreach (var reload in new[] { (JObject)health["domainReload"], (JObject)state["domainReload"] })
            {
                Assert.IsNotNull(reload);
                CollectionAssert.AreEquivalent(new[] { "count", "lastMs", "recentMs", "baselineMs", "restartRecommended" },
                    reload.Properties().Select(p => p.Name).ToArray());
                Assert.AreEqual(DomainReloadStats.Current.count, (long)reload["count"]);
            }
        }
    }
}

// Producer:Betsy
