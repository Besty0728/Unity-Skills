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
        /// <summary>
        /// Parses ?mode= / ?dryRun= from the already-parsed query string. Returns false when either parameter is
        /// present but its value can't be recognized (writes an INVALID_MODE error to the job) — the request must
        /// never execute in that case. Without this guard, an agent that misspells the mode (e.g. ?mode=dry_run,
        /// ?dryRun=1) would think it was previewing, while the server had already silently executed for real.
        /// </summary>
        private static bool TryResolveRequestMode(RequestJob job, Dictionary<string, string> qs, string skillName, out SkillRouter.RequestMode mode)
        {
            mode = SkillRouter.RequestMode.Execute;

            if (qs.TryGetValue("mode", out var modeValue) && !string.IsNullOrWhiteSpace(modeValue))
            {
                if (modeValue.Equals("dryRun", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.DryRun;
                    return true;
                }
                if (modeValue.Equals("plan", StringComparison.OrdinalIgnoreCase))
                {
                    mode = SkillRouter.RequestMode.Plan;
                    return true;
                }

                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Unknown mode '{modeValue}' — request was NOT executed.",
                    skill: skillName,
                    details: new
                    {
                        received = modeValue,
                        validValues = new[] { "dryRun", "plan" },
                        hint = "Use '?mode=dryRun' to validate without executing, '?mode=plan' for an execution plan, or omit '?mode=' entirely to execute for real.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            if (!TryReadDryRunFlag(job, qs, skillName, out var dryRun))
                return false;
            if (dryRun)
                mode = SkillRouter.RequestMode.DryRun;
            return true;
        }

        /// <summary>
        /// Reads ?dryRun= for both endpoints: true previews, false or absent executes. Any other value writes
        /// INVALID_MODE and returns false, so a misspelled flag never executes for real.
        /// </summary>
        private static bool TryReadDryRunFlag(RequestJob job, Dictionary<string, string> qs, string skillForErrors, out bool dryRun)
        {
            dryRun = false;
            if (!qs.TryGetValue("dryRun", out var dryRunVal) || string.IsNullOrWhiteSpace(dryRunVal))
                return true;
            if (dryRunVal.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                return true;
            }
            if (dryRunVal.Equals("false", StringComparison.OrdinalIgnoreCase))
                return true; // Explicit false = execute for real

            job.StatusCode = 400;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.InvalidMode,
                $"Invalid dryRun value '{dryRunVal}' — request was NOT executed.",
                skill: skillForErrors,
                details: new
                {
                    received = dryRunVal,
                    validValues = new[] { "true", "false" },
                    hint = "Use '?dryRun=true' (or '?mode=dryRun') to validate without executing; omit the parameter to execute for real.",
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
            return false;
        }

        /// <summary>
        /// Parses ?diff= for POST /skill/{name}. A semantic sceneDiff is only meaningful for a real execution, so
        /// it's silently ignored under ?mode=dryRun / ?mode=plan (nothing executed, so there's nothing to diff).
        /// An unrecognized value is rejected with 400 (consistent with TryResolveRequestMode) rather than silently
        /// ignored, so an agent that misspells ?diff doesn't think it got a diff while the server quietly dropped it.
        /// Only returns false when the value is invalid (and writes a 400); every other case leaves captureDiff set.
        /// </summary>
        private static bool TryResolveDiff(RequestJob job, Dictionary<string, string> qs, string skillName, SkillRouter.RequestMode mode, out bool captureDiff)
        {
            captureDiff = false;

            if (!qs.TryGetValue("diff", out var diffValue) || string.IsNullOrWhiteSpace(diffValue))
                return true;

            bool requested;
            if (diffValue.Equals("1", StringComparison.OrdinalIgnoreCase) || diffValue.Equals("true", StringComparison.OrdinalIgnoreCase))
                requested = true;
            else if (diffValue.Equals("0", StringComparison.OrdinalIgnoreCase) || diffValue.Equals("false", StringComparison.OrdinalIgnoreCase))
                requested = false;
            else
            {
                job.StatusCode = 400;
                job.ResponseJson = SkillErrorResponse.Build(
                    SkillErrorCode.InvalidMode,
                    $"Invalid diff value '{diffValue}' — request was NOT executed.",
                    skill: skillName,
                    details: new
                    {
                        received = diffValue,
                        validValues = new[] { "1", "true", "0", "false" },
                        hint = "Use '?diff=1' (or '?diff=true') to attach a semantic sceneDiff to the success response; omit it or use '?diff=0' for none. Ignored under ?mode=dryRun/plan.",
                    },
                    retryStrategy: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }

            // diff only applies to a real execution; dryRun/plan previews have nothing to compare against.
            captureDiff = requested && mode == SkillRouter.RequestMode.Execute;
            return true;
        }

        /// <summary>
        /// The 405 METHOD_NOT_ALLOWED body for GET /skill/{name} on a registered skill. It carries the POST the caller
        /// most likely meant, ready to paste: the query's arguments become the JSON body (details.body, also the fix's
        /// args) and the request-level keys stay in the URL (details.curl). Main thread only (reads the skill's
        /// declared parameters), otherwise pure.
        /// </summary>
        internal static string BuildMethodNotAllowedResponse(string skillName, string rawQuery, System.Reflection.ParameterInfo[] parameters, int port)
        {
            var body = ConvertQueryToSkillBody(rawQuery, parameters, out string requestQuery);
            string url = $"http://localhost:{port}/skill/{skillName}{requestQuery}";
            string curl = $"curl -s -X POST '{EscapeForSingleQuotes(url)}' -H 'Content-Type: application/json' -d '{EscapeForSingleQuotes(body.ToString(Formatting.None))}'";

            return SkillErrorResponse.Build(
                SkillErrorCode.MethodNotAllowed,
                $"Skills are called with POST /skill/{skillName} and a JSON body; this GET was not executed.",
                skill: skillName,
                details: new
                {
                    method = "GET",
                    allowed = "POST",
                    url,
                    body,
                    curl,
                },
                suggestedFixes: new List<SuggestedFix>
                {
                    new SuggestedFix
                    {
                        action = "retry",
                        skill = skillName,
                        args = body,
                        reason = $"Resend as POST with the query parameters as the JSON body: {curl}",
                    },
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        /// <summary>
        /// Moves a GET query into a POST body. Pairs are decoded form-style ('+' is a space) in their original order; a
        /// bare key becomes true. Each value is typed by the skill's declared parameter: string and enum parameters stay
        /// strings, bool and numeric ones become JSON booleans and numbers when they parse, and array/object parameters
        /// take JSON text as JSON. A key the skill does not declare becomes a number or boolean when it parses as one,
        /// otherwise a string (the POST then reports it). Request-level keys are returned in requestQuery, still encoded.
        /// </summary>
        internal static JObject ConvertQueryToSkillBody(string rawQuery, System.Reflection.ParameterInfo[] parameters, out string requestQuery)
        {
            var body = new JObject();
            var requestPairs = new List<string>();

            string raw = string.IsNullOrEmpty(rawQuery) ? string.Empty : rawQuery.TrimStart('?');
            foreach (var pair in raw.Split('&'))
            {
                if (pair.Length == 0)
                    continue;
                int eq = pair.IndexOf('=');
                if (eq == 0)
                    continue;

                string key = DecodeQueryComponent(eq < 0 ? pair : pair.Substring(0, eq)).Trim();
                if (key.Length == 0)
                    continue;
                if (RequestLevelQueryKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                {
                    requestPairs.Add(pair);
                    continue;
                }

                string value = eq < 0 ? "true" : DecodeQueryComponent(pair.Substring(eq + 1));
                var parameter = parameters?.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                body[parameter?.Name ?? key] = ConvertQueryValue(value, parameter?.ParameterType, key);
            }

            requestQuery = requestPairs.Count > 0 ? "?" + string.Join("&", requestPairs) : string.Empty;
            return body;
        }

        private static JToken ConvertQueryValue(string value, Type declaredType, string key)
        {
            var type = declaredType == null ? null : (Nullable.GetUnderlyingType(declaredType) ?? declaredType);
            var invariant = System.Globalization.CultureInfo.InvariantCulture;

            if (type == typeof(string) || (type != null && type.IsEnum) ||
                (type == null && string.Equals(key, "entityId", StringComparison.OrdinalIgnoreCase)))
                return value;

            if (type == typeof(bool))
            {
                if (bool.TryParse(value, out var flag)) return flag;
                if (value == "1") return true;
                if (value == "0") return false;
                return value;
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
                return long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, invariant, out var whole)
                    ? (JToken)whole : value;

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return TryParseFiniteDouble(value, out var real) ? (JToken)real : value;

            if (type != null)
            {
                var trimmed = value.TrimStart();
                if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    try { return JToken.Parse(value); }
                    catch (JsonException) { }
                }
                return value;
            }

            if (bool.TryParse(value, out var undeclaredFlag)) return undeclaredFlag;
            if (long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, invariant, out var undeclaredWhole))
                return undeclaredWhole;
            if (TryParseFiniteDouble(value, out var undeclaredReal)) return undeclaredReal;
            return value;
        }

        private static bool TryParseFiniteDouble(string value, out double result) =>
            double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) &&
            !double.IsNaN(result) && !double.IsInfinity(result);

        private static string DecodeQueryComponent(string component) =>
            Uri.UnescapeDataString(component.Replace('+', ' '));

        private static string EscapeForSingleQuotes(string text) => text.Replace("'", "'\\''");

        /// <summary>The raw query (leading '?' dropped) without any pair whose key is <paramref name="key"/>; other pairs stay encoded, in order.</summary>
        private static string QueryWithout(string rawQuery, string key)
        {
            if (string.IsNullOrEmpty(rawQuery)) return string.Empty;
            var kept = rawQuery.TrimStart('?').Split('&').Where(pair =>
            {
                if (pair.Length == 0) return false;
                int eq = pair.IndexOf('=');
                return !string.Equals(DecodeQueryComponent(eq < 0 ? pair : pair.Substring(0, eq)).Trim(), key, StringComparison.OrdinalIgnoreCase);
            });
            return string.Join("&", kept);
        }
    }
}

// Producer:Betsy
