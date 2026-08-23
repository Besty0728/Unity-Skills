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
        private Label     _helpBox;

        private bool _detectionPending;
        // 轮询句柄：语言切换整树重建时先 Pause 旧项，避免在 root 上累积重复调度。
        private UnityEngine.UIElements.IVisualElementScheduledItem _pollSchedule;

        public static void ShowWindow()
        {
            var w = GetWindow<UnityCliWindow>(
                SkillsLocalization.Get("cli_window_title"));
            w.minSize = new Vector2(460, 420);
            w.Focus();
        }

        // ----- 语言跟随：主面板切换语言时整树重建（含窗口标题） -----

        private void OnEnable() => SkillsLocalization.LanguageChanged += RebuildForLanguage;
        private void OnDisable() => SkillsLocalization.LanguageChanged -= RebuildForLanguage;

        private void RebuildForLanguage()
        {
            titleContent = new GUIContent(SkillsLocalization.Get("cli_window_title"));
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
            if (detectTitle != null) detectTitle.text = SkillsLocalization.Get("cli_detect_title");

            var pathLabel = rootVisualElement.Q<Label>("cli-path-label");
            if (pathLabel != null) pathLabel.text = SkillsLocalization.Get("cli_path_label");
            if (_pathField != null)
                _pathField.tooltip = SkillsLocalization.Get("cli_path_tip");

            var detectBtn = rootVisualElement.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.text = SkillsLocalization.Get("cli_detect");

            var bindTitle = rootVisualElement.Q<Label>("cli-bind-title");
            if (bindTitle != null) bindTitle.text = SkillsLocalization.Get("cli_bind_title");

            var featTitle = rootVisualElement.Q<Label>("cli-features-title");
            if (featTitle != null) featTitle.text = SkillsLocalization.Get("cli_features_title");

            var installHint = rootVisualElement.Q<Label>("cli-install-hint");
            if (installHint != null)
                installHint.text = SkillsLocalization.Get("cli_install_hint");

            var installCmd = rootVisualElement.Q<TextField>("cli-install-cmd");
            if (installCmd != null) installCmd.SetValueWithoutNotify(InstallCmd);

            if (_featColdStart != null)
                _featColdStart.label = SkillsLocalization.Get("cli_feat_coldstart");
            if (_featOpenArgs != null)
                _featOpenArgs.label = SkillsLocalization.Get("cli_feat_openargs");
            if (_featTest != null)
                _featTest.label = SkillsLocalization.Get("cli_feat_test");
            if (_featRun != null)
                _featRun.label = SkillsLocalization.Get("cli_feat_run");
            if (_featBuild != null)
                _featBuild.label = SkillsLocalization.Get("cli_feat_build");

            if (_helpBox != null)
                _helpBox.text = SkillsLocalization.Get("cli_help");
        }

        private void WireActions()
        {
            var browseBtn = rootVisualElement.Q<Button>("cli-browse-btn");
            if (browseBtn != null)
            {
                browseBtn.tooltip = SkillsLocalization.Get("cli_browse_tip");
                browseBtn.clicked += () =>
                {
                    string p = EditorUtility.OpenFilePanel(
                        SkillsLocalization.Get("cli_browse_title"), "", "");
                    if (!string.IsNullOrEmpty(p)) _pathField?.SetValueWithoutNotify(p);
                };
            }

            var detectBtn = rootVisualElement.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.clicked += StartDetection;

            var copyBtn = rootVisualElement.Q<Button>("cli-copy-cmd-btn");
            if (copyBtn != null)
            {
                copyBtn.text = SkillsLocalization.Get("cli_copy");
                copyBtn.clicked += () => EditorGUIUtility.systemCopyBuffer = InstallCmd;
            }

            var docsBtn = rootVisualElement.Q<Button>("cli-docs-btn");
            if (docsBtn != null)
            {
                docsBtn.text = SkillsLocalization.Get("cli_docs");
                docsBtn.clicked += () => Application.OpenURL(DocsUrl);
            }

            if (_bindBtn != null) _bindBtn.clicked += OnBindClicked;
            if (_unbindBtn != null) _unbindBtn.clicked += OnUnbindClicked;

            var revealBtn = rootVisualElement.Q<Button>("cli-reveal-cfg-btn");
            if (revealBtn != null)
            {
                revealBtn.text = SkillsLocalization.Get("cli_reveal_cfg");
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
            SetBadge(_statusBadge, "unknown", SkillsLocalization.Get("cli_detecting"));
        }

        private void PollDetection()
        {
            if (!_detectionPending || UnityCliService.IsDetecting) return;
            _detectionPending = false;

            var r = UnityCliService.LastResult;
            bool found = r != null && r.found;
            if (found)
            {
                SetBadge(_statusBadge, "installed", SkillsLocalization.Get("cli_status_found"));
                if (_versionLabel != null) _versionLabel.text = $"{r.version}  ·  {r.cliPath}";
                if (_pathField != null && string.IsNullOrEmpty(_pathField.value))
                    _pathField.SetValueWithoutNotify(r.cliPath);
            }
            else
            {
                SetBadge(_statusBadge, "not-installed", SkillsLocalization.Get("cli_status_missing"));
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
                EditorUtility.DisplayDialog(SkillsLocalization.Get("cli_group_title"),
                    SkillsLocalization.Get("cli_bind_need_cli"), SkillsLocalization.Get("dialog_ok"));
                return;
            }
            UnityCliService.Bind(r.cliPath, r.version);
            RefreshBindingUi();
        }

        private void OnUnbindClicked()
        {
            if (!EditorUtility.DisplayDialog(
                    SkillsLocalization.Get("cli_unbind"),
                    SkillsLocalization.Get("cli_unbind_confirm"),
                    SkillsLocalization.Get("dialog_ok"), SkillsLocalization.Get("dialog_cancel")))
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
                bound ? SkillsLocalization.Get("cli_bound")
                      : SkillsLocalization.Get("cli_unbound"));

            if (_bindInfo != null)
            {
                _bindInfo.text = bound
                    ? string.Format(
                        SkillsLocalization.Get("cli_bind_info_fmt"),
                        cfg.cliVersion,
                        FormatLocalTime(cfg.boundAt))
                    : SkillsLocalization.Get("cli_bind_none");
            }

            var detected = UnityCliService.LastResult;
            if (_bindBtn != null)
            {
                _bindBtn.text = bound ? SkillsLocalization.Get("cli_rebind")
                                      : SkillsLocalization.Get("cli_bind");
                _bindBtn.SetEnabled(detected != null && detected.found);
            }
            if (_unbindBtn != null)
            {
                _unbindBtn.text = SkillsLocalization.Get("cli_unbind");
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
