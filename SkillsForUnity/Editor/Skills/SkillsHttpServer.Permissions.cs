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
        // ===== Permission system =====

        private static void HandlePermissionRequest(RequestJob job)
        {
            string path = job.Path ?? string.Empty;

            if (string.Equals(path, "/permission/status", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionStatus(job);
                return;
            }

            if (string.Equals(path, "/permission/audit", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionAudit(job);
                return;
            }

            if (string.Equals(path, "/permission/allowlist", StringComparison.OrdinalIgnoreCase) && job.HttpMethod == "GET")
            {
                HandlePermissionAllowlistList(job);
                return;
            }

            if (job.HttpMethod == "POST")
            {
                if (string.Equals(path, "/permission/grant", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionGrant(job);
                    return;
                }
                if (string.Equals(path, "/permission/approve", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionApprove(job);
                    return;
                }
                if (string.Equals(path, "/permission/deny", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionDeny(job);
                    return;
                }
                if (string.Equals(path, "/permission/allowlist/add", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionAllowlistAdd(job);
                    return;
                }
                if (string.Equals(path, "/permission/allowlist/remove", StringComparison.OrdinalIgnoreCase))
                {
                    HandlePermissionAllowlistRemove(job);
                    return;
                }
                if (string.Equals(path, "/permission/revoke", StringComparison.OrdinalIgnoreCase))
                {
                    // Deprecated alias: forwards to the allowlist/remove logic, response carries deprecated=true.
                    HandlePermissionRevoke(job);
                    return;
                }
            }

            job.StatusCode = 404;
            job.ResponseJson = SkillErrorResponse.Build(
                SkillErrorCode.NotFound,
                "Permission endpoint not found",
                details: new
                {
                    endpoints = new[]
                    {
                        "GET /permission/status",
                        "POST /permission/grant",
                        "POST /permission/approve",
                        "POST /permission/deny",
                        "GET /permission/allowlist",
                        "POST /permission/allowlist/add",
                        "POST /permission/allowlist/remove",
                        "POST /permission/revoke",
                        "GET /permission/audit"
                    }
                },
                retryStrategy: SkillErrorResponse.RetryFixAndRetry);
        }

        private static void HandlePermissionStatus(RequestJob job)
        {
            var qs = SkillRouter.ParseQueryString(job.QueryString);
            string focusToken = qs.TryGetValue("token", out var tokenVal) ? tokenVal : null;

            var pending = SkillsModeManager.PendingGrantRequests;
            var allowlist = SkillsModeManager.AllowlistSkills;

            object focusEntry = null;
            if (!string.IsNullOrEmpty(focusToken))
            {
                var match = pending.FirstOrDefault(p => string.Equals(p.Token, focusToken, StringComparison.Ordinal));
                if (match != null)
                {
                    focusEntry = new
                    {
                        token = match.Token,
                        skill = match.SkillName,
                        argsSummary = match.ArgsSummary,
                        channel = match.Channel,
                        approvedByPanel = match.ApprovedByPanel,
                        expiresAtUtc = match.ExpiresAtUtc.ToString("o"),
                        ttlSeconds = Math.Max(0, (int)(match.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
                    };
                }
            }

            job.StatusCode = 200;
            // Field rename: `granted` → `allowlist`. The `granted` field is kept as a compatibility alias for one
            // version, and will be removed in the next minor version — clients should migrate to the `allowlist` field.
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                mode = SkillsModeManager.ModeToWire(SkillsModeManager.CurrentMode),
                panelApprovalRequired = SkillsModeManager.PanelApprovalRequired,
                dryRunPolicy = DryRunPolicyService.CurrentWire,
                allowlist = allowlist,
                granted = allowlist, // deprecated alias — removed in the next minor version
                pending = pending.Select(p => new
                {
                    token = p.Token,
                    skill = p.SkillName,
                    argsSummary = p.ArgsSummary,
                    channel = p.Channel,
                    approvedByPanel = p.ApprovedByPanel,
                    expiresAtUtc = p.ExpiresAtUtc.ToString("o"),
                    ttlSeconds = Math.Max(0, (int)(p.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
                }).ToArray(),
                focus = focusEntry,
                counts = new
                {
                    allowlist = allowlist.Count,
                    granted = allowlist.Count, // deprecated alias
                    pending = pending.Count,
                },
                deprecated = new
                {
                    granted = "Use 'allowlist' instead. The 'granted' field will be removed in a future minor version.",
                },
            }, _jsonSettings);
        }

        private static void HandlePermissionGrant(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var sToken) ? sToken?.ToString() : null;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var tToken) ? tToken?.ToString() : null;

            if (string.IsNullOrWhiteSpace(skill) || string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Both 'skill' and 'token' are required.",
                    details: new { required = new[] { "skill", "token" }, optional = new[] { "args" } },
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            // The args field is optional — Approach B prefers the entry's cached original argsJson. When the body
            // carries args, it participates in hash validation under the existing rules; else the entry cache is read directly (TryPeekArgsJson).
            bool argsProvided = body.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var argsToken)
                                && argsToken != null && argsToken.Type != JTokenType.Null;
            string argsJson;
            if (argsProvided)
            {
                argsJson = ExtractArgsJson(body);
            }
            else
            {
                // Read the cached original argsJson directly from the entry — works for both zero-arg and parameterized
                // skills, so an AI calling grant only needs the token, matching "one-step execution" semantics. Falls back
                // to "{}" when the entry doesn't exist/has expired, so TryGrantAndReturnArgs below returns Invalid with a clear error.
                argsJson = SkillsModeManager.TryPeekArgsJson(token) ?? "{}";
            }

            // Note: HandlePermissionGrant is called by ProcessJobQueue on the main thread (EditorApplication.update), so
            // the ThreadStatic one-shot token set by TryGrantAndReturnArgs, and the subsequent SkillRouter.Execute, both
            // run on that same main thread — the thread-safety precondition holds, no extra dispatch needed.
            var (outcome, cachedSkill, cachedArgs) = SkillsModeManager.TryGrantAndReturnArgs(skill, token, argsJson);
            RefineAgentId(job);
            // Covers every audit entry this handler can write: the "call" event from Execute's permission gate
            // (Granted branch) and grant_executed below both get a late-bound "agent" field.
            using (SkillsAuditLog.BeginRequestContext(job.RemotePort, job.AgentId, job.AgentIdIsExplicit))
            switch (outcome)
            {
                case GrantOutcome.Granted:
                {
                    // Approach B one-step execution: the one-shot token was already set on the current thread by
                    // TryGrantAndReturnArgs, and SkillRouter.Execute → CheckAccess consumes it immediately for a single pass.
                    //
                    // But the consumption point isn't guaranteed to be reached: Execute's four parameter-validation checks
                    // (UnknownParam / MissingParam / TypeMismatch / SemanticInvalid) all early-return before the permission
                    // gate, and any of those — or an exception caught here — would leave the token on the main thread, to be
                    // picked up by a later request for the same-named skill with different args. finally's unconditional clear covers every path.
                    string execJson;
                    try
                    {
                        execJson = SkillRouter.Execute(cachedSkill, cachedArgs);
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"grant_executed failed for '{cachedSkill}': {ex.Message}");
                        execJson = SkillErrorResponse.Build(
                            SkillErrorCode.Internal,
                            ex.Message,
                            skill: cachedSkill,
                            details: new { type = ex.GetType().Name },
                            retryStrategy: SkillErrorResponse.RetryWaitAndRetry);
                    }
                    finally
                    {
                        SkillsModeManager.ClearOneShotBypass();
                    }

                    SkillsAuditLog.Append("grant_executed", new { skill = cachedSkill, token });

                    // Try to inline execJson as a JSON object so callers upstream can read fields directly; falls back to a string on failure.
                    object resultPayload;
                    try { resultPayload = JObject.Parse(execJson); }
                    catch
                    {
                        try { resultPayload = JToken.Parse(execJson); }
                        catch { resultPayload = execJson; }
                    }

                    job.StatusCode = 200;
                    job.ResponseJson = JsonConvert.SerializeObject(new
                    {
                        ok = true,
                        skill = cachedSkill,
                        executed = true,
                        result = resultPayload,
                    }, _jsonSettings);
                    return;
                }
                case GrantOutcome.PendingApproval:
                    job.StatusCode = 200;
                    job.ResponseJson = SkillErrorResponse.Build(
                        SkillErrorCode.GrantPendingApproval,
                        "Token is valid but waiting for panel approval.",
                        skill: skill,
                        details: new
                        {
                            hint = "Tell the user to click Approve on the Unity panel; then POST /permission/grant again to trigger one-step execution.",
                        },
                        retryStrategy: SkillErrorResponse.RetryAskUserAndGrant,
                        extra: new Dictionary<string, object> { ["ok"] = false, ["reason"] = "GRANT_PENDING_APPROVAL" });
                    return;
                default:
                    WritePermissionError(job, 400, SkillErrorCode.InvalidToken,
                        "Grant token is invalid, expired, or does not match (skill, args).",
                        skill: skill,
                        details: new { suggestion = "Re-trigger the skill to obtain a fresh MODE_RESTRICTED token bound to your current args." },
                        retry: SkillErrorResponse.RetryAskUserAndGrant);
                    return;
            }
        }

        private static void HandlePermissionApprove(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var t) ? t?.ToString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam, "'token' is required.", retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            RefineAgentId(job);
            bool ok;
            using (SkillsAuditLog.BeginRequestContext(job.RemotePort, job.AgentId, job.AgentIdIsExplicit))
                ok = SkillsModeManager.Approve(token);
            job.StatusCode = ok ? 200 : 404;
            job.ResponseJson = JsonConvert.SerializeObject(new { ok, token }, _jsonSettings);
        }

        private static void HandlePermissionDeny(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string token = body.TryGetValue("token", StringComparison.OrdinalIgnoreCase, out var t) ? t?.ToString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam, "'token' is required.", retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            RefineAgentId(job);
            bool ok;
            using (SkillsAuditLog.BeginRequestContext(job.RemotePort, job.AgentId, job.AgentIdIsExplicit))
                ok = SkillsModeManager.Deny(token);
            job.StatusCode = ok ? 200 : 404;
            job.ResponseJson = JsonConvert.SerializeObject(new { ok, token }, _jsonSettings);
        }

        private static void HandlePermissionRevoke(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            bool all = body.TryGetValue("all", StringComparison.OrdinalIgnoreCase, out var allToken)
                && allToken.Type == JTokenType.Boolean && allToken.ToObject<bool>();

            // Deprecated alias: forwards to AllowlistRemove / ClearAllowlist. The response carries `deprecated: true`,
            // to help clients migrate to /permission/allowlist/remove.
            if (all)
            {
                int before = SkillsModeManager.AllowlistSkills.Count;
                SkillsModeManager.ClearAllowlist();
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    revoked = before,
                    allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                    deprecated = true,
                    deprecationHint = "Use POST /permission/allowlist/remove with {all:true} instead.",
                }, _jsonSettings);
                return;
            }

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Provide either 'skill' or 'all:true'.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool removed = SkillsModeManager.RemoveFromAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                revoked = removed ? 1 : 0,
                skill,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                deprecated = true,
                deprecationHint = "Use POST /permission/allowlist/remove with {skill:'<name>'} instead.",
            }, _jsonSettings);
        }

        // ===== Allowlist endpoints =====

        private static void HandlePermissionAllowlistList(RequestJob job)
        {
            var allowlist = SkillsModeManager.AllowlistSkills;
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                allowlist = allowlist,
                count = allowlist.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAllowlistAdd(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "'skill' is required.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }
            if (!SkillRouter.HasSkill(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.SkillNotFound,
                    $"Unknown skill: {skill}",
                    details: new { skill, hint = "Use GET /skills to list registered skill names." },
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool added = SkillsModeManager.AddToAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                skill,
                added,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAllowlistRemove(RequestJob job)
        {
            if (!TryParseBody(job, out var body)) return;
            bool all = body.TryGetValue("all", StringComparison.OrdinalIgnoreCase, out var allToken)
                && allToken.Type == JTokenType.Boolean && allToken.ToObject<bool>();

            if (all)
            {
                int before = SkillsModeManager.AllowlistSkills.Count;
                SkillsModeManager.ClearAllowlist();
                job.StatusCode = 200;
                job.ResponseJson = JsonConvert.SerializeObject(new
                {
                    ok = true,
                    removed = before > 0,
                    removedCount = before,
                    allowlistCount = SkillsModeManager.AllowlistSkills.Count,
                }, _jsonSettings);
                return;
            }

            string skill = body.TryGetValue("skill", StringComparison.OrdinalIgnoreCase, out var s) ? s?.ToString() : null;
            if (string.IsNullOrWhiteSpace(skill))
            {
                WritePermissionError(job, 400, SkillErrorCode.MissingParam,
                    "Provide either 'skill' or 'all:true'.",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return;
            }

            bool removed = SkillsModeManager.RemoveFromAllowlist(skill);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                ok = true,
                skill,
                removed,
                allowlistCount = SkillsModeManager.AllowlistSkills.Count,
            }, _jsonSettings);
        }

        private static void HandlePermissionAudit(RequestJob job)
        {
            var qs = SkillRouter.ParseQueryString(job.QueryString);
            int limit = 100;
            if (qs.TryGetValue("limit", out var l) && int.TryParse(l, out var lp))
                limit = Mathf.Clamp(lp, 1, 1000);

            var entries = SkillsAuditLog.ReadRecent(limit);
            job.StatusCode = 200;
            job.ResponseJson = JsonConvert.SerializeObject(new
            {
                count = entries.Count,
                limit,
                entries,
                path = SkillsAuditLog.GetLogPath(),
            }, _jsonSettings);
        }

        private static bool TryParseBody(RequestJob job, out JObject body)
        {
            body = null;
            try
            {
                body = string.IsNullOrWhiteSpace(job.Body) ? new JObject() : JObject.Parse(job.Body);
                return true;
            }
            catch (Exception ex)
            {
                WritePermissionError(job, 400, SkillErrorCode.InvalidJson,
                    $"Invalid JSON body: {ex.Message}",
                    retry: SkillErrorResponse.RetryFixAndRetry);
                return false;
            }
        }

        private static string ExtractArgsJson(JObject body)
        {
            if (body == null) return string.Empty;
            if (!body.TryGetValue("args", StringComparison.OrdinalIgnoreCase, out var argsToken))
                return string.Empty;
            if (argsToken == null || argsToken.Type == JTokenType.Null) return string.Empty;
            if (argsToken.Type == JTokenType.String) return argsToken.ToString();
            // Strip _confirm and re-serialize, so the hash matches SkillRouter-side normalization.
            if (argsToken is JObject obj)
            {
                var clone = (JObject)obj.DeepClone();
                clone.Remove("_confirm");
                return clone.ToString(Formatting.None);
            }
            return argsToken.ToString(Formatting.None);
        }

        private static void WritePermissionError(
            RequestJob job, int statusCode, SkillErrorCode code, string message,
            string skill = null, object details = null, string retry = null)
        {
            job.StatusCode = statusCode;
            job.ResponseJson = SkillErrorResponse.Build(code, message, skill: skill, details: details, retryStrategy: retry);
        }

        private static bool IsTerminalStatus(string status)
        {
            if (string.IsNullOrEmpty(status)) return false;
            return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase);
        }
    }
}

// Producer:Betsy
