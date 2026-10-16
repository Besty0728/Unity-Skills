using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    [TestFixture]
    public class ConsoleLogHistoryServiceTests
    {
        private IDisposable _scope;

        [SetUp]
        public void SetUp()
        {
            _scope = ConsoleLogHistoryService.BeginTestScope(Path.Combine(Path.GetTempPath(), "UnitySkillsConsoleLogs_" + Guid.NewGuid().ToString("N") + ".jsonl"));
        }

        [TearDown]
        public void TearDown()
        {
            _scope?.Dispose();
        }

        [Test]
        public void SnapshotPaging_1001Logs_HasFixedSnapshotAndNoDuplicates()
        {
            for (int i = 0; i < 1001; i++)
                ConsoleLogHistoryService.RecordForTests("log-" + i);

            var first = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1000, false, null, null, null, null, false);
            var second = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1000, false, null, null, null, first.nextCursor, false);

            Assert.AreEqual(1000, first.count);
            Assert.IsTrue(first.hasMore);
            Assert.AreEqual(1001, first.totalMatched);
            Assert.AreEqual(1, second.count);
            Assert.IsFalse(second.hasMore);
            Assert.AreEqual(1001, second.totalMatched);
            Assert.AreEqual(1001L, JObject.FromObject(first.logs[0]).Value<long>("seq"));
            Assert.AreEqual(1L, JObject.FromObject(second.logs[0]).Value<long>("seq"));
        }

        [Test]
        public void FullTextAndHalfOpenCrossMidnightWindow_UseFullStackAndUtcBounds()
        {
            var start = new DateTime(2026, 10, 10, 23, 59, 59, DateTimeKind.Utc);
            ConsoleLogHistoryService.RecordForTests("before", "needle stack", LogType.Error, start);
            ConsoleLogHistoryService.RecordForTests("at-end", "needle stack", LogType.Error, start.AddSeconds(1));

            var page = ConsoleLogHistoryService.ReadPage(DebugSkills.ErrorModeMask, "needle", 100, false,
                start.ToString("o"), start.AddSeconds(1).ToString("o"), null, null, true);

            Assert.AreEqual(1, page.count);
            var record = JObject.FromObject(page.logs[0]);
            Assert.AreEqual("before", record.Value<string>("message"));
            Assert.AreEqual("needle stack", record.Value<string>("stackTrace"));
        }

        [Test]
        public void CollapsedPaging_GroupsAcrossPagesWithoutRepeatingOrShrinkingCounts()
        {
            for (int i = 0; i < 3; i++) ConsoleLogHistoryService.RecordForTests("same", "same-stack", LogType.Warning);
            ConsoleLogHistoryService.RecordForTests("other", "other-stack", LogType.Warning);

            var first = ConsoleLogHistoryService.ReadPage(DebugSkills.WarningModeMask, null, 1, true, null, null, null, null, true);
            var second = ConsoleLogHistoryService.ReadPage(DebugSkills.WarningModeMask, null, 1, true, null, null, null, first.nextCursor, true);

            Assert.AreEqual(4, first.totalMatched);
            Assert.AreEqual(1, JObject.FromObject(first.logs[0]).Value<int>("count"));
            Assert.AreEqual(3, JObject.FromObject(second.logs[0]).Value<int>("count"));
            Assert.AreNotEqual(JObject.FromObject(first.logs[0]).Value<string>("groupId"), JObject.FromObject(second.logs[0]).Value<string>("groupId"));
        }

        [Test]
        public void Collapse_KeepsSameMessageWithDifferentStacksInSeparateGroups()
        {
            ConsoleLogHistoryService.RecordForTests("same-message", "stack-one", LogType.Error);
            ConsoleLogHistoryService.RecordForTests("same-message", "stack-two", LogType.Error);

            var page = ConsoleLogHistoryService.ReadPage(DebugSkills.ErrorModeMask, null, 10, true, null, null, null, null, true);

            Assert.AreEqual(2, page.count);
            Assert.AreNotEqual(JObject.FromObject(page.logs[0]).Value<string>("groupId"), JObject.FromObject(page.logs[1]).Value<string>("groupId"));
        }

        [Test]
        public void FixedSnapshot_DoesNotAdmitLogsAddedAfterFirstPage()
        {
            ConsoleLogHistoryService.RecordForTests("first");
            ConsoleLogHistoryService.RecordForTests("second");
            var first = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1, false, null, null, null, null, false);
            ConsoleLogHistoryService.RecordForTests("late");

            var second = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1, false, null, null, null, first.nextCursor, false);

            Assert.AreEqual(2, second.totalMatched);
            Assert.AreEqual("first", JObject.FromObject(second.logs[0]).Value<string>("message"));
        }

        [Test]
        public void Clear_InvalidatesOldCursor()
        {
            ConsoleLogHistoryService.RecordForTests("before-clear");
            var first = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1, false, null, null, null, null, false);
            ConsoleLogHistoryService.Clear();

            var afterClear = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1, false, null, null, null, first.cursor, false);

            Assert.AreEqual("cursor", afterClear.errorParameter);
        }

        [Test]
        public void Reload_SkipsCorruptTailAndMarksCoverageIncomplete()
        {
            ConsoleLogHistoryService.RecordForTests("durable");
            ConsoleLogHistoryService.AppendCorruptTailForTests();
            ConsoleLogHistoryService.ReloadForTests();

            var page = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 10, false, null, null, null, null, false);
            var coverage = JObject.FromObject(page.coverage);
            Assert.AreEqual(1, page.count);
            Assert.IsFalse(page.complete);
            Assert.AreEqual(1, coverage.Value<long>("corruptRows"));
        }

        [Test]
        public void ConsoleUtcBounds_RejectLocalAndNonUtcInput()
        {
            var local = JObject.FromObject(ConsoleSkills.ConsoleGetLogs(fromUtc: "2026-10-10"));
            var nonUtc = JObject.FromObject(ConsoleSkills.ConsoleGetLogs(fromUtc: "2026-10-10T00:00:00+08:00"));

            Assert.AreEqual("fromUtc", local.Value<string>("parameter"));
            Assert.AreEqual("fromUtc", nonUtc.Value<string>("parameter"));
        }

        [Test]
        public void EventChannel_RecordsEveryRuntimeErrorBeforeEventThrottle()
        {
            int recorded = 0;
            EventChannelService.ResetConsoleErrorThrottleForTests();
            EventChannelService.RuntimeErrorRecorderForTests = (message, stack, type) => recorded++;
            try
            {
                for (int i = 0; i < 1001; i++)
                    EventChannelService.OnConsoleErrorForTests("burst-" + i, "stack", LogType.Error);

                Assert.AreEqual(1001, recorded);
            }
            finally
            {
                EventChannelService.ResetConsoleErrorThrottleForTests();
            }
        }

        [Test]
        public void OverflowAndReload_PreserveBoundedStateAndPersistedCursor()
        {
            for (int i = 0; i <= ConsoleLogHistoryService.MaxEntries; i++)
                ConsoleLogHistoryService.RecordForTests("overflow-" + i);

            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.ReloadForTests();
            var window = ConsoleLogHistoryService.ReadWindow(0, limit: 1);

            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out var complete).Count);
            Assert.Greater(dropped, 0);
            Assert.IsFalse(complete);
            Assert.IsFalse(window.complete);
            Assert.AreEqual(2L, window.logs[0].seq);
        }
    }
}

// Producer:Betsy
