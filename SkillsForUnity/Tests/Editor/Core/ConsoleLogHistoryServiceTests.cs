using System;
using System.Collections.Generic;
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
        // About ten rows: files of a few dozen rows already take the tail-only reload path.
        private const long SmallTail = 2048;
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

        [Test]
        public void FullHistory_AFewEvictionsAppendInsteadOfRewritingTheFile()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            Fill(10, "late-");
            ConsoleLogHistoryService.FlushForTests();

            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + 10, FileRows());
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, ConsoleLogHistoryService.SnapshotAll(false, out _, out _).Count);
        }

        [Test]
        public void EvictionsPastAQuarterOfTheCap_CompactTheFileBackToTheCap()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            Fill(ConsoleLogHistoryService.CompactEvictionCount, "late-");
            ConsoleLogHistoryService.FlushForTests();

            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, FileRows());
        }

        [Test]
        public void PlayMode_DefersCompactionUntilAFullTurnover()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            Fill(ConsoleLogHistoryService.CompactEvictionCount, "play-");
            ConsoleLogHistoryService.FlushForTests(inPlayMode: true);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + ConsoleLogHistoryService.CompactEvictionCount, FileRows());

            Fill(ConsoleLogHistoryService.MaxEntries - ConsoleLogHistoryService.CompactEvictionCount, "turnover-");
            ConsoleLogHistoryService.FlushForTests(inPlayMode: true);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, FileRows());
        }

        [Test]
        public void AppendOnlyOverflow_ReloadKeepsTheNewestRowsAndCountsEachDropOnce()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            Fill(10, "late-");
            ConsoleLogHistoryService.FlushForTests();

            ConsoleLogHistoryService.ReloadForTests();
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out _);

            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, all.Count);
            Assert.AreEqual("late-9", all.Last().message);
            Assert.AreEqual(10L, dropped);
            ConsoleLogHistoryService.FlushForTests();
            // A tail-only reload leaves the ten evicted rows in the file until a quarter of the cap has piled up.
            Assert.LessOrEqual(FileRows(), ConsoleLogHistoryService.MaxEntries + ConsoleLogHistoryService.CompactEvictionCount);

            ConsoleLogHistoryService.ReloadForTests();
            var again = ConsoleLogHistoryService.SnapshotAll(false, out var droppedAgain, out _);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, again.Count);
            Assert.AreEqual(10L, droppedAgain, "Rows evicted before a reload are counted once however often it reloads.");
        }

        [Test]
        public void ReloadOfAFileLargerThanTheTail_ParsesOnlyTheTail()
        {
            Fill(200);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            long before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests();
            long parsed = ConsoleLogHistoryService.ParsedRowsForTests - before;

            Assert.Greater(parsed, 0);
            Assert.Less(parsed, 50, "A reload parses only the rows in the tail of the file.");
            Assert.AreEqual(200L, ConsoleLogHistoryService.GetCurrentSeq());
            var recent = ConsoleLogHistoryService.ReadWindow(195);
            var coverage = JObject.FromObject(recent.coverage);
            Assert.AreEqual(5, recent.logs.Count);
            Assert.IsTrue(recent.complete);
            Assert.AreEqual(1L, coverage.Value<long>("oldestSeq"), "The oldest seq is read from the file's head without loading it.");
            Assert.AreEqual(200L, coverage.Value<long>("retained"));
            Assert.Less(ConsoleLogHistoryService.ParsedRowsForTests - before, 50, "A window within the loaded tail does not load the head.");
            Assert.AreEqual(200, ConsoleLogHistoryService.ReadWindow(0).totalMatched, "An older window loads the head on demand.");
        }

        [Test]
        public void TailOnlyReload_ContinuesSeqAndAKilledEditorStillResumesAboveTheReservation()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            Fill(100);
            ConsoleLogHistoryService.ReloadForTests();
            ConsoleLogHistoryService.RecordForTests("after-clean-reload");
            Assert.AreEqual(101L, ConsoleLogHistoryService.GetCurrentSeq(), "A clean tail-only reload continues the sequence exactly.");

            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.RecordForTests("served-but-never-flushed");
            long lastServed = ConsoleLogHistoryService.GetCurrentSeq();
            long before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests(clean: false);
            long resumed = ConsoleLogHistoryService.GetCurrentSeq();
            Assert.Less(ConsoleLogHistoryService.ParsedRowsForTests - before, 50, "The killed editor's file is still read from its tail only.");
            ConsoleLogHistoryService.RecordForTests("after-kill");

            Assert.Greater(resumed, lastServed, "No seq a client may have read is handed out again.");
            var afterKill = ConsoleLogHistoryService.ReadWindow(resumed);
            Assert.IsTrue(afterKill.complete);
            Assert.AreEqual(resumed + 1, afterKill.logs[0].seq);
            Assert.AreEqual("after-kill", afterKill.logs[0].message);
            Assert.IsFalse(ConsoleLogHistoryService.ReadWindow(lastServed).complete, "Records served before the kill may be lost.");
            var messages = ConsoleLogHistoryService.ReadWindow(0).logs.Select(log => log.message).ToList();
            CollectionAssert.Contains(messages, "row-0");
            CollectionAssert.Contains(messages, "after-clean-reload");
            CollectionAssert.DoesNotContain(messages, "served-but-never-flushed");
        }

        [Test]
        public void TailOnlyReload_QuerySpanningOldAndNewRowsReturnsEachRowOnce()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            Fill(60, "old-");
            ConsoleLogHistoryService.ReloadForTests();
            // Appended after the reload, so both in memory and in the file; then two that are only in memory.
            Fill(3, "flushed-");
            ConsoleLogHistoryService.FlushForTests();
            Fill(2, "pending-");

            var page = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1000, false, null, null, null, null, false);
            var window = ConsoleLogHistoryService.ReadWindow(0);

            var expected = Enumerable.Range(0, 60).Select(i => "old-" + i)
                .Concat(Enumerable.Range(0, 3).Select(i => "flushed-" + i))
                .Concat(Enumerable.Range(0, 2).Select(i => "pending-" + i)).ToList();
            Assert.AreEqual(65, page.totalMatched);
            Assert.IsTrue(page.complete);
            CollectionAssert.AreEqual(expected, page.logs.Select(log => JObject.FromObject(log).Value<string>("message")).Reverse().ToList());
            Assert.AreEqual(65, window.totalMatched);
            CollectionAssert.AreEqual(Enumerable.Range(1, 65).Select(i => (long)i).ToList(), window.logs.Select(log => log.seq).ToList());

            ConsoleLogHistoryService.ReloadForTests();
            var reloaded = ConsoleLogHistoryService.ReadPage(DebugSkills.LogModeMask, null, 1000, false, null, null, null, null, false);
            Assert.AreEqual(65, reloaded.totalMatched, "The next reload neither loses nor repeats a row.");
        }

        [Test]
        public void TailOnlyReload_EvictingRowsOnlyTheFileHoldsStillCompactsTheFileToTheCap()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();
            long parsed = ConsoleLogHistoryService.ParsedRowsForTests;

            Fill(ConsoleLogHistoryService.CompactEvictionCount - 1, "late-");
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(parsed, ConsoleLogHistoryService.ParsedRowsForTests, "Evicting rows only the file holds does not parse them.");
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + ConsoleLogHistoryService.CompactEvictionCount - 1, FileRows(), "Below the threshold evictions only append.");

            Fill(1, "last-");
            ConsoleLogHistoryService.FlushForTests();
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out _);

            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, FileRows());
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, all.Count);
            Assert.AreEqual("row-" + ConsoleLogHistoryService.CompactEvictionCount, all[0].message, "The evicted rows are exactly the oldest ones.");
            Assert.AreEqual("last-0", all.Last().message);
            Assert.AreEqual((long)ConsoleLogHistoryService.CompactEvictionCount, dropped);
        }

        [Test]
        public void StateWithoutFileCounts_FallsBackToAFullLoadAndTheNextFlushWritesThem()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            Fill(60);
            ConsoleLogHistoryService.FinalFlushForTests();
            // The state file as versions before tail-only reloads wrote it.
            var state = JObject.Parse(File.ReadAllText(StatePath));
            foreach (var name in new[] { "fileRows", "fileBytes", "fileEvictedRows", "fileEvictedBytes", "fileLastSeq" })
                Assert.IsTrue(state.Remove(name), name);
            File.WriteAllText(StatePath, state.ToString(Formatting.None));

            long before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests(clean: false);
            Assert.AreEqual(60L, ConsoleLogHistoryService.ParsedRowsForTests - before, "Without file counts every row is read, as before.");
            Assert.AreEqual(60, ConsoleLogHistoryService.ReadWindow(0).totalMatched);

            ConsoleLogHistoryService.RecordForTests("after-upgrade");
            ConsoleLogHistoryService.FlushForTests();
            var upgraded = JObject.Parse(File.ReadAllText(StatePath));
            Assert.AreEqual(61L, upgraded.Value<long>("fileRows"));
            Assert.AreEqual(new FileInfo(_path).Length, upgraded.Value<long>("fileBytes"));
            Assert.AreEqual(61L, upgraded.Value<long>("fileLastSeq"));

            before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests();
            Assert.Less(ConsoleLogHistoryService.ParsedRowsForTests - before, 30, "The next reload reads only the tail again.");
            Assert.AreEqual(61, ConsoleLogHistoryService.ReadWindow(0).totalMatched);
        }

        [Test]
        public void NativeImport_SkipsAnEntryOnlyWhileAManagedTwinIsRetained()
        {
            ConsoleLogHistoryService.RecordForTests("twin", "", LogType.Error);
            Fill(ConsoleLogHistoryService.MaxEntries - 1);
            // Evicts the first twin; the second keeps the pair retained.
            ConsoleLogHistoryService.RecordForTests("twin", "", LogType.Error);
            ConsoleLogHistoryService.ImportNativeEntries(new[] { Native("Error", "twin", 1), Native("Warning", "twin", 2) });
            CollectionAssert.AreEqual(new[] { "Warning" }, NativeRows("twin"), "Only the entry without a retained managed twin is imported.");

            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.ImportNativeEntries(new[] { Native("Error", "twin", 1), Native("Warning", "twin", 2) });
            CollectionAssert.AreEqual(new[] { "Error" }, NativeRows("twin"),
                "With every managed twin evicted the entry is imported; an entry imported once is not imported again.");
        }

        [Test]
        public void NativeImport_AfterATailOnlyReload_SeesTwinsAndKeysOnlyTheFileHeld()
        {
            ConsoleLogHistoryService.RecordForTests("old-twin", "", LogType.Error);
            ConsoleLogHistoryService.ImportNativeEntries(new[] { Native("Warning", "old-native", 1) });
            Fill(100);
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();

            ConsoleLogHistoryService.ImportNativeEntries(new[] { Native("Error", "old-twin", 2), Native("Warning", "old-native", 1), Native("Log", "new-native", 3) });

            CollectionAssert.IsEmpty(NativeRows("old-twin"), "A managed twin read back from the file's head still counts.");
            Assert.AreEqual(1, NativeRows("old-native").Count, "A native key read back from the file's head is not imported again.");
            Assert.AreEqual(1, NativeRows("new-native").Count);
        }

        [Test]
        public void TailOnlyReload_UnreadableFileDuringEvictionKeepsEveryRowAndTheNextRecordCatchesUp()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();

            ConsoleLogHistoryService.SetReadsFailForTests(true);
            Fill(5, "over-");
            var recent = ConsoleLogHistoryService.ReadWindow(ConsoleLogHistoryService.GetCurrentSeq() - 3);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + 5L, JObject.FromObject(recent.coverage).Value<long>("retained"),
                "Rows the file cannot give up right now stay, a little over the cap.");
            Assert.AreEqual(0L, recent.dropped);
            Assert.IsTrue(recent.complete);
            Assert.IsFalse(ConsoleLogHistoryService.ReadWindow(0).complete, "A window needing the unreadable rows is incomplete, not short.");
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + 5, FileRows());

            ConsoleLogHistoryService.SetReadsFailForTests(false);
            Fill(1, "next-");
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out _);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, all.Count);
            Assert.AreEqual(6L, dropped);
            Assert.AreEqual("row-6", all[0].message);
            Assert.AreEqual("next-0", all.Last().message);
        }

        [Test]
        public void TailOnlyReload_UnreadableFileWhenCompactionIsDueRewritesNothingAndTheNextFlushCompacts()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();
            Fill(ConsoleLogHistoryService.CompactEvictionCount, "late-");

            ConsoleLogHistoryService.SetReadsFailForTests(true);
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + ConsoleLogHistoryService.CompactEvictionCount, FileRows(),
                "Without the rows only the file holds, a rewrite would lose them; the new rows are appended instead.");

            ConsoleLogHistoryService.SetReadsFailForTests(false);
            Fill(1, "last-");
            ConsoleLogHistoryService.FlushForTests();
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out _);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, FileRows());
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, all.Count);
            Assert.AreEqual(ConsoleLogHistoryService.CompactEvictionCount + 1L, dropped);
            Assert.AreEqual("row-" + (ConsoleLogHistoryService.CompactEvictionCount + 1), all[0].message);
        }

        [Test]
        public void TailOnlyReload_ReadingTheWholeHistoryOnlyAppendsAndPlayModeStillDefersCompaction()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();
            Fill(100, "e-");
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, ConsoleLogHistoryService.ReadWindow(0).totalMatched);
            Fill(1, "f-");
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + 101, FileRows(), "Loading the head on demand is no reason to rewrite the file.");

            ConsoleLogHistoryService.ReloadForTests();
            long parsed = ConsoleLogHistoryService.ParsedRowsForTests;
            Fill(ConsoleLogHistoryService.CompactEvictionCount, "play-");
            ConsoleLogHistoryService.FlushForTests(inPlayMode: true);
            Assert.AreEqual(parsed, ConsoleLogHistoryService.ParsedRowsForTests);
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries + 101 + ConsoleLogHistoryService.CompactEvictionCount, FileRows(),
                "Play Mode waits for a full turnover, also when the evictions came from the file's head.");

            Fill(1, "edit-");
            ConsoleLogHistoryService.FlushForTests();
            Assert.AreEqual(ConsoleLogHistoryService.MaxEntries, FileRows());
        }

        [Test]
        public void TailOnlyReloadsChainedWithoutLoadingTheHead_MatchAFullLoad()
        {
            Fill(ConsoleLogHistoryService.MaxEntries);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            ConsoleLogHistoryService.ReloadForTests();
            Fill(1000, "b-");
            ConsoleLogHistoryService.FlushForTests();
            // This load starts the history right after the evictions the previous domain persisted.
            ConsoleLogHistoryService.ReloadForTests();
            long parsed = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ImportNativeEntries(new DebugSkills.LogEntryInfo[0]);
            var recent = JObject.FromObject(ConsoleLogHistoryService.ReadWindow(ConsoleLogHistoryService.GetCurrentSeq() - 3).coverage);
            Assert.Less(ConsoleLogHistoryService.ParsedRowsForTests - parsed, 5, "Neither an empty import nor a recent window loads the head.");
            Assert.AreEqual(1001L, recent.Value<long>("oldestSeq"));
            Assert.AreEqual((long)ConsoleLogHistoryService.MaxEntries, recent.Value<long>("retained"));
            Fill(1000, "c-");
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.ReloadForTests();
            string tailOnly = Describe();

            ConsoleLogHistoryService.SetTailBytesForTests(long.MaxValue);
            ConsoleLogHistoryService.ReloadForTests();
            Assert.AreEqual(Describe(), tailOnly);
            StringAssert.Contains("dropped=2000 ", tailOnly);
        }

        [Test]
        public void TailOnlyReload_ByteCapAndRowsLongerThanTheHeadChunkMatchAFullLoad()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(128 * 1024);
            ConsoleLogHistoryService.SetHeadChunkBytesForTests(1024);
            FillLong(1700, "big-");
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.ReloadForTests();
            FillLong(30, "more-");
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.ReloadForTests();
            var recent = JObject.FromObject(ConsoleLogHistoryService.ReadWindow(ConsoleLogHistoryService.GetCurrentSeq() - 1).coverage);
            string tailOnly = Describe();

            ConsoleLogHistoryService.SetTailBytesForTests(long.MaxValue);
            ConsoleLogHistoryService.ReloadForTests();
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out _);
            Assert.AreEqual(Describe(), tailOnly);
            Assert.Greater(dropped, 30, "The byte cap, not the row cap, evicted these rows.");
            Assert.Less(all.Count, 1730);
            Assert.AreEqual(all[0].seq, recent.Value<long>("oldestSeq"), "The oldest seq was read from a row longer than the head's chunk.");
        }

        [Test]
        public void ClearAfterATailOnlyReload_KeepsOnlyLaterRowsAcrossTheNextReload()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            Fill(300);
            ConsoleLogHistoryService.FlushForTests();
            ConsoleLogHistoryService.ReloadForTests();
            Fill(5, "a-");
            ConsoleLogHistoryService.Clear();
            Fill(4, "c-");
            var expected = Enumerable.Range(0, 4).Select(i => "c-" + i).ToList();
            CollectionAssert.AreEqual(expected, ConsoleLogHistoryService.ReadWindow(0).logs.Select(log => log.message).ToList());

            ConsoleLogHistoryService.ReloadForTests();
            var window = ConsoleLogHistoryService.ReadWindow(0);
            CollectionAssert.AreEqual(expected, window.logs.Select(log => log.message).ToList());
            Assert.AreEqual(0L, window.dropped);
            Assert.AreEqual(4, FileRows());
        }

        [Test]
        public void TailLoad_FallsBackToAFullLoadWhenTheFileLengthOrLastSeqDiffersFromTheState()
        {
            ConsoleLogHistoryService.SetTailBytesForTests(SmallTail);
            Fill(100);
            ConsoleLogHistoryService.FinalFlushForTests();
            // A kill between an append and its state write: the file is longer than the state says.
            string late = JsonConvert.SerializeObject(new
            {
                seq = 101L, type = "Log", message = "appended-after-the-state", stackTrace = "", utc = DateTime.UtcNow.ToString("o"),
                timeKnown = true, sessionId = "s", source = "managed", nativeKey = (string)null,
            });
            ConsoleLogHistoryService.AppendRawForTests(late + Environment.NewLine);
            long before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests(clean: false);
            Assert.GreaterOrEqual(ConsoleLogHistoryService.ParsedRowsForTests - before, 101L, "The whole file is read.");
            CollectionAssert.Contains(ConsoleLogHistoryService.ReadWindow(0).logs.Select(log => log.message).ToList(), "appended-after-the-state");

            // The same length with another last row: a file the state does not describe.
            ConsoleLogHistoryService.FinalFlushForTests();
            File.WriteAllText(_path, File.ReadAllText(_path).Replace("\"seq\":101,", "\"seq\":109,"));
            before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests(clean: false);
            Assert.GreaterOrEqual(ConsoleLogHistoryService.ParsedRowsForTests - before, 101L, "The tail is rejected and the whole file read.");
            Assert.AreEqual(109L, ConsoleLogHistoryService.ReadWindow(0).logs.Last().seq);

            before = ConsoleLogHistoryService.ParsedRowsForTests;
            ConsoleLogHistoryService.ReloadForTests();
            Assert.Less(ConsoleLogHistoryService.ParsedRowsForTests - before, 30, "Once the full load has described the file again, the tail is enough.");
        }

        private static void Fill(int count, string prefix = "row-")
        {
            for (int i = 0; i < count; i++)
                ConsoleLogHistoryService.RecordForTests(prefix + i);
        }

        // Rows of 30-50 KB: 1,700 of them pass the 64 MB cap while staying far below the row cap.
        private static void FillLong(int count, string prefix)
        {
            var stack = new string('s', 50000);
            for (int i = 0; i < count; i++)
                ConsoleLogHistoryService.RecordForTests(prefix + i, stack.Substring(0, 30000 + i * 7919 % 20000), LogType.Warning);
        }

        /// <summary>What readers see of the whole history; loads it completely.</summary>
        private static string Describe()
        {
            long seq = ConsoleLogHistoryService.GetCurrentSeq();
            var coverage = JObject.FromObject(ConsoleLogHistoryService.ReadWindow(seq - 1).coverage);
            var all = ConsoleLogHistoryService.SnapshotAll(false, out var dropped, out var complete);
            return string.Concat("seq=", seq, " rows=", all.Count, " dropped=", dropped, " complete=", complete,
                " oldest=", coverage.Value<long>("oldestSeq"), " retained=", coverage.Value<long>("retained"),
                " incomplete=", coverage.Value<long>("incompleteThroughSeq"), " seqs=", string.Join(",", all.Select(log => log.seq)));
        }

        private static DebugSkills.LogEntryInfo Native(string type, string message, int line) =>
            new DebugSkills.LogEntryInfo { type = type, message = message, file = "Assets/Twin.cs", line = line };

        private static List<string> NativeRows(string message) => ConsoleLogHistoryService.SnapshotAll(false, out _, out _)
            .Where(log => log.source == "native" && log.message == message).Select(log => log.type).ToList();

        private int FileRows() => File.ReadLines(_path).Count(line => !string.IsNullOrWhiteSpace(line));

        private string StatePath => _path + ".state";
    }
}

// Producer:Betsy
