using System.Collections.Generic;
using System.Text.RegularExpressions;
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
        public const string RetryAskUserAndGrant = "ask_user_and_grant";
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

        /// <summary>
        /// The caller sent a Python-client helper function name (e.g. <c>get_skill_schema</c>) as if
        /// it were a REST skill. Reported as SKILL_NOT_FOUND like any other miss, but with the
        /// concrete REST equivalent instead of fuzzy name candidates: these helpers share no token
        /// with any registered skill, so <see cref="SkillNotFound"/>'s nearest-name search comes back
        /// empty and leaves the caller with no way to self-correct.
        /// </summary>
        public static string ClientHelperNotASkill(string helperName, string restEquivalent)
        {
            return Build(
                SkillErrorCode.SkillNotFound,
                $"'{helperName}' is a Python client helper function (unity_skills.py), not a REST skill — " +
                $"POST /skill/{helperName} can never succeed. Use {restEquivalent} instead.",
                skill: helperName,
                suggestedFixes: new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "retry",
                        skill = restEquivalent,
                        reason = "REST equivalent of the client-side helper",
                    },
                },
                retryStrategy: RetryFixAndRetry);
        }

        /// <summary>Generic internal error wrapper for caller convenience.</summary>
        public static string Internal(string message, string skill = null) =>
            Build(SkillErrorCode.Internal, message, skill: skill, retryStrategy: Abort);
    }

    /// <summary>
    /// The classification decided for one business error: which code to report, how the caller
    /// should react, and what to try next.
    /// </summary>
    public sealed class SkillErrorClassification
    {
        public SkillErrorCode Code;
        public string RetryStrategy;
        public List<SuggestedFix> SuggestedFixes;
        public List<string> RelatedSkills;
    }

    /// <summary>
    /// Message-pattern classifier for skill business errors — layer 2 of the router's error
    /// contract.
    ///
    /// <para>Layer 1 is the opt-in pass-through: a skill that declares <c>errorCode</c> /
    /// <c>suggestedFixes</c> / <c>retryStrategy</c> / <c>relatedSkills</c> on its error object has
    /// those honoured verbatim. Layer 2 exists because the overwhelming majority of skills return
    /// only <c>new { error = "..." }</c>; without it every one of them would collapse into
    /// <c>SKILL_ERROR</c> + <c>abort</c>, which tells an agent nothing about whether the call is
    /// worth retrying.</para>
    ///
    /// <para>The rules below were derived by bucketing the ~950 error literals that actually exist
    /// in <c>*Skills.cs</c>, not from first principles; they cover ~82% of them. Order matters —
    /// the first matching rule wins, and the residual bucket keeps today's
    /// <c>SKILL_ERROR</c> + <c>abort</c> behaviour. No rule may ever emit
    /// <c>wait_and_retry</c>: the Python client auto-retries on that strategy, and a business
    /// error the caller must fix would spin.</para>
    /// </summary>
    public static class SkillErrorClassifier
    {
        // Rule 1 — an optional package/asset-store dependency is absent.
        private static readonly string[] DependencyMarkers =
        {
            "not installed", "not imported", "requires com.", "requires the",
            "package manager", "install via", "from the asset store", "未安装",
        };

        // Rule 2 — the thing the caller wants to create is already there.
        private static readonly string[] ConflictMarkers =
        {
            "already exists", "already has", "already in use", "already registered", "已存在",
        };

        // Rule 3 — the target could not be located.
        private static readonly string[] NotFoundMarkers =
        {
            "not found", "was not found", "no gameobject", "could not find", "could not locate",
            "cannot be found", "does not exist", "doesn't exist", "no such", "not present",
            "找不到", "不存在",
        };

        // Rule 6 — a parameter the caller owns was omitted. "provide " carries a trailing space so
        // it cannot match "provided"; the "no X provided" forms are already taken by rule 4.
        private static readonly string[] MissingParamMarkers =
        {
            "is required", "are required", "required when", "must be provided", "must be specified",
            "provide ", "missing", "必填", "必须提供",
        };

        // Rule 7 — a parameter was supplied but is unusable.
        private static readonly string[] SemanticMarkers =
        {
            "invalid", "must be", "must not", "must start", "unknown ", "unsupported",
            "out of range", "not allowed", "not a valid", "cannot be", "expected ",
            "非法", "无效",
        };

        // Rule 4 — "No faces selected" / "No items provided": the caller simply passed nothing.
        private static readonly Regex NotSuppliedPattern = new Regex(
            @"\bno \S+ (provided|selected|specified|supplied|given)\b|\bno objects selected\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 5 — "GameObject has no RectTransform" / "No Light component on X" / "No mesh found":
        // the object was located but does not carry what the skill needs.
        private static readonly Regex MissingOnTargetPattern = new Regex(
            @"\bhas no \b|\bno \S+ (component|found)\b|\bno \S+ on |^no [a-z]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 7b — "Not a texture: X" / "Child is not a Cinemachine Virtual Camera".
        // Word-anchored so "cannot allocate" and "not allowed" cannot match.
        private static readonly Regex WrongKindPattern = new Regex(
            @"\bnot an?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 2b — the message *opens* by naming the failure kind ("Invalid bindingMode 'X': ...",
        // "Unknown step 'y'."). Such a message often quotes an inner exception further along, and
        // .NET's own enum parse failure reads "Requested value 'X' was not found" — which would
        // otherwise match the not-found markers first and report a bad enum value as a missing
        // scene object, sending the caller off to gameobject_find. Anchored at the start so only
        // the message's own verdict wins, never a phrase buried in quoted inner text.
        private static readonly Regex LeadingSemanticPattern = new Regex(
            @"^\s*(invalid|unknown|unsupported|malformed)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Map a raw skill error message onto a code, a retry strategy and concrete next steps.
        /// Case-insensitive; never returns null and never throws.
        /// </summary>
        public static SkillErrorClassification Classify(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Unclassified();

            var text = message.ToLowerInvariant();

            // "Package 'x' not found" means the package is not installed — but "Group 'g' not
            // found in package 'p'" is a lookup inside an existing package, not a dependency gap.
            bool packageAbsent = text.Contains("package")
                && !text.Contains("in package")
                && (text.Contains("not found") || text.Contains("does not exist"));

            if (packageAbsent || ContainsAny(text, DependencyMarkers))
                return Dependency();

            if (ContainsAny(text, ConflictMarkers))
                return AlreadyExists();

            if (LeadingSemanticPattern.IsMatch(text))
                return SemanticInvalid();

            if (ContainsAny(text, NotFoundMarkers))
                return TargetNotFound(text);

            if (NotSuppliedPattern.IsMatch(text))
                return MissingParam();

            if (MissingOnTargetPattern.IsMatch(text))
                return TargetNotFound(text);

            if (ContainsAny(text, MissingParamMarkers))
                return MissingParam();

            if (ContainsAny(text, SemanticMarkers) || WrongKindPattern.IsMatch(text))
                return SemanticInvalid();

            return Unclassified();
        }

        /// <summary>
        /// Advice for a code the skill declared on its own error object. This keeps a *partial*
        /// declaration coherent: a skill that states <c>errorCode</c> but omits
        /// <c>retryStrategy</c>/<c>suggestedFixes</c> gets the advice belonging to that code rather
        /// than whatever its message text happens to look like. Codes outside this classifier's own
        /// vocabulary fall back to message classification — deliberately, so that declaring a
        /// transient code (COMPILING, RATE_LIMIT, …) can never make the router infer
        /// <c>wait_and_retry</c>; a skill that wants it must say so explicitly.
        /// </summary>
        public static SkillErrorClassification ForCode(SkillErrorCode code, string message)
        {
            switch (code)
            {
                case SkillErrorCode.TargetNotFound:
                    return TargetNotFound((message ?? string.Empty).ToLowerInvariant());
                case SkillErrorCode.MissingPackage:
                    return Dependency();
                case SkillErrorCode.MissingParam:
                    return MissingParam();
                case SkillErrorCode.SemanticInvalid:
                    return SemanticInvalid();
                default:
                    return Classify(message);
            }
        }

        private static bool ContainsAny(string text, string[] markers)
        {
            for (int i = 0; i < markers.Length; i++)
            {
                if (text.Contains(markers[i]))
                    return true;
            }
            return false;
        }

        private static SkillErrorClassification Dependency() => new SkillErrorClassification
        {
            Code = SkillErrorCode.MissingPackage,
            RetryStrategy = SkillErrorResponse.RetryInstallAndRetry,
            RelatedSkills = new List<string> { "package_install", "package_list" },
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "install_package",
                    skill = "package_install",
                    reason = "The error names the missing package — install it, wait for the domain reload, then retry."
                },
                new SuggestedFix
                {
                    action = "retry",
                    skill = "package_list",
                    reason = "Confirm what is actually installed before assuming the package id."
                },
            },
        };

        private static SkillErrorClassification AlreadyExists() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SemanticInvalid,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    reason = "The target already exists. Retry with a different name/path, or pass the skill's overwrite/force parameter if it has one."
                },
            },
        };

        private static SkillErrorClassification TargetNotFound(string text)
        {
            var classification = new SkillErrorClassification
            {
                Code = SkillErrorCode.TargetNotFound,
                RetryStrategy = SkillErrorResponse.RetryFindAndRetry,
            };

            if (text.Contains("component"))
            {
                classification.RelatedSkills = new List<string> { "component_list", "gameobject_get_info" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "component_list",
                        reason = "List the components actually present on the object, then retry with a name from that list."
                    },
                };
                return classification;
            }

            if (ContainsAny(text, AssetMarkers))
            {
                classification.RelatedSkills = new List<string> { "asset_find", "asset_get_info" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "asset_find",
                        reason = "Resolve the real project path first — asset paths are case-sensitive and must start with Assets/ or Packages/."
                    },
                };
                return classification;
            }

            // A job id is not a scene object: pointing the caller at gameobject_find here sends it
            // hunting through the hierarchy for something that only ever lived in the job table.
            if (text.Contains("job"))
            {
                classification.RelatedSkills = new List<string> { "job_list", "job_status" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "find_target",
                        skill = "job_list",
                        reason = "List the jobs this session still knows about — ids do not survive a domain reload."
                    },
                };
                return classification;
            }

            classification.RelatedSkills = new List<string> { "gameobject_find", "scene_get_hierarchy" };
            classification.SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "find_target",
                    skill = "gameobject_find",
                    reason = "Confirm the target exists in an open scene, then retry with the entityId it returns rather than a name."
                },
                new SuggestedFix
                {
                    action = "find_target",
                    skill = "scene_get_hierarchy",
                    reason = "If the name is a guess, list the hierarchy and pick the exact path."
                },
            };
            return classification;
        }

        private static readonly string[] AssetMarkers =
        {
            "asset", "path", "file", "folder", "directory", "prefab", "material", "shader", "texture",
        };

        private static SkillErrorClassification MissingParam() => new SkillErrorClassification
        {
            Code = SkillErrorCode.MissingParam,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = "Supply the parameter named in the message; dryRun returns the full parameter schema without executing."
                },
            },
        };

        private static SkillErrorClassification SemanticInvalid() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SemanticInvalid,
            RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = "The value is rejected, not the parameter name. Read the accepted range/enum in the message, then dryRun the corrected args."
                },
            },
        };

        // Residual bucket: genuine runtime failures ("Failed to ...", stuck editor state).
        // Same code and strategy as before this classifier existed.
        private static SkillErrorClassification Unclassified() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SkillError,
            RetryStrategy = SkillErrorResponse.Abort,
        };
    }
}

// Producer:Betsy
