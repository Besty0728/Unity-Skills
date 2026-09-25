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
        private static string ErrorJson(string error) =>
            SkillErrorResponse.Build(SkillErrorCode.Internal, error);

        private static string ErrorJson(SkillErrorCode code, string error, string skill = null, string retryStrategy = null, object details = null) =>
            SkillErrorResponse.Build(code, error, skill: skill, details: details, retryStrategy: retryStrategy);

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                // Installed here rather than in a static constructor: every path that can produce a
                // cached output string first goes through Initialize(), so by the time a cache exists whose validity depends on the profile, this hook is guaranteed to already be listening.
                // Reset along with the other static fields on a domain reload.
                if (!_surfaceHookInstalled)
                {
                    SkillsSurfaceProfile.OnChanged += InvalidateOutputCaches;
                    _surfaceHookInstalled = true;
                }

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
                var trackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Uses the Unity editor's index to query skill methods directly, avoiding enumerating every assembly and type after a Domain Reload.
                var methods = TypeCache.GetMethodsWithAttribute<UnitySkillAttribute>();
                foreach (var method in methods)
                {
                    if (!method.IsPublic || !method.IsStatic)
                        continue;

                    UnitySkillAttribute attr;
                    try { attr = method.GetCustomAttribute<UnitySkillAttribute>(); }
                    catch { continue; }
                    if (attr != null)
                    {
                        var name = attr.Name ?? ToSnakeCase(method.Name);
                        var parameters = method.GetParameters();
                        var parameterNames = parameters.Select(p => p.Name).ToArray();
                        var allowedSet = new HashSet<string>(parameterNames, StringComparer.OrdinalIgnoreCase);
                        allowedSet.UnionWith(_reservedBodyParameters);
                        if (!allowedSet.Contains(EntityIdParameterName) && SupportsSyntheticEntityId(parameterNames))
                            allowedSet.Add(EntityIdParameterName);
                        skills[name] = new SkillInfo
                        {
                            Name = name,
                            Description = attr.Description ?? "",
                            Method = method,
                            Parameters = parameters,
                            TracksWorkflow = attr.TracksWorkflow,
                            SkipAutoPresnapshot = attr.SkipAutoPresnapshot,
                            Category = attr.Category,
                            Operation = attr.Operation,
                            Tags = attr.Tags,
                            Outputs = attr.Outputs,
                            RequiresInput = attr.RequiresInput,
                            RequiredParams = attr.RequiredParams,
                            ReadOnly = attr.ReadOnly,
                            MutatesScene = attr.MutatesScene,
                            MutatesAssets = attr.MutatesAssets,
                            MayTriggerReload = attr.MayTriggerReload,
                            MayEnterPlayMode = attr.MayEnterPlayMode,
                            SupportsDryRun = attr.SupportsDryRun,
                            LongRunning = attr.LongRunning,
                            RiskLevel = attr.RiskLevel ?? "low",
                            RequiresPackages = attr.RequiresPackages,
                            Mode = attr.Mode,
                            ParameterNames = parameterNames,
                            ParameterDescriptions = ReadParameterDescriptions(parameters),
                            AllowedParameterSet = allowedSet,
                            NameLower = name.ToLowerInvariant(),
                            DescriptionLower = (attr.Description ?? "").ToLowerInvariant(),
                            TagsLower = attr.Tags?.Select(t => t.ToLowerInvariant()).ToArray()
                        };
                        if (attr.TracksWorkflow)
                            trackedSkills.Add(name);
                    }
                }

                _skills = skills; // Atomic assignment once the whole thing is built
                _workflowTrackedSkills = trackedSkills;

                // Reverse index: output field -> the skill that produces it
                var outputIdx = new Dictionary<string, List<SkillInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in skills.Values)
                {
                    var effectiveOutputs = GetEffectiveOutputs(s);
                    if (effectiveOutputs == null) continue;
                    foreach (var output in effectiveOutputs)
                    {
                        if (!outputIdx.TryGetValue(output, out var list))
                        {
                            list = new List<SkillInfo>();
                            outputIdx[output] = list;
                        }
                        list.Add(s);
                    }
                }
                _outputIndex = outputIdx;

                _initialized = true;
                SkillsLogger.LogVerbose($"Discovered {_skills.Count} skills"); // Start() reports the count on its own line
            }
        }

        /// <summary>
        /// The set of skills the current surface profile exposes externally -- every external discovery
        /// surface (manifest, schema, filtered manifest/schema, brief, recommend, snapshot)
        /// must enumerate this rather than <c>_skills.Values</c>. The one deliberate exception is <see cref="ValidateMetadata"/>,
        /// which audits the registry itself and must see everything.
        ///
        /// Main-thread only (needs to read the profile; the first call may hit EditorPrefs). True for every caller:
        /// what the HTTP thread's fast path reads is always just a string this method helped build.
        /// </summary>
        private static IEnumerable<SkillInfo> VisibleSkills()
        {
            // Under the default profile, returns the same instance as before -- no allocation, no per-skill check.
            if (SkillsSurfaceProfile.IsFull)
                return _skills.Values;
            return _skills.Values.Where(s => !SkillsSurfaceProfile.IsExcluded(s));
        }

        /// <summary>
        /// The workflow-tracked skill names actually offered externally, in the same order as the
        /// original collection. Every payload carrying this block is external-facing, so it must draw from the same authority as <see cref="VisibleSkills"/> --
        /// listing a hidden name here is exactly the leak the profile is meant to prevent, and what it would leak is the most consequential half of the registry
        /// (tracked skills are by definition write operations, and write operations are exactly what a profile withdraws).
        ///
        /// Under the default full profile nothing gets filtered, so this array -- and every byte of the v1 envelope built from it --
        /// is identical to the unfiltered set. Main-thread only, for the same reason as VisibleSkills.
        /// </summary>
        private static string[] VisibleWorkflowTrackedSkills()
        {
            if (SkillsSurfaceProfile.IsFull)
                return _workflowTrackedSkills.OrderBy(name => name).ToArray();

            return _workflowTrackedSkills
                .Where(name => _skills.TryGetValue(name, out var skill) && !SkillsSurfaceProfile.IsExcluded(skill))
                .OrderBy(name => name)
                .ToArray();
        }

        /// <summary>
        /// Drops every cached output string, but doesn't rerun skill discovery. Hooked onto
        /// <see cref="SkillsSurfaceProfile.OnChanged"/>: switching profiles doesn't change the skill registry,
        /// but every payload built from it changes, so the strings must be rebuilt, though reflection doesn't need to be redone.
        /// ETag follows along automatically as a side effect -- an entry in <c>_etagCache</c> is only valid when its source string is reference-equal to the current cache,
        /// and a rebuilt string's content differs, so its hash is naturally different too.
        /// </summary>
        internal static void InvalidateOutputCaches()
        {
            lock (_initLock)
            {
                _cachedManifest = null;
                _cachedSchema = null;
                _cachedBrief = null;
                _cachedMeta = null;
                _cachedMetaV2 = null;
                _filteredOutputCache.Clear();
                _etagCache.Clear();
            }
        }

        public static void Refresh()
        {
            lock (_initLock)
            {
                _initialized = false;
                _skills = null;
                _outputIndex = null;
                InvalidateOutputCaches();
                _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            Initialize();
        }

        internal static string ResolveSkillNotFound(string name)
        {
            // A client helper function's name can never fuzzy-match any skill -- before falling back to nearest-name search,
            // answer with its corresponding REST usage first.
            if (!string.IsNullOrEmpty(name) &&
                k_ClientHelperRestEquivalents.TryGetValue(name, out var restEquivalent))
            {
                return SkillErrorResponse.ClientHelperNotASkill(name, restEquivalent);
            }

            // Gives up to 5 closest *externally offered* skill names, letting the AI agent self-correct a typo.
            // Drawn from VisibleSkills rather than the registry: an approximate match against a hidden skill
            // would hand back, verbatim, the very name the surface profile just withdrew,
            // turning typo correction into an enumeration channel for what the user chose not to expose.
            var nearest = VisibleSkills().Select(s => s.Name)
                .Select(k => new { Name = k, Distance = ComputeLevenshteinDistance(name ?? string.Empty, k) })
                .Where(x => x.Distance <= 5 ||
                            (!string.IsNullOrEmpty(name) && k_ContainsCi(x.Name, name)))
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(x => x.Name)
                .ToList();

            return SkillErrorResponse.SkillNotFound(name, nearest);
        }

        private static bool k_ContainsCi(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && !string.IsNullOrEmpty(needle) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool TryGetSkill(string name, out SkillInfo skill)
        {
            Initialize();
            return _skills.TryGetValue(name, out skill);
        }

        /// <summary>
        /// The externally-offered skill set, honoring the current surface profile. Anywhere skills get offered to a caller
        /// (allowlist picker, skill browser, smoke probing) should use this: offering a skill the profile hides
        /// would only earn a SURFACE_EXCLUDED later on. Use <see cref="GetAllSkillsSnapshotUnfiltered"/> when accounting
        /// needs to cover the entire registry.
        /// </summary>
        internal static SkillInfo[] GetAllSkillsSnapshot()
        {
            Initialize();
            return VisibleSkills()
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Every registered skill, ignoring the surface profile -- for callers that must reason about the registry itself
        /// (rather than "what's offered externally"): resolving an already-persisted skill name (an allowlist entry stays valid
        /// across a profile switch, so rendering it as "(Unknown)" just because the current profile hides it would be a lie),
        /// and full-registry audits of the same kind as <see cref="ValidateMetadata"/>.
        ///
        /// Local editor UI and diagnostics only. Never wire this to any HTTP surface: the profile is the user's statement of
        /// "what can be offered to the AI," and any endpoint enumerating from here would hand back a skill name the user chose to withdraw --
        /// exactly the leak <see cref="VisibleSkills"/> exists to prevent.
        /// </summary>
        internal static SkillInfo[] GetAllSkillsSnapshotUnfiltered()
        {
            Initialize();
            return _skills.Values
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}

// Producer:Betsy
