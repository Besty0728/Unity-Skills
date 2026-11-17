using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Verifies GitHub Issue #68: POST /skill/editor_stop remains callable while Unity compilation is deferred
    /// during Play Mode, without loosening mode/allowlist gates, batch guards, or domain-reload teardown protection.
    /// </summary>
    [TestFixture]
    public class CompileGuardEditorStopTests
    {
        private ModePreferenceSnapshot _prefSnapshot;

        private bool? _savedCompilationOverride;
        private Func<bool> _savedPlayingForTests;
        private Action<bool> _savedSetPlayingForTests;
        private SkillsOperatingMode _savedMode;
        private bool _savedEditorStopAllowlisted;
        private SurfaceProfileKind _savedProfile;
        private DryRunPolicy? _savedDryRunPolicy;
        private bool? _savedRequireConfirmation;

        private bool _playing;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _prefSnapshot = ModePreferenceSnapshot.Capture();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _prefSnapshot?.Restore();
        }

        [SetUp]
        public void SetUp()
        {
            _savedCompilationOverride = ServerAvailabilityHelper.CompilationInProgressOverrideForTests;
            _savedPlayingForTests = EditorSkills.PlayingForTests;
            _savedSetPlayingForTests = EditorSkills.SetPlayingForTests;
            _savedMode = SkillsModeManager.CurrentMode;
            _savedEditorStopAllowlisted = SkillsModeManager.IsInAllowlist("editor_stop");
            _savedProfile = SkillsSurfaceProfile.Current;
            _savedDryRunPolicy = DryRunPolicyService.OverrideForTests;
            _savedRequireConfirmation = ConfirmationTokenService.RequireConfirmationOverrideForTests;

            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = false;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = false;
            SetDomainReloadPending(false);

            _playing = false;
            EditorSkills.PlayingForTests = () => _playing;
            EditorSkills.SetPlayingForTests = value => _playing = value;
        }

        [TearDown]
        public void TearDown()
        {
            SetDomainReloadPending(false);
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = _savedCompilationOverride;
            EditorSkills.PlayingForTests = _savedPlayingForTests;
            EditorSkills.SetPlayingForTests = _savedSetPlayingForTests;

            if (_savedEditorStopAllowlisted)
                SkillsModeManager.AddToAllowlist("editor_stop");
            else
                SkillsModeManager.RemoveFromAllowlist("editor_stop");

            SkillsModeManager.CurrentMode = _savedMode;
            SkillsSurfaceProfile.Current = _savedProfile;
            DryRunPolicyService.OverrideForTests = _savedDryRunPolicy;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = _savedRequireConfirmation;
        }

        [Test]
        public void EditorStop_WhileCompilingInPlayMode_ExecutesAndExitsPlayMode()
        {
            _playing = true;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;
            string expectInstance = Uri.EscapeDataString(RegistryService.InstanceId ?? "test_instance");

            var (statusCode, json) = ProcessRequest("POST", "/skill/editor_stop", $"?expectInstance={expectInstance}", "{}");
            var body = JObject.Parse(json);

            Assert.That(statusCode, Is.EqualTo(200), json);
            Assert.That(body["status"]?.ToString(), Is.EqualTo("success"), json);
            Assert.That(body["result"]?["mode"]?.ToString(), Is.EqualTo("stopped"), json);
            Assert.That(body["result"]?["serverAvailability"]?["isCompiling"]?.Value<bool>(), Is.True, json);
            Assert.That(_playing, Is.False);
        }

        [Test]
        public void EditorStop_CaseInsensitiveAndDryRun_AllowedWhileCompiling()
        {
            _playing = true;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;

            var (dryStatus, dryJson) = ProcessRequest("POST", "/skill/Editor_Stop", "?mode=dryRun", "{}");
            var dryBody = JObject.Parse(dryJson);

            Assert.That(dryStatus, Is.EqualTo(200), dryJson);
            Assert.That(dryBody["status"]?.ToString(), Is.EqualTo("dryRun"), dryJson);
            Assert.That(dryBody["valid"]?.Value<bool>(), Is.True, dryJson);
            Assert.That(_playing, Is.True, "dryRun must not exit Play Mode.");

            var (execStatus, execJson) = ProcessRequest("POST", "/skill/Editor_Stop", string.Empty, "{}");
            var execBody = JObject.Parse(execJson);

            Assert.That(execStatus, Is.EqualTo(200), execJson);
            Assert.That(execBody["status"]?.ToString(), Is.EqualTo("success"), execJson);
            Assert.That(execBody["result"]?["mode"]?.ToString(), Is.EqualTo("stopped"), execJson);
            Assert.That(_playing, Is.False);
        }

        [Test]
        public void EditorStop_WhileCompilingOutsideBypassWithoutAllowlist_StillEnforcesModeGate()
        {
            _playing = true;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;
            SkillsModeManager.RemoveFromAllowlist("editor_stop");

            var (statusCode, json) = ProcessRequest("POST", "/skill/editor_stop", string.Empty, "{}");
            var body = JObject.Parse(json);

            Assert.That(statusCode, Is.EqualTo(200), json);
            Assert.That(body["status"]?.ToString(), Is.EqualTo("error"), json);
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("MODE_FORBIDDEN"), json);
            Assert.That(_playing, Is.True);
        }

        [Test]
        public void OrdinarySkillsAndBatch_WhileCompilingInPlayMode_RemainRejectedWith503Compiling()
        {
            _playing = true;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = true;

            var requests = new (string Path, string Body)[]
            {
                ("/skill/gameobject_create", "{\"name\":\"CompileGuardProbe\"}"),
                ("/skill/ui_create_canvas", "{}"),
                ("/skill/editor_get_state", "{}"),
                ("/skill/editor_play", "{}"),
                ("/skills/batch", "{\"steps\":[{\"skill\":\"editor_stop\",\"args\":{}}]}"),
            };

            foreach (var (path, reqBody) in requests)
            {
                var (statusCode, json) = ProcessRequest("POST", path, string.Empty, reqBody);
                var body = JObject.Parse(json);

                Assert.That(statusCode, Is.EqualTo(503), $"{path}: {json}");
                Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("COMPILING"), $"{path}: {json}");
                Assert.That(body["details"]?["isPlaying"]?.Value<bool>(), Is.True, $"{path}: {json}");
                Assert.That(body["details"]?["isCompiling"]?.Value<bool>(), Is.True, $"{path}: {json}");
                StringAssert.Contains("editor_stop", body["details"]?["suggestion"]?.ToString(), $"{path}: {json}");
                Assert.That(_playing, Is.True, $"{path} must not exit Play Mode.");
            }
        }

        [Test]
        public void EditorStop_WhenDomainReloadPendingIsTrue_IsStillRejectedWith503Compiling()
        {
            _playing = true;
            ServerAvailabilityHelper.CompilationInProgressOverrideForTests = false;
            SetDomainReloadPending(true);

            var (statusCode, json) = ProcessRequest("POST", "/skill/editor_stop", string.Empty, "{}");
            var body = JObject.Parse(json);

            Assert.That(statusCode, Is.EqualTo(503), json);
            Assert.That(body["errorCode"]?.ToString(), Is.EqualTo("COMPILING"), json);
            Assert.That(body["details"]?["domainReloadPending"]?.Value<bool>(), Is.True, json);
            Assert.That(_playing, Is.True);
        }

        // ---------- helpers ----------

        private static FieldInfo DomainReloadPendingField()
        {
            var field = typeof(SkillsHttpServer).GetField("_domainReloadPending", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "SkillsHttpServer._domainReloadPending was renamed.");
            return field;
        }

        private static void SetDomainReloadPending(bool value) => DomainReloadPendingField().SetValue(null, value);

        private static (int StatusCode, string ResponseJson) ProcessRequest(string httpMethod, string path, string query, string body)
        {
            var jobType = typeof(SkillsHttpServer).GetNestedType("RequestJob", BindingFlags.NonPublic);
            Assert.That(jobType, Is.Not.Null, "SkillsHttpServer.RequestJob was renamed.");

            var job = Activator.CreateInstance(jobType, nonPublic: true);
            jobType.GetField("HttpMethod").SetValue(job, httpMethod);
            jobType.GetField("Path").SetValue(job, path);
            jobType.GetField("QueryString").SetValue(job, query);
            jobType.GetField("Body").SetValue(job, body);
            jobType.GetField("StatusCode").SetValue(job, 200);

            var processJob = typeof(SkillsHttpServer).GetMethod("ProcessJob", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(processJob, Is.Not.Null, "SkillsHttpServer.ProcessJob was renamed.");
            processJob.Invoke(null, new[] { job });

            if (httpMethod == "POST")
                GameObjectFinder.InvalidateCache();

            return ((int)jobType.GetField("StatusCode").GetValue(job), (string)jobType.GetField("ResponseJson").GetValue(job));
        }
    }
}

// Producer:Betsy
