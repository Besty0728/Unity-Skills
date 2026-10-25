using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Journal segment rotation: the active file is renamed to <c>.1</c> (never rewritten), older segments shift up, the
    /// eleventh is dropped, a failed rename keeps the journal appending, and a reload recovers seq and ring from the
    /// segments. The entry/byte caps are overridden to a handful so a test needs a few appends, not thousands.
    /// </summary>
    [TestFixture]
    public class EditorChangeJournalRotationTests
    {
        private string _tempPath;
        private string _savedOverride;

        [SetUp]
        public void SetUp()
        {
            _savedOverride = EditorChangeTrackerService.OverrideLogPathForTests;
            _tempPath = Path.Combine(Path.GetTempPath(), "UnitySkillsEditorChangesRotation_" + Guid.NewGuid().ToString("N") + ".jsonl");
            EditorChangeTrackerService.OverrideLogPathForTests = _tempPath;
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
        }

        [TearDown]
        public void TearDown()
        {
            EditorChangeTrackerService.ResetForTests(deleteFile: true);
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 0;
            EditorChangeTrackerService.OverrideSegmentMaxBytesForTests = 0;
            EditorChangeTrackerService.OverrideLogPathForTests = _savedOverride;
            // Hand the live journal back with its own ring and seq, not the emptied state ResetForTests leaves behind.
            EditorChangeTrackerService.ReloadForTests();
        }

        [Test]
        public void Constants_MatchTheJournalContract()
        {
            Assert.AreEqual(2000, EditorChangeTrackerService.SegmentMaxEntries);
            Assert.AreEqual(8L * 1024 * 1024, EditorChangeTrackerService.SegmentMaxBytes);
            Assert.AreEqual(10, EditorChangeTrackerService.MaxRotatedSegments);
            Assert.AreEqual(500, EditorChangeTrackerService.BufferCapacity);
        }

        [Test]
        public void SegmentPath_NamesRotatedFilesNextToTheActiveOne()
        {
            string directory = Path.Combine(Path.GetTempPath(), "UnitySkillsJournalNaming");
            EditorChangeTrackerService.OverrideLogPathForTests = Path.Combine(directory, "editor_changes.jsonl");

            Assert.AreEqual(Path.Combine(directory, "editor_changes.1.jsonl"), EditorChangeTrackerService.SegmentPath(1));
            Assert.AreEqual(Path.Combine(directory, "editor_changes.10.jsonl"), EditorChangeTrackerService.SegmentPath(10));
        }

        [Test]
        public void Rotation_ByEntryCount_MovesTheFullSegmentToSlotOne()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 5;

            Publish(4);
            Assert.IsFalse(File.Exists(Segment(1)), "below the cap nothing rotates");
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, Seqs(_tempPath));

            Publish(1);
            Assert.IsFalse(File.Exists(_tempPath), "the append that reaches the cap hands the whole file over");
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, Seqs(Segment(1)));

            Publish(1);
            CollectionAssert.AreEqual(new long[] { 6 }, Seqs(_tempPath), "later entries start a fresh active segment");
        }

        [Test]
        public void Rotation_ByByteSize_RotatesOnceTheActiveSegmentReachesTheByteCap()
        {
            const long cap = 1500;
            EditorChangeTrackerService.OverrideSegmentMaxBytesForTests = cap;
            var payload = new JObject { ["pad"] = new string('x', 400) };

            int published = 0;
            while (!File.Exists(Segment(1)) && published < 20)
            {
                EditorChangeTrackerService.PublishForTests("rotation_probe", "editor", payload);
                published++;
            }

            Assert.IsTrue(File.Exists(Segment(1)), "the byte cap alone must trigger rotation");
            Assert.That(new FileInfo(Segment(1)).Length, Is.GreaterThanOrEqualTo(cap));
            Assert.That(ReadLines(Segment(1)).Length, Is.LessThanOrEqualTo(4), "rotation happens as soon as the cap is crossed");
            Assert.IsFalse(File.Exists(_tempPath));
        }

        [Test]
        public void Rotation_KeepsAtMostTenSegmentsAndDropsTheOldest()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 2;

            Publish(22); // eleven rotations: the first segment (seq 1-2) is gone

            for (int n = 1; n <= EditorChangeTrackerService.MaxRotatedSegments; n++)
                Assert.IsTrue(File.Exists(Segment(n)), $"segment {n} should exist");
            Assert.IsFalse(File.Exists(Segment(EditorChangeTrackerService.MaxRotatedSegments + 1)), "an eleventh rotated segment must never appear");
            Assert.IsFalse(File.Exists(_tempPath));
            CollectionAssert.AreEqual(new long[] { 21, 22 }, Seqs(Segment(1)));
            CollectionAssert.AreEqual(new long[] { 3, 4 }, Seqs(Segment(EditorChangeTrackerService.MaxRotatedSegments)));
        }

        [Test]
        public void Rotation_LeavesTheInMemoryRingAndReadChangesUntouched()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 2;

            Publish(23);
            var result = ToJson(EditorSkills.EditorGetChanges(since: 0, limit: EditorChangeTrackerService.MaxReadLimit));

            Assert.AreEqual(23, result.Value<int>("returned"));
            Assert.AreEqual(23, result.Value<long>("cursor"));
            Assert.AreEqual(1, result.Value<long>("oldestSeq"));
            Assert.IsFalse(result.Value<bool>("dropped"));
            Assert.AreEqual(EditorChangeTrackerService.BufferCapacity, result.Value<int>("retention"));
        }

        [Test]
        public void Persist_NeverRewritesTheActiveSegment()
        {
            int total = EditorChangeTrackerService.BufferCapacity + 150;
            Publish(total);

            // The old journal rewrote the file down to the newest 500 entries every 100 overflows.
            CollectionAssert.AreEqual(Enumerable.Range(1, total).Select(i => (long)i).ToArray(), Seqs(_tempPath));
            Assert.IsFalse(File.Exists(Segment(1)));
        }

        [Test]
        public void Rotation_WhenTheTargetSlotIsBlocked_KeepsAppendingAndRetriesOnTheNextAppend()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 3;
            Directory.CreateDirectory(Segment(1)); // makes the final rename fail

            Assert.DoesNotThrow(() => Publish(5));
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, Seqs(_tempPath), "a failed rotation must not lose or skip entries");

            Directory.Delete(Segment(1));
            Publish(1);

            Assert.IsFalse(File.Exists(_tempPath), "the append after the obstacle is gone retries the rotation");
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, Seqs(Segment(1)));
        }

        [Test]
        public void Rotation_WhenAShiftFails_LeavesExistingSegmentsUntouched()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 3;
            const string marker = "{\"seq\":-1}\n";
            File.WriteAllText(Segment(1), marker);
            Directory.CreateDirectory(Segment(2)); // the shift .1 -> .2 cannot happen

            Assert.DoesNotThrow(() => Publish(4));

            Assert.AreEqual(marker, File.ReadAllText(Segment(1)), "an existing segment is never overwritten");
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, Seqs(_tempPath));
        }

        [Test]
        public void Rotation_ClosesAGapLeftByAnEarlierFailedShiftWithoutDroppingSegments()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 2;
            File.WriteAllText(Segment(1), "A\n");
            File.WriteAllText(Segment(2), "B\n");
            File.WriteAllText(Segment(4), "D\n"); // .3 is missing

            Publish(2);

            CollectionAssert.AreEqual(new long[] { 1, 2 }, Seqs(Segment(1)));
            Assert.AreEqual("A\n", File.ReadAllText(Segment(2)));
            Assert.AreEqual("B\n", File.ReadAllText(Segment(3)));
            Assert.AreEqual("D\n", File.ReadAllText(Segment(4)), "segments above the gap stay where they were");
        }

        [Test]
        public void Load_ContinuesSeqFromTheNewestRotatedSegmentWhenTheActiveOneIsAbsent()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 5;
            Publish(5); // rotated right away: no active file on disk
            Assert.IsFalse(File.Exists(_tempPath));

            EditorChangeTrackerService.ReloadForTests();
            Assert.AreEqual(5, ToJson(EditorSkills.EditorGetChanges()).Value<long>("cursor"));

            Publish(1);
            CollectionAssert.AreEqual(new long[] { 6 }, Seqs(_tempPath), "seq must not restart after a reload that follows a rotation");
        }

        [Test]
        public void Load_FillsTheRingFromRotatedSegmentsInSeqOrder()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 5;
            Publish(12); // .2 = 1-5, .1 = 6-10, active = 11-12

            EditorChangeTrackerService.ReloadForTests();
            var result = ToJson(EditorSkills.EditorGetChanges(since: 0, limit: EditorChangeTrackerService.MaxReadLimit));

            Assert.AreEqual(12, result.Value<long>("cursor"));
            Assert.AreEqual(1, result.Value<long>("oldestSeq"));
            Assert.IsFalse(result.Value<bool>("dropped"));
            CollectionAssert.AreEqual(Enumerable.Range(1, 12).Select(i => (long)i).ToArray(),
                result["changes"].Select(c => c.Value<long>("seq")).ToArray());
        }

        [Test]
        public void Load_RingKeepsOnlyTheNewestBufferCapacityEntriesAcrossSegments()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 100;
            Publish(1100); // eleven rotations: seq 1-100 dropped, 101-1100 on disk

            EditorChangeTrackerService.ReloadForTests();
            var result = ToJson(EditorSkills.EditorGetChanges(since: 0, limit: EditorChangeTrackerService.MaxReadLimit));
            var seqs = result["changes"].Select(c => c.Value<long>("seq")).ToArray();

            Assert.AreEqual(1100, result.Value<long>("cursor"));
            Assert.AreEqual(EditorChangeTrackerService.BufferCapacity, seqs.Length);
            Assert.AreEqual(601, seqs.First());
            Assert.AreEqual(1100, seqs.Last());
            Assert.AreEqual(601, result.Value<long>("oldestSeq"));
            Assert.IsTrue(result.Value<bool>("dropped"));
        }

        [Test]
        public void Load_CountsTheActiveSegmentSoARotationStillFiresAfterAReload()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 5;
            Publish(3);

            EditorChangeTrackerService.ReloadForTests();
            Publish(2);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, Seqs(Segment(1)));
            Assert.IsFalse(File.Exists(_tempPath));
        }

        [Test]
        public void Load_SkipsUnparseableLinesButKeepsTheHighestSeq()
        {
            File.WriteAllText(_tempPath,
                "{\"seq\":7,\"type\":\"file_changes\",\"source\":\"editor\",\"payload\":{}}\n" +
                "\n" +
                "{\"seq\":8,\"type\":\"file_changes\",\"source\":\"editor\",\"payload\":{}}\n" +
                "{\"seq\":9,\"type\":\"file_chan");

            EditorChangeTrackerService.ReloadForTests();
            var result = ToJson(EditorSkills.EditorGetChanges());

            Assert.AreEqual(8, result.Value<long>("cursor"));
            Assert.AreEqual(2, result.Value<int>("returned"));
        }

        [Test]
        public void ReadSince_FromTheRing_ReturnsTheLaterEntriesOldestFirst()
        {
            Publish(3);

            var entries = EditorChangeTrackerService.ReadSince(1, out string coverage);

            Assert.AreEqual("full", coverage);
            CollectionAssert.AreEqual(new long[] { 2, 3 }, entries.Select(entry => entry.Value<long>("seq")).ToArray());
        }

        [Test]
        public void ReadSince_CursorAheadOfTheJournal_IsUnknownAndEmpty()
        {
            Publish(3);

            var entries = EditorChangeTrackerService.ReadSince(10, out string coverage);

            Assert.AreEqual("unknown", coverage);
            Assert.That(entries, Is.Empty);
        }

        [Test]
        public void ReadSince_ZeroCursor_IsUnknownAndReturnsTheRing()
        {
            Publish(3);

            var entries = EditorChangeTrackerService.ReadSince(0, out string coverage);

            Assert.AreEqual("unknown", coverage);
            Assert.AreEqual(3, entries.Count);
        }

        [Test]
        public void ReadSince_CursorOlderThanTheRingButInTheSegments_IsFull()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 100;
            Publish(1200);

            var entries = EditorChangeTrackerService.ReadSince(300, out string coverage);

            Assert.AreEqual("full", coverage, "The segments reach back to seq 301, which the ring of 500 no longer holds.");
            Assert.AreEqual(900, entries.Count);
            Assert.AreEqual(301, entries[0].Value<long>("seq"));
            Assert.AreEqual(1200, entries[entries.Count - 1].Value<long>("seq"));
        }

        [Test]
        public void ReadSince_CursorBeforeTheKeptSegments_IsPartial()
        {
            EditorChangeTrackerService.OverrideSegmentMaxEntriesForTests = 100;
            Publish(1200);

            var entries = EditorChangeTrackerService.ReadSince(50, out string coverage);

            Assert.AreEqual("partial", coverage, "Rotation dropped the segments that held seq 51 to 200.");
            Assert.AreEqual(201, entries[0].Value<long>("seq"));
        }

        private static void Publish(int count)
        {
            for (int i = 0; i < count; i++)
                EditorChangeTrackerService.PublishForTests("rotation_probe", "editor");
        }

        private static string Segment(int n) => EditorChangeTrackerService.SegmentPath(n);

        private static string[] ReadLines(string path) =>
            File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();

        private static long[] Seqs(string path) =>
            ReadLines(path).Select(line => JObject.Parse(line).Value<long>("seq")).ToArray();

        private static JObject ToJson(object value)
        {
            return JObject.Parse(JsonConvert.SerializeObject(value));
        }
    }
}

// Producer:Betsy
