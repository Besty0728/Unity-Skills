using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Concrete recovery suggestion delivered alongside an error response so AI agents
    /// can self-recover without round-tripping through a human.
    /// </summary>
    public sealed class SuggestedFix
    {
        /// <summary>Action verb: "retry", "fix_param", "find_target", "install_package", "wait", "confirm".</summary>
        public string action;

        /// <summary>Optional alternative skill the caller should consider.</summary>
        public string skill;

        /// <summary>Optional argument shape the caller should retry with.</summary>
        public object args;

        /// <summary>Single-sentence rationale for this suggestion.</summary>
        public string reason;
    }

    /// <summary>
    /// Unified builder for REST error payloads. Every routing/validation/runtime failure
    /// returns the same shape:
    /// <code>
    /// {
    ///   "status": "error",
    ///   "errorCode": "MISSING_PARAM",
    ///   "error": "...",
    ///   "skill": "...",
    ///   "details": { ... },
    ///   "suggestedFixes": [ ... ],
    ///   "relatedSkills": [ ... ],
    ///   "retryStrategy": "fix_and_retry",
    ///   "retryAfterSeconds": 5
    /// }
    /// </code>
    /// </summary>
    public static class SkillErrorResponse
    {
        // Stable wire values for retryStrategy.
        public const string RetryFixAndRetry     = "fix_and_retry";
        public const string RetryWaitAndRetry    = "wait_and_retry";
        public const string RetryFindAndRetry    = "find_target_and_retry";
        public const string RetryInstallAndRetry = "install_and_retry";
        public const string RetryConfirmAndRetry = "confirm_and_retry";
        public const string Abort                = "abort";

        private static readonly JsonSerializerSettings _jsonSettings = SkillsCommon.JsonSettings;
        private static JsonSerializer Serializer => JsonSerializer.Create(_jsonSettings);

        public static string Build(
            SkillErrorCode code,
            string message,
            string skill = null,
            object details = null,
            IList<SuggestedFix> suggestedFixes = null,
            IList<string> relatedSkills = null,
            string retryStrategy = null,
            int? retryAfterSeconds = null,
            IDictionary<string, object> extra = null)
        {
            var payload = new JObject
            {
                ["status"] = "error",
                ["errorCode"] = code.ToWireString(),
                ["error"] = message ?? string.Empty,
            };

            if (!string.IsNullOrEmpty(skill))
                payload["skill"] = skill;

            if (details != null)
                payload["details"] = JToken.FromObject(details, Serializer);

            if (suggestedFixes != null && suggestedFixes.Count > 0)
                payload["suggestedFixes"] = JToken.FromObject(suggestedFixes, Serializer);

            if (relatedSkills != null && relatedSkills.Count > 0)
                payload["relatedSkills"] = JArray.FromObject(relatedSkills);

            if (!string.IsNullOrEmpty(retryStrategy))
                payload["retryStrategy"] = retryStrategy;

            if (retryAfterSeconds.HasValue)
                payload["retryAfterSeconds"] = retryAfterSeconds.Value;

            if (extra != null)
            {
                foreach (var kv in extra)
                {
                    if (payload.ContainsKey(kv.Key))
                        continue;
                    payload[kv.Key] = kv.Value == null
                        ? JValue.CreateNull()
                        : JToken.FromObject(kv.Value, Serializer);
                }
            }

            return JsonConvert.SerializeObject(payload, _jsonSettings);
        }

        /// <summary>Skill name lookup miss with optional suggestions from fuzzy matching.</summary>
        public static string SkillNotFound(string skillName, IList<string> nearestSkills = null)
        {
            var fixes = new List<SuggestedFix>();
            if (nearestSkills != null)
            {
                foreach (var s in nearestSkills)
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "retry",
                        skill = s,
                        reason = "Closest registered skill name"
                    });
                }
            }
            fixes.Add(new SuggestedFix
            {
                action = "retry",
                skill = "GET /skills/recommend?intent=...",
                reason = "Discover skills by natural-language intent"
            });

            return Build(
                SkillErrorCode.SkillNotFound,
                $"Skill '{skillName}' not found",
                skill: skillName,
                relatedSkills: nearestSkills,
                suggestedFixes: fixes.Count > 0 ? fixes : null,
                retryStrategy: RetryFixAndRetry);
        }

        /// <summary>Generic internal error wrapper for caller convenience.</summary>
        public static string Internal(string message, string skill = null) =>
            Build(SkillErrorCode.Internal, message, skill: skill, retryStrategy: Abort);
    }
}
