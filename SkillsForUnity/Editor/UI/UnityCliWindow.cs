using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Unity CLI 配置面板 —— 独立二级窗口（同 UnitySkillsAuditWindow 范式）。
    /// 入口：主窗口设置抽屉 Unity CLI 组按钮（权限组之下、服务器组之上）
    /// + ShortcutActions 可绑定快捷键。
    /// 未挂 Window 菜单（Window/UnitySkills 单入口约束）。
    ///
    /// 三区：CLI 检测（后台线程探测，schedule 轮询收结果）→ 项目绑定
    /// （Library/UnitySkills/cli_config.json）→ Feature 开关。
    /// </summary>
    public sealed class UnityCliWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/UnityCliWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/UnityCliWindow.uss";
        // 主题变量（--color-*）唯一源：主窗口 USS 先于本窗口 USS 加载。
        private const string ThemeUssPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";
        private const string InstallCmdUnix = "curl -fsSL https://cli.unity.com/install.sh | UNITY_CLI_CHANNEL=beta bash";
        private const string DocsUrl = "https://docs.unity.com/unity-cli";

#if UNITY_EDITOR_WIN
        private const string InstallCmd = "powershell -c \"irm https://cli.unity.com/install.ps1 | iex\"";
#else
        private const string InstallCmd = InstallCmdUnix;
