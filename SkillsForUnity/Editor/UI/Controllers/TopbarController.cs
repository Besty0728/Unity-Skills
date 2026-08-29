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

            // Permission mode changes can't wait for the 500ms main tick -- refresh the badge text / pending count immediately.
            SkillsModeManager.OnChanged += UpdateLiveData;
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
            if (_topbarElement != null)
            {
                _topbarElement.RegisterCallback<GeometryChangedEvent>(OnTopbarGeometryChanged);
                // Self-healing fallback: on Unity 6, double-clicking maximize causes the window to detach->attach
                // and lay out multiple times in the same frame; GeometryChangedEvent can miss dispatching the
                // "final size" pass, leaving the responsive layout stuck narrow. Deferring the class change to
                // delayCall via EditorUiScheduler.RepeatSafe avoids triggering an InvalidOperationException during repaint/generateVisualContent (issue #44).
                EditorUiScheduler.RepeatSafe(_topbarElement, SelfHealIntervalMs, SelfHealResponsiveLayout);
            }
        }

        private void OnRootDetached(DetachFromPanelEvent _)
        {
            // Only unsubscribe the static event, so a closed window doesn't leave TopbarController uncollectable / callbacks hitting destroyed UI.
            // GeometryChangedEvent is attached to _topbarElement itself, and gets cleaned up automatically when the element is destroyed -- it must never be unsubscribed here,
            // or the detach->attach caused by maximize would permanently break the responsive layout.
            SkillsModeManager.OnChanged -= UpdateLiveData;
        }

        private void OnTopbarGeometryChanged(GeometryChangedEvent evt)
        {
            ApplyResponsiveLayout(evt.newRect.width);
        }

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

            if (_urlField != null)
            {
                string url = running ? SkillsHttpServer.Url ?? "" : "";
                if (_urlField.value != url) _urlField.value = url;
            }

            RefreshPermBadge();

            _lastRunning = running;
        }

        /// <summary>
        /// Syncs the permission mode badge's text + tooltip.
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
        /// Pops up a GenericMenu below the badge: three mode options plus an "Open permission settings..." item.
        /// The current mode is checked; picking another one triggers SkillsModeManager.OnChanged -> the whole UI refreshes automatically.
        /// </summary>
        private void ShowModeDropdownMenu()
        {
            if (_permBadge == null) return;
            var menu = new GenericMenu();
            var current = SkillsModeManager.CurrentMode;

            AddModeMenuItem(menu, SkillsOperatingMode.Approval, current,
                SkillsLocalization.Get("perm_mode_approval_short"));
            AddModeMenuItem(menu, SkillsOperatingMode.Auto, current,
                SkillsLocalization.Get("perm_mode_auto_short"));
            AddModeMenuItem(menu, SkillsOperatingMode.Bypass, current,
                SkillsLocalization.Get("perm_mode_bypass_short"));

            menu.AddSeparator("");
            menu.AddItem(
                new GUIContent(SkillsLocalization.Get("perm_open_settings_menu")),
                false,
                () => _window.OpenSettings());

            // worldBound aligns with EditorWindow local coordinates; pops up right below the badge.
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
                _permBadge.tooltip = SkillsLocalization.Get("topbar_perm_badge_tooltip");

            // Force re-render running/stopped text in current language
            UpdateLiveData();
        }
    }
}

// Producer:Betsy
