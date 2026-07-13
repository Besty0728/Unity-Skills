using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Settings drawer — slide-in panel from the right edge.
    /// Hosts (in order): Permissions / Server / Runtime / Statistics.
    /// Permissions is first so users see it on opening the drawer.
    /// </summary>
    public class SettingsDrawerController
    {
        private const string DrawerUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/SettingsDrawer.uxml";

        // class marker on pending-row expires Label — used by the per-second countdown sweep.
        // No USS rule needed; only consumed by Query() in RefreshPendingExpiry.
        private const string PendingExpiresClass = "perm-pending-expires";

        // dropdown choices 与 SkillsOperatingMode 的位置一对一对应，避免依赖本地化文本做反查。
        private static readonly SkillsOperatingMode[] _modeOrder = new[]
        {
            SkillsOperatingMode.Approval,
            SkillsOperatingMode.Auto,
            SkillsOperatingMode.Bypass,
        };

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private VisualElement _drawerContainer;
        private VisualElement _drawerMask;

        // Header
        private Label  _drawerTitle;
        private Button _closeBtn;

        // Permissions group
        private Label         _permGroupTitle;
        private Label         _modeLabel;
        private DropdownField _modeDropdown;
        private Label         _modeHint;
        private VisualElement _panelApprovalRow;
        private Toggle        _panelApprovalToggle;
        private Label         _panelApprovalHint;
        private VisualElement _pendingSection;
        private Label         _pendingTitle;
        private VisualElement _pendingList;
        private VisualElement _allowlistSection;
        private Foldout       _allowlistFoldout;
        private VisualElement _allowlistList;
        private Button        _allowlistClearBtn;
        private Button        _allowlistAddBtn;
        private Button        _viewAuditBtn;

        // Server group
        private Label           _serverGroupTitle;
        private Toggle          _autoStartToggle;
        private Label           _autoStartHint;
        private Label           _portLabel;
        private DropdownField   _portDropdown;
        private Label           _timeoutLabel;
        private IntegerField    _timeoutField;
        private Label           _timeoutUnit;
        private Label           _keepaliveLabel;
        private IntegerField    _keepaliveField;
        private Label           _keepaliveUnit;
        private Label           _keepaliveHint;

        // Runtime group
        private Label         _runtimeGroupTitle;
        private Label         _loglevelLabel;
        private DropdownField _logDropdown;
        private Toggle        _confirmToggle;
        private Label         _confirmHint;
        private Toggle        _telemetryToggle;
        private Label         _telemetryHint;

        // Stats group
        private Label  _statsGroupTitle;
        private Label  _statsHint;
        private Button _statsResetBtn;

        // Shortcuts group — own controller (capture state machine + conflict detection).
        private ShortcutsSettingsController _shortcutsController;

        public SettingsDrawerController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            _drawerContainer = _root.Q<VisualElement>("drawer");
            _drawerMask      = _root.Q<VisualElement>("drawer-mask");

            if (_drawerContainer == null)
            {
                Debug.LogError("[UnitySkills] Drawer container not found in main UXML.");
                return;
            }

            // Clone drawer content into the drawer container
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(DrawerUxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load drawer UXML: {DrawerUxmlPath}");
                return;
            }
            uxml.CloneTree(_drawerContainer);

            CacheUiReferences();
            ApplyCloseIcon();
            BindEvents();
            InitializeValues();
            RefreshPermissionsUi();

            // Shortcuts 节：独立控制器接管捕获态机与冲突检测，抽屉仅做组装与生命周期转发。
            _shortcutsController = new ShortcutsSettingsController(_drawerContainer);

            // Click on mask closes the drawer
            if (_drawerMask != null)
            {
                _drawerMask.RegisterCallback<ClickEvent>(_ => Close());
            }

            // 权限状态由 SkillsModeManager 全局广播；订阅以同步抽屉 UI。
            // 用 DetachFromPanelEvent 解绑，避免 EditorWindow 关闭后泄漏。
            SkillsModeManager.OnChanged += RefreshPermissionsUi;
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);

            // 倒计时每秒推进一次；ScheduleItem 跟随 _root 生命周期自动停止。
            // 同时做权限状态快照对比 — OnChanged 信号若因后台窗口/事件循环延迟丢失，
            // 这条 polling 兜底保证 Drawer 总能在 1s 内同步到最新 pending/granted。
            // 经 EditorUiScheduler.RepeatSafe 把实际 mutation 推迟到 delayCall，避免落在
            // repaint/generateVisualContent 期间触发 InvalidOperationException（issue #44）。
            EditorUiScheduler.RepeatSafe(_root, 1000, TickPermissions);
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            SkillsModeManager.OnChanged -= RefreshPermissionsUi;
        }

        private void ApplyCloseIcon()
        {
            if (_closeBtn == null) return;
            // Unity 内置 winbtn_win_close 在不同版本/平台命名不一致，
            // 直接用 Unicode × 更稳定，避免 "Unable to load the icon" 警告。
            _closeBtn.text = "✕";
        }

        private void CacheUiReferences()
        {
            _drawerTitle = _drawerContainer.Q<Label>("drawer-title");
            _closeBtn    = _drawerContainer.Q<Button>("drawer-close-btn");

            // Permissions group
            _permGroupTitle      = _drawerContainer.Q<Label>("group-permissions-title");
            _modeLabel           = _drawerContainer.Q<Label>("perm-mode-label");
            _modeDropdown        = _drawerContainer.Q<DropdownField>("perm-mode-dropdown");
            _modeHint            = _drawerContainer.Q<Label>("perm-mode-hint");
            _panelApprovalRow    = _drawerContainer.Q<VisualElement>("row-panel-approval");
            _panelApprovalToggle = _drawerContainer.Q<Toggle>("perm-panel-approval-toggle");
            _panelApprovalHint   = _drawerContainer.Q<Label>("perm-panel-approval-hint");
            _pendingSection      = _drawerContainer.Q<VisualElement>("perm-pending-section");
            _pendingTitle        = _drawerContainer.Q<Label>("perm-pending-title");
            _pendingList         = _drawerContainer.Q<VisualElement>("perm-pending-list");
            _allowlistSection    = _drawerContainer.Q<VisualElement>("perm-allowlist-section");
            _allowlistFoldout    = _drawerContainer.Q<Foldout>("perm-allowlist-foldout");
            _allowlistList       = _drawerContainer.Q<VisualElement>("perm-allowlist-list");
            _allowlistClearBtn   = _drawerContainer.Q<Button>("perm-allowlist-clear-btn");
            _allowlistAddBtn     = _drawerContainer.Q<Button>("perm-allowlist-add-btn");
            _viewAuditBtn        = _drawerContainer.Q<Button>("perm-view-audit-btn");

            _serverGroupTitle = _drawerContainer.Q<Label>("group-server-title");
            _autoStartToggle  = _drawerContainer.Q<Toggle>("autostart-toggle");
            _autoStartHint    = _drawerContainer.Q<Label>("autostart-hint");
            _portLabel        = _drawerContainer.Q<Label>("port-label");
            _portDropdown     = _drawerContainer.Q<DropdownField>("port-dropdown");
            _timeoutLabel     = _drawerContainer.Q<Label>("timeout-label");
            _timeoutField     = _drawerContainer.Q<IntegerField>("timeout-field");
            _timeoutUnit      = _drawerContainer.Q<Label>("timeout-unit");
            _keepaliveLabel   = _drawerContainer.Q<Label>("keepalive-label");
            _keepaliveField   = _drawerContainer.Q<IntegerField>("keepalive-field");
            _keepaliveUnit    = _drawerContainer.Q<Label>("keepalive-unit");
            _keepaliveHint    = _drawerContainer.Q<Label>("keepalive-hint");

            _runtimeGroupTitle = _drawerContainer.Q<Label>("group-runtime-title");
            _loglevelLabel     = _drawerContainer.Q<Label>("loglevel-label");
            _logDropdown       = _drawerContainer.Q<DropdownField>("loglevel-dropdown");
            _confirmToggle     = _drawerContainer.Q<Toggle>("confirm-toggle");
            _confirmHint       = _drawerContainer.Q<Label>("confirm-hint");
            _telemetryToggle   = _drawerContainer.Q<Toggle>("telemetry-toggle");
            _telemetryHint     = _drawerContainer.Q<Label>("telemetry-hint");

            _statsGroupTitle = _drawerContainer.Q<Label>("group-stats-title");
            _statsHint       = _drawerContainer.Q<Label>("stats-hint");
            _statsResetBtn   = _drawerContainer.Q<Button>("stats-reset-btn");
        }

        private void BindEvents()
        {
            if (_closeBtn != null) _closeBtn.clicked += Close;

            // Permissions: 用 dropdown 替代原来的三个 radio toggle。
            // index 由 _modeOrder 反查为枚举，避免依赖本地化文本。
            if (_modeDropdown != null)
                _modeDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = _modeDropdown.choices.IndexOf(evt.newValue);
                    if (idx < 0 || idx >= _modeOrder.Length) return;
                    var target = _modeOrder[idx];
                    if (SkillsModeManager.CurrentMode != target)
                        SkillsModeManager.CurrentMode = target; // setter 触发 OnChanged → RefreshPermissionsUi
                });

            if (_panelApprovalToggle != null)
                _panelApprovalToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsModeManager.PanelApprovalRequired)
                        SkillsModeManager.PanelApprovalRequired = evt.newValue;
                });

            if (_allowlistClearBtn != null)
                _allowlistClearBtn.clicked += () => SkillsModeManager.ClearAllowlist();

            if (_allowlistAddBtn != null)
                _allowlistAddBtn.clicked += OnAddAllowlistClicked;

            if (_viewAuditBtn != null)
                _viewAuditBtn.clicked += () => UnitySkillsAuditWindow.ShowWindow();

            if (_autoStartToggle != null)
                _autoStartToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsHttpServer.AutoStart)
                        SkillsHttpServer.AutoStart = evt.newValue;
                });

            if (_portDropdown != null)
                _portDropdown.RegisterValueChangedCallback(evt =>
                {
                    int newIdx = _portDropdown.choices.IndexOf(evt.newValue);
                    int targetPort = (newIdx <= 0) ? 0 : 8089 + newIdx;
                    if (targetPort != SkillsHttpServer.PreferredPort)
                        SkillsHttpServer.PreferredPort = targetPort;
                });

            if (_timeoutField != null)
                _timeoutField.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsHttpServer.RequestTimeoutMinutes)
                        SkillsHttpServer.RequestTimeoutMinutes = evt.newValue;
                });

            if (_keepaliveField != null)
                _keepaliveField.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsHttpServer.KeepAliveIntervalSeconds)
                        SkillsHttpServer.KeepAliveIntervalSeconds = evt.newValue;
                });

            if (_logDropdown != null)
                _logDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = _logDropdown.choices.IndexOf(evt.newValue);
                    if (idx >= 0 && idx != (int)SkillsLogger.Level)
                        SkillsLogger.Level = (LogLevel)idx;
                });

            if (_confirmToggle != null)
                _confirmToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != ConfirmationTokenService.RequireConfirmation)
                        ConfirmationTokenService.RequireConfirmation = evt.newValue;
                });

            if (_telemetryToggle != null)
                _telemetryToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillTelemetryService.Enabled)
                        SkillTelemetryService.Enabled = evt.newValue;
                });

            if (_statsResetBtn != null)
                _statsResetBtn.clicked += () =>
                {
                    SkillsHttpServer.ResetStatistics();
                };
        }

        private void InitializeValues()
        {
            // dropdown 的 choices 用模式术语英文短名；不本地化（与 Claude Code 文档一致）。
            // RefreshPermissionsUi 负责按当前模式 SetValue。
            if (_modeDropdown != null)
            {
                _modeDropdown.choices = new List<string> { "Approval", "Auto", "Bypass" };
            }

            if (_portDropdown != null)
            {
                _portDropdown.choices = new List<string>
                {
                    "Auto", "8090", "8091", "8092", "8093", "8094",
                    "8095", "8096", "8097", "8098", "8099", "8100"
                };
                int currentPort = SkillsHttpServer.PreferredPort;
                int idx = (currentPort == 0) ? 0 : currentPort - 8089;
                if (idx < 0 || idx >= _portDropdown.choices.Count) idx = 0;
                _portDropdown.value = _portDropdown.choices[idx];
            }

            if (_logDropdown != null)
            {
                _logDropdown.choices = new List<string>
                {
                    "Off", "Error", "Warning", "Info", "Agent", "Verbose"
                };
                int lvl = (int)SkillsLogger.Level;
                if (lvl < 0 || lvl >= _logDropdown.choices.Count) lvl = 0;
                _logDropdown.value = _logDropdown.choices[lvl];
            }

            if (_autoStartToggle != null) _autoStartToggle.value = SkillsHttpServer.AutoStart;
            if (_timeoutField   != null) _timeoutField.value     = SkillsHttpServer.RequestTimeoutMinutes;
            if (_keepaliveField != null) _keepaliveField.value   = SkillsHttpServer.KeepAliveIntervalSeconds;
            if (_confirmToggle  != null) _confirmToggle.value    = ConfirmationTokenService.RequireConfirmation;
            if (_telemetryToggle != null) _telemetryToggle.value = SkillTelemetryService.Enabled;
        }

        public void Open()
        {
            // 每次打开重建 Shortcuts 行，拉取最新绑定（覆盖 Edit ▸ Shortcuts 外部改动）。
            _shortcutsController?.Refresh();

            if (_drawerContainer != null) _drawerContainer.AddToClassList("open");
            if (_drawerMask != null)
            {
                _drawerMask.RemoveFromClassList("hidden");
                // next frame add 'open' for opacity transition (avoids flash)
                _drawerMask.schedule.Execute(() => _drawerMask.AddToClassList("open")).StartingIn(0);
                _drawerMask.pickingMode = PickingMode.Position;
            }
        }

        public void Close()
        {
            if (_drawerContainer != null) _drawerContainer.RemoveFromClassList("open");
            if (_drawerMask != null)
            {
                _drawerMask.RemoveFromClassList("open");
                _drawerMask.pickingMode = PickingMode.Ignore;
                // hide after the 0.18s opacity transition completes
                _drawerMask.schedule.Execute(() => _drawerMask.AddToClassList("hidden")).StartingIn(200);
            }
        }

        public void RefreshLocalization()
        {
            if (_drawerTitle != null) _drawerTitle.text = SkillsLocalization.Get("drawer_settings_title");
            if (_closeBtn != null)    _closeBtn.tooltip = SkillsLocalization.Get("drawer_close_tooltip");

            // Permissions group — uses PermissionUiHelpers.L fallback so missing keys
            // don't render raw keys before Localization.cs is updated.
            if (_permGroupTitle != null)
                _permGroupTitle.text = PermissionUiHelpers.L("drawer_section_permissions",
                    "Permissions", "权限");

            if (_modeLabel != null)
                _modeLabel.text = PermissionUiHelpers.L("perm_mode_label",
                    "Operating Mode", "操作模式");
            ApplyModeHintText(SkillsModeManager.CurrentMode);

            if (_panelApprovalToggle != null)
                _panelApprovalToggle.label = PermissionUiHelpers.L("perm_require_panel_approval",
                    "Require Panel Approval", "必须面板批准");
            if (_panelApprovalHint != null)
                _panelApprovalHint.text = PermissionUiHelpers.L("perm_require_panel_approval_hint",
                    "When checked, grant tokens must be Approved here on the panel; otherwise verbal consent in the AI chat is enough.",
                    "勾选后 grant token 必须在此面板点 Approve 才生效；否则 AI 对话中用户文字同意即可。");

            if (_allowlistClearBtn != null)
                _allowlistClearBtn.text = PermissionUiHelpers.L("perm_allowlist_clear_all",
                    "Clear All", "全部清除");
            if (_allowlistAddBtn != null)
                _allowlistAddBtn.text = PermissionUiHelpers.L("perm_add_skill_btn",
                    "+ Add Skill", "+ 添加 Skill");
            if (_viewAuditBtn != null)
                _viewAuditBtn.text = PermissionUiHelpers.L("perm_view_audit_log",
                    "View Audit Log", "查看审计日志");

            // Pending / Allowlist titles include counts, so rebuild via RefreshPermissionsUi
            // to pick up the new language strings together with the live data.
            RefreshPermissionsUi();

            if (_serverGroupTitle  != null) _serverGroupTitle.text  = SkillsLocalization.Get("drawer_section_server");
            if (_runtimeGroupTitle != null) _runtimeGroupTitle.text = SkillsLocalization.Get("drawer_section_runtime");
            if (_statsGroupTitle   != null) _statsGroupTitle.text   = SkillsLocalization.Get("drawer_section_stats");

            if (_autoStartToggle != null) _autoStartToggle.label = SkillsLocalization.Get("auto_restart");
            if (_autoStartHint   != null) _autoStartHint.text    = SkillsLocalization.Get("auto_restart_hint");

            if (_portLabel       != null) _portLabel.text     = SkillsLocalization.Get("drawer_port_label");
            if (_timeoutLabel    != null) _timeoutLabel.text  = SkillsLocalization.Get("drawer_timeout_label");
            if (_timeoutUnit     != null) _timeoutUnit.text   = SkillsLocalization.Get("timeout_unit");
            if (_keepaliveLabel  != null) _keepaliveLabel.text = SkillsLocalization.Get("drawer_keepalive_label");
            if (_keepaliveUnit   != null) _keepaliveUnit.text  = SkillsLocalization.Get("keepalive_unit");
            if (_keepaliveHint   != null) _keepaliveHint.text  = SkillsLocalization.Get("keepalive_hint");

            if (_loglevelLabel != null) _loglevelLabel.text = SkillsLocalization.Get("drawer_loglevel_label");
            if (_confirmToggle != null) _confirmToggle.label = SkillsLocalization.Get("drawer_confirm_label");
            if (_confirmHint   != null)
            {
                _confirmHint.text = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "开启后：删除类/RiskLevel=high 技能首次调用返回 _confirm token + dryRun 预览，5 分钟内带 token 重试才执行。"
                    : "When ON: delete / high-risk skills first return a _confirm token + dryRun preview; re-call within 5 min with the token to execute.";
            }

            if (_telemetryToggle != null)
                _telemetryToggle.label = SkillsLocalization.Get("drawer_telemetry_label");
            if (_telemetryHint != null)
                _telemetryHint.text = SkillsLocalization.Get("drawer_telemetry_hint");

            if (_statsHint     != null) _statsHint.text     = SkillsLocalization.Get("drawer_stats_hint");
            if (_statsResetBtn != null) _statsResetBtn.text = SkillsLocalization.Get("drawer_reset_stats_btn");

            _shortcutsController?.RefreshLocalization();
        }

        // ===== Permissions group helpers =====

        private void SyncModeDropdownValue(SkillsOperatingMode mode)
        {
            if (_modeDropdown == null) return;
            int idx = Array.IndexOf(_modeOrder, mode);
            if (idx < 0 || idx >= _modeDropdown.choices.Count) return;
            _modeDropdown.SetValueWithoutNotify(_modeDropdown.choices[idx]);
        }

        private void ApplyModeHintText(SkillsOperatingMode mode)
        {
            if (_modeHint == null) return;
            switch (mode)
            {
                case SkillsOperatingMode.Approval:
                    _modeHint.text = PermissionUiHelpers.L("perm_mode_approval_hint",
                        "AI must ask the user before invoking each FullAuto skill (per-skill grant).",
                        "AI 必须询问用户后才能调用 FullAuto 技能（逐技能授权）。");
                    break;
                case SkillsOperatingMode.Auto:
                    _modeHint.text = PermissionUiHelpers.L("perm_mode_auto_hint",
                        "AI decides on its own. Server only blocks high-risk skills (Delete / PlayMode / Reload).",
                        "AI 自动决策；服务端仅拦截真高危技能（Delete / PlayMode / Reload）。");
                    break;
                case SkillsOperatingMode.Bypass:
                    _modeHint.text = PermissionUiHelpers.L("perm_mode_bypass_hint",
                        "All skills pass through. ConfirmationToken still gates high-risk operations.",
                        "全部技能直接放行；ConfirmationToken 仍对高危操作生效。");
                    break;
                default:
                    _modeHint.text = string.Empty;
                    break;
            }
        }

        /// <summary>
        /// 同步三类权限 UI：模式 toggles、Approval 设置 row、Pending/Granted 列表。
        /// 由 OnChanged 事件、本类初始化、Localization 切换调用。
        /// </summary>
        private void RefreshPermissionsUi()
        {
            if (_drawerContainer == null) return;
            var mode = SkillsModeManager.CurrentMode;

            // 1) dropdown 同步到当前模式 + 刷新 hint
            SyncModeDropdownValue(mode);
            ApplyModeHintText(mode);

            // 2) Panel Approval row 仅 Approval 模式可见
            SetDisplay(_panelApprovalRow, mode == SkillsOperatingMode.Approval);
            if (_panelApprovalToggle != null)
                _panelApprovalToggle.SetValueWithoutNotify(SkillsModeManager.PanelApprovalRequired);

            // 3) Pending 列表 — 仅 Approval 模式 + 有待批时显示
            var pending = SkillsModeManager.PendingGrantRequests;
            bool showPending = mode == SkillsOperatingMode.Approval && pending.Count > 0;
            SetDisplay(_pendingSection, showPending);
            if (showPending)
            {
                if (_pendingTitle != null)
                    _pendingTitle.text = string.Format(
                        PermissionUiHelpers.L("perm_pending_requests_fmt",
                            "Pending Grant Requests ({0})",
                            "待批请求 ({0})"),
                        pending.Count);
                RebuildPendingList(pending);
            }
            else if (_pendingList != null)
            {
                _pendingList.Clear();
            }

            // 4) Allowlist 列表 — Approval/Auto 显示（Bypass 隐藏）
            var allowlist = SkillsModeManager.AllowlistSkills;
            bool showAllowlist = mode != SkillsOperatingMode.Bypass;
            SetDisplay(_allowlistSection, showAllowlist);
            if (showAllowlist)
            {
                if (_allowlistFoldout != null)
                    _allowlistFoldout.text = string.Format(
                        PermissionUiHelpers.L("perm_allowlist_skills_fmt",
                            "Allowlist Skills ({0})",
                            "白名单 Skills ({0})"),
                        allowlist.Count);
                if (_allowlistAddBtn != null)
                    _allowlistAddBtn.SetEnabled(true);
                if (_allowlistClearBtn != null)
                    _allowlistClearBtn.SetEnabled(allowlist.Count > 0);
                RebuildAllowlistList(allowlist);
            }
            else if (_allowlistList != null)
            {
                _allowlistList.Clear();
            }
        }

        private void RebuildPendingList(IReadOnlyList<GrantRequest> pending)
        {
            if (_pendingList == null) return;
            _pendingList.Clear();
            foreach (var req in pending)
                _pendingList.Add(BuildPendingRow(req));
        }

        private static VisualElement BuildPendingRow(GrantRequest req)
        {
            var card = new VisualElement();
            card.AddToClassList("task-card");
            card.style.flexDirection = FlexDirection.Column;
            card.style.marginBottom = 4;

            var head = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            var title = new Label($"{req.SkillName}  ({req.Channel})  #{PermissionUiHelpers.ShortToken(req.Token)}");
            title.AddToClassList("bold-label");
            title.style.flexGrow = 1;
            title.style.fontSize = 11;
            head.Add(title);

            var expires = new Label(PermissionUiHelpers.FormatCountdown(req.ExpiresAtUtc));
            expires.AddToClassList("setting-hint");
            expires.AddToClassList(PendingExpiresClass); // marker for RefreshPendingExpiry sweep
            expires.userData = req.ExpiresAtUtc;
            expires.style.marginTop = 0;
            expires.style.marginBottom = 0;
            head.Add(expires);
            card.Add(head);

            if (!string.IsNullOrEmpty(req.ArgsSummary))
            {
                var args = new Label($"args: {req.ArgsSummary}");
                args.AddToClassList("setting-hint");
                args.style.whiteSpace = WhiteSpace.Normal;
                args.style.marginTop = 2;
                args.style.marginBottom = 4;
                card.Add(args);
            }

            bool isPanel = req.Channel == "panel";

            // 渠道区分反馈：Panel 渠道走面板 Approve；Dialog 渠道的批准走 AI 对话，面板按钮无效，给出明确指引
            if (isPanel && req.ApprovedByPanel)
            {
                var status = new Label(PermissionUiHelpers.L("perm_approved_waiting",
                    "Approved · waiting for AI to execute", "已批准 · 等待 AI 执行"));
                status.AddToClassList("setting-hint");
                status.style.marginBottom = 2;
                card.Add(status);
            }
            else if (!isPanel)
            {
                var chatHint = new Label(PermissionUiHelpers.L("perm_approve_in_chat",
                    "Dialog channel — approve in the AI chat", "对话渠道 · 请在 AI 对话中批准"));
                chatHint.AddToClassList("setting-hint");
                chatHint.style.marginBottom = 2;
                card.Add(chatHint);
            }

            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 2 } };
            var approveBtn = new Button(() => SkillsModeManager.Approve(req.Token))
            {
                text = PermissionUiHelpers.L("perm_approve", "Approve", "批准")
            };
            approveBtn.AddToClassList("mini-btn");
            approveBtn.style.marginRight = 4;
            approveBtn.SetEnabled(isPanel && !req.ApprovedByPanel); // 仅 Panel 渠道未批准时可点
            actions.Add(approveBtn);

            var denyBtn = new Button(() => SkillsModeManager.Deny(req.Token))
            {
                text = PermissionUiHelpers.L("perm_deny", "Deny", "拒绝")
            };
            denyBtn.AddToClassList("mini-btn");
            denyBtn.AddToClassList("danger");
            actions.Add(denyBtn);

            card.Add(actions);
            return card;
        }

        /// <summary>
        /// "+ Add Skill" 按钮回调：弹出按 Category 分组的 GenericMenu，让用户手动把 skill
        /// 加入白名单。高危 skill（RiskLevel=high / Delete / PlayMode / Reload）会先弹
        /// 二次确认 dialog，避免一键放行严重操作。
        ///
        /// 高危判定特意在 UI 层重做（而不是反射 SkillsModeManager.IsForbiddenInSemi），
        /// 保持 ModeManager 的可见性边界不被 UI 反向污染。
        /// </summary>
        /// <summary>
        /// 打开 AllowlistPickerWindow —— 支持搜索、按 Category 分组勾选、整组一键选中、
        /// 提交时合并高危确认。窗口自负责调 AddToAllowlist；本控制器在 OnChanged 链路上自动刷新列表。
        /// </summary>
        private void OnAddAllowlistClicked()
        {
            AllowlistPickerWindow.Open();
        }

        private void RebuildAllowlistList(IReadOnlyCollection<string> allowlist)
        {
            if (_allowlistList == null) return;
            _allowlistList.Clear();

            if (allowlist.Count == 0)
            {
                var empty = new Label(PermissionUiHelpers.L("perm_no_allowlist",
                    "No allowlisted skills.", "白名单为空。"));
                empty.AddToClassList("setting-hint");
                _allowlistList.Add(empty);
                return;
            }

            // 用 SkillRouter snapshot 解析 name → Category；未注册 skill（注册表 refresh 间隔等）
            // 归入特殊分组 "(Unknown)" 而不是丢弃，让用户至少能看到并 Remove。
            var nameToCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var s in SkillRouter.GetAllSkillsSnapshot() ?? Array.Empty<SkillRouter.SkillInfo>())
                {
                    if (s != null && !string.IsNullOrEmpty(s.Name))
                        nameToCategory[s.Name] = s.Category.ToString();
                }
            }
            catch { /* snapshot 失败时全部归入 Unknown 分组 */ }

            var grouped = allowlist
                .GroupBy(n => nameToCategory.TryGetValue(n, out var c) ? c : "(Unknown)")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                var items = group.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var foldout = new Foldout
                {
                    text = $"{group.Key}  ({items.Count})",
                    value = false, // 默认折叠，省空间；用户点开展看
                };
                foldout.style.marginTop = 2;

                foreach (var name in items)
                    foldout.Add(BuildAllowlistRow(name));

                _allowlistList.Add(foldout);
            }
        }

        private static VisualElement BuildAllowlistRow(string skillName)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 }
            };
            var label = new Label(skillName) { style = { flexGrow = 1, fontSize = 11 } };
            row.Add(label);

            var removeBtn = new Button(() => SkillsModeManager.RemoveFromAllowlist(skillName))
            {
                text = PermissionUiHelpers.L("perm_remove_from_allowlist", "Remove", "移除")
            };
            removeBtn.AddToClassList("mini-btn");
            row.Add(removeBtn);
            return row;
        }

        /// <summary>
        /// 每秒扫一遍 pending 列表中的 expires Label，按 userData 中的 UTC 过期时间重算文字。
        /// 不重建条目，避免破坏潜在的 hover/focus；过期到 0 后下次 OnChanged 会清掉条目。
        /// </summary>
        /// <summary>
        /// 每秒一次：先比对 pending+granted 快照决定是否需要重建 list，否则只刷新倒计时。
        /// OnChanged 事件链路如果丢失（后台窗口、跨域调用等场景），这条 polling 就是兜底。
        /// </summary>
        private void TickPermissions()
        {
            var snapshot = ComputePermSnapshot();
            if (snapshot != _lastPermSnapshot)
            {
                _lastPermSnapshot = snapshot;
                RefreshPermissionsUi();
            }
            else
            {
                RefreshPendingExpiry();
            }
        }

        private string _lastPermSnapshot = "";

        private static string ComputePermSnapshot()
        {
            var pending = SkillsModeManager.PendingGrantRequests;
            var allowlist = SkillsModeManager.AllowlistSkills;
            var sb = new System.Text.StringBuilder(64);
            sb.Append((int)SkillsModeManager.CurrentMode).Append('|');
            sb.Append(SkillsModeManager.PanelApprovalRequired ? '1' : '0').Append('|');
            sb.Append('p').Append(pending.Count).Append(':');
            for (int i = 0; i < pending.Count; i++)
                sb.Append(pending[i].Token).Append(pending[i].ApprovedByPanel ? '+' : '-').Append(',');
            sb.Append('|').Append('a').Append(allowlist.Count).Append(':');
            foreach (var s in allowlist)
                sb.Append(s).Append(',');
            return sb.ToString();
        }

        private void RefreshPendingExpiry()
        {
            if (_pendingList == null) return;
            // 没有待批就跳过 — 避免每秒都遍历空列表。
            if (SkillsModeManager.CurrentMode != SkillsOperatingMode.Approval) return;
            if (SkillsModeManager.PendingGrantRequests.Count == 0) return;

            _pendingList.Query<Label>(className: PendingExpiresClass).ForEach(label =>
            {
                if (label.userData is DateTime expiresUtc)
                    label.text = PermissionUiHelpers.FormatCountdown(expiresUtc);
            });
        }

        private static void SetDisplay(VisualElement el, bool visible)
        {
            if (el == null) return;
            el.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}

// Producer:Betsy
