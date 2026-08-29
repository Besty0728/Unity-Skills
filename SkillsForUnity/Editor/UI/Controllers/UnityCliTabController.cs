using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Owns the Unity CLI setup subtree so it can be mounted in a standalone window or a top-level tab.
    /// Detection is started here and its background result is consumed only by the UI Toolkit schedule.
    /// </summary>
    public sealed class UnityCliTabController
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/UnityCliTab.uxml";
        private const string UssPath = "Packages/com.besty.unity-skills/Editor/UI/UnityCliWindow.uss";
        private const string InstallCmdUnix = "curl -fsSL https://cli.unity.com/install.sh | UNITY_CLI_CHANNEL=beta bash";
        private const string DocsUrl = "https://docs.unity.com/unity-cli";

#if UNITY_EDITOR_WIN
        private const string InstallCmd = "powershell -c \"irm https://cli.unity.com/install.ps1 | iex\"";
#else
        private const string InstallCmd = InstallCmdUnix;
#endif

        private readonly VisualElement _root;
        private readonly EditorWindow _owner;

        private Label _statusBadge;
        private Label _versionLabel;
        private TextField _pathField;
        private VisualElement _installGuide;
        private Label _bindBadge;
        private Label _bindInfo;
        private Button _bindBtn;
        private Button _unbindBtn;
        private Toggle _featColdStart;
        private Toggle _featOpenArgs;
        private Toggle _featTest;
        private Toggle _featRun;
        private Toggle _featBuild;
        private Label _helpBox;

        private bool _detectionPending;
        private bool _disposed;
        private IVisualElementScheduledItem _pollSchedule;

        /// <summary>Creates and wires the tab subtree under <paramref name="root"/>.</summary>
        public UnityCliTabController(VisualElement root, EditorWindow owner = null)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _owner = owner;

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null)
                _root.styleSheets.Add(uss);
            else if (uss == null)
                SkillsLogger.LogWarning($"Failed to load CLI USS: {UssPath}");

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                SkillsLogger.LogError($"Failed to load CLI tab UXML: {UxmlPath}");
                return;
            }

            uxml.CloneTree(_root);
            CacheReferences();
            WireStaticTexts();
            WireActions();
            RefreshBindingUi();

            SkillsLocalization.LanguageChanged += RefreshLocalization;
            StartDetection();
            _pollSchedule = _root.schedule.Execute(PollDetection).Every(300);
        }

        /// <summary>
        /// Refreshes all localized labels after a language switch. The visual tree is kept intact,
        /// which also keeps the controller's schedule and event wiring stable when mounted as a tab.
        /// </summary>
        public void RefreshLocalization()
        {
            if (_disposed) return;
            WireStaticTexts();
            RefreshBindingUi();
        }

        /// <summary>Stops polling and releases the static localization subscription.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollSchedule?.Pause();
            _pollSchedule = null;
            SkillsLocalization.LanguageChanged -= RefreshLocalization;
            _detectionPending = false;
        }

        private bool OwnerIsAlive => _owner == null || _owner;

        private void CacheReferences()
        {
            _statusBadge = _root.Q<Label>("cli-status-badge");
            _versionLabel = _root.Q<Label>("cli-version-label");
            _pathField = _root.Q<TextField>("cli-path-field");
            _installGuide = _root.Q<VisualElement>("cli-install-guide");
            _bindBadge = _root.Q<Label>("cli-bind-badge");
            _bindInfo = _root.Q<Label>("cli-bind-info");
            _bindBtn = _root.Q<Button>("cli-bind-btn");
            _unbindBtn = _root.Q<Button>("cli-unbind-btn");
            _featColdStart = _root.Q<Toggle>("cli-feat-coldstart");
            _featOpenArgs = _root.Q<Toggle>("cli-feat-openargs");
            _featTest = _root.Q<Toggle>("cli-feat-test");
            _featRun = _root.Q<Toggle>("cli-feat-run");
            _featBuild = _root.Q<Toggle>("cli-feat-build");
            _helpBox = _root.Q<Label>("cli-help-box");
        }

        private void WireStaticTexts()
        {
            var detectTitle = _root.Q<Label>("cli-detect-title");
            if (detectTitle != null) detectTitle.text = SkillsLocalization.Get("cli_detect_title");

            var pathLabel = _root.Q<Label>("cli-path-label");
            if (pathLabel != null) pathLabel.text = SkillsLocalization.Get("cli_path_label");
            if (_pathField != null) _pathField.tooltip = SkillsLocalization.Get("cli_path_tip");

            var detectBtn = _root.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.text = SkillsLocalization.Get("cli_detect");

            var bindTitle = _root.Q<Label>("cli-bind-title");
            if (bindTitle != null) bindTitle.text = SkillsLocalization.Get("cli_bind_title");

            var featTitle = _root.Q<Label>("cli-features-title");
            if (featTitle != null) featTitle.text = SkillsLocalization.Get("cli_features_title");

            var installHint = _root.Q<Label>("cli-install-hint");
            if (installHint != null) installHint.text = SkillsLocalization.Get("cli_install_hint");

            var installCmd = _root.Q<TextField>("cli-install-cmd");
            if (installCmd != null) installCmd.SetValueWithoutNotify(InstallCmd);

            if (_featColdStart != null) _featColdStart.label = SkillsLocalization.Get("cli_feat_coldstart");
            if (_featOpenArgs != null) _featOpenArgs.label = SkillsLocalization.Get("cli_feat_openargs");
            if (_featTest != null) _featTest.label = SkillsLocalization.Get("cli_feat_test");
            if (_featRun != null) _featRun.label = SkillsLocalization.Get("cli_feat_run");
            if (_featBuild != null) _featBuild.label = SkillsLocalization.Get("cli_feat_build");
            if (_helpBox != null) _helpBox.text = SkillsLocalization.Get("cli_help");
        }

        private void WireActions()
        {
            var browseBtn = _root.Q<Button>("cli-browse-btn");
            if (browseBtn != null)
            {
                browseBtn.tooltip = SkillsLocalization.Get("cli_browse_tip");
                browseBtn.clicked += () =>
                {
                    string path = EditorUtility.OpenFilePanel(SkillsLocalization.Get("cli_browse_title"), "", "");
                    if (!string.IsNullOrEmpty(path)) _pathField?.SetValueWithoutNotify(path);
                };
            }

            var detectBtn = _root.Q<Button>("cli-detect-btn");
            if (detectBtn != null) detectBtn.clicked += StartDetection;

            var copyBtn = _root.Q<Button>("cli-copy-cmd-btn");
            if (copyBtn != null)
            {
                copyBtn.text = SkillsLocalization.Get("cli_copy");
                copyBtn.clicked += () => EditorGUIUtility.systemCopyBuffer = InstallCmd;
            }

            var docsBtn = _root.Q<Button>("cli-docs-btn");
            if (docsBtn != null)
            {
                docsBtn.text = SkillsLocalization.Get("cli_docs");
                docsBtn.clicked += () => Application.OpenURL(DocsUrl);
            }

            if (_bindBtn != null) _bindBtn.clicked += OnBindClicked;
            if (_unbindBtn != null) _unbindBtn.clicked += OnUnbindClicked;

            var revealBtn = _root.Q<Button>("cli-reveal-cfg-btn");
            if (revealBtn != null)
            {
                revealBtn.text = SkillsLocalization.Get("cli_reveal_cfg");
                revealBtn.clicked += () =>
                {
                    string configPath = System.IO.Path.Combine(Application.dataPath, "../Library/UnitySkills/cli_config.json");
                    if (System.IO.File.Exists(configPath)) EditorUtility.RevealInFinder(configPath);
                };
            }

            if (_featColdStart != null) _featColdStart.RegisterValueChangedCallback(e => UnityCliService.SetFeature(f => f.coldStart = e.newValue));
            if (_featOpenArgs != null) _featOpenArgs.RegisterValueChangedCallback(e => UnityCliService.SetFeature(f => f.openArgs = e.newValue));
            if (_featTest != null) _featTest.RegisterValueChangedCallback(e => UnityCliService.SetFeature(f => f.cliTest = e.newValue));
            if (_featRun != null) _featRun.RegisterValueChangedCallback(e => UnityCliService.SetFeature(f => f.cliRun = e.newValue));
            if (_featBuild != null) _featBuild.RegisterValueChangedCallback(e => UnityCliService.SetFeature(f => f.cliBuild = e.newValue));
        }

        private void StartDetection()
        {
            if (_disposed) return;
            string userPath = _pathField?.value?.Trim();
            UnityCliService.DetectAsync(string.IsNullOrEmpty(userPath) ? null : userPath);
            _detectionPending = true;
            SetBadge(_statusBadge, "unknown", SkillsLocalization.Get("cli_detecting"));
        }

        private void PollDetection()
        {
            if (_disposed || !OwnerIsAlive || !_detectionPending || UnityCliService.IsDetecting) return;
            _detectionPending = false;

            var result = UnityCliService.LastResult;
            bool found = result != null && result.found;
            if (found)
            {
                SetBadge(_statusBadge, "installed", SkillsLocalization.Get("cli_status_found"));
                if (_versionLabel != null) _versionLabel.text = $"{result.version}  ·  {result.cliPath}";
                if (_pathField != null && string.IsNullOrEmpty(_pathField.value))
                    _pathField.SetValueWithoutNotify(result.cliPath);
            }
            else
            {
                SetBadge(_statusBadge, "not-installed", SkillsLocalization.Get("cli_status_missing"));
                if (_versionLabel != null) _versionLabel.text = "";
            }

            if (_installGuide != null) _installGuide.style.display = found ? DisplayStyle.None : DisplayStyle.Flex;
            RefreshBindingUi();
        }

        private void OnBindClicked()
        {
            var result = UnityCliService.LastResult;
            if (result == null || !result.found)
            {
                EditorUtility.DisplayDialog(SkillsLocalization.Get("cli_group_title"), SkillsLocalization.Get("cli_bind_need_cli"), SkillsLocalization.Get("dialog_ok"));
                return;
            }
            UnityCliService.Bind(result.cliPath, result.version);
            RefreshBindingUi();
        }

        private void OnUnbindClicked()
        {
            if (!EditorUtility.DisplayDialog(SkillsLocalization.Get("cli_unbind"), SkillsLocalization.Get("cli_unbind_confirm"), SkillsLocalization.Get("dialog_ok"), SkillsLocalization.Get("dialog_cancel")))
                return;
            UnityCliService.Unbind();
            RefreshBindingUi();
        }

        private void RefreshBindingUi()
        {
            if (_disposed) return;
            var config = UnityCliService.LoadConfig();
            bool bound = config != null && config.enabled && !string.IsNullOrEmpty(config.cliPath);

            SetBadge(_bindBadge, bound ? "installed" : "not-installed", bound ? SkillsLocalization.Get("cli_bound") : SkillsLocalization.Get("cli_unbound"));
            if (_bindInfo != null)
            {
                _bindInfo.text = bound
                    ? string.Format(SkillsLocalization.Get("cli_bind_info_fmt"), config.cliVersion, FormatLocalTime(config.boundAt))
                    : SkillsLocalization.Get("cli_bind_none");
            }

            var detected = UnityCliService.LastResult;
            if (_bindBtn != null)
            {
                _bindBtn.text = bound ? SkillsLocalization.Get("cli_rebind") : SkillsLocalization.Get("cli_bind");
                _bindBtn.SetEnabled(detected != null && detected.found);
            }
            if (_unbindBtn != null)
            {
                _unbindBtn.text = SkillsLocalization.Get("cli_unbind");
                _unbindBtn.SetEnabled(bound);
            }

            var features = config?.features;
            bool featureEnabled = bound && features != null;
            SetFeatureToggle(_featColdStart, featureEnabled, features?.coldStart ?? true);
            SetFeatureToggle(_featOpenArgs, featureEnabled, features?.openArgs ?? true);
            SetFeatureToggle(_featTest, featureEnabled, features?.cliTest ?? true);
            SetFeatureToggle(_featRun, featureEnabled, features?.cliRun ?? false);
            SetFeatureToggle(_featBuild, featureEnabled, features?.cliBuild ?? false);
        }

        private static void SetFeatureToggle(Toggle toggle, bool enabled, bool value)
        {
            if (toggle == null) return;
            toggle.SetValueWithoutNotify(value);
            toggle.SetEnabled(enabled);
        }

        private static void SetBadge(Label badge, string className, string text)
        {
            if (badge == null) return;
            badge.text = text;
            badge.RemoveFromClassList("installed");
            badge.RemoveFromClassList("not-installed");
            badge.RemoveFromClassList("unknown");
            badge.AddToClassList(className);
        }

        private static string FormatLocalTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "?";
            if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dateTime))
                return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return iso;
        }
    }
}

// Producer:Betsy
