using System;
using System.IO;
using System.Linq;
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
        private string _path;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), "UnitySkillsConsoleLogs_" + Guid.NewGuid().ToString("N") + ".jsonl");
            _scope = ConsoleLogHistoryService.BeginTestScope(_path);
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
        public void TornRowThenValidRows_LaterWindowsAreCompleteAndCompactionDropsTheRow()
        {
            ConsoleLogHistoryService.RecordForTests("before-1");
            ConsoleLogHistoryService.RecordForTests("before-2");
            long beforeTorn = ConsoleLogHistoryService.GetCurrentSeq();
            // The tail of a record whose head another writer overwrote, as found in the evaluation project's history.
            ConsoleLogHistoryService.AppendRawForTests("rce\":\"managed\",\"nativeKey\":null}" + Environment.NewLine);
            ConsoleLogHistoryService.RecordForTests("after-1");
            ConsoleLogHistoryService.RecordForTests("after-2");
            ConsoleLogHistoryService.ReloadForTests();

            var fromStart = ConsoleLogHistoryService.ReadWindow(0);
            var afterTorn = ConsoleLogHistoryService.ReadWindow(beforeTorn);
            var coverage = JObject.FromObject(afterTorn.coverage);
            Assert.IsFalse(fromStart.complete, "A window reaching back past the torn row may miss its record.");
            Assert.IsTrue(afterTorn.complete, "A torn row must not make later windows incomplete.");
            CollectionAssert.IsSubsetOf(new[] { "after-1", "after-2" }, afterTorn.logs.Select(log => log.message).ToList());
            Assert.AreEqual(1, coverage.Value<long>("corruptRows"));
            Assert.AreEqual(beforeTorn, coverage.Value<long>("incompleteThroughSeq"));

            ConsoleLogHistoryService.FlushForTests();
            foreach (var line in File.ReadAllLines(_path).Where(line => line.Length > 0))
                Assert.DoesNotThrow(() => JObject.Parse(line), "Compaction must drop the torn row: " + line);

            ConsoleLogHistoryService.ReloadForTests();
            var reloaded = JObject.FromObject(ConsoleLogHistoryService.ReadWindow(beforeTorn).coverage);
            Assert.IsTrue(ConsoleLogHistoryService.ReadWindow(beforeTorn).complete);
            Assert.IsFalse(ConsoleLogHistoryService.ReadWindow(0).complete, "Compaction removes the row, not the gap it marks.");
            Assert.AreEqual(1, reloaded.Value<long>("corruptRows"), "The count survives the compaction that removed the row.");
            Assert.AreEqual(beforeTorn, reloaded.Value<long>("incompleteThroughSeq"));
        }

        [Test]
        public void TornTailWithoutNewline_DoesNotSwallowTheNextAppendedRecord()
        {
            ConsoleLogHistoryService.RecordForTests("durable");
            // An append a kill cut short: no newline, so a plain append would glue the next record onto it.
            ConsoleLogHistoryService.AppendRawForTests("{\"seq\":999999,\"type\":\"Log\",\"mess");
            ConsoleLogHistoryService.RecordForTests("next");
            ConsoleLogHistoryService.ReloadForTests();

            var window = ConsoleLogHistoryService.ReadWindow(0);
            CollectionAssert.Contains(window.logs.Select(log => log.message).ToList(), "next");
            Assert.AreEqual(1, JObject.FromObject(window.coverage).Value<long>("corruptRows"));
        }

        [Test]
        public void RepeatedSeqFromASecondWriter_IsDroppedSoSeqStaysUnique()
        {
            ConsoleLogHistoryService.RecordForTests("first");
            ConsoleLogHistoryService.RecordForTests("second");
            long collided = ConsoleLogHistoryService.GetCurrentSeq();
            string foreign = JsonConvert.SerializeObject(new
            {
                seq = collided, type = "Warning", message = "foreign-writer", stackTrace = "", utc = DateTime.UtcNow.ToString("o"),
                timeKnown = true, sessionId = "another-process", source = "managed", nativeKey = (string)null,
            });
            ConsoleLogHistoryService.AppendRawForTests(foreign + Environment.NewLine);
            ConsoleLogHistoryService.RecordForTests("third");
            ConsoleLogHistoryService.ReloadForTests();

            var window = ConsoleLogHistoryService.ReadWindow(0);
            var seqs = window.logs.Select(log => log.seq).ToList();
            CollectionAssert.AllItemsAreUnique(seqs);
            CollectionAssert.IsOrdered(seqs);
            CollectionAssert.DoesNotContain(window.logs.Select(log => log.message).ToList(), "foreign-writer");
            CollectionAssert.Contains(window.logs.Select(log => log.message).ToList(), "third");
            Assert.AreEqual(1, JObject.FromObject(window.coverage).Value<long>("corruptRows"));
            Assert.IsTrue(ConsoleLogHistoryService.ReadWindow(collided).complete);
            Assert.IsFalse(ConsoleLogHistoryService.ReadWindow(collided - 1).complete);
        }

        [Test]
        public void CleanReload_ContinuesSeqAndKeepsSpanningWindowsComplete()
        {
            long start = ConsoleLogHistoryService.GetCurrentSeq();
            ConsoleLogHistoryService.RecordForTests("before-reload");
            ConsoleLogHistoryService.FinalFlushForTests();
            // Logged by another beforeAssemblyReload handler after ours ran; the DomainUnload flush still persists it.
            ConsoleLogHistoryService.RecordForTests("during-reload");
            long duringSeq = ConsoleLogHistoryService.GetCurrentSeq();
            ConsoleLogHistoryService.ReloadForTests();
            ConsoleLogHistoryService.RecordForTests("after-reload");

            var window = ConsoleLogHistoryService.ReadWindow(start);
            var messages = window.logs.Select(log => log.message).ToList();
            Assert.IsTrue(window.complete, "A clean domain reload must not make a window that spans it incomplete.");
            CollectionAssert.IsSubsetOf(new[] { "before-reload", "during-reload", "after-reload" }, messages);
            long afterSeq = window.logs.Single(log => log.message == "after-reload").seq;
            Assert.Greater(afterSeq, duringSeq);
            Assert.Less(afterSeq - duringSeq, ConsoleLogHistoryService.SeqReservationBlock, "A clean reload continues the sequence without a reservation gap.");
            Assert.AreEqual(0, JObject.FromObject(window.coverage).Value<long>("incompleteThroughSeq"));
        }

        [Test]
        public void KilledEditor_ResumesAboveItsReservationSoNoSeqRepeats()
        {
            ConsoleLogHistoryService.RecordForTests("flushed");
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.RecordForTests("served-but-never-flushed");
            long lastServed = ConsoleLogHistoryService.GetCurrentSeq();
            var state = JObject.Parse(File.ReadAllText(_path + ".state"));
            Assert.GreaterOrEqual(state.Value<long>("reservedSeq"), lastServed, "A served seq is always covered by the persisted reservation.");

            ConsoleLogHistoryService.ReloadForTests(clean: false);
            long resumed = ConsoleLogHistoryService.GetCurrentSeq();
            ConsoleLogHistoryService.RecordForTests("after-kill");

            Assert.Greater(resumed, lastServed, "No seq a client may have read is handed out again.");
            var messages = ConsoleLogHistoryService.ReadWindow(0).logs.Select(log => log.message).ToList();
            CollectionAssert.Contains(messages, "flushed");
            CollectionAssert.DoesNotContain(messages, "served-but-never-flushed");
            Assert.IsFalse(ConsoleLogHistoryService.ReadWindow(lastServed).complete, "Records served before the kill may be lost.");
            var afterKill = ConsoleLogHistoryService.ReadWindow(resumed);
            Assert.IsTrue(afterKill.complete);
            CollectionAssert.Contains(afterKill.logs.Select(log => log.message).ToList(), "after-kill");
        }

        [Test]
        public void FailingDiskWrite_IsReportedWithoutMakingInMemoryWindowsIncomplete()
        {
            ConsoleLogHistoryService.RecordForTests("kept-in-memory");
            Directory.CreateDirectory(_path);
            try
            {
                ConsoleLogHistoryService.FlushForTests();
                var window = ConsoleLogHistoryService.ReadWindow(0);
                Assert.IsTrue(JObject.FromObject(window.coverage).Value<bool>("durabilityIssue"));
                Assert.IsTrue(window.complete, "Records still in memory are complete evidence; the failure is reported, not hidden.");
            }
            finally
            {
                Directory.Delete(_path, true);
            }

            ConsoleLogHistoryService.FlushForTests();
            Assert.IsFalse(JObject.FromObject(ConsoleLogHistoryService.ReadWindow(0).coverage).Value<bool>("durabilityIssue"));
            CollectionAssert.Contains(File.ReadAllLines(_path).Select(line => JObject.Parse(line).Value<string>("message")).ToList(), "kept-in-memory");
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
