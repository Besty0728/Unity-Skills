using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Shared plumbing for the dryRun policy fixtures. The machine-wide permission prefs these tests move (mode, panel
    /// approval, Allowlist) are saved once and restored exactly, as SkillsModeManagerTests does; the policy and
    /// RequireConfirmation only ever go through their test seams, never real EditorPrefs. Every test starts in Bypass mode
    /// on an empty scene with the full surface profile, so the only gate in play is the one under test.
    /// </summary>
    internal sealed class DryRunPolicyTestHarness
    {
        private static readonly string[] StringPrefKeys =
            { "UnitySkills_OperatingMode", "UnitySkills_AllowlistSkills", "UnitySkills_GrantedSkills" };
        private static readonly string[] BoolPrefKeys =
            { "UnitySkills_PanelApprovalRequired", "UnitySkills_AllowlistMigratedFromGranted" };

        private readonly bool[] _hadString = new bool[StringPrefKeys.Length];
        private readonly string[] _savedString = new string[StringPrefKeys.Length];
        private readonly bool[] _hadBool = new bool[BoolPrefKeys.Length];
        private readonly bool[] _savedBool = new bool[BoolPrefKeys.Length];
        private SurfaceProfileKind _savedProfile;

        public void OneTimeSetUp()
        {
            for (int i = 0; i < StringPrefKeys.Length; i++)
            {
                _hadString[i] = EditorPrefs.HasKey(StringPrefKeys[i]);
                _savedString[i] = EditorPrefs.GetString(StringPrefKeys[i], string.Empty);
            }
            for (int i = 0; i < BoolPrefKeys.Length; i++)
            {
                _hadBool[i] = EditorPrefs.HasKey(BoolPrefKeys[i]);
                _savedBool[i] = EditorPrefs.GetBool(BoolPrefKeys[i], false);
            }
        }

        public void OneTimeTearDown()
        {
            for (int i = 0; i < StringPrefKeys.Length; i++)
            {
                if (_hadString[i]) EditorPrefs.SetString(StringPrefKeys[i], _savedString[i]);
                else EditorPrefs.DeleteKey(StringPrefKeys[i]);
            }
            for (int i = 0; i < BoolPrefKeys.Length; i++)
            {
                if (_hadBool[i]) EditorPrefs.SetBool(BoolPrefKeys[i], _savedBool[i]);
                else EditorPrefs.DeleteKey(BoolPrefKeys[i]);
            }
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            // The allowlist is cached in memory; drop the cache so the next read sees the restored pref.
            typeof(SkillsModeManager).GetField("_allowlist", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
            SkillsModeManager.CompleteTestPreferenceRecovery();
        }

        public void SetUp(DryRunPolicy policy)
        {
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = false;
            SkillsAuditLog.ResetForTests();
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Bypass;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = false;
            DryRunPolicyService.OverrideForTests = policy;
            DryRunPolicyService.UtcNowOverrideForTests = null;
            DryRunPolicyService.ResetTokensForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        public void TearDown()
        {
            DryRunPolicyService.OverrideForTests = null;
            DryRunPolicyService.UtcNowOverrideForTests = null;
            DryRunPolicyService.ResetTokensForTests();
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
            SkillsSurfaceProfile.Current = _savedProfile;
            SkillsModeManager.ClearOneShotBypass();
            SkillsModeManager.ResetForTests();
            SkillsModeManager.ExistingInstallOverrideForTests = null;
            SkillsAuditLog.ResetForTests();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObjectFinder.InvalidateCache();
        }

        /// <summary>A scene object created directly, so making the fixture never passes through a gate.</summary>
        public static GameObject Spawn(string name)
        {
            var go = new GameObject(name);
            GameObjectFinder.InvalidateCache();
            return go;
        }

        public static bool Exists(string name) => GameObject.Find(name) != null;

        /// <summary>
        /// Drives the real main-thread handler (SkillsHttpServer.ProcessJob), as SkillsBatchWireTests does, then drops the finder
        /// cache the way RunJob does after every POST.
        /// </summary>
        public static (int StatusCode, string Json) Send(string httpMethod, string path, string query, string body)
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

        public static JObject Post(string path, string query, string body)
        {
            var (_, json) = Send("POST", path, query, body);
            return JObject.Parse(json);
        }

        public static string TokenOf(JObject response)
        {
            var token = response["details"]?["dryRunToken"]?.ToString() ?? response["dryRunToken"]?.ToString();
            Assert.That(token, Is.Not.Null.And.Not.Empty, response.ToString(Formatting.None));
            return token;
        }

        public static JObject LastAudit(string type, Func<JObject, bool> match = null)
        {
            return SkillsAuditLog.ReadRecent(50).OfType<JObject>()
                .LastOrDefault(e => e["type"]?.ToString() == type && (match == null || match(e)));
        }
    }

    /// <summary>
    /// The dryRun policy gate on POST /skill/{name}: what it gates, what it never touches, where it sits among the other gates,
    /// and the token's single-use / TTL / argument binding. Driven through the real handler so the query-string token, the
    /// explicit ?mode=dryRun issuance and the retry URL are covered end to end.
    /// </summary>
    [TestFixture]
    public class DryRunPolicyGateTests
    {
        private readonly DryRunPolicyTestHarness _harness = new DryRunPolicyTestHarness();

        [OneTimeSetUp] public void OneTimeSetUp() => _harness.OneTimeSetUp();
        [OneTimeTearDown] public void OneTimeTearDown() => _harness.OneTimeTearDown();
        [SetUp] public void SetUp() => _harness.SetUp(DryRunPolicy.Off);
        [TearDown] public void TearDown() => _harness.TearDown();

        private static string Body(string name) => new JObject { ["name"] = name }.ToString(Formatting.None);

        // ---------- off: nothing changes ----------

        [Test]
        public void Off_HandlerResponsesAreByteIdenticalToTheUngatedRouter()
        {
            DryRunPolicyTestHarness.Spawn("OffProbe");
            var calls = new (string Skill, string Body)[]
            {
                ("scene_get_info", "{}"),
                ("gameobject_find", "{\"name\":\"NoSuchObjectForDryRunTests\"}"),
                ("gameobject_create", "{\"bogus\":1}"),
                ("gameobject_delete", "{\"name\":\"NoSuchObjectForDryRunTests\"}"),
            };

            foreach (var (skill, body) in calls)
            {
                var (_, viaHandler) = DryRunPolicyTestHarness.Send("POST", "/skill/" + skill, "?dryRunToken=unused", body);
                Assert.That(viaHandler, Is.EqualTo(SkillRouter.Execute(skill, body)), skill);
            }

            foreach (var wire in new[] { 1, 2 })
            {
                var query = wire == 2 ? "?mode=dryRun&wire=v2" : "?mode=dryRun";
                var (_, preview) = DryRunPolicyTestHarness.Send("POST", "/skill/gameobject_delete", query, Body("OffProbe"));
                Assert.That(preview, Is.EqualTo(SkillRouter.DryRun("gameobject_delete", Body("OffProbe"), wire)), "wire v" + wire);
                Assert.That(JObject.Parse(preview).Property("dryRunToken"), Is.Null);
            }
            Assert.That(DryRunPolicyTestHarness.Exists("OffProbe"), Is.True);
        }

        [Test]
        public void Policy_UngatedCallsStayByteIdentical()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var (_, read) = DryRunPolicyTestHarness.Send("POST", "/skill/scene_get_info", "", "{}");
            Assert.That(read, Is.EqualTo(SkillRouter.Execute("scene_get_info", "{}")), "read-only skills are never gated");

            var (_, readPreview) = DryRunPolicyTestHarness.Send("POST", "/skill/scene_get_info", "?mode=dryRun", "{}");
            Assert.That(readPreview, Is.EqualTo(SkillRouter.DryRun("scene_get_info", "{}")));

            // An invalid preview never carries a token: a call that fails validation can not run anyway.
            var (_, invalidPreview) = DryRunPolicyTestHarness.Send("POST", "/skill/gameobject_create", "?mode=dryRun", "{}");
            Assert.That(JObject.Parse(invalidPreview)["valid"]?.Value<bool>(), Is.False, invalidPreview);
            Assert.That(invalidPreview, Is.EqualTo(SkillRouter.DryRun("gameobject_create", "{}")));
        }

        // ---------- highRisk / allWrites ----------

        [Test]
        public void HighRisk_DeleteWithoutToken_ReturnsPreviewAndTokenAndRunsNothing()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            DryRunPolicyTestHarness.Spawn("GateProbe");

            var (status, json) = DryRunPolicyTestHarness.Send("POST", "/skill/gameobject_delete", "?expectProject=Probe", Body("GateProbe"));
            var response = JObject.Parse(json);

            Assert.That(status, Is.EqualTo(200));
            Assert.That(response["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"), json);
            Assert.That(response["retryStrategy"]?.ToString(), Is.EqualTo(SkillErrorResponse.RetryConfirmAndRetry));
            Assert.That(response["retryAfterSeconds"]?.Value<int>(), Is.EqualTo(0));
            var details = (JObject)response["details"];
            Assert.That(details["policy"]?.ToString(), Is.EqualTo("highRisk"));
            Assert.That(details["reason"]?.ToString(), Is.EqualTo("missingToken"));
            Assert.That(details["ttlSeconds"]?.Value<int>(), Is.EqualTo(300));
            Assert.That(details["dryRun"]?["status"]?.ToString(), Is.EqualTo("dryRun"));
            Assert.That(details["dryRun"]?["wire"]?.ToString(), Is.EqualTo("v2"));
            Assert.That(details["dryRun"]?["valid"]?.Value<bool>(), Is.True);
            var token = DryRunPolicyTestHarness.TokenOf(response);
            Assert.That(token, Has.Length.EqualTo(22));
            StringAssert.Contains($"POST /skill/gameobject_delete?dryRunToken={token}&expectProject=Probe",
                response["suggestedFixes"]?[0]?["reason"]?.ToString());
            Assert.That(DryRunPolicyTestHarness.Exists("GateProbe"), Is.True, "DRYRUN_REQUIRED must mean nothing ran.");

            var executed = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", $"?dryRunToken={token}&expectProject=Probe", Body("GateProbe"));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("GateProbe"), Is.False);
        }

        [Test]
        public void HighRisk_DoesNotGateReadsOrOrdinaryWrites()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;

            Assert.That(DryRunPolicyTestHarness.Post("/skill/scene_get_info", "", "{}")["status"]?.ToString(), Is.EqualTo("success"));
            var created = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("PlainWrite"));
            Assert.That(created["status"]?.ToString(), Is.EqualTo("success"), created.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("PlainWrite"), Is.True);
        }

        [Test]
        public void AllWrites_GatesASimpleWrite_AndTheDryRunTokenExecutesIt()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var refused = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("AllWritesProbe"));
            Assert.That(refused["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"), refused.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("AllWritesProbe"), Is.False);

            foreach (var query in new[] { "?mode=dryRun", "?mode=dryRun&wire=v2" })
            {
                var preview = DryRunPolicyTestHarness.Post("/skill/gameobject_create", query, Body("AllWritesProbe"));
                var names = preview.Properties().Select(p => p.Name).ToList();
                Assert.That(names.IndexOf("dryRunToken"), Is.EqualTo(names.IndexOf("valid") + 1), query + ": token sits right after valid");
                Assert.That(DryRunPolicyTestHarness.Exists("AllWritesProbe"), Is.False);
            }

            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?mode=dryRun", Body("AllWritesProbe")));
            var executed = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("AllWritesProbe"));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("AllWritesProbe"), Is.True);
        }

        // ---------- the token ----------

        [Test]
        public void Token_IsSingleUse_AndTheRefusalCarriesAFreshOne()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("SingleUse")));

            Assert.That(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("SingleUse"))["status"]?.ToString(),
                Is.EqualTo("success"));
            var replay = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("SingleUse"));

            Assert.That(replay["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"));
            Assert.That(replay["details"]?["reason"]?.ToString(), Is.EqualTo("tokenUnknownOrUsed"));
            Assert.That(DryRunPolicyTestHarness.TokenOf(replay), Is.Not.EqualTo(token));
            Assert.That(SceneManager.GetActiveScene().GetRootGameObjects().Count(go => go.name == "SingleUse"), Is.EqualTo(1));
        }

        [Test]
        public void Token_ExpiresAfterItsTtl()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var issuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DryRunPolicyService.UtcNowOverrideForTests = () => issuedAt;
            var early = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("TtlEarly")));
            var late = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("TtlLate")));

            DryRunPolicyService.UtcNowOverrideForTests = () => issuedAt.AddSeconds(299);
            Assert.That(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + early, Body("TtlEarly"))["status"]?.ToString(),
                Is.EqualTo("success"));

            DryRunPolicyService.UtcNowOverrideForTests = () => issuedAt.AddSeconds(301);
            var expired = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + late, Body("TtlLate"));
            Assert.That(expired["details"]?["reason"]?.ToString(), Is.EqualTo("tokenExpired"), expired.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("TtlLate"), Is.False);
        }

        [Test]
        public void Token_IsBoundToTheArguments_AndAMismatchDoesNotBurnIt()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?mode=dryRun", Body("BoundA")));

            var changed = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("BoundB"));
            Assert.That(changed["details"]?["reason"]?.ToString(), Is.EqualTo("argsChanged"), changed.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("BoundB"), Is.False);

            var original = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("BoundA"));
            Assert.That(original["status"]?.ToString(), Is.EqualTo("success"), original.ToString(Formatting.None));
        }

        [Test]
        public void Token_ToleratesKeyOrderWhitespaceAndEnvelopeKeys()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?mode=dryRun",
                "{\"name\":\"Ordered\",\"primitiveType\":\"Cube\"}"));

            var executed = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token,
                "{ \"primitiveType\" : \"Cube\",\n  \"verbose\": false, \"name\": \"Ordered\" }");
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
        }

        // ---------- exemptions and precedence ----------

        [Test]
        public void RequireConfirmation_HighRiskSkill_GetsOnlyTheConfirmationChallenge()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            ConfirmationTokenService.RequireConfirmationOverrideForTests = true;
            DryRunPolicyTestHarness.Spawn("ConfirmProbe");

            var challenge = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "", Body("ConfirmProbe"));
            Assert.That(challenge["errorCode"]?.ToString(), Is.EqualTo("CONFIRMATION_REQUIRED"), challenge.ToString(Formatting.None));
            Assert.That(challenge["details"]?["dryRun"], Is.Not.Null, "The confirmation challenge already carries the preview.");
            Assert.That(DryRunPolicyTestHarness.Exists("ConfirmProbe"), Is.True);

            var confirmToken = challenge["details"]?["_confirm"]?.ToString();
            var confirmed = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "",
                new JObject { ["name"] = "ConfirmProbe", ["_confirm"] = confirmToken }.ToString(Formatting.None));
            Assert.That(confirmed["status"]?.ToString(), Is.EqualTo("success"), "No dryRun token on top of _confirm: " + confirmed.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("ConfirmProbe"), Is.False);
        }

        [Test]
        public void DryRunToken_NeverSatisfiesConfirm()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            DryRunPolicyTestHarness.Spawn("ConsentProbe");
            var dryRunToken = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "?mode=dryRun", Body("ConsentProbe")));

            ConfirmationTokenService.RequireConfirmationOverrideForTests = true;

            var asConfirm = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "",
                new JObject { ["name"] = "ConsentProbe", ["_confirm"] = dryRunToken }.ToString(Formatting.None));
            Assert.That(asConfirm["errorCode"]?.ToString(), Is.EqualTo("INVALID_TOKEN"), asConfirm.ToString(Formatting.None));

            var asQuery = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "?dryRunToken=" + dryRunToken, Body("ConsentProbe"));
            Assert.That(asQuery["errorCode"]?.ToString(), Is.EqualTo("CONFIRMATION_REQUIRED"), asQuery.ToString(Formatting.None));

            Assert.That(DryRunPolicyTestHarness.Exists("ConsentProbe"), Is.True, "Neither route may bypass the user's confirmation.");
        }

        [Test]
        public void Allowlist_DoesNotExemptFromTheGate_AndModeForbiddenAnswersFirst()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Auto;
            DryRunPolicyTestHarness.Spawn("AllowlistProbe");

            var forbidden = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "", Body("AllowlistProbe"));
            Assert.That(forbidden["errorCode"]?.ToString(), Is.EqualTo("MODE_FORBIDDEN"),
                "A call that can only end in MODE_FORBIDDEN gets that answer, not a token it cannot use: " + forbidden.ToString(Formatting.None));

            Assert.That(SkillsModeManager.AddToAllowlist("gameobject_delete"), Is.True);
            var refused = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "", Body("AllowlistProbe"));
            Assert.That(refused["errorCode"]?.ToString(), Is.EqualTo("DRYRUN_REQUIRED"), refused.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("AllowlistProbe"), Is.True);

            var executed = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "?dryRunToken=" + DryRunPolicyTestHarness.TokenOf(refused), Body("AllowlistProbe"));
            Assert.That(executed["status"]?.ToString(), Is.EqualTo("success"), executed.ToString(Formatting.None));
        }

        [Test]
        public void ApprovalMode_TokenIsConsumedBeforeTheGrant_AndTheReplayIsNotGated()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;
            SkillsModeManager.CurrentMode = SkillsOperatingMode.Approval;
            SkillsModeManager.PanelApprovalRequired = false;

            var token = DryRunPolicyTestHarness.TokenOf(DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?mode=dryRun", Body("ApprovalProbe")));
            var restricted = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + token, Body("ApprovalProbe"));
            Assert.That(restricted["errorCode"]?.ToString(), Is.EqualTo("MODE_RESTRICTED"), restricted.ToString(Formatting.None));

            var grant = DryRunPolicyTestHarness.Post("/permission/grant", "", new JObject
            {
                ["skill"] = "gameobject_create",
                ["token"] = restricted["details"]?["grantRequestToken"]?.ToString(),
            }.ToString(Formatting.None));
            Assert.That(grant["executed"]?.Value<bool>(), Is.True, grant.ToString(Formatting.None));
            Assert.That(grant["result"]?["status"]?.ToString(), Is.EqualTo("success"), grant.ToString(Formatting.None));
            Assert.That(DryRunPolicyTestHarness.Exists("ApprovalProbe"), Is.True);
        }

        [Test]
        public void ValidationAndSurfaceVerdicts_AnswerBeforeTheGate()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var unknown = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", "{\"name\":\"X\",\"bogus\":1}");
            Assert.That(unknown["errorCode"]?.ToString(), Is.EqualTo("UNKNOWN_PARAM"));
            Assert.That(unknown["details"]?["dryRunToken"], Is.Null);

            DryRunPolicyTestHarness.Spawn("GuideProbe");
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Guide;
            var excluded = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "", Body("GuideProbe"));
            Assert.That(excluded["errorCode"]?.ToString(), Is.EqualTo("SURFACE_EXCLUDED"), excluded.ToString(Formatting.None));
            var preview = DryRunPolicyTestHarness.Post("/skill/gameobject_delete", "?mode=dryRun", Body("GuideProbe"));
            Assert.That(preview.Property("dryRunToken"), Is.Null, "A hidden skill never gets a token it can not use.");
        }

        [Test]
        public void DirectRouterCalls_AreNeverGated()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var response = JObject.Parse(SkillRouter.Execute("gameobject_create", Body("PanelProbe")));
            Assert.That(response["status"]?.ToString(), Is.EqualTo("success"), "Panel, tests and replays call the router directly.");
        }

        // ---------- audit ----------

        [Test]
        public void Audit_RecordsTheRefusalAndThePass()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var refused = DryRunPolicyTestHarness.Post("/skill/gameobject_create", "", Body("AuditProbe"));
            var call = DryRunPolicyTestHarness.LastAudit("call", e => e["result"]?.ToString() == "dryRunRequired");
            Assert.That(call, Is.Not.Null, "The refusal must leave a call entry.");
            Assert.That(call["skill"]?.ToString(), Is.EqualTo("gameobject_create"));
            Assert.That(call["policy"]?.ToString(), Is.EqualTo("allWrites"));
            Assert.That(call["reason"]?.ToString(), Is.EqualTo("missingToken"));

            DryRunPolicyTestHarness.Post("/skill/gameobject_create", "?dryRunToken=" + DryRunPolicyTestHarness.TokenOf(refused), Body("AuditProbe"));
            var passed = DryRunPolicyTestHarness.LastAudit("dryrun_passed");
            Assert.That(passed, Is.Not.Null);
            Assert.That(passed["skill"]?.ToString(), Is.EqualTo("gameobject_create"));
            Assert.That(passed["tokenAgeSec"]?.Value<int>(), Is.GreaterThanOrEqualTo(0));
        }

        // ---------- dryRunPolicy on /health, /permission/status, unity_diagnose ----------

        [Test]
        public void Health_LiveAndSnapshot_ReportThePolicyAfterPanelApprovalRequired()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.HighRisk;

            var (_, liveJson) = DryRunPolicyTestHarness.Send("GET", "/health", "?live=1", null);
            AssertPolicyField(JObject.Parse(liveJson), "highRisk");

            var server = typeof(SkillsHttpServer);
            try
            {
                server.GetMethod("RefreshHealthSnapshot", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { true });
                var vitals = server.GetNestedType("HealthVitals", BindingFlags.NonPublic)
                    .GetMethod("FromSnapshot", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                var snapshotJson = (string)server.GetMethod("BuildHealthJson", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new[] { vitals, (object)false });
                AssertPolicyField(JObject.Parse(snapshotJson), "highRisk");
            }
            finally
            {
                // The live server's snapshot now mirrors the test seam; the next editor tick re-reads the real policy.
                server.GetField("_healthSnapshotDirty", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, true);
            }
        }

        [Test]
        public void PolicyChange_MarksTheHealthSnapshotDirty()
        {
            var server = typeof(SkillsHttpServer);
            if (!(bool)server.GetField("_modeHookInstalled", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null))
                Assert.Inconclusive("The REST server has not installed its change hooks in this editor session.");

            var dirty = server.GetField("_healthSnapshotDirty", BindingFlags.NonPublic | BindingFlags.Static);
            dirty.SetValue(null, false);
            DryRunPolicyService.Current = DryRunPolicy.AllWrites;
            Assert.That((bool)dirty.GetValue(null), Is.True);
        }

        [Test]
        public void PermissionStatusAndDiagnose_ReportThePolicy()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.AllWrites;

            var (_, statusJson) = DryRunPolicyTestHarness.Send("GET", "/permission/status", "", null);
            var status = JObject.Parse(statusJson);
            Assert.That(status["dryRunPolicy"]?.ToString(), Is.EqualTo("allWrites"), statusJson);

            var diagnose = JObject.Parse(SkillRouter.Execute("unity_diagnose", "{\"includeRecentJobs\":false}"));
            Assert.That(diagnose["result"]?["server"]?["dryRunPolicy"]?.ToString(), Is.EqualTo("allWrites"), diagnose.ToString(Formatting.None));
        }

        private static void AssertPolicyField(JObject health, string expected)
        {
            Assert.That(health["dryRunPolicy"]?.ToString(), Is.EqualTo(expected), health.ToString(Formatting.None));
            var names = health.Properties().Select(p => p.Name).ToList();
            Assert.That(names.IndexOf("dryRunPolicy"), Is.EqualTo(names.IndexOf("panelApprovalRequired") + 1));
        }
    }
}

// Producer:Betsy
