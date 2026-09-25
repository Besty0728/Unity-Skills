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
        /// Returns null when the current surface profile exposes this skill; otherwise returns a serialized SURFACE_EXCLUDED
        /// payload, for the caller to present as-is.
        ///
        /// This message has exactly one job: stopping the agent from working around the exclusion. After hitting the wall, an
        /// agent instinctively retries, then goes looking for a neighboring module that can do the same write -- either would defeat the profile's purpose.
        /// So the payload names which profile is hiding it, states that the setting belongs to the user,
        /// and (under guide) hands over the manual-* doc, so the agent switches to acting as an instructor to get the task done instead.
        /// </summary>
        /// <summary>
        /// For an excluded skill, the manual-* doc that should be handed to the agent; returns null if there is none.
        /// That question is answered by category, with one exception for the "escape hatch" skills hidden by name: they're hidden
        /// precisely because their category can't describe what they can reach, so a doc derived from category would be the wrong guidance.
        /// </summary>
        private static string SurfaceExclusionManualDoc(SkillInfo skill) =>
            SkillsSurfaceProfile.IsAlwaysHiddenSkill(skill.Name)
                ? null
                : SkillsSurfaceProfile.ManualDocFor(skill.Category);

        /// <summary>
        /// The two rejection paths -- the dry-run preview (<paramref name="forPreview"/>) and the execute gate -- share one copy,
        /// so they never tell the agent something different about the same wall. The preview states "what would happen",
        /// the gate tells the agent "what to do instead." Three cases, in order of precedence:
        /// <list type="bullet">
        /// <item><b>An escape hatch hidden by name:</b> no manual doc applies, because the reason is this skill's reach, not its module.
        /// Points at the Editor menu -- exactly what this skill was driving in the first place, so the user can do by hand what the AI isn't allowed to do for them.</item>
        /// <item><b>guide, with a manual doc available:</b> hand over that doc; the agent finishes the task acting as an instructor.</item>
        /// <item><b>Everything else:</b> only the user can lift it.</item>
        /// </list>
        /// </summary>
        private static string BuildSurfaceExclusionHint(SkillInfo skill, bool forPreview)
        {
            var profile = SkillsSurfaceProfile.CurrentWire;

            if (SkillsSurfaceProfile.IsAlwaysHiddenSkill(skill.Name))
            {
                return forPreview
                    ? $"Hidden by the \"{profile}\" surface profile — it can execute any menu item, including the writes this profile withdraws, so it is off the menu in every mode, allowlist included. Tell the user which Editor menu path does the job and let them run it."
                    : $"Do not retry and do not look for another route — this skill drives arbitrary Editor menu items, which is why the profile withdraws it wholesale. Name the exact menu path (e.g. GameObject > Create Empty) and walk the user through clicking it, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel.";
            }

            var manualDoc = SkillsSurfaceProfile.ManualDocFor(skill.Category);
            if (manualDoc != null)
            {
                return forPreview
                    ? $"Hidden by the \"{profile}\" surface profile — executing is impossible in any mode, allowlist included. Guide the user by hand ({manualDoc}), or they switch the profile back to \"full\" in the UnitySkills panel."
                    // category is already named in the message and in details.category, so the hint just says "this change"
                    // rather than interpolating it -- "walk the user through the Sample change"
                    // reads as meaningless to the one audience that matters here.
                    : $"Do not retry and do not substitute another module — the write is off the menu, not failing. Read {manualDoc} and walk the user through the change in the Editor yourself, or ask them to switch the surface profile back to \"full\" in the UnitySkills panel if they want it automated.";
            }

            // Only noSceneAuthoring reaches here right now, so hardcoding "excludes scene-authoring writes"
            // is safe: guide never gets here, because every category guide hides ships a manual-* doc,
            // which gets caught by the branch above. That invariant is guarded by
            // SkillsSurfaceProfileTests.EveryGuideHiddenCategory_ShipsAManualDoc --
            // a newly added guide category must have its manual-* doc ready first, or this branch would start telling guide users
            // their write was blocked for being "scene authoring" when it wasn't.
            return forPreview
                ? $"Hidden by the \"{profile}\" surface profile, which excludes scene-authoring writes — executing is impossible in any mode, allowlist included. Only the user can switch the profile back to \"full\" in the UnitySkills panel."
                : $"Do not retry and do not substitute another module. The \"{profile}\" profile excludes scene-authoring writes; tell the user this step needs one and let them switch the surface profile back to \"full\" in the UnitySkills panel.";
        }

        private static string ApplySurfaceGate(SkillInfo skill, string name)
        {
            if (!SkillsSurfaceProfile.IsExcluded(skill))
                return null;

            var profile = SkillsSurfaceProfile.CurrentWire;
            var category = skill.Category.ToString();
            var manualDoc = SurfaceExclusionManualDoc(skill);
            var hint = BuildSurfaceExclusionHint(skill, forPreview: false);

            SkillsAuditLog.Append("call", new
            {
                skill = name,
                result = "surfaceExcluded",
                surfaceProfile = profile,
                category,
            });

            return SkillErrorResponse.Build(
                SkillErrorCode.SurfaceExcluded,
                // The escape hatch uses its own wording: "a write skill in the Editor category" would be both wrong
                // (its category isn't what's hidden) and useless (it wouldn't explain why this skill is hidden).
                // Every other exclusion really is category + write.
                SkillsSurfaceProfile.IsAlwaysHiddenSkill(name)
                    ? $"Skill '{name}' is hidden by the current surface profile '{profile}': it can execute any Editor menu item, which would reach the writes this profile withdraws."
                    : $"Skill '{name}' is hidden by the current surface profile '{profile}': it is a write skill in the {category} category.",
                skill: name,
                details: new
                {
                    surfaceProfile = profile,
                    category,
                    manualDoc,
                    userControlled = true,
                    hint,
                },
                // The closest available strategy: this call must not be repeated as-is. Unlike ask_user_and_grant,
                // there's no token to obtain here -- either the user changes a panel setting, or the task gets done by hand.
                retryStrategy: SkillErrorResponse.Abort);
        }

        /// <summary>
        /// Whether the dryRun policy demands a token for this call right now. Beyond <see cref="DryRunPolicyService.IsGated(SkillInfo)"/>,
        /// a call that can only end in SURFACE_EXCLUDED or MODE_FORBIDDEN is left to those gates, so it gets its real answer in
        /// the same round trip instead of a token it cannot use. Shared by the /skill/ gate, token issuance on ?mode=dryRun and
        /// the /skills/batch gate.
        /// </summary>
        internal static bool DryRunGateApplies(SkillInfo skill) =>
            DryRunPolicyService.IsGated(skill)
            && !SkillsSurfaceProfile.IsExcluded(skill)
            && !PreviewsModeForbidden(skill);

        /// <summary>
        /// The MODE_FORBIDDEN rung of <see cref="BuildModeAuthorizationPreview"/>'s ladder as a pure check (CheckAccess would
        /// consume a one-shot grant). A one-shot is only ever set around an approval replay, which is never gated.
        /// </summary>
        private static bool PreviewsModeForbidden(SkillInfo skill) =>
            SkillsModeManager.CurrentMode != SkillsOperatingMode.Bypass
            && !SkillsModeManager.IsInAllowlist(skill.Name)
            && SkillsModeManager.IsForbiddenInSemi(skill);

        /// <summary>
        /// Returns null when the call may proceed: the policy does not gate it, or the presented ?dryRunToken= was issued for
        /// exactly these arguments and is now consumed. Otherwise DRYRUN_REQUIRED with the v2 preview and a fresh token.
        /// </summary>
        private static string ApplyDryRunPolicyGate(SkillInfo skill, string name, string json, DryRunGateContext gate)
        {
            if (!DryRunGateApplies(skill))
                return null;

            var argsHash = DryRunPolicyService.HashSkillArgs(skill, json);
            var check = DryRunPolicyService.TryConsume(gate.Token, skill.Name, argsHash, out var tokenAgeSec);
            if (check == OneTimeTokenStore.Check.Ok)
            {
                SkillsAuditLog.Append("dryrun_passed", new { skill = name, tokenAgeSec });
                return null;
            }

            var reason = DryRunPolicyService.ReasonFor(check, gate.Token);
            var token = DryRunPolicyService.IssueToken(skill.Name, argsHash);
            JToken preview = null;
            try { preview = DryRunPolicyService.ParseJson(DryRun(name, json, WireV2)); }
            catch { /* best-effort, like the confirmation gate's preview: the token is valid either way */ }

            SkillsAuditLog.Append("call", new
            {
                skill = name,
                result = "dryRunRequired",
                policy = DryRunPolicyService.CurrentWire,
                reason,
                tokenIssued = true,
            });
            return DryRunPolicyService.BuildSkillRequiredResponse(skill.Name, reason, token, preview, gate.QueryForFixes);
        }

        /// <summary>
        /// Returns null when the permission tier allows this skill; otherwise returns a serialized error payload
        /// (MODE_RESTRICTED or MODE_FORBIDDEN), for the caller to present as-is.
        /// Always writes a "call" audit entry when the verdict is Allowed, so silent execution under Auto mode is still traceable.
        /// </summary>
        private static string ApplyModeGate(SkillInfo skill, string name, ParameterValidationResult validation)
        {
            var argsForHash = validation?.Args == null ? new JObject() : (JObject)validation.Args.DeepClone();
            argsForHash.Remove("_confirm");
            var argsJson = argsForHash.ToString(Formatting.None);

            // Critical: allowlist status must be read before CheckAccess -- CheckAccess consumes the one-shot marker internally,
            // while IsInAllowlist can still be queried repeatedly afterward. Recording the allowlist hit first lets the audit distinguish allowlist vs oneShot vs auto.
            bool allowlistHit = SkillsModeManager.IsInAllowlist(skill.Name);
            var access = SkillsModeManager.CheckAccess(skill);
            var currentMode = SkillsModeManager.CurrentMode;
            var modeWire = SkillsModeManager.ModeToWire(currentMode);

            switch (access)
            {
                case SkillsModeManager.AccessResult.Allowed:
                    bool highImpact = currentMode == SkillsOperatingMode.Auto
                        && (skill.MutatesScene || skill.MutatesAssets
                            || skill.Operation.HasFlag(SkillOperation.Modify)
                            || skill.Operation.HasFlag(SkillOperation.Create));
                    // grantSource: an allowlist hit takes top priority; otherwise Bypass mode counts as bypass;
                    // every other Allowed that's neither Allowlist nor Bypass is classified as auto (CheckAccess already consumed
                    // any one-shot token before this call, so it can't be told apart afterward; this is the best approximation currently observable).
                    string grantSource;
                    if (allowlistHit) grantSource = "allowlist";
                    else if (currentMode == SkillsOperatingMode.Bypass) grantSource = "bypass";
                    else grantSource = "auto";
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "allowed",
                        highImpact,
                        allowlistHit,
                        grantSource,
                    });
                    return null;

                case SkillsModeManager.AccessResult.Forbidden:
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "forbidden",
                    });
                    return SkillErrorResponse.Build(
                        SkillErrorCode.ModeForbidden,
                        "This skill is classified as never-in-semi and is only allowed in Bypass mode.",
                        skill: name,
                        details: new
                        {
                            currentMode = modeWire,
                            riskLevel = skill.RiskLevel,
                            mayEnterPlayMode = skill.MayEnterPlayMode,
                            mayTriggerReload = skill.MayTriggerReload,
                            operation = FormatOperation(skill.Operation),
                            hint = "Switch the Unity panel to Bypass mode, or use a different skill.",
                        },
                        retryStrategy: SkillErrorResponse.Abort);

                case SkillsModeManager.AccessResult.NeedsGrant:
                    var (token, ttl, channel) = SkillsModeManager.IssueGrantRequest(name, argsJson);
                    var channelWire = SkillsModeManager.ChannelToWire(channel);
                    var pendingSummary = SkillsModeManager.PeekPending(token);
                    SkillsAuditLog.Append("call", new
                    {
                        skill = name,
                        mode = modeWire,
                        skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                        result = "restricted",
                        grantToken = token,
                        channel = channelWire,
                    });
                    return SkillErrorResponse.Build(
                        SkillErrorCode.ModeRestricted,
                        "This skill is FullAuto and requires user approval under the current mode.",
                        skill: name,
                        details: new
                        {
                            currentMode = modeWire,
                            skillMode = SkillsModeManager.SkillModeToWire(skill.Mode),
                            approvalChannel = channelWire,
                            grantRequestToken = token,
                            tokenTtlSeconds = ttl,
                            argsSummary = pendingSummary?.ArgsSummary,
                            hint = channel == SkillsModeManager.ApprovalChannel.Dialog
                                ? "Ask the user; on consent POST /permission/grant {skill, token}. That grant call executes the skill in-line and returns the result (response.result). Do not re-call the original skill."
                                : "Tell the user to click Approve on the Unity panel; then POST /permission/grant {skill, token} once. That grant call executes the skill in-line and returns the result. Do not poll grant; do not re-call the original skill.",
                        },
                        retryStrategy: SkillErrorResponse.RetryAskUserAndGrant);
            }
            return null;
        }

        /// <summary>
        /// Returns null when this skill is allowed to execute (the token has been consumed); otherwise returns a serialized error payload
        /// (CONFIRMATION_REQUIRED or INVALID_TOKEN), which the caller should pass back to the client as-is.
        /// </summary>
        private static string ApplyConfirmationGate(
            SkillInfo skill,
            string name,
            string rawJson,
            ParameterValidationResult validation)
        {
            string token = null;
            if (validation.Args.TryGetValue("_confirm", StringComparison.OrdinalIgnoreCase, out var ct) && ct.Type != JTokenType.Null)
            {
                token = ct.ToString();
            }

            // argsHash excludes _confirm, so the same parameters hash identically across both calls.
            var argsForHash = (JObject)validation.Args.DeepClone();
            argsForHash.Remove("_confirm");
            var argsForHashJson = argsForHash.ToString(Formatting.None);

            if (string.IsNullOrEmpty(token))
            {
                var (newToken, ttl) = ConfirmationTokenService.IssueToken(name, argsForHashJson);
                JObject dryRunPreview = null;
                try
                {
                    var dryRunJson = DryRun(name, rawJson);
                    if (!string.IsNullOrEmpty(dryRunJson))
                        dryRunPreview = JObject.Parse(dryRunJson);
                }
                catch
                {
                    // The dry-run is best-effort; the token is still valid even if it fails.
                }

                return SkillErrorResponse.Build(
                    SkillErrorCode.ConfirmationRequired,
                    "This skill is high-risk and requires confirmation. Re-call with the same args plus '_confirm':'<token>' to execute.",
                    skill: name,
                    details: new
                    {
                        _confirm = newToken,
                        ttlSeconds = ttl,
                        why = $"riskLevel={skill.RiskLevel}, operation={string.Join("|", FormatOperation(skill.Operation) ?? new[] { "?" })}",
                        dryRun = dryRunPreview
                    },
                    retryStrategy: SkillErrorResponse.RetryConfirmAndRetry,
                    retryAfterSeconds: 0);
            }

            if (!ConfirmationTokenService.TryConsume(token, name, argsForHashJson))
            {
                return SkillErrorResponse.Build(
                    SkillErrorCode.InvalidToken,
                    "_confirm token is invalid, expired, or args differ from when the token was issued.",
                    skill: name,
                    details: new { suggestion = "Re-call without '_confirm' to receive a fresh token bound to your current args." },
                    retryStrategy: SkillErrorResponse.RetryConfirmAndRetry);
            }

            return null;
        }
    }
}

// Producer:Betsy
