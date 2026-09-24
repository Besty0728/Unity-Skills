using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>How much of the write surface must be previewed with a dryRun before it executes.</summary>
    public enum DryRunPolicy
    {
        /// <summary>Not enforced (default); responses are exactly what they were before the policy existed.</summary>
        Off = 0,
        /// <summary>The never-in-semi writes: deletes, domain reloads, Play Mode and RiskLevel=high.</summary>
        HighRisk,
        /// <summary>Every skill that is not read-only.</summary>
        AllWrites
    }

    /// <summary>
    /// The per-project dryRun policy, set only in the UnitySkills panel. It turns "dryRun first" from advice into a server
    /// guarantee: a gated call over REST (POST /skill/{name}, POST /skills/batch) runs only with <c>?dryRunToken=</c> from a
    /// dryRun of the same body. Without one it answers DRYRUN_REQUIRED carrying that preview and a fresh token, so an allowed
    /// call costs at most one extra round trip and nothing that runs today stops running.
    ///
    /// Orthogonal to the operating mode, the Allowlist and the surface profile. The confirmation gate keeps its own tokens:
    /// a dryRun token never satisfies <c>_confirm</c>, and a skill the confirmation gate challenges is not gated here (that
    /// challenge already carries a preview).
    /// </summary>
    public static class DryRunPolicyService
    {
        public const string WireOff = "off";
        public const string WireHighRisk = "highRisk";
        public const string WireAllWrites = "allWrites";

        /// <summary>Query key a gated execute carries its token in.</summary>
        internal const string TokenQueryKey = "dryRunToken";
        internal const int TokenTtlSeconds = 300;
        /// <summary>Token scope of POST /skills/batch. Skill names are [a-z0-9_], so it never collides with one.</summary>
        internal const string BatchScope = "/skills/batch";
        private const int MaxLiveTokens = 256;

        /// <summary>Test seam: when set, <see cref="Current"/> reads and writes this value instead of EditorPrefs.</summary>
        internal static DryRunPolicy? OverrideForTests;
        /// <summary>Test seam: the clock token expiry is measured against.</summary>
        internal static Func<DateTime> UtcNowOverrideForTests;

        private static readonly OneTimeTokenStore _tokens = new OneTimeTokenStore(
            TokenTtlSeconds, MaxLiveTokens, () => UtcNowOverrideForTests?.Invoke() ?? DateTime.UtcNow);

        private static DryRunPolicy? _current;
        private static string _prefKey;

        /// <summary>Per-project, the same instance-scoped key pattern as SkillsHttpServer and SkillInstallSyncService.</summary>
        internal static string PrefKey => _prefKey ??= $"UnitySkills_{RegistryService.InstanceId}_DryRunPolicy";

        /// <summary>Raised after the policy changed and was persisted.</summary>
        public static event Action OnChanged;

        /// <summary>
        /// The active policy. Main thread only (EditorPrefs). The setter belongs to the panel: there is deliberately no REST
        /// write path, so an agent can never lift the gate it is held to.
        /// </summary>
        public static DryRunPolicy Current
        {
            get
            {
                if (OverrideForTests.HasValue) return OverrideForTests.Value;
                if (!_current.HasValue) _current = Load();
                return _current.Value;
            }
            set
            {
                var previous = Current;
                if (previous == value) return;
                if (OverrideForTests.HasValue)
                {
                    OverrideForTests = value;
                }
                else
                {
                    _current = value;
                    EditorPrefs.SetString(PrefKey, ToWire(value));
                }
                SkillsAuditLog.Append("dryrun_policy_changed", new { from = ToWire(previous), to = ToWire(value), source = "panel" });
                RaiseChanged();
            }
        }

        /// <summary>The wire value reported on /health, /permission/status and unity_diagnose.</summary>
        public static string CurrentWire => ToWire(Current);

        public static string ToWire(DryRunPolicy policy)
        {
            switch (policy)
            {
                case DryRunPolicy.HighRisk: return WireHighRisk;
                case DryRunPolicy.AllWrites: return WireAllWrites;
                default: return WireOff;
            }
        }

        /// <summary>Case-insensitive. Anything unrecognized returns false, and the caller falls back to Off.</summary>
        public static bool TryParseWire(string value, out DryRunPolicy policy)
        {
            policy = DryRunPolicy.Off;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var trimmed = value.Trim();
            if (trimmed.Equals(WireOff, StringComparison.OrdinalIgnoreCase))
                return true;
            if (trimmed.Equals(WireHighRisk, StringComparison.OrdinalIgnoreCase))
            {
                policy = DryRunPolicy.HighRisk;
                return true;
            }
            if (trimmed.Equals(WireAllWrites, StringComparison.OrdinalIgnoreCase))
            {
                policy = DryRunPolicy.AllWrites;
                return true;
            }
            return false;
        }

        /// <summary>Whether the current policy holds this skill to a dryRun first; see the overload.</summary>
        internal static bool IsGated(SkillRouter.SkillInfo skill) => IsGated(skill, Current);

        /// <summary>
        /// HighRisk gates the never-in-semi writes (<see cref="SkillsModeManager.IsForbiddenInSemi"/>), AllWrites every
        /// non-read-only skill. Exempt under every policy: skills that cannot dryRun (SupportsDryRun=false), skills with their
        /// own preview-then-confirmToken protocol, and skills the confirmation gate challenges.
        /// </summary>
        internal static bool IsGated(SkillRouter.SkillInfo skill, DryRunPolicy policy)
        {
            if (skill == null || policy == DryRunPolicy.Off) return false;
            if (skill.ReadOnly || !skill.SupportsDryRun || ConsumesPreviewToken(skill)) return false;
            if (policy == DryRunPolicy.HighRisk && !SkillsModeManager.IsForbiddenInSemi(skill)) return false;
            // CONFIRMATION_REQUIRED already carries a dryRun preview; gating here too would ask for a second token.
            return !(ConfirmationTokenService.RequireConfirmation && ConfirmationTokenService.IsHighRisk(skill));
        }

        private static bool ConsumesPreviewToken(SkillRouter.SkillInfo skill) => Declares(skill, "confirmToken");

        // ===== Tokens =====

        internal static string IssueToken(string scope, string argsHash) => _tokens.Issue(scope, argsHash);

        internal static OneTimeTokenStore.Check TryConsume(string token, string scope, string argsHash, out int tokenAgeSec) =>
            _tokens.TryConsume(token, scope, argsHash, out tokenAgeSec);

        /// <summary>The DRYRUN_REQUIRED <c>details.reason</c> for a presented token that did not pass.</summary>
        internal static string ReasonFor(OneTimeTokenStore.Check check, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "missingToken";
            switch (check)
            {
                case OneTimeTokenStore.Check.Expired: return "tokenExpired";
                case OneTimeTokenStore.Check.Mismatch: return "argsChanged";
                default: return "tokenUnknownOrUsed";
            }
        }

        internal static void ResetTokensForTests() => _tokens.Clear();

        // ===== Canonical argument hashes =====

        /// <summary>
        /// Hash of a POST /skill/{name} body: top-level envelope keys (paging, verbose, _confirm, dryRunToken) are dropped unless
        /// the skill declares a parameter of that name (asset_reimport_batch's own limit changes what runs), then object keys are
        /// sorted ordinally at every depth. Key order and whitespace never matter; values do (1 and 1.0, null and absent differ).
        /// </summary>
        internal static string HashSkillArgs(SkillRouter.SkillInfo skill, string json)
        {
            var body = ParseJson(json) as JObject ?? new JObject();
            foreach (var property in body.Properties().ToList())
            {
                if (IsEnvelopeKey(property.Name) && !Declares(skill, property.Name))
                    property.Remove();
            }
            return Sha256Hex(Canonicalize(body).ToString(Formatting.None));
        }

        /// <summary>
        /// Hash of a POST /skills/batch request: steps as submitted (before $param/$ref substitution), params and the resolved
        /// continueOnError. The mode is left out, so the token a batch dryRun returns serves execute and transactional alike.
        /// </summary>
        internal static string HashBatch(JArray steps, JObject batchParams, bool continueOnError)
        {
            var content = new JObject
            {
                ["continueOnError"] = continueOnError,
                ["params"] = batchParams != null ? batchParams.DeepClone() : JValue.CreateNull(),
                ["steps"] = steps != null ? steps.DeepClone() : new JArray(),
            };
            return Sha256Hex(Canonicalize(content).ToString(Formatting.None));
        }

        /// <summary>Parses without turning ISO date strings into dates, so a forwarded preview keeps the exact text it had.</summary>
        internal static JToken ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                return JToken.ReadFrom(reader);
        }

        private static bool IsEnvelopeKey(string name) =>
            SkillRouter.IsReservedBodyParameter(name) || string.Equals(name, TokenQueryKey, StringComparison.OrdinalIgnoreCase);

        private static bool Declares(SkillRouter.SkillInfo skill, string parameterName) =>
            skill?.ParameterNames != null &&
            skill.ParameterNames.Any(p => string.Equals(p, parameterName, StringComparison.OrdinalIgnoreCase));

        private static JToken Canonicalize(JToken token)
        {
            if (token is JObject obj)
            {
                var sorted = new JObject();
                foreach (var property in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted.Add(property.Name, Canonicalize(property.Value));
                return sorted;
            }
            if (token is JArray array)
                return new JArray(array.Select(Canonicalize));
            return token.DeepClone();
        }

        private static string Sha256Hex(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ===== DRYRUN_REQUIRED =====

        internal static string BuildSkillRequiredResponse(string skill, string reason, string token, JToken preview, string queryForFixes) =>
            BuildRequiredResponse(skill, $"{skill} runs only after a dryRun of these exact arguments", reason, token, preview,
                $"/skill/{skill}", queryForFixes, fixSkill: skill, gatedSteps: null);

        internal static string BuildBatchRequiredResponse(string reason, string token, JToken preview, string queryForFixes, int[] gatedSteps) =>
            BuildRequiredResponse("skills_batch", "this batch runs only after a dryRun of the same body", reason, token, preview,
                BatchScope, queryForFixes, fixSkill: null, gatedSteps: gatedSteps);

        /// <summary>
        /// HTTP 200 like the other gate decisions. Always carries the preview and a fresh token for the same body, whatever was
        /// wrong with the presented one, so the next call executes.
        /// </summary>
        private static string BuildRequiredResponse(string skill, string subject, string reason, string token, JToken preview,
            string path, string queryForFixes, string fixSkill, int[] gatedSteps)
        {
            var policy = CurrentWire;
            var retryQuery = string.IsNullOrEmpty(queryForFixes) ? string.Empty : "&" + queryForFixes;
            object details = gatedSteps == null
                ? (object)new { policy, reason, dryRunToken = token, ttlSeconds = TokenTtlSeconds, dryRun = preview }
                : new { policy, reason, dryRunToken = token, ttlSeconds = TokenTtlSeconds, gatedSteps, dryRun = preview };

            return SkillErrorResponse.Build(
                SkillErrorCode.DryRunRequired,
                $"dryRunPolicy '{policy}': {subject}. details.dryRun is that preview; resend the same body with ?{TokenQueryKey}=<details.dryRunToken> to execute.",
                skill: skill,
                details: details,
                suggestedFixes: new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "retry",
                        skill = fixSkill,
                        reason = $"POST {path}?{TokenQueryKey}={token}{retryQuery} with the same body executes it (single use, {TokenTtlSeconds} s).",
                    },
                },
                retryStrategy: SkillErrorResponse.RetryConfirmAndRetry,
                retryAfterSeconds: 0);
        }

        // ===== Persistence =====

        private static DryRunPolicy Load()
        {
            try
            {
                var raw = EditorPrefs.GetString(PrefKey, WireOff);
                if (TryParseWire(raw, out var stored))
                    return stored;
                SkillsLogger.LogVerbose($"Unrecognized dryRunPolicy pref '{raw}'; treating it as '{WireOff}'.");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"dryRunPolicy pref read failed, treating it as '{WireOff}': {ex.Message}");
            }
            return DryRunPolicy.Off;
        }

        /// <summary>Each subscriber isolated, so one that throws cannot keep the /health snapshot from refreshing.</summary>
        private static void RaiseChanged()
        {
            var handlers = OnChanged;
            if (handlers == null) return;

            foreach (var handler in handlers.GetInvocationList())
            {
                try { ((Action)handler)?.Invoke(); }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"DryRunPolicy OnChanged handler '{handler.Method?.DeclaringType?.Name}.{handler.Method?.Name}' threw: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// What POST /skill/{name} hands the router's dryRun gate: the presented token (null when absent) and the caller's query
    /// minus that token, kept for the retry URL (expectProject, expectInstance, wire, diff survive).
    /// </summary>
    internal sealed class DryRunGateContext
    {
        internal DryRunGateContext(string token, string queryForFixes)
        {
            Token = token;
            QueryForFixes = queryForFixes ?? string.Empty;
        }

        internal string Token { get; }
        internal string QueryForFixes { get; }
    }
}

// Producer:Betsy
