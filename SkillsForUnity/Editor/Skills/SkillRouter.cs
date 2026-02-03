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
        private static Dictionary<string, SkillInfo> _skills;
        private static bool _initialized;
        
        // JSON 序列化设置，禁用 Unicode 转义确保中文正确显示
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
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _skills = new Dictionary<string, SkillInfo>(StringComparer.OrdinalIgnoreCase);

            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } });

            foreach (var type in allTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<UnitySkillAttribute>();
                    if (attr != null)
                    {
                        var name = attr.Name ?? ToSnakeCase(method.Name);
                        _skills[name] = new SkillInfo
                        {
                            Name = name,
                            Description = attr.Description ?? "",
                            Method = method,
                            Parameters = method.GetParameters()
                        };
                    }
                }
            }
            _initialized = true;
            Debug.Log($"[UnitySkills] Discovered {_skills.Count} skills");
        }

        public static string GetManifest()
        {
            Initialize();
            var manifest = new
            {
                version = "1.3.2",
                totalSkills = _skills.Count,
                skills = _skills.Values.Select(s => new
                {
                    name = s.Name,
                    description = s.Description,
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
                
                // Track objects created to allow reverting if needed
                // Note: Unity Undo system handles reverting automatically if we use Undo.RegisterCreatedObjectUndo etc inside skills.
                // Assuming all skills leverage Unity's Undo system properly, we just need to revert the group on error.

                // Verbose control
                bool verbose = true; // Default to true if not specified to maintain backward compatibility for direct calls
                if (args.TryGetValue("verbose", StringComparison.OrdinalIgnoreCase, out var verboseToken))
                {
                    verbose = verboseToken.ToObject<bool>();
                }
                
                var result = skill.Method.Invoke(null, invoke);
                
                // Commit transaction
                UnityEditor.Undo.CollapseUndoOperations(undoGroup);
                
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
                        
                        return JsonConvert.SerializeObject(new { status = "success", result = wrapper }, _jsonSettings);
                    }
                }
                
                // Full Mode (verbose=true OR small result) - Return original result as is
                return JsonConvert.SerializeObject(new { status = "success", result }, _jsonSettings);
            }
            catch (TargetInvocationException ex)
            {
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
                // Revert transaction
                UnityEditor.Undo.RevertAllInCurrentGroup();
                
                return JsonConvert.SerializeObject(new { 
                    status = "error", 
                    error = $"[Transactional Revert] {ex.Message}" 
                }, _jsonSettings);
            }
        }

        public static void Refresh()
        {
            _initialized = false;
            _skills = null;
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
    }
}
