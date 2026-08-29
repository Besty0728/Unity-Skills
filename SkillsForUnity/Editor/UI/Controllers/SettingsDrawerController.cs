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

        // The dropdown choices correspond position-for-position to SkillsOperatingMode, to avoid depending on localized text for reverse lookup.
        private static readonly SkillsOperatingMode[] _modeOrder = new[]
        {
            SkillsOperatingMode.Approval,
            SkillsOperatingMode.Auto,
            SkillsOperatingMode.Bypass,
        };

        // Same contract as _modeOrder: choice position maps one-to-one onto SurfaceProfileKind, so
        // the reverse lookup never depends on localized text.
        // (English on purpose — CollectUiCharacters scans this file for the baked font atlas, and
        // new CJK in comments forces an atlas top-up. See the note above _russian in Localization.cs.)
        private static readonly SurfaceProfileKind[] _profileOrder = new[]
        {
            SurfaceProfileKind.Full,
            SurfaceProfileKind.Guide,
            SurfaceProfileKind.NoSceneAuthoring,
        };

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label _languagePinsTitle;
        private DropdownField _languagePinPrimary;
        private DropdownField _languagePinSecondary;

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

        // AI tools group
        private Label  _agentSyncGroupTitle;
        private Toggle _agentAutoSyncToggle;
        private Label  _agentAutoSyncHint;

        private Label  _cliGroupTitle;
        private Label  _cliHint;
        private Button _cliOpenBtn;

        // Server group
        private Label           _serverGroupTitle;
        private Toggle          _autoStartToggle;
        private Label           _autoStartHint;
        private Toggle          _startOnLaunchToggle;
        private Label           _startOnLaunchHint;
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
        private VisualElement _updateNotificationsSwitch;
        private Label         _updateNotificationsLabel;
        private Label         _updateNotificationsHint;
        private VisualElement _confirmSwitch;
        private Label         _confirmLabel;
        private Label         _confirmHint;
        private VisualElement _telemetrySwitch;
        private Label         _telemetryLabel;
        private Label         _telemetryHint;
        private VisualElement _summaryTruncateSwitch;
        private Label         _summaryTruncateLabel;
        private Label         _summaryTruncateHint;
        private Label         _surfaceProfileLabel;
        private DropdownField _surfaceProfileDropdown;
        private Label         _surfaceProfileHint;

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

            // Shortcuts section: a separate controller owns the capture state machine and
            // conflict detection; the drawer only assembles it and forwards lifecycle events.
            _shortcutsController = new ShortcutsSettingsController(_drawerContainer);

            if (_drawerMask != null)
            {
                _drawerMask.RegisterCallback<ClickEvent>(_ => Close());
            }

            // Permission state is broadcast globally by SkillsModeManager; subscribe to keep the drawer UI in sync.
            // Unsubscribe via DetachFromPanelEvent, to avoid a leak after the EditorWindow closes.
            SkillsModeManager.OnChanged += RefreshPermissionsUi;
            // The profile can also change outside the panel (EditorPrefs migration, test fixtures),
            // so subscribe to keep the drawer showing the profile that is actually in force.
            SkillsSurfaceProfile.OnChanged += RefreshSurfaceProfileUi;
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);

            // The countdown advances once per second; ScheduleItem stops automatically following
            // the _root lifecycle.
            // This also does a snapshot comparison of permission state — if the OnChanged signal
            // is lost due to a background window or event-loop delay, this polling is the
            // fallback that guarantees the Drawer always syncs to the latest pending/granted
            // state within 1s.
            // The actual mutation is deferred to delayCall via EditorUiScheduler.RepeatSafe, to
            // avoid triggering an InvalidOperationException during repaint/generateVisualContent (issue #44).
            EditorUiScheduler.RepeatSafe(_root, 1000, TickPermissions);
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            SkillsModeManager.OnChanged -= RefreshPermissionsUi;
            SkillsSurfaceProfile.OnChanged -= RefreshSurfaceProfileUi;
        }

        private void ApplyCloseIcon()
        {
            if (_closeBtn == null) return;
            // Unity's built-in winbtn_win_close is named inconsistently across versions/platforms;
            // using the Unicode × directly is more reliable, avoiding the "Unable to load the icon" warning.
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

            _agentSyncGroupTitle = _drawerContainer.Q<Label>("group-agent-sync-title");
            _agentAutoSyncToggle = _drawerContainer.Q<Toggle>("agent-autosync-toggle");
            _agentAutoSyncHint   = _drawerContainer.Q<Label>("agent-autosync-hint");

            _cliGroupTitle = _drawerContainer.Q<Label>("group-cli-title");
            _cliHint       = _drawerContainer.Q<Label>("cli-drawer-hint");
            _cliOpenBtn    = _drawerContainer.Q<Button>("cli-open-setup-btn");

            _serverGroupTitle = _drawerContainer.Q<Label>("group-server-title");
            _autoStartToggle  = _drawerContainer.Q<Toggle>("autostart-toggle");
            _autoStartHint    = _drawerContainer.Q<Label>("autostart-hint");
            _startOnLaunchToggle = _drawerContainer.Q<Toggle>("start-on-launch-toggle");
            _startOnLaunchHint   = _drawerContainer.Q<Label>("start-on-launch-hint");
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
            _updateNotificationsSwitch = _drawerContainer.Q<VisualElement>("update-notifications-switch");
            _updateNotificationsLabel  = _drawerContainer.Q<Label>("update-notifications-label");
            _updateNotificationsHint   = _drawerContainer.Q<Label>("update-notifications-hint");
            _confirmSwitch     = _drawerContainer.Q<VisualElement>("confirm-switch");
            _confirmLabel      = _drawerContainer.Q<Label>("confirm-label");
            _confirmHint       = _drawerContainer.Q<Label>("confirm-hint");
            _telemetrySwitch   = _drawerContainer.Q<VisualElement>("telemetry-switch");
            _telemetryLabel    = _drawerContainer.Q<Label>("telemetry-label");
            _telemetryHint     = _drawerContainer.Q<Label>("telemetry-hint");
            _summaryTruncateSwitch = _drawerContainer.Q<VisualElement>("summary-truncate-switch");
            _summaryTruncateLabel  = _drawerContainer.Q<Label>("summary-truncate-label");
            _summaryTruncateHint   = _drawerContainer.Q<Label>("summary-truncate-hint");
            _surfaceProfileLabel    = _drawerContainer.Q<Label>("surface-profile-label");
            _surfaceProfileDropdown = _drawerContainer.Q<DropdownField>("surface-profile-dropdown");
            _surfaceProfileHint     = _drawerContainer.Q<Label>("surface-profile-hint");

            _statsGroupTitle = _drawerContainer.Q<Label>("group-stats-title");
            _statsHint       = _drawerContainer.Q<Label>("stats-hint");
            _statsResetBtn   = _drawerContainer.Q<Button>("stats-reset-btn");
            _languagePinsTitle = _drawerContainer.Q<Label>("language-pins-title");
            _languagePinPrimary = _drawerContainer.Q<DropdownField>("language-pin-primary");
            _languagePinSecondary = _drawerContainer.Q<DropdownField>("language-pin-secondary");
        }

        private void BindEvents()
        {
            if (_closeBtn != null) _closeBtn.clicked += Close;

            // The index is looked up back into the enum via _modeOrder, to avoid depending on localized text.
            if (_modeDropdown != null)
                _modeDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = _modeDropdown.choices.IndexOf(evt.newValue);
                    if (idx < 0 || idx >= _modeOrder.Length) return;
                    var target = _modeOrder[idx];
                    if (SkillsModeManager.CurrentMode != target)
                        SkillsModeManager.CurrentMode = target; // The setter triggers OnChanged → RefreshPermissionsUi
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

            if (_agentAutoSyncToggle != null)
                _agentAutoSyncToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillInstallSyncService.Enabled)
                        SkillInstallSyncService.Enabled = evt.newValue;
                });

            if (_cliOpenBtn != null)
                _cliOpenBtn.clicked += () => UnityCliWindow.ShowWindow();

            if (_autoStartToggle != null)
                _autoStartToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsHttpServer.AutoStart)
                        SkillsHttpServer.AutoStart = evt.newValue;
                });

            if (_startOnLaunchToggle != null)
                _startOnLaunchToggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue != SkillsHttpServer.StartOnEditorLaunch)
                        SkillsHttpServer.StartOnEditorLaunch = evt.newValue;
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

            if (_updateNotificationsSwitch != null)
                _updateNotificationsSwitch.RegisterCallback<ClickEvent>(_ =>
                {
                    VersionCheckService.NotificationsEnabled =
                        !VersionCheckService.NotificationsEnabled;
                    SyncSettingSwitches();
                });

            if (_confirmSwitch != null)
                _confirmSwitch.RegisterCallback<ClickEvent>(_ =>
                {
                    ConfirmationTokenService.RequireConfirmation = !ConfirmationTokenService.RequireConfirmation;
                    SyncSettingSwitches();
                });

            if (_telemetrySwitch != null)
                _telemetrySwitch.RegisterCallback<ClickEvent>(_ =>
                {
                    SkillTelemetryService.Enabled = !SkillTelemetryService.Enabled;
                    SyncSettingSwitches();
                });

            if (_summaryTruncateSwitch != null)
                _summaryTruncateSwitch.RegisterCallback<ClickEvent>(_ =>
                {
                    SkillRouter.SummaryAutoTruncate = !SkillRouter.SummaryAutoTruncate;
                    SyncSettingSwitches();
                });

            // Index is resolved back to the enum through _profileOrder. Writing
            // SkillsSurfaceProfile.Current makes its setter raise OnChanged, which reaches
            // RefreshSurfaceProfileUi and repaints the row, so no manual sync is needed here.
            if (_surfaceProfileDropdown != null)
                _surfaceProfileDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = _surfaceProfileDropdown.choices.IndexOf(evt.newValue);
                    if (idx < 0 || idx >= _profileOrder.Length) return;
                    var target = _profileOrder[idx];
                    if (SkillsSurfaceProfile.Current != target)
                        SkillsSurfaceProfile.Current = target;
                });

            if (_statsResetBtn != null)
                _statsResetBtn.clicked += () =>
                {
                    SkillsHttpServer.ResetStatistics();
                };

            if (_languagePinPrimary != null)
                _languagePinPrimary.RegisterValueChangedCallback(evt =>
                    SkillsLocalization.PinnedPrimary = ParseLanguage(evt.newValue));
            if (_languagePinSecondary != null)
                _languagePinSecondary.RegisterValueChangedCallback(evt =>
                    SkillsLocalization.PinnedSecondary = ParseLanguage(evt.newValue));
        }

        private void InitializeValues()
        {
            // The dropdown's choices use the short English mode terms; not localized (matching Claude Code's docs).
            // RefreshPermissionsUi is responsible for the SetValue based on the current mode.
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
                    SkillsLocalization.Get("loglevel_off"),
                    SkillsLocalization.Get("loglevel_error"),
                    SkillsLocalization.Get("loglevel_warning"),
                    SkillsLocalization.Get("loglevel_info"),
                    SkillsLocalization.Get("loglevel_agent"),
                    SkillsLocalization.Get("loglevel_verbose")
                };
                int lvl = (int)SkillsLogger.Level;
                if (lvl < 0 || lvl >= _logDropdown.choices.Count) lvl = 0;
                _logDropdown.value = _logDropdown.choices[lvl];
            }

            if (_autoStartToggle != null) _autoStartToggle.value = SkillsHttpServer.AutoStart;
            if (_startOnLaunchToggle != null) _startOnLaunchToggle.value = SkillsHttpServer.StartOnEditorLaunch;
            if (_agentAutoSyncToggle != null) _agentAutoSyncToggle.value = SkillInstallSyncService.Enabled;
            if (_timeoutField   != null) _timeoutField.value     = SkillsHttpServer.RequestTimeoutMinutes;
            if (_keepaliveField != null) _keepaliveField.value   = SkillsHttpServer.KeepAliveIntervalSeconds;
            SyncSettingSwitches();
            RebuildSurfaceProfileDropdown();
            RefreshLanguagePins();
        }

        public void Open()
        {
            // Rebuild the Shortcuts row on every open, pulling the latest bindings (to reflect
            // changes made outside via Edit ▸ Shortcuts).
            _shortcutsController?.Refresh();
            // The binding state may have just changed in UnityCliWindow, so fetch the latest when opening the drawer.
            RefreshCliGroup();

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

            // Permissions group
            if (_permGroupTitle != null)
                _permGroupTitle.text = SkillsLocalization.Get("drawer_section_permissions");

            if (_modeLabel != null)
                _modeLabel.text = SkillsLocalization.Get("perm_mode_label");
            ApplyModeHintText(SkillsModeManager.CurrentMode);

            if (_panelApprovalToggle != null)
                _panelApprovalToggle.label = SkillsLocalization.Get("perm_require_panel_approval");
            if (_panelApprovalHint != null)
                _panelApprovalHint.text = SkillsLocalization.Get("perm_require_panel_approval_hint");

            if (_allowlistClearBtn != null)
                _allowlistClearBtn.text = SkillsLocalization.Get("perm_allowlist_clear_all");
            if (_allowlistAddBtn != null)
                _allowlistAddBtn.text = SkillsLocalization.Get("perm_add_skill_btn");
            if (_viewAuditBtn != null)
                _viewAuditBtn.text = SkillsLocalization.Get("perm_view_audit_log");

            if (_agentSyncGroupTitle != null)
                _agentSyncGroupTitle.text = SkillsLocalization.Get("drawer_section_agent_sync");
            if (_agentAutoSyncToggle != null)
                _agentAutoSyncToggle.label = SkillsLocalization.Get("agent_autosync_label");
            if (_agentAutoSyncHint != null)
                _agentAutoSyncHint.text = SkillsLocalization.Get("agent_autosync_hint");

            RefreshCliGroup();

            // Pending / Allowlist titles include counts, so rebuild via RefreshPermissionsUi
            // to pick up the new language strings together with the live data.
            RefreshPermissionsUi();

            if (_serverGroupTitle  != null) _serverGroupTitle.text  = SkillsLocalization.Get("drawer_section_server");
            if (_runtimeGroupTitle != null) _runtimeGroupTitle.text = SkillsLocalization.Get("drawer_section_runtime");
            if (_statsGroupTitle   != null) _statsGroupTitle.text   = SkillsLocalization.Get("drawer_section_stats");

            if (_autoStartToggle != null) _autoStartToggle.label = SkillsLocalization.Get("auto_restart");
            if (_autoStartHint   != null) _autoStartHint.text    = SkillsLocalization.Get("auto_restart_hint");
            if (_startOnLaunchToggle != null) _startOnLaunchToggle.label = SkillsLocalization.Get("start_on_editor_launch");
            if (_startOnLaunchHint   != null) _startOnLaunchHint.text    = SkillsLocalization.Get("start_on_editor_launch_hint");

            if (_portLabel       != null) _portLabel.text     = SkillsLocalization.Get("drawer_port_label");
            if (_timeoutLabel    != null) _timeoutLabel.text  = SkillsLocalization.Get("drawer_timeout_label");
            if (_timeoutUnit     != null) _timeoutUnit.text   = SkillsLocalization.Get("timeout_unit");
            if (_keepaliveLabel  != null) _keepaliveLabel.text = SkillsLocalization.Get("drawer_keepalive_label");
            if (_keepaliveUnit   != null) _keepaliveUnit.text  = SkillsLocalization.Get("keepalive_unit");
            if (_keepaliveHint   != null) _keepaliveHint.text  = SkillsLocalization.Get("keepalive_hint");

            if (_loglevelLabel != null) _loglevelLabel.text = SkillsLocalization.Get("drawer_loglevel_label");
            if (_updateNotificationsLabel != null)
                _updateNotificationsLabel.text = SkillsLocalization.Get("drawer_update_notifications_label");
            if (_updateNotificationsHint != null)
                _updateNotificationsHint.text = SkillsLocalization.Get("drawer_update_notifications_hint");
            if (_confirmLabel != null) _confirmLabel.text = SkillsLocalization.Get("drawer_confirm_label");
            if (_confirmHint   != null)
            {
                _confirmHint.text = SkillsLocalization.Get("drawer_confirm_hint");
            }

            if (_telemetryLabel != null)
                _telemetryLabel.text = SkillsLocalization.Get("drawer_telemetry_label");
            if (_telemetryHint != null)
                _telemetryHint.text = SkillsLocalization.Get("drawer_telemetry_hint");

            if (_summaryTruncateLabel != null)
                _summaryTruncateLabel.text = SkillsLocalization.Get("drawer_summary_truncate_label");
            if (_summaryTruncateHint != null)
                _summaryTruncateHint.text = SkillsLocalization.Get("drawer_summary_truncate_hint");

            if (_surfaceProfileLabel != null)
            {
                _surfaceProfileLabel.text    = SkillsLocalization.Get("surface_profile");
                _surfaceProfileLabel.tooltip = SkillsLocalization.Get("surface_profile_tooltip");
            }
            if (_surfaceProfileDropdown != null)
                _surfaceProfileDropdown.tooltip = SkillsLocalization.Get("surface_profile_tooltip");
            RebuildSurfaceProfileDropdown();

            if (_statsHint     != null) _statsHint.text     = SkillsLocalization.Get("drawer_stats_hint");
            if (_statsResetBtn != null) _statsResetBtn.text = SkillsLocalization.Get("drawer_reset_stats_btn");
            if (_languagePinsTitle != null) _languagePinsTitle.text = SkillsLocalization.Get("language_pins_title");
            RefreshLanguagePins();

            _shortcutsController?.RefreshLocalization();
        }

        private void RefreshLanguagePins()
        {
            var choices = new List<string> { "English", "Chinese", "Russian" };
            if (_languagePinPrimary != null)
            {
                _languagePinPrimary.choices = choices;
                _languagePinPrimary.SetValueWithoutNotify(SkillsLocalization.PinnedPrimary.ToString());
            }
            if (_languagePinSecondary != null)
            {
                _languagePinSecondary.choices = choices;
                _languagePinSecondary.SetValueWithoutNotify(SkillsLocalization.PinnedSecondary.ToString());
            }
        }

        private void SyncSettingSwitches()
        {
            _updateNotificationsSwitch?.EnableInClassList(
                "on", VersionCheckService.NotificationsEnabled);
            _confirmSwitch?.EnableInClassList("on", ConfirmationTokenService.RequireConfirmation);
            _telemetrySwitch?.EnableInClassList("on", SkillTelemetryService.Enabled);
            _summaryTruncateSwitch?.EnableInClassList("on", SkillRouter.SummaryAutoTruncate);
        }

        private static SkillsLocalization.Language ParseLanguage(string value) =>
            (SkillsLocalization.Language)Enum.Parse(typeof(SkillsLocalization.Language), value);

        // ===== Surface profile (skill surface) =====

        /// <summary>
        /// Fills the localized option names in <see cref="_profileOrder"/> order, then writes back
        /// the current profile and its description. Rebuilt on language change, because choices
        /// hold display text.
        /// </summary>
        private void RebuildSurfaceProfileDropdown()
        {
            if (_surfaceProfileDropdown == null) return;
            _surfaceProfileDropdown.choices = new List<string>
            {
                SkillsLocalization.Get("surface_profile_full"),
                SkillsLocalization.Get("surface_profile_guide"),
                SkillsLocalization.Get("surface_profile_no_scene_authoring"),
            };
            RefreshSurfaceProfileUi();
        }

        /// <summary>
        /// Writes the current profile back into the dropdown and recomputes the hint. This is also
        /// the <see cref="SkillsSurfaceProfile.OnChanged"/> handler, which is why the value must go
        /// in via SetValueWithoutNotify — otherwise it and its own ValueChanged callback would
        /// trigger each other.
        /// </summary>
        private void RefreshSurfaceProfileUi()
        {
            if (_surfaceProfileDropdown != null)
            {
                int idx = Array.IndexOf(_profileOrder, SkillsSurfaceProfile.Current);
                if (idx >= 0 && idx < _surfaceProfileDropdown.choices.Count)
                    _surfaceProfileDropdown.SetValueWithoutNotify(_surfaceProfileDropdown.choices[idx]);
            }
            ApplySurfaceProfileHintText();
        }

        private void ApplySurfaceProfileHintText()
        {
            if (_surfaceProfileHint == null) return;

            var profile = SkillsSurfaceProfile.Current;
            var stats = MeasureHiddenSurface();
            string text;
            switch (profile)
            {
                case SurfaceProfileKind.Guide:
                    text = string.Format(
                        SkillsLocalization.Get("surface_profile_guide_hint"),
                        stats.IsKnown ? string.Join(" / ", stats.Modules) : FallbackModuleList(profile));
                    break;
                case SurfaceProfileKind.NoSceneAuthoring:
                    // This profile covers too many modules to list without filling the drawer, so
                    // it gets prose plus the measured count appended below.
                    text = SkillsLocalization.Get("surface_profile_no_scene_authoring_hint");
                    break;
                default:
                    text = SkillsLocalization.Get("surface_profile_full_hint");
                    break;
            }

            if (stats.IsKnown && stats.Writes > 0)
                text += " " + string.Format(
                    SkillsLocalization.Get("surface_profile_hidden_count_fmt"),
                    stats.Writes, stats.Modules.Count);

            _surfaceProfileHint.text = text;
        }

        /// <summary>
        /// What the current profile hides, measured against the registry rather than restated from
        /// the category sets. <see cref="Modules"/> is null when the measurement could not be taken.
        /// </summary>
        private readonly struct HiddenSurfaceStats
        {
            public readonly int Writes;
            public readonly List<string> Modules;
            public HiddenSurfaceStats(int writes, List<string> modules) { Writes = writes; Modules = modules; }
            public bool IsKnown => Modules != null;
        }

        /// <summary>
        /// Counts the hidden writes and collects the modules they belong to in one pass over the
        /// unfiltered registry, asking <see cref="SkillsSurfaceProfile.IsExcluded(SkillRouter.SkillInfo)"/>
        /// about each skill.
        ///
        /// Deriving both numbers from the same verdict the router enforces is the entire point.
        /// Neither can be read off the category sets any more: escape-hatch skills are hidden by
        /// name under every non-full profile, and NoSceneAuthoring additionally hides any write
        /// declaring MutatesScene whatever its module — so <c>HiddenCategories</c> understates both
        /// the module list and the count. The category-only IsExcluded overload documents the same
        /// caveat and is deliberately not used here.
        ///
        /// Returns the default (IsKnown false) when the registry cannot be read, and the caller
        /// then drops the count sentence rather than printing a wrong number.
        /// </summary>
        private static HiddenSurfaceStats MeasureHiddenSurface()
        {
            if (SkillsSurfaceProfile.IsFull) return default;
            try
            {
                var all = SkillRouter.GetAllSkillsSnapshotUnfiltered();
                if (all == null) return default;

                int writes = 0;
                var modules = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var skill in all)
                {
                    if (skill == null || !SkillsSurfaceProfile.IsExcluded(skill)) continue;
                    writes++;
                    modules.Add(skill.Category.ToString());
                }
                return new HiddenSurfaceStats(writes, modules.ToList());
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// Module list used only when the registry could not be measured. Reads the category set
        /// directly, which understates what is hidden but beats leaving the sentence blank.
        /// <see cref="SkillsSurfaceProfile.HiddenCategories"/> hands back a reference to an internal
        /// HashSet, so this only enumerates it — never mutate it in place.
        /// </summary>
        private static string FallbackModuleList(SurfaceProfileKind profile)
        {
            var categories = SkillsSurfaceProfile.HiddenCategories(profile);
            if (categories == null || categories.Count == 0) return string.Empty;
            return string.Join(" / ", categories
                .Select(c => c.ToString())
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
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
                    _modeHint.text = SkillsLocalization.Get("perm_mode_approval_hint");
                    break;
                case SkillsOperatingMode.Auto:
                    _modeHint.text = SkillsLocalization.Get("perm_mode_auto_hint");
                    break;
                case SkillsOperatingMode.Bypass:
                    _modeHint.text = SkillsLocalization.Get("perm_mode_bypass_hint");
                    break;
                default:
                    _modeHint.text = string.Empty;
                    break;
            }
        }

        /// <summary>
        /// Unity CLI group: title/button text + binding-status hint. Binding happens in
        /// UnityCliWindow; the drawer only needs to fetch the latest state once per localization
        /// refresh (including Open) — no polling needed.
        /// </summary>
        private void RefreshCliGroup()
        {
            if (_cliGroupTitle != null)
                _cliGroupTitle.text = SkillsLocalization.Get("cli_group_title");
            if (_cliOpenBtn != null)
            {
                _cliOpenBtn.text = SkillsLocalization.Get("cli_setup_entry");
                _cliOpenBtn.tooltip = SkillsLocalization.Get("cli_setup_entry_tip");
            }
            if (_cliHint != null)
            {
                _cliHint.text = UnityCliService.IsBound
                    ? SkillsLocalization.Get("cli_drawer_hint_bound")
                    : SkillsLocalization.Get("cli_drawer_hint_unbound");
            }
        }

        /// <summary>
        /// Syncs the three categories of permission UI: mode toggles, the Approval settings row,
        /// and the Pending/Granted lists.
        /// Called by the OnChanged event, this class's initialization, and localization switches.
        /// </summary>
        private void RefreshPermissionsUi()
        {
            if (_drawerContainer == null) return;
            var mode = SkillsModeManager.CurrentMode;

            // 1) Sync the dropdown to the current mode + refresh the hint
            SyncModeDropdownValue(mode);
            ApplyModeHintText(mode);

            // 2) The Panel Approval row is only visible in Approval mode
            SetDisplay(_panelApprovalRow, mode == SkillsOperatingMode.Approval);
            if (_panelApprovalToggle != null)
                _panelApprovalToggle.SetValueWithoutNotify(SkillsModeManager.PanelApprovalRequired);

            // 3) The Pending list — shown only in Approval mode + when there are pending items
            var pending = SkillsModeManager.PendingGrantRequests;
            bool showPending = mode == SkillsOperatingMode.Approval && pending.Count > 0;
            SetDisplay(_pendingSection, showPending);
            if (showPending)
            {
                if (_pendingTitle != null)
                    _pendingTitle.text = string.Format(
                        SkillsLocalization.Get("perm_pending_requests_fmt"),
                        pending.Count);
                RebuildPendingList(pending);
            }
            else if (_pendingList != null)
            {
                _pendingList.Clear();
            }

            // 4) The Allowlist list — shown in Approval/Auto (hidden in Bypass)
            var allowlist = SkillsModeManager.AllowlistSkills;
            bool showAllowlist = mode != SkillsOperatingMode.Bypass;
            SetDisplay(_allowlistSection, showAllowlist);
            if (showAllowlist)
            {
                if (_allowlistFoldout != null)
                    _allowlistFoldout.text = string.Format(
                        SkillsLocalization.Get("perm_allowlist_skills_fmt"),
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

            // Channel-specific feedback: the panel channel goes through the panel's Approve; the
            // dialog channel's approval happens in the AI chat, the panel button doesn't apply
            // there, so give a clear pointer instead
            if (isPanel && req.ApprovedByPanel)
            {
                var status = new Label(SkillsLocalization.Get("perm_approved_waiting"));
                status.AddToClassList("setting-hint");
                status.style.marginBottom = 2;
                card.Add(status);
            }
            else if (!isPanel)
            {
                var chatHint = new Label(SkillsLocalization.Get("perm_approve_in_chat"));
                chatHint.AddToClassList("setting-hint");
                chatHint.style.marginBottom = 2;
                card.Add(chatHint);
            }

            var actions = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 2 } };
            var approveBtn = new Button(() => SkillsModeManager.Approve(req.Token))
            {
                text = SkillsLocalization.Get("perm_approve")
            };
            approveBtn.AddToClassList("mini-btn");
            approveBtn.style.marginRight = 4;
            approveBtn.SetEnabled(isPanel && !req.ApprovedByPanel); // Clickable only when the panel channel hasn't approved yet
            actions.Add(approveBtn);

            var denyBtn = new Button(() => SkillsModeManager.Deny(req.Token))
            {
                text = SkillsLocalization.Get("perm_deny")
            };
            denyBtn.AddToClassList("mini-btn");
            denyBtn.AddToClassList("danger");
            actions.Add(denyBtn);

            card.Add(actions);
            return card;
        }

        /// <summary>
        /// Opens AllowlistPickerWindow — supports search, checkbox selection grouped by
        /// Category, select-all-in-group, and merges the high-risk confirmation on submit. The
        /// window handles calling AddToAllowlist itself; this controller auto-refreshes the list
        /// on the OnChanged chain.
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
                var empty = new Label(SkillsLocalization.Get("perm_no_allowlist"));
                empty.AddToClassList("setting-hint");
                _allowlistList.Add(empty);
                return;
            }

            // Resolve name → Category using the SkillRouter snapshot; an unregistered skill (e.g.
            // during the registry's refresh interval) is grouped into the special "(Unknown)"
            // bucket rather than dropped, so the user can at least see it and Remove it.
            // The unfiltered snapshot is required here: an allowlist can hold skill names the
            // current profile hides (switching profile does not clear the allowlist), and the
            // filtered snapshot would drop every one of them into "(Unknown)", leaving the user
            // unable to tell which module an entry belongs to.
            var nameToCategory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var s in SkillRouter.GetAllSkillsSnapshotUnfiltered() ?? Array.Empty<SkillRouter.SkillInfo>())
                {
                    if (s != null && !string.IsNullOrEmpty(s.Name))
                        nameToCategory[s.Name] = s.Category.ToString();
                }
            }
            catch { /* If the snapshot fails, group everything into Unknown */ }

            var grouped = allowlist
                .GroupBy(n => nameToCategory.TryGetValue(n, out var c) ? c : "(Unknown)")
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                var items = group.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var foldout = new Foldout
                {
                    text = $"{group.Key}  ({items.Count})",
                    value = false, // Collapsed by default to save space; the user expands it to view
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
                text = SkillsLocalization.Get("perm_remove_from_allowlist")
            };
            removeBtn.AddToClassList("mini-btn");
            row.Add(removeBtn);
            return row;
        }

        /// <summary>
        /// Runs once per second: first compares a pending+granted snapshot to decide whether the
        /// list needs rebuilding, otherwise just refreshes the countdown.
        /// If the OnChanged event chain is ever lost (background window, cross-domain calls,
        /// etc.), this polling is the fallback.
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

        /// <summary>
        /// Scans the pending list's expires Labels once per second, recomputing the text from
        /// the UTC expiry time stored in userData.
        /// Doesn't rebuild the entries, to avoid disrupting any hover/focus in progress; once
        /// expiry reaches 0, the next OnChanged clears the entry.
        /// </summary>
        private void RefreshPendingExpiry()
        {
            if (_pendingList == null) return;
            // Skip when there's nothing pending — avoids iterating an empty list every second.
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
