using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillRouter
    {
        /// <summary>
        /// Maps keywords onto the dictionary via exact match plus substring match (substring matching is there for unsegmented Chinese).
        /// </summary>
        private static HashSet<TValue> MatchKeywords<TValue>(string[] keywords, Dictionary<string, TValue> map)
        {
            var results = new HashSet<TValue>();
            foreach (var kw in keywords)
            {
                if (map.TryGetValue(kw, out var val)) results.Add(val);
                foreach (var entry in map)
                {
                    if (TokenMatchesKey(kw, entry.Key))
                        results.Add(entry.Value);
                }
            }
            return results;
        }

        private static bool IsAscii(string s)
        {
            foreach (var c in s)
                if (c > 127) return false;
            return true;
        }

        /// <summary>
        /// Whole-word match for ASCII table keys (plus a prefix/suffix allowance for keys of 4+ letters, so "boxcollider" and
        /// "colliders" still reach "collider"); substring match for non-ASCII keys, because Chinese intents arrive as one token
        /// ("设置物体位置" must still hit "设置", "物体", "位置").
        /// </summary>
        private static bool TokenMatchesKey(string token, string key)
        {
            if (token.Equals(key, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!IsAscii(key))
                return key.Length >= 2 && token.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
            return key.Length >= 4 &&
                   (token.StartsWith(key, StringComparison.OrdinalIgnoreCase) || token.EndsWith(key, StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<SkillCategory> ExtractComponentTypeHints(string[] rawKeywords, List<string> hintWords)
        {
            var cats = new HashSet<SkillCategory>();
            foreach (var kw in rawKeywords)
            {
                foreach (var entry in _componentTypeHints)
                {
                    if (TokenMatchesKey(kw, entry.Key))
                    {
                        hintWords.Add(entry.Key);
                        foreach (var c in entry.Value) cats.Add(c);
                    }
                }
            }
            return cats;
        }

        private static string[] ExpandIntent(string[] keywords)
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kw in keywords) expanded.Add(kw);
            foreach (var synonyms in MatchKeywords(keywords, _synonymMap))
            {
                foreach (var s in synonyms) expanded.Add(s);
            }
            return expanded.ToArray();
        }

        private static HashSet<SkillOperation> ExtractOperations(string[] keywords)
            => MatchKeywords(keywords, _operationKeywords);

        private static HashSet<SkillCategory> ExtractCategories(string[] keywords)
            => MatchKeywords(keywords, _categoryKeywords);

        // ========== Skill recommendations ==========

        /// <summary>
        /// Intent-based skill recommendation. Scores by keyword matches against name (3 points), tags (2 points), and description (1 point),
        /// returning the top N results. A query that repeats <c>intent=</c> is answered by <see cref="GetRecommendationsMulti"/>;
        /// a single intent keeps its original response bytes.
        /// </summary>
        public static string GetRecommendations(string queryString)
        {
            Initialize();
            var intents = DistinctIntents(ReadQueryValues(queryString, "intent"));
            if (intents.Count > 1)
                return GetRecommendationsMulti(intents, queryString);
            return BuildSingleIntentRecommendations(intents.Count == 1 ? intents[0] : "", queryString);
        }

        /// <summary>
        /// Several intents in one call, for a multi-step task: each intent is ranked independently with the same topN /
        /// includeSchema / wire handling as a single intent, and the answer is
        /// <c>{ topN, includeSchema, ..., intents: [ { intent, expandedKeywords, totalMatches, results } ] }</c>.
        /// Blank and case-insensitively repeated intents are dropped; when only one remains, the single-intent response is returned unchanged.
        /// <paramref name="queryString"/> supplies topN / includeSchema / wire exactly as it does for <see cref="GetRecommendations"/>.
        /// </summary>
        public static string GetRecommendationsMulti(IReadOnlyList<string> intents, string queryString)
        {
            Initialize();
            var distinct = DistinctIntents(intents);
            if (distinct.Count <= 1)
                return BuildSingleIntentRecommendations(distinct.Count == 1 ? distinct[0] : "", queryString);

            var options = ReadRecommendOptions(queryString);
            var context = new RecommendContext();
            var perIntent = distinct.Select(intent =>
            {
                var ranked = RankIntent(intent, options.TopN, context);
                return new
                {
                    intent,
                    expandedKeywords = ranked.ExpandedKeywords,
                    totalMatches = ranked.TotalMatches,
                    results = BuildRecommendationEntries(ranked, options, context)
                };
            }).ToList();

            // Same envelope branches as a single intent (see BuildSingleIntentRecommendations): v2 states its wire contract,
            // and v1 names the surface profile only when it pruned something.
            if (options.Wire == WireV2)
            {
                return JsonConvert.SerializeObject(new
                {
                    topN = options.TopN,
                    includeSchema = options.IncludeSchema,
                    wire = "v2",
                    metaUrl = MetaEndpointPath,
                    defaults = BuildWireDefaults(),
                    surfaceProfile = SkillsSurfaceProfile.IsFull ? null : SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SkillsSurfaceProfile.IsFull ? null : SurfaceProfilePrunedHint,
                    intents = perIntent
                }, _jsonSettingsV2);
            }

            if (!SkillsSurfaceProfile.IsFull)
            {
                return JsonConvert.SerializeObject(new
                {
                    topN = options.TopN,
                    includeSchema = options.IncludeSchema,
                    surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SurfaceProfilePrunedHint,
                    intents = perIntent
                }, _jsonSettings);
            }

            return JsonConvert.SerializeObject(new
            {
                topN = options.TopN,
                includeSchema = options.IncludeSchema,
                intents = perIntent
            }, _jsonSettings);
        }

        private static string BuildSingleIntentRecommendations(string intent, string queryString)
        {
            var options = ReadRecommendOptions(queryString);

            if (string.IsNullOrWhiteSpace(intent))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing required parameter: intent",
                    details: new { example = "/skills/recommend?intent=create+cube&topN=10&includeSchema=true" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }

            var context = new RecommendContext();
            var ranked = RankIntent(intent, options.TopN, context);
            var response = new
            {
                intent,
                expandedKeywords = ranked.ExpandedKeywords,
                topN = options.TopN,
                includeSchema = options.IncludeSchema,
                totalMatches = ranked.TotalMatches,
                results = BuildRecommendationEntries(ranked, options, context)
            };

            if (options.Wire == WireV2)
            {
                // v2's recommend keeps the same envelope, only reshaping the per-skill schema, so it's described by the same
                // `flags` / `defaults` contract as the manifest. Declared explicitly here rather than left implicit:
                // a caller that requested v2 but silently got v1 would read a missing `flags` array as "no flags set" --
                // treating a skill that mutates something as harmless -- and this echo exists to make that misreading impossible.
                return JsonConvert.SerializeObject(new
                {
                    response.intent,
                    response.expandedKeywords,
                    response.topN,
                    response.includeSchema,
                    response.totalMatches,
                    wire = "v2",
                    metaUrl = MetaEndpointPath,
                    defaults = BuildWireDefaults(),
                    // Null under `full`, and v2 drops nulls -- so under the default profile it costs nothing.
                    // See SurfaceProfilePrunedHint for why a ranking-style endpoint must state this.
                    surfaceProfile = SkillsSurfaceProfile.IsFull ? null : SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SkillsSurfaceProfile.IsFull ? null : SurfaceProfilePrunedHint,
                    response.results
                }, _jsonSettingsV2);
            }

            // The scoring stage already skipped hidden skills, so a non-full profile silently shortens this ranking.
            // Same rationale as the chain envelope, and the same byte-stability branch: v1 serialization writes out null,
            // so `full` must never touch these extra fields.
            if (!SkillsSurfaceProfile.IsFull)
            {
                return JsonConvert.SerializeObject(new
                {
                    response.intent,
                    response.expandedKeywords,
                    response.topN,
                    response.includeSchema,
                    response.totalMatches,
                    surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                    surfaceProfileHint = SurfaceProfilePrunedHint,
                    response.results
                }, _jsonSettings);
            }

            return JsonConvert.SerializeObject(response, _jsonSettings);
        }

        private readonly struct RecommendOptions
        {
            public readonly int TopN;
            public readonly bool IncludeSchema;
            public readonly int Wire;

            public RecommendOptions(int topN, bool includeSchema, int wire)
            {
                TopN = topN;
                IncludeSchema = includeSchema;
                Wire = wire;
            }
        }

        private static RecommendOptions ReadRecommendOptions(string queryString)
        {
            var filters = ParseQueryString(queryString);
            int topN = 10;
            bool includeSchema = false;
            if (filters.TryGetValue("topn", out var n) && int.TryParse(n, out var parsed)) topN = Mathf.Clamp(parsed, 1, 50);
            if (filters.TryGetValue("includeschema", out var inc))
                includeSchema = inc.Equals("true", StringComparison.OrdinalIgnoreCase) || inc == "1";
            return new RecommendOptions(topN, includeSchema, ResolveWireVersion(filters));
        }

        /// <summary>Per-request state shared by every intent of one call.</summary>
        private sealed class RecommendContext
        {
            public readonly IReadOnlyDictionary<string, SkillTelemetryService.RecommendationHealth> HealthBySkill =
                SkillTelemetryService.GetRecommendationHealth();

            // null while the package list is still refreshing asynchronously -- for why that means "skip the check" rather than "go find out,"
            // see HasUninstalledPackage.
            public readonly Dictionary<string, bool> PackageCache = PackageManagerHelper.InstalledPackages != null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : null;
        }

        private sealed class RankedIntent
        {
            public string[] ExpandedKeywords;
            public int TotalMatches;
            public List<(SkillInfo skill, int score, int semanticScore, List<string> matchedOn, SkillTelemetryService.RecommendationHealth health)> Top;
        }

        private static RankedIntent RankIntent(string intent, int topN, RecommendContext context)
        {
            var rawKeywords = intent.ToLowerInvariant().Split(new[] { ' ', '+', '_', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var keywords = ExpandIntent(rawKeywords);
            var rawSet = new HashSet<string>(rawKeywords, StringComparer.OrdinalIgnoreCase);
            // Text-scoring vocabulary: no stopwords; operation verbs only as whole name tokens (handled inline below).
            var textKeywords = keywords.Where(k => !_intentStopwords.Contains(k)).ToArray();
            var scored = new List<(SkillInfo skill, int score, int semanticScore, List<string> matchedOn, SkillTelemetryService.RecommendationHealth health)>();

            // Precomputes operation and category matches (supports Chinese substrings)
            var matchedOps = ExtractOperations(rawKeywords);
            var explicitCats = ExtractCategories(rawKeywords);
            var matchedCats = new HashSet<SkillCategory>(explicitCats);
            var hintWords = new List<string>();
            var hintedCats = ExtractComponentTypeHints(rawKeywords, hintWords);
            matchedCats.UnionWith(hintedCats);
            // Generic component_* rule: the intent talks about a component type but names no other module.
            bool genericRule = hintedCats.Contains(SkillCategory.Component) && !explicitCats.Any(_genericRuleSuppressors.Contains);
            // No verb at all ("box collider center size"): the caller wants component property access either way.
            bool genericAnyOp = genericRule && matchedOps.Count == 0;
            // "add public field to script": an explicit script intent about something *inside* a file is an edit, not a new file.
            bool scriptEditRule = explicitCats.Contains(SkillCategory.Script) && rawKeywords.Any(_scriptContentWords.Contains);

            // Input for intent alignment (see ApplyIntentAlignment). Drawn from the raw intent words rather than the synonym-expanded set:
            // expansion exists to loosen keyword matching, and letting it decide "does the caller want to observe or to change"
            // would count verbs the caller never wrote (材质 -> material, hierarchy -> parent/child/gameobject).
            bool readIntent = rawKeywords.Any(_readIntentVerbs.Contains);
            bool writeIntent = rawKeywords.Any(_writeIntentVerbs.Contains);
            bool sampleIntent = rawKeywords.Any(_sampleIntentWords.Contains);

            foreach (var s in VisibleSkills())
            {
                int score = 0;
                var matchedOn = new List<string>();
                var nameLower = s.NameLower;
                var descLower = s.DescriptionLower;

                var nameTokens = nameLower.Split('_');
                foreach (var kw in textKeywords)
                {
                    bool raw = rawSet.Contains(kw);          // synonym expansions earn less than the caller's own words
                    bool opVerb = _operationKeywords.ContainsKey(kw);
                    bool wholeToken = Array.IndexOf(nameTokens, kw) >= 0;
                    if (wholeToken)
                    {
                        score += raw ? 3 : 1;
                        matchedOn.Add($"name:{kw}");
                    }
                    else if (!opVerb && nameLower.Contains(kw))
                    {
                        score += 1;
                        matchedOn.Add($"name~{kw}");
                    }
                    if (opVerb)
                        continue;                            // verbs never earn tag/description credit
                    if (s.TagsLower != null && s.TagsLower.Any(t => t.Equals(kw, StringComparison.Ordinal)))
                    {
                        score += raw ? 2 : 1;
                        matchedOn.Add($"tag:{kw}");
                    }
                    if (kw.Length >= 4 && descLower.Contains(kw))
                    {
                        score += 1;
                        matchedOn.Add($"desc:{kw}");
                    }
                }

                if (genericRule && s.Category == SkillCategory.Component)
                {
                    if ((genericAnyOp || matchedOps.Contains(SkillOperation.Modify)) && Array.IndexOf(_genericModifySkills, s.Name) >= 0 ||
                        (genericAnyOp || matchedOps.Contains(SkillOperation.Query)) && Array.IndexOf(_genericQuerySkills, s.Name) >= 0 ||
                        matchedOps.Contains(SkillOperation.Create) && Array.IndexOf(_genericCreateSkills, s.Name) >= 0)
                    {
                        score += 4;
                        matchedOn.Add("generic:component");
                    }
                }
                if (scriptEditRule && Array.IndexOf(_scriptEditSkills, s.Name) >= 0)
                {
                    score += 4;
                    matchedOn.Add("generic:script_edit");
                }
                if (hintWords.Count > 0 && hintedCats.Contains(s.Category) && s.Category != SkillCategory.Component &&
                    s.Operation != 0 && matchedOps.Any(op => s.Operation.HasFlag(op)))
                {
                    // A dedicated module for the hinted type (light_*, camera_*, animator_*) with the requested operation.
                    score += 2;
                    matchedOn.Add($"hint:{hintWords[0]}");
                }

                // category bonus
                if (matchedCats.Count > 0 && s.Category != SkillCategory.Uncategorized && matchedCats.Contains(s.Category))
                {
                    score += 2;
                    matchedOn.Add($"category:{s.Category}");
                }

                // operation bonus
                if (matchedOps.Count > 0 && s.Operation != 0)
                {
                    foreach (var op in matchedOps)
                    {
                        if (s.Operation.HasFlag(op))
                        {
                            score += 2;
                            matchedOn.Add($"operation:{op}");
                            break;
                        }
                    }
                }

                if (score > 0)
                {
                    // Only adjusts skills that already matched something. Applying the read-intent bonus to zero-score skills
                    // would pull every read-only skill in the registry into the results based on intent alone.
                    score = ApplyIntentAlignment(s, score, readIntent, writeIntent, sampleIntent, context.PackageCache, matchedOn);
                    context.HealthBySkill.TryGetValue(s.Name, out var health);
                    var adjustedScore = Math.Max(1, score - (health?.Penalty ?? 0));
                    scored.Add((s, adjustedScore, score, matchedOn, health));
                }
            }

            var results = scored.OrderByDescending(x => x.score)
                .ThenByDescending(x => x.semanticScore)
                // Stable tie-breaking. Without it, same-score skills would come out in reflection discovery order,
                // and that order differs across projects and across domain reloads --
                // the same intent would rank the same candidates differently for no reason.
                .ThenBy(x => x.skill.Name, StringComparer.Ordinal)
                .Take(topN).ToList();

            return new RankedIntent
            {
                ExpandedKeywords = keywords.Length > rawKeywords.Length ? keywords : null,
                TotalMatches = scored.Count,
                Top = results
            };
        }

        private const string UnavailableMissingPackage = "missing_package";

        /// <summary>
        /// One ranked candidate. A named type rather than an anonymous one only so the availability fields can be omitted
        /// when unset: v1 serialization writes nulls, and an always-present key would change every entry's bytes.
        /// Order pins the wire order to the anonymous shape this replaced.
        /// </summary>
        private sealed class RecommendationEntry
        {
            [JsonProperty(Order = 0)] public string name;
            // Set when the candidate cannot run here as-is; its ranking is untouched (ApplyIntentAlignment already demotes it).
            [JsonProperty(Order = 1, NullValueHandling = NullValueHandling.Ignore)] public string unavailable;
            [JsonProperty(Order = 2, NullValueHandling = NullValueHandling.Ignore)] public string[] missingPackages;
            [JsonProperty(Order = 3)] public string description;
            [JsonProperty(Order = 4)] public string category;
            [JsonProperty(Order = 5)] public int score;
            [JsonProperty(Order = 6)] public int semanticScore;
            [JsonProperty(Order = 7)] public string confidence;
            [JsonProperty(Order = 8)] public string[] matchedOn;
            [JsonProperty(Order = 9)] public object telemetry;
            [JsonProperty(Order = 10)] public int telemetryPenalty;
            [JsonProperty(Order = 11)] public string[] warnings;
            [JsonProperty(Order = 12)] public object schema;
        }

        /// <summary>
        /// Projects ranked candidates into result entries. A candidate whose declared package is missing is marked
        /// <c>unavailable: "missing_package"</c> with the ids to install. Skills hidden by the surface profile never reach
        /// this point (ranking enumerates <see cref="VisibleSkills"/> only), so there is no hidden-skill mark to give.
        /// </summary>
        private static List<RecommendationEntry> BuildRecommendationEntries(RankedIntent ranked, RecommendOptions options, RecommendContext context)
        {
            return ranked.Top.Select(x =>
            {
                var missingPackages = FindUninstalledPackages(x.skill, context.PackageCache);
                return new RecommendationEntry
                {
                    name = x.skill.Name,
                    unavailable = missingPackages != null ? UnavailableMissingPackage : null,
                    missingPackages = missingPackages?.ToArray(),
                    description = GetEffectiveDescription(x.skill),
                    category = x.skill.Category != SkillCategory.Uncategorized ? x.skill.Category.ToString() : null,
                    score = x.score,
                    semanticScore = x.semanticScore,
                    confidence = ScoreToConfidence(x.score),
                    matchedOn = x.matchedOn.Distinct().ToArray(),
                    telemetry = x.health == null ? null : new
                    {
                        window = "7d",
                        calls = x.health.Calls,
                        errors = x.health.Errors,
                        errorRate = x.health.ErrorRate,
                        avgMs = x.health.AvgMs,
                    },
                    telemetryPenalty = x.health?.Penalty ?? 0,
                    warnings = x.health != null && x.health.Warnings.Length > 0 ? x.health.Warnings : null,
                    schema = options.IncludeSchema
                        ? (options.Wire == WireV2 ? BuildSkillSchemaForRecommendV2(x.skill) : BuildSkillSchemaForRecommend(x.skill))
                        : null
                };
            }).ToList();
        }

        /// <summary>Trims each intent and drops blanks and case-insensitive repeats, keeping first-seen order.</summary>
        private static List<string> DistinctIntents(IEnumerable<string> intents)
        {
            var distinct = new List<string>();
            if (intents == null)
                return distinct;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var intent in intents)
            {
                var trimmed = intent?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                    distinct.Add(trimmed);
            }
            return distinct;
        }

        /// <summary>
        /// Three corrections applied on top of the keyword score, each addressing an observed bad ranking:
        ///
        /// <list type="bullet">
        /// <item><b>Read/write intent alignment.</b> "read current camera properties inspect fov" once ranked
        /// camera_set_properties above camera_get_properties -- a setter's description inevitably mentions the properties a getter
        /// returns, and happens to mention them more. Now, an intent that's unambiguously "read" in shape favors read-only skills,
        /// and one that's unambiguously "write" pushes them down. Mixed or verb-less intents are left untouched: guessing there is worse than not.</item>
        /// <item><b>Demoting Sample.</b> Skills under <see cref="SkillCategory.Sample"/>
        /// (create_cube, set_object_position, ...) are teaching duplicates of the real gameobject_* / camera_* skills,
        /// and their short names reliably win the name-substring bonus -- when an agent wants to move an object,
        /// set_object_position would outrank gameobject_set_transform. They're still reachable,
        /// but only enter the ranking when sample/demo/example genuinely appears in the intent.</item>
        /// <item><b>Uninstalled optional packages.</b> Recommending yooasset_* / probuilder_* for an ordinary material edit
        /// is worse than not recommending it: the skill is registered, so nothing warns the agent before the call fails for a missing package.</item>
        /// </list>
        ///
        /// <para>Deliberately does not rewrite anything. Keyword weights (name 3 / tag 2 / desc 1), category and operation bonuses,
        /// the telemetry penalty, and the sort key are all left untouched. Every adjustment is appended to <c>matchedOn</c>,
        /// so an unexpected ranking can be audited from the response alone; the result floor is 1,
        /// so no adjustment can strike a genuine keyword hit out of <c>totalMatches</c> -- it can only push it to the bottom.</para>
        /// </summary>
        private static int ApplyIntentAlignment(
            SkillInfo skill,
            int score,
            bool readIntent,
            bool writeIntent,
            bool sampleIntent,
            Dictionary<string, bool> packageCache,
            List<string> matchedOn)
        {
            int delta = 0;

            // readIntent != writeIntent means "exactly one holds," i.e. the unambiguous cases.
            if (skill.ReadOnly && readIntent != writeIntent)
            {
                if (readIntent)
                {
                    delta += 3;
                    matchedOn.Add("intent:read+3");
                }
                else
                {
                    delta -= 1;
                    matchedOn.Add("intent:write-1");
                }
            }

            if (!sampleIntent && skill.Category == SkillCategory.Sample)
            {
                // Halve rather than subtract a constant: whole-token name credit (create_cube for "create cube") lifts demo skills
                // above the real module, and a fixed -3 no longer clears that gap. A sample-word intent still reaches them untouched.
                delta -= (score + 1) / 2;
                matchedOn.Add("demoted:sample-half");
            }

            if (HasUninstalledPackage(skill, packageCache))
            {
                delta -= 5;
                matchedOn.Add("demoted:packageMissing-5");
            }

            return delta == 0 ? score : Math.Max(1, score + delta);
        }

        /// <summary>
        /// Whether this skill names an optional package that isn't installed yet. The mechanism matches the smoke test's skip gate
        /// (<c>TestSkills.EvaluateSmokeSkill</c>), including its empty-cache guard:
        /// <paramref name="packageCache"/> being null means
        /// <see cref="PackageManagerHelper.InstalledPackages"/>'s async refresh hasn't finished yet,
        /// and at that point the scorer would rather *demote no candidate at all* than answer based on a package list that doesn't exist yet --
        /// reading "don't know yet" as "not installed" would suppress every optional-package skill during the first few seconds of a session.
        ///
        /// This guard exists for correctness, not to save work. <c>IsPackageInstalled</c>'s miss path is
        /// <c>ResolveDirectly</c> -> <c>PackageInfo.FindForAssetPath("Packages/&lt;id&gt;")</c>,
        /// an in-memory registry lookup rather than a Package Manager client request -- so a single id is cheap enough,
        /// and <paramref name="packageCache"/> memoizes the result for the rest of *this request*,
        /// so a package shared by twenty skills is only resolved once. The cache is deliberately scoped per request:
        /// a longer-lived cache would keep answering "missing" even after the user installed the package.
        /// </summary>
        private static bool HasUninstalledPackage(SkillInfo skill, Dictionary<string, bool> packageCache) =>
            FindUninstalledPackages(skill, packageCache) != null;

        /// <summary>
        /// The declared packages of <paramref name="skill"/> that are not installed, or null when none is missing (or when
        /// <paramref name="packageCache"/> is null, the "package list not ready" signal of <see cref="HasUninstalledPackage"/>).
        /// Every result is memoized in <paramref name="packageCache"/> for the rest of the request.
        /// </summary>
        private static List<string> FindUninstalledPackages(SkillInfo skill, Dictionary<string, bool> packageCache)
        {
            if (packageCache == null || skill.RequiresPackages == null || skill.RequiresPackages.Length == 0)
                return null;

            List<string> missing = null;
            foreach (var packageId in skill.RequiresPackages)
            {
                if (string.IsNullOrWhiteSpace(packageId))
                    continue;

                if (!packageCache.TryGetValue(packageId, out var installed))
                {
                    installed = PackageManagerHelper.IsPackageInstalled(packageId);
                    packageCache[packageId] = installed;
                }

                if (!installed && (missing == null || !missing.Contains(packageId)))
                    (missing ??= new List<string>()).Add(packageId);
            }

            return missing;
        }

        private static string ScoreToConfidence(int score)
        {
            if (score >= 10) return "high";
            if (score >= 5) return "medium";
            return "low";
        }

        private static object BuildSkillSchemaForRecommend(SkillInfo s) => new
        {
            parameters = BuildParameterSchema(s),
            outputs = GetEffectiveOutputs(s),
            requiresInput = s.RequiresInput,
            tags = s.Tags,
            operation = FormatOperation(s.Operation),
            riskLevel = s.RiskLevel,
            readOnly = s.ReadOnly,
            mutatesScene = s.MutatesScene,
            mutatesAssets = s.MutatesAssets,
            requiresPackages = s.RequiresPackages,
            mode = SkillsModeManager.SkillModeToWire(s.Mode),
            approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
        };

        /// <summary>
        /// The <c>?wire=v2</c> form of <see cref="BuildSkillSchemaForRecommend"/>: uses the same <c>flags</c> array and the same
        /// "omit the default" rule as a v2 manifest entry, so an agent only ever has to parse one shape across every endpoint.
        /// Note it outputs all seven flags, while v1 only carries three booleans (readOnly / mutatesScene / mutatesAssets) --
        /// strictly more information in fewer bytes; nothing v1 reported is lost.
        /// </summary>
        private static object BuildSkillSchemaForRecommendV2(SkillInfo s) => new
        {
            parameters = BuildParameterSchema(s),
            outputs = GetEffectiveOutputs(s),
            requiresInput = s.RequiresInput,
            tags = s.Tags,
            operation = FormatOperation(s.Operation),
            flags = BuildSkillFlags(s),
            riskLevel = NonDefaultRiskLevel(s),
            requiresPackages = s.RequiresPackages,
            mode = SkillsModeManager.SkillModeToWire(s.Mode),
            approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
        };

        // ========== Skill dependency chain ==========

        /// <summary>
        /// Builds an operation chain via BFS along the Outputs -> RequiresInput relationship.
        /// Given a target output field, finds every skill that produces it and its dependencies.
        /// </summary>
        public static string GetSkillChain(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            string targetOutput = "";
            int maxDepth = 3;
            if (filters.TryGetValue("output", out var o)) targetOutput = o;
            if (filters.TryGetValue("maxdepth", out var d) && int.TryParse(d, out var dp))
                maxDepth = Mathf.Clamp(dp, 1, 10);

            if (string.IsNullOrWhiteSpace(targetOutput))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.MissingParam,
                    "Missing required parameter: output",
                    details: new { example = "/skills/chain?output=instanceId&maxDepth=3" },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            }

            // BFS: first find the skills that produce the target field, then trace back through their RequiresInput
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string field, int depth)>();
            queue.Enqueue((targetOutput, 0));
            visited.Add(targetOutput);

            var producers = new List<object>();

            while (queue.Count > 0)
            {
                var (field, depth) = queue.Dequeue();

                if (!_outputIndex.TryGetValue(field, out var fieldProducers))
                    continue;

                foreach (var s in fieldProducers)
                {
                    // _outputIndex is a complete index over the entire registry, so filtering happens here rather than at build time:
                    // naming a skill the current profile hides would send the agent down a chain whose first step answers
                    // SURFACE_EXCLUDED. An excluded producer is skipped entirely -- its RequiresInput fields
                    // are not enqueued either, because a step that can't run can't be part of the plan.
                    if (SkillsSurfaceProfile.IsExcluded(s))
                        continue;

                    producers.Add(new
                    {
                        skill = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        depth,
                        producesField = field,
                        outputs = GetEffectiveOutputs(s),
                        requiresInput = s.RequiresInput
                    });

                    // Enqueues the RequiresInput fields, for use at the next depth level
                    if (depth < maxDepth && s.RequiresInput != null)
                    {
                        foreach (var req in s.RequiresInput)
                        {
                            if (!visited.Contains(req))
                            {
                                visited.Add(req);
                                queue.Enqueue((req, depth + 1));
                            }
                        }
                    }
                }
            }

            // Under `full`, nothing was trimmed and the payload is byte-for-byte identical to v1. Under a non-full profile, the producers list above
            // has already silently dropped some steps, and this envelope is the only place that can explain it: otherwise a shortened chain would be read as
            // "Unity has no way to produce this field," and the agent would report something impossible when the skill was actually just hidden.
            // Note this envelope serializes with _jsonSettings, which writes out null -- hence the branch here rather than a field that's simply null.
            if (SkillsSurfaceProfile.IsFull)
            {
                return JsonConvert.SerializeObject(new
                {
                    targetOutput,
                    maxDepth,
                    totalProducers = producers.Count,
                    producers
                }, _jsonSettings);
            }

            return JsonConvert.SerializeObject(new
            {
                targetOutput,
                maxDepth,
                totalProducers = producers.Count,
                surfaceProfile = SkillsSurfaceProfile.CurrentWire,
                surfaceProfileHint = SurfaceProfilePrunedHint,
                producers
            }, _jsonSettings);
        }

        /// <summary>
        /// Attached to discovery envelopes that "silently return fewer skills under a non-full surface profile"
        /// (<c>/skills/recommend</c>, <c>/skills/chain</c>). Both are ranking and traversal rather than enumeration,
        /// so a trimmed result is indistinguishable from an empty one -- without this hint, an agent would conclude the operation is impossible and tell the user so,
        /// when in fact the user hid it, and can also un-hide it.
        /// </summary>
        private const string SurfaceProfilePrunedHint = "Results were pruned by the user's surface profile — a skill missing here may exist but be hidden, so do not conclude Unity cannot do it. GET /health for the active profile; only the user can switch it back to \"full\" in the UnitySkills panel.";

        internal static string[] FormatOperationForPlanning(SkillOperation op)
        {
            return FormatOperation(op);
        }
    }
}

// Producer:Betsy
