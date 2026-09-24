using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Registry entry lifecycle (running / reloading / stopped) and the staleness rule that decides pruning.
    /// Every test runs against a scratch registry file through RegistryService.OverrideRegistryFilePathForTests,
    /// so the real ~/.unity_skills/registry.json of the running Editor is never touched.
    /// </summary>
    [TestFixture]
    public class RegistryStatusTests
    {
        // Process.GetProcessById never finds these, so they stand in for Editors that have exited.
        private const int DeadPid = int.MaxValue;
        private const int OtherDeadPid = int.MaxValue - 1;

        private string _tempRoot;
        private string _registryPath;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "UnitySkillsRegistryTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            _registryPath = Path.Combine(_tempRoot, "registry.json");
            RegistryService.OverrideRegistryFilePathForTests = _registryPath;
        }

        [TearDown]
        public void TearDown()
        {
            RegistryService.OverrideRegistryFilePathForTests = null;
            try { Directory.Delete(_tempRoot, true); } catch { }
        }

        // ---------- state transitions ----------

        [Test]
        public void Register_WritesARunningEntry()
        {
            RegistryService.Register(8095);

            var self = ReadSelf();
            Assert.That(self, Is.Not.Null);
            Assert.That(self.status, Is.EqualTo(RegistryService.StatusRunning));
            Assert.That(self.statusSince, Is.GreaterThan(0));
            Assert.That(self.port, Is.EqualTo(8095));
            Assert.That(self.pid, Is.EqualTo(Process.GetCurrentProcess().Id));
            Assert.That(self.id, Is.EqualTo(RegistryService.InstanceId));
        }

        [Test]
        public void MarkReloading_KeepsTheEntryWithPortAndPid()
        {
            RegistryService.Register(8095);
            RegistryService.MarkReloading(8095);

            var self = ReadSelf();
            Assert.That(self, Is.Not.Null, "A domain reload must no longer remove the entry.");
            Assert.That(self.status, Is.EqualTo(RegistryService.StatusReloading));
            Assert.That(self.port, Is.EqualTo(8095));
            Assert.That(self.pid, Is.EqualTo(Process.GetCurrentProcess().Id));
        }

        [Test]
        public void MarkReloading_WithoutAnEntry_CreatesOne()
        {
            RegistryService.MarkReloading(8096);

            var self = ReadSelf();
            Assert.That(self?.status, Is.EqualTo(RegistryService.StatusReloading),
                "A client in this project must be able to see the reload even if another writer dropped the entry.");
            Assert.That(self.port, Is.EqualTo(8096));
        }

        [Test]
        public void RegisterAfterReload_FlipsBackToRunning()
        {
            RegistryService.Register(8095);
            RegistryService.MarkReloading(8095);
            RegistryService.Register(8097);

            var self = ReadSelf();
            Assert.That(self.status, Is.EqualTo(RegistryService.StatusRunning));
            Assert.That(self.port, Is.EqualTo(8097), "A restart that fell back to another port publishes the new one.");
        }

        [Test]
        public void Heartbeat_RestoresRunning()
        {
            RegistryService.Register(8095);
            RegistryService.MarkStopped(8095);
            RegistryService.Heartbeat(8095);

            Assert.That(ReadSelf().status, Is.EqualTo(RegistryService.StatusRunning));
        }

        [Test]
        public void MarkStopped_KeepsThePortWhenGivenZero()
        {
            RegistryService.Register(8095);
            RegistryService.MarkStopped(0);

            var self = ReadSelf();
            Assert.That(self.status, Is.EqualTo(RegistryService.StatusStopped));
            Assert.That(self.port, Is.EqualTo(8095));
        }

        [Test]
        public void MarkStopped_NeverCreatesAnEntry()
        {
            File.WriteAllText(_registryPath, "{}");
            RegistryService.MarkStopped(8095);

            Assert.That(ReadSelf(), Is.Null, "A server that never registered must stay unlisted.");
        }

        [Test]
        public void Unregister_RemovesTheEntry()
        {
            RegistryService.Register(8095);
            RegistryService.MarkReloading(8095);
            RegistryService.Unregister();

            Assert.That(ReadSelf(), Is.Null);
        }

        // ---------- pruning through Register ----------

        [Test]
        public void Register_PrunesEntriesOfExitedEditors_InEveryStatus()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteRegistry(new Dictionary<string, RegistryService.InstanceInfo>
            {
                ["/gone/reloading"] = Entry("Gone_1", DeadPid, now, RegistryService.StatusReloading, now),
                ["/gone/running"] = Entry("Gone_2", OtherDeadPid, now, null, 0),
            });

            RegistryService.Register(8095);

            var registry = ReadRegistry();
            Assert.That(registry.ContainsKey("/gone/reloading"), Is.False, "A reloading entry whose process is gone is dead.");
            Assert.That(registry.ContainsKey("/gone/running"), Is.False);
            Assert.That(registry.ContainsKey(RegistryService.ProjectPath), Is.True);
        }

        // ---------- the staleness rule ----------

        [Test]
        public void ShouldPrune_RunningEntries_FollowTheHeartbeatRule()
        {
            long now = 1_000_000;
            Assert.That(Prune(Entry("A", 1, now - 10, RegistryService.StatusRunning, now - 10), now, alive: true), Is.False);
            Assert.That(Prune(Entry("A", 1, now - 121, RegistryService.StatusRunning, now - 121), now, alive: true), Is.True,
                "A running entry without a heartbeat for over 120 s is stale even while its pid lives.");
            Assert.That(Prune(Entry("A", 1, now - 10, RegistryService.StatusRunning, now - 10), now, alive: false), Is.True);
        }

        [Test]
        public void ShouldPrune_EntriesWithoutStatus_AreRunning()
        {
            long now = 1_000_000;
            Assert.That(Prune(Entry("Old", 1, now - 500, null, 0), now, alive: true), Is.True);
            Assert.That(Prune(Entry("Future", 1, now - 500, "starting", now), now, alive: true), Is.True,
                "An unknown status falls back to the heartbeat rule.");
        }

        [TestCase(RegistryService.StatusReloading)]
        [TestCase(RegistryService.StatusStopped)]
        public void ShouldPrune_NonRunningEntries_LiveAsLongAsTheirProcess(string status)
        {
            long now = 1_000_000;
            var entry = Entry("R", 1, now - 600, status, now - 600);

            Assert.That(Prune(entry, now, alive: true), Is.False,
                "No heartbeat during a reload is expected; a live pid keeps the entry.");
            Assert.That(Prune(entry, now, alive: false), Is.True);
        }

        [Test]
        public void ShouldPrune_ReusedPid_RetiresOnlyPastTheWindow()
        {
            long now = 10_000_000;
            long window = RegistryService.NonRunningReuseCheckSeconds;
            var recent = Entry("R", 1, now - 60, RegistryService.StatusReloading, now - 60);
            var old = Entry("R", 1, now - window - 1, RegistryService.StatusReloading, now - window - 1);

            Assert.That(Prune(recent, now, alive: true, reused: true), Is.False,
                "Inside the window the reuse heuristic is never consulted.");
            Assert.That(Prune(old, now, alive: true, reused: false), Is.False,
                "Past the window a pid that still looks like Unity keeps the entry.");
            Assert.That(Prune(old, now, alive: true, reused: true), Is.True);
        }

        [Test]
        public void ShouldPrune_WithoutStatusSince_AgesFromTheLastHeartbeat()
        {
            long now = 10_000_000;
            long window = RegistryService.NonRunningReuseCheckSeconds;
            var entry = Entry("R", 1, now - window - 1, RegistryService.StatusStopped, 0);

            Assert.That(Prune(entry, now, alive: true, reused: true), Is.True);
        }

        // ---------- read-only view for error payloads ----------

        [Test]
        public void GetLiveInstances_AppliesTheRule_AndLeavesOutSelf()
        {
            int livePid = Process.GetCurrentProcess().Id;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            WriteRegistry(new Dictionary<string, RegistryService.InstanceInfo>
            {
                [RegistryService.ProjectPath] = Entry(RegistryService.InstanceId, livePid, now, RegistryService.StatusRunning, now),
                ["/other/running"] = Entry("Running_1", livePid, now, RegistryService.StatusRunning, now),
                ["/other/reloading"] = Entry("Reloading_1", livePid, now - 600, RegistryService.StatusReloading, now - 600),
                ["/other/stale"] = Entry("Stale_1", livePid, now - 600, RegistryService.StatusRunning, now - 600),
                ["/other/dead"] = Entry("Dead_1", DeadPid, now, RegistryService.StatusRunning, now),
            });

            var ids = new List<string>();
            foreach (var instance in RegistryService.GetLiveInstances())
                ids.Add(instance.id);

            Assert.That(ids, Is.EquivalentTo(new[] { "Running_1", "Reloading_1" }));
            Assert.That(File.Exists(_registryPath), Is.True);
            Assert.That(ReadRegistry().Count, Is.EqualTo(5), "GetLiveInstances only reads; it never prunes or rewrites.");
        }

        [Test]
        public void GetLiveInstances_WithoutARegistryFile_IsEmpty()
        {
            Assert.That(RegistryService.GetLiveInstances(), Is.Empty);
        }

        [Test]
        public void OldEntries_WithoutStatusFields_StillDeserialize()
        {
            const string legacy = "{\"/p/Legacy\":{\"id\":\"Legacy_1\",\"name\":\"Legacy\",\"path\":\"/p/Legacy\",\"port\":8090," +
                                  "\"pid\":1,\"last_active\":100,\"unityVersion\":\"2022.3.0f1\",\"cliBound\":false,\"cliPath\":null}}";

            var entry = JsonConvert.DeserializeObject<Dictionary<string, RegistryService.InstanceInfo>>(legacy)["/p/Legacy"];

            Assert.That(entry.status, Is.Null);
            Assert.That(entry.statusSince, Is.EqualTo(0));
            Assert.That(RegistryService.IsRunningStatus(entry.status), Is.True);
        }

        // ---------- helpers ----------

        private static RegistryService.InstanceInfo Entry(string id, int pid, long lastActive, string status, long statusSince) =>
            new RegistryService.InstanceInfo
            {
                id = id,
                name = id,
                pid = pid,
                port = 8099,
                last_active = lastActive,
                status = status,
                statusSince = statusSince,
            };

        private static bool Prune(RegistryService.InstanceInfo entry, long now, bool alive, bool reused = false) =>
            RegistryService.ShouldPrune(entry, now, _ => alive, _ => reused);

        private void WriteRegistry(Dictionary<string, RegistryService.InstanceInfo> registry) =>
            File.WriteAllText(_registryPath, JsonConvert.SerializeObject(registry, Formatting.Indented));

        private Dictionary<string, RegistryService.InstanceInfo> ReadRegistry()
        {
            Assert.That(File.Exists(_registryPath), Is.True, "The scratch registry was never written.");
            return JsonConvert.DeserializeObject<Dictionary<string, RegistryService.InstanceInfo>>(File.ReadAllText(_registryPath))
                   ?? new Dictionary<string, RegistryService.InstanceInfo>();
        }

        private RegistryService.InstanceInfo ReadSelf() =>
            ReadRegistry().TryGetValue(RegistryService.ProjectPath, out var self) ? self : null;
    }
}

// Producer:Betsy
