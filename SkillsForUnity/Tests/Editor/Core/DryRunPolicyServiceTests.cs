using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// DryRunPolicyService on its own: wire values, the per-project pref key, which skills each policy gates (a truth table on
    /// constructed skills plus invariants over the live registry, with no counts hardcoded), the canonical argument hashes and
    /// the one-time token store. The policy and RequireConfirmation go through their test seams only.
    /// </summary>
    [TestFixture]
    public class DryRunPolicyServiceTests
    {
        [SetUp]
        public void SetUp()
        {
            DryRunPolicyService.OverrideForTests = DryRunPolicy.Off;
            DryRunPolicyService.UtcNowOverrideForTests = null;
            DryRunPolicyService.ResetTokensForTests();
            ConfirmationTokenService.RequireConfirmationOverrideForTests = false;
        }

        [TearDown]
        public void TearDown()
        {
            DryRunPolicyService.OverrideForTests = null;
            DryRunPolicyService.UtcNowOverrideForTests = null;
            DryRunPolicyService.ResetTokensForTests();
            ConfirmationTokenService.RequireConfirmationOverrideForTests = null;
        }

        // ---------- wire values and persistence ----------

        [Test]
        public void WireValues_RoundTrip()
        {
            foreach (DryRunPolicy policy in Enum.GetValues(typeof(DryRunPolicy)))
            {
                Assert.That(DryRunPolicyService.TryParseWire(DryRunPolicyService.ToWire(policy), out var parsed), Is.True, policy.ToString());
                Assert.That(parsed, Is.EqualTo(policy));
            }
            Assert.That(DryRunPolicyService.ToWire(DryRunPolicy.Off), Is.EqualTo("off"));
            Assert.That(DryRunPolicyService.ToWire(DryRunPolicy.HighRisk), Is.EqualTo("highRisk"));
            Assert.That(DryRunPolicyService.ToWire(DryRunPolicy.AllWrites), Is.EqualTo("allWrites"));
        }

        [Test]
        public void TryParseWire_IsCaseInsensitive_AndReadsAnythingElseAsOff()
        {
            Assert.That(DryRunPolicyService.TryParseWire(" HIGHRISK ", out var upper), Is.True);
            Assert.That(upper, Is.EqualTo(DryRunPolicy.HighRisk));

            foreach (var junk in new[] { null, "", "  ", "strict", "all_writes", "2" })
            {
                Assert.That(DryRunPolicyService.TryParseWire(junk, out var parsed), Is.False, junk ?? "<null>");
                Assert.That(parsed, Is.EqualTo(DryRunPolicy.Off), "An unreadable pref never becomes a stricter or looser guess.");
            }
        }

        [Test]
        public void PrefKey_IsScopedToThisProject()
        {
            Assert.That(DryRunPolicyService.PrefKey, Is.EqualTo($"UnitySkills_{RegistryService.InstanceId}_DryRunPolicy"));
        }

        [Test]
        public void CurrentSetter_AuditsAndRaisesOnChangedOnce_WithoutTouchingEditorPrefsUnderTheSeam()
        {
            bool hadKey = EditorPrefs.HasKey(DryRunPolicyService.PrefKey);
            string stored = EditorPrefs.GetString(DryRunPolicyService.PrefKey, "<absent>");
            int raised = 0;
            Action onChanged = () => raised++;
            SkillsAuditLog.ResetForTests();
            DryRunPolicyService.OnChanged += onChanged;
            try
            {
                DryRunPolicyService.Current = DryRunPolicy.HighRisk;
                DryRunPolicyService.Current = DryRunPolicy.HighRisk;

                Assert.That(raised, Is.EqualTo(1), "Setting the value it already has is not a change.");
                Assert.That(DryRunPolicyService.Current, Is.EqualTo(DryRunPolicy.HighRisk));
                Assert.That(DryRunPolicyService.CurrentWire, Is.EqualTo("highRisk"));
                var entry = SkillsAuditLog.ReadRecent(20).OfType<JObject>().LastOrDefault(e => e["type"]?.ToString() == "dryrun_policy_changed");
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry["from"]?.ToString(), Is.EqualTo("off"));
                Assert.That(entry["to"]?.ToString(), Is.EqualTo("highRisk"));
                Assert.That(entry["source"]?.ToString(), Is.EqualTo("panel"));
                Assert.That(EditorPrefs.HasKey(DryRunPolicyService.PrefKey), Is.EqualTo(hadKey));
                Assert.That(EditorPrefs.GetString(DryRunPolicyService.PrefKey, "<absent>"), Is.EqualTo(stored));
            }
            finally
            {
                DryRunPolicyService.OnChanged -= onChanged;
                SkillsAuditLog.ResetForTests();
            }
        }

        [Test]
        public void ErrorCode_HasItsWireString()
        {
            Assert.That(SkillErrorCode.DryRunRequired.ToWireString(), Is.EqualTo("DRYRUN_REQUIRED"));
            Assert.That(SkillErrorCodeExtensions.TryParseWire("DRYRUN_REQUIRED", out var code), Is.True);
            Assert.That(code, Is.EqualTo(SkillErrorCode.DryRunRequired));
        }

        // ---------- which skills are gated ----------

        private static SkillRouter.SkillInfo Skill(SkillOperation op = SkillOperation.Modify, string risk = "low", bool readOnly = false,
            bool reload = false, bool play = false, bool supportsDryRun = true, params string[] parameters) => new SkillRouter.SkillInfo
        {
            Name = "probe_skill",
            Operation = op,
            RiskLevel = risk,
            ReadOnly = readOnly,
            MayTriggerReload = reload,
            MayEnterPlayMode = play,
            SupportsDryRun = supportsDryRun,
            ParameterNames = parameters,
        };

        private static void AssertGated(SkillRouter.SkillInfo skill, bool highRisk, bool allWrites, string label)
        {
            Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.Off), Is.False, label + " / off");
            Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.HighRisk), Is.EqualTo(highRisk), label + " / highRisk");
            Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.AllWrites), Is.EqualTo(allWrites), label + " / allWrites");
        }

        [Test]
        public void IsGated_TruthTable()
        {
            AssertGated(Skill(), false, true, "plain write");
            AssertGated(Skill(SkillOperation.Delete), true, true, "delete");
            AssertGated(Skill(reload: true), true, true, "mayTriggerReload");
            AssertGated(Skill(play: true), true, true, "mayEnterPlayMode");
            AssertGated(Skill(risk: "high"), true, true, "riskLevel high");
            AssertGated(Skill(SkillOperation.Query, readOnly: true), false, false, "read-only");
            AssertGated(Skill(SkillOperation.Delete, parameters: new[] { "confirmToken" }), false, false, "own confirmToken protocol");
            AssertGated(Skill(play: true, supportsDryRun: false), false, false, "supportsDryRun false");
            Assert.That(DryRunPolicyService.IsGated(null, DryRunPolicy.AllWrites), Is.False);
        }

        [Test]
        public void IsGated_RequireConfirmation_ExemptsExactlyWhatTheConfirmationGateChallenges()
        {
            ConfirmationTokenService.RequireConfirmationOverrideForTests = true;

            AssertGated(Skill(SkillOperation.Delete), false, false, "delete (confirmation challenges it)");
            AssertGated(Skill(risk: "high"), false, false, "riskLevel high (confirmation challenges it)");
            AssertGated(Skill(reload: true, risk: "medium"), true, true, "mayTriggerReload (not challenged)");
            AssertGated(Skill(), false, true, "plain write (not challenged)");
        }

        [Test]
        public void Registry_HighRiskIsASubsetOfAllWrites_AndCoversEveryConfirmationHighRiskWrite()
        {
            var skills = SkillRouter.GetAllSkillsSnapshotUnfiltered();
            int highRisk = 0, allWrites = 0;
            foreach (var skill in skills)
            {
                bool gatedHigh = DryRunPolicyService.IsGated(skill, DryRunPolicy.HighRisk);
                bool gatedAll = DryRunPolicyService.IsGated(skill, DryRunPolicy.AllWrites);
                if (gatedHigh) highRisk++;
                if (gatedAll) allWrites++;
                if (gatedHigh)
                    Assert.That(gatedAll, Is.True, skill.Name + ": highRisk must be a subset of allWrites");
                if (skill.ReadOnly)
                    Assert.That(gatedAll, Is.False, skill.Name + ": read-only skills are never gated");
                if (!skill.ReadOnly && skill.SupportsDryRun && !skill.ParameterNames.Contains("confirmToken") &&
                    ConfirmationTokenService.IsHighRisk(skill))
                    Assert.That(gatedHigh, Is.True, skill.Name + ": every confirmation-high-risk write is in highRisk");
            }
            Assert.That(highRisk, Is.GreaterThan(0));
            Assert.That(allWrites, Is.GreaterThan(highRisk));
        }

        [Test]
        public void Registry_SkillsThatCannotDryRun_AreNeverGated()
        {
            var noDryRun = SkillRouter.GetAllSkillsSnapshotUnfiltered().Where(s => !s.SupportsDryRun).ToList();
            Assert.That(noDryRun.Select(s => s.Name), Does.Contain("editor_play"), "Registry probe: editor_play declares SupportsDryRun=false.");
            Assert.That(noDryRun.Any(s => !s.ReadOnly && SkillsModeManager.IsForbiddenInSemi(s)), Is.True,
                "At least one of them would otherwise be gated by highRisk, so this check is not vacuous.");

            foreach (var skill in noDryRun)
            {
                Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.HighRisk), Is.False, skill.Name);
                Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.AllWrites), Is.False, skill.Name);
            }
        }

        [Test]
        public void Registry_SkillsWithTheirOwnConfirmToken_AreNeverGated()
        {
            var own = SkillRouter.GetAllSkillsSnapshotUnfiltered()
                .Where(s => s.ParameterNames.Any(p => string.Equals(p, "confirmToken", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.That(own.Select(s => s.Name), Is.SupersetOf(new[] { "batch_execute", "cleaner_delete_assets" }));
            foreach (var skill in own)
                Assert.That(DryRunPolicyService.IsGated(skill, DryRunPolicy.AllWrites), Is.False, skill.Name);
        }

        // ---------- canonical hashes ----------

        private static SkillRouter.SkillInfo Registered(string name)
        {
            Assert.That(SkillRouter.TryGetSkill(name, out var skill), Is.True, name + " is not registered.");
            return skill;
        }

        [Test]
        public void HashSkillArgs_IgnoresKeyOrderWhitespaceAndUndeclaredEnvelopeKeys()
        {
            var skill = Registered("gameobject_create");
            var reference = DryRunPolicyService.HashSkillArgs(skill, "{\"name\":\"A\",\"x\":1,\"nested\":{\"b\":1,\"a\":[2,{\"d\":1,\"c\":0}]}}");

            Assert.That(DryRunPolicyService.HashSkillArgs(skill, " {\n \"nested\" : {\"a\":[2,{\"c\":0,\"d\":1}],\"b\":1}, \"x\":1,\"name\":\"A\" } "),
                Is.EqualTo(reference));
            Assert.That(DryRunPolicyService.HashSkillArgs(skill,
                "{\"name\":\"A\",\"x\":1,\"nested\":{\"b\":1,\"a\":[2,{\"d\":1,\"c\":0}]},\"Verbose\":false,\"limit\":5,\"offset\":1,\"pageOffset\":2,\"pageLimit\":3,\"_confirm\":\"c\",\"dryRunToken\":\"t\"}"),
                Is.EqualTo(reference), "Envelope keys the skill does not declare never change what runs.");
        }

        [Test]
        public void HashSkillArgs_KeepsAReservedNameTheSkillDeclaresItself()
        {
            var skill = Registered("asset_reimport_batch");
            Assert.That(skill.ParameterNames, Does.Contain("limit"), "Probe precondition: asset_reimport_batch declares its own limit.");

            Assert.That(DryRunPolicyService.HashSkillArgs(skill, "{\"searchFilter\":\"x\",\"limit\":1}"),
                Is.Not.EqualTo(DryRunPolicyService.HashSkillArgs(skill, "{\"searchFilter\":\"x\",\"limit\":2}")),
                "limit changes how many assets this skill reimports, so it must be part of the binding.");
        }

        [Test]
        public void HashSkillArgs_DistinguishesValues()
        {
            var skill = Registered("gameobject_create");
            string Hash(string json) => DryRunPolicyService.HashSkillArgs(skill, json);

            Assert.That(Hash("{\"name\":\"A\"}"), Is.Not.EqualTo(Hash("{\"name\":\"B\"}")));
            Assert.That(Hash("{\"name\":\"A\",\"x\":1}"), Is.Not.EqualTo(Hash("{\"name\":\"A\",\"x\":1.0}")));
            Assert.That(Hash("{\"name\":\"A\",\"parentName\":null}"), Is.Not.EqualTo(Hash("{\"name\":\"A\"}")));
            Assert.That(Hash("{\"name\":\"A\",\"p\":[1,2]}"), Is.Not.EqualTo(Hash("{\"name\":\"A\",\"p\":[2,1]}")));
        }

        [Test]
        public void HashBatch_IgnoresKeyOrder_ButNotStepsParamsOrContinueOnError()
        {
            var steps = JArray.Parse("[{\"skill\":\"gameobject_delete\",\"args\":{\"name\":\"A\",\"path\":\"B\"}},{\"skill\":\"scene_get_info\",\"args\":{}}]");
            var reordered = JArray.Parse("[{\"args\":{\"path\":\"B\",\"name\":\"A\"},\"skill\":\"gameobject_delete\"},{\"args\":{},\"skill\":\"scene_get_info\"}]");
            var swapped = new JArray(steps[1].DeepClone(), steps[0].DeepClone());
            var reference = DryRunPolicyService.HashBatch(steps, null, false);

            Assert.That(DryRunPolicyService.HashBatch(reordered, null, false), Is.EqualTo(reference));
            Assert.That(DryRunPolicyService.HashBatch(swapped, null, false), Is.Not.EqualTo(reference));
            Assert.That(DryRunPolicyService.HashBatch(steps, null, true), Is.Not.EqualTo(reference));
            Assert.That(DryRunPolicyService.HashBatch(steps, JObject.Parse("{\"h\":1}"), false), Is.Not.EqualTo(reference));
        }

        // ---------- tokens ----------

        [Test]
        public void TokenStore_IsSingleUse_ValidatesBeforeConsuming_AndExpires()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var store = new OneTimeTokenStore(300, 16, () => now);

            var token = store.Issue("gameobject_delete", "h1");
            Assert.That(token, Does.Match("^[A-Za-z0-9_-]{22}$"));
            Assert.That(store.TryConsume(token, "gameobject_create", "h1", out _), Is.EqualTo(OneTimeTokenStore.Check.Mismatch));
            Assert.That(store.TryConsume(token, "gameobject_delete", "h2", out _), Is.EqualTo(OneTimeTokenStore.Check.Mismatch));

            now = now.AddSeconds(42);
            Assert.That(store.TryConsume(token, "GameObject_Delete", "h1", out var age), Is.EqualTo(OneTimeTokenStore.Check.Ok),
                "A mismatch must not burn the token for the arguments it was issued for.");
            Assert.That(age, Is.EqualTo(42));
            Assert.That(store.TryConsume(token, "gameobject_delete", "h1", out _), Is.EqualTo(OneTimeTokenStore.Check.Unknown));

            var stale = store.Issue("gameobject_delete", "h1");
            now = now.AddSeconds(301);
            Assert.That(store.TryConsume(stale, "gameobject_delete", "h1", out _), Is.EqualTo(OneTimeTokenStore.Check.Expired));
            Assert.That(store.TryConsume(null, "gameobject_delete", "h1", out _), Is.EqualTo(OneTimeTokenStore.Check.Unknown));
        }

        [Test]
        public void ConfirmationTokens_AndDryRunTokens_NeverCrossOver()
        {
            var skill = Registered("gameobject_delete");
            const string args = "{\"name\":\"CrossOverProbe\"}";
            var hash = DryRunPolicyService.HashSkillArgs(skill, args);

            var (confirmToken, _) = ConfirmationTokenService.IssueToken(skill.Name, args);
            Assert.That(DryRunPolicyService.TryConsume(confirmToken, skill.Name, hash, out _), Is.EqualTo(OneTimeTokenStore.Check.Unknown));

            var dryRunToken = DryRunPolicyService.IssueToken(skill.Name, hash);
            Assert.That(ConfirmationTokenService.TryConsume(dryRunToken, skill.Name, args), Is.False);

            Assert.That(ConfirmationTokenService.TryConsume(confirmToken, skill.Name, args), Is.True, "Each store still honors its own token.");
            Assert.That(DryRunPolicyService.TryConsume(dryRunToken, skill.Name, hash, out _), Is.EqualTo(OneTimeTokenStore.Check.Ok));
        }

        [Test]
        public void ReasonFor_NamesWhatWasWrongWithThePresentedToken()
        {
            Assert.That(DryRunPolicyService.ReasonFor(OneTimeTokenStore.Check.Unknown, null), Is.EqualTo("missingToken"));
            Assert.That(DryRunPolicyService.ReasonFor(OneTimeTokenStore.Check.Unknown, " "), Is.EqualTo("missingToken"));
            Assert.That(DryRunPolicyService.ReasonFor(OneTimeTokenStore.Check.Unknown, "t"), Is.EqualTo("tokenUnknownOrUsed"));
            Assert.That(DryRunPolicyService.ReasonFor(OneTimeTokenStore.Check.Expired, "t"), Is.EqualTo("tokenExpired"));
            Assert.That(DryRunPolicyService.ReasonFor(OneTimeTokenStore.Check.Mismatch, "t"), Is.EqualTo("argsChanged"));
        }
    }
}

// Producer:Betsy
