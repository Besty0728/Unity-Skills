using System;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Unity Editor Window — UnitySkills layout.
    /// Topbar (server status + URL + toggle + settings) — persistent.
    /// 4 tabs: Skills / AI Config / History / Analytics.
    /// Footer: version + live stats pill + segmented language switch.
    /// Settings panel: slide-in drawer from the right.
    /// </summary>
    public class UnitySkillsWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";

        // One-time first-run toast flag.
        // Only shown for "fresh install + OperatingMode never set", to avoid disturbing existing/configured users.
        private const string PrefKeyFirstRunToast = "UnitySkills_FirstRunToastShown";

        [SerializeField] private int _selectedTab = 0;

        // ----- Skill catalog (unchanged data contract — Controllers consume it) -----
        public class SkillInfo
        {
            public string Name;
            public string Description;
            public MethodInfo Method;
        }
        private Dictionary<string, List<SkillInfo>> _skillsByCategory;
        public Dictionary<string, List<SkillInfo>> SkillsByCategory => _skillsByCategory;

        // Coalesces surface-profile switches into one deferred catalog rebuild.
        private bool _catalogRebuildQueued;

        // ----- Sub-controllers -----
        private TopbarController         _topbar;
        private FooterController         _footer;
        private SettingsDrawerController _drawer;
        private PendingApprovalBannerController _pendingBanner;
        private VersionUpdateBannerController _versionUpdateBanner;
        private SkillsTabController      _skillsController;
        private AIConfigTabController    _configController;
        private HistoryTabController     _historyController;
        private AnalyticsTabController   _analyticsController;

        // ----- Tab strip -----
        private VisualElement[] _tabContents;
        private Button[]        _tabButtons;
        private VisualElement[] _tabUnderlines;

        // ----- Live tick — routed through EditorUiScheduler to avoid mutating the
        // visual tree during repaint/generateVisualContent (issue #44); paused on disable
        // so a closed window can't keep queuing refreshes. -----
        private IVisualElementScheduledItem _liveUpdateItem;

        // Single flat entry: clicking "Window ▸ UnitySkills" opens the main panel directly.
        // CONSTRAINT: a "Window/UnitySkills" leaf cannot coexist with any
        // "Window/UnitySkills/..." submenu item — Unity swallows the leaf.
        // Secondary panels (such as Audit Log) are therefore reachable only via in-panel buttons and
        // shortcuts (ShortcutActions); never add another [MenuItem] under this prefix.
        [MenuItem("Window/UnitySkills", false, 1)]
        public static void ShowWindow()
        {
            var window = GetWindow<UnitySkillsWindow>(SkillsLocalization.Get("window_title"));
            window.minSize = new Vector2(420, 480);
        }

        private void OnEnable()
        {
            RefreshSkillsList();
            // Links a mode/authorization change to the topbar/footer's next repaint, avoiding subscribing separately in every sub-controller.
            SkillsModeManager.OnChanged += Repaint;
            // A profile switch changes the visible skill set, so the catalog needs rebuilding
            // rather than repainting — see OnSurfaceProfileChanged.
            SkillsSurfaceProfile.OnChanged += OnSurfaceProfileChanged;
            MaybeShowFirstRunToast();
        }

        private void OnDisable()
        {
            SkillsModeManager.OnChanged -= Repaint;
            SkillsSurfaceProfile.OnChanged -= OnSurfaceProfileChanged;
            SkillsLocalization.LanguageChanged -= RefreshLocalization;
            _liveUpdateItem?.Pause();
            _liveUpdateItem = null;
        }

        public void CreateGUI()
        {
            SkillsLocalization.LanguageChanged -= RefreshLocalization;
            SkillsLocalization.LanguageChanged += RefreshLocalization;

            // Load USS first so :root variables resolve when UXML clones
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);
            else Debug.LogWarning($"[UnitySkills] Failed to load USS: {UssPath}");

            // Bundled CJK font — fixes the macOS shared-atlas glyph drop (see UISkillsFont).
            UISkillsFont.Apply(rootVisualElement);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load UXML: {UxmlPath}");
                return;
            }
            uxml.CloneTree(rootVisualElement);

            CacheTabReferences();

            // --- Sub-controllers ---
            _topbar         = new TopbarController(rootVisualElement, this);
            _footer         = new FooterController(rootVisualElement, this);
            _drawer         = new SettingsDrawerController(rootVisualElement, this);
            _pendingBanner  = new PendingApprovalBannerController(rootVisualElement, this);
            _versionUpdateBanner = new VersionUpdateBannerController(rootVisualElement);

            _skillsController  = new SkillsTabController(_tabContents[0], this);
            _configController  = new AIConfigTabController(_tabContents[1], this);
            _historyController = new HistoryTabController(_tabContents[2], this);
            _analyticsController = new AnalyticsTabController(_tabContents[3], this);

            // --- Tab clicks ---
            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int idx = i;
                if (_tabButtons[i] != null)
                    _tabButtons[i].clicked += () => SwitchTab(idx);
            }

            SwitchTab(_selectedTab);
            RefreshLocalization();

            // Live update tick — 500ms (server stats, status). Routed through
            // EditorUiScheduler.RepeatSafe so the actual mutation happens on delayCall,
            // outside repaint/generateVisualContent (issue #44).
            _liveUpdateItem = EditorUiScheduler.RepeatSafe(rootVisualElement, 500, OnLiveDataUpdate);
        }

        private void CacheTabReferences()
        {
            _tabButtons    = new Button[4];
            _tabContents   = new VisualElement[4];
            _tabUnderlines = new VisualElement[4];
            for (int i = 0; i < 4; i++)
            {
                _tabButtons[i]    = rootVisualElement.Q<Button>($"tab-btn-{i}");
                _tabContents[i]   = rootVisualElement.Q<VisualElement>($"tab-content-{i}");
                _tabUnderlines[i] = rootVisualElement.Q<VisualElement>($"tab-underline-{i}");
            }
        }

        private void SwitchTab(int index)
        {
            if (index < 0 || index >= _tabContents.Length) return;
            _selectedTab = index;

            for (int i = 0; i < _tabContents.Length; i++)
            {
                if (_tabContents[i] != null)
                    _tabContents[i].style.display = (i == index) ? DisplayStyle.Flex : DisplayStyle.None;

                if (_tabButtons[i] != null)
                {
                    if (i == index) _tabButtons[i].AddToClassList("tab-active");
                    else            _tabButtons[i].RemoveFromClassList("tab-active");
                }

                if (_tabUnderlines[i] != null)
                {
                    if (i == index) _tabUnderlines[i].AddToClassList("active");
                    else            _tabUnderlines[i].RemoveFromClassList("active");
                }
            }

            if (_tabButtons[index] != null) _tabButtons[index].Blur();

            // Analytics pulls aggregates on demand (30s cache in SkillTelemetryService),
            // so activating the tab is the natural refresh point — no live tick involved.
            if (index == 3) _analyticsController?.OnTabSelected();
        }

        /// <summary>
        /// Called when user clicks a skill in Skills Tab. Stays within the
        /// Skills tab (master-detail) rather than a separate "Test" tab.
        /// Tab switch ensured here so external callers (legacy code paths) still work.
        /// </summary>
        public void SelectTestSkill(string skillName, string defaultParams)
        {
            SwitchTab(0);
            _skillsController?.SelectSkillByName(skillName, defaultParams);
        }

        public void OpenSettings()  => _drawer?.Open();
        public void CloseSettings() => _drawer?.Close();

        // ----- Live tick — fanned out to controllers that care -----
        private void OnLiveDataUpdate()
        {
            _topbar?.UpdateLiveData();
            _footer?.UpdateLiveData();
            _versionUpdateBanner?.UpdateLiveData();
        }

        // ----- Language switch (called by FooterController) -----
        public void SetLanguage(SkillsLocalization.Language lang)
        {
            if (SkillsLocalization.Current == lang) return;
            SkillsLocalization.Current = lang;
        }

        public void RefreshLocalization()
        {
            UISkillsFont.Apply(rootVisualElement);

            // Main tabs
            if (_tabButtons[0] != null) _tabButtons[0].text = SkillsLocalization.Get("tab_skills");
            if (_tabButtons[1] != null) _tabButtons[1].text = SkillsLocalization.Get("tab_ai_config");
            if (_tabButtons[2] != null) _tabButtons[2].text = SkillsLocalization.Get("tab_history");
            if (_tabButtons[3] != null) _tabButtons[3].text = SkillsLocalization.Get("tab_analytics");

            _topbar?.RefreshLocalization();
            _footer?.RefreshLocalization();
            _drawer?.RefreshLocalization();
            _pendingBanner?.RefreshLocalization();
            _versionUpdateBanner?.RefreshLocalization();
            _skillsController?.RefreshLocalization();
            _configController?.RefreshLocalization();
            _historyController?.RefreshLocalization();
            _analyticsController?.RefreshLocalization();
        }

        // ===== Skill catalog (preserved API for controllers) =====

        public void RefreshSkillsList()
        {
            _skillsByCategory = new Dictionary<string, List<SkillInfo>>();

            // Must go through the router's PROFILE-FILTERED snapshot, not a raw TypeCache sweep.
            // The surface profile is the user's statement about which skills may be offered at all;
            // enumerating types directly ignores it, so under the guide profile the panel would
            // still list gameobject_create, let the user select it and press Run, and answer with a
            // SURFACE_EXCLUDED rejection whose wording is addressed to an AI agent. Same source and
            // same reasoning as AllowlistPickerWindow.
            SkillRouter.SkillInfo[] snapshot;
            try
            {
                snapshot = SkillRouter.GetAllSkillsSnapshot();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnitySkills] Skill snapshot failed; Skills tab left empty: {ex.Message}");
                return;
            }

            foreach (var skill in snapshot ?? Array.Empty<SkillRouter.SkillInfo>())
            {
                if (skill == null || string.IsNullOrEmpty(skill.Name) || skill.Method == null)
                    continue;

                // Group by the declared category rather than the declaring type's file name: the
                // two agree for most modules but not all (skills split across helper files landed
                // in their own bogus group), and the category is what the rest of the product uses.
                var category = skill.Category.ToString();
                if (!_skillsByCategory.TryGetValue(category, out var list))
                    _skillsByCategory[category] = list = new List<SkillInfo>();

                list.Add(new SkillInfo
                {
                    Name = skill.Name,
                    Description = skill.Description ?? "",
                    Method = skill.Method
                });
            }
        }

        /// <summary>
        /// The surface profile decides which skills exist at all, so a switch has to rebuild the
        /// catalog and the rows, not merely repaint. Deferred through EditorUiScheduler because
        /// OnChanged can arrive mid-layout and rebuilding the tree there throws (issue #44); the
        /// flag coalesces a burst of switches into a single rebuild.
        /// </summary>
        private void OnSurfaceProfileChanged()
        {
            if (_catalogRebuildQueued) return;
            _catalogRebuildQueued = true;

            // Routed through EditorUiScheduler.RepeatSafe rather than a bare delayCall: the
            // rebuild below mutates the visual tree, and a hand-written delayCall has none of
            // RepeatSafe's InvalidOperationException/MissingReferenceException guard for a
            // deferred callback that lands mid-render (issue #44). RepeatSafe's item is a
            // recurring schedule, so it is paused from inside its own first firing to keep this a
            // one-shot coalesced rebuild rather than a real repeating tick.
            IVisualElementScheduledItem rebuildItem = null;
            rebuildItem = EditorUiScheduler.RepeatSafe(rootVisualElement, 1, () =>
            {
                rebuildItem?.Pause();
                _catalogRebuildQueued = false;
                if (!this) return; // window closed while the rebuild was queued
                RefreshSkillsList();
                _skillsController?.RefreshCatalog();
                Repaint();
            });
        }

        public string BuildDefaultParams(MethodInfo method)
        {
            var ps = method.GetParameters();
            if (ps.Length == 0) return "{}";

            var parts = ps.Select(p =>
            {
                var defaultVal = p.HasDefaultValue ? p.DefaultValue : GetDefaultForType(p.ParameterType);
                var valStr = defaultVal == null ? "null" :
                    p.ParameterType == typeof(string) ? $"\"{defaultVal}\"" :
                    defaultVal.ToString().ToLower();
                return $"\"{p.Name}\": {valStr}";
            });

            return "{\n  " + string.Join(",\n  ", parts) + "\n}";
        }

        private object GetDefaultForType(System.Type t)
        {
            if (t == typeof(string)) return "";
            if (t == typeof(int) || t == typeof(float)) return 0;
            if (t == typeof(bool)) return false;
            return null;
        }

        // ===== first-run permission toast =====

        private void MaybeShowFirstRunToast()
        {
            if (EditorPrefs.HasKey(PrefKeyFirstRunToast)) return;
            // A mode has already been explicitly chosen -> this isn't a fresh install's first launch, no need to prompt.
            if (EditorPrefs.HasKey("UnitySkills_OperatingMode")) return;
            if (PermissionUiHelpers.IsExistingInstall()) return;

            // Set the flag before showing the dialog: the dialog runs inside delayCall, and the user might close the window in the meantime; writing the pref immediately guarantees
            // this won't re-trigger "regardless of whether the dialog actually appeared."
            EditorPrefs.SetBool(PrefKeyFirstRunToast, true);

            EditorApplication.delayCall += () =>
            {
                string title = SkillsLocalization.Get("perm_first_run_toast_title");
                string msg = SkillsLocalization.Get("perm_first_run_toast_msg");
                string openBtn = SkillsLocalization.Get("perm_first_run_toast_open");
                string okBtn = SkillsLocalization.Get("perm_first_run_toast_dismiss");

                if (EditorUtility.DisplayDialog(title, msg, openBtn, okBtn))
                {
                    // The main window + Settings drawer is the single entry point for permission UI.
                    // delayCall lets CreateGUI finish first, so OpenSettings can obtain the drawer reference.
                    var window = GetWindow<UnitySkillsWindow>(SkillsLocalization.Get("window_title"));
                    window.minSize = new Vector2(420, 480);
                    EditorApplication.delayCall += () => window.OpenSettings();
                }
            };
        }
    }

    /// <summary>
    /// Shared helpers for the permission/audit panels.
    /// Centralizes Localization fallback and "existing install" detection, keeping the EditorWindow implementations thin.
    /// </summary>
    internal static class PermissionUiHelpers
    {
        /// <summary>
        /// UI-side determination kept in sync with <c>SkillsModeManager</c>'s internal IsExistingInstall, used to decide whether to hide the first-run toast for existing
        /// users; just keep the two key lists consistent.
        /// </summary>
        public static bool IsExistingInstall()
        {
            return EditorPrefs.HasKey("UnitySkills_RequireConfirmation")
                || EditorPrefs.HasKey("UnitySkills_PreferredPort")
                || EditorPrefs.HasKey("UnitySkills_LogLevel")
                || EditorPrefs.HasKey("UnitySkills_Language")
                || EditorPrefs.HasKey("UnitySkills_RequestTimeoutMinutes")
                || EditorPrefs.HasKey("UnitySkills_KeepAliveIntervalSeconds")
                || EditorPrefs.HasKey("UnitySkills_AutoInstallPackagesOnStartup");
        }

        public static string FormatCountdown(DateTime expiresAtUtc)
        {
            var remaining = expiresAtUtc - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0) return "expired";
            if (remaining.TotalMinutes >= 1)
                return $"{(int)remaining.TotalMinutes}m{remaining.Seconds:00}s";
            return $"{(int)remaining.TotalSeconds}s";
        }

        public static string ShortToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            return token.Length <= 6 ? token : token.Substring(0, 6);
        }
    }

    /// <summary>
    /// Audit log viewer -- a console-style list implemented with UI Toolkit / UXML.
    /// Toolbar (path + Reveal + Refresh) -> Filter (search + type dropdown + count) -> ListView (icon+time+badge+summary) -> Detail (raw JSON).
    /// Entry point: main window -> gear -> Settings Drawer -> Permissions group -> [View Audit Log].
    /// Not mounted as its own menu item, to avoid Window/UnitySkills submenu sprawl.
    /// </summary>
    public sealed class UnitySkillsAuditWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/AuditLogWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/AuditLogWindow.uss";
        // Single source of theme variables (--color-*): the main window's USS loads before this window's USS (same pattern as UnityCliWindow).
        private const string ThemeUssPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";
        private const int MaxEntries = 500;

        // Type-filter dropdown options; "All" means no filtering. New event types get appended here after being added to AuditLog.
        // revoke / revoke_all are kept for compatibility with old logs.
        private static readonly string[] _typeOptions = new[]
        {
            "All",
            "call", "mode_restricted_hit", "mode_changed",
            "grant", "grant_executed", "approve", "deny",
            "allowlist_add", "allowlist_remove", "allowlist_clear", "allowlist_migrated",
            "audit_deleted", "audit_cleared",
            "revoke", "revoke_all",
        };

        private TextField     _pathField;
        private TextField     _searchField;
        private DropdownField _typeFilter;
        private Label         _countLabel;
        private ListView      _list;
        private Label         _detailTitle;
        private TextField     _detailJson;

        private string _logPath = "";
        private readonly List<AuditEntry> _all = new List<AuditEntry>();
        private List<AuditEntry> _filtered = new List<AuditEntry>();

        public static void ShowWindow()
        {
            var w = GetWindow<UnitySkillsAuditWindow>(
                SkillsLocalization.Get("perm_audit_window_title"));
            w.minSize = new Vector2(720, 480);
            w.Focus();
        }

        // ----- Language follow: the whole tree rebuilds (including the window title) when the main panel switches language -----

        private void OnEnable() => SkillsLocalization.LanguageChanged += RebuildForLanguage;
        private void OnDisable() => SkillsLocalization.LanguageChanged -= RebuildForLanguage;

        private void RebuildForLanguage()
        {
            titleContent = new GUIContent(
                SkillsLocalization.Get("perm_audit_window_title"));
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
            else Debug.LogWarning($"[UnitySkills] Failed to load Audit USS: {UssPath}");

            // Bundled CJK font — fixes the macOS shared-atlas glyph drop (see UISkillsFont).
            UISkillsFont.Apply(rootVisualElement);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load Audit UXML: {UxmlPath}");
                return;
            }
            uxml.CloneTree(rootVisualElement);

            _pathField   = rootVisualElement.Q<TextField>("audit-path-field");
            _searchField = rootVisualElement.Q<TextField>("audit-search");
            _typeFilter  = rootVisualElement.Q<DropdownField>("audit-type-filter");
            _countLabel  = rootVisualElement.Q<Label>("audit-count-label");
            _list        = rootVisualElement.Q<ListView>("audit-list");
            _detailTitle = rootVisualElement.Q<Label>("audit-detail-title");
            _detailJson  = rootVisualElement.Q<TextField>("audit-detail-json");

            var revealBtn  = rootVisualElement.Q<Button>("audit-reveal-btn");
            var refreshBtn = rootVisualElement.Q<Button>("audit-refresh-btn");
            var clearBtn   = rootVisualElement.Q<Button>("audit-clear-btn");
            var pathLabel  = rootVisualElement.Q<Label>("audit-path-label");

            if (pathLabel != null)
                pathLabel.text = SkillsLocalization.Get("perm_log_path_label");
            if (revealBtn != null)
            {
                revealBtn.text = SkillsLocalization.Get("perm_open_in_explorer");
                revealBtn.clicked += () =>
                {
                    if (!string.IsNullOrEmpty(_logPath))
                        EditorUtility.RevealInFinder(_logPath);
                };
            }
            if (refreshBtn != null)
            {
                refreshBtn.text = SkillsLocalization.Get("perm_refresh");
                refreshBtn.clicked += Reload;
            }
            if (clearBtn != null)
            {
                clearBtn.text = SkillsLocalization.Get("perm_audit_clear_all");
                clearBtn.tooltip = SkillsLocalization.Get("perm_audit_clear_all_tip");
                clearBtn.clicked += OnClearAllClicked;
            }

            if (_searchField != null)
            {
                _searchField.tooltip = SkillsLocalization.Get("perm_audit_search_tip");
                _searchField.RegisterValueChangedCallback(_ => ApplyFilter());
            }

            if (_typeFilter != null)
            {
                _typeFilter.choices = new List<string>(_typeOptions);
                _typeFilter.SetValueWithoutNotify(_typeOptions[0]);
                _typeFilter.RegisterValueChangedCallback(_ => ApplyFilter());
            }

            if (_detailTitle != null)
                _detailTitle.text = SkillsLocalization.Get("perm_audit_select_hint");

            if (_detailJson != null)
            {
                _detailJson.multiline = true;
                _detailJson.isReadOnly = true;
            }

            if (_list != null)
            {
                _list.fixedItemHeight = 22;
                _list.makeItem = MakeRow;
                _list.bindItem = BindRow;
                _list.selectionType = SelectionType.Single;
                // Unity 6 / 2022.2+ uses selectedIndicesChanged; the old API still works but is obsolete.
                _list.selectedIndicesChanged += _ => RefreshDetail();
            }

            Reload();
        }

        private void Reload()
        {
            try { _logPath = SkillsAuditLog.GetLogPath() ?? ""; }
            catch (Exception ex) { _logPath = $"<{ex.Message}>"; }
            if (_pathField != null) _pathField.SetValueWithoutNotify(_logPath);

            _all.Clear();
            try
            {
                var raw = SkillsAuditLog.ReadRecent(MaxEntries);
                if (raw != null)
                {
                    foreach (var item in raw)
                    {
                        var entry = ParseEntry(item as Newtonsoft.Json.Linq.JObject);
                        if (entry != null) _all.Add(entry);
                    }
                }
                // Newest on top
                _all.Reverse();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AuditLog UI reload failed: {ex.Message}");
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string q = (_searchField?.value ?? "").Trim();
            string type = _typeFilter?.value ?? "All";
            bool typeAll = string.Equals(type, "All", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(q) && typeAll)
            {
                _filtered = new List<AuditEntry>(_all);
            }
            else
            {
                var qLower = q.ToLowerInvariant();
                _filtered = _all.Where(e =>
                {
                    if (!typeAll && !string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
                    if (qLower.Length == 0) return true;
                    return ContainsIgnoreCase(e.Skill, qLower)
                        || ContainsIgnoreCase(e.GrantToken, qLower)
                        || ContainsIgnoreCase(e.Token, qLower)
                        || ContainsIgnoreCase(e.ArgsSummary, qLower)
                        || ContainsIgnoreCase(e.RawJson, qLower);
                }).ToList();
            }

            if (_list != null)
            {
                _list.itemsSource = _filtered;
                _list.Rebuild();
                _list.ClearSelection();
            }
            if (_countLabel != null)
            {
                _countLabel.text = string.Format(
                    SkillsLocalization.Get("perm_audit_count_fmt"),
                    _filtered.Count, _all.Count);
            }
            RefreshDetail();
        }

        private static bool ContainsIgnoreCase(string s, string qLower)
        {
            return !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(qLower);
        }

        private void RefreshDetail()
        {
            int idx = _list?.selectedIndex ?? -1;
            if (idx < 0 || idx >= _filtered.Count)
            {
                if (_detailTitle != null)
                    _detailTitle.text = SkillsLocalization.Get("perm_audit_select_hint");
                if (_detailJson != null) _detailJson.SetValueWithoutNotify("");
                return;
            }
            var entry = _filtered[idx];
            if (_detailTitle != null)
                _detailTitle.text = $"[{entry.ShortTime}]  {entry.Type}";
            if (_detailJson != null)
                _detailJson.SetValueWithoutNotify(PrettifyJson(entry.RawJson));
        }

        private static string PrettifyJson(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            try
            {
                var tok = Newtonsoft.Json.Linq.JToken.Parse(raw);
                return tok.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch { return raw; }
        }

        // ===== ListView row rendering =====

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("audit-row");

            var icon = new Label { name = "row-icon" };
            icon.AddToClassList("audit-row__icon");
            row.Add(icon);

            var time = new Label { name = "row-time" };
            time.AddToClassList("audit-row__time");
            row.Add(time);

            var badge = new Label { name = "row-badge" };
            badge.AddToClassList("audit-row__badge");
            row.Add(badge);

            var skill = new Label { name = "row-skill" };
            skill.AddToClassList("audit-row__skill");
            row.Add(skill);

            var suffix = new Label { name = "row-suffix" };
            suffix.AddToClassList("audit-row__suffix");
            row.Add(suffix);

            // Per-row delete button. ListView reuses row instances, so we register the
            // click handler ONCE here and resolve the current entry via the button's
            // userData (rebound on every BindRow). This avoids stacking duplicate
            // handlers on each rebind.
            var del = new Button { name = "row-delete", text = "X" };
            del.AddToClassList("audit-row__delete");
            del.tooltip = SkillsLocalization.Get("perm_audit_delete_row");
            del.clicked += () => OnDeleteRowClicked(del.userData as AuditEntry);
            row.Add(del);

            return row;
        }

        private void BindRow(VisualElement el, int index)
        {
            if (index < 0 || index >= _filtered.Count) return;
            var e = _filtered[index];

            var icon   = el.Q<Label>("row-icon");
            var time   = el.Q<Label>("row-time");
            var badge  = el.Q<Label>("row-badge");
            var skill  = el.Q<Label>("row-skill");
            var suffix = el.Q<Label>("row-suffix");
            var del    = el.Q<Button>("row-delete");

            if (icon != null   && icon.text   != e.Icon)    icon.text   = e.Icon;
            if (time != null   && time.text   != e.ShortTime) time.text = e.ShortTime;
            if (skill != null  && skill.text  != e.Summary) skill.text  = e.Summary;
            if (suffix != null && suffix.text != e.Suffix)  suffix.text = e.Suffix;
            if (badge != null)
            {
                if (badge.text != e.BadgeText) badge.text = e.BadgeText;
                ClearBadgeClass(badge);
                badge.AddToClassList(e.BadgeClass);
            }
            // Rebind the entry reference the row's delete handler reads via userData.
            if (del != null) del.userData = e;
        }

        private static void ClearBadgeClass(Label badge)
        {
            badge.RemoveFromClassList("badge-allow");
            badge.RemoveFromClassList("badge-restricted");
            badge.RemoveFromClassList("badge-forbidden");
            badge.RemoveFromClassList("badge-mode");
            badge.RemoveFromClassList("badge-grant");
            badge.RemoveFromClassList("badge-deny");
            badge.RemoveFromClassList("badge-revoke");
            badge.RemoveFromClassList("badge-other");
        }

        // ===== Entry parsing =====

        /// <summary>A strongly-typed projection of each audit event; only the fields the UI displays are picked out, the raw JSON is still kept in RawJson.</summary>
        private sealed class AuditEntry
        {
            public string Ts;
            public string ShortTime;
            public string Type;
            public string Skill;
            public string Mode;
            public string SkillMode;
            public string Result;
            public string GrantToken;
            public string Token;
            public string Channel;
            public string Source;
            public string ArgsSummary;
            public int? TokenAgeSec;
            public int? Count;
            public string RawJson;

            public string Icon;
            public string BadgeText;
            public string BadgeClass;
            public string Summary;
            public string Suffix;
        }

        private static AuditEntry ParseEntry(Newtonsoft.Json.Linq.JObject obj)
        {
            if (obj == null) return null;
            var e = new AuditEntry
            {
                Ts          = obj["ts"]?.ToString(),
                Type        = obj["type"]?.ToString(),
                Skill       = obj["skill"]?.ToString(),
                Mode        = obj["mode"]?.ToString(),
                SkillMode   = obj["skillMode"]?.ToString(),
                Result      = obj["result"]?.ToString(),
                GrantToken  = obj["grantToken"]?.ToString(),
                Token       = obj["token"]?.ToString(),
                Channel     = obj["channel"]?.ToString(),
                Source      = obj["source"]?.ToString(),
                ArgsSummary = obj["argsSummary"]?.ToString(),
                TokenAgeSec = (int?)obj["tokenAgeSec"],
                Count       = (int?)obj["count"],
                RawJson     = obj.ToString(Newtonsoft.Json.Formatting.None),
            };
            e.ShortTime = FormatShortTime(e.Ts);
            ApplyTypeStyle(e);
            e.Summary = BuildSummary(e);
            e.Suffix  = BuildSuffix(e);
            return e;
        }

        private static string FormatShortTime(string isoTs)
        {
            if (string.IsNullOrEmpty(isoTs)) return "";
            if (DateTime.TryParse(isoTs, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToLocalTime().ToString("HH:mm:ss");
            }
            // Fallback for a parse failure: the 8 characters after "T" in an ISO string are usually HH:mm:ss.
            return isoTs.Length >= 19 ? isoTs.Substring(11, 8) : isoTs;
        }

        private static void ApplyTypeStyle(AuditEntry e)
        {
            switch (e.Type)
            {
                case "call":
                    if (e.Result == "allowed")
                    { e.Icon = ">"; e.BadgeText = "CALL ALLOW";    e.BadgeClass = "badge-allow"; }
                    else if (e.Result == "restricted")
                    { e.Icon = "!"; e.BadgeText = "CALL RESTRICT"; e.BadgeClass = "badge-restricted"; }
                    else if (e.Result == "forbidden")
                    { e.Icon = "x"; e.BadgeText = "CALL FORBID";   e.BadgeClass = "badge-forbidden"; }
                    else
                    { e.Icon = "*"; e.BadgeText = "CALL";          e.BadgeClass = "badge-other"; }
                    break;
                case "mode_restricted_hit": e.Icon = "!"; e.BadgeText = "RESTRICTED"; e.BadgeClass = "badge-restricted"; break;
                case "mode_changed":        e.Icon = "M"; e.BadgeText = "MODE";       e.BadgeClass = "badge-mode";       break;
                case "grant":               e.Icon = "+"; e.BadgeText = "GRANT";      e.BadgeClass = "badge-grant";      break;
                case "grant_executed":      e.Icon = ">"; e.BadgeText = "GRANT EXEC"; e.BadgeClass = "badge-grant";      break;
                case "approve":             e.Icon = "+"; e.BadgeText = "APPROVE";    e.BadgeClass = "badge-grant";      break;
                case "deny":                e.Icon = "x"; e.BadgeText = "DENY";       e.BadgeClass = "badge-deny";       break;
                case "allowlist_add":       e.Icon = "+"; e.BadgeText = "ALLOW +";    e.BadgeClass = "badge-allow";      break;
                case "allowlist_remove":    e.Icon = "-"; e.BadgeText = "ALLOW -";    e.BadgeClass = "badge-revoke";     break;
                case "allowlist_clear":     e.Icon = "C"; e.BadgeText = "ALLOW CLR";  e.BadgeClass = "badge-revoke";     break;
                case "allowlist_migrated":  e.Icon = "^"; e.BadgeText = "MIGRATED";   e.BadgeClass = "badge-mode";       break;
                case "audit_deleted":       e.Icon = "x"; e.BadgeText = "AUDIT DEL";  e.BadgeClass = "badge-revoke";     break;
                case "audit_cleared":       e.Icon = "X"; e.BadgeText = "AUDIT CLR";  e.BadgeClass = "badge-deny";       break;
                case "revoke":              e.Icon = "<"; e.BadgeText = "REVOKE";     e.BadgeClass = "badge-revoke";     break;
                case "revoke_all":          e.Icon = "<<";e.BadgeText = "REVOKE ALL"; e.BadgeClass = "badge-revoke";     break;
                default:
                    e.Icon = "*";
                    e.BadgeText = e.Type?.ToUpperInvariant() ?? "?";
                    e.BadgeClass = "badge-other";
                    break;
            }
        }

        private static string BuildSummary(AuditEntry e)
        {
            switch (e.Type)
            {
                case "mode_changed": return $"-> {e.Mode ?? "?"}";
                case "revoke_all":   return $"{(e.Count?.ToString() ?? "?")} skills";
                default:             return string.IsNullOrEmpty(e.Skill) ? "" : e.Skill;
            }
        }

        private static string BuildSuffix(AuditEntry e)
        {
            var parts = new List<string>();
            if (e.Type == "call" && !string.IsNullOrEmpty(e.Mode))
                parts.Add($"{e.Mode}/{e.SkillMode ?? "?"}");
            if (!string.IsNullOrEmpty(e.GrantToken))
                parts.Add($"#{ShortTokenLocal(e.GrantToken)}");
            if (!string.IsNullOrEmpty(e.Token))
                parts.Add($"#{ShortTokenLocal(e.Token)}");
            if (!string.IsNullOrEmpty(e.Channel))
                parts.Add(e.Channel);
            if (!string.IsNullOrEmpty(e.Source))
                parts.Add(e.Source);
            if (e.TokenAgeSec.HasValue)
                parts.Add($"{e.TokenAgeSec}s");
            return string.Join(" · ", parts);
        }

        private static string ShortTokenLocal(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            return t.Length <= 8 ? t : t.Substring(0, 8) + "…";
        }

        // ===== Delete actions =====

        private void OnDeleteRowClicked(AuditEntry entry)
        {
            if (entry == null) return;
            string ok = SkillsLocalization.Get("perm_audit_delete_ok");
            string cancel = SkillsLocalization.Get("perm_audit_delete_cancel");
            string title  = SkillsLocalization.Get("perm_audit_delete_row");
            string msg = string.Format(
                SkillsLocalization.Get("perm_audit_delete_row_confirm_fmt"),
                entry.ShortTime, entry.Type ?? "?", entry.Summary ?? "");
            if (!EditorUtility.DisplayDialog(title, msg, ok, cancel)) return;

            try
            {
                int removed = SkillsAuditLog.DeleteEntry(entry.Ts, entry.Type);
                if (removed <= 0)
                {
                    EditorUtility.DisplayDialog(title,
                        SkillsLocalization.Get("perm_audit_delete_not_found"),
                        SkillsLocalization.Get("dialog_ok"));
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), ex.Message, SkillsLocalization.Get("dialog_ok"));
            }
            Reload();
        }

        private void OnClearAllClicked()
        {
            string title = SkillsLocalization.Get("perm_audit_clear_all");
            string msg = SkillsLocalization.Get("perm_audit_clear_all_confirm");
            if (!EditorUtility.DisplayDialog(title, msg,
                    SkillsLocalization.Get("perm_audit_clear_ok"),
                    SkillsLocalization.Get("perm_audit_delete_cancel")))
                return;

            try
            {
                SkillsAuditLog.ClearAll();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(SkillsLocalization.Get("dialog_error"), ex.Message, SkillsLocalization.Get("dialog_ok"));
            }
            Reload();
        }
    }
}

// Producer:Betsy
