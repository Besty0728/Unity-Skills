using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    public static partial class SkillsHttpServer
    {
        // ===== Static $param substitution (batch) =====

        /// <summary>
        /// A {"$param":"name"} / {"$param":"name","default":X} slot found within a step's args.
        /// ParamName is null when $param's value isn't a JSON string (reported as malformed at execution time).
        /// Unlike BatchRefNode, there's no TopLevelParam here: $param carries a real value under every mode, so
        /// nothing needs to be stripped from the dry-run validation body.
        /// </summary>
        private sealed class BatchParamNode
        {
            public JObject Node;
            public string ParamName;
            public bool HasDefault;
            public JToken DefaultValue;
        }

        /// <summary>
        /// An object node is a param node if and only if "$param" is its sole property (a bare slot), or it has
        /// exactly two properties, "$param" + "default" (a slot with a fallback). An object merely containing
        /// "$param" among other keys is payload data and is left alone — consistent with IsBatchRefNode.
        /// paramName is null when $param's value isn't a JSON string.
        /// </summary>
        private static bool IsBatchParamNode(JObject obj, out string paramName, out bool hasDefault, out JToken defaultValue)
        {
            paramName = null;
            hasDefault = false;
            defaultValue = null;

            if (obj.Count == 1)
            {
                var prop = (JProperty)obj.First;
                if (!string.Equals(prop.Name, "$param", StringComparison.Ordinal))
                    return false;
                paramName = prop.Value?.Type == JTokenType.String ? prop.Value.ToString() : null;
                return true;
            }

            if (obj.Count == 2)
            {
                JProperty paramProp = null, defaultProp = null;
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "$param", StringComparison.Ordinal)) paramProp = prop;
                    else if (string.Equals(prop.Name, "default", StringComparison.Ordinal)) defaultProp = prop;
                }
                if (paramProp == null || defaultProp == null)
                    return false;
                paramName = paramProp.Value?.Type == JTokenType.String ? paramProp.Value.ToString() : null;
                hasDefault = true;
                defaultValue = defaultProp.Value;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Collects every $param slot at any depth within a step's args.
        /// When a node carries both "$param" and "$ref" (a node can only be one or the other, never both), it's
        /// recorded in paramRefConflict for the caller to reject with SEMANTIC_INVALID, and the search stops there.
        /// </summary>
        private static List<BatchParamNode> FindBatchParamNodes(JToken argsRoot, out JObject paramRefConflict)
        {
            var found = new List<BatchParamNode>();
            paramRefConflict = null;
            CollectBatchParamNodes(argsRoot, found, ref paramRefConflict);
            return found;
        }

        private static void CollectBatchParamNodes(JToken token, List<BatchParamNode> found, ref JObject paramRefConflict)
        {
            if (paramRefConflict != null)
                return;

            if (token is JObject obj)
            {
                bool hasParam = false, hasRef = false;
                foreach (var prop in obj.Properties())
                {
                    if (string.Equals(prop.Name, "$param", StringComparison.Ordinal)) hasParam = true;
                    else if (string.Equals(prop.Name, "$ref", StringComparison.Ordinal)) hasRef = true;
                }
                if (hasParam && hasRef)
                {
                    paramRefConflict = obj;
                    return;
                }
                if (hasParam && IsBatchParamNode(obj, out var paramName, out var hasDefault, out var defaultValue))
                {
                    found.Add(new BatchParamNode
                    {
                        Node = obj,
                        ParamName = paramName,
                        HasDefault = hasDefault,
                        DefaultValue = defaultValue,
                    });
                    return;
                }
                foreach (var prop in obj.Properties())
                    CollectBatchParamNodes(prop.Value, found, ref paramRefConflict);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    CollectBatchParamNodes(item, found, ref paramRefConflict);
            }
        }

        /// <summary>
        /// Resolves a single slot's value: if the batch's "params" object holds that name, it wins (case-sensitive),
        /// otherwise the node's inline "default" is used, otherwise that step fails with SEMANTIC_INVALID
        /// ("not provided and no default"). A $param name that isn't a string is judged malformed.
        /// </summary>
        private static bool TryResolveBatchParam(BatchParamNode node, JObject batchParams, out JToken value, out string reason)
        {
            value = null;
            reason = null;

            if (node.ParamName == null)
            {
                reason = "the $param value must be a string naming a batch parameter";
                return false;
            }
            if (batchParams != null && batchParams.TryGetValue(node.ParamName, StringComparison.Ordinal, out var provided))
            {
                value = provided;
                return true;
            }
            if (node.HasDefault)
            {
                value = node.DefaultValue ?? JValue.CreateNull();
                return true;
            }
            reason = "not provided and no default";
            return false;
        }

        // ===== Cross-step $ref references (batch) =====

        /// <summary>
        /// A {"$ref":"$N.path"} node found within a step's args. RefString is null when $ref's value isn't a JSON
        /// string (reported as malformed at execution time).
        /// TopLevelParam is the args property whose subtree contains this node; null when the node is itself the args root.
        /// </summary>
        private sealed class BatchRefNode
        {
            public JObject Node;
            public string RefString;
            public string TopLevelParam;
        }

        /// <summary>
        /// An object node is a reference if and only if "$ref" is its sole property;
        /// an object that merely contains "$ref" among many other keys is payload data and is left alone.
        /// </summary>
        private static bool IsBatchRefNode(JObject obj, out string refString)
        {
            refString = null;
            if (obj.Count != 1)
                return false;
            var prop = (JProperty)obj.First;
            if (!string.Equals(prop.Name, "$ref", StringComparison.Ordinal))
                return false;
            refString = prop.Value?.Type == JTokenType.String ? prop.Value.ToString() : null;
            return true;
        }

        private static List<BatchRefNode> FindBatchRefNodes(JToken argsRoot)
        {
            var found = new List<BatchRefNode>();
            CollectBatchRefNodes(argsRoot, argsRoot, found);
            return found;
        }

        private static void CollectBatchRefNodes(JToken token, JToken root, List<BatchRefNode> found)
        {
            if (token is JObject obj)
            {
                if (IsBatchRefNode(obj, out var refString))
                {
                    found.Add(new BatchRefNode
                    {
                        Node = obj,
                        RefString = refString,
                        TopLevelParam = GetTopLevelParamName(obj, root),
                    });
                    return;
                }
                foreach (var prop in obj.Properties())
                    CollectBatchRefNodes(prop.Value, root, found);
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    CollectBatchRefNodes(item, root, found);
            }
        }

        private static string GetTopLevelParamName(JToken node, JToken root)
        {
            JToken cur = node;
            while (cur != null && !ReferenceEquals(cur, root) && !ReferenceEquals(cur.Parent, root))
                cur = cur.Parent;
            return cur is JProperty prop ? prop.Name : null;
        }

        /// <summary>
        /// Parses "$N", "$N.path", or "$N[…]" — N is the 0-based step index, and the rest is a Newtonsoft
        /// SelectToken path into that step's already-unwrapped result.
        /// </summary>
        private static bool TryParseBatchRef(string refString, out int stepIndex, out string selectPath, out string parseError)
        {
            stepIndex = -1;
            selectPath = null;
            parseError = null;

            if (string.IsNullOrEmpty(refString) || refString[0] != '$')
            {
                parseError = "the $ref value must be a string like \"$0\", \"$0.instanceId\" or \"$1.items[0].path\"";
                return false;
            }

            int i = 1;
            while (i < refString.Length && char.IsDigit(refString[i]))
                i++;
            if (i == 1 || !int.TryParse(refString.Substring(1, i - 1), out stepIndex))
            {
                stepIndex = -1;
                parseError = "no step index after '$' (expected \"$N\" with N = 0-based index of an earlier step)";
                return false;
            }

            if (i == refString.Length)
                return true; // "$N" — the whole unwrapped result

            char next = refString[i];
            if (next == '.')
            {
                selectPath = refString.Substring(i + 1);
                if (selectPath.Length > 0)
                    return true;
                parseError = "empty path after '.'";
                return false;
            }
            if (next == '[')
            {
                selectPath = refString.Substring(i);
                return true;
            }

            parseError = $"unexpected character '{next}' after the step index";
            return false;
        }

        /// <summary>
        /// Resolves a single reference against the already-executed steps' unwrapped results.
        /// Fails (with a structured reason) when: the reference is malformed, the index is out of range for this
        /// batch, it's a forward reference (N >= current step), the referenced step didn't succeed, or the SelectToken path matches nothing.
        /// </summary>
        private static bool TryResolveBatchRef(string refString, JToken[] stepResults, int currentIndex, int stepCount,
            out JToken resolved, out string reason, out int referencedStep)
        {
            resolved = null;
            reason = null;

            if (!TryParseBatchRef(refString, out referencedStep, out var selectPath, out var parseError))
            {
                reason = parseError;
                return false;
            }

            if (referencedStep >= stepCount)
            {
                reason = $"step index {referencedStep} is out of range (batch has {stepCount} steps)";
                return false;
            }
            if (referencedStep >= currentIndex)
            {
                reason = $"forward reference — steps[{referencedStep}] does not run before steps[{currentIndex}]; $refs may only point to earlier steps";
                return false;
            }
            if (stepResults[referencedStep] == null)
            {
                reason = $"steps[{referencedStep}] did not complete successfully, so its result is not available";
                return false;
            }

            if (selectPath == null)
            {
                resolved = stepResults[referencedStep];
                return true;
            }

            try
            {
                resolved = stepResults[referencedStep].SelectToken(selectPath, errorWhenNoMatch: false);
            }
            catch (Exception ex)
            {
                reason = $"invalid SelectToken path '{selectPath}': {ex.Message}";
                return false;
            }
            if (resolved == null)
            {
                reason = $"path '{selectPath}' matched nothing in the result of steps[{referencedStep}]";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Does a dry-run structural validation of a single reference (no real value to resolve yet): the index is
        /// in range and points to an earlier step, the referenced skill is known, and the path's first segment
        /// appears among the referenced skill's declared Outputs. Findings are only warnings — Outputs metadata may be incomplete, and a dry-run batch never halts.
        /// </summary>
        private static void ValidateBatchRefStructural(string refString, int currentIndex, JArray steps, List<string> warnings)
        {
            string label = $"$ref '{refString ?? "(non-string)"}'";
            if (!TryParseBatchRef(refString, out var refStep, out var selectPath, out var parseError))
            {
                warnings.Add($"{label}: malformed ({parseError}) — this step will fail at execution.");
                return;
            }
            if (refStep >= steps.Count)
            {
                warnings.Add($"{label}: step index {refStep} is out of range (batch has {steps.Count} steps) — this step will fail at execution.");
                return;
            }
            if (refStep >= currentIndex)
            {
                warnings.Add($"{label}: forward reference (steps[{refStep}] does not run before steps[{currentIndex}]) — this step will fail at execution.");
                return;
            }

            string refSkillName = GetBatchStepSkillName(steps[refStep]);
            if (string.IsNullOrWhiteSpace(refSkillName) || !SkillRouter.TryGetSkill(refSkillName, out var refSkill))
            {
                warnings.Add($"{label}: referenced steps[{refStep}] has no known skill ('{refSkillName}') — this step will fail at execution.");
                return;
            }

            if (selectPath == null)
                return;
            var outputs = SkillRouter.GetEffectiveOutputs(refSkill);
            if (outputs == null || outputs.Length == 0)
                return; // No declared Outputs to compare against
            string firstSegment = FirstSelectTokenSegment(selectPath);
            if (firstSegment == null)
                return; // "[0]…" indexes into the result root — there's no name to validate

            foreach (var output in outputs)
            {
                if (string.Equals(output, firstSegment, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            warnings.Add($"{label}: field '{firstSegment}' is not among the declared outputs of '{refSkillName}' [{string.Join(", ", outputs)}] — declared Outputs may be incomplete, so this is only a warning; verify at execution.");
        }

        private static string FirstSelectTokenSegment(string selectPath)
        {
            if (string.IsNullOrEmpty(selectPath) || selectPath[0] == '[')
                return null;
            int cut = selectPath.IndexOfAny(new[] { '.', '[' });
            return cut < 0 ? selectPath : selectPath.Substring(0, cut);
        }

        /// <summary>
        /// Post-processes a step's DryRun payload after its $ref parameters are stripped from the validation body:
        /// drops the MISSING_PARAM entries stripping produced, downgrades that step's semanticErrors to warnings
        /// (the check ran without a reference value, so pass/fail is only a guess), recomputes 'valid' from what's
        /// left, and attaches refsValidated so the caller can see which parameters only got a structural check.
        /// </summary>
        private static void AdjustDryRunPayloadForRefs(JObject stepPayload, List<BatchRefNode> refNodes,
            HashSet<string> strippedParams, bool wholeArgsFromRef, List<string> refWarnings)
        {
            var refsValidated = new JArray();
            foreach (var refNode in refNodes)
            {
                refsValidated.Add(new JObject
                {
                    ["param"] = refNode.TopLevelParam ?? "(args)",
                    ["ref"] = refNode.RefString,
                    ["structural"] = true,
                });
            }
            stepPayload["refsValidated"] = refsValidated;

            if (!(stepPayload["validation"] is JObject validation))
                return; // An error payload (unknown skill, etc.) — refsValidated is already attached, nothing more to correct

            var addedWarnings = new List<string>(refWarnings);

            if (validation["missingParams"] is JArray missing && missing.Count > 0)
            {
                for (int m = missing.Count - 1; m >= 0; m--)
                {
                    string param = missing[m]?.ToString();
                    if (wholeArgsFromRef || (param != null && strippedParams.Contains(param)))
                        missing.RemoveAt(m);
                }
                if (missing.Count == 0)
                    validation["missingParams"] = null;
            }

            if (validation["semanticErrors"] is JArray semantic && semantic.Count > 0)
            {
                foreach (var item in semantic)
                    addedWarnings.Add($"semantic check not confirmable while '$ref' params are unresolved (structural-only): {item.ToString(Formatting.None)}");
                validation["semanticErrors"] = null;
            }

            if (addedWarnings.Count > 0)
            {
                if (!(validation["warnings"] is JArray warningsArr))
                {
                    warningsArr = new JArray();
                    validation["warnings"] = warningsArr;
                }
                foreach (var warning in addedWarnings)
                    warningsArr.Add(warning);
            }

            stepPayload["valid"] =
                IsNullOrEmptyJArray(validation["missingParams"]) &&
                IsNullOrEmptyJArray(validation["unknownParams"]) &&
                IsNullOrEmptyJArray(validation["typeErrors"]) &&
                IsNullOrEmptyJArray(validation["semanticErrors"]) &&
                IsNullOrEmptyJArray(validation["missingPackages"]);
        }

        private static bool IsNullOrEmptyJArray(JToken token) => !(token is JArray arr) || arr.Count == 0;
    }
}

// Producer:Betsy
