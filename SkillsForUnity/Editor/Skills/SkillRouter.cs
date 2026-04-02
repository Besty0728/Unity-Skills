using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Routes REST API requests to skill methods.
    /// </summary>
    public static class SkillRouter
    {
        private static volatile Dictionary<string, SkillInfo> _skills;
        private static volatile bool _initialized;
        private static string _cachedManifest;
        private static readonly object _initLock = new object();

        private static HashSet<string> _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Keep Unicode readable in JSON responses instead of forcing escaped sequences.
        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = StringEscapeHandling.Default
        };

        private class SkillInfo
        {
            public string Name;
            public string Description;
            public MethodInfo Method;
            public ParameterInfo[] Parameters;
            public bool TracksWorkflow;
            // Intent-level metadata (v1.7)
            public SkillCategory Category;
            public SkillOperation Operation;
            public string[] Tags;
            public string[] Outputs;
            public string[] RequiresInput;
            public bool ReadOnly;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initLock)
            {
                if (_initialized) return;

                var skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);
                var trackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });

                foreach (var type in allTypes)
                {
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        UnitySkillAttribute attr;
                        try { attr = method.GetCustomAttribute<UnitySkillAttribute>(); }
                        catch { continue; }
                        if (attr != null)
                        {
                            var name = attr.Name ?? ToSnakeCase(method.Name);
                            skills[name] = new SkillInfo
                            {
                                Name = name,
                                Description = attr.Description ?? "",
                                Method = method,
                                Parameters = method.GetParameters(),
                                TracksWorkflow = attr.TracksWorkflow,
                                Category = attr.Category,
                                Operation = attr.Operation,
                                Tags = attr.Tags,
                                Outputs = attr.Outputs,
                                RequiresInput = attr.RequiresInput,
                                ReadOnly = attr.ReadOnly
                            };
                            if (attr.TracksWorkflow)
                                trackedSkills.Add(name);
                        }
                    }
                }

                _skills = skills; // Atomic assignment of fully-built dictionary
                _workflowTrackedSkills = trackedSkills;
                _initialized = true;
                SkillsLogger.Log($"Discovered {_skills.Count} skills");
            }
        }

        public static string GetManifest()
        {
            Initialize();
            var cached = _cachedManifest;
            if (cached != null) return cached;

            lock (_initLock)
            {
                if (_cachedManifest != null) return _cachedManifest;

                var manifest = new
                {
                    version = SkillsLogger.Version,
                    unityVersion = Application.unityVersion,
                    totalSkills = _skills.Count,
                    categories = Enum.GetNames(typeof(SkillCategory)).Where(c => c != "Uncategorized").ToArray(),
                    operationTypes = Enum.GetNames(typeof(SkillOperation)),
                    workflowTrackedSkills = _workflowTrackedSkills.OrderBy(name => name).ToArray(),
                    skills = _skills.Values.Select(s => new
                    {
                        name = s.Name,
                        description = s.Description,
                        category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                        operation = FormatOperation(s.Operation),
                        tags = s.Tags,
                        outputs = s.Outputs,
                        requiresInput = s.RequiresInput,
                        readOnly = s.ReadOnly,
                        tracksWorkflow = s.TracksWorkflow,
                        parameters = s.Parameters.Select(p => new
                        {
                            name = p.Name,
                            type = GetJsonType(p.ParameterType),
                            required = !p.HasDefaultValue,
                            defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                        })
                    })
                };
                _cachedManifest = JsonConvert.SerializeObject(manifest, Formatting.Indented, _jsonSettings);
                return _cachedManifest;
            }
        }

        public static string Execute(string name, string json)
        {
            Initialize();
            if (!_skills.TryGetValue(name, out var skill))
            {
                return JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"Skill '{name}' not found",
                    availableSkills = _skills.Keys.Take(20).ToArray()
                }, _jsonSettings);
            }

            bool autoStartedWorkflow = false;
            try
            {
                var args = string.IsNullOrEmpty(json) ? new JObject() : JObject.Parse(json);
                var ps = skill.Parameters;
                var invoke = new object[ps.Length];

                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (args.TryGetValue(p.Name, StringComparison.OrdinalIgnoreCase, out var token))
                    {
                        invoke[i] = token.ToObject(p.ParameterType);
                    }
                    else if (p.HasDefaultValue)
                    {
                        invoke[i] = p.DefaultValue;
                    }
                    else if (!p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null)
                    {
                        invoke[i] = null;
                    }
                    else
                    {
                        return JsonConvert.SerializeObject(new
                        {
                            status = "error",
                            error = $"Missing required parameter: {p.Name}"
                        }, _jsonSettings);
                    }
                }


                // Transactional Support: Start Undo Group
                UnityEditor.Undo.IncrementCurrentGroup();
                UnityEditor.Undo.SetCurrentGroupName($"Skill: {name}");
                int undoGroup = UnityEditor.Undo.GetCurrentGroup();

                // ========== AUTO WORKFLOW RECORDING ==========
                if (skill.TracksWorkflow && !WorkflowManager.IsRecording)
                {
                    var desc = $"{name} - {(json?.Length > 80 ? json.Substring(0, 80) + "..." : json ?? "")}";
                    WorkflowManager.BeginTask(name, desc);
                    autoStartedWorkflow = true;
                }

                // Auto-snapshot target objects BEFORE skill execution for rollback support
                if (WorkflowManager.IsRecording)
                {
                    TrySnapshotTargetsFromArgs(args);
                }
                // ==============================================

                // Verbose control
                bool verbose = true; // Default to true if not specified to maintain backward compatibility for direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    verbose = verboseToken.ToObject<bool>();
                    args.Remove("verbose");
                }
                
                var result = skill.Method.Invoke(null, invoke);

                // ========== AUTO WORKFLOW END ==========
                if (autoStartedWorkflow)
                {
                    WorkflowManager.EndTask();
                    WorkflowManager.SaveHistory();
                }
                else if (WorkflowManager.IsRecording)
                {
                    WorkflowManager.SaveHistory();
                }
                // ========================================

                // Commit transaction
                UnityEditor.Undo.CollapseUndoOperations(undoGroup);

                // Return a normalized error payload when a skill reports a logical failure.
                if (SkillResultHelper.TryGetError(result, out string errorText))
                {
                    return JsonConvert.SerializeObject(new
                    {
                        status = "error",
                        errorCode = "SKILL_ERROR",
                        error = errorText,
                        skill = name
                    }, _jsonSettings);
                }

                if (!verbose && result != null)
                {
                    // "Summary Mode" Logic
                    // 1. Convert result to JToken to inspect it
                    var jsonResult = JToken.FromObject(result);
                    
                    // 2. Check if it's a large Array (> 10 items)
                    if (jsonResult is JArray arr && arr.Count > 10)
                    {
                        var truncatedItems = new JArray();
                        for(int i=0; i<5; i++) truncatedItems.Add(arr[i]);
                        
                        // Return a wrapper object instead of the list
                        // This keeps 'items' clean (same type) while providing meta info
                        var wrapper = new JObject
                        {
                            ["isTruncated"] = true,
                            ["totalCount"] = arr.Count,
                            ["showing"] = 5,
                            ["items"] = truncatedItems,
                            ["hint"] = "Result is truncated. To see all items, pass 'verbose=true' parameter."
                        };
                        
                        return SerializeSuccessResponse(wrapper);
                    }
                }
                
                // Full Mode (verbose=true OR small result) - Return original result as is
                return SerializeSuccessResponse(result);
            }
            catch (TargetInvocationException ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                // Revert transaction
                UnityEditor.Undo.RevertAllInCurrentGroup();

                var inner = ex.InnerException ?? ex;
                return JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = $"[Transactional Revert] {inner.Message}"
                }, _jsonSettings);
            }
            catch (Exception ex)
            {
                // Clean up auto-started workflow on error
                if (autoStartedWorkflow && WorkflowManager.IsRecording)
                    WorkflowManager.EndTask();

                // Revert transaction
                UnityEditor.Undo.RevertAllInCurrentGroup();
                
                return JsonConvert.SerializeObject(new { 
                    status = "error", 
                    error = $"[Transactional Revert] {ex.Message}" 
                }, _jsonSettings);
            }
        }

        private static string SerializeSuccessResponse(object result)
        {
            if (ServerAvailabilityHelper.IsCompilationInProgress())
            {
                try
                {
                    var jsonResult = JToken.FromObject(result ?? new object());
                    if (jsonResult is JObject obj && !obj.ContainsKey("serverAvailability"))
                    {
                        var notice = ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            "A skill execution may have triggered compilation or asset refresh.",
                            alwaysInclude: true);
                        if (notice != null)
                        {
                            obj["serverAvailability"] = JToken.FromObject(notice);
                            return JsonConvert.SerializeObject(new { status = "success", result = obj }, _jsonSettings);
                        }
                    }
                }
                catch { /* 注入失败不影响正常返回 */ }
            }
            return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
        }

        public static void Refresh()
        {
            lock (_initLock)
            {
                _initialized = false;
                _skills = null;
                _cachedManifest = null;
                _workflowTrackedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            Initialize();
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

        // ========== Filtered Manifest ==========

        /// <summary>
        /// Returns a filtered skills manifest based on query string parameters.
        /// Supported: category, operation, tags, readOnly, q (text search).
        /// </summary>
        public static string GetFilteredManifest(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            if (filters.Count == 0) return GetManifest();

            IEnumerable<SkillInfo> filtered = _skills.Values;

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
                    s.Name.ToLowerInvariant().Contains(kw) ||
                    (s.Description != null && s.Description.ToLowerInvariant().Contains(kw)) ||
                    (s.Tags != null && s.Tags.Any(t => t.ToLowerInvariant().Contains(kw)))));
            }

            var results = filtered.ToList();
            var manifest = new
            {
                version = SkillsLogger.Version,
                unityVersion = Application.unityVersion,
                totalSkills = results.Count,
                filtered = true,
                filters,
                skills = results.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
                    category = s.Category != SkillCategory.Uncategorized ? s.Category.ToString() : null,
                    operation = FormatOperation(s.Operation),
                    tags = s.Tags,
                    outputs = s.Outputs,
                    requiresInput = s.RequiresInput,
                    readOnly = s.ReadOnly,
                    tracksWorkflow = s.TracksWorkflow,
                    parameters = s.Parameters.Select(p => new
                    {
                        name = p.Name,
                        type = GetJsonType(p.ParameterType),
                        required = !p.HasDefaultValue,
                        defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                    })
                })
            };
            return JsonConvert.SerializeObject(manifest, Formatting.Indented, _jsonSettings);
        }

        // ========== Skill Recommendations ==========

        /// <summary>
        /// Intent-based skill recommendation. Scores skills by keyword matching against
        /// name (3pts), tags (2pts), and description (1pt). Returns top-N ranked results.
        /// </summary>
        public static string GetRecommendations(string queryString)
        {
            Initialize();
            var filters = ParseQueryString(queryString);
            var intent = "";
            int topN = 10;
            if (filters.TryGetValue("intent", out var i)) intent = i;
            if (filters.TryGetValue("topn", out var n) && int.TryParse(n, out var parsed)) topN = Mathf.Clamp(parsed, 1, 50);

            if (string.IsNullOrWhiteSpace(intent))
            {
                return JsonConvert.SerializeObject(new
                {
                    status = "error",
                    error = "Missing required parameter: intent",
                    example = "/skills/recommend?intent=create+cube&topN=10"
                }, _jsonSettings);
            }

            var keywords = intent.ToLowerInvariant().Split(new[] { ' ', '+', '_' }, StringSplitOptions.RemoveEmptyEntries);
            var scored = new List<(SkillInfo skill, int score, List<string> matchedOn)>();

            foreach (var s in _skills.Values)
            {
                int score = 0;
                var matchedOn = new List<string>();
                var nameLower = s.Name.ToLowerInvariant();
                var descLower = s.Description?.ToLowerInvariant() ?? "";

                foreach (var kw in keywords)
                {
                    if (nameLower.Contains(kw))
                    {
                        score += 3;
                        matchedOn.Add($"name:{kw}");
                    }
                    if (s.Tags != null && s.Tags.Any(t => t.ToLowerInvariant().Contains(kw)))
                    {
                        score += 2;
                        matchedOn.Add($"tag:{kw}");
                    }
                    if (descLower.Contains(kw))
                    {
                        score += 1;
                        matchedOn.Add($"desc:{kw}");
                    }
                }

                if (score > 0)
                    scored.Add((s, score, matchedOn));
            }

            var results = scored.OrderByDescending(x => x.score).Take(topN).ToList();
            var response = new
            {
                intent,
                topN,
                totalMatches = scored.Count,
                results = results.Select(x => new
                {
                    name = x.skill.Name,
                    description = x.skill.Description,
                    category = x.skill.Category != SkillCategory.Uncategorized ? x.skill.Category.ToString() : null,
                    score = x.score,
                    matchedOn = x.matchedOn.Distinct().ToArray()
                })
            };
            return JsonConvert.SerializeObject(response, Formatting.Indented, _jsonSettings);
        }

        // ========== Metadata Validation ==========

        /// <summary>
        /// Validates metadata completeness and consistency across all discovered skills.
        /// Returns a list of diagnostic messages (WARN/ERROR prefix).
        /// </summary>
        public static List<string> ValidateMetadata()
        {
            Initialize();
            var issues = new List<string>();

            foreach (var s in _skills.Values)
            {
                if (s.Category == SkillCategory.Uncategorized)
                    issues.Add($"[WARN] {s.Name}: Category is Uncategorized");

                if (s.Operation == 0)
                    issues.Add($"[WARN] {s.Name}: Operation not specified");

                if (s.ReadOnly && s.TracksWorkflow)
                    issues.Add($"[ERROR] {s.Name}: ReadOnly=true conflicts with TracksWorkflow=true");

                if (s.Tags == null || s.Tags.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Tags is empty");

                if (s.Outputs == null || s.Outputs.Length == 0)
                    issues.Add($"[WARN] {s.Name}: Outputs is empty");

                if (s.Operation.HasFlag(SkillOperation.Delete) || s.Operation.HasFlag(SkillOperation.Modify))
                {
                    if (s.RequiresInput == null || s.RequiresInput.Length == 0)
                        issues.Add($"[WARN] {s.Name}: Delete/Modify operation but RequiresInput is empty");
                }
            }

            return issues;
        }

        // ========== Query String Parser ==========

        private static Dictionary<string, string> ParseQueryString(string qs)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(qs)) return result;

            // Remove leading '?'
            var raw = qs.StartsWith("?") ? qs.Substring(1) : qs;
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var pair in raw.Split('&'))
            {
                var eqIdx = pair.IndexOf('=');
                if (eqIdx <= 0) continue;
                var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx)).Trim();
                var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1)).Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                    result[key] = val;
            }
            return result;
        }

        /// <summary>
        /// Auto-snapshot target objects from skill arguments for universal rollback support.
        /// Identifies common target parameters (name, instanceId, path, materialPath, etc.) and snapshots them.
        /// </summary>
        private static void TrySnapshotTargetsFromArgs(JObject args)
        {
            try
            {
                // Try to find target GameObject by common parameter names
                string targetName = null;
                int targetInstanceId = 0;
                string targetPath = null;

                if (args.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken))
                    targetName = nameToken.ToString();
                if (args.TryGetValue("instanceId", StringComparison.OrdinalIgnoreCase, out var idToken))
                    targetInstanceId = idToken.ToObject<int>();
                if (args.TryGetValue("path", StringComparison.OrdinalIgnoreCase, out var pathToken))
                    targetPath = pathToken.ToString();

                // Snapshot GameObject if identifiable
                if (!string.IsNullOrEmpty(targetName) || targetInstanceId != 0 || !string.IsNullOrEmpty(targetPath))
                {
                    var (go, _) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath);
                    if (go != null)
                    {
                        WorkflowManager.SnapshotObject(go);
                        // Also snapshot Transform which is commonly modified
                        WorkflowManager.SnapshotObject(go.transform);
                        // Snapshot Renderer's material if present
                        var renderer = go.GetComponent<UnityEngine.Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null)
                            WorkflowManager.SnapshotObject(renderer.sharedMaterial);
                    }
                }

                // Snapshot Material asset if materialPath is provided
                if (args.TryGetValue("materialPath", StringComparison.OrdinalIgnoreCase, out var matPathToken))
                {
                    var matPath = matPathToken.ToString();
                    if (!string.IsNullOrEmpty(matPath))
                    {
                        var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(matPath);
                        if (mat != null)
                            WorkflowManager.SnapshotObject(mat);
                    }
                }

                // Snapshot asset if assetPath is provided
                if (args.TryGetValue("assetPath", StringComparison.OrdinalIgnoreCase, out var assetPathToken))
                {
                    var assetPath = assetPathToken.ToString();
                    if (!string.IsNullOrEmpty(assetPath))
                    {
                        var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                        if (asset != null)
                            WorkflowManager.SnapshotObject(asset);
                    }
                }

                // Handle child/parent operations
                if (args.TryGetValue("childName", StringComparison.OrdinalIgnoreCase, out var childNameToken))
                {
                    var (childGo, _) = GameObjectFinder.FindOrError(childNameToken.ToString(), 0, null);
                    if (childGo != null)
                        WorkflowManager.SnapshotObject(childGo.transform);
                }

                // Handle batch items - snapshot each target in the batch
                if (args.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var itemsToken))
                {
                    try
                    {
                        var items = itemsToken.ToObject<List<Dictionary<string, object>>>();
                        if (items != null)
                        {
                            foreach (var item in items.Take(50)) // Limit to avoid performance issues
                            {
                                string itemName = item.ContainsKey("name") ? item["name"]?.ToString() : null;
                                int itemId = item.ContainsKey("instanceId") ? Convert.ToInt32(item["instanceId"]) : 0;
                                string itemPath = item.ContainsKey("path") ? item["path"]?.ToString() : null;

                                if (!string.IsNullOrEmpty(itemName) || itemId != 0 || !string.IsNullOrEmpty(itemPath))
                                {
                                    var (itemGo, _) = GameObjectFinder.FindOrError(itemName, itemId, itemPath);
                                    if (itemGo != null)
                                    {
                                        WorkflowManager.SnapshotObject(itemGo);
                                        WorkflowManager.SnapshotObject(itemGo.transform);
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore batch parsing errors */ }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Workflow snapshot failed: {ex.Message}");
            }
        }
    }

    internal static class SkillResultHelper
    {
        public static bool TryGetError(object result, out string errorText)
        {
            errorText = null;
            if (result == null)
                return false;

            if (!TryGetMemberValue(result, "error", out object errorValue) || errorValue == null)
                return false;

            if (TryGetMemberValue(result, "success", out object successValue) && successValue is bool successBool && successBool)
                return false;

            errorText = errorValue.ToString();
            return !string.IsNullOrWhiteSpace(errorText);
        }

        public static bool TryGetMemberValue(object result, string memberName, out object value)
        {
            value = null;
            if (result == null || string.IsNullOrEmpty(memberName))
                return false;

            if (result is JObject jsonObject &&
                jsonObject.TryGetValue(memberName, StringComparison.OrdinalIgnoreCase, out JToken token))
            {
                value = token.Type == JTokenType.Null ? null : token.ToObject<object>();
                return true;
            }

            if (result is IDictionary<string, object> dictionary)
            {
                foreach (var pair in dictionary)
                {
                    if (string.Equals(pair.Key, memberName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = pair.Value;
                        return true;
                    }
                }
            }

            var resultType = result.GetType();
            var property = resultType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
            {
                value = property.GetValue(result);
                return true;
            }

            var field = resultType.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                value = field.GetValue(result);
                return true;
            }

            return false;
        }
    }
}
