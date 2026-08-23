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
    /// <see cref="SkillsModeManager.TryGrantDetailed"/> 的结果。
    /// 让 HTTP 处理器能区分"等待面板批准"（Panel 通道的正常状态）与"令牌无效/过期"（错误）。
    /// </summary>
    public enum GrantOutcome
    {
        Granted,
        PendingApproval,
        Invalid,
    }

    /// <summary>
    /// 待处理授权请求的对外（UI 可见）视图。
    /// 由 <see cref="SkillsModeManager.PendingGrantRequests"/> 返回；
    /// UI 面板把它们渲染成带"批准/拒绝"按钮的卡片。
    /// </summary>
    public sealed class GrantRequest
    {
        public string Token;
        public string SkillName;
        public string ArgsSummary;
        public DateTime ExpiresAtUtc;
        /// <summary>用户在面板上点过"批准"后为 true（仅 Panel 通道）。</summary>
        public bool ApprovedByPanel;
        /// <summary>"dialog" 或 "panel"——REST 响应用的 wire 字符串。</summary>
        public string Channel;
    }

    /// <summary>
    /// Skill 模式权限系统的核心。三档运行模式（Approval / Auto / Bypass）
    /// + 双通道授权（Dialog / Panel）
    /// + **Allowlist（用户手动管理的常驻白名单，可覆盖 IsForbiddenInSemi）**
    /// + **单次有效的 Approval**（grant/approve 只放行当次调用）。
    ///
    /// 与最初 Approval 设计相比的语义拆分：
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
    /// 状态存储：
    /// - <c>CurrentMode</c> / <c>PanelApprovalRequired</c> / <c>AllowlistSkills</c>：EditorPrefs（按机器）
    /// - 待处理的 grant 令牌：仅内存（TTL 5 分钟，最多 256 个存活）
    /// - 单次放行标记：仅内存的 ThreadStatic
    ///
    /// 升级兼容：安装环境中若已存在任何 v1.9 之前的 <c>UnitySkills_*</c> pref
    /// （如 <c>UnitySkills_PreferredPort</c>），默认落到 <see cref="SkillsOperatingMode.Bypass"/>，
    /// 使老用户行为零变化；全新安装默认 <see cref="SkillsOperatingMode.Auto"/>——
    /// 所有未被自动判为 NeverInSemi 的技能（含 FullAuto 写技能）直接执行，只有 NeverInSemi
    /// （Delete / MayEnterPlayMode / MayTriggerReload / RiskLevel=high）返回 MODE_FORBIDDEN。
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsModeManager
    {
        public enum AccessResult { Allowed, NeedsGrant, Forbidden }
        public enum ApprovalChannel { Dialog, Panel }

        private const string PrefKeyMode = "UnitySkills_OperatingMode";
        private const string PrefKeyPanelApproval = "UnitySkills_PanelApprovalRequired";

        /// <summary>Allowlist 持久化 key（用户手动管理）。</summary>
        private const string PrefKeyAllowlist = "UnitySkills_AllowlistSkills";
        /// <summary>首次迁移完成标记，避免重复执行。</summary>
        private const string PrefKeyMigrationDone = "UnitySkills_AllowlistMigratedFromGranted";
        /// <summary>旧 GrantedSkills key（仅用于一次性迁移读取，迁移后不删除以便回滚）。</summary>
        private const string PrefKeyLegacyGranted = "UnitySkills_GrantedSkills";

        // ResetForTests 会临时清空这些机器级偏好。SessionState 能撑过域重载，
        // 使测试运行被中断时仍可恢复用户原本的设置。
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

        // NeverInSemi 判定完全由元数据标志驱动（Operation=Delete / MayEnterPlayMode /
        // MayTriggerReload / RiskLevel=high），在 IsForbiddenInSemi 中检查，不存在硬编码名单。
        // 将来若有高危技能需要非元数据的例外，优先给技能本身加标注（RiskLevel="high" 或显式操作标志），
        // 不要重新引入名单。

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
        ///
        /// 设置方**必须**在 finally 里调用 <see cref="ClearOneShotBypass"/>——消费点不是必经之路，
        /// 详见该方法的注释。<see cref="_oneShotDeadlineUtc"/> 是第二道保险。
        /// </summary>
        [ThreadStatic] private static string _currentOneShotSkill;

        /// <summary>
        /// 令牌失效时刻。设置到消费之间只隔一次 SkillRouter.Execute 的参数校验（毫秒级），
        /// 所以任何超出 <see cref="OneShotLifetime"/> 的令牌都是残留物，一律作废而非放行。
        /// </summary>
        [ThreadStatic] private static DateTime _oneShotDeadlineUtc;

        private static readonly TimeSpan OneShotLifetime = TimeSpan.FromSeconds(30);

        public static event Action OnChanged;

        static SkillsModeManager()
        {
            RestorePreferencesAfterTestDomainReload();
        }

        // ===== 属性 =====

        /// <summary>
        /// 当前运行模式。setter 持久化到 EditorPrefs 并触发 <see cref="OnChanged"/>。
        /// 没有显式 pref 时，getter 套用出厂默认规则：老安装（存在任何其他 UnitySkills_* 键）
        /// → <see cref="SkillsOperatingMode.Bypass"/>；全新安装 → <see cref="SkillsOperatingMode.Auto"/>。
        /// 绝不会默认落到 Approval。
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
        /// 为 true 时（仅 Approval 模式），AI 发起的授权请求必须先在 Unity 面板上获批，
        /// <see cref="TryGrant"/> 才会成功。默认 false，即走 Dialog 通道
        /// （AI 在对话中取得用户同意后直接调 grant）。
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
        /// 用户手动管理的白名单。名单内的技能无论何种模式、也不管 <see cref="IsForbiddenInSemi"/>，
        /// 都能通过 <see cref="CheckAccess"/>。取代 v1.9 的 "GrantedSkills" 常驻授权名单。
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

        // ===== 公共 API：Allowlist =====

        /// <summary><paramref name="skillName"/> 在用户白名单中时返回 true。</summary>
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
        /// 把技能加入用户白名单。新增成功返回 true，已存在返回 false。
        /// 新增时记审计事件 "allowlist_add"。
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
        /// 从用户白名单移除技能。原本存在返回 true，否则 false。
        /// 成功时记审计事件 "allowlist_remove"。
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

        /// <summary>清空整个白名单。仅在原本非空时记审计事件 "allowlist_clear"。</summary>
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

        // ===== 公共 API：授权生命周期 =====

        /// <summary>
        /// 签发一个新的授权请求令牌，绑定 (skillName, argsHash, channel, TTL)。
        /// AI 随后通过 <see cref="TryGrant"/> 回放该令牌。Panel 通道下该令牌还会出现在
        /// <see cref="PendingGrantRequests"/> 中，供面板侧批准/拒绝。
        ///
        /// 完整 argsJson 也缓存到 entry 中，供方案 B 一步执行回放。
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
        /// 消费一个授权令牌。仅在结果为完全 Granted 时返回 true。
        /// 需要区分 PendingApproval 与 Invalid 的 HTTP 处理器请改用 <see cref="TryGrantDetailed"/>。
        /// </summary>
        public static bool TryGrant(string skillName, string token, string argsJson)
            => TryGrantDetailed(skillName, token, argsJson) == GrantOutcome.Granted;

        /// <summary>
        /// 与 <see cref="TryGrant"/> 相同，但返回细分结果，使调用方能把
        /// PendingApproval 映射为 GRANT_PENDING_APPROVAL、Invalid 映射为 INVALID_TOKEN。
        ///
        /// Granted 分支**不再** AddGranted/AddToAllowlist；grant 只对本次有效，
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
        /// Panel-side approve. **不再** 将 skill 永久写入白名单，而是只把
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

        /// <summary>面板侧拒绝：移除待处理条目且不放行。</summary>
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
        /// 已废弃：请用 <see cref="AllowlistSkills"/>。为 v1.9 → v1.9.x 拆分过渡期的 HTTP/UI 兼容保留的转发器。
        /// </summary>
        [Obsolete("Use AllowlistSkills. v1.9 'Granted' was renamed to 'Allowlist' with new semantics.")]
        public static IReadOnlyCollection<string> GrantedSkills => AllowlistSkills;

        /// <summary>
        /// 已废弃：请用 <see cref="RemoveFromAllowlist"/>。为 v1.9 → v1.9.x 拆分过渡期的 HTTP/UI 兼容保留的转发器。
        /// </summary>
        [Obsolete("Use RemoveFromAllowlist. v1.9 'Revoke' was renamed to clarify the new Allowlist semantics.")]
        public static void Revoke(string skillName) => RemoveFromAllowlist(skillName);

        /// <summary>
        /// 已废弃：请用 <see cref="ClearAllowlist"/>。为 v1.9 → v1.9.x 拆分过渡期的 HTTP/UI 兼容保留的转发器。
        /// </summary>
        [Obsolete("Use ClearAllowlist. v1.9 'RevokeAll' was renamed to clarify the new Allowlist semantics.")]
        public static void RevokeAll() => ClearAllowlist();

        // ===== 内部（由 SkillRouter / SkillsHttpServer 调用） =====

        /// <summary>
        /// 在当前运行模式 + 白名单状态下判定某技能是否可执行。
        /// 调用方（SkillRouter）据结果转成错误响应或继续执行。
        ///
        /// 优先级（依次判断）：
        /// 1. Bypass 模式 → Allowed
        /// 2. one-shot bypass 命中（grant 方案 B 重入）→ Allowed
        /// 3. Allowlist 命中 → Allowed（**优先于** <see cref="IsForbiddenInSemi"/>，
        ///    实现"用户手动放行高危拦截"）
        /// 4. 命中 IsForbiddenInSemi → Forbidden
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
        /// 消费当前线程的 one-shot 放行令牌。命中（即 <c>_currentOneShotSkill</c> 等于
        /// <paramref name="skillName"/>，忽略大小写，且未超出存活窗口）则清空并返回 true；
        /// 否则返回 false。过期令牌被直接丢弃并告警——它只可能来自漏掉
        /// <see cref="ClearOneShotBypass"/> 的路径，放行它等于静默绕过 Approval 门。
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
        /// 无条件清除当前线程的 one-shot 放行令牌。**设置令牌的一方必须在 finally 里调用它**：
        /// 消费点 <see cref="CheckAccess"/> 位于 SkillRouter.Execute 的四道参数校验
        /// （UnknownParam / MissingParam / TypeMismatch / SemanticInvalid）之后，任何一道早退
        /// 都走不到消费点。令牌是 ThreadStatic，而 grant 与普通请求跑在同一条 Unity 主线程上，
        /// 残留令牌会让下一个同名 skill 请求带着完全不同的参数被静默放行（审计里还只记成
        /// grantSource="auto"，无法追溯）。
        ///
        /// 更强的绑定是把令牌升级为 (skillName, argsHash) 并在消费点比对本次请求的 args；
        /// 但消费点只拿得到 SkillInfo，args 需要改 SkillRouter.ApplyModeGate → CheckAccess 的
        /// 调用签名才能传进来（不在本次改动范围）。当前用"设置方无条件清除 +
        /// <see cref="OneShotLifetime"/> 存活窗口"把泄漏窗口封死。
        /// </summary>
        public static void ClearOneShotBypass()
        {
            _currentOneShotSkill = null;
            _oneShotDeadlineUtc = default;
        }

        /// <summary>
        /// 该技能在非 Bypass 模式下必须被拦截时返回 true。判定纯由元数据驱动。
        ///
        /// 移除 _explicitNeverList 兜底（已无命中）— metadata 已完全覆盖当前 75 个
        /// NeverInSemi skill（全部由下面 4 条规则触发，0 个依赖名单兜底）。
        ///
        /// 注意：<see cref="CheckAccess"/> 在 IsInAllowlist 命中时**会跳过本判定**，
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

        /// <summary>运行模式的 wire 字符串（"approval"|"auto"|"bypass"）。</summary>
        internal static string ModeToWire(SkillsOperatingMode mode) => mode.ToString().ToLowerInvariant();

        /// <summary>授权通道的 wire 字符串（"dialog"|"panel"）。</summary>
        internal static string ChannelToWire(ApprovalChannel channel) => channel.ToString().ToLowerInvariant();

        /// <summary>SkillMode 的 wire 字符串（"semi"|"full"），供 /skills 清单使用。</summary>
        internal static string SkillModeToWire(SkillMode mode) =>
            mode == SkillMode.SemiAuto ? "semi" : "full";

        /// <summary>
        /// 技能在 <see cref="SkillsOperatingMode.Approval"/> 模式下的默认行为的 wire 字符串，
        /// 不考虑用户白名单与单次放行状态。供 /skills 清单使用，使调用方无需从 <c>mode</c> 反推规则
        /// 即可判断授权要求。
        ///
        /// 映射关系（与 <see cref="CheckAccess"/> 的 Approval 分支一致）：
        /// <list type="bullet">
        /// <item><c>"forbid"</c>——<see cref="IsForbiddenInSemi"/> 为 true；仅 Bypass 模式（或经白名单覆盖）可调。</item>
        /// <item><c>"grant"</c>——FullAuto 技能且未被禁；执行前需要 <c>/permission/grant</c>。</item>
        /// <item><c>"allow"</c>——SemiAuto 技能且未被禁；Approval 模式下直接执行。</item>
        /// </list>
        /// </summary>
        internal static string ApprovalBehaviorForSkill(SkillRouter.SkillInfo skill)
        {
            if (skill == null) return "allow";
            if (IsForbiddenInSemi(skill)) return "forbid";
            return skill.Mode == SkillMode.SemiAuto ? "allow" : "grant";
        }

        /// <summary>仅测试用：把全部状态（白名单、待处理项、prefs、迁移标记）清成干净初始态。</summary>
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

        /// <summary>仅测试用：夹具恢复原偏好后清除恢复数据。</summary>
        internal static void CompleteTestPreferenceRecovery()
        {
            ClearTestPreferenceRecovery();
        }

        /// <summary>仅测试用：模拟静态构造器在域重载后执行的恢复过程。</summary>
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

        /// <summary>按令牌查找待处理授权条目（内部使用——SkillRouter 借此暴露 argsSummary）。</summary>
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

        /// <summary>仅测试用：按令牌查看待处理条目。</summary>
        internal static GrantRequest PeekPendingForTests(string token) => PeekPending(token);

        // ===== 辅助方法 =====

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
                        // 畸形 JSON 视为空——绝不因一条损坏的 pref 拖垮编辑器。
                    }
                }
                _allowlist = set;
                // 首次初始化后立即尝试迁移；幂等通过 PrefKeyMigrationDone 标记。
                MigrateLegacyGrantedToAllowlist();
            }
        }

        /// <summary>
        /// 一次性把旧的 <c>UnitySkills_GrantedSkills</c> 数据迁移到新的
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
        /// 为面板与审计日志生成一段简短可读的参数摘要。
        /// 保留顶层标量的 key=value，嵌套对象一律替换成 "{...}"。
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
        /// v1.9 之前安装的判据。存在其中任何一个全局 UnitySkills_* pref，就说明用户在模式系统出现之前
        /// 就在用本包 → 默认落到 Bypass，使升级对行为无影响。
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