#endif

        private Label     _statusBadge;
        private Label     _versionLabel;
        private TextField _pathField;
        private VisualElement _installGuide;
        private Label     _bindBadge;
        private Label     _bindInfo;
        private Button    _bindBtn;
        private Button    _unbindBtn;
        private Toggle    _featColdStart;
        private Toggle    _featOpenArgs;
        private Toggle    _featTest;
        private Toggle    _featRun;
        private Toggle    _featBuild;
        private Label     _helpBox;   // 方案 A：无框脚注文字（原 HelpBox 扁平化）

        private bool _detectionPending;
        // 轮询句柄：语言切换整树重建时先 Pause 旧项，避免在 root 上累积重复调度。
        private UnityEngine.UIElements.IVisualElementScheduledItem _pollSchedule;

        private static string L(string key, string en, string zh) => PermissionUiHelpers.L(key, en, zh);

        public static void ShowWindow()
        {
            var w = GetWindow<UnityCliWindow>(
                L("cli_window_title", "Unity CLI Setup", "Unity CLI 配置"));
            w.minSize = new Vector2(460, 420);
            w.Focus();
        }

        // ----- 语言跟随：主面板切换语言时整树重建（含窗口标题） -----

        private void OnEnable() => SkillsLocalization.LanguageChanged += RebuildForLanguage;
        private void OnDisable() => SkillsLocalization.LanguageChanged -= RebuildForLanguage;

        private void RebuildForLanguage()
        {
            titleContent = new GUIContent(L("cli_window_title", "Unity CLI Setup", "Unity CLI 配置"));
            rootVisualElement.Clear();
            rootVisualElement.styleSheets.Clear();
            CreateGUI();
        }

        private void CreateGUI()
        {
            var themeUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ThemeUssPath);
            if (themeUss != null) rootVisualElement.styleSheets.Add(themeUss);
            else Debug.LogWarning($"[UnitySkills] Failed to load theme USS: {ThemeUssPath}");

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);
            else Debug.LogWarning($"[UnitySkills] Failed to load CLI USS: {UssPath}");

            UISkillsFont.Apply(rootVisualElement);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load CLI UXML: {UxmlPath}");
                return;
            }
            uxml.CloneTree(rootVisualElement);

            _statusBadge   = rootVisualElement.Q<Label>("cli-status-badge");
            _versionLabel  = rootVisualElement.Q<Label>("cli-version-label");
            _pathField     = rootVisualElement.Q<TextField>("cli-path-field");
            _installGuide  = rootVisualElement.Q<VisualElement>("cli-install-guide");
            _bindBadge     = rootVisualElement.Q<Label>("cli-bind-badge");
            _bindInfo      = rootVisualElement.Q<Label>("cli-bind-info");
            _bindBtn       = rootVisualElement.Q<Button>("cli-bind-btn");
            _unbindBtn     = rootVisualElement.Q<Button>("cli-unbind-btn");
            _featColdStart = rootVisualElement.Q<Toggle>("cli-feat-coldstart");
            _featOpenArgs  = rootVisualElement.Q<Toggle>("cli-feat-openargs");
            _featTest      = rootVisualElement.Q<Toggle>("cli-feat-test");
            _featRun       = rootVisualElement.Q<Toggle>("cli-feat-run");
            _featBuild     = rootVisualElement.Q<Toggle>("cli-feat-build");
            _helpBox       = rootVisualElement.Q<Label>("cli-help-box");

            WireStaticTexts();
            WireActions();
            RefreshBindingUi();

            // 打开面板即触发一次检测；后台线程完成后由轮询收结果。
            StartDetection();
            _pollSchedule?.Pause();
            _pollSchedule = rootVisualElement.schedule.Execute(PollDetection).Every(300);
        }

        private void WireStaticTexts()
        {
            var detectTitle = rootVisualElement.Q<Label>("cli-detect-title");
            if (detectTitle != null) detectTitle.text = L("cli_detect_title", "Detection", "环境检测");

            var pathLabel = rootVisualElement.Q<Label>("cli-path-label");
            if (pathLabel != null) pathLabel.text = L("cli_path_label", "Path", "路径");
            if (_pathField != null)
                _pathField.tooltip = L("cli_path_tip",
                    "Optional: full path to the unity CLI executable (leave empty to auto-detect)",
                    "可选：unity CLI 可执行文件完整路径（留空自动探测）");

            var detectBtn = rootVisualElement.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.text = L("cli_detect", "Detect", "检测");

            var bindTitle = rootVisualElement.Q<Label>("cli-bind-title");
            if (bindTitle != null) bindTitle.text = L("cli_bind_title", "Project Binding", "项目绑定");

            var featTitle = rootVisualElement.Q<Label>("cli-features-title");
            if (featTitle != null) featTitle.text = L("cli_features_title", "Features", "能力开关");

            var installHint = rootVisualElement.Q<Label>("cli-install-hint");
            if (installHint != null)
                installHint.text = L("cli_install_hint",
                    "Unity CLI not found. Install it with the command below, or fill in a custom path above.",
                    "未检测到 Unity CLI。可用下方命令安装，或在上方填写自定义路径。");

            var installCmd = rootVisualElement.Q<TextField>("cli-install-cmd");
            if (installCmd != null) installCmd.SetValueWithoutNotify(InstallCmd);

            if (_featColdStart != null)
                _featColdStart.label = L("cli_feat_coldstart",
                    "Cold start (open project without Unity Hub)", "冷启动（免 Unity Hub 直接打开本项目）");
            if (_featOpenArgs != null)
                _featOpenArgs.label = L("cli_feat_openargs",
                    "Launch with arguments (unity open --args)", "传参启动（unity open --args）");
            if (_featTest != null)
                _featTest.label = L("cli_feat_test",
                    "Headless test runs (unity test)", "无头测试（unity test）");
            if (_featRun != null)
                _featRun.label = L("cli_feat_run",
                    "Batch runs (unity run)", "批处理运行（unity run）");
            if (_featBuild != null)
                _featBuild.label = L("cli_feat_build",
                    "Headless builds (unity build)", "无头构建（unity build）");

            if (_helpBox != null)
                _helpBox.text = L("cli_help",
                    "After binding, AI agents read Library/UnitySkills/cli_config.json and may use the Unity CLI to cold-start this project without Unity Hub. Unity CLI is experimental (beta); unbinding disables all CLI capabilities. The binding is machine-local and never committed to git.",
                    "绑定后，AI Agent 会读取 Library/UnitySkills/cli_config.json，并可通过 Unity CLI 免 Unity Hub 冷启动本项目。Unity CLI 目前为实验性（beta）；解绑即关闭全部 CLI 能力。绑定信息仅存本机，不会进入 git。");
        }

        private void WireActions()
        {
            var browseBtn = rootVisualElement.Q<Button>("cli-browse-btn");
            if (browseBtn != null)
            {
                browseBtn.tooltip = L("cli_browse_tip", "Browse for the unity executable", "浏览选择 unity 可执行文件");
                browseBtn.clicked += () =>
                {
                    string p = EditorUtility.OpenFilePanel(
                        L("cli_browse_title", "Select unity CLI executable", "选择 unity CLI 可执行文件"), "", "");
                    if (!string.IsNullOrEmpty(p)) _pathField?.SetValueWithoutNotify(p);
                };
            }

            var detectBtn = rootVisualElement.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.clicked += StartDetection;

            var copyBtn = rootVisualElement.Q<Button>("cli-copy-cmd-btn");
            if (copyBtn != null)
            {
                copyBtn.text = L("cli_copy", "Copy", "复制");
                copyBtn.clicked += () => EditorGUIUtility.systemCopyBuffer = InstallCmd;
            }

            var docsBtn = rootVisualElement.Q<Button>("cli-docs-btn");
            if (docsBtn != null)
            {
                docsBtn.text = L("cli_docs", "Docs", "文档");
                docsBtn.clicked += () => Application.OpenURL(DocsUrl);
            }

            if (_bindBtn != null) _bindBtn.clicked += OnBindClicked;
            if (_unbindBtn != null) _unbindBtn.clicked += OnUnbindClicked;

            var revealBtn = rootVisualElement.Q<Button>("cli-reveal-cfg-btn");
            if (revealBtn != null)
            {
                revealBtn.text = L("cli_reveal_cfg", "Reveal Config", "打开配置文件");
                revealBtn.clicked += () =>
                {
                    string cfgPath = System.IO.Path.Combine(
                        Application.dataPath, "../Library/UnitySkills/cli_config.json");
                    if (System.IO.File.Exists(cfgPath)) EditorUtility.RevealInFinder(cfgPath);
                };
            }

            if (_featColdStart != null)
                _featColdStart.RegisterValueChangedCallback(
                    e => UnityCliService.SetFeature(f => f.coldStart = e.newValue));
            if (_featOpenArgs != null)
                _featOpenArgs.RegisterValueChangedCallback(
                    e => UnityCliService.SetFeature(f => f.openArgs = e.newValue));
            if (_featTest != null)
                _featTest.RegisterValueChangedCallback(
                    e => UnityCliService.SetFeature(f => f.cliTest = e.newValue));
            if (_featRun != null)
                _featRun.RegisterValueChangedCallback(
                    e => UnityCliService.SetFeature(f => f.cliRun = e.newValue));
            if (_featBuild != null)
                _featBuild.RegisterValueChangedCallback(
                    e => UnityCliService.SetFeature(f => f.cliBuild = e.newValue));
        }

        // ===== 检测（后台线程 → 轮询收结果，遵守零跨线程约束）=====

        private void StartDetection()
        {
            string userPath = _pathField?.value?.Trim();
            UnityCliService.DetectAsync(string.IsNullOrEmpty(userPath) ? null : userPath);
            _detectionPending = true;
            SetBadge(_statusBadge, "unknown", L("cli_detecting", "Detecting…", "检测中…"));
        }

        private void PollDetection()
        {
            if (!_detectionPending || UnityCliService.IsDetecting) return;
            _detectionPending = false;

            var r = UnityCliService.LastResult;
            bool found = r != null && r.found;
            if (found)
            {
                SetBadge(_statusBadge, "installed", L("cli_status_found", "Installed", "已安装"));
                if (_versionLabel != null) _versionLabel.text = $"{r.version}  ·  {r.cliPath}";
                if (_pathField != null && string.IsNullOrEmpty(_pathField.value))
                    _pathField.SetValueWithoutNotify(r.cliPath);
            }
            else
            {
                SetBadge(_statusBadge, "not-installed", L("cli_status_missing", "Not Found", "未安装"));
                if (_versionLabel != null) _versionLabel.text = "";
            }
            if (_installGuide != null)
                _installGuide.style.display = found ? DisplayStyle.None : DisplayStyle.Flex;
            RefreshBindingUi();
        }

        // ===== 绑定 =====

        private void OnBindClicked()
        {
            var r = UnityCliService.LastResult;
            if (r == null || !r.found)
            {
                EditorUtility.DisplayDialog("Unity CLI",
                    L("cli_bind_need_cli",
                        "Unity CLI must be detected before binding. Run Detect first.",
                        "绑定前需要先成功检测到 Unity CLI，请先点击“检测”。"), "OK");
                return;
            }
            UnityCliService.Bind(r.cliPath, r.version);
            RefreshBindingUi();
        }

        private void OnUnbindClicked()
        {
            if (!EditorUtility.DisplayDialog(
                    L("cli_unbind", "Unbind", "解绑"),
                    L("cli_unbind_confirm",
                        "Disable Unity CLI capabilities for this project? AI agents will stop using the CLI (cold start, --args, headless tests, batch runs, headless builds).",
                        "确定关闭本项目的 Unity CLI 能力吗？AI Agent 将停止使用 CLI（冷启动、传参启动、无头测试、批处理运行、无头构建）。"),
                    "OK", "Cancel"))
                return;
            UnityCliService.Unbind();
            RefreshBindingUi();
        }

        private void RefreshBindingUi()
        {
            var cfg = UnityCliService.LoadConfig();
            bool bound = cfg != null && cfg.enabled && !string.IsNullOrEmpty(cfg.cliPath);

            SetBadge(_bindBadge,
                bound ? "installed" : "not-installed",
                bound ? L("cli_bound", "Bound", "已绑定")
                      : L("cli_unbound", "Not Bound", "未绑定"));

            if (_bindInfo != null)
            {
                _bindInfo.text = bound
                    ? string.Format(
                        L("cli_bind_info_fmt", "CLI {0} · bound {1}", "CLI {0} · 绑定于 {1}"),
                        cfg.cliVersion,
                        FormatLocalTime(cfg.boundAt))
                    : L("cli_bind_none",
                        "Bind this project to enable CLI capabilities for AI agents.",
                        "绑定当前项目后，AI Agent 才会启用 CLI 能力。");
            }

            var detected = UnityCliService.LastResult;
            if (_bindBtn != null)
            {
                _bindBtn.text = bound ? L("cli_rebind", "Re-bind", "重新绑定")
                                      : L("cli_bind", "Bind This Project", "绑定当前项目");
                _bindBtn.SetEnabled(detected != null && detected.found);
            }
            if (_unbindBtn != null)
            {
                _unbindBtn.text = L("cli_unbind", "Unbind", "解绑");
                _unbindBtn.SetEnabled(bound);
            }

            var features = cfg?.features;
            bool featEnabled = bound && features != null;
            SetFeatureToggle(_featColdStart, featEnabled, features?.coldStart ?? true);
            SetFeatureToggle(_featOpenArgs,  featEnabled, features?.openArgs ?? true);
            SetFeatureToggle(_featTest,      featEnabled, features?.cliTest ?? true);
            SetFeatureToggle(_featRun,   featEnabled, features?.cliRun ?? false);
            SetFeatureToggle(_featBuild, featEnabled, features?.cliBuild ?? false);
        }

        private static void SetFeatureToggle(Toggle t, bool enabled, bool value)
        {
            if (t == null) return;
            t.SetValueWithoutNotify(value);
            t.SetEnabled(enabled);
        }

        private static void SetBadge(Label badge, string cls, string text)
        {
            if (badge == null) return;
            badge.text = text;
            badge.RemoveFromClassList("installed");
            badge.RemoveFromClassList("not-installed");
            badge.RemoveFromClassList("unknown");
            badge.AddToClassList(cls);
        }

        private static string FormatLocalTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "?";
            if (DateTime.TryParse(iso, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }
    }
}

// Producer:Betsy
