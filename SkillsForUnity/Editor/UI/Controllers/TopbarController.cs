using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Persistent topbar controller — status dot, URL pill, server toggle
    /// switch, status text, permission mode badge, settings (gear) button.
    /// Owned by UnitySkillsWindow; bound to elements that live in the main UXML.
    /// </summary>
    public class TopbarController
    {
        private const float CompactTopbarWidth = 380f;
        private const float NarrowTopbarWidth = 300f;
        private const long SelfHealIntervalMs = 500;

        private enum TopbarLayoutState
        {
            Unset,
            Normal,
            Compact,
            Narrow
        }

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;
        private VisualElement _topbarElement;
        private VisualElement _statusDot;
        private TextField     _urlField;
        private Button        _copyBtn;
        private VisualElement _serverSwitch;
        private Label         _statusText;
        private Button        _permBadge;
        private Label         _permBadgeLabel;
        private Button        _settingsBtn;

        private bool? _lastRunning;
        private TopbarLayoutState _layoutState = TopbarLayoutState.Unset;

        public TopbarController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;
            _topbarElement = _root.Q<VisualElement>("topbar");
            _statusDot    = _root.Q<VisualElement>("status-dot");
            _urlField     = _root.Q<TextField>("url-field");
            _copyBtn      = _root.Q<Button>("url-copy-btn");
            _serverSwitch = _root.Q<VisualElement>("server-switch");
            _statusText   = _root.Q<Label>("server-status-text");
            _permBadge    = _root.Q<Button>("perm-mode-badge");
            _settingsBtn  = _root.Q<Button>("open-settings-btn");

            BuildPermBadgeContent();
            ApplySettingsIcon();
            BindEvents();
            UpdateLiveData(); // initial paint

            // 权限模式变化不等 500ms 主 tick — 立刻刷新徽章文字 / 待批计数。
            SkillsModeManager.OnChanged += UpdateLiveData;
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
            if (_topbarElement != null)
            {
                _topbarElement.RegisterCallback<GeometryChangedEvent>(OnTopbarGeometryChanged);
                // 兜底自愈：Unity 6 双击最大化会让窗口 detach→attach 并在同帧多次 layout，
                // GeometryChangedEvent 可能漏派发"最终尺寸"那次，使响应式卡在窄布局。
                // 低频轮询真实 layout 宽度重算——schedule 挂在元素上，detach 自动暂停、
                // attach 自动恢复，不依赖事件派发；ApplyResponsiveLayout 内有状态早退，重复调用零副作用。
                // 经 EditorUiScheduler.RepeatSafe 把 class 变更推迟到 delayCall，避免落在
                // repaint/generateVisualContent 期间触发 InvalidOperationException（issue #44）。
                EditorUiScheduler.RepeatSafe(_topbarElement, SelfHealIntervalMs, SelfHealResponsiveLayout);
            }
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            // 仅退订静态事件，防止 window 关闭后 TopbarController 无法回收 / 回调打到已销毁 UI。
            // GeometryChangedEvent 挂在 _topbarElement 自身，元素销毁会自动清理——
            // 这里绝不能退订它，否则 maximize 引发的 detach→attach 会让响应式永久失效。
            SkillsModeManager.OnChanged -= UpdateLiveData;
        }

        private void OnTopbarGeometryChanged(GeometryChangedEvent evt)
        {
            ApplyResponsiveLayout(evt.newRect.width);
        }

        // 周期兜底：直接读取 topbar 当前 layout 宽度重算断点，覆盖 GeometryChangedEvent 漏派发的情况。
        private void SelfHealResponsiveLayout()
        {
            if (_topbarElement == null) return;
            float width = _topbarElement.layout.width;
            if (width > 0f && !float.IsNaN(width))
                ApplyResponsiveLayout(width);
        }

        private void ApplyResponsiveLayout(float width)
        {
            if (_topbarElement == null || width <= 0f || float.IsNaN(width)) return;

            TopbarLayoutState nextState;
            if (width < NarrowTopbarWidth)
                nextState = TopbarLayoutState.Narrow;
            else if (width < CompactTopbarWidth)
                nextState = TopbarLayoutState.Compact;
            else
                nextState = TopbarLayoutState.Normal;

            if (_layoutState == nextState) return;
            _layoutState = nextState;

            _topbarElement.RemoveFromClassList("topbar--compact");
            _topbarElement.RemoveFromClassList("topbar--narrow");

            if (nextState == TopbarLayoutState.Compact)
                _topbarElement.AddToClassList("topbar--compact");
            else if (nextState == TopbarLayoutState.Narrow)
                _topbarElement.AddToClassList("topbar--narrow");
        }

        /// <summary>
        /// Apply Unity's built-in Settings icon.
        /// Tried in order: d_SettingsIcon, SettingsIcon, _Popup. The last one
        /// always exists as a final fallback.
        /// </summary>
        private void ApplySettingsIcon()
        {
            UISkillsEditorIcons.Apply(_settingsBtn, "d_SettingsIcon", "SettingsIcon", "_Popup");
        }

        private void BuildPermBadgeContent()
        {
            if (_permBadge == null) return;

            _permBadge.text = "";
            _permBadge.Clear();
            _permBadge.AddToClassList("perm-mode-badge--fallback");

            var dot = new VisualElement { name = "perm-mode-badge-dot" };
            dot.AddToClassList("perm-mode-badge__dot");
            _permBadge.Add(dot);

            _permBadgeLabel = new Label { name = "perm-mode-badge-label" };
            _permBadgeLabel.AddToClassList("perm-mode-badge__label");
            _permBadge.Add(_permBadgeLabel);
        }

        private void BindEvents()
        {
            if (_copyBtn != null)
            {
                _copyBtn.clicked += () =>
                {
                    if (!string.IsNullOrEmpty(SkillsHttpServer.Url))
                        EditorGUIUtility.systemCopyBuffer = SkillsHttpServer.Url;
                };
            }

            if (_settingsBtn != null)
            {
                _settingsBtn.clicked += () => _window.OpenSettings();
            }

            if (_permBadge != null)
            {
                _permBadge.clicked += ShowModeDropdownMenu;
            }

            if (_serverSwitch != null)
            {
                // Click anywhere on the switch toggles the server
                _serverSwitch.RegisterCallback<ClickEvent>(_ => ToggleServer());
            }
        }

        private void ToggleServer()
        {
            if (SkillsHttpServer.IsRunning)
                SkillsHttpServer.StopPermanent();
            else
                SkillsHttpServer.Start(SkillsHttpServer.PreferredPort);

            UpdateLiveData();
        }

        public void UpdateLiveData()
        {
            bool running = SkillsHttpServer.IsRunning;

            if (_statusDot != null)
            {
                _statusDot.RemoveFromClassList("success");
                _statusDot.RemoveFromClassList("error");
                _statusDot.AddToClassList(running ? "success" : "error");
            }

            if (_serverSwitch != null)
            {
                if (running) _serverSwitch.AddToClassList("on");
                else         _serverSwitch.RemoveFromClassList("on");
            }

            if (_statusText != null)
            {
                _statusText.text = SkillsLocalization.Get(running ? "topbar_running" : "topbar_stopped");
                _statusText.RemoveFromClassList("on");
                _statusText.RemoveFromClassList("off");
                _statusText.AddToClassList(running ? "on" : "off");
            }

            // Refresh URL only when state changes or text differs
            if (_urlField != null)
            {
                string url = running ? SkillsHttpServer.Url ?? "" : "";
                if (_urlField.value != url) _urlField.value = url;
            }

            RefreshPermBadge();

            _lastRunning = running;
        }

        /// <summary>
        /// 同步权限模式徽章的文字 + tooltip。
        /// Approval 模式下若有待批，追加 ⚠N 计数提示用户。
        /// </summary>
        private void RefreshPermBadge()
        {
            if (_permBadge == null) return;
            var mode = SkillsModeManager.CurrentMode;
            string label;

            switch (mode)
            {
                case SkillsOperatingMode.Approval:
                    int pending = SkillsModeManager.PendingGrantRequests.Count;
                    label = pending > 0 ? $"Approval {pending}" : "Approval";
                    break;
                case SkillsOperatingMode.Auto:
                    label = "Auto";
                    break;
                case SkillsOperatingMode.Bypass:
                    label = "Bypass";
                    break;
                default:
                    label = mode.ToString();
                    break;
            }

            _permBadge.RemoveFromClassList("perm-mode-badge--approval");
            _permBadge.RemoveFromClassList("perm-mode-badge--auto");
            _permBadge.RemoveFromClassList("perm-mode-badge--bypass");
            switch (mode)
            {
                case SkillsOperatingMode.Approval:
                    _permBadge.AddToClassList("perm-mode-badge--approval");
                    break;
                case SkillsOperatingMode.Auto:
                    _permBadge.AddToClassList("perm-mode-badge--auto");
                    break;
                case SkillsOperatingMode.Bypass:
                    _permBadge.AddToClassList("perm-mode-badge--bypass");
                    break;
            }

            if (_permBadgeLabel != null)
            {
                if (_permBadgeLabel.text != label) _permBadgeLabel.text = label;
            }
            else if (_permBadge.text != label)
            {
                _permBadge.text = label;
            }
        }

        /// <summary>
        /// 在徽章下方弹出 GenericMenu，三档选项 + 一项"打开权限设置…"。
        /// 当前模式打勾；点选别的会触发 SkillsModeManager.OnChanged → 整套 UI 自动刷新。
        /// </summary>
        private void ShowModeDropdownMenu()
        {
            if (_permBadge == null) return;
            var menu = new GenericMenu();
            var current = SkillsModeManager.CurrentMode;

            AddModeMenuItem(menu, SkillsOperatingMode.Approval, current,
                PermissionUiHelpers.L("perm_mode_approval_short", "Approval", "Approval（审批）"));
            AddModeMenuItem(menu, SkillsOperatingMode.Auto, current,
                PermissionUiHelpers.L("perm_mode_auto_short", "Auto", "Auto（自动）"));
            AddModeMenuItem(menu, SkillsOperatingMode.Bypass, current,
                PermissionUiHelpers.L("perm_mode_bypass_short", "Bypass", "Bypass（全部直接放行）"));

            menu.AddSeparator("");
            menu.AddItem(
                new GUIContent(PermissionUiHelpers.L("perm_open_settings_menu",
                    "Open Permission Settings…",
                    "打开权限设置…")),
                false,
                () => _window.OpenSettings());

            // worldBound 与 EditorWindow 局部坐标对齐；从徽章正下方弹出。
            menu.DropDown(_permBadge.worldBound);
        }

        private void AddModeMenuItem(GenericMenu menu, SkillsOperatingMode mode,
                                     SkillsOperatingMode current, string label)
        {
            menu.AddItem(new GUIContent(label), mode == current, () =>
            {
                if (SkillsModeManager.CurrentMode != mode)
                    SkillsModeManager.CurrentMode = mode;
            });
        }

        public void RefreshLocalization()
        {
            if (_copyBtn != null)     _copyBtn.text     = SkillsLocalization.Get("topbar_copy_url");
            if (_settingsBtn != null) _settingsBtn.tooltip = SkillsLocalization.Get("topbar_settings_tooltip");
            if (_serverSwitch != null) _serverSwitch.tooltip = SkillsLocalization.Get("topbar_server_tooltip");
            if (_permBadge != null)
                _permBadge.tooltip = PermissionUiHelpers.L("topbar_perm_badge_tooltip",
                    "Click to switch operating mode",
                    "点击切换运行模式");

            // Force re-render running/stopped text in current language
            UpdateLiveData();
        }
    }
}

// Producer:Betsy
