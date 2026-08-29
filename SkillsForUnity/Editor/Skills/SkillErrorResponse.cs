using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// A concrete recovery suggestion delivered alongside an error response, so an AI agent can recover
    /// on its own without going back to ask a human.
    /// </summary>
    public sealed class SuggestedFix
    {
        /// <summary>The action verb: "retry", "fix_param", "find_target", "install_package", "wait", "confirm".</summary>
        public string action;

        /// <summary>Optional: an alternative skill suggested for the caller to consider.</summary>
        public string skill;

        /// <summary>Optional: parameters suggested for the caller to retry with, in this shape.</summary>
        public object args;

        /// <summary>A one-sentence explanation of why this suggestion applies.</summary>
        public string reason;
    }

    /// <summary>
    /// Unified constructor for REST error payloads. Every routing/validation/runtime failure returns the same shape:
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
        // Stable, publicly-relied-upon values for retryStrategy.
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

        /// <summary>Skill name lookup missed; may attach candidate suggestions from a fuzzy match.</summary>
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
        /// The caller sent the name of a Python client helper function (e.g. <c>get_skill_schema</c>) as if it were a REST skill.
        /// Reports SKILL_NOT_FOUND like any other miss, but gives the specific corresponding REST usage
        /// instead of fuzzy name candidates: these helper functions share no token with any registered
        /// skill, so <see cref="SkillNotFound"/>'s nearest-name search would come back empty, leaving the
        /// caller with no way to self-correct.
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

        /// <summary>Generic internal error wrapper, for easy handling by the caller.</summary>
        public static string Internal(string message, string skill = null) =>
            Build(SkillErrorCode.Internal, message, skill: skill, retryStrategy: Abort);
    }

    /// <summary>
    /// The classification verdict reached for a business error: which error code to report, how the
    /// caller should react, and what to try next.
    /// </summary>
    public sealed class SkillErrorClassification
    {
        public SkillErrorCode Code;
        public string RetryStrategy;
        public List<SuggestedFix> SuggestedFixes;
        public List<string> RelatedSkills;
    }

    /// <summary>
    /// A message-pattern classifier for skill business errors — the second tier of the router's error contract.
    ///
    /// <para>The first tier is an optional pass-through: if a skill declares <c>errorCode</c> /
    /// <c>suggestedFixes</c> / <c>retryStrategy</c> / <c>relatedSkills</c> on its own error object, that is
    /// used as-is. The second tier exists because the vast majority of skills just return
    /// <c>new { error = "..." }</c>; without it, these errors would all collapse into
    /// <c>SKILL_ERROR</c> + <c>abort</c>, which gives the agent no help at all in deciding whether
    /// this call is worth retrying.</para>
    ///
    /// <para>The rules below were induced by bucketing the roughly 950 error literals that actually
    /// exist in <c>*Skills.cs</c> — not derived from first principles — and cover about 82% of them.
    /// Order matters — the first rule that matches wins, and the fallback bucket keeps the
    /// <c>SKILL_ERROR</c> + <c>abort</c> behavior that existed before. No rule may ever produce
    /// <c>wait_and_retry</c>: the Python client auto-retries on that strategy, and a business error that
    /// needs the caller to fix something would just spin in place.</para>
    /// </summary>
    public static class SkillErrorClassifier
    {
        // Rule 1 — Missing an optional package / Asset Store dependency.
        private static readonly string[] DependencyMarkers =
        {
            "not installed", "not imported", "requires com.", "requires the",
            "package manager", "install via", "from the asset store", "未安装",
        };

        // Rule 1b — "Package not found: com.x" / "Package 'x' does not exist" identifies the missing
        // thing as the *package* itself.
        // The word "package" must sit right next to the not-found phrase (at most one quoted, parenthesized,
        // or dotted package id may sit between them): error messages interpolate an identifier supplied by
        // the caller, and if we only matched Contains("package"), any jobId
        // ("DefaultPackage_validation_1") or "Packages/..." asset path could get an ordinary lookup miss
        // misjudged as MISSING_PACKAGE, sending the agent to package_install instead of fixing that id or path.
        // \bpackage\b doesn't match either of those; the lookbehind assertion also excludes
        // "Group 'g' not found in package 'p'" (a lookup inside an already-existing package).
        private static readonly Regex PackageAbsentPattern = new Regex(
            @"(?<!\bin )\bpackage\b(?:\s+(?:'[^']*'|""[^""]*""|\([\w.@/~-]+\)|[\w-]+(?:\.[\w-]+)+))?\s*:?\s*(?:is |was )?(?:not found|does not exist)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 2 — What the caller wants to create already exists.
        private static readonly string[] ConflictMarkers =
        {
            "already exists", "already has", "already in use", "already registered", "已存在",
        };

        // Rule 3 — The target can't be located.
        private static readonly string[] NotFoundMarkers =
        {
            "not found", "was not found", "no gameobject", "could not find", "could not locate",
            "cannot be found", "does not exist", "doesn't exist", "no such", "not present",
            "找不到", "不存在",
        };

        // Rule 3a — The target *is already* located; what's missing is the property or field the caller
        // named. Must come before Rule 3, because that rule claims the bare "not found" text: "Property
        // not found: _Cull" would match there and return TARGET_NOT_FOUND, sending the suggested fix to
        // gameobject_find to hunt for an object that was never the problem, and never pointing at the
        // property-reading skill it actually needs. It must likewise come before Rule 5 — that rule's
        // ^no [a-z] branch would claim "No color property found on material".
        //
        // Each branch anchors on nouns like property/field/enum-value, so genuine cases like
        // "GameObject not found" / "Material asset not found: <path>" are unaffected — neither carries
        // that kind of noun. These five shapes are taken from error literals that actually exist:
        // "<noun> ... not found" ("Property not found: X",
        // "Property '_x' not found on Rigidbody", "Property/field not found: X",
        // "Shader Graph property 'x' was not found"), a read-only rejection, the inverted
        // "No color property found on material", "<thing> does not have a color property",
        // and the enum-value form below.
        //
        // "Enum value 'x' not found for 'm_Foo'" is the same class of defect phrased differently — both
        // the object and the property already resolved; what doesn't exist is the *value* — but it needs
        // its own branch, because here "not found" sits before the noun rather than after it, which the
        // first branch doesn't cover.
        private static readonly Regex PropertyNotOnTargetPattern = new Regex(
            @"\b(?:propert(?:y|ies)|field)\b[^.;]{0,40}?\b(?:not found|is read-?only)\b" +
            @"|\bno\b[^.;]{0,30}?\bpropert(?:y|ies)\b[^.;]{0,20}?\bfound\b" +
            @"|\bdoes not have\b[^.;]{0,40}?\bpropert(?:y|ies)\b" +
            @"|\benum value\b[^.;]{0,60}?\bnot found\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // A bare "shader" would also match "shaders" (GraphicsSettings' plural Always Included Shaders list)
        // and "shader graph property type not found" (an internal type-name lookup failure, not a
        // property that genuinely exists on some material/shader instance) — neither should be routed to
        // material_get_properties. So we anchor on the singular word to make the plural miss, and exclude
        // "property type" to make type-lookup failures miss too; while "Shader Graph property 'x' was not
        // found" (a genuine named-property miss) still matches and keeps its existing routing.
        private static readonly Regex ShaderPropertyPhrase = new Regex(
            @"\bshader\b[^.;]{0,30}?\bpropert(?:y|ies)\b(?!\s+type\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 6 — A parameter the caller was supposed to supply is missing. "provide " has a trailing
        // space, so it doesn't match "provided"; the "no X provided" shape is already claimed by Rule 4.
        private static readonly string[] MissingParamMarkers =
        {
            "is required", "are required", "required when", "must be provided", "must be specified",
            "provide ", "missing", "必填", "必须提供",
        };

        // Rule 7 — A parameter was supplied, but it isn't usable.
        private static readonly string[] SemanticMarkers =
        {
            "invalid", "must be", "must not", "must start", "unknown ", "unsupported",
            "out of range", "not allowed", "not a valid", "cannot be", "expected ",
            "非法", "无效",
        };

        // Rule 4 — "No faces selected" / "No items provided": the caller passed nothing at all.
        private static readonly Regex NotSuppliedPattern = new Regex(
            @"\bno \S+ (provided|selected|specified|supplied|given)\b|\bno objects selected\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 5 — "GameObject has no RectTransform" / "No Light component on X" / "No mesh found":
        // the object was located, but it doesn't have what this skill needs.
        private static readonly Regex MissingOnTargetPattern = new Regex(
            @"\bhas no \b|\bno \S+ (component|found)\b|\bno \S+ on |^no [a-z]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 7b — "Not a texture: X" / "Child is not a Cinemachine Virtual Camera".
        // Anchored on the word, so "cannot allocate" and "not allowed" can't match.
        private static readonly Regex WrongKindPattern = new Regex(
            @"\bnot an?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Rule 2b — The message *starts* by naming the kind of failure ("Invalid bindingMode 'X': ...",
        // "Unknown step 'y'."). Messages like this often quote an inner exception in their second half, and
        // .NET's own enum-parsing failure reads as "Requested value 'X' was not found" — without this
        // handling, that would match the not-found marker first, reporting an invalid enum value as a
        // missing scene object and sending the caller to gameobject_find.
        // Anchored at the start, so only the message's own leading verdict word takes effect, never a
        // phrase buried in a quoted inner-exception text.
        private static readonly Regex LeadingSemanticPattern = new Regex(
            @"^\s*(invalid|unknown|unsupported|malformed)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Maps a raw skill error message to an error code, retry strategy, and specific next action.
        /// Case-insensitive; never returns null, never throws.
        /// </summary>
        public static SkillErrorClassification Classify(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return Unclassified();

            var text = message.ToLowerInvariant();

            if (PackageAbsentPattern.IsMatch(text) || ContainsAny(text, DependencyMarkers))
                return Dependency();

            if (ContainsAny(text, ConflictMarkers))
                return AlreadyExists();

            if (LeadingSemanticPattern.IsMatch(text))
                return SemanticInvalid();

            if (PropertyNotOnTargetPattern.IsMatch(text))
                return PropertyNotOnTarget(text);

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
        /// <summary>
        /// Gives a matching suggestion for the error code a skill declares on its own error object. This
        /// keeps *partial* declarations self-consistent too: a skill that only writes <c>errorCode</c>
        /// without <c>retryStrategy</c>/<c>suggestedFixes</c> gets the suggestion that belongs to that
        /// error code, rather than something incidentally inferred from its message text.
        /// An error code outside this classifier's vocabulary falls back to message classification — this
        /// is deliberate: declaring a transient error code (COMPILING, RATE_LIMIT, etc.) must never let the
        /// router infer <c>wait_and_retry</c> on its own; a skill that needs that must declare it explicitly.
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

            // A job id is not a scene object: if we sent the caller to gameobject_find here, it would go
            // hunting through the hierarchy for something that only ever exists in the job table.
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

        /// <summary>
        /// <summary>
        /// The object exists, the property doesn't. Reports SEMANTIC_INVALID + fix_and_retry: the target
        /// the caller named just doesn't have it — there's nothing to find, just a parameter to change.
        ///
        /// <para>Which read skill gets recommended depends on the kind of property, and that's the entire
        /// value of this suggestion: recommending component_get_properties for a shader property would be
        /// just as useless as the gameobject_find this rule replaces.
        /// When the message gives no clue — "Property not found: _Cull" doesn't tell you whether it's a
        /// material or a component — both read skills are given, rather than guessing one.</para>
        /// </summary>
        private static SkillErrorClassification PropertyNotOnTarget(string text)
        {
            var classification = new SkillErrorClassification
            {
                Code = SkillErrorCode.SemanticInvalid,
                RetryStrategy = SkillErrorResponse.RetryFixAndRetry,
            };

            // Must come before the checks below: the propertyPath it references may itself contain
            // "shader"/"material" (e.g. "Enum value 'x' not found for 'm_Shader'"), otherwise a serialized
            // enum parse failure would get routed to the material-property read skill.
            if (text.Contains("enum value"))
            {
                classification.RelatedSkills = new List<string> { "component_get_serialized_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        reason = "The property resolved; the value does not exist on it. The message lists the accepted names — retry with one of those, or a comma-separated set / raw bitmask for a [Flags] enum."
                    },
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "component_get_serialized_properties",
                        reason = "If the accepted names are not enough to tell which property was addressed, list the serialized properties and confirm the propertyPath."
                    },
                };
                return classification;
            }

            // Excludes "GraphicsSettings serialized property not found" — that SerializedObject belongs
            // to a project settings asset, not a component, and recommending
            // component_get_serialized_properties would send the caller to inspect an object that was never involved.
            if (text.Contains("serialized") && !text.Contains("graphicssettings"))
            {
                classification.RelatedSkills = new List<string> { "component_get_serialized_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "component_get_serialized_properties",
                        reason = "Serialized paths are not the C# member names — list them and retry with an exact propertyPath."
                    },
                };
                return classification;
            }

            if (text.Contains("material") || ShaderPropertyPhrase.IsMatch(text))
            {
                classification.RelatedSkills = new List<string> { "material_get_properties" };
                classification.SuggestedFixes = new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "fix_param",
                        skill = "material_get_properties",
                        reason = "Shader property names vary by render pipeline (_Color vs _BaseColor). List what this material's shader exposes, then retry with a name from that list."
                    },
                };
                return classification;
            }

            classification.RelatedSkills = new List<string> { "component_get_properties", "material_get_properties" };
            classification.SuggestedFixes = new List<SuggestedFix>
            {
                new SuggestedFix
                {
                    action = "fix_param",
                    skill = "component_get_properties",
                    reason = "The target exists but carries no such property. List the properties it does expose, then retry with one of them — use material_get_properties instead if the target is a material."
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

        // Fallback bucket: genuine runtime failures ("Failed to ...", editor-stuck states).
        // Error code and strategy match what this classifier had before it existed.
        private static SkillErrorClassification Unclassified() => new SkillErrorClassification
        {
            Code = SkillErrorCode.SkillError,
            RetryStrategy = SkillErrorResponse.Abort,
        };
    }
}

// Producer:Betsy
