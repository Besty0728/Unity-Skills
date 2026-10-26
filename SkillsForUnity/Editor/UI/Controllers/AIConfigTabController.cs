using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// AI Config Tab — one card per supported Agent (Claude Code / Codex /
    /// Antigravity / Cursor / OpenCode / Kimi Code) plus a Custom Agent card.
    /// Cards are built dynamically so adding a new agent only requires one
    /// entry in _agentConfigs.
    /// </summary>
    public class AIConfigTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/AIConfigTab.uxml";
        private const string IconsFolder = "Packages/com.besty.unity-skills/Editor/UI/Icons";

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private VisualElement _agentsContainer;
        private HelpBox       _helpBox;

        // ----- Per-agent metadata -----
        private class AgentConfig
        {
            public string id;          // Used for icon file lookup (icons/<id>.png)
            public string brandClass;  // CSS class on the icon container
            public string nameDisplay;
            public Func<bool> isProjInstalled;
            public Func<bool> isGlobInstalled;
            public Func<bool, string> getPath;   // global → install directory, used for the version stamp check
            public Func<bool, (bool success, string message)> installFunc;
            public Func<bool, (bool success, string message)> uninstallFunc;
            public Func<bool, string, string> getInstallSuccessMsg;
        }

        private List<AgentConfig> _agentConfigs;

        // Custom agent inputs (kept across rebuilds)
        private string _customPath = "";
        private string _customName = "Custom";
        private string _customIconSource;  // Icon picked on the Custom Agent card, recorded with the next install
        private readonly List<Texture2D> _iconTextures = new List<Texture2D>();

        public AIConfigTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load AIConfigTab UXML: {TabUxmlPath}");
                return;
            }
            uxml.CloneTree(_root);

            _agentsContainer = _root.Q<VisualElement>("agents-container");
            _helpBox         = _root.Q<HelpBox>("help-box");

            SetupAgentConfigs();
            RebuildAgentsList();
        }

        private void SetupAgentConfigs()
        {
            _agentConfigs = new List<AgentConfig>
            {
                new AgentConfig
                {
                    id = "claudecode", brandClass = "brand-claudecode",
                    nameDisplay = "Claude Code",
                    isProjInstalled = () => SkillInstaller.IsClaudeProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsClaudeGlobalInstalled,
                    getPath = global => global ? SkillInstaller.ClaudeGlobalPath : SkillInstaller.ClaudeProjectPath,
                    installFunc = SkillInstaller.InstallClaude,
                    uninstallFunc = SkillInstaller.UninstallClaude
                },
                new AgentConfig
                {
                    id = "codex", brandClass = "brand-codex",
                    nameDisplay = "Codex",
                    isProjInstalled = () => SkillInstaller.IsCodexProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsCodexGlobalInstalled,
                    getPath = global => global ? SkillInstaller.CodexGlobalPath : SkillInstaller.CodexProjectPath,
                    installFunc = SkillInstaller.InstallCodex,
                    uninstallFunc = SkillInstaller.UninstallCodex,
                    getInstallSuccessMsg = (global, msg) =>
                        string.Format(SkillsLocalization.Get("agent_codex_install_success_fmt"), msg)
                        + (global ? "" : SkillsLocalization.Get("agent_codex_install_note"))
                },
                new AgentConfig
                {
                    id = "antigravity", brandClass = "brand-antigravity",
                    nameDisplay = "Antigravity",
                    isProjInstalled = () => SkillInstaller.IsAntigravityProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsAntigravityGlobalInstalled,
                    getPath = global => global ? SkillInstaller.AntigravityGlobalPath : SkillInstaller.AntigravityProjectPath,
                    installFunc = SkillInstaller.InstallAntigravity,
                    uninstallFunc = SkillInstaller.UninstallAntigravity
                },
                new AgentConfig
                {
                    id = "cursor", brandClass = "brand-cursor",
                    nameDisplay = "Cursor",
                    isProjInstalled = () => SkillInstaller.IsCursorProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsCursorGlobalInstalled,
                    getPath = global => global ? SkillInstaller.CursorGlobalPath : SkillInstaller.CursorProjectPath,
                    installFunc = SkillInstaller.InstallCursor,
                    uninstallFunc = SkillInstaller.UninstallCursor
                },
                new AgentConfig
                {
                    id = "opencode", brandClass = "brand-opencode",
                    nameDisplay = "OpenCode",
                    isProjInstalled = () => SkillInstaller.IsOpenCodeProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsOpenCodeGlobalInstalled,
                    getPath = global => global ? SkillInstaller.OpenCodeGlobalPath : SkillInstaller.OpenCodeProjectPath,
                    installFunc = SkillInstaller.InstallOpenCode,
                    uninstallFunc = SkillInstaller.UninstallOpenCode
                },
                new AgentConfig
                {
                    id = "kimicode", brandClass = "brand-kimicode",
                    nameDisplay = "Kimi Code",
                    isProjInstalled = () => SkillInstaller.IsKimiCodeProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsKimiCodeGlobalInstalled,
                    getPath = global => global ? SkillInstaller.KimiCodeGlobalPath : SkillInstaller.KimiCodeProjectPath,
                    installFunc = SkillInstaller.InstallKimiCode,
                    uninstallFunc = SkillInstaller.UninstallKimiCode
                }
            };
        }

        private void RebuildAgentsList()
        {
            if (_agentsContainer == null) return;
            _agentsContainer.Clear();
            foreach (var texture in _iconTextures)
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            _iconTextures.Clear();

            foreach (var cfg in _agentConfigs)
                _agentsContainer.Add(BuildAgentCard(cfg));

            foreach (var target in SkillInstaller.ReadCustomTargets())
                _agentsContainer.Add(BuildRememberedCustomCard(target));

            _agentsContainer.Add(BuildCustomAgentCard());
        }

        private VisualElement BuildAgentCard(AgentConfig cfg)
        {
            bool projInstalled = cfg.isProjInstalled();
            bool globInstalled = cfg.isGlobInstalled();
            bool anyInstalled = projInstalled || globInstalled;

            var card = new VisualElement();
            card.AddToClassList("agent-card");

            var head = new VisualElement();
            head.AddToClassList("agent-card-head");
            // .agent-card-head already declares flex-direction:row + align-items:center in USS.

            var icon = new VisualElement();
            icon.AddToClassList("agent-icon");
            icon.AddToClassList(cfg.brandClass);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{IconsFolder}/{cfg.id}.png");
            if (tex != null)
            {
                // Use backgroundImage instead of Image.image — more reliable in
                // Editor windows under UI Toolkit 2022.3+. Also lets USS tint.
                icon.style.backgroundImage = new StyleBackground(tex);
                icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            }
            head.Add(icon);

            var nameLabel = new Label(cfg.nameDisplay);
            nameLabel.AddToClassList("agent-name");
            head.Add(nameLabel);

            var statusBadge = new Label(SkillsLocalization.Get(
                anyInstalled ? "agent_status_installed" : "agent_status_not_installed"));
            statusBadge.AddToClassList("agent-status");
            statusBadge.AddToClassList(anyInstalled ? "installed" : "not-installed");
            head.Add(statusBadge);

            card.Add(head);

            var actions = new VisualElement();
            actions.AddToClassList("agent-card-actions");
            // .agent-card-actions already declares flex-direction:row in USS.

            // Project button — install or update depending on state
            var projBtn = new Button(() => OnInstallClick(cfg, isGlobal: false, isUpdate: projInstalled));
            projBtn.AddToClassList("mini-btn");
            projBtn.AddToClassList(projInstalled ? "update" : "install");
            projBtn.text = SkillsLocalization.Get(projInstalled ? "agent_update_project" : "agent_install_project");
            actions.Add(projBtn);

            // Global button
            var globBtn = new Button(() => OnInstallClick(cfg, isGlobal: true, isUpdate: globInstalled));
            globBtn.AddToClassList("mini-btn");
            globBtn.AddToClassList(globInstalled ? "update" : "install");
            globBtn.text = SkillsLocalization.Get(globInstalled ? "agent_update_global" : "agent_install_global");
            actions.Add(globBtn);

            // Uninstall — single button whose label/behaviour follows install state:
            //   nothing installed → disabled "Uninstall" placeholder (keeps layout stable)
            //   only one scope    → direct uninstall of that scope
            //   both scopes       → dropdown menu with explicit per-scope items
            // Picking install-state at click time (via cfg.isXxxInstalled) protects against
            // stale captures if the user installs/uninstalls between rebuilds.
            var uninstallBtn = BuildUninstallButton(cfg);
            actions.Add(uninstallBtn);

            card.Add(actions);
            return card;
        }

        private Button BuildUninstallButton(AgentConfig cfg)
        {
            bool projInstalled = cfg.isProjInstalled();
            bool globInstalled = cfg.isGlobInstalled();

            var btn = new Button();
            btn.AddToClassList("mini-btn");
            btn.AddToClassList("uninstall");

            if (!projInstalled && !globInstalled)
            {
                btn.text = SkillsLocalization.Get("uninstall");
                btn.tooltip = btn.text;
                btn.SetEnabled(false);
                return btn;
            }

            if (projInstalled && globInstalled)
            {
                // Show a "▾" affordance so the user knows it opens a menu rather than
                // immediately wiping one scope.
                btn.text = SkillsLocalization.Get("uninstall") + " ▾";
                btn.tooltip = btn.text;
                btn.clicked += () => ShowUninstallMenu(btn, cfg);
                return btn;
            }

            // Exactly one scope installed → directly uninstall it.
            bool targetGlobal = globInstalled;
            string actionKey = targetGlobal ? "agent_uninstall_global" : "agent_uninstall_project";
            // Use a localized action label that identifies the affected scope.
            btn.text = SkillsLocalization.Get(actionKey);
            // Keep the full action name available when the equal-width layout ellipsizes it.
            btn.tooltip = btn.text;
            btn.clicked += () => OnUninstallClick(cfg, targetGlobal);
            return btn;
        }

        private void ShowUninstallMenu(Button anchor, AgentConfig cfg)
        {
            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent(SkillsLocalization.Get("agent_uninstall_project")),
                false,
                () => OnUninstallClick(cfg, isGlobal: false));
            menu.AddItem(
                new GUIContent(SkillsLocalization.Get("agent_uninstall_global")),
                false,
                () => OnUninstallClick(cfg, isGlobal: true));
            menu.DropDown(anchor.worldBound);
        }

        private VisualElement BuildCustomAgentCard()
        {
            var card = new VisualElement();
            card.AddToClassList("agent-card");

            var head = new VisualElement();
            head.AddToClassList("agent-card-head");
            // .agent-card-head already declares flex-direction:row + align-items:center in USS.

            var icon = new Label("+");
            icon.AddToClassList("agent-icon");
            icon.AddToClassList("brand-custom");
            ShowIconImage(icon, _customIconSource);
            MakeIconPickable(icon, picked =>
            {
                _customIconSource = picked;
                RebuildAgentsList();
            }, () =>
            {
                _customIconSource = null;
                RebuildAgentsList();
            });
            head.Add(icon);

            var nameLabel = new Label(SkillsLocalization.Get("agent_custom_title"));
            nameLabel.AddToClassList("agent-name");
            head.Add(nameLabel);

            card.Add(head);

            var pathRow = new VisualElement();
            pathRow.AddToClassList("setting-row");
            pathRow.AddToClassList("setting-row--gap-top");
            // .setting-row already declares flex-direction:row + align-items:center in USS.

            pathRow.Add(FieldLabel("agent_custom_path_label"));
            var pathField = new TextField();
            pathField.value = _customPath;
            pathField.AddToClassList("flex-grow");
            pathField.tooltip = SkillsLocalization.Get("agent_custom_path_placeholder");
            pathField.RegisterValueChangedCallback(e => _customPath = e.newValue ?? "");
            pathRow.Add(pathField);

            var browseBtn = new Button(() =>
            {
                string title = SkillsLocalization.Get("agent_select_install_dir");
                string p = EditorUtility.OpenFolderPanel(title, _customPath, "");
                if (!string.IsNullOrEmpty(p))
                {
                    _customPath = p;
                    pathField.value = p;
                }
            });
            browseBtn.AddToClassList("mini-btn");
            browseBtn.AddToClassList("mini-btn--inline-gap");
            browseBtn.text = SkillsLocalization.Get("agent_custom_browse");
            pathRow.Add(browseBtn);
            card.Add(pathRow);

            var nameRow = new VisualElement();
            nameRow.AddToClassList("setting-row");
            // .setting-row already declares flex-direction:row + align-items:center in USS.

            nameRow.Add(FieldLabel("agent_custom_name_label"));
            var nameInput = new TextField();
            nameInput.value = _customName;
            nameInput.AddToClassList("flex-grow");
            nameInput.tooltip = SkillsLocalization.Get("agent_custom_name_placeholder");
            nameInput.RegisterValueChangedCallback(e => _customName = e.newValue ?? "");
            nameRow.Add(nameInput);

            var installBtn = new Button(() => InstallCustom());
            installBtn.AddToClassList("mini-btn");
            installBtn.AddToClassList("install");
            installBtn.AddToClassList("mini-btn--inline-gap");
            installBtn.text = SkillsLocalization.Get("agent_custom_install");
            nameRow.Add(installBtn);
            card.Add(nameRow);

            return card;
        }

        private static Label FieldLabel(string key)
        {
            var label = new Label(SkillsLocalization.Get(key));
            label.AddToClassList("agent-field-label");
            return label;
        }

        /// <summary>A custom install remembered by SkillInstaller: update, uninstall, or drop it from the list.</summary>
        private VisualElement BuildRememberedCustomCard(SkillInstaller.CustomTarget target)
        {
            bool installed = File.Exists(Path.Combine(target.path, "SKILL.md"));
            var card = new VisualElement();
            card.AddToClassList("agent-card");

            var head = new VisualElement();
            head.AddToClassList("agent-card-head");
            var icon = new Label(IconLetter(target.name));
            icon.AddToClassList("agent-icon");
            icon.AddToClassList("brand-custom");
            ShowIconImage(icon, target.icon);
            MakeIconPickable(icon, picked => ApplyIcon(target, picked), () => ApplyIcon(target, null));
            head.Add(icon);
            var nameLabel = new Label(target.name);
            nameLabel.AddToClassList("agent-name");
            head.Add(nameLabel);
            var statusBadge = new Label(SkillsLocalization.Get(installed ? "agent_status_installed" : "agent_status_not_installed"));
            statusBadge.AddToClassList("agent-status");
            statusBadge.AddToClassList(installed ? "installed" : "not-installed");
            head.Add(statusBadge);
            card.Add(head);

            var pathLabel = new Label(target.path) { tooltip = target.path };
            pathLabel.AddToClassList("agent-path");
            card.Add(pathLabel);

            var actions = new VisualElement();
            actions.AddToClassList("agent-card-actions");
            var installBtn = new Button(() => OnRememberedInstallClick(target, installed));
            installBtn.AddToClassList("mini-btn");
            installBtn.AddToClassList(installed ? "update" : "install");
            installBtn.text = SkillsLocalization.Get(installed ? "agent_custom_update" : "agent_custom_reinstall");
            actions.Add(installBtn);

            var uninstallBtn = new Button(() => OnRememberedUninstallClick(target));
            uninstallBtn.AddToClassList("mini-btn");
            uninstallBtn.AddToClassList("uninstall");
            uninstallBtn.text = SkillsLocalization.Get("uninstall");
            uninstallBtn.SetEnabled(installed);
            actions.Add(uninstallBtn);

            var forgetBtn = new Button(() => OnForgetClick(target));
            forgetBtn.AddToClassList("mini-btn");
            forgetBtn.text = SkillsLocalization.Get("agent_custom_forget");
            actions.Add(forgetBtn);
            card.Add(actions);
            return card;
        }

        /// <summary>Paints <paramref name="file"/> into the icon square, keeping its letter when the image cannot be read.</summary>
        private void ShowIconImage(Label icon, string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
                return;
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            bool loaded;
            try { loaded = texture.LoadImage(File.ReadAllBytes(file)); }
            catch (IOException) { loaded = false; }
            if (!loaded)
            {
                UnityEngine.Object.DestroyImmediate(texture);
                return;
            }
            _iconTextures.Add(texture);
            icon.text = string.Empty;
            icon.style.backgroundImage = new StyleBackground(texture);
            icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
        }

        /// <summary>Click picks a PNG/JPG for the icon square, right-click offers resetting it to the default.</summary>
        private static void MakeIconPickable(VisualElement icon, Action<string> onPicked, Action onReset)
        {
            icon.AddToClassList("agent-icon--pickable");
            icon.tooltip = SkillsLocalization.Get("agent_custom_icon_tip");
            icon.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.button != 0) return;
                string file = EditorUtility.OpenFilePanelWithFilters(SkillsLocalization.Get("agent_custom_icon_pick_title"), "",
                    new[] { "Image", "png,jpg,jpeg" });
                if (string.IsNullOrEmpty(file)) return;
                if (SkillInstaller.CheckIconFile(file) is string error)
                {
                    EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), SkillsLocalization.Get(error), SkillsLocalization.Get("dialog_ok"));
                    return;
                }
                onPicked(file);
            });
            icon.AddManipulator(new ContextualMenuManipulator(evt =>
                evt.menu.AppendAction(SkillsLocalization.Get("agent_custom_icon_reset"), _ => onReset())));
        }

        private void ApplyIcon(SkillInstaller.CustomTarget target, string file)
        {
            if (SkillInstaller.SetCustomTargetIcon(target.path, file) is string error)
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), SkillsLocalization.Get(error), SkillsLocalization.Get("dialog_ok"));
            RebuildAgentsList();
        }

        // The icon square shows the name's first ASCII letter; other scripts may lack glyphs in the bundled UI font.
        private static string IconLetter(string name)
        {
            char first = string.IsNullOrEmpty(name) ? '?' : name.TrimStart()[0];
            return first < 128 && char.IsLetterOrDigit(first) ? char.ToUpperInvariant(first).ToString() : "?";
        }

        private void OnRememberedInstallClick(SkillInstaller.CustomTarget target, bool installed)
        {
            if (installed && ShowSkipIfInstalledCopyIsCurrentOrNewer(target.path))
                return;
            var result = SkillInstaller.InstallCustom(target.path, target.name);
            if (result.success)
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_success"), SkillsLocalization.Get("install_success"), SkillsLocalization.Get("dialog_ok"));
            else
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"),
                    string.Format(SkillsLocalization.Get(installed ? "update_failed" : "install_failed"), result.message), SkillsLocalization.Get("dialog_ok"));
            RebuildAgentsList();
        }

        private void OnRememberedUninstallClick(SkillInstaller.CustomTarget target)
        {
            if (!EditorUtility.DisplayDialog(SkillsLocalization.Get("uninstall"),
                    string.Format(SkillsLocalization.Get("uninstall_confirm"), target.name),
                    SkillsLocalization.Get("dialog_ok"), SkillsLocalization.Get("dialog_cancel")))
                return;
            var result = SkillInstaller.UninstallSkill(target.path, target.name);
            if (result.success)
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_success"), SkillsLocalization.Get("uninstall_success"), SkillsLocalization.Get("dialog_ok"));
            else
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"),
                    string.Format(SkillsLocalization.Get("uninstall_failed"), result.message), SkillsLocalization.Get("dialog_ok"));
            RebuildAgentsList();
        }

        private void OnForgetClick(SkillInstaller.CustomTarget target)
        {
            if (!EditorUtility.DisplayDialog(SkillsLocalization.Get("agent_custom_forget"),
                    string.Format(SkillsLocalization.Get("agent_custom_forget_confirm"), target.name, target.path),
                    SkillsLocalization.Get("dialog_ok"), SkillsLocalization.Get("dialog_cancel")))
                return;
            SkillInstaller.ForgetCustomTarget(target.path);
            RebuildAgentsList();
        }

        /// <summary>
        /// Same rule as the post-upgrade auto-sync: a copy already at this package version or newer (e.g. refreshed
        /// by another project on a newer package) is left alone. Returns true when the install should be skipped;
        /// forcing a reinstall means Uninstall first. Only consulted when a copy is already present.
        /// </summary>
        private static bool ShowSkipIfInstalledCopyIsCurrentOrNewer(string targetPath)
        {
            var installedVersion = SkillInstaller.ReadInstalledVersion(targetPath);
            switch (SkillInstaller.CompareInstalledVersion(installedVersion, SkillsLogger.Version))
            {
                case SkillInstaller.InstalledVersionState.Current:
                    EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_info"),
                        string.Format(SkillsLocalization.Get("agent_install_already_current"), SkillsLogger.Version),
                        SkillsLocalization.Get("dialog_ok"));
                    return true;
                case SkillInstaller.InstalledVersionState.Newer:
                    EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_info"),
                        string.Format(SkillsLocalization.Get("agent_install_newer_kept"), installedVersion.Trim(), SkillsLogger.Version),
                        SkillsLocalization.Get("dialog_ok"));
                    return true;
                default:
                    return false;
            }
        }

        private void OnInstallClick(AgentConfig cfg, bool isGlobal, bool isUpdate)
        {
            if (isUpdate && cfg.getPath != null && ShowSkipIfInstalledCopyIsCurrentOrNewer(cfg.getPath(isGlobal)))
                return;

            var result = cfg.installFunc(isGlobal);
            if (result.success)
            {
                string msg = cfg.getInstallSuccessMsg != null
                    ? cfg.getInstallSuccessMsg(isGlobal, result.message)
                    : SkillsLocalization.Get("install_success") + "\n" + result.message;
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_success"), msg, SkillsLocalization.Get("dialog_ok"));
            }
            else
            {
                string errMsg = isUpdate
                    ? string.Format(SkillsLocalization.Get("update_failed"), result.message)
                    : string.Format(SkillsLocalization.Get("install_failed"), result.message);
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), errMsg, SkillsLocalization.Get("dialog_ok"));
            }
            RebuildAgentsList();
        }

        private void OnUninstallClick(AgentConfig cfg, bool isGlobal)
        {
            string scopeText = isGlobal
                ? " (" + SkillsLocalization.Get("agent_install_global") + ")"
                : " (" + SkillsLocalization.Get("agent_install_project") + ")";

            if (!EditorUtility.DisplayDialog(
                SkillsLocalization.Get("uninstall"),
                string.Format(SkillsLocalization.Get("uninstall_confirm"), cfg.nameDisplay + scopeText),
                SkillsLocalization.Get("dialog_ok"), SkillsLocalization.Get("dialog_cancel")))
                return;

            var result = cfg.uninstallFunc(isGlobal);
            if (result.success)
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_success"), SkillsLocalization.Get("uninstall_success"), SkillsLocalization.Get("dialog_ok"));
            else
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"),
                    string.Format(SkillsLocalization.Get("uninstall_failed"), result.message), SkillsLocalization.Get("dialog_ok"));

            RebuildAgentsList();
        }

        private void InstallCustom()
        {
            if (string.IsNullOrEmpty(_customPath))
            {
                string msg = SkillsLocalization.Get("agent_path_empty");
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), msg, SkillsLocalization.Get("dialog_ok"));
                return;
            }
            if (File.Exists(Path.Combine(_customPath, "SKILL.md")) && ShowSkipIfInstalledCopyIsCurrentOrNewer(_customPath))
                return;

            var result = SkillInstaller.InstallCustom(_customPath, _customName);
            if (result.success)
            {
                SkillInstaller.RememberCustomTarget(_customName, _customPath);
                if (_customIconSource != null)
                    SkillInstaller.SetCustomTargetIcon(_customPath, _customIconSource);
                _customIconSource = null;
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_success"), SkillsLocalization.Get("install_success"), SkillsLocalization.Get("dialog_ok"));
            }
            else
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"),
                    string.Format(SkillsLocalization.Get("install_failed"), result.message), SkillsLocalization.Get("dialog_ok"));
            RebuildAgentsList();
        }

        public void RefreshLocalization()
        {
            if (_helpBox != null)
            {
                _helpBox.text = SkillsLocalization.Get("agent_config_help");
            }
            RebuildAgentsList();
        }
    }
}

// Producer:Betsy
