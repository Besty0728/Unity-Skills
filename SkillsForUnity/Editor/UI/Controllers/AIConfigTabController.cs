using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// AI Config Tab — one card per supported Agent (Claude Code / Codex /
    /// Antigravity / Cursor) plus a Custom Agent card.
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
            public Func<bool, (bool success, string message)> installFunc;
            public Func<bool, (bool success, string message)> uninstallFunc;
            public Func<bool, string, string> getInstallSuccessMsg;
        }

        private List<AgentConfig> _agentConfigs;

        // Custom agent inputs (kept across rebuilds)
        private string _customPath = "";
        private string _customName = "Custom";

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
                    installFunc = SkillInstaller.InstallClaude,
                    uninstallFunc = SkillInstaller.UninstallClaude
                },
                new AgentConfig
                {
                    id = "codex", brandClass = "brand-codex",
                    nameDisplay = "Codex",
                    isProjInstalled = () => SkillInstaller.IsCodexProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsCodexGlobalInstalled,
                    installFunc = SkillInstaller.InstallCodex,
                    uninstallFunc = SkillInstaller.UninstallCodex,
                    getInstallSuccessMsg = (global, msg) =>
                        SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                            ? "安装成功！\n" + msg + (global ? "" : "\n\n注意：Antigravity 和 Codex 工作区共享 .agents/skills 路径。")
                            : "Install success!\n" + msg + (global ? "" : "\n\nNote: Antigravity and Codex share .agents/skills in workspace mode.")
                },
                new AgentConfig
                {
                    id = "antigravity", brandClass = "brand-antigravity",
                    nameDisplay = "Antigravity",
                    isProjInstalled = () => SkillInstaller.IsAntigravityProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsAntigravityGlobalInstalled,
                    installFunc = SkillInstaller.InstallAntigravity,
                    uninstallFunc = SkillInstaller.UninstallAntigravity
                },
                new AgentConfig
                {
                    id = "cursor", brandClass = "brand-cursor",
                    nameDisplay = "Cursor",
                    isProjInstalled = () => SkillInstaller.IsCursorProjectInstalled,
                    isGlobInstalled = () => SkillInstaller.IsCursorGlobalInstalled,
                    installFunc = SkillInstaller.InstallCursor,
                    uninstallFunc = SkillInstaller.UninstallCursor
                }
            };
        }

        private void RebuildAgentsList()
        {
            if (_agentsContainer == null) return;
            _agentsContainer.Clear();

            foreach (var cfg in _agentConfigs)
                _agentsContainer.Add(BuildAgentCard(cfg));

            _agentsContainer.Add(BuildCustomAgentCard());
        }

        private VisualElement BuildAgentCard(AgentConfig cfg)
        {
            bool projInstalled = cfg.isProjInstalled();
            bool globInstalled = cfg.isGlobInstalled();
            // Show "Installed" badge if either scope has been installed.
            bool anyInstalled = projInstalled || globInstalled;

            var card = new VisualElement();
            card.AddToClassList("agent-card");

            // Head
            var head = new VisualElement();
            head.AddToClassList("agent-card-head");
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;

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

            // Actions row
            var actions = new VisualElement();
            actions.AddToClassList("agent-card-actions");
            actions.style.flexDirection = FlexDirection.Row;

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
                btn.SetEnabled(false);
                return btn;
            }

            if (projInstalled && globInstalled)
            {
                // Show a "▾" affordance so the user knows it opens a menu rather than
                // immediately wiping one scope.
                btn.text = SkillsLocalization.Get("uninstall") + " ▾";
                btn.clicked += () => ShowUninstallMenu(btn, cfg);
                return btn;
            }

            // Exactly one scope installed → directly uninstall it.
            bool targetGlobal = globInstalled;
            string scopeKey = targetGlobal ? "agent_install_global" : "agent_install_project";
            // Compose a clear label like "Uninstall Project" / "卸载 全局" so the user
            // sees which scope this single click will affect.
            btn.text = SkillsLocalization.Get("uninstall") + " " + SkillsLocalization.Get(scopeKey);
            btn.clicked += () => OnUninstallClick(cfg, targetGlobal);
            return btn;
        }

        private void ShowUninstallMenu(Button anchor, AgentConfig cfg)
        {
            var menu = new GenericMenu();
            menu.AddItem(
                new GUIContent(SkillsLocalization.Get("uninstall") + " " + SkillsLocalization.Get("agent_install_project")),
                false,
                () => OnUninstallClick(cfg, isGlobal: false));
            menu.AddItem(
                new GUIContent(SkillsLocalization.Get("uninstall") + " " + SkillsLocalization.Get("agent_install_global")),
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
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;

            var icon = new Label("+");
            icon.AddToClassList("agent-icon");
            icon.AddToClassList("brand-custom");
            head.Add(icon);

            var nameLabel = new Label(SkillsLocalization.Get("agent_custom_title"));
            nameLabel.AddToClassList("agent-name");
            head.Add(nameLabel);

            card.Add(head);

            // Path row
            var pathRow = new VisualElement();
            pathRow.AddToClassList("setting-row");
            pathRow.style.flexDirection = FlexDirection.Row;
            pathRow.style.alignItems = Align.Center;
            pathRow.style.marginTop = 4;

            var pathField = new TextField();
            pathField.value = _customPath;
            pathField.style.flexGrow = 1;
            pathField.tooltip = SkillsLocalization.Get("agent_custom_path_placeholder");
            pathField.RegisterValueChangedCallback(e => _customPath = e.newValue ?? "");
            pathRow.Add(pathField);

            var browseBtn = new Button(() =>
            {
                string title = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "选择安装目录" : "Select Install Directory";
                string p = EditorUtility.OpenFolderPanel(title, _customPath, "");
                if (!string.IsNullOrEmpty(p))
                {
                    _customPath = p;
                    pathField.value = p;
                }
            });
            browseBtn.AddToClassList("mini-btn");
            browseBtn.text = SkillsLocalization.Get("agent_custom_browse");
            browseBtn.style.marginLeft = 4;
            pathRow.Add(browseBtn);
            card.Add(pathRow);

            // Name row
            var nameRow = new VisualElement();
            nameRow.AddToClassList("setting-row");
            nameRow.style.flexDirection = FlexDirection.Row;
            nameRow.style.alignItems = Align.Center;

            var nameInput = new TextField();
            nameInput.value = _customName;
            nameInput.style.flexGrow = 1;
            nameInput.tooltip = SkillsLocalization.Get("agent_custom_name_placeholder");
            nameInput.RegisterValueChangedCallback(e => _customName = e.newValue ?? "");
            nameRow.Add(nameInput);

            var installBtn = new Button(() => InstallCustom());
            installBtn.AddToClassList("mini-btn");
            installBtn.AddToClassList("install");
            installBtn.text = SkillsLocalization.Get("agent_custom_install");
            installBtn.style.marginLeft = 4;
            nameRow.Add(installBtn);
            card.Add(nameRow);

            return card;
        }

        private void OnInstallClick(AgentConfig cfg, bool isGlobal, bool isUpdate)
        {
            var result = cfg.installFunc(isGlobal);
            if (result.success)
            {
                string msg = cfg.getInstallSuccessMsg != null
                    ? cfg.getInstallSuccessMsg(isGlobal, result.message)
                    : SkillsLocalization.Get("install_success") + "\n" + result.message;
                EditorUtility.DisplayDialog("Success", msg, "OK");
            }
            else
            {
                string errMsg = isUpdate
                    ? string.Format(SkillsLocalization.Get("update_failed"), result.message)
                    : string.Format(SkillsLocalization.Get("install_failed"), result.message);
                EditorUtility.DisplayDialog("Error", errMsg, "OK");
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
                "OK", "Cancel"))
                return;

            var result = cfg.uninstallFunc(isGlobal);
            if (result.success)
                EditorUtility.DisplayDialog("Success", SkillsLocalization.Get("uninstall_success"), "OK");
            else
                EditorUtility.DisplayDialog("Error",
                    string.Format(SkillsLocalization.Get("uninstall_failed"), result.message), "OK");

            RebuildAgentsList();
        }

        private void InstallCustom()
        {
            if (string.IsNullOrEmpty(_customPath))
            {
                string msg = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "路径不能为空" : "Path cannot be empty";
                EditorUtility.DisplayDialog("Error", msg, "OK");
                return;
            }
            var result = SkillInstaller.InstallCustom(_customPath, _customName);
            if (result.success)
                EditorUtility.DisplayDialog("Success", SkillsLocalization.Get("install_success"), "OK");
            else
                EditorUtility.DisplayDialog("Error",
                    string.Format(SkillsLocalization.Get("install_failed"), result.message), "OK");
        }

        public void RefreshLocalization()
        {
            if (_helpBox != null)
            {
                _helpBox.text = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "项目安装：将 Skill 安装到当前 Unity 项目目录\n全局安装：将 Skill 安装到用户目录，所有项目可用\n\n注意：Antigravity 和 Codex 工作区都使用 .agents/skills，安装一次即两边可用"
                    : "Project Install: install skill to current Unity project\nGlobal Install: install skill to user folder, available to all projects\n\nNote: Antigravity and Codex both use .agents/skills in workspace mode — install once works for both.";
            }
            RebuildAgentsList();
        }
    }
}
