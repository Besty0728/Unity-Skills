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
        internal static ParameterValidationResult ValidateParameters(SkillInfo skill, string json)
        {
            var validation = new ParameterValidationResult
            {
                Args = string.IsNullOrEmpty(json) ? new JObject() : JObject.Parse(json)
            };

            var ps = skill.Parameters;
            NormalizeSyntheticEntityIdLocator(skill, validation);
            CollectUnknownParameters(skill, validation);
            var invoke = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                bool provided = validation.Args.TryGetValue(p.Name, StringComparison.OrdinalIgnoreCase, out var token);
                bool required = IsParameterRequired(skill, p);

                if (provided)
                {
                    try
                    {
                        // Batch-style skills declare a JSON payload as a string parameter, and an agent frequently sends a native
                        // array/object directly. Serializes it back to a string here, instead of failing with TYPE_MISMATCH --
                        // the skill re-parses that JSON internally, so the round trip is lossless.
                        // This leniency only applies when the target type is string; every other type stays strict.
                        if (p.ParameterType == typeof(string) && (token is JArray || token is JObject))
                            invoke[i] = token.ToString(Formatting.None);
                        else
                            invoke[i] = token.ToObject(p.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        validation.TypeErrors.Add(new { parameter = p.Name, expectedType = GetJsonType(p.ParameterType), error = ex.Message });
                    }
                }
                else if (required)
                {
                    validation.MissingParams.Add(p.Name);
                }
                else if (p.HasDefaultValue)
                {
                    invoke[i] = p.DefaultValue;
                }
                else
                {
                    invoke[i] = null;
                }

                var name = p.Name;
                var type = GetJsonType(p.ParameterType);
                var defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null;
                var description = GetParameterDescription(skill, i);
                validation.ParameterDetails.Add(description == null
                    ? (object)new { name, type, required, provided, defaultValue }
                    : new { name, type, required, provided, defaultValue, description });
            }

            ValidateReservedBodyParameters(skill, validation);

            if (ShouldExposeSyntheticEntityId(skill))
            {
                validation.ParameterDetails.Add(new
                {
                    name = EntityIdParameterName,
                    type = "string",
                    required = false,
                    provided = validation.Args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out _),
                    defaultValue = (string)null,
                    synthetic = true
                });
            }

            validation.InvokeArgs = invoke;
            ApplyRequiredPackages(skill, validation);
            SkillPlanningService.ApplySemanticValidation(skill, validation);
            return validation;
        }

        /// <summary>
        /// dryRun-time counterpart to the reserved-body-parameter parsing Execute performs just before invoking
        /// the skill (verbose bool coercion; offset/limit/pageOffset/pageLimit integer + range checks): without
        /// this, POST /skill/xxx?mode=dryRun with e.g. <c>{"offset":"abc"}</c> reported valid:true, and the same
        /// body without ?mode=dryRun failed with TYPE_MISMATCH -- dryRun is supposed to preview exactly that
        /// failure, not miss it. Skipped whenever the skill itself declares a same-named parameter (its own
        /// declaration governs, e.g. asset_reimport_batch's own int <c>limit</c>) via the same
        /// <see cref="SkillDeclaresParameter"/> guard Execute already uses for offset/limit -- extended here to
        /// verbose/pageOffset/pageLimit too, so a skill can never have two different rules for the same name
        /// depending on whether the call is a dryRun or not.
        /// </summary>
        private static void ValidateReservedBodyParameters(SkillInfo skill, ParameterValidationResult validation)
        {
            var args = validation.Args;

            if (!SkillDeclaresParameter(skill, "verbose") &&
                args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken) &&
                !TryParseVerboseFlag(verboseToken, out _))
            {
                validation.TypeErrors.Add(new
                {
                    parameter = "verbose",
                    expectedType = "boolean",
                    error = $"Parameter 'verbose' must be a boolean (true/false), got: {verboseToken.ToString(Formatting.None)}"
                });
            }

            ValidateReservedPagingParameter(skill, args, validation, "pageOffset", minValue: 0);
            ValidateReservedPagingParameter(skill, args, validation, "pageLimit", minValue: 1);
            ValidateReservedPagingParameter(skill, args, validation, "offset", minValue: 0);
            ValidateReservedPagingParameter(skill, args, validation, "limit", minValue: 1);
        }

        private static void ValidateReservedPagingParameter(SkillInfo skill, JObject args, ParameterValidationResult validation, string parameterName, int minValue)
        {
            if (SkillDeclaresParameter(skill, parameterName) ||
                !args.TryGetValue(parameterName, StringComparison.OrdinalIgnoreCase, out var token))
                return;

            if (!TryReadPagingArg(token, parameterName, minValue, out _, out var error))
                validation.TypeErrors.Add(new { parameter = parameterName, expectedType = "integer", error });
        }

        /// <summary>
        /// Checks the skill's declared RequiresPackages against what is installed, so dryRun stops calling a call
        /// valid that can only fail and Execute refuses it before any side effect. Three verdicts:
        /// <list type="bullet">
        /// <item>absent, for a skill that writes -> <see cref="ParameterValidationResult.MissingPackages"/> (invalid);</item>
        /// <item>absent, for a ReadOnly skill -> a warning only: status probes must keep answering "not installed" themselves;</item>
        /// <item>unconfirmed while the async package list is still loading -> a warning, and the refresh is kicked off.
        /// Same guard as recommend (<see cref="HasUninstalledPackage"/>): "don't know yet" must never read as "not installed".</item>
        /// </list>
        /// </summary>
        private static void ApplyRequiredPackages(SkillInfo skill, ParameterValidationResult validation)
        {
            if (skill.RequiresPackages == null || skill.RequiresPackages.Length == 0)
                return;

            bool listReady = PackageManagerHelper.InstalledPackages != null;
            // IsPackageInstalled also answers from a direct registry lookup, so a package confirmed that way is settled
            // even before the list arrives; only a miss needs the list to be believed.
            var missing = FindUninstalledPackages(skill, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
            if (missing == null)
                return;

            if (!listReady)
            {
                PackageManagerHelper.EnsurePackageListRefresh();
                validation.Warnings.Add($"Package list is still refreshing; requiresPackages unverified: {string.Join(", ", missing)}.");
                return;
            }

            if (skill.ReadOnly)
            {
                validation.Warnings.Add($"Required package(s) not installed: {string.Join(", ", missing)}. This read-only skill still runs and reports availability itself.");
                return;
            }

            validation.MissingPackages.AddRange(missing);
        }

        /// <summary>
        /// Test seam: makes <see cref="NormalizeSyntheticEntityIdLocator"/> act as on Unity 6000.4+, where
        /// <see cref="UnityObjectIdUtility.GetObjectId"/> is always 0, so its path and refusal branches run on every editor.
        /// </summary>
        internal static bool LegacyInstanceIdUnavailableForTests;

        private const int MaxEntityIdCandidates = 10;

        /// <summary>
        /// A skill exposing the synthetic entityId reads only name/instanceId/path, so the entityId is forwarded through a
        /// locator that names this one object: the legacy instanceId where this Unity version still has one, otherwise the
        /// hierarchy path when it leads back to this object alone. Anything else is a semantic error listing the candidates;
        /// a shared path or a bare name would hand the call to another same-name object. Runs inside ValidateParameters, so
        /// Execute, DryRun and Plan all reach the same verdict.
        /// </summary>
        private static void NormalizeSyntheticEntityIdLocator(SkillInfo skill, ParameterValidationResult validation)
        {
            if (!ShouldExposeSyntheticEntityId(skill) ||
                validation?.Args == null ||
                !validation.Args.TryGetValue(EntityIdParameterName, StringComparison.OrdinalIgnoreCase, out var token))
            {
                return;
            }

            var entityId = token.Type == JTokenType.Null ? null : token.ToString();
            if (string.IsNullOrWhiteSpace(entityId))
                return;

            var unityObject = UnityObjectIdUtility.EntityIdToObject(entityId);
            var gameObject = unityObject as GameObject ?? (unityObject as Component)?.gameObject;
            if (gameObject == null)
            {
                validation.SemanticErrors.Add(new
                {
                    // "field" is the established semanticErrors key (SkillPlanningService.AddSemanticError,
                    // ~50 call sites); "parameter" is UnknownParams's key. This was the one producer mixing the
                    // two, so a skill's own semantic analyzer re-reporting the same failed entityId lookup under
                    // "field" was never recognized as a duplicate by TokenAlreadyReported (which only reads
                    // "field"). Both keys, same value, so either convention's reader gets the right answer.
                    field = EntityIdParameterName,
                    parameter = EntityIdParameterName,
                    error = $"Object not found for entityId: {entityId}"
                });
                return;
            }

            var instanceIdParameter = FindDeclaredParameter(skill, "instanceId");
            var legacyId = LegacyInstanceIdUnavailableForTests ? 0 : UnityObjectIdUtility.GetObjectId(gameObject);
            if (instanceIdParameter != null && legacyId != 0)
            {
                validation.Args[instanceIdParameter] = legacyId;
                return;
            }

            var path = GameObjectFinder.GetCachedPath(gameObject);
            var pathParameter = _entityIdPathFallbackParameters
                .Select(candidate => FindDeclaredParameter(skill, candidate))
                .FirstOrDefault(declared => declared != null);
            var pathMatches = pathParameter != null ? FindObjectsAtPath(path) : null;
            if (pathMatches != null &&
                pathMatches.Count == 1 && pathMatches[0] == gameObject &&
                GameObjectFinder.FindByPath(path) == gameObject)
            {
                validation.Args[pathParameter] = path;
                return;
            }

            validation.SemanticErrors.Add(BuildUnforwardableEntityIdError(skill, entityId, gameObject, path, pathMatches));
        }

        private static string FindDeclaredParameter(SkillInfo skill, string parameterName) =>
            skill.ParameterNames?.FirstOrDefault(name => string.Equals(name, parameterName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Every scene object a path lookup for <paramref name="path"/> could land on: its hierarchy path, bare or prefixed
        /// with its scene name (FindByPath accepts both), equals the path case-insensitively.
        /// </summary>
        private static List<GameObject> FindObjectsAtPath(string path)
        {
            var matches = new List<GameObject>();
            var key = GameObjectFinder.NormalizePathKey(path);
            if (key == null)
                return matches;

            foreach (var candidate in GameObjectFinder.GetSceneObjects())
            {
                if (candidate == null)
                    continue;

                var candidatePath = GameObjectFinder.GetCachedPath(candidate);
                var sceneName = candidate.scene.name;
                if (PathKeyEquals(candidatePath, key) ||
                    (!string.IsNullOrEmpty(sceneName) && PathKeyEquals(sceneName + "/" + candidatePath, key)))
                    matches.Add(candidate);
            }

            return matches;
        }

        private static bool PathKeyEquals(string path, string normalizedKey) =>
            string.Equals(GameObjectFinder.NormalizePathKey(path), normalizedKey, StringComparison.OrdinalIgnoreCase);

        /// <summary><paramref name="pathMatches"/> is null when the skill has no path parameter.</summary>
        private static object BuildUnforwardableEntityIdError(SkillInfo skill, string entityId, GameObject target, string path,
            List<GameObject> pathMatches)
        {
            string reason;
            string error;
            List<GameObject> candidates;
            if (pathMatches == null)
            {
                reason = "noPathParameter";
                candidates = GameObjectFinder.GetSceneObjects()
                    .Where(go => go != null && string.Equals(go.name, target.name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                error = $"entityId {entityId} ('{path}') cannot be passed to {skill.Name}: it locates objects by name or instanceId only, " +
                        "this Unity version has no legacy instanceId, and entityId is never forwarded as a name. " +
                        "Pass name yourself only if candidates lists a single object.";
            }
            else if (pathMatches.Count > 1)
            {
                reason = "ambiguousPath";
                candidates = pathMatches;
                error = $"entityId {entityId} resolves to '{path}', a path {pathMatches.Count} objects share, and this Unity version has no " +
                        $"legacy instanceId, so {skill.Name} cannot be pointed at it alone. Rename or move it so its path is unique " +
                        "(gameobject_rename takes entityId), then retry.";
            }
            else
            {
                reason = "pathUnresolvable";
                candidates = pathMatches;
                error = $"entityId {entityId} names '{path}', but a path lookup does not lead back to it (it is not in an open scene, " +
                        $"or its name breaks the path syntax), and this Unity version has no legacy instanceId, so {skill.Name} cannot target it.";
            }

            return new
            {
                // Same two keys as the not-found entry above, for the same reason.
                field = EntityIdParameterName,
                parameter = EntityIdParameterName,
                error,
                reason,
                path,
                candidates = candidates.Take(MaxEntityIdCandidates).Select(go => new
                {
                    name = go.name,
                    path = GameObjectFinder.GetCachedPath(go),
                    scene = go.scene.name,
                    entityId = UnityObjectIdUtility.GetEntityId(go)
                }).ToArray()
            };
        }

        private static void CollectUnknownParameters(SkillInfo skill, ParameterValidationResult validation)
        {
            if (validation?.Args == null)
                return;

            var allowed = skill.AllowedParameterSet;
            var parameterNames = skill.ParameterNames;

            foreach (var property in validation.Args.Properties())
            {
                if (allowed.Contains(property.Name))
                    continue;

                var suggestions = SuggestParameters(skill.Name, property.Name, parameterNames);
                var entry = new Dictionary<string, object>
                {
                    ["parameter"] = property.Name
                };

                if (suggestions.Length > 0)
                    entry["suggestions"] = suggestions;

                var hint = GetParameterHint(skill.Name, property.Name);
                if (!string.IsNullOrWhiteSpace(hint))
                    entry["hint"] = hint;

                validation.UnknownParams.Add(entry);
            }
        }

        private static List<SuggestedFix> BuildUnknownParamFixes(string skillName, List<object> unknownParams)
        {
            var fixes = new List<SuggestedFix>();
            if (unknownParams == null || unknownParams.Count == 0)
                return fixes;

            foreach (var entry in unknownParams)
            {
                if (entry is not IDictionary<string, object> dict)
                    continue;

                string param = dict.TryGetValue("parameter", out var pv) ? pv?.ToString() : null;
                string hint = dict.TryGetValue("hint", out var hv) ? hv?.ToString() : null;

                // The schema's supportsDryRun flag advertises a router-level preview transport mode
                // (POST /skill/<name>?mode=dryRun), not a request-body parameter -- but an agent that reads that flag
                // invariably passes one anyway, and Levenshtein finds no useful neighbor for "dryRun".
                if (string.Equals(param, "dryRun", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(param, "dry_run", StringComparison.OrdinalIgnoreCase))
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "fix_param",
                        skill = skillName,
                        reason = $"'{param}' is not a parameter — dry run is a transport mode: " +
                                 $"POST /skill/{skillName}?mode=dryRun with the same JSON body, then execute without the query flag."
                    });
                    continue;
                }

                if (dict.TryGetValue("suggestions", out var sObj) && sObj is IEnumerable<string> sugs)
                {
                    foreach (var s in sugs)
                    {
                        fixes.Add(new SuggestedFix
                        {
                            action = "fix_param",
                            skill = skillName,
                            args = new Dictionary<string, string> { [s] = "<value>" },
                            reason = !string.IsNullOrEmpty(hint)
                                ? $"Did you mean '{s}'? {hint}"
                                : (!string.IsNullOrEmpty(param)
                                    ? $"Replace unknown parameter '{param}' with '{s}'"
                                    : $"Use '{s}'")
                        });
                    }
                }
                else if (!string.IsNullOrEmpty(hint))
                {
                    fixes.Add(new SuggestedFix
                    {
                        action = "fix_param",
                        skill = skillName,
                        reason = hint
                    });
                }
            }
            return fixes.Count > 0 ? fixes : null;
        }

        private static string[] SuggestParameters(string skillName, string unknownParameter, string[] allowedParameterNames)
        {
            if (_commonParameterSuggestions.TryGetValue(skillName, out var skillSuggestions) &&
                skillSuggestions.TryGetValue(unknownParameter, out var directSuggestions) &&
                directSuggestions?.Length > 0)
            {
                return directSuggestions;
            }

            var fuzzyMatches = allowedParameterNames
                .Select(name => new
                {
                    Name = name,
                    Distance = ComputeLevenshteinDistance(unknownParameter, name)
                })
                .Where(x =>
                    x.Distance <= 3 ||
                    x.Name.IndexOf(unknownParameter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    unknownParameter.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (fuzzyMatches.Length > 0)
                return fuzzyMatches;

            // Last-resort fallback: neither the alias table nor edit distance catches a rename like assetPath->savePath
            // (distance 4, no substring overlap), but parameter names across the whole skill library reuse the same set of camelCase tokens
            // (path/name/id/target/source/...), so "sharing any token" is a strong signal.
            // Only enabled when the stricter tiers find nothing, to avoid adding noise to suggestions that already have a good match.
            var unknownTokens = SplitCamelCaseTokens(unknownParameter);
            if (unknownTokens.Count == 0)
                return fuzzyMatches;

            return allowedParameterNames
                .Where(name => SplitCamelCaseTokens(name).Overlaps(unknownTokens))
                .Select(name => new
                {
                    Name = name,
                    Distance = ComputeLevenshteinDistance(unknownParameter, name)
                })
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static HashSet<string> SplitCamelCaseTokens(string name)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(name))
                return tokens;

            var current = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                if (!char.IsLetter(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                if (char.IsUpper(c) && current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                current.Append(char.ToLowerInvariant(c));
            }

            if (current.Length > 0)
                tokens.Add(current.ToString());
            return tokens;
        }

        private static string GetParameterHint(string skillName, string parameterName)
        {
            if (_commonParameterHints.TryGetValue(skillName, out var hints) &&
                hints.TryGetValue(parameterName, out var hint))
            {
                return hint;
            }

            return null;
        }

        private static int ComputeLevenshteinDistance(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
                return string.IsNullOrEmpty(right) ? 0 : right.Length;
            if (string.IsNullOrEmpty(right))
                return left.Length;

            var matrix = new int[left.Length + 1, right.Length + 1];
            for (int i = 0; i <= left.Length; i++)
                matrix[i, 0] = i;
            for (int j = 0; j <= right.Length; j++)
                matrix[0, j] = j;

            for (int i = 1; i <= left.Length; i++)
            {
                for (int j = 1; j <= right.Length; j++)
                {
                    int cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                    matrix[i, j] = Math.Min(
                        Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                        matrix[i - 1, j - 1] + cost);
                }
            }

            return matrix[left.Length, right.Length];
        }

        private static string[] ExtractValidationParameterNames(IEnumerable<object> validationEntries)
        {
            if (validationEntries == null)
                return Array.Empty<string>();

            return validationEntries
                .Select(entry => TryGetValidationEntryField(entry, "parameter"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ExtractValidationMessage(object validationEntry, string fallback)
        {
            return SkillResultHelper.TryGetMemberValue(validationEntry, "error", out var errorValue) && errorValue != null
                ? errorValue.ToString()
                : fallback;
        }

        private static string TryGetValidationEntryField(object validationEntry, string fieldName)
        {
            return SkillResultHelper.TryGetMemberValue(validationEntry, fieldName, out var value) && value != null
                ? value.ToString()
                : null;
        }
    }
}

// Producer:Betsy
