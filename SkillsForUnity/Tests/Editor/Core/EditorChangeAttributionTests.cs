using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The attribution and identity fields added to journal entries: agent (explicit, resolved, mixed, unknown, null for
    /// manual edits), isPlaying, debounced property grouping, file-list totals, and entity ids on reparent and destroy.
    /// </summary>
    [TestFixture]
    public class EditorChangeAttributionTests
    {
        private string _tempPath;
        private string _savedOverride;

        [SetUp]
        public void SetUp()
        {
            _savedOverride = EditorChangeTrackerService.OverrideLogPathForTests;
            _tempPath = Path.Combine(Path.GetTempPath(), "UnitySkillsEditorChangesAttribution_" + Guid.NewGuid().ToString("N") + ".jsonl");
            EditorChangeTrackerService.OverrideLogPathForTests = _tempPath;
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
        }

        [TearDown]
        public void TearDown()
        {
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
            EditorChangeTrackerService.OverrideIsPlayingForTests = null;
            EditorChangeTrackerService.OverrideLogPathForTests = _savedOverride;
            EditorChangeTrackerService.ReloadForTests();
        }

        // ===== agent: the resolution rule =====

        [Test]
        public void ResolveRestAgent_ExplicitHeaderWinsOverTheProcessResolver()
        {
            var context = Context(port: 5555, fallback: "HeaderAgent", explicitId: true);

            string agent = EditorChangeTrackerService.ResolveRestAgent(context, Lookup("ResolvedAgent"));

            Assert.AreEqual("HeaderAgent", agent);
        }

        [Test]
        public void ResolveRestAgent_PrefersTheResolvedAgentWhenTheIdIsNotExplicit()
        {
            var context = Context(port: 5555, fallback: "UserAgentGuess", explicitId: false);

            Assert.AreEqual("ResolvedAgent", EditorChangeTrackerService.ResolveRestAgent(context, Lookup("ResolvedAgent")));
            Assert.AreEqual("UserAgentGuess", EditorChangeTrackerService.ResolveRestAgent(context, Lookup(null)),
                "a resolver miss falls back to the header guess");
            Assert.AreEqual("UserAgentGuess", EditorChangeTrackerService.ResolveRestAgent(context, Lookup("")),
                "an empty resolution is a miss");
        }

        [Test]
        public void ResolveRestAgent_WithoutAContextOrAnyUsableIdIsUnknown()
        {
            var lookedUp = false;
            EditorChangeTrackerService.AgentLookup spy = (int port, out string id) => { lookedUp = true; id = null; return false; };

            Assert.AreEqual("unknown", EditorChangeTrackerService.ResolveRestAgent(default, spy), "no request context");
            Assert.AreEqual("unknown", EditorChangeTrackerService.ResolveRestAgent(Context(5555, null, false), Lookup(null)), "nothing resolvable");
            Assert.AreEqual("unknown", EditorChangeTrackerService.ResolveRestAgent(Context(5555, "", true), spy), "blank explicit id");
            Assert.IsFalse(lookedUp, "the resolver is not consulted without a context or for an explicit id");
        }

        // ===== agent: what lands in the journal =====

        [Test]
        public void RestEntry_CarriesTheCallerAndEditorEntryCarriesNull()
        {
            using (SkillsAuditLog.BeginRequestContext(4242, "ProbeAgent", true))
                EditorChangeTrackerService.BeginRestExecution();
            // The request scope is gone by the time ObjectChangeEvents publish; the window must still know the caller.
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.EndRestExecutionNowForTests();
            EditorChangeTrackerService.PublishForTests("scene_saved", "editor");

            var entries = ReadEntries();

            Assert.AreEqual("rest", entries[0].Value<string>("source"));
            Assert.AreEqual("ProbeAgent", entries[0].Value<string>("agent"));
            Assert.AreEqual("editor", entries[1].Value<string>("source"));
            Assert.AreEqual(JTokenType.Null, entries[1]["agent"].Type);
            StringAssert.Contains("\"agent\":null", File.ReadAllLines(_tempPath)[1]);
        }

        [Test]
        public void RestEntry_WithoutARequestContextIsUnknown()
        {
            EditorChangeTrackerService.BeginRestExecution();
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.EndRestExecutionNowForTests();
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");

            var entries = ReadEntries();

            Assert.AreEqual("unknown", entries[0].Value<string>("agent"));
            Assert.AreEqual("unknown", entries[1].Value<string>("agent"), "an entry that claims REST with no window is still unknown, not stale");
        }

        [Test]
        public void RestWindow_WithDifferentCallersIsMixedAndTheNextWindowStartsClean()
        {
            using (SkillsAuditLog.BeginRequestContext(1, "AgentA", true))
                EditorChangeTrackerService.BeginRestExecution();
            using (SkillsAuditLog.BeginRequestContext(2, "AgentB", true))
                EditorChangeTrackerService.BeginRestExecution();
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.EndRestExecutionNowForTests();
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest"); // depth 1: still the same window
            EditorChangeTrackerService.EndRestExecutionNowForTests();

            using (SkillsAuditLog.BeginRequestContext(3, "AgentC", true))
                EditorChangeTrackerService.BeginRestExecution();
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.EndRestExecutionNowForTests();

            var agents = ReadEntries().Select(e => e.Value<string>("agent")).ToArray();

            CollectionAssert.AreEqual(new[] { "mixed", "mixed", "AgentC" }, agents);
        }

        [Test]
        public void RestWindow_WithOneCallerNestedTwiceKeepsThatCaller()
        {
            using (SkillsAuditLog.BeginRequestContext(1, "AgentA", true))
            {
                EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.BeginRestExecution();
            }
            EditorChangeTrackerService.PublishForTests("scene_changes", "rest");
            EditorChangeTrackerService.EndRestExecutionNowForTests();
            EditorChangeTrackerService.EndRestExecutionNowForTests();

            Assert.AreEqual("AgentA", ReadEntries()[0].Value<string>("agent"));
        }

        // ===== isPlaying and field order =====

        [Test]
        public void IsPlaying_IsStoredAtPublishTime()
        {
            EditorChangeTrackerService.PublishForTests("scene_saved", "editor");
            EditorChangeTrackerService.OverrideIsPlayingForTests = true;
            EditorChangeTrackerService.PublishForTests("scene_saved", "editor");

            var entries = ReadEntries();

            Assert.IsFalse(entries[0].Value<bool>("isPlaying"));
            Assert.IsTrue(entries[1].Value<bool>("isPlaying"));
        }

        [Test]
        public void Entry_PropertiesAppearInContractOrder()
        {
            EditorChangeTrackerService.PublishForTests("scene_saved", "editor");

            var names = JObject.Parse(File.ReadAllLines(_tempPath)[0]).Properties().Select(p => p.Name).ToArray();

            CollectionAssert.AreEqual(new[] { "seq", "type", "source", "agent", "isPlaying", "tsUtc", "payload" }, names);
        }

        // ===== file_changes totals =====

        [Test]
        public void FileChanges_LongListsAreSplitIntoConsecutiveEntriesWithNothingOmitted()
        {
            var imported = Enumerable.Range(0, 250).Select(i => "Assets/F" + i + ".txt")
                .Concat(new[] { "ASSETS/F0.TXT", "", null, "  " }).ToArray(); // duplicates and blanks are not counted
            var deleted = new[] { "Assets/D0.txt", "Assets/D1.txt", "Assets/D2.txt" };
            var moved = Enumerable.Range(0, 205).Select(i => "Assets/To/M" + i + ".txt").ToArray();
            var movedFrom = Enumerable.Range(0, 205).Select(i => "Assets/From/M" + i + ".txt").ToArray();

            EditorChangeTrackerService.RecordFileChanges(imported, deleted, moved, movedFrom);
            var entries = ReadEntries();

            Assert.AreEqual(2, entries.Length, "one entry per 200 paths of the longest list");
            var first = entries[0]["payload"];
            var second = entries[1]["payload"];
            Assert.AreEqual(entries[0].Value<long>("seq") + 1, entries[1].Value<long>("seq"), "the parts are consecutive");
            Assert.AreEqual(new[] { 200, 3, 200 }, Lengths(first));
            Assert.AreEqual(new[] { 50, 0, 5 }, Lengths(second));
            Assert.AreEqual("Assets/F200.txt", second["imported"][0].Value<string>());
            Assert.AreEqual("Assets/From/M200.txt", second["moved"][0].Value<string>("from"));
            Assert.AreEqual(new[] { 403, 0, 200, 3, 200, 1, 2 }, Counts(first));
            Assert.AreEqual(new[] { 55, 0, 50, 0, 5, 2, 2 }, Counts(second));
        }

        [Test]
        public void FileChanges_PastTheFlushBoundOnlyTheLastEntryCountsTheRest()
        {
            int total = EditorChangeTrackerService.MaxChangesPerFlush + 7;
            var imported = Enumerable.Range(0, total).Select(i => "Assets/Bulk/F" + i + ".txt").ToArray();

            EditorChangeTrackerService.RecordFileChanges(imported, null, null, null);
            var entries = ReadEntries();

            Assert.AreEqual(EditorChangeTrackerService.MaxEventsPerFlush, entries.Length);
            Assert.IsTrue(entries.Take(entries.Length - 1).All(e => e["payload"].Value<int>("omitted") == 0));
            var last = entries.Last()["payload"];
            Assert.AreEqual(EditorChangeTrackerService.MaxChangesPerEvent, ((JArray)last["imported"]).Count);
            Assert.AreEqual(7, last.Value<int>("omitted"));
            Assert.AreEqual(EditorChangeTrackerService.MaxChangesPerEvent + 7, last.Value<int>("importedTotal"));
            Assert.AreEqual(total, entries.Sum(e => e["payload"].Value<int>("importedTotal")),
                "the totals of the parts add up to the callback's");
        }

        [Test]
        public void FileChanges_WithoutTruncationReportZeroOmitted()
        {
            EditorChangeTrackerService.RecordFileChanges(
                new[] { "Assets/A.cs", "Assets/B.cs" }, new[] { "Assets/C.cs" },
                new[] { "Assets/New/D.cs" }, new[] { "Assets/Old/D.cs" });
            var payload = ReadEntries()[0]["payload"];

            Assert.AreEqual(4, payload.Value<int>("count"));
            Assert.AreEqual(0, payload.Value<int>("omitted"));
            Assert.AreEqual(2, payload.Value<int>("importedTotal"));
            Assert.AreEqual(1, payload.Value<int>("deletedTotal"));
            Assert.AreEqual(1, payload.Value<int>("movedTotal"));
        }

        // ===== entity ids on reparent / destroy =====

        [Test]
        public void Reparent_RecordCarriesEntityIdInstanceIdAndScenePath()
        {
            var parent = new GameObject("EctReparentParent");
            var child = new GameObject("EctReparentChild");
            try
            {
                EditorChangeTrackerService.UpsertCatalog(child);
                child.transform.SetParent(parent.transform);

                var change = EditorChangeTrackerService.CaptureReparented(
                    UnityObjectIdUtility.GetEntityId(child), UnityObjectIdUtility.GetEntityId(parent));

                Assert.AreEqual("gameobject_reparented", change.Value<string>("kind"));
                Assert.AreEqual(UnityObjectIdUtility.GetEntityId(child), change.Value<string>("entityId"));
                Assert.AreEqual(UnityObjectIdUtility.GetObjectId(child), change.Value<int>("instanceId"));
                Assert.AreEqual(child.scene.path, change.Value<string>("scenePath"));
                Assert.AreEqual(UnityObjectIdUtility.GetEntityId(parent), change.Value<string>("newParentEntityId"));
                StringAssert.EndsWith("EctReparentParent/EctReparentChild", change.Value<string>("toPath"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(child);
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void Destroyed_KnownObjectCarriesEntityIdAndInstanceId()
        {
            var go = new GameObject("EctDestroyedKnown");
            try
            {
                EditorChangeTrackerService.UpsertCatalog(go);
                string entityId = UnityObjectIdUtility.GetEntityId(go);
                int instanceId = UnityObjectIdUtility.GetObjectId(go);

                var change = EditorChangeTrackerService.CaptureDestroyed(entityId);

                Assert.AreEqual("gameobject_destroyed", change.Value<string>("kind"));
                Assert.AreEqual("EctDestroyedKnown", change.Value<string>("name"));
                Assert.AreEqual(entityId, change.Value<string>("entityId"));
                Assert.AreEqual(instanceId, change.Value<int>("instanceId"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Destroyed_UnknownObjectDerivesInstanceIdFromTheEventKey()
        {
#if UNITY_6000_4_OR_NEWER
            var change = EditorChangeTrackerService.CaptureDestroyed("987654321");
            Assert.AreEqual(0, change.Value<int>("instanceId"), "6000.4+ has no legacy instance id");
            Assert.AreEqual("987654321", change.Value<string>("entityId"));
#else
            var change = EditorChangeTrackerService.CaptureDestroyed("-987654");
            Assert.AreEqual(-987654, change.Value<int>("instanceId"));
            Assert.AreEqual("-987654", change.Value<string>("entityId"));
#endif
        }

        // ===== debounced property changes =====

        [Test]
        public void DebouncedProperty_KeepsTheRestCallerAfterTheWindowCloses()
        {
            var go = new GameObject("EctPropertyRest");
            try
            {
                using (SkillsAuditLog.BeginRequestContext(7, "PropAgent", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.x", "0", "1") });
                EditorChangeTrackerService.EndRestExecutionNowForTests(); // the debounce fires after the REST window is over
                EditorChangeTrackerService.FlushPendingProperties();

                var entry = PropertyEntries().Single();

                Assert.AreEqual("rest", entry.Value<string>("source"));
                Assert.AreEqual("PropAgent", entry.Value<string>("agent"));
                Assert.IsFalse(entry.Value<bool>("isPlaying"));
                Assert.AreEqual("m_LocalPosition.x", entry["payload"]["changes"][0].Value<string>("property"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DebouncedProperties_AreGroupedBySourceAgentAndPlayState()
        {
            var go = new GameObject("EctPropertyGroups");
            try
            {
                using (SkillsAuditLog.BeginRequestContext(1, "AgentA", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(new[]
                {
                    Modification(go.transform, "m_LocalPosition.x", "0", "1"),
                    Modification(go.transform, "m_LocalPosition.y", "0", "2")
                });
                EditorChangeTrackerService.EndRestExecutionNowForTests();

                using (SkillsAuditLog.BeginRequestContext(2, "AgentB", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.z", "0", "3") });
                EditorChangeTrackerService.OverrideIsPlayingForTests = true;
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalScale.x", "1", "4") });
                EditorChangeTrackerService.OverrideIsPlayingForTests = null;
                EditorChangeTrackerService.EndRestExecutionNowForTests();

                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalScale.y", "1", "5") });
                EditorChangeTrackerService.FlushPendingProperties();

                var groups = PropertyEntries()
                    .ToDictionary(e => Describe(e), e => e["payload"].Value<int>("count"));

                CollectionAssert.AreEquivalent(new[] { "rest/AgentA/edit", "rest/AgentB/edit", "rest/AgentB/play", "editor//edit" }, groups.Keys);
                Assert.AreEqual(2, groups["rest/AgentA/edit"], "two properties of one caller share an entry");
                Assert.AreEqual(1, groups["rest/AgentB/edit"]);
                Assert.AreEqual(1, groups["rest/AgentB/play"]);
                Assert.AreEqual(1, groups["editor//edit"]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DebouncedProperties_ManyChangesAreSplitIntoConsecutiveEntries()
        {
            var go = new GameObject("EctPropertyMany");
            try
            {
                using (SkillsAuditLog.BeginRequestContext(3, "BulkAgent", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(Enumerable.Range(0, 450)
                    .Select(i => Modification(go.transform, "m_Bulk" + i, "0", "1")).ToArray());
                EditorChangeTrackerService.EndRestExecutionNowForTests();
                EditorChangeTrackerService.FlushPendingProperties();

                var entries = PropertyEntries();

                Assert.AreEqual(new[] { 200, 200, 50 }, entries.Select(e => ((JArray)e["payload"]["changes"]).Count).ToArray());
                Assert.IsTrue(entries.All(e => e["payload"].Value<int>("omitted") == 0), "nothing is dropped below the flush bound");
                Assert.AreEqual(new[] { 1, 2, 3 }, entries.Select(e => e["payload"].Value<int>("part")).ToArray());
                Assert.IsTrue(entries.All(e => e["payload"].Value<int>("parts") == 3));
                Assert.IsTrue(entries.All(e => e.Value<string>("agent") == "BulkAgent"), "every part keeps the attribution");
                Assert.AreEqual(450, entries.SelectMany(e => e["payload"]["changes"])
                    .Select(c => c.Value<string>("property")).Distinct().Count());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DebouncedProperty_ChangedByTwoCallersInOneDebounceIsMixed()
        {
            var go = new GameObject("EctPropertyMixed");
            try
            {
                using (SkillsAuditLog.BeginRequestContext(1, "AgentA", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.x", "0", "1") });
                EditorChangeTrackerService.EndRestExecutionNowForTests();

                using (SkillsAuditLog.BeginRequestContext(2, "AgentB", true))
                    EditorChangeTrackerService.BeginRestExecution();
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.x", "1", "9") });
                EditorChangeTrackerService.EndRestExecutionNowForTests();
                EditorChangeTrackerService.FlushPendingProperties();

                var entry = PropertyEntries().Single();
                var changes = (JArray)entry["payload"]["changes"];

                Assert.AreEqual("mixed", entry.Value<string>("agent"));
                Assert.AreEqual(1, changes.Count, "the same property still collapses into one change");
                Assert.AreEqual("9", changes[0].Value<string>("value"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void DebouncedProperty_EditAndPlayChangesToOnePropertyStaySeparate()
        {
            var go = new GameObject("EctPropertyPlay");
            try
            {
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.x", "0", "1") });
                EditorChangeTrackerService.OverrideIsPlayingForTests = true;
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(go.transform, "m_LocalPosition.x", "1", "2") });
                EditorChangeTrackerService.OverrideIsPlayingForTests = null;
                EditorChangeTrackerService.FlushPendingProperties();

                var states = PropertyEntries().Select(e => e.Value<bool>("isPlaying")).OrderBy(x => x).ToArray();

                CollectionAssert.AreEqual(new[] { false, true }, states);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Property_OfAnObjectInsideAPrefabAssetCarriesTheAssetPath()
        {
            const string folder = "Assets/Temp/EditorChangeAttributionTests";
            if (!AssetDatabase.IsValidFolder("Assets/Temp"))
                AssetDatabase.CreateFolder("Assets", "Temp");
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets/Temp", "EditorChangeAttributionTests");
            var source = new GameObject("EctPrefabAsset");
            new GameObject("Child").transform.SetParent(source.transform);
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, folder + "/Probe.prefab");
                var child = prefab.transform.Find("Child");
                EditorChangeTrackerService.OnPostprocessModifications(new[] { Modification(child, "m_LocalPosition.x", "0", "2") });
                EditorChangeTrackerService.FlushPendingProperties();

                var change = PropertyEntries().Single()["payload"]["changes"][0];

                Assert.AreEqual(folder + "/Probe.prefab", change.Value<string>("assetPath"));
                StringAssert.EndsWith("/Child", change.Value<string>("path"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        // ===== helpers =====

        private static SkillsAuditLog.AmbientContext Context(int port, string fallback, bool explicitId) =>
            new SkillsAuditLog.AmbientContext { Active = true, RemotePort = port, FallbackAgentId = fallback, AgentIdIsExplicit = explicitId };

        private static EditorChangeTrackerService.AgentLookup Lookup(string resolved) =>
            (int port, out string id) => { id = resolved; return !string.IsNullOrEmpty(resolved); };

        private static UndoPropertyModification Modification(UnityEngine.Object target, string propertyPath, string previous, string current) =>
            new UndoPropertyModification
            {
                previousValue = new PropertyModification { target = target, propertyPath = propertyPath, value = previous },
                currentValue = new PropertyModification { target = target, propertyPath = propertyPath, value = current }
            };

        /// <summary>Listed imported, deleted and moved entries of a file_changes payload.</summary>
        private static int[] Lengths(JToken payload) =>
            new[] { ((JArray)payload["imported"]).Count, ((JArray)payload["deleted"]).Count, ((JArray)payload["moved"]).Count };

        /// <summary>count, omitted, importedTotal, deletedTotal, movedTotal, part, parts of a file_changes payload.</summary>
        private static int[] Counts(JToken payload) =>
            new[] { "count", "omitted", "importedTotal", "deletedTotal", "movedTotal", "part", "parts" }
                .Select(name => payload.Value<int>(name)).ToArray();

        private static string Describe(JToken entry)
        {
            string agent = entry["agent"].Type == JTokenType.Null ? string.Empty : entry.Value<string>("agent");
            return entry.Value<string>("source") + "/" + agent + "/" + (entry.Value<bool>("isPlaying") ? "play" : "edit");
        }

        private JObject[] ReadEntries() =>
            File.ReadAllLines(_tempPath)
                .Where(line => line.Length > 0)
                .Select(line => JObject.Parse(line))
                .ToArray();

        /// <summary>Only the debounced property entries: object-change events of the test's own GameObjects can land in the same journal.</summary>
        private JObject[] PropertyEntries() =>
            ReadEntries()
                .Where(entry => entry.Value<string>("type") == "scene_changes" &&
                                ((JArray)entry["payload"]["changes"]).Any(c => c.Value<string>("kind") == "property_changed"))
                .ToArray();
    }
}

// Producer:Betsy
