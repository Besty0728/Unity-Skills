using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Result of <see cref="SkillsModeManager.TryGrantDetailed"/>.
    /// Lets the HTTP handler distinguish "waiting for panel approval" (a normal state for the Panel channel) from "token invalid/expired" (an error).
    /// </summary>
    public enum GrantOutcome
    {
        Granted,
        PendingApproval,
        Invalid,
    }

    /// <summary>
    /// External (UI-visible) view of a pending grant request.
    /// Returned by <see cref="SkillsModeManager.PendingGrantRequests"/>;
    /// the UI panel renders these as cards with "Approve/Deny" buttons.
    /// </summary>
    public sealed class GrantRequest
    {
        public string Token;
        public string SkillName;
        public string ArgsSummary;
        public DateTime ExpiresAtUtc;
        /// <summary>True once the user has clicked "Approve" on the panel (Panel channel only).</summary>
        public bool ApprovedByPanel;
        /// <summary>"dialog" or "panel" — the wire string used in REST responses.</summary>
        public string Channel;
    }

    /// <summary>
    /// Core of the Skill mode permission system. Three operating modes (Approval / Auto / Bypass)
    /// + dual-channel approval (Dialog / Panel)
    /// + **Allowlist (a user-managed, persistent allowlist that can override IsForbiddenInSemi)**
    /// + **one-shot Approval** (grant/approve only clears the current invocation).
    ///
    /// Semantic split relative to the original Approval design:
    /// - **Allowlist channel**: managed manually by the user on the panel; a hit is allowed straight through,
    ///   **taking priority over IsForbiddenInSemi**, letting the user manually clear an otherwise high-risk-blocked skill.
    /// - **One-shot Approval**: grant/approve only clears the current invocation, no longer writes permanently to the allowlist.
    ///   The Granted branch uses the ThreadStatic <c>_currentOneShotSkill</c> so the subsequent CheckAccess
    ///   is let through exactly once, then immediately consumed and cleared.
    /// - **Grant Plan B (single-step execution)**: on Granted, <see cref="TryGrantAndReturnArgs"/> also returns
    ///   the cached original argsJson and marks it one-shot, so the HTTP endpoint can call SkillRouter.Execute directly.
    /// - **EditorPrefs migration**: the old key <c>UnitySkills_GrantedSkills</c> is auto-migrated on first launch to
    ///   the new key <c>UnitySkills_AllowlistSkills</c>; the migration is idempotent.
    ///
    /// State storage:
    /// - <c>CurrentMode</c> / <c>PanelApprovalRequired</c> / <c>AllowlistSkills</c>: EditorPrefs (per machine)
    /// - Pending grant tokens: in-memory only (TTL 5 minutes, at most 256 live)
    /// - One-shot bypass marker: in-memory ThreadStatic only
    ///
    /// Upgrade compatibility: if the install already has any pre-v1.9 <c>UnitySkills_*</c> pref
    /// (e.g. <c>UnitySkills_PreferredPort</c>), it defaults to <see cref="SkillsOperatingMode.Bypass"/>,
    /// so existing users see zero behavior change; a fresh install defaults to <see cref="SkillsOperatingMode.Auto"/> —
    /// every skill not auto-classified as NeverInSemi (including FullAuto write skills) executes directly, and only
    /// NeverInSemi skills (Delete / MayEnterPlayMode / MayTriggerReload / RiskLevel=high) return MODE_FORBIDDEN.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsModeManager
    {
        public enum AccessResult { Allowed, NeedsGrant, Forbidden }
        public enum ApprovalChannel { Dialog, Panel }

        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";

        /// <summary>Allowlist persistence key (user-managed).</summary>
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        /// <summary>Marks that the first-run migration has completed, to avoid re-running it.</summary>
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        /// <summary>The old GrantedSkills key (read only for the one-time migration; kept after migration to allow rollback).</summary>
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        // ResetForTests temporarily clears these machine-level preferences. SessionState survives domain reloads,
        // so the user's original settings can still be restored if a test run gets interrupted.
        private const string TestRecoveryActiveKey = "UnitySkills.Tests.PreferenceRecovery.Active";
        private const string TestRecoveryModeExistsKey = "UnitySkills.Tests.PreferenceRecovery.Mode.Exists";
        private const string TestRecoveryModeValueKey = "UnitySkills.Tests.PreferenceRecovery.Mode.Value";
        private const string TestRecoveryPanelApprovalExistsKey = "UnitySkills.Tests.PreferenceRecovery.PanelApproval.Exists";
        private const string TestRecoveryPanelApprovalValueKey = "UnitySkills.Tests.PreferenceRecovery.PanelApproval.Value";
        private const string TestRecoveryAllowlistExistsKey = "UnitySkills.Tests.PreferenceRecovery.Allowlist.Exists";
        private const string TestRecoveryAllowlistValueKey = "UnitySkills.Tests.PreferenceRecovery.Allowlist.Value";
        private const string TestRecoveryMigrationExistsKey = "UnitySkills.Tests.PreferenceRecovery.Migration.Exists";
        private const string TestRecoveryMigrationValueKey = "UnitySkills.Tests.PreferenceRecovery.Migration.Value";
        private const string TestRecoveryLegacyGrantedExistsKey = "UnitySkills.Tests.PreferenceRecovery.LegacyGranted.Exists";
        private const string TestRecoveryLegacyGrantedValueKey = "UnitySkills.Tests.PreferenceRecovery.LegacyGranted.Value";

        private const int DefaultGrantTtlSeconds = 300;
        private const int MaxLiveGrants = 256;
        private const int MaxArgsSummaryChars = 120;

        // NeverInSemi classification is driven entirely by metadata flags (Operation=Delete / MayEnterPlayMode /
        // MayTriggerReload / RiskLevel=high), checked in IsForbiddenInSemi — there is no hardcoded list.
        // If a future high-risk skill needs an exception outside of metadata, prefer annotating the skill itself
        // (RiskLevel="high" or an explicit operation flag); do not reintroduce a list.

        private sealed class GrantEntry
        {
            public string Token;
            public string SkillName;
            public string ArgsHash;
            public string ArgsSummary;
            /// <summary>The full original args text, replayed to SkillRouter by the HTTP endpoint during Plan B single-step execution.</summary>
            public string ArgsJson;
            public DateTime IssuedAtUtc;
            public DateTime ExpiresAtUtc;
            public ApprovalChannel Channel;
            public bool ApprovedByPanel;
            /// <summary>Plan B double-consumption guard flag (not currently triggered; reserved for a future grant-path branch).</summary>
            public bool OneShotConsumed;
        }

        private static readonly ConcurrentDictionary<string, GrantEntry> _grants =
            new ConcurrentDictionary<string, GrantEntry>(StringComparer.Ordinal);

        private static readonly object _allowlistLock = new object();
        private static HashSet<string> _allowlist;
        internal static bool? ExistingInstallOverrideForTests;

        /// <summary>
        /// The "bypass token" for a one-shot grant. Set by <see cref="TryGrantAndReturnArgs"/>,
        /// consumed by <see cref="ConsumeOneShotBypass"/>. ThreadStatic ensures different request threads don't interfere.
        ///
        /// The setter **must** call <see cref="ClearOneShotBypass"/> in a finally block — the consumption point is not
        /// guaranteed to be reached; see that method's comment. <see cref="_oneShotDeadlineUtc"/> is the second safety net.
        /// </summary>
        [ThreadStatic] private static string _currentOneShotSkill;

        /// <summary>
        /// The moment the token expires. Only one SkillRouter.Execute parameter check (millisecond-scale) separates
        /// setting from consumption, so any token outliving <see cref="OneShotLifetime"/> is a leftover, discarded not honored.
        /// </summary>
        [ThreadStatic] private static DateTime _oneShotDeadlineUtc;

        private static readonly TimeSpan OneShotLifetime = TimeSpan.FromSeconds(30);

        public static event Action OnChanged;

        static SkillsModeManager()
        {
            RestorePreferencesAfterTestDomainReload();
        }

        // ===== Properties =====

        /// <summary>
        /// The current operating mode. The setter persists to EditorPrefs and raises <see cref="OnChanged"/>.
        /// With no explicit pref set, the getter applies the factory-default rule: an existing install (any other
        /// UnitySkills_* key present) → <see cref="SkillsOperatingMode.Bypass"/>; a fresh install → <see cref="SkillsOperatingMode.Auto"/>.
        /// Never defaults to Approval.
        /// </summary>
        public static SkillsOperatingMode CurrentMode
        {
            get
            {
                if (EditorPrefs.HasKey(PrefKeyMode))
                {
                    var raw = EditorPrefs.GetString(PrefKeyMode, string.Empty);
                    if (Enum.TryParse<SkillsOperatingMode>(raw, true, out var parsed))
                        return parsed;
                }
                return IsExistingInstall() ? SkillsOperatingMode.Bypass : SkillsOperatingMode.Auto;
            }
            set
            {
                EditorPrefs.SetString(PrefKeyMode, value.ToString());
                SkillsAuditLog.Append("mode_changed", new { mode = value.ToString().ToLowerInvariant() });
                RaiseChanged();
            }
        }

        /// <summary>
        /// When true (Approval mode only), an AI-initiated authorization request must first be approved on the
        /// Unity panel before <see cref="TryGrant"/> can succeed. Defaults to false, i.e. the Dialog channel
        /// (the AI calls grant directly after obtaining the user's consent in conversation).
        /// </summary>
        public static bool PanelApprovalRequired
        {
            get => EditorPrefs.GetBool(PrefKeyPanelApproval, false);
            set
            {
                EditorPrefs.SetBool(PrefKeyPanelApproval, value);
                RaiseChanged();
            }
        }

        /// <summary>
        /// A user-managed allowlist. Skills on this list pass <see cref="CheckAccess"/> regardless of the current
        /// mode and regardless of <see cref="IsForbiddenInSemi"/>. Replaces v1.9's persistent "GrantedSkills" list.
        /// </summary>
        public static IReadOnlyCollection<string> AllowlistSkills
        {
            get
            {
                EnsureAllowlistLoaded();
                lock (_allowlistLock)
                {
                    return _allowlist.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        public static IReadOnlyList<GrantRequest> PendingGrantRequests
        {
            get
            {
                CleanupExpired();
                return _grants.Values
                    .OrderBy(e => e.IssuedAtUtc)
                    .Select(ToPublic)
                    .ToList();
            }
        }

        // ===== Public API: Allowlist =====

        /// <summary>Returns true when <paramref name="skillName"/> is in the user's allowlist.</summary>
        public static bool IsInAllowlist(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            EnsureAllowlistLoaded();
            lock (_allowlistLock)
            {
                return _allowlist.Contains(skillName);
            }
        }

        /// <summary>
        /// Adds a skill to the user's allowlist. Returns true if newly added, false if it already existed.
        /// Logs the "allowlist_add" audit event on a successful add.
        /// </summary>
        public static bool AddToAllowlist(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            EnsureAllowlistLoaded();
            bool added;
            lock (_allowlistLock)
            {
                added = _allowlist.Add(skillName);
                if (added) SaveAllowlistUnlocked();
            }
            if (added)
            {
                SkillsAuditLog.Append("allowlist_add", new { skill = skillName, source = "panel" });
                RaiseChanged();
            }
            return added;
        }

        /// <summary>
        /// Removes a skill from the user's allowlist. Returns true if it previously existed, false otherwise.
        /// Logs the "allowlist_remove" audit event on success.
        /// </summary>
        public static bool RemoveFromAllowlist(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            EnsureAllowlistLoaded();
            bool removed;
            lock (_allowlistLock)
            {
                removed = _allowlist.Remove(skillName);
                if (removed) SaveAllowlistUnlocked();
            }
            if (removed)
            {
                SkillsAuditLog.Append("allowlist_remove", new { skill = skillName, source = "panel" });
                RaiseChanged();
            }
            return removed;
        }

        /// <summary>Clears the entire allowlist. Logs the "allowlist_clear" audit event only if it was non-empty.</summary>
        public static void ClearAllowlist()
        {
            EnsureAllowlistLoaded();
            int count;
            lock (_allowlistLock)
            {
                count = _allowlist.Count;
                _allowlist.Clear();
                if (count > 0) SaveAllowlistUnlocked();
            }
            if (count > 0)
            {
                SkillsAuditLog.Append("allowlist_clear", new { count, source = "panel" });
                RaiseChanged();
            }
        }

        // ===== Public API: Authorization Lifecycle =====

        /// <summary>
        /// Issues a new authorization request token, bound to (skillName, argsHash, channel, TTL).
        /// The AI later replays this token via <see cref="TryGrant"/>. On the Panel channel, this token also
        /// appears in <see cref="PendingGrantRequests"/> for the panel side to approve/deny.
        ///
        /// The full argsJson is also cached on the entry for Plan B single-step execution replay.
        /// </summary>
        public static (string token, int ttlSeconds, ApprovalChannel channel)
            IssueGrantRequest(string skillName, string argsJson)
        {
            CleanupExpired();
            EnforceCapacity();

            var channel = PanelApprovalRequired ? ApprovalChannel.Panel : ApprovalChannel.Dialog;
            var nowUtc = DateTime.UtcNow;
            var entry = new GrantEntry
            {
                Token = GenerateToken(),
                SkillName = skillName ?? string.Empty,
                ArgsHash = HashArgs(argsJson),
                ArgsSummary = SummarizeArgs(argsJson),
                ArgsJson = argsJson ?? string.Empty,
                IssuedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddSeconds(DefaultGrantTtlSeconds),
                Channel = channel,
                ApprovedByPanel = false,
                OneShotConsumed = false,
            };
            _grants[entry.Token] = entry;

            SkillsAuditLog.Append("mode_restricted_hit", new
            {
                skill = entry.SkillName,
                grantToken = entry.Token,
                channel = ChannelToWire(channel),
                argsSummary = entry.ArgsSummary,
            });
            RaiseChanged();
            return (entry.Token, DefaultGrantTtlSeconds, channel);
        }

        /// <summary>
        /// Consumes an authorization token. Returns true only when the outcome is fully Granted.
        /// HTTP handlers that need to distinguish PendingApproval from Invalid should use <see cref="TryGrantDetailed"/> instead.
        /// </summary>
        public static bool TryGrant(string skillName, string token, string argsJson)
            => TryGrantDetailed(skillName, token, argsJson) == GrantOutcome.Granted;

        /// <summary>
        /// Same as <see cref="TryGrant"/>, but returns a finer-grained result so the caller can map
        /// PendingApproval to GRANT_PENDING_APPROVAL and Invalid to INVALID_TOKEN.
        ///
        /// The Granted branch **no longer** calls AddGranted/AddToAllowlist; a grant only clears the current call,
        /// the permanent allowlist is managed manually by the user on the panel. The entry is consumed and removed on Granted.
        /// </summary>
        public static GrantOutcome TryGrantDetailed(string skillName, string token, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token)) return GrantOutcome.Invalid;
            if (!_grants.TryGetValue(token, out var entry)) return GrantOutcome.Invalid;

            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                _grants.TryRemove(token, out _);
                RaiseChanged();
                return GrantOutcome.Invalid;
            }
            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return GrantOutcome.Invalid;
            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return GrantOutcome.Invalid;

            if (entry.Channel == ApprovalChannel.Panel && !entry.ApprovedByPanel)
                return GrantOutcome.PendingApproval;

            // Granted — free the token slot and audit. One-shot semantics: no longer written to the permanent allowlist.
            _grants.TryRemove(token, out _);
            int tokenAgeSec = (int)Math.Max(0, (DateTime.UtcNow - entry.IssuedAtUtc).TotalSeconds);
            SkillsAuditLog.Append("grant", new
            {
                skill = entry.SkillName,
                token,
                channel = ChannelToWire(entry.Channel),
                tokenAgeSec,
            });
            RaiseChanged();
            return GrantOutcome.Granted;
        }

        /// <summary>
        /// Panel-side approve. **No longer** writes the skill permanently to the allowlist; instead it just sets
        /// <c>entry.ApprovedByPanel = true</c>, keeping the entry so the AI's subsequent <see cref="TryGrant"/>
        /// (or Plan B's <see cref="TryGrantAndReturnArgs"/>) can take the Granted branch and trigger one-shot execution.
        /// </summary>
        public static bool Approve(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!_grants.TryGetValue(token, out var entry)) return false;
            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                _grants.TryRemove(token, out _);
                RaiseChanged();
                return false;
            }
            // One-shot: only mark it, don't write the allowlist, and don't remove the entry — the entry is only removed after a subsequent TryGrant succeeds.
            entry.ApprovedByPanel = true;
            SkillsAuditLog.Append("approve", new { skill = entry.SkillName, token, source = "panel" });
            RaiseChanged();
            return true;
        }

        /// <summary>Panel-side deny: removes the pending entry and does not grant access.</summary>
        public static bool Deny(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!_grants.TryRemove(token, out var entry)) return false;
            SkillsAuditLog.Append("deny", new { skill = entry.SkillName, token, source = "panel" });
            RaiseChanged();
            return true;
        }

        // ===== Obsolete forwarders (kept for one version until HTTP/UI switch over) =====

        /// <summary>
        /// Obsolete: use <see cref="AllowlistSkills"/>. Kept as an HTTP/UI compatibility forwarder for the v1.9 → v1.9.x split transition period.
        /// </summary>
        [Obsolete("Use AllowlistSkills. v1.9 'Granted' was renamed to 'Allowlist' with new semantics.")]
        public static IReadOnlyCollection<string> GrantedSkills => AllowlistSkills;

        /// <summary>
        /// Obsolete: use <see cref="RemoveFromAllowlist"/>. Kept as an HTTP/UI compatibility forwarder for the v1.9 → v1.9.x split transition period.
        /// </summary>
        [Obsolete("Use RemoveFromAllowlist. v1.9 'Revoke' was renamed to clarify the new Allowlist semantics.")]
        public static void Revoke(string skillName) => RemoveFromAllowlist(skillName);

        /// <summary>
        /// Obsolete: use <see cref="ClearAllowlist"/>. Kept as an HTTP/UI compatibility forwarder for the v1.9 → v1.9.x split transition period.
        /// </summary>
        [Obsolete("Use ClearAllowlist. v1.9 'RevokeAll' was renamed to clarify the new Allowlist semantics.")]
        public static void RevokeAll() => ClearAllowlist();

        // ===== Internal (called by SkillRouter / SkillsHttpServer) =====

        /// <summary>
        /// Determines whether a given skill may execute under the current operating mode + allowlist state.
        /// The caller (SkillRouter) turns the result into an error response or proceeds with execution.
        ///
        /// Priority (checked in order):
        /// 1. Bypass mode → Allowed
        /// 2. One-shot bypass hit (grant Plan B re-entry) → Allowed
        /// 3. Allowlist hit → Allowed (**takes priority over** <see cref="IsForbiddenInSemi"/>,
        ///    implementing "user manually clears a high-risk block")
        /// 4. IsForbiddenInSemi hit → Forbidden
        /// 5. Auto mode → Allowed
        /// 6. Approval mode + SemiAuto → Allowed
        /// 7. Otherwise → NeedsGrant
        /// </summary>
        internal static AccessResult CheckAccess(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return AccessResult.Allowed;
            var mode = CurrentMode;

            if (mode == SkillsOperatingMode.Bypass)
                return AccessResult.Allowed;

            // 2. One-shot bypass must precede IsForbiddenInSemi — otherwise grant Plan B re-entry would be blocked by the forbidden list.
            if (ConsumeOneShotBypass(skill.Name))
                return AccessResult.Allowed;

            // 3. Allowlist must precede IsForbiddenInSemi — the user's allowlist has the highest priority.
            if (IsInAllowlist(skill.Name))
                return AccessResult.Allowed;

            if (IsForbiddenInSemi(skill))
                return AccessResult.Forbidden;

            if (mode == SkillsOperatingMode.Auto)
                return AccessResult.Allowed;
            if (skill.Mode == SkillMode.SemiAuto) return AccessResult.Allowed;

            return AccessResult.NeedsGrant;
        }

        /// <summary>
        /// Plan B single-step execution entry point (HTTP endpoint only): attempts to consume the grant token; on
        /// success it returns the cached original argsJson and sets the ThreadStatic one-shot bypass token, so the
        /// subsequent SkillRouter.Execute is hit by <see cref="ConsumeOneShotBypass"/> in <see cref="CheckAccess"/>
        /// and let through once. Entry consumed and removed (as in <see cref="TryGrantDetailed"/>'s Granted branch).
        /// </summary>
        /// <returns>
        /// When <c>outcome</c> = Granted: <c>skillName</c> is the entry's canonical name, <c>cachedArgsJson</c> is
        /// the original text cached at IssueGrantRequest time. For any other outcome both fields are null/empty.
        /// </returns>
        internal static (GrantOutcome outcome, string skillName, string cachedArgsJson)
            TryGrantAndReturnArgs(string skillName, string token, string argsJson)
        {
            if (string.IsNullOrWhiteSpace(token)) return (GrantOutcome.Invalid, null, null);
            if (!_grants.TryGetValue(token, out var entry)) return (GrantOutcome.Invalid, null, null);

            if (DateTime.UtcNow > entry.ExpiresAtUtc)
            {
                _grants.TryRemove(token, out _);
                RaiseChanged();
                return (GrantOutcome.Invalid, null, null);
            }
            if (!string.Equals(entry.SkillName, skillName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                return (GrantOutcome.Invalid, null, null);
            if (!string.Equals(entry.ArgsHash, HashArgs(argsJson), StringComparison.Ordinal))
                return (GrantOutcome.Invalid, null, null);

            if (entry.Channel == ApprovalChannel.Panel && !entry.ApprovedByPanel)
                return (GrantOutcome.PendingApproval, null, null);

            // Granted — consume the entry, set one-shot, audit. Semantically equivalent to TryGrantDetailed's Granted branch.
            _grants.TryRemove(token, out _);
            entry.OneShotConsumed = true;
            SetOneShotBypass(entry.SkillName);
            int tokenAgeSec = (int)Math.Max(0, (DateTime.UtcNow - entry.IssuedAtUtc).TotalSeconds);
            SkillsAuditLog.Append("grant", new
            {
                skill = entry.SkillName,
                token,
                channel = ChannelToWire(entry.Channel),
                tokenAgeSec,
                oneShot = true,
            });
            RaiseChanged();
            return (GrantOutcome.Granted, entry.SkillName, entry.ArgsJson);
        }

        /// <summary>
        /// Consumes the current thread's one-shot bypass token. On a hit (<c>_currentOneShotSkill</c> equals
        /// <paramref name="skillName"/>, case-insensitive, within its lifetime window), clears it and returns true;
        /// otherwise returns false. An expired token is discarded outright with a warning — it can only come from
        /// a path that missed <see cref="ClearOneShotBypass"/>, and honoring it would silently bypass the Approval gate.
        /// </summary>
        internal static bool ConsumeOneShotBypass(string skillName)
        {
            var current = _currentOneShotSkill;
            if (string.IsNullOrEmpty(current)) return false;

            if (DateTime.UtcNow > _oneShotDeadlineUtc)
            {
                ClearOneShotBypass();
                SkillsLogger.LogWarning(
                    $"Discarded a stale one-shot grant token for '{current}' (not consumed). " +
                    "Some grant path failed to clear it; the current request is re-checked against the operating mode.");
                return false;
            }

            if (string.IsNullOrEmpty(skillName)) return false;
            if (!string.Equals(current, skillName, StringComparison.OrdinalIgnoreCase)) return false;
            ClearOneShotBypass();
            return true;
        }

        private static void SetOneShotBypass(string skillName)
        {
            _currentOneShotSkill = skillName;
            _oneShotDeadlineUtc = DateTime.UtcNow + OneShotLifetime;
        }

        /// <summary>
        /// Unconditionally clears the current thread's one-shot bypass token. **The setter must call this in a
        /// finally block**: the consumption point <see cref="CheckAccess"/> sits after SkillRouter.Execute's four
        /// parameter checks (UnknownParam / MissingParam / TypeMismatch / SemanticInvalid); an early return from any
        /// of them skips it. The token is ThreadStatic, and grant/ordinary requests share the Unity main thread, so
        /// a leftover token lets the next same-named skill request — with different arguments — pass silently
        /// (audit only records grantSource="auto", untraceable).
        ///
        /// A stronger binding would upgrade the token to (skillName, argsHash) and compare it against this
        /// request's args at the consumption point; but the consumption point only has SkillInfo — args would need
        /// a change to SkillRouter.ApplyModeGate → CheckAccess's call signature (out of scope here). For now, "the
        /// setter unconditionally clears + the <see cref="OneShotLifetime"/> lifetime window" seals the leak.
        /// </summary>
        public static void ClearOneShotBypass()
        {
            _currentOneShotSkill = null;
            _oneShotDeadlineUtc = default;
        }

        /// <summary>
        /// Returns true when this skill must be blocked in any non-Bypass mode. The decision is driven purely by metadata.
        ///
        /// The _explicitNeverList fallback was removed (no longer hit) — metadata now fully covers the current 75
        /// NeverInSemi skills (all triggered by the 4 rules below, 0 relying on a list fallback).
        ///
        /// Note: <see cref="CheckAccess"/> **skips this check** on an IsInAllowlist hit, letting the user manually
        /// clear an otherwise blocked high-risk skill.
        /// </summary>
        internal static bool IsForbiddenInSemi(SkillRouter.SkillInfo s)
        {
            if (s == null) return false;
            return s.Operation.HasFlag(SkillOperation.Delete)
                || s.MayEnterPlayMode
                || s.MayTriggerReload
                || string.Equals(s.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The wire string for the operating mode ("approval"|"auto"|"bypass").</summary>
        internal static string ModeToWire(SkillsOperatingMode mode) => mode.ToString().ToLowerInvariant();

        /// <summary>The wire string for the approval channel ("dialog"|"panel").</summary>
        internal static string ChannelToWire(ApprovalChannel channel) => channel.ToString().ToLowerInvariant();

        /// <summary>The wire string for SkillMode ("semi"|"full"), used by the /skills listing.</summary>
        internal static string SkillModeToWire(SkillMode mode) =>
            mode == SkillMode.SemiAuto ? "semi" : "full";

        /// <summary>
        /// The wire string for a skill's default behavior under <see cref="SkillsOperatingMode.Approval"/> mode,
        /// ignoring the user allowlist and one-shot bypass state. Used by the /skills listing so callers can
        /// determine the authorization requirement without re-deriving the rules from <c>mode</c>.
        ///
        /// Mapping (consistent with the Approval branch of <see cref="CheckAccess"/>):
        /// <list type="bullet">
        /// <item><c>"forbid"</c> — <see cref="IsForbiddenInSemi"/> is true; callable only in Bypass mode (or via an allowlist override).</item>
        /// <item><c>"grant"</c> — a FullAuto skill that isn't blocked; requires <c>/permission/grant</c> before execution.</item>
        /// <item><c>"allow"</c> — a SemiAuto skill that isn't blocked; executes directly in Approval mode.</item>
        /// </list>
        /// </summary>
        internal static string ApprovalBehaviorForSkill(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return "allow";
            if (IsForbiddenInSemi(skill)) return "forbid";
            return skill.Mode == SkillMode.SemiAuto ? "allow" : "grant";
        }

        /// <summary>Test-only: resets all state (allowlist, pending items, prefs, migration marker) to a clean initial state.</summary>
        internal static void ResetForTests()
        {
            CapturePreferencesForTestRecovery();
            _grants.Clear();
            lock (_allowlistLock)
            {
                _allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SaveAllowlistUnlocked();
            }
            ClearOneShotBypass();
            EditorPrefs.DeleteKey(PrefKeyMode);
            EditorPrefs.DeleteKey(PrefKeyPanelApproval);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyLegacyGranted);
            RaiseChanged();
        }

        /// <summary>Test-only: clears the recovery data after the fixture has restored the original preferences.</summary>
        internal static void CompleteTestPreferenceRecovery()
        {
            ClearTestPreferenceRecovery();
        }

        /// <summary>Test-only: simulates the recovery process the static constructor runs after a domain reload.</summary>
        internal static void RestorePreferencesAfterTestDomainReload()
        {
            if (!SessionState.GetBool(TestRecoveryActiveKey, false)) return;

            RestoreStringPreference(PrefKeyMode, TestRecoveryModeExistsKey, TestRecoveryModeValueKey);
            RestoreBoolPreference(PrefKeyPanelApproval, TestRecoveryPanelApprovalExistsKey,
                TestRecoveryPanelApprovalValueKey);
            RestoreStringPreference(PrefKeyAllowlist, TestRecoveryAllowlistExistsKey,
                TestRecoveryAllowlistValueKey);
            RestoreBoolPreference(PrefKeyMigrationDone, TestRecoveryMigrationExistsKey,
                TestRecoveryMigrationValueKey);
            RestoreStringPreference(PrefKeyLegacyGranted, TestRecoveryLegacyGrantedExistsKey,
                TestRecoveryLegacyGrantedValueKey);
            ClearTestPreferenceRecovery();
        }

        /// <summary>Looks up a pending authorization entry by token (internal use — SkillRouter uses this to expose argsSummary).</summary>
        internal static GrantRequest PeekPending(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            return _grants.TryGetValue(token, out var entry) ? ToPublic(entry) : null;
        }

        /// <summary>
        /// Returns the entry's cached original argsJson for this token, so the Plan B single-step execution endpoint
        /// can backfill it when the client doesn't pass args. Returns null if the token is missing/expired. Does not consume the entry.
        /// </summary>
        internal static string TryPeekArgsJson(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (!_grants.TryGetValue(token, out var entry)) return null;
            if (DateTime.UtcNow > entry.ExpiresAtUtc) return null;
            return entry.ArgsJson;
        }

        /// <summary>Test-only: peeks at a pending entry by token.</summary>
        internal static GrantRequest PeekPendingForTests(string token) => PeekPending(token);

        // ===== Helper methods =====

        private static GrantRequest ToPublic(GrantEntry e) => new GrantRequest
        {
            Token = e.Token,
            SkillName = e.SkillName,
            ArgsSummary = e.ArgsSummary,
            ExpiresAtUtc = e.ExpiresAtUtc,
            ApprovedByPanel = e.ApprovedByPanel,
            Channel = ChannelToWire(e.Channel),
        };

        private static void RaiseChanged()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception ex) { SkillsLogger.LogWarning($"ModeManager OnChanged handler threw: {ex.Message}"); }
        }

        private static void EnsureAllowlistLoaded()
        {
            if (_allowlist != null) return;
            lock (_allowlistLock)
            {
                if (_allowlist != null) return;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var raw = EditorPrefs.GetString(PrefKeyAllowlist, string.Empty);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        var arr = JArray.Parse(raw);
                        foreach (var t in arr)
                        {
                            var s = t?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
                        }
                    }
                    catch
                    {
                        // Malformed JSON is treated as empty — a corrupted pref must never take down the editor.
                    }
                }
                _allowlist = set;
                // Attempt migration immediately after first initialization; idempotency is enforced via the PrefKeyMigrationDone marker.
                MigrateLegacyGrantedToAllowlist();
            }
        }

        /// <summary>
        /// One-time migration of old <c>UnitySkills_GrantedSkills</c> data to the new
        /// <c>UnitySkills_AllowlistSkills</c>. Idempotency is guaranteed via <see cref="PrefKeyMigrationDone"/>.
        /// The old key is intentionally not deleted, kept as a rollback marker.
        ///
        /// Must be called while holding <see cref="_allowlistLock"/> (guaranteed by <see cref="EnsureAllowlistLoaded"/>).
        /// </summary>
        private static void MigrateLegacyGrantedToAllowlist()
        {
            if (EditorPrefs.GetBool(PrefKeyMigrationDone, false)) return;

            int migrated = 0;
            var legacy = EditorPrefs.GetString(PrefKeyLegacyGranted, string.Empty);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                try
                {
                    var arr = JArray.Parse(legacy);
                    foreach (var t in arr)
                    {
                        var s = t?.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && _allowlist.Add(s))
                            migrated++;
                    }
                }
                catch
                {
                    // Corrupt legacy data should not block the migration; just mark it complete, equivalent to "nothing to migrate".
                }
            }
            if (migrated > 0) SaveAllowlistUnlocked();
            EditorPrefs.SetBool(PrefKeyMigrationDone, true);
            SkillsAuditLog.Append("allowlist_migrated", new { count = migrated, source = "v1.9_granted" });
        }

        private static void SaveAllowlistUnlocked()
        {
            var arr = new JArray();
            foreach (var s in _allowlist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                arr.Add(s);
            EditorPrefs.SetString(PrefKeyAllowlist, arr.ToString(Formatting.None));
        }

        private static void CleanupExpired()
        {
            var nowUtc = DateTime.UtcNow;
            bool any = false;
            foreach (var kv in _grants)
            {
                if (nowUtc > kv.Value.ExpiresAtUtc && _grants.TryRemove(kv.Key, out _))
                    any = true;
            }
            if (any) RaiseChanged();
        }

        private static void EnforceCapacity()
        {
            if (_grants.Count < MaxLiveGrants) return;
            foreach (var key in _grants.Keys)
            {
                if (_grants.Count < MaxLiveGrants) break;
                _grants.TryRemove(key, out _);
            }
        }

        private static string GenerateToken()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string HashArgs(string argsJson)
        {
            var normalized = (argsJson ?? string.Empty).Trim();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void CapturePreferencesForTestRecovery()
        {
            if (SessionState.GetBool(TestRecoveryActiveKey, false)) return;

            StoreStringPreference(PrefKeyMode, TestRecoveryModeExistsKey, TestRecoveryModeValueKey);
            StoreBoolPreference(PrefKeyPanelApproval, TestRecoveryPanelApprovalExistsKey,
                TestRecoveryPanelApprovalValueKey);
            StoreStringPreference(PrefKeyAllowlist, TestRecoveryAllowlistExistsKey,
                TestRecoveryAllowlistValueKey);
            StoreBoolPreference(PrefKeyMigrationDone, TestRecoveryMigrationExistsKey,
                TestRecoveryMigrationValueKey);
            StoreStringPreference(PrefKeyLegacyGranted, TestRecoveryLegacyGrantedExistsKey,
                TestRecoveryLegacyGrantedValueKey);
            SessionState.SetBool(TestRecoveryActiveKey, true);
        }

        private static void StoreStringPreference(string preferenceKey, string existsKey, string valueKey)
        {
            var exists = EditorPrefs.HasKey(preferenceKey);
            SessionState.SetBool(existsKey, exists);
            SessionState.SetString(valueKey, exists ? EditorPrefs.GetString(preferenceKey) : string.Empty);
        }

        private static void StoreBoolPreference(string preferenceKey, string existsKey, string valueKey)
        {
            var exists = EditorPrefs.HasKey(preferenceKey);
            SessionState.SetBool(existsKey, exists);
            SessionState.SetBool(valueKey, exists && EditorPrefs.GetBool(preferenceKey));
        }

        private static void RestoreStringPreference(string preferenceKey, string existsKey, string valueKey)
        {
            if (SessionState.GetBool(existsKey, false))
                EditorPrefs.SetString(preferenceKey, SessionState.GetString(valueKey, string.Empty));
            else
                EditorPrefs.DeleteKey(preferenceKey);
        }

        private static void RestoreBoolPreference(string preferenceKey, string existsKey, string valueKey)
        {
            if (SessionState.GetBool(existsKey, false))
                EditorPrefs.SetBool(preferenceKey, SessionState.GetBool(valueKey, false));
            else
                EditorPrefs.DeleteKey(preferenceKey);
        }

        private static void ClearTestPreferenceRecovery()
        {
            SessionState.EraseBool(TestRecoveryActiveKey);
            SessionState.EraseBool(TestRecoveryModeExistsKey);
            SessionState.EraseString(TestRecoveryModeValueKey);
            SessionState.EraseBool(TestRecoveryPanelApprovalExistsKey);
            SessionState.EraseBool(TestRecoveryPanelApprovalValueKey);
            SessionState.EraseBool(TestRecoveryAllowlistExistsKey);
            SessionState.EraseString(TestRecoveryAllowlistValueKey);
            SessionState.EraseBool(TestRecoveryMigrationExistsKey);
            SessionState.EraseBool(TestRecoveryMigrationValueKey);
            SessionState.EraseBool(TestRecoveryLegacyGrantedExistsKey);
            SessionState.EraseString(TestRecoveryLegacyGrantedValueKey);
        }

        /// <summary>
        /// Generates a short, readable args summary for the panel and audit log.
        /// Keeps top-level scalar key=value pairs; nested objects are always replaced with "{...}".
        /// </summary>
        private static string SummarizeArgs(string argsJson)
        {
            if (string.IsNullOrWhiteSpace(argsJson)) return string.Empty;
            try
            {
                var obj = JObject.Parse(argsJson);
                var parts = new List<string>();
                foreach (var prop in obj.Properties())
                {
                    string val;
                    switch (prop.Value.Type)
                    {
                        case JTokenType.Object: val = "{...}"; break;
                        case JTokenType.Array:  val = $"[{((JArray)prop.Value).Count}]"; break;
                        case JTokenType.String: val = prop.Value.ToString(); break;
                        default: val = prop.Value.ToString(Formatting.None); break;
                    }
                    if (val.Length > 32) val = val.Substring(0, 29) + "...";
                    parts.Add($"{prop.Name}={val}");
                    if (parts.Count >= 6) break;
                }
                var joined = string.Join(", ", parts);
                if (joined.Length > MaxArgsSummaryChars)
                    joined = joined.Substring(0, MaxArgsSummaryChars - 3) + "...";
                return joined;
            }
            catch
            {
                var s = argsJson.Trim();
                return s.Length > MaxArgsSummaryChars ? s.Substring(0, MaxArgsSummaryChars - 3) + "..." : s;
            }
        }

        /// <summary>
        /// The criterion for a pre-v1.9 install. If any of these global UnitySkills_* prefs exist, the user was
        /// already using this package before the mode system existed → default to Bypass so the upgrade is behavior-neutral.
        /// </summary>
        private static bool IsExistingInstall()
        {
            if (ExistingInstallOverrideForTests.HasValue)
                return ExistingInstallOverrideForTests.Value;
            return EditorPrefs.HasKey("UnitySkills_RequireConfirmation")
                || EditorPrefs.HasKey("UnitySkills_PreferredPort")
                || EditorPrefs.HasKey("UnitySkills_LogLevel")
                || EditorPrefs.HasKey("UnitySkills_Language")
                || EditorPrefs.HasKey("UnitySkills_RequestTimeoutMinutes")
                || EditorPrefs.HasKey("UnitySkills_KeepAliveIntervalSeconds")
                || EditorPrefs.HasKey("UnitySkills_AutoInstallPackagesOnStartup");
        }
    }
}

// Producer:Betsy
