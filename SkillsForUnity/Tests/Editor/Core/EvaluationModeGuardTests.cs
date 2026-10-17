using System;
using System.IO;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Mode switch and conditional restore (contract 7.5) through the guard's store seam: the key and the mode live in
    /// a fake, so no settings store is touched; runner state is written into a temp project. The real store is checked
    /// against two temp projects' per-project settings at the end.
    /// </summary>
    [TestFixture]
    public class EvaluationModeGuardTests
    {
        private const string SessionId = "s20261017T030000a1b2c3";

        private sealed class FakeStore : EvaluationModeGuard.IStore
        {
            public string Key;
            public SkillsOperatingMode Mode { get; set; }
            public string ReadKey() => Key;
            public void WriteKey(string json) => Key = json;
            public void DeleteKey() => Key = null;
        }

        private string _project;
        private FakeStore _store;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "unityskills-mode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_project);
            _store = new FakeStore { Mode = SkillsOperatingMode.Auto };
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_project)) Directory.Delete(_project, true);
        }

        [Test]
        public void Arm_WritesTheKeyThenSwitchesToBypass()
        {
            var now = new DateTime(2026, 10, 17, 2, 59, 0, DateTimeKind.Utc);
            Assert.That(EvaluationModeGuard.Arm(_store, now), Is.EqualTo(EvaluationModeGuard.ArmResult.Armed));

            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
            var key = EvaluationState.ParseObject(_store.Key);
            Assert.That(key.Value<int>("schema"), Is.EqualTo(1));
            Assert.That(key.Value<string>("previousMode"), Is.EqualTo("auto"));
            Assert.That(key.Value<string>("armedAt"), Is.EqualTo("2026-10-17T02:59:00.000Z"));
            Assert.That(key["projectPath"], Is.Null, "the key lives in the project's own settings; no path is recorded");
            Assert.That(key["sessionId"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Null));
        }

        [Test]
        public void Arm_WhenAlreadyBypass_WritesNothing()
        {
            _store.Mode = SkillsOperatingMode.Bypass;
            Assert.That(EvaluationModeGuard.Arm(_store, DateTime.UtcNow), Is.EqualTo(EvaluationModeGuard.ArmResult.AlreadyBypass));
            Assert.That(_store.Key, Is.Null);
        }

        [Test]
        public void Check_BindsASessionStartedAfterArming_AndWaitsWhileItRuns()
        {
            ArmAt("2026-10-17T02:59:00.000Z");
            WriteRunnerState("active", "2026-10-17T03:00:00.000Z");

            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.Waiting));
            Assert.That(EvaluationModeGuard.Parse(_store.Key).SessionId, Is.EqualTo(SessionId));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
        }

        [Test]
        public void Check_DoesNotBindASessionOlderThanTheKey()
        {
            ArmAt("2026-10-17T04:00:00.000Z");
            WriteRunnerState("completed", "2026-10-17T03:00:00.000Z");

            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.Waiting));
            Assert.That(EvaluationModeGuard.Parse(_store.Key).SessionId, Is.Null);
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
        }

        [TestCase("completed")]
        [TestCase("aborted")]
        public void Check_RestoresWhenTheBoundSessionEnded(string status)
        {
            ArmAt("2026-10-17T02:59:00.000Z");
            WriteRunnerState("active", "2026-10-17T03:00:00.000Z");
            EvaluationModeGuard.Check(_store, _project, false);

            WriteRunnerState(status, "2026-10-17T03:00:00.000Z");
            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.Restored));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Auto));
            Assert.That(_store.Key, Is.Null);
        }

        [Test]
        public void Check_ModeChangedByHand_OnlyDropsTheKey()
        {
            ArmAt("2026-10-17T02:59:00.000Z");
            WriteRunnerState("completed", "2026-10-17T03:00:00.000Z");
            _store.Mode = SkillsOperatingMode.Approval;

            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.KeyCleared));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Approval));
            Assert.That(_store.Key, Is.Null);
        }

        [Test]
        public void EditorStore_KeepsTheKeyAndTheModeInThisProjectsSettings()
        {
            var projectA = Path.Combine(_project, "A");
            var projectB = Path.Combine(_project, "B");
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            try
            {
                using (ProjectPrefsTestScope.AtRoot(projectA))
                {
                    SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
                    Assert.That(EvaluationModeGuard.Arm(EvaluationModeGuard.Editor, DateTime.UtcNow),
                        Is.EqualTo(EvaluationModeGuard.ArmResult.Armed));
                    Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Bypass));
                    Assert.That(ProjectPrefs.HasKey(EvaluationModeGuard.PrefKey), Is.True);
                }

                using (ProjectPrefsTestScope.AtRoot(projectB))
                {
                    Assert.That(EvaluationModeGuard.Pending(EvaluationModeGuard.Editor), Is.Null);
                    Assert.That(EvaluationModeGuard.Check(EvaluationModeGuard.Editor, projectB, true),
                        Is.EqualTo(EvaluationModeGuard.CheckResult.None));
                    Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Auto), "B keeps its own mode");
                }

                using (ProjectPrefsTestScope.AtRoot(projectA))
                {
                    Assert.That(EvaluationModeGuard.Pending(EvaluationModeGuard.Editor)?.PreviousMode, Is.EqualTo("approval"));
                    // No runner session in A: "Restore now" puts the mode back.
                    Assert.That(EvaluationModeGuard.Check(EvaluationModeGuard.Editor, projectA, true),
                        Is.EqualTo(EvaluationModeGuard.CheckResult.Restored));
                    Assert.That(SkillsModeManager.CurrentMode, Is.EqualTo(SkillsOperatingMode.Approval));
                    Assert.That(ProjectPrefs.HasKey(EvaluationModeGuard.PrefKey), Is.False);
                }
            }
            finally
            {
                SkillsModeManager.ExistingInstallOverrideForTests = null;
            }
        }

        [Test]
        public void Check_RestoreNow_WorksForAnUnboundKey()
        {
            ArmAt("2026-10-17T02:59:00.000Z");

            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.Waiting));
            Assert.That(EvaluationModeGuard.Check(_store, _project, true), Is.EqualTo(EvaluationModeGuard.CheckResult.Restored));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Auto));
        }

        [Test]
        public void Check_RestoreNow_StillWaitsForAnActiveSession()
        {
            ArmAt("2026-10-17T02:59:00.000Z");
            WriteRunnerState("active", "2026-10-17T03:00:00.000Z");

            Assert.That(EvaluationModeGuard.Check(_store, _project, true), Is.EqualTo(EvaluationModeGuard.CheckResult.Waiting));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
            Assert.That(_store.Key, Is.Not.Null);
        }

        [TestCase("{not json")]
        [TestCase("{\"schema\":1,\"previousMode\":\"2\",\"armedAt\":\"2026-10-17T02:59:00.000Z\",\"sessionId\":null}")]
        [TestCase("{\"schema\":1,\"previousMode\":\"auto\",\"armedAt\":\"later\",\"sessionId\":null}")]
        [TestCase("{\"schema\":2,\"previousMode\":\"auto\",\"armedAt\":\"2026-10-17T02:59:00.000Z\",\"sessionId\":null}")]
        public void Check_UnreadableKey_IsDiscardedWithoutTouchingTheMode(string json)
        {
            _store.Key = json;
            _store.Mode = SkillsOperatingMode.Bypass;

            Assert.That(EvaluationModeGuard.Check(_store, _project, false), Is.EqualTo(EvaluationModeGuard.CheckResult.KeyDiscarded));
            Assert.That(_store.Key, Is.Null);
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
        }

        [Test]
        public void Check_NoKey_DoesNothing()
        {
            _store.Mode = SkillsOperatingMode.Bypass;
            Assert.That(EvaluationModeGuard.Check(_store, _project, true), Is.EqualTo(EvaluationModeGuard.CheckResult.None));
            Assert.That(_store.Mode, Is.EqualTo(SkillsOperatingMode.Bypass));
        }

        private void ArmAt(string armedAt)
        {
            Assert.That(EvaluationModeGuard.Arm(_store, DateTime.Parse(armedAt, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal)), Is.EqualTo(EvaluationModeGuard.ArmResult.Armed));
        }

        private void WriteRunnerState(string status, string createdAt)
        {
            var current = "{\"schema\":1,\"sessionId\":\"" + SessionId + "\",\"status\":\"" + status +
                          "\",\"activeAttemptId\":null,\"updatedAt\":\"" + createdAt + "\"}";
            var session = "{\"schema\":1,\"sessionId\":\"" + SessionId + "\",\"status\":\"" + status +
                          "\",\"createdAt\":\"" + createdAt + "\",\"order\":[],\"tasks\":{}}";
            EvaluationState.WriteTextAtomic(EvaluationState.CurrentFile(_project), current);
            EvaluationState.WriteTextAtomic(EvaluationState.SessionFile(_project, SessionId), session);
        }
    }
}

// Producer:Betsy
