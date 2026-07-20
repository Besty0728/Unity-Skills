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
    /// Outcome of <see cref="SkillsModeManager.TryGrantDetailed"/>.
    /// Lets HTTP handlers distinguish "pending panel approval" (a normal Panel-channel state)
    /// from "invalid/expired token" (an error).
    /// </summary>
    public enum GrantOutcome
    {
        Granted,
        PendingApproval,
        Invalid,
    }

    /// <summary>
    /// Public, UI-visible view of an outstanding grant request.
    /// Returned by <see cref="SkillsModeManager.PendingGrantRequests"/>; the UI panel renders
    /// these as cards with Approve/Deny buttons.
    /// </summary>
    public sealed class GrantRequest
    {
        public string Token;
        public string SkillName;
        public string ArgsSummary;
        public DateTime ExpiresAtUtc;
        /// <summary>True after the user clicks Approve on the panel (Panel channel only).</summary>
        public bool ApprovedByPanel;
        /// <summary>"dialog" or "panel" — wire string for REST responses.</summary>
        public string Channel;
    }

    /// <summary>
    /// Core of the v1.9 Skill mode permission system. Three-tier operating modes
    /// (Approval / Auto / Bypass) + two-channel approval (Dialog / Panel) +
    /// **Allowlist (user-managed permanent whitelist, can override IsForbiddenInSemi)** +
    /// **single-use Approval** (grant/approve only releases the current call).
    ///
    /// v1.9 semantic split (vs the original v1.9 Approval design):
    /// - **Allowlist 通道**：用户在面板手动管理；命中直接放行，**优先级高于 IsForbiddenInSemi**，
    ///   允许用户手动放行原本的高危拦截 skill。
    /// - **Approval 单次有效**：grant/approve 仅放行本次调用，不再永久写入白名单。
    ///   Granted 分支通过 ThreadStatic 的 <c>_currentOneShotSkill</c> 让随后的 CheckAccess
    ///   一次性命中放行，然后立即被消费清空。
    /// - **Grant 方案 B（一步执行）**：<see cref="TryGrantAndReturnArgs"/> 在 Granted 时
    ///   同时返回缓存的原 argsJson 并标记 one-shot，HTTP 端点据此直接调 SkillRouter.Execute。
    /// - **EditorPrefs 迁移**：老 key <c>UnitySkills_GrantedSkills</c> 首次启动自动迁移到
    ///   新 key <c>UnitySkills_AllowlistSkills</c>，迁移幂等。
    ///
    /// State storage:
    /// - <c>CurrentMode</c> / <c>PanelApprovalRequired</c> / <c>AllowlistSkills</c>: EditorPrefs (per-machine)
    /// - Pending grant tokens: in-memory only (TTL 5 min, max 256 live)
    /// - One-shot bypass marker: ThreadStatic in-memory only
    ///
    /// Upgrade compatibility: an install that already has any pre-v1.9 <c>UnitySkills_*</c>
    /// pref (e.g. <c>UnitySkills_PreferredPort</c>) defaults to <see cref="SkillsOperatingMode.Bypass"/>
    /// so existing users see zero behavior change; fresh installs default to
    /// <see cref="SkillsOperatingMode.Auto"/> — every skill that is NOT auto-classified
    /// NeverInSemi (incl. FullAuto write skills) executes directly; only NeverInSemi
    /// (Delete / MayEnterPlayMode / MayTriggerReload / RiskLevel=high) returns MODE_FORBIDDEN.
    /// </summary>
    public static class SkillsModeManager
    {
        public enum AccessResult { Allowed, NeedsGrant, Forbidden }
        public enum ApprovalChannel { Dialog, Panel }

        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";

        /// <summary>v1.9 改版后的 Allowlist 持久化 key（用户手动管理）。</summary>
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        /// <summary>首次迁移完成标记，避免重复执行。</summary>
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        /// <summary>v1.9 旧 GrantedSkills key（仅用于一次性迁移读取，迁移后不删除以便回滚）。</summary>
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        private const int DefaultGrantTtlSeconds = 300;
        private const int MaxLiveGrants = 256;
        private const int MaxArgsSummaryChars = 120;

        // v1.9.x: the historical `_explicitNeverList` fallback (scene_clear / scene_new / batch_apply)
        // has been removed — none of those skill names exist in the current 750-skill surface, and the
        // 75 NeverInSemi skills are now fully covered by metadata flags (Operation=Delete /
        // MayEnterPlayMode / MayTriggerReload / RiskLevel=high) checked in IsForbiddenInSemi.
        // If a future high-risk skill ever needs a non-metadata override, prefer annotating the
        // skill itself (RiskLevel="high" or an explicit operation flag) over re-introducing a list.

        private sealed class GrantEntry
        {
            public string Token;
            public string SkillName;
            public string ArgsHash;
            public string ArgsSummary;
            /// <summary>原 args 完整原文，方案 B 一步执行时由 HTTP 端点回放给 SkillRouter。</summary>
            public string ArgsJson;
            public DateTime IssuedAtUtc;
            public DateTime ExpiresAtUtc;
            public ApprovalChannel Channel;
            public bool ApprovedByPanel;
            /// <summary>方案 B 防双消费标记（当前未触发；预留给未来 grant 路径分叉）。</summary>
            public bool OneShotConsumed;
        }

        private static readonly ConcurrentDictionary<string, GrantEntry> _grants =
            new ConcurrentDictionary<string, GrantEntry>(StringComparer.Ordinal);

        private static readonly object _allowlistLock = new object();
        private static HashSet<string> _allowlist;
        internal static bool? ExistingInstallOverrideForTests;

        /// <summary>
        /// 单次有效 grant 的"放行令牌"。由 <see cref="TryGrantAndReturnArgs"/> 设置，
        /// 由 <see cref="ConsumeOneShotBypass"/> 消费。ThreadStatic 保证不同请求线程互不干扰。
        /// </summary>
        [ThreadStatic] private static string _currentOneShotSkill;

        public static event Action OnChanged;

        // ===== Properties =====

        /// <summary>
        /// Current operating mode. Setter persists to EditorPrefs and raises <see cref="OnChanged"/>.
        /// Getter applies the factory-default rule when no explicit pref is set: an existing
        /// install (any other UnitySkills_* key present) → <see cref="SkillsOperatingMode.Bypass"/>;
        /// a fresh install → <see cref="SkillsOperatingMode.Auto"/>. It never defaults to Approval.
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
        /// When true (Approval mode only), AI-issued grant requests must be approved on
        /// the Unity panel before <see cref="TryGrant"/> succeeds. Default false → Dialog
        /// channel (AI obtains user consent over chat and calls grant directly).
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
        /// User-managed allowlist. Skills here always pass <see cref="CheckAccess"/> regardless
        /// of mode or <see cref="IsForbiddenInSemi"/>. Replaces the v1.9 "GrantedSkills"
        /// permanent grant list.
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

        /// <summary>True if <paramref name="skillName"/> is in the user-managed allowlist.</summary>
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
        /// Add a skill to the user-managed allowlist. Returns true if it was newly added,
        /// false if it was already present. Audits as "allowlist_add" on add.
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
        /// Remove a skill from the user-managed allowlist. Returns true if it was present,
        /// false otherwise. Audits as "allowlist_remove" on success.
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

        /// <summary>Clear the entire allowlist. Audits as "allowlist_clear" (only when non-empty).</summary>
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

        // ===== Public API: Grant lifecycle =====

        /// <summary>
        /// Issue a fresh grant request token bound to (skillName, argsHash, channel, TTL).
        /// AI re-plays the token via <see cref="TryGrant"/>. For Panel channel the token is
        /// also visible in <see cref="PendingGrantRequests"/> for panel-side Approve/Deny.
        ///
        /// v1.9 改版后：完整 argsJson 也缓存到 entry 中，供方案 B 一步执行回放。
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
        /// Consume a grant token. Returns true only on full Granted outcome.
        /// HTTP handlers wanting to distinguish PendingApproval vs Invalid should use
        /// <see cref="TryGrantDetailed"/>.
        /// </summary>
        public static bool TryGrant(string skillName, string token, string argsJson)
            => TryGrantDetailed(skillName, token, argsJson) == GrantOutcome.Granted;

        /// <summary>
        /// Like <see cref="TryGrant"/> but returns a detailed outcome so callers can map
        /// PendingApproval to GRANT_PENDING_APPROVAL and Invalid to INVALID_TOKEN.
        ///
        /// v1.9 改版后：Granted 分支**不再** AddGranted/AddToAllowlist；grant 只对本次有效，
        /// 永久白名单由用户在面板手动管理。entry 在 Granted 时被消费移除。
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

            // Granted — free the token slot and audit. 单次有效语义：不再写入永久白名单。
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
        /// Panel-side approve. v1.9 改版后语义：**不再** 将 skill 永久写入白名单，而是只把
        /// <c>entry.ApprovedByPanel = true</c>，保留 entry 让 AI 后续 <see cref="TryGrant"/>
        /// （或方案 B 的 <see cref="TryGrantAndReturnArgs"/>）走 Granted 分支并触发一次性执行。
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
            // 单次有效：仅标记，不写白名单，也不删除 entry——entry 在后续 TryGrant 成功后才移除。
            entry.ApprovedByPanel = true;
            SkillsAuditLog.Append("approve", new { skill = entry.SkillName, token, source = "panel" });
            RaiseChanged();
            return true;
        }

        /// <summary>Panel-side deny. Removes the pending entry without granting.</summary>
        public static bool Deny(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (!_grants.TryRemove(token, out var entry)) return false;
            SkillsAuditLog.Append("deny", new { skill = entry.SkillName, token, source = "panel" });
            RaiseChanged();
            return true;
        }

        // ===== Obsolete forwarders（保留一个版本，等 HTTP/UI 同步切换） =====

        /// <summary>
        /// Obsolete: use <see cref="AllowlistSkills"/>. Forwarder retained for HTTP/UI
        /// compatibility during the v1.9 → v1.9.x split rollout.
        /// </summary>
        [Obsolete("Use AllowlistSkills. v1.9 'Granted' was renamed to 'Allowlist' with new semantics.")]
        public static IReadOnlyCollection<string> GrantedSkills => AllowlistSkills;

        /// <summary>
        /// Obsolete: use <see cref="RemoveFromAllowlist"/>. Forwarder retained for HTTP/UI
        /// compatibility during the v1.9 → v1.9.x split rollout.
        /// </summary>
        [Obsolete("Use RemoveFromAllowlist. v1.9 'Revoke' was renamed to clarify the new Allowlist semantics.")]
        public static void Revoke(string skillName) => RemoveFromAllowlist(skillName);

        /// <summary>
        /// Obsolete: use <see cref="ClearAllowlist"/>. Forwarder retained for HTTP/UI
        /// compatibility during the v1.9 → v1.9.x split rollout.
        /// </summary>
        [Obsolete("Use ClearAllowlist. v1.9 'RevokeAll' was renamed to clarify the new Allowlist semantics.")]
        public static void RevokeAll() => ClearAllowlist();

        // ===== Internal (called from SkillRouter / SkillsHttpServer) =====

        /// <summary>
        /// Decide whether a skill may execute under the current operating mode + allowlist state.
        /// Caller (SkillRouter) translates the result into an error response or continues.
        ///
        /// v1.9 改版后优先级（依次判断）：
        /// 1. Bypass 模式 → Allowed
        /// 2. one-shot bypass 命中（grant 方案 B 重入）→ Allowed
        /// 3. Allowlist 命中 → Allowed（**优先于** <see cref="IsForbiddenInSemi"/>，
        ///    实现"用户手动放行高危拦截"）
        /// 4. IsForbiddenInSemi → Forbidden
        /// 5. Auto 模式 → Allowed
        /// 6. Approval 模式 + SemiAuto → Allowed
        /// 7. 其它 → NeedsGrant
        /// </summary>
        internal static AccessResult CheckAccess(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return AccessResult.Allowed;
            var mode = CurrentMode;

            if (mode == SkillsOperatingMode.Bypass)
                return AccessResult.Allowed;

            // 2. one-shot 必须先于 IsForbiddenInSemi —— 否则 grant 方案 B 重入会被禁列表拦截。
            if (ConsumeOneShotBypass(skill.Name))
                return AccessResult.Allowed;

            // 3. Allowlist 必须先于 IsForbiddenInSemi —— 用户白名单优先级最高。
            if (IsInAllowlist(skill.Name))
                return AccessResult.Allowed;

            if (IsForbiddenInSemi(skill))
                return AccessResult.Forbidden;

            if (mode == SkillsOperatingMode.Auto)
                return AccessResult.Allowed;

            // Approval
            if (skill.Mode == SkillMode.SemiAuto) return AccessResult.Allowed;

            return AccessResult.NeedsGrant;
        }

        /// <summary>
        /// 方案 B 一步执行入口（HTTP 端点专用）：尝试消费 grant token；成功时返回缓存的
        /// 原 argsJson 并设置 ThreadStatic one-shot 放行令牌，让随后的 SkillRouter.Execute
        /// 在同一线程内通过 <see cref="CheckAccess"/> 时被 <see cref="ConsumeOneShotBypass"/>
        /// 命中、单次放行。entry 被消费移除（与 <see cref="TryGrantDetailed"/> Granted 分支一致）。
        /// </summary>
        /// <returns>
        /// <c>outcome</c> = Granted 时：<c>skillName</c> 为 entry 中的规范名、<c>cachedArgsJson</c>
        /// 为 IssueGrantRequest 时缓存的原文。其它 outcome 时这两个字段为 null/empty。
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

            // Granted — 消费 entry、设置 one-shot、审计。语义上等价于 TryGrantDetailed Granted 分支。
            _grants.TryRemove(token, out _);
            entry.OneShotConsumed = true;
            _currentOneShotSkill = entry.SkillName;
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
        /// 消费当前线程的 one-shot 放行令牌。命中（即 <c>_currentOneShotSkill</c> 等于
        /// <paramref name="skillName"/>，忽略大小写）则清空并返回 true；否则返回 false。
        /// </summary>
        internal static bool ConsumeOneShotBypass(string skillName)
        {
            var current = _currentOneShotSkill;
            if (string.IsNullOrEmpty(current)) return false;
            if (string.IsNullOrEmpty(skillName)) return false;
            if (!string.Equals(current, skillName, StringComparison.OrdinalIgnoreCase)) return false;
            _currentOneShotSkill = null;
            return true;
        }

        /// <summary>
        /// True if the skill must be blocked outside Bypass mode. Implementation matches
        /// plan section 8 — purely metadata-driven judgement.
        ///
        /// v1.9.x: 移除 _explicitNeverList 兜底（已无命中）— metadata 已完全覆盖当前 75 个
        /// NeverInSemi skill（全部由下面 4 条规则触发，0 个依赖名单兜底）。
        ///
        /// 注意：v1.9 改版后，<see cref="CheckAccess"/> 在 IsInAllowlist 命中时**会跳过本判定**，
        /// 让用户能手动放行原本被拦截的高危 skill。
        /// </summary>
        internal static bool IsForbiddenInSemi(SkillRouter.SkillInfo s)
        {
            if (s == null) return false;
            return s.Operation.HasFlag(SkillOperation.Delete)
                || s.MayEnterPlayMode
                || s.MayTriggerReload
                || string.Equals(s.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Wire string for the operating mode ("approval"|"auto"|"bypass").</summary>
        internal static string ModeToWire(SkillsOperatingMode mode) => mode.ToString().ToLowerInvariant();

        /// <summary>Wire string for the approval channel ("dialog"|"panel").</summary>
        internal static string ChannelToWire(ApprovalChannel channel) => channel.ToString().ToLowerInvariant();

        /// <summary>Wire string for a SkillMode ("semi"|"full"). Used by /skills manifest.</summary>
        internal static string SkillModeToWire(SkillMode mode) =>
            mode == SkillMode.SemiAuto ? "semi" : "full";

        /// <summary>
        /// Wire string for the skill's default behavior in <see cref="SkillsOperatingMode.Approval"/> mode,
        /// ignoring per-user allowlist / one-shot bypass state. Used by /skills manifest so callers can
        /// reason about authorization requirements without re-deriving the rules from <c>mode</c>.
        ///
        /// Mapping (mirrors <see cref="CheckAccess"/> Approval branch):
        /// <list type="bullet">
        /// <item><c>"forbid"</c> — <see cref="IsForbiddenInSemi"/> is true; only callable in Bypass mode (or via Allowlist override).</item>
        /// <item><c>"grant"</c> — FullAuto skill, not forbidden; needs <c>/permission/grant</c> before execution.</item>
        /// <item><c>"allow"</c> — SemiAuto skill, not forbidden; runs directly in Approval mode.</item>
        /// </list>
        /// </summary>
        internal static string ApprovalBehaviorForSkill(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return "allow";
            if (IsForbiddenInSemi(skill)) return "forbid";
            return skill.Mode == SkillMode.SemiAuto ? "allow" : "grant";
        }

        /// <summary>Test-only: clear all state (allowlist, pending, prefs, migration flag) to a clean slate.</summary>
        internal static void ResetForTests()
        {
            _grants.Clear();
            lock (_allowlistLock)
            {
                _allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SaveAllowlistUnlocked();
            }
            _currentOneShotSkill = null;
            EditorPrefs.DeleteKey(PrefKeyMode);
            EditorPrefs.DeleteKey(PrefKeyPanelApproval);
            EditorPrefs.DeleteKey(PrefKeyAllowlist);
            EditorPrefs.DeleteKey(PrefKeyMigrationDone);
            EditorPrefs.DeleteKey(PrefKeyLegacyGranted);
            RaiseChanged();
        }

        /// <summary>Look up a pending grant entry by token (internal — used by SkillRouter to surface argsSummary).</summary>
        internal static GrantRequest PeekPending(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            return _grants.TryGetValue(token, out var entry) ? ToPublic(entry) : null;
        }

        /// <summary>
        /// 返回 token 对应 entry 缓存的原 argsJson，供方案 B 一步执行端点在客户端未传 args 时回填使用。
        /// token 不存在或已过期返回 null。不消费 entry。
        /// </summary>
        internal static string TryPeekArgsJson(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            if (!_grants.TryGetValue(token, out var entry)) return null;
            if (DateTime.UtcNow > entry.ExpiresAtUtc) return null;
            return entry.ArgsJson;
        }

        /// <summary>Test-only: introspect a pending entry by token.</summary>
        internal static GrantRequest PeekPendingForTests(string token) => PeekPending(token);

        // ===== Helpers =====

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
                        // Treat malformed JSON as empty — never crash the editor on a corrupt pref.
                    }
                }
                _allowlist = set;
                // 首次初始化后立即尝试迁移；幂等通过 PrefKeyMigrationDone 标记。
                MigrateLegacyGrantedToAllowlist();
            }
        }

        /// <summary>
        /// 一次性把 v1.9 旧的 <c>UnitySkills_GrantedSkills</c> 数据迁移到新的
        /// <c>UnitySkills_AllowlistSkills</c>。通过 <see cref="PrefKeyMigrationDone"/> 保证幂等。
        /// 旧 key 故意不删除，留作回滚标记。
        ///
        /// 必须在持有 <see cref="_allowlistLock"/> 时调用（由 <see cref="EnsureAllowlistLoaded"/> 保证）。
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
                    // 旧数据损坏不应阻塞迁移；标记完成即可，等价于"无东西可迁"。
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

        /// <summary>
        /// Produce a short human-readable summary of args for the panel and audit log.
        /// Keeps top-level scalar key=value pairs and replaces nested objects with "{...}".
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
        /// Pre-v1.9 install marker. Any of these global UnitySkills_* prefs means the user
        /// was running the package before the mode system existed → default to Bypass so
        /// the upgrade is behavior-neutral.
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
