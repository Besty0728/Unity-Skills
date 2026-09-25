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
        public static string GetManifest()
        {
            Initialize();
            var cached = _cachedManifest;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedManifest != null) return _cachedManifest;

                var manifest = BuildManifest(VisibleSkills(), filtered: false, filters: null, manifestType: "manifest");
                _cachedManifest = JsonConvert.SerializeObject(manifest, _jsonSettings);
                return _cachedManifest;
            }
        }

        public static string GetSchema()
        {
            Initialize();
            var cached = _cachedSchema;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedSchema != null) return _cachedSchema;

                var schema = BuildManifest(VisibleSkills(), filtered: false, filters: null, manifestType: "schema");
                _cachedSchema = JsonConvert.SerializeObject(schema, _jsonSettings);
                return _cachedSchema;
            }
        }

        /// <summary>
        /// The catalog layer -- what bare <c>GET /skills</c> (and <c>?brief=1</c>) now returns.
        /// Cached as a single string just like the full manifest: under the same skill set the payload bytes are stable,
        /// so the HTTP thread's fast path can return it directly with a stable ETag.
        /// </summary>
        public static string GetBrief()
        {
            Initialize();
            var cached = _cachedBrief;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedBrief != null) return _cachedBrief;

                _cachedBrief = JsonConvert.SerializeObject(BuildBriefManifest(), _jsonSettings);
                return _cachedBrief;
            }
        }

        /// <summary>
        /// <c>GET /skills/meta</c> -- the session-constant half of the manifest envelope (the category and operation enums,
        /// reserved request-body parameter names, the workflow-tracked skill list), plus the field defaults
        /// that <c>?wire=v2</c> entries omit. The v2 payload drops these blocks and points here, so an agent pays this cost once per session,
        /// rather than on every scoped fetch.
        ///
        /// Except for <c>workflowTrackedSkills</c>, everything here satisfies "session constant": that field is filtered by the surface profile
        /// (see <see cref="VisibleWorkflowTrackedSkills"/>), so it changes when the user switches profiles --
        /// the cache and its ETag are both dropped on a switch, and <c>metaHint</c> says as much.
        /// Removing the filtering to restore literal constancy would mean sending out names the user chose to hide.
        /// </summary>
        public static string GetMeta() => GetMeta(WireV1);

        /// <summary>
        /// <c>?wire=v2</c> drops <c>workflowTrackedSkills</c> (roughly 10 KB of names): every v2 skill entry already carries the
        /// <c>tracksWorkflow</c> flag, so the list is redundant for a v2 caller. The v1 body is byte-for-byte unchanged.
        /// </summary>
        public static string GetMeta(int wire)
        {
            Initialize();
            if (wire == WireV2)
            {
                var cachedV2 = _cachedMetaV2;
                if (cachedV2 != null) return cachedV2;
                lock (_initLock)
                {
                    if (_cachedMetaV2 != null) return _cachedMetaV2;
                    _cachedMetaV2 = JsonConvert.SerializeObject(new
                    {
                        manifestType = "meta",
                        schemaVersion = SkillSchemaVersion,
                        wire = "v2",
                        version = SkillsLogger.Version,
                        defaults = BuildWireDefaults(),
                        categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                        operationTypes = Enum.GetNames(typeof(SkillOperation)),
                        reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                        metaHint = "SESSION CONSTANTS — fetch once, reuse for the whole session. wire=v2 omits workflowTrackedSkills; read each skill's flags instead."
                    }, _jsonSettingsV2);
                    return _cachedMetaV2;
                }
            }
            var cached = _cachedMeta;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedMeta != null) return _cachedMeta;

                _cachedMeta = JsonConvert.SerializeObject(new
                {
                    manifestType = "meta",
                    schemaVersion = SkillSchemaVersion,
                    version = SkillsLogger.Version,
                    defaults = BuildWireDefaults(),
                    categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                    operationTypes = Enum.GetNames(typeof(SkillOperation)),
                    reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                    workflowTrackedSkills = VisibleWorkflowTrackedSkills(),
                    // Deliberately no surfaceProfile field here: the profile can be switched by the user at any time, and mixing a live value into
                    // a payload that says "fetch once per session" would only let someone read a stale value from here.
                    // /health is its sole authority, and every rejection response carries it too. This is different from workflowTrackedSkills
                    // being filtered by profile -- a name the user withdrew must never be sent out;
                    // the hint below states the consequence (this one block may change mid-session) rather than concealing it.
                    metaHint = "SESSION CONSTANTS — fetch once, reuse for the whole session. The enums, reserved parameters and defaults change only with the plugin version; 'workflowTrackedSkills' lists only what the active surface profile offers, so it moves (and the ETag changes) if the user switches profile mid-session. 'defaults' states the values ?wire=v2 omits from skill entries: a missing riskLevel is \"low\", a missing supportsDryRun is true, and a flag absent from 'flags' is false. For the live surface profile read 'surfaceProfile' on GET /health — it is user-switchable and deliberately not mirrored here."
                }, _jsonSettingsV2);
                return _cachedMeta;
            }
        }

        /// <summary>Whether a skill with the given name is registered.</summary>
        public static bool HasSkill(string name)
        {
            Initialize();
            return !string.IsNullOrEmpty(name) && _skills.ContainsKey(name);
        }

        private static string ToSnakeCase(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, "([a-z])([A-Z])", "$1_$2").ToLower();

        private static string GetJsonType(Type t)
        {
            var underlying = Nullable.GetUnderlyingType(t) ?? t;
            if (underlying == typeof(string)) return "string";
            if (underlying == typeof(int) || underlying == typeof(long)) return "integer";
            if (underlying == typeof(float) || underlying == typeof(double)) return "number";
            if (underlying == typeof(bool)) return "boolean";
            if (underlying.IsArray) return "array";
            return "object";
        }

        /// <summary>
        /// A parameter named in RequiredParams, or named literally in RequiresInput, is required whatever its CLR default.
        /// Otherwise, only a parameter with neither a default value nor null acceptance counts as required.
        /// </summary>
        private static bool IsParameterRequired(SkillInfo skill, ParameterInfo p)
        {
            if (ContainsParameter(skill?.RequiredParams, p.Name) || ContainsParameter(skill?.RequiresInput, p.Name))
                return true;
            if (p.HasDefaultValue) return false;
            if (p.ParameterType.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null)
                return true;
            return false;
        }

        /// <summary>The SkillParamAttribute notes of a skill's parameters, index-aligned; null when none carries one.</summary>
        private static string[] ReadParameterDescriptions(ParameterInfo[] parameters)
        {
            string[] descriptions = null;
            for (int i = 0; i < parameters.Length; i++)
            {
                SkillParamAttribute note;
                try { note = parameters[i].GetCustomAttribute<SkillParamAttribute>(); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(note?.Description))
                    continue;
                descriptions ??= new string[parameters.Length];
                descriptions[i] = note.Description;
            }
            return descriptions;
        }

        private static string GetParameterDescription(SkillInfo skill, int parameterIndex) =>
            skill?.ParameterDescriptions != null && parameterIndex < skill.ParameterDescriptions.Length
                ? skill.ParameterDescriptions[parameterIndex]
                : null;

        private static string[] FormatOperation(SkillOperation op)
        {
            if (op == 0) return null;
            var list = new List<string>();
            foreach (SkillOperation flag in Enum.GetValues(typeof(SkillOperation)))
            {
                if (flag != 0 && op.HasFlag(flag))
                    list.Add(flag.ToString());
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        // ========== Filtered manifest ==========

        /// <summary>
        /// Returns the skill manifest filtered by query parameters.
        /// Supports category, operation, tags, readOnly, q (text search).
        /// </summary>
        public static string GetFilteredManifest(string queryString) => BuildFilteredOutput(queryString, "manifest", out _);

        /// <summary>
        /// Same filter conditions as GetFilteredManifest (category/operation/tags/readOnly/q),
        /// but marks the payload's manifestType as "schema" -- backing GET /skills/schema?category=...
        /// (a scoped schema, so needing just one category doesn't require pulling the whole roughly 707KB schema).
        /// </summary>
        public static string GetFilteredSchema(string queryString) => BuildFilteredOutput(queryString, "schema", out _);

        /// <summary>
        /// On top of <see cref="GetFilteredManifest(string)"/>, additionally reports whether the returned string is a rejection response
        /// or a manifest. The HTTP layer needs this distinction, and can't recover it from the payload without sniffing the text, for two reasons:
        /// an error must answer 400 rather than 200; and it must not get an ETag -- a cached 400 response body would give the client's next
        /// If-None-Match a 304 with no body at all, which reads as "your query was fine, and nothing changed."
        /// </summary>
        public static string GetFilteredManifest(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "manifest", out isError);

        /// <summary>The schema counterpart of <see cref="GetFilteredManifest(string, out bool)"/>.</summary>
        public static string GetFilteredSchema(string queryString, out bool isError) =>
            BuildFilteredOutput(queryString, "schema", out isError);

        private static string FindSkillSelectorLikeKey(Dictionary<string, string> rawFilters)
        {
            foreach (var key in rawFilters.Keys)
                if (_skillSelectorLikeKeys.Contains(key) && !_recognizedFilterKeys.Contains(key))
                    return key;
            return null;
        }

        private static string BuildSkillSelectorKeyError(string key, string value)
        {
            return SkillErrorResponse.Build(
                SkillErrorCode.UnknownParam,
                $"Unknown query parameter '{key}'. To fetch specific skills use names=<exact_name,...>; to search use q=<fragment>.",
                details: new
                {
                    parameter = key,
                    value,
                    validKeys = _recognizedFilterKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                    example = $"/skills/schema?names={value}&wire=v2"
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        private static Dictionary<string, string> StripUnrecognizedFilterKeys(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || filters.Keys.All(k => _recognizedFilterKeys.Contains(k)))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (_recognizedFilterKeys.Contains(kv.Key))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// Strips <see cref="_surfaceSelectionKeys"/>, leaving only keys that actually narrow the skill set.
        /// Returns the argument instance unchanged when there's nothing to strip -- this alone is what keeps the <c>filters</c> object
        /// echoed for every pre-v2 query byte-for-byte identical.
        /// </summary>
        private static Dictionary<string, string> StripSurfaceSelectionKeys(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || !filters.Keys.Any(k => _surfaceSelectionKeys.Contains(k)))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (!_surfaceSelectionKeys.Contains(kv.Key))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        private static bool IsQueryFlagSet(Dictionary<string, string> filters, string key)
        {
            return filters.TryGetValue(key, out var value) && value != null &&
                (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        // Wire format version. v1 is the legacy payload and stays the default forever: an unrecognized ?wire value resolves to v1 rather than erroring,
        // so a typo never silently sends a caller a shape it can't parse.
        private const int WireV1 = 1;
        private const int WireV2 = 2;

        internal static int ResolveWireVersion(string queryString) => ResolveWireVersion(ParseQueryString(queryString ?? string.Empty));

        private static int ResolveWireVersion(Dictionary<string, string> filters)
        {
            if (filters.TryGetValue("wire", out var raw) && raw != null)
            {
                var value = raw.Trim();
                if (value == "2" || value.Equals("v2", StringComparison.OrdinalIgnoreCase))
                    return WireV2;
            }
            return WireV1;
        }

        /// <summary>Which cached string a GET in the manifest family answers with.</summary>
        private enum GetSurface
        {
            /// <summary>_cachedManifest / _cachedSchema -- the untouched full v1 payload.</summary>
            FullV1,
            /// <summary>_cachedBrief -- bare GET /skills, and ?brief=1 on either path.</summary>
            Brief,
            /// <summary>_cachedMeta -- GET /skills/meta.</summary>
            Meta,
            /// <summary>_filteredOutputCache -- every scoped, summary, or wire=v2 variant.</summary>
            Keyed
        }

        private const string BriefCacheKey = "manifest|__brief__";
        private const string MetaCacheKey = "meta|__full__";

        /// <summary>
        /// The single source of truth for "which surface this query selects, and which cache key to use." The main-thread builder
        /// (<see cref="BuildFilteredOutput"/>) and the HTTP thread's fast path (<see cref="BuildGetCacheKey"/>)
        /// both call it -- the moment the two disagree, the fast path will answer a request for this surface with bytes from a different one.
        ///
        /// Given that <paramref name="filters"/> has already had irrelevant keys stripped, the determination order is:
        /// <list type="number">
        /// <item>meta path -> <see cref="GetSurface.Meta"/>.</item>
        /// <item>?brief is true, or a bare /skills request (no narrowing filter and no ?full) ->
        /// <see cref="GetSurface.Brief"/>. This is the v2.7 default flip: bare GET /skills used to return the roughly 707KB
        /// manifest, and now returns the catalog; ?full=1 restores the old behavior.</item>
        /// <item>No narrowing key at all and wire is v1 -> <see cref="GetSurface.FullV1"/>
        /// (bare /skills/schema, and /skills?full=1).</item>
        /// <item>Everything else -> <see cref="GetSurface.Keyed"/>.</item>
        /// </list>
        /// Brief is independent of wire (it carries no per-skill flags that could be trimmed), so both wire versions share one cache entry,
        /// and therefore share one ETag.
        /// </summary>
        private static string ResolveGetSurface(string manifestType, Dictionary<string, string> filters, out GetSurface surface)
        {
            if (manifestType == "meta")
            {
                surface = GetSurface.Meta;
                return ResolveWireVersion(filters) == WireV2 ? MetaCacheKey + "|wire=v2" : MetaCacheKey;
            }

            bool hasNarrowingFilter = StripSurfaceSelectionKeys(filters).Count > 0;

            if (IsQueryFlagSet(filters, "brief") ||
                (!hasNarrowingFilter && manifestType != "schema" && !IsQueryFlagSet(filters, "full")))
            {
                surface = GetSurface.Brief;
                return BriefCacheKey;
            }

            if (!hasNarrowingFilter && ResolveWireVersion(filters) == WireV1)
            {
                surface = GetSurface.FullV1;
                return manifestType + "|__full__";
            }

            surface = GetSurface.Keyed;
            // ?full is stripped from the key, ?wire is not. Once a request reaches Keyed, ?full's one job
            // (defeating the brief default in the branch above) is already done, and it can no longer affect the bytes -- keeping it would only split the same payload
            // into two several-hundred-KB entries with identical content (/skills/schema?wire=v2 and ?full=1&wire=v2).
            // ?wire, on the other hand, really does select different bytes, and must stay in the identifier.
            return BuildFilteredOutputCacheKey(StripFullFlagKey(filters), manifestType);
        }

        /// <summary>
        /// Strips only the <c>full</c> key, keeping the rest in insertion order. Like
        /// <see cref="StripSurfaceSelectionKeys"/>, returns the argument instance directly when there's nothing to change.
        /// </summary>
        private static Dictionary<string, string> StripFullFlagKey(Dictionary<string, string> filters)
        {
            if (filters.Count == 0 || !filters.ContainsKey("full"))
                return filters;

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in filters)
            {
                if (!string.Equals(kv.Key, "full", StringComparison.OrdinalIgnoreCase))
                    result[kv.Key] = kv.Value;
            }
            return result;
        }

        /// <summary>
        /// Rejects unknown <c>?category=</c> / <c>?operation=</c> values, and a blank narrowing key,
        /// rather than silently filtering with it.
        ///
        /// <para>Both filters used to silently "fail closed": an unrecognized category can never equal any
        /// <c>Category.ToString()</c>, and an unparseable operation makes every <c>Enum.TryParse</c> below fail --
        /// so the answer is 200 plus <c>skills: []</c>, byte-for-byte identical to "this category is genuinely
        /// empty under the current surface profile." An agent reading that concludes the module doesn't exist in this project and stops looking,
        /// when it actually just typo'd <c>?category=GameObjects</c>.</para>
        ///
        /// <para>Must run before <see cref="ResolveGetSurface"/>. category/operation are narrowing keys,
        /// so a bad value would fall into <see cref="GetSurface.Keyed"/>, and mint -- then permanently hold --
        /// a manifest-sized cache entry keyed on that typo.</para>
        ///
        /// <para>Returns null when every value present is acceptable, including when there's no narrowing key at all.
        /// Never rejects a value the filters below would actually match, so no legitimate query's bytes ever change.</para>
        /// </summary>
        private static string ValidateNarrowingFilterValues(Dictionary<string, string> filters)
        {
            var invalidKey = FindInvalidNarrowingFilterKey(filters);
            if (invalidKey == null)
                return null;

            var value = filters[invalidKey];

            object details;
            if (string.Equals(invalidKey, "category", StringComparison.OrdinalIgnoreCase))
                details = new { parameter = invalidKey, value, validCategories = _validCategoryNames };
            else if (string.Equals(invalidKey, "operation", StringComparison.OrdinalIgnoreCase))
                details = new { parameter = invalidKey, value, validOperations = _validOperationNames };
            else
                details = new
                {
                    parameter = invalidKey,
                    value,
                    hint = $"'{invalidKey}' was written with no value. Give it one or drop the key entirely — a blank is neither an omission nor a usable value, and answering as if the key were absent is what let a mistyped query look like it worked.",
                };

            return SkillErrorResponse.Build(
                SkillErrorCode.SemanticInvalid,
                $"Invalid value '{value}' for parameter '{invalidKey}'.",
                details: details,
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// Returns the narrowing key whose value the filters below can't use; returns null when every value is acceptable.
        /// Split out of <see cref="ValidateNarrowingFilterValues"/> so the HTTP thread's fast path can ask
        /// the same question: it never touches the Unity API, never logs, never calls Initialize() -- exactly what the fast-path zone's cross-thread contract requires.
        /// The fast path only needs to know "should this query be rejected," never the error body, so building the payload still happens on the main thread.
        ///
        /// Every check here must be *exactly identical* to what the corresponding filter does in <see cref="BuildFilteredOutput"/>,
        /// and never stricter: rejecting a value here that the filter would actually match would turn a normal 200 into a 400.
        /// </summary>
        private static string FindInvalidNarrowingFilterKey(Dictionary<string, string> filters)
        {
            if (filters.Count == 0)
                return null;

            if (filters.TryGetValue("category", out var category) &&
                !_validCategoryNames.Contains(category, StringComparer.OrdinalIgnoreCase))
                return "category";

            // Uses Enum.TryParse rather than "is the name in the list": SkillOperation is [Flags],
            // the filter accepts a comma list ("Query,Modify" -- matching a skill that declares both) and numeric literals,
            // and checking against a name list would reject exactly those two forms.
            if (filters.TryGetValue("operation", out var operation) &&
                !Enum.TryParse<SkillOperation>(operation, true, out _))
                return "operation";

            // A key written with no value ("?tags=", "?summary=") is now preserved by ParseQueryString rather than dropped,
            // and none of these keys has a meaningful reading of "empty": a narrowing key would become a filter condition that matches nothing,
            // while a shape key would fall back to the very default the caller meant to override. Either answer would leave the caller believing the key took effect,
            // so it's rejected outright. category/operation don't need to be listed -- an empty string isn't a member of either word list,
            // and the two checks above already catch them.
            foreach (var key in _blankRejectingFilterKeys)
            {
                if (filters.TryGetValue(key, out var value) && string.IsNullOrWhiteSpace(value))
                    return key;
            }

            return null;
        }

        private static string BuildFilteredOutput(string queryString, string manifestType, out bool isError)
        {
            Initialize();
            isError = false;
            var rawFilters = ParseQueryString(queryString);
            var selectorKey = FindSkillSelectorLikeKey(rawFilters);
            if (selectorKey != null)
            {
                isError = true;
                return BuildSkillSelectorKeyError(selectorKey, rawFilters[selectorKey]);
            }
            var filters = StripUnrecognizedFilterKeys(rawFilters);

            // Placed before ResolveGetSurface, so an unknown value can never become a cache key; also placed before the brief/meta branches,
            // otherwise a query that should be rejected would get a perfectly legitimate catalog. The HTTP fast path asks
            // the same question via FindInvalidNarrowingFilterKey and voluntarily steps aside,
            // so it never hands out _cachedBrief for a query that would return an error here.
            var filterValueError = ValidateNarrowingFilterValues(filters);
            if (filterValueError != null)
            {
                isError = true;
                return filterValueError;
            }

            string cacheKey = ResolveGetSurface(manifestType, filters, out var surface);

            switch (surface)
            {
                case GetSurface.Meta:
                    return GetMeta(ResolveWireVersion(filters));
                // ?brief=1 (or ?brief=true), and now bare GET /skills too -> the catalog layer: skill names grouped by
                // category, without descriptions or parameter schemas (roughly 19KB, versus roughly 139KB for summary / roughly 707KB for full).
                // Takes priority over summary/category and other filters (which are ignored), to keep the semantics minimal:
                // locate the module first, then pull the exact signature via GET /skills/schema?category=<Category>.
                case GetSurface.Brief:
                    return GetBrief();
                case GetSurface.FullV1:
                    return manifestType == "schema" ? GetSchema() : GetManifest();
            }

            // Before Refresh(), filtered output is byte-for-byte deterministic for the same query; caching it means
            // a repeated scoped fetch (?category=...) doesn't have to rebuild and re-serialize every skill each time.
            if (_filteredOutputCache.TryGetValue(cacheKey, out var cachedOutput))
                return cachedOutput;

            IEnumerable<SkillInfo> filtered = VisibleSkills();

            if (filters.TryGetValue("category", out var cat))
                filtered = filtered.Where(s => s.Category.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase));

            if (filters.TryGetValue("operation", out var op))
                filtered = filtered.Where(s => s.Operation != 0 &&
                    Enum.TryParse<SkillOperation>(op, true, out var flag) && s.Operation.HasFlag(flag));

            if (filters.TryGetValue("tags", out var tag))
                filtered = filtered.Where(s => s.Tags != null &&
                    s.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));

            if (filters.TryGetValue("readonly", out var ro))
                filtered = filtered.Where(s => s.ReadOnly == (ro.Equals("true", StringComparison.OrdinalIgnoreCase)));

            if (filters.TryGetValue("q", out var q))
            {
                var keywords = q.ToLowerInvariant().Split(new[] { ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);
                filtered = filtered.Where(s => keywords.Any(kw =>
                    s.NameLower.Contains(kw) ||
                    s.DescriptionLower.Contains(kw) ||
                    (s.TagsLower != null && s.TagsLower.Any(t => t.Contains(kw)))));
            }

            // ?names=a,b -- exact skill names, the cheapest way to fetch a known signature (no sibling entries, unlike q=).
            if (filters.TryGetValue("names", out var namesValue))
            {
                var wanted = new HashSet<string>(
                    namesValue.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(s => wanted.Contains(s.Name));
            }

            var results = filtered.ToList();

            // ?summary=1 (or ?includeSchema=false, consistent with the /skills/recommend convention)
            // -> a lightweight cognitive manifest: omits parameter schemas, truncates descriptions.
            bool summary = filters.TryGetValue("summary", out var sumVal) &&
                (sumVal == "1" || sumVal.Equals("true", StringComparison.OrdinalIgnoreCase));
            if (!summary && filters.TryGetValue("includeSchema", out var incVal) &&
                (incVal == "0" || incVal.Equals("false", StringComparison.OrdinalIgnoreCase)))
                summary = true;

            // Only keys that actually narrow the scope are echoed back as `filters` and counted toward `filtered`;
            // a ?wire=v2 or ?full=1 request that narrows nothing reports filtered:false.
            // For every pre-v2 query, this is the same dictionary instance as before, so the bytes match.
            var narrowingFilters = StripSurfaceSelectionKeys(filters);
            bool isFiltered = narrowingFilters.Count > 0;
            int wire = ResolveWireVersion(filters);

            var manifest = BuildManifest(results, isFiltered, isFiltered ? narrowingFilters : null, manifestType, summary, wire);
            var json = JsonConvert.SerializeObject(manifest, wire == WireV2 ? _jsonSettingsV2 : _jsonSettings);
            if (_filteredOutputCache.Count >= MaxCacheEntries) _filteredOutputCache.Clear();
            _filteredOutputCache[cacheKey] = json;
            return json;
        }

        private static string BuildFilteredOutputCacheKey(Dictionary<string, string> filters, string manifestType)
        {
            // Normalizes and lowercases the keys. Every filter comparison in BuildFilteredOutput is case-insensitive
            // (category/tags/readonly use OrdinalIgnoreCase, operation's TryParse passes ignoreCase=true,
            // q goes through ToLowerInvariant), so lowercasing both keys and values together converges equivalent queries
            // (?category=GameObject and ?Category=gameobject) onto the same cache entry.
            var parts = filters.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k.ToLowerInvariant()}={(filters[k] ?? string.Empty).ToLowerInvariant()}");
            return manifestType + "|" + string.Join("|", parts);
        }

        private static bool ContainsParameter(IEnumerable<string> parameterNames, string parameterName)
        {
            return parameterNames != null &&
                parameterNames.Any(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool SupportsSyntheticEntityId(string[] parameterNames)
        {
            return !ContainsParameter(parameterNames, EntityIdParameterName) &&
                ContainsParameter(parameterNames, "instanceId") &&
                (_entityIdPathFallbackParameters.Any(name => ContainsParameter(parameterNames, name)) ||
                 _entityIdNameFallbackParameters.Any(name => ContainsParameter(parameterNames, name)));
        }

        private static bool ShouldExposeSyntheticEntityId(SkillInfo skill)
        {
            return skill != null &&
                !ContainsParameter(skill.ParameterNames, EntityIdParameterName) &&
                skill.AllowedParameterSet != null &&
                skill.AllowedParameterSet.Contains(EntityIdParameterName);
        }

        private static string[] GetEffectiveParameterNames(SkillInfo skill)
        {
            if (skill?.ParameterNames == null)
                return Array.Empty<string>();

            if (!ShouldExposeSyntheticEntityId(skill))
                return skill.ParameterNames;

            return skill.ParameterNames
                .Concat(new[] { EntityIdParameterName })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Whether the skill itself declares a parameter with this name. Such a name must reach it as its own parameter,
        /// and must not be swallowed by the envelope layer as a pagination parameter.
        /// </summary>
        private static bool SkillDeclaresParameter(SkillInfo skill, string parameterName) =>
            skill != null && ContainsParameter(skill.ParameterNames, parameterName);

        /// <summary>
        /// Lenient boolean parse for the reserved 'verbose' body parameter: accepts a JSON bool, or the strings
        /// "true"/"false"/"1"/"0"/"yes"/"no" (ToObject&lt;bool&gt; alone accepts true/false/"true"/"false"/1/0 but
        /// rejects "1"/"yes"/"no"). Shared by Execute's own read and ValidateParameters's dryRun-time check
        /// (<see cref="ValidateReservedBodyParameters"/>), so the two paths accept and reject exactly the same
        /// values instead of dryRun silently allowing what Execute would reject as TYPE_MISMATCH.
        /// </summary>
        private static bool TryParseVerboseFlag(JToken token, out bool value)
        {
            try
            {
                value = token.ToObject<bool>();
                return true;
            }
            catch (Exception)
            {
                var raw = token.Type == JTokenType.String
                    ? token.Value<string>()?.Trim().ToLowerInvariant()
                    : null;
                if (raw == "true" || raw == "1" || raw == "yes") { value = true; return true; }
                if (raw == "false" || raw == "0" || raw == "no") { value = false; return true; }
                value = false;
                return false;
            }
        }

        /// <summary>
        /// Reads an envelope-layer pagination parameter ('offset'/'limit') as an integer no smaller than minValue.
        /// Also accepts both a JSON number and its string form ("10"), so a caller going through a query string also works.
        /// </summary>
        private static bool TryReadPagingArg(JToken token, string parameterName, int minValue, out int value, out string error)
        {
            value = 0;
            error = null;

            var raw = token.Type == JTokenType.Integer
                ? token.ToString(Formatting.None)
                : token.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Parameter '{parameterName}' must be an integer, got: {token.ToString(Formatting.None)}";
                return false;
            }

            if (parsed < minValue)
            {
                error = minValue <= 0
                    ? $"Parameter '{parameterName}' must be a non-negative integer, got: {parsed}"
                    : $"Parameter '{parameterName}' must be a positive integer, got: {parsed}";
                return false;
            }

            value = parsed;
            return true;
        }

        private static JArray FindPageArray(JToken result, out string propertyName)
        {
            propertyName = null;
            if (result is JArray array)
                return array;
            if (!(result is JObject obj))
                return null;

            foreach (var name in new[] { "items", "assets", "objects", "groups", "entries" })
            {
                if (obj[name] is JArray nested)
                {
                    propertyName = name;
                    return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Rolls back the workflow/undo bookkeeping opened before <c>Method.Invoke</c>, for when an envelope-layer parameter
        /// is judged invalid before anything has executed. Matches the cleanup each catch does in Execute.
        /// </summary>
        private static void UnwindBeforeInvoke(bool autoStartedWorkflow, int workflowSnapshotCountBefore, int undoGroup)
        {
            if (autoStartedWorkflow && WorkflowManager.IsRecording)
                WorkflowManager.AbortTask();
            else if (WorkflowManager.IsRecording)
                WorkflowManager.TruncateCurrentTask(workflowSnapshotCountBefore);

            if (undoGroup >= 0)
                UnityEditor.Undo.RevertAllInCurrentGroup();
        }

        private static object[] BuildParameterSchema(SkillInfo skill)
        {
            if (skill == null)
                return Array.Empty<object>();

            var parameters = new List<object>(skill.Parameters.Length + 1);
            for (int i = 0; i < skill.Parameters.Length; i++)
            {
                var p = skill.Parameters[i];
                var name = p.Name;
                var type = GetJsonType(p.ParameterType);
                var required = IsParameterRequired(skill, p);
                var defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null;
                var description = GetParameterDescription(skill, i);
                // Two shapes rather than a null member: v1 writes nulls, and an always-present key would change the bytes
                // of every parameter entry that carries no note.
                parameters.Add(description == null
                    ? (object)new { name, type, required, defaultValue }
                    : new { name, type, required, defaultValue, description });
            }

            if (ShouldExposeSyntheticEntityId(skill))
            {
                parameters.Add(new
                {
                    name = EntityIdParameterName,
                    type = "string",
                    required = false,
                    defaultValue = (string)null
                });
            }

            return parameters.ToArray();
        }

        // internal: /skills/batch's dry-run uses this to structurally validate $ref paths against the referenced skill's declared outputs
        // (including the synthesized entityId).
        internal static string[] GetEffectiveOutputs(SkillInfo skill)
        {
            if (skill?.Outputs == null)
                return null;

            if (!skill.Outputs.Any(output => string.Equals(output, "instanceId", StringComparison.OrdinalIgnoreCase)) ||
                skill.Outputs.Any(output => string.Equals(output, EntityIdParameterName, StringComparison.OrdinalIgnoreCase)))
            {
                return skill.Outputs;
            }

            return skill.Outputs
                .Concat(new[] { EntityIdParameterName })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetEffectiveDescription(SkillInfo skill)
        {
            var description = skill?.Description ?? string.Empty;
            if (!ShouldExposeSyntheticEntityId(skill))
                return description;

            return description
                .Replace("name/instanceId/path", "name/entityId/instanceId/path")
                .Replace("name, instanceId, or path", "name, entityId, instanceId, or path")
                .Replace("name / instanceId / path", "name / entityId / instanceId / path");
        }

        private static object BuildManifest(IEnumerable<SkillInfo> skills, bool filtered, Dictionary<string, string> filters, string manifestType, bool summary = false, int wire = WireV1)
        {
            var skillArray = skills
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (wire == WireV2)
                return BuildManifestV2(skillArray, filtered, filters, manifestType, summary);

            return new
            {
                manifestType,
                schemaVersion = SkillSchemaVersion,
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = skillArray.Length,
                filtered,
                filters,
                summary,
                summaryHint = summary
                    ? SummaryHintText
                    : null,
                categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                operationTypes = Enum.GetNames(typeof(SkillOperation)),
                reservedBodyParameters = _reservedBodyParameters.OrderBy(x => x).ToArray(),
                // Filtered by profile, not by query: this block is an envelope constant, so a scoped ?category=
                // fetch must still list every externally-offered tracked skill (narrowing it to the current page would change the v1 bytes
                // of every scoped query). See VisibleWorkflowTrackedSkills -- under the default profile it's the full set.
                workflowTrackedSkills = VisibleWorkflowTrackedSkills(),
                skills = summary
                    ? skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        riskLevel = s.RiskLevel
                    })
                    : skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        tags = s.Tags,
                        outputs = GetEffectiveOutputs(s),
                        requiresInput = s.RequiresInput,
                        readOnly = s.ReadOnly,
                        tracksWorkflow = s.TracksWorkflow,
                        mutatesScene = s.MutatesScene,
                        mutatesAssets = s.MutatesAssets,
                        mayTriggerReload = s.MayTriggerReload,
                        mayEnterPlayMode = s.MayEnterPlayMode,
                        supportsDryRun = s.SupportsDryRun,
                        riskLevel = s.RiskLevel,
                        requiresPackages = s.RequiresPackages,
                        mode = SkillsModeManager.SkillModeToWire(s.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
                        parameters = BuildParameterSchema(s)
                    })
            };
        }

        // Shared between the v1 and v2 envelopes, so the two can never drift apart.
        private const string SummaryHintText = "AWARENESS ONLY — parameter schemas are omitted and descriptions are informal (human-written; some omit parameter hints entirely), not a formal signature. Before executing any skill listed here, validate its parameters with ?mode=dryRun (the server returns unknownParam suggestions + the full parameter schema) or fetch its scoped schema GET /skills/schema?category=<Category>. Do NOT guess parameters from descriptions alone.";

        internal const string MetaEndpointPath = "/skills/meta";

        // The one riskLevel value a v2 entry omits. Compared with Ordinal (not IgnoreCase),
        // so any other spelling passes through unchanged rather than being silently normalized away.
        private const string DefaultRiskLevel = "low";

        /// <summary>
        /// The per-skill values that <c>?wire=v2</c> omits from an entry, declared here once, centrally.
        /// Both the v2 envelope and <see cref="GetMeta"/> output it, and the way it's constructed guarantees the two are always identical --
        /// this block alone is what makes those omissions reversible.
        /// </summary>
        private static object BuildWireDefaults() => new
        {
            riskLevel = DefaultRiskLevel,
            supportsDryRun = true
        };

        /// <summary>
        /// <c>?wire=v2</c> envelope. Every difference from v1 is a subtraction:
        /// <list type="bullet">
        /// <item>Four session-constant blocks (categories / operationTypes / reservedBodyParameters /
        /// workflowTrackedSkills) give way to <c>metaUrl</c> -- fetch
        /// <see cref="MetaEndpointPath"/> once, no need to pay for them on every scoped fetch;</item>
        /// <item>Six impact booleans plus longRunning collapse into <c>flags</c>, listing only the ones that are true;</item>
        /// <item><c>riskLevel</c> appears only at a non-default value, <c>supportsDryRun</c> appears only when false,
        /// and <c>defaults</c> states what each omission means;</item>
        /// <item>Null members disappear entirely (serialized with <c>_jsonSettingsV2</c>).</item>
        /// </list>
        /// <c>approvalBehavior</c> is deliberately kept on every entry: it's the one field an agent must know
        /// before judging "will this call actually be allowed," and reverse-deriving it from mode + flags is exactly the guessing this payload exists to eliminate.
        /// </summary>
        private static object BuildManifestV2(SkillInfo[] skillArray, bool filtered, Dictionary<string, string> filters, string manifestType, bool summary)
        {
            return new
            {
                manifestType,
                schemaVersion = SkillSchemaVersion,
                wire = "v2",
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = skillArray.Length,
                filtered,
                filters,
                summary,
                summaryHint = summary
                    ? SummaryHintText
                    : null,
                metaUrl = MetaEndpointPath,
                defaults = BuildWireDefaults(),
                skills = summary
                    ? skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        // Even though v1's summary carries neither, flags and supportsDryRun still need to be carried here.
                        // `defaults` appears in every v2 payload, and it states that "a flag's absence means false" --
                        // so a summary entry missing them isn't read as "impact unknown", it's read as
                        // "this skill changes nothing, and dry-run works fine." Omitting them here
                        // would mean every summary entry asserts the exact opposite fact for 784 skills.
                        // All v2 surfaces share one contract.
                        flags = BuildSkillFlags(s),
                        riskLevel = NonDefaultRiskLevel(s),
                        supportsDryRun = s.SupportsDryRun ? (bool?)null : false
                    })
                    : skillArray.Select(s => (object)new
                    {
                        name = s.Name,
                        description = GetEffectiveDescription(s),
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        tags = s.Tags,
                        outputs = GetEffectiveOutputs(s),
                        requiresInput = s.RequiresInput,
                        flags = BuildSkillFlags(s),
                        riskLevel = NonDefaultRiskLevel(s),
                        supportsDryRun = s.SupportsDryRun ? (bool?)null : false,
                        requiresPackages = s.RequiresPackages,
                        mode = SkillsModeManager.SkillModeToWire(s.Mode),
                        approvalBehavior = SkillsModeManager.ApprovalBehaviorForSkill(s),
                        parameters = BuildParameterSchema(s)
                    })
            };
        }

        private static string NonDefaultRiskLevel(SkillInfo s) =>
            string.Equals(s.RiskLevel, DefaultRiskLevel, StringComparison.Ordinal) ? null : s.RiskLevel;

        /// <summary>
        /// Replaces the six impact booleans plus longRunning in v2: lists only the flags that are set, in a fixed order to keep payload bytes stable.
        /// Null when none are set (and therefore omitted); a flag's absence from the array means false.
        /// </summary>
        private static string[] BuildSkillFlags(SkillInfo s)
        {
            var flags = new List<string>(7);
            if (s.ReadOnly) flags.Add("readOnly");
            if (s.TracksWorkflow) flags.Add("tracksWorkflow");
            if (s.MutatesScene) flags.Add("mutatesScene");
            if (s.MutatesAssets) flags.Add("mutatesAssets");
            if (s.MayTriggerReload) flags.Add("mayTriggerReload");
            if (s.MayEnterPlayMode) flags.Add("mayEnterPlayMode");
            if (s.LongRunning) flags.Add("longRunning");
            return flags.Count > 0 ? flags.ToArray() : null;
        }

        /// <summary>
        /// The catalog-layer manifest -- what bare <c>GET /skills</c> (and <c>?brief=1</c>) returns:
        /// skill names grouped by category, nothing else. Both module keys and names are sorted, so the payload bytes are stable for the same skill set,
        /// which is why the cached string (and its fast-path ETag) stays valid until Refresh().
        /// </summary>
        private static object BuildBriefManifest()
        {
            var modules = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int visibleCount = 0;
            foreach (var s in VisibleSkills())
            {
                var category = s.Category.ToString();
                if (!modules.TryGetValue(category, out var names))
                    modules[category] = names = new List<string>();
                names.Add(s.Name);
                visibleCount++;
            }
            foreach (var names in modules.Values)
                names.Sort(StringComparer.OrdinalIgnoreCase);

            return new
            {
                manifestType = "brief",
                schemaVersion = SkillSchemaVersion,
                version = SkillsLogger.Version,
                // Reports the count actually listed in this payload. Under a non-full surfaceProfile it's smaller than the registry's total --
                // reporting the registry's total count here would send the agent looking for names that don't exist in the catalog.
                totalSkills = visibleCount,
                briefHint = "DIRECTORY ONLY — names + categories, no descriptions or parameters. This is the default answer for GET /skills. Locate the module(s) you need, then fetch exact signatures via GET /skills/schema?names=<a,b>&wire=v2 or ?category=<Category>&wire=v2 (dryRun where the root protocol asks: deletes, high risk, reload, approval mode). If a name is ambiguous, use GET /skills/recommend?intent=...&includeSchema=true&topN=3&wire=v2 or GET /skills?summary=1 (full descriptions). The complete manifest is still available at GET /skills?full=1 (~707KB — add &wire=v2 to cut it down), and session constants live at GET /skills/meta.",
                modules
            };
        }
    }
}

// Producer:Betsy
