using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    internal static partial class SkillPlanningService
    {
        private static string ResolveShaderName(string shaderName, SkillRouter.ParameterValidationResult validation)
        {
            if (string.IsNullOrWhiteSpace(shaderName))
                shaderName = ProjectSkills.GetDefaultShaderName();

            var shader = Shader.Find(shaderName);
            if (shader != null)
                return shaderName;

            var pipeline = ProjectSkills.DetectRenderPipeline();
            var fallbackShaders = pipeline switch
            {
                ProjectSkills.RenderPipelineType.URP => new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard" },
                ProjectSkills.RenderPipelineType.HDRP => new[] { "HDRP/Lit", "Standard" },
                _ => new[] { "Standard", "Mobile/Diffuse", "Unlit/Color" }
            };

            foreach (var fallback in fallbackShaders)
            {
                shader = Shader.Find(fallback);
                if (shader != null)
                {
                    AddWarning(validation, $"Shader '{shaderName}' not found. Planner fell back to '{fallback}'.");
                    return fallback;
                }
            }

            AddSemanticError(validation, "shaderName", $"Shader not found: {shaderName}");
            return shaderName;
        }

        private static bool TryParseBatchItems(SkillRouter.ParameterValidationResult validation, out JArray items)
        {
            items = null;
            if (validation?.Args == null)
                return false;

            var itemsJson = GetStringArg(validation.Args, "items");
            if (Validate.RequiredJsonArray(itemsJson, "items") is object requiredErr)
            {
                AddSemanticError(validation, "items", ExtractError(requiredErr));
                return false;
            }

            try
            {
                items = JArray.Parse(itemsJson);
                if (items.Count == 0)
                {
                    AddSemanticError(validation, "items", "items must be a non-empty array");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AddSemanticError(validation, "items", $"Failed to parse items JSON: {ex.Message}");
                return false;
            }
        }

        private static bool AllowsMultiple(Type type)
        {
            try
            {
                return type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length == 0;
            }
            catch
            {
                return true;
            }
        }

        private static (PropertyInfo prop, FieldInfo field) FindMember(Type type, string memberName)
        {
            if (type == null || string.IsNullOrEmpty(memberName))
                return (null, null);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var prop = type.GetProperty(memberName, flags);
            var field = type.GetField(memberName, flags);
            return (prop, field);
        }

        private static object ResolveSceneReference(Type targetType, GameObject go)
        {
            if (targetType == typeof(GameObject))
                return go;
            if (targetType == typeof(Transform))
                return go.transform;
            if (typeof(Component).IsAssignableFrom(targetType))
                return go.GetComponent(targetType);
            return null;
        }

        private static void SetPlanDetails(
            IDictionary<string, object> plan,
            List<object> steps,
            List<object> create = null,
            List<object> modify = null,
            List<object> delete = null,
            IDictionary<string, object> extra = null)
        {
            if (plan == null)
                return;

            plan["steps"] = steps?.ToArray() ?? Array.Empty<object>();
            plan["changes"] = new Dictionary<string, object>
            {
                ["create"] = (create ?? new List<object>()).ToArray(),
                ["modify"] = (modify ?? new List<object>()).ToArray(),
                ["delete"] = (delete ?? new List<object>()).ToArray()
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                    plan[pair.Key] = pair.Value;
            }
        }

        private static void MarkSemantic(IDictionary<string, object> plan)
        {
            if (plan != null)
                plan["planLevel"] = "semantic";
        }

        private static string GetStringArg(JObject args, params string[] keys)
        {
            if (args == null)
                return null;

            foreach (var key in keys)
            {
                if (args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
                    return token.ToString();
            }
            return null;
        }

        private static int GetIntArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<int>(); } catch { }
            }
            return 0;
        }

        // Null when the key is absent, null or not an integer (the executor's item binding fails on the latter anyway).
        private static int? GetOptionalIntArg(JObject args, string key)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<int>(); } catch { }
            }
            return null;
        }

        private static float GetFloatArg(JObject args, string key, float fallback = 0f)
        {
            if (args != null && args.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var token) && token.Type != JTokenType.Null)
            {
                try { return token.ToObject<float>(); } catch { }
            }
            return fallback;
        }

        private static void AddErrorFromValidation(SkillRouter.ParameterValidationResult validation, object result, string field)
        {
            if (result == null)
                return;
            // A parameter the router already lists in MissingParams is one problem, not two.
            if (validation != null && validation.MissingParams.Any(missing => string.Equals(missing, field, StringComparison.OrdinalIgnoreCase)))
                return;
            AddSemanticError(validation, field, ExtractError(result));
        }

        private static void AddSemanticError(SkillRouter.ParameterValidationResult validation, string field, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            bool exists = validation.SemanticErrors.Any(entry =>
                SkillResultHelper.TryGetMemberValue(entry, "field", out var existingField) &&
                SkillResultHelper.TryGetMemberValue(entry, "error", out var existingError) &&
                string.Equals(existingField?.ToString(), field, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingError?.ToString(), message, StringComparison.Ordinal));

            if (exists)
                return;

            validation.SemanticErrors.Add(new Dictionary<string, object>
            {
                ["field"] = field,
                // Additive: UnknownParams's readers use "parameter", SemanticErrors's ~50 call sites (here) use
                // "field". Carrying both under the same value means either convention's reader gets the right
                // answer, without a one-time sweep of every internal/external consumer.
                ["parameter"] = field,
                ["error"] = message
            });
        }

        private static void AddWarning(SkillRouter.ParameterValidationResult validation, string message)
        {
            if (validation == null || string.IsNullOrWhiteSpace(message))
                return;

            if (validation.Warnings.Any(w => string.Equals(w, message, StringComparison.Ordinal)))
                return;

            validation.Warnings.Add(message);
        }

        private static string ExtractError(object result)
        {
            return SkillResultHelper.TryGetError(result, out var errorText) ? errorText : result?.ToString() ?? "Unknown error";
        }

        private static string ExtractSemanticMessage(object semanticError)
        {
            if (SkillResultHelper.TryGetMemberValue(semanticError, "error", out var value) && value != null)
                return value.ToString();
            return semanticError?.ToString() ?? "Unknown semantic error";
        }

        // ===================== Batch analysis helpers =====================

        private class BatchAnalyzeContext
        {
            public readonly SkillRouter.ParameterValidationResult Validation;
            public readonly IDictionary<string, object> Plan;
            public readonly JArray Items;
            public readonly List<object> ItemPlans = new List<object>();

            public BatchAnalyzeContext(SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan, JArray items)
            {
                Validation = validation;
                Plan = plan;
                Items = items;
            }

            public JObject GetItem(int i) => Items[i] as JObject ?? new JObject();

            public void ReportItemErrors(int index, List<string> errors)
            {
                if (errors.Count > 0)
                    AddSemanticError(Validation, $"items[{index}]", string.Join("; ", errors));
            }

            public void ReportDelegatedErrors(int index, SkillRouter.ParameterValidationResult itemValidation)
            {
                foreach (var se in itemValidation.SemanticErrors)
                    AddSemanticError(Validation, $"items[{index}]", ExtractSemanticMessage(se));
            }

            public void AddItemPlan(int index, string target, bool valid, string[] errors,
                IDictionary<string, object> extra = null)
            {
                var entry = new Dictionary<string, object>
                {
                    ["index"] = index,
                    ["target"] = target,
                    ["valid"] = valid,
                    ["errors"] = errors
                };
                if (extra != null)
                    foreach (var kv in extra) entry[kv.Key] = kv.Value;
                ItemPlans.Add(entry);
            }

            public void EmitPlan(string actionLabel,
                List<object> create = null, List<object> modify = null, List<object> delete = null)
            {
                if (Plan == null) return;
                MarkSemantic(Plan);
                SetPlanDetails(Plan,
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["index"] = 1,
                            ["action"] = actionLabel,
                            ["target"] = $"{Items.Count} items"
                        }
                    },
                    create: create, modify: modify, delete: delete,
                    extra: new Dictionary<string, object>
                    {
                        ["batchPreview"] = new Dictionary<string, object>
                        {
                            ["totalItems"] = Items.Count,
                            ["items"] = ItemPlans.ToArray()
                        }
                    });
            }
        }

        private static BatchAnalyzeContext TryBeginBatchAnalyze(
            SkillRouter.ParameterValidationResult validation, IDictionary<string, object> plan)
        {
            if (!TryParseBatchItems(validation, out var items))
                return null;
            return new BatchAnalyzeContext(validation, plan, items);
        }
    }
}

// Producer:Betsy
