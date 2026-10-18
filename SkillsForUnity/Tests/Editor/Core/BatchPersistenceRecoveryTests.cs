using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// batch_state.json is swapped in whole, keeps its previous generation as .bak, and a load of an unreadable file falls
    /// back to that generation instead of silently starting with no jobs. Every test runs on a scratch directory through
    /// BatchPersistence.BeginTestScope, so the editor's real job table is never touched.
    /// </summary>
    [TestFixture]
    public class BatchPersistenceRecoveryTests
    {
        private string _dir;
        private IDisposable _scope;

        private string StatePath => Path.Combine(_dir, "batch_state.json");

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "UnitySkillsBatchState_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _scope = BatchPersistence.BeginTestScope(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            ImportWorkerGate.OverrideForTests = null;
            _scope?.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        }

        private static BatchJobRecord Job(string id, string status) => new BatchJobRecord
        {
            jobId = id,
            kind = "recovery_probe",
            status = status,
            currentStage = status,
            startedAt = 1,
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        [Test]
        public void Save_SwapsTheWholeDocumentAndKeepsThePreviousGeneration()
        {
            BatchPersistence.UpsertJob(Job("first", "completed"));
            BatchPersistence.Save();
            BatchPersistence.UpsertJob(Job("second", "completed"));
            BatchPersistence.Save();

            Assert.IsFalse(File.Exists(StatePath + LibraryStateFile.TempSuffix));
            Assert.AreEqual(2, JObject.Parse(File.ReadAllText(StatePath))["jobs"].Count());
            Assert.AreEqual(1, JObject.Parse(File.ReadAllText(StatePath + ".bak"))["jobs"].Count());
        }

        [Test]
        public void Load_TruncatedState_RestoresThePreviousGenerationAndFailsItsUnfinishedJobs()
        {
            BatchPersistence.UpsertJob(Job("done", "completed"));
            BatchPersistence.UpsertJob(Job("unfinished", "running"));
            BatchPersistence.Save();
            BatchPersistence.UpsertJob(Job("newest", "completed"));
            BatchPersistence.Save();
            string current = File.ReadAllText(StatePath);
            string damaged = current.Substring(0, current.Length / 2);
            File.WriteAllText(StatePath, damaged);

            BatchPersistence.ReloadForTests();

            Assert.AreEqual("completed", BatchPersistence.GetJob("done")?.status, "Finished jobs keep their results.");
            var unfinished = BatchPersistence.GetJob("unfinished");
            Assert.AreEqual("failed", unfinished?.status, "A job restored from an older generation is not resumed.");
            StringAssert.Contains("previous generation", unfinished.error);
            Assert.IsNull(BatchPersistence.GetJob("newest"), "Only the previous generation was readable.");
            Assert.AreEqual(damaged, File.ReadAllText(StatePath + ".corrupt"), "The unreadable bytes are kept for diagnosis.");
            Assert.AreEqual(2, JObject.Parse(File.ReadAllText(StatePath))["jobs"].Count(), "The restored generation is written back at once.");
            Assert.AreEqual(2, JObject.Parse(File.ReadAllText(StatePath + ".bak"))["jobs"].Count(),
                "The damaged file must not be rotated into the backup slot.");
        }

        [Test]
        public void Load_EmptyState_IsDamageNotAnEmptyJobTable()
        {
            BatchPersistence.UpsertJob(Job("kept", "completed"));
            BatchPersistence.Save();
            BatchPersistence.Save();
            File.WriteAllText(StatePath, string.Empty);

            BatchPersistence.ReloadForTests();

            Assert.AreEqual("completed", BatchPersistence.GetJob("kept")?.status);
        }

        [Test]
        public void Load_UnreadableStateWithoutBackup_StartsEmptyAndKeepsTheBytes()
        {
            const string damaged = "{\"jobs\":[{\"jobId\":\"x\",";
            File.WriteAllText(StatePath, damaged);

            BatchPersistence.ReloadForTests();

            Assert.AreEqual(0, BatchPersistence.ListJobs(10).Length);
            Assert.AreEqual(damaged, File.ReadAllText(StatePath + ".corrupt"));
        }

        [Test]
        public void Load_KillBetweenTheSwapRenames_UsesTheCompleteTempGeneration()
        {
            BatchPersistence.UpsertJob(Job("in-flight", "completed"));
            BatchPersistence.Save();
            // The current name is gone and the next generation sits, complete, under the temp name.
            File.Move(StatePath, StatePath + LibraryStateFile.TempSuffix);

            BatchPersistence.ReloadForTests();

            Assert.AreEqual("completed", BatchPersistence.GetJob("in-flight")?.status);
        }

        [Test]
        public void ImportWorker_NeverReadsOrWritesTheEditorsJobTable()
        {
            BatchPersistence.UpsertJob(Job("editor-job", "completed"));
            BatchPersistence.Save();
            string before = File.ReadAllText(StatePath);

            ImportWorkerGate.OverrideForTests = true;
            BatchPersistence.ReloadForTests();
            Assert.IsNull(BatchPersistence.GetJob("editor-job"), "A worker starts with an empty table instead of parsing the editor's file.");
            BatchPersistence.UpsertJob(Job("worker-job", "completed"));
            BatchPersistence.Save();

            Assert.AreEqual(before, File.ReadAllText(StatePath));
            Assert.IsFalse(File.Exists(StatePath + LibraryStateFile.TempSuffix));
        }
    }
}

// Producer:Betsy
