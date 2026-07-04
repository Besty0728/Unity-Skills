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
    /// Unity Editor Window — UnitySkills v2 layout.
    /// Topbar (server status + URL + toggle + settings) — persistent.
    /// 3 tabs: Skills / AI Config / History.
    /// Footer: version + live stats pill + segmented language switch.
    /// Settings panel: slide-in drawer from the right.
    /// </summary>
    public class UnitySkillsWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";

        // v1.9 一次性 first-run toast 标记。
        // 仅在 "新安装 + 未设过 OperatingMode" 时弹出，避免老用户/已配置用户被打扰。
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

        // ----- Sub-controllers -----
        private TopbarController         _topbar;
        private FooterController         _footer;
        private SettingsDrawerController _drawer;
        private PendingApprovalBannerController _pendingBanner;
        private SkillsTabController      _skillsController;
        private AIConfigTabController    _configController;
        private HistoryTabController     _historyController;

        // ----- Tab strip -----
        private VisualElement[] _tabContents;
        private Button[]        _tabButtons;
        private VisualElement[] _tabUnderlines;

        // ----- Live tick — routed through EditorUiScheduler to avoid mutating the
        // visual tree during repaint/generateVisualContent (issue #44); paused on disable
        // so a closed window can't keep queuing refreshes. -----
        private IVisualElementScheduledItem _liveUpdateItem;

        [MenuItem("Window/UnitySkills")]
        public static void ShowWindow()
        {
            var window = GetWindow<UnitySkillsWindow>("UnitySkills");
            window.minSize = new Vector2(420, 480);
        }

        private void OnEnable()
        {
            RefreshSkillsList();
            // v1.9：模式/授权变化时联动 topbar/footer 的下次重绘，避免分别在每个子 Controller 里订阅。
            SkillsModeManager.OnChanged += Repaint;
            MaybeShowFirstRunToast();
        }

        private void OnDisable()
        {
            SkillsModeManager.OnChanged -= Repaint;
            _liveUpdateItem?.Pause();
            _liveUpdateItem = null;
        }

        public void CreateGUI()
        {
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

            _skillsController  = new SkillsTabController(_tabContents[0], this);
            _configController  = new AIConfigTabController(_tabContents[1], this);
            _historyController = new HistoryTabController(_tabContents[2], this);

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
            _tabButtons    = new Button[3];
            _tabContents   = new VisualElement[3];
            _tabUnderlines = new VisualElement[3];
            for (int i = 0; i < 3; i++)
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
        }

        /// <summary>
        /// Called when user clicks a skill in Skills Tab — now stays within the
        /// Skills tab (master-detail) instead of jumping to a separate "Test" tab.
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
        }

        // ----- Language switch (called by FooterController) -----
        public void SetLanguage(SkillsLocalization.Language lang)
        {
            if (SkillsLocalization.Current == lang) return;
            SkillsLocalization.Current = lang;
            RefreshLocalization();
        }

        public void RefreshLocalization()
        {
            // Main tabs
            if (_tabButtons[0] != null) _tabButtons[0].text = SkillsLocalization.Get("tab_skills");
            if (_tabButtons[1] != null) _tabButtons[1].text = SkillsLocalization.Get("tab_ai_config");
            if (_tabButtons[2] != null) _tabButtons[2].text = SkillsLocalization.Get("tab_history");

            _topbar?.RefreshLocalization();
            _footer?.RefreshLocalization();
            _drawer?.RefreshLocalization();
            _pendingBanner?.RefreshLocalization();
            _skillsController?.RefreshLocalization();
            _configController?.RefreshLocalization();
            _historyController?.RefreshLocalization();
        }

        // ===== Skill catalog (preserved API for controllers) =====

        public void RefreshSkillsList()
        {
            _skillsByCategory = new Dictionary<string, List<SkillInfo>>();
            var allTypes = SkillsCommon.GetAllLoadedTypes();

            foreach (var type in allTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    UnitySkillAttribute attr;
                    try { attr = method.GetCustomAttribute<UnitySkillAttribute>(); }
                    catch { continue; }
                    if (attr == null) continue;

                    var category = type.Name.Replace("Skills", "");
                    if (!_skillsByCategory.ContainsKey(category))
                        _skillsByCategory[category] = new List<SkillInfo>();

                    _skillsByCategory[category].Add(new SkillInfo
                    {
                        Name = attr.Name ?? method.Name,
                        Description = attr.Description ?? "",
                        Method = method
                    });
                }
            }
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

        // ===== v1.9 first-run permission toast =====

        private void MaybeShowFirstRunToast()
        {
            if (EditorPrefs.HasKey(PrefKeyFirstRunToast)) return;
            // 已显式选过模式 → 不是新安装的首启，无需提示。
            if (EditorPrefs.HasKey("UnitySkills_OperatingMode")) return;
            if (PermissionUiHelpers.IsExistingInstall()) return;

            // 先落标记再弹窗：弹窗在 delayCall 里执行，期间用户可能关闭窗口；
            // 立即写 pref 保证"无论是否真弹出"都不会重复触发。
            EditorPrefs.SetBool(PrefKeyFirstRunToast, true);

            EditorApplication.delayCall += () =>
            {
                string title = PermissionUiHelpers.L("perm_first_run_toast_title",
                    "UnitySkills v1.9", "UnitySkills v1.9");
                string msg = PermissionUiHelpers.L("perm_first_run_toast_msg",
                    "Auto mode is the default for fresh installs (v1.9). FullAuto skills run directly; only high-risk operations (NeverInSemi) are blocked. Open the UnitySkills window and use the gear icon to switch to Approval (per-skill confirmation) or Bypass (allow all).",
                    "新安装默认 Auto 自动模式（v1.9）。FullAuto skill 直接执行，仅高危操作（NeverInSemi）被拦截。打开 UnitySkills 主窗口，点击右上角设置齿轮抽屉，可切换到 Approval（逐项审批）或 Bypass（全部放行）。");
                string openBtn = PermissionUiHelpers.L("perm_first_run_toast_open",
                    "Open Permissions", "打开权限面板");
                string okBtn = PermissionUiHelpers.L("perm_first_run_toast_dismiss",
                    "OK", "知道了");

                if (EditorUtility.DisplayDialog(title, msg, openBtn, okBtn))
                {
                    // 主窗口 + Settings 抽屉作为权限 UI 唯一入口。
                    // delayCall 让 CreateGUI 先完成，OpenSettings 才能拿到 drawer 引用。
                    var window = GetWindow<UnitySkillsWindow>("UnitySkills");
                    window.minSize = new Vector2(420, 480);
                    EditorApplication.delayCall += () => window.OpenSettings();
                }
            };
        }
    }

    /// <summary>
    /// v1.9 权限/审计面板共享小工具。
    /// 集中处理 Localization fallback 与"老安装"判定，让 EditorWindow 实现保持薄。
    /// </summary>
    internal static class PermissionUiHelpers
    {
        /// <summary>
        /// 先查 SkillsLocalization；如果 key 缺失（Get 返回 key 本身），按当前语言走 fallback。
        /// 让 UI 不依赖其他 agent 补译 Localization.cs，后续补 key 自动生效。
        /// </summary>
        public static string L(string key, string enFallback, string cnFallback)
        {
            var v = SkillsLocalization.Get(key);
            if (!string.Equals(v, key, StringComparison.Ordinal)) return v;
            return SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                ? cnFallback : enFallback;
        }

        /// <summary>
        /// 与 <c>SkillsModeManager</c> 内部 IsExistingInstall 同步的 UI 侧判定，
        /// 用于决定是否对老用户隐藏首启 toast；保持两侧 key 列表一致即可。
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
    /// v1.9 审计日志查看器 — UI Toolkit / UXML 实现的控制台风格列表。
    /// Toolbar(路径 + Reveal + Refresh) → Filter(搜索 + 类型下拉 + 计数) → ListView(图标+时间+徽章+摘要) → Detail(原始 JSON)。
    /// 入口：主窗口 → 齿轮 → Settings Drawer → Permissions 组 → [View Audit Log]。
    /// 未单独挂菜单，避免 Window/UnitySkills 子菜单泛滥。
    /// </summary>
    public sealed class UnitySkillsAuditWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/AuditLogWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/AuditLogWindow.uss";
        private const int MaxEntries = 500;

        // 类型筛选下拉选项；"All" 表示不过滤。新事件类型在 AuditLog 添加后同步追加。
        // v1.9 引入了 allowlist_* / grant_executed / audit_* 系列；revoke / revoke_all 保留以兼容旧日志。
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
                PermissionUiHelpers.L("perm_audit_window_title", "UnitySkills Audit Log", "UnitySkills 审计日志"));
            w.minSize = new Vector2(720, 480);
            w.Focus();
        }

        private void CreateGUI()
        {
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
                pathLabel.text = PermissionUiHelpers.L("perm_log_path_label", "Log:", "日志：");
            if (revealBtn != null)
            {
                revealBtn.text = PermissionUiHelpers.L("perm_open_in_explorer",
                    "Reveal", "在资源管理器中打开");
                revealBtn.clicked += () =>
                {
                    if (!string.IsNullOrEmpty(_logPath))
                        EditorUtility.RevealInFinder(_logPath);
                };
            }
            if (refreshBtn != null)
            {
                refreshBtn.text = PermissionUiHelpers.L("perm_refresh", "Refresh", "刷新");
                refreshBtn.clicked += Reload;
            }
            if (clearBtn != null)
            {
                clearBtn.text = PermissionUiHelpers.L("perm_audit_clear_all",
                    "Clear All", "清空全部");
                clearBtn.tooltip = PermissionUiHelpers.L("perm_audit_clear_all_tip",
                    "Permanently delete the entire audit log (including rotated files).",
                    "永久删除整个审计日志（含已滚动文件）。");
                clearBtn.clicked += OnClearAllClicked;
            }

            if (_searchField != null)
            {
                _searchField.tooltip = PermissionUiHelpers.L("perm_audit_search_tip",
                    "Filter by skill name, token or args",
                    "按技能名 / token / 参数过滤");
                _searchField.RegisterValueChangedCallback(_ => ApplyFilter());
            }

            if (_typeFilter != null)
            {
                _typeFilter.choices = new List<string>(_typeOptions);
                _typeFilter.SetValueWithoutNotify(_typeOptions[0]);
                _typeFilter.RegisterValueChangedCallback(_ => ApplyFilter());
            }

            if (_detailTitle != null)
                _detailTitle.text = PermissionUiHelpers.L("perm_audit_select_hint",
                    "Select an entry to view raw JSON",
                    "选择一行查看原始 JSON");

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
                // Unity 6 / 2022.2+ 用 selectedIndicesChanged；老 API 仍兼容但已 obsolete。
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
                // 最新在上
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
                    PermissionUiHelpers.L("perm_audit_count_fmt",
                        "{0} / {1} entries", "{0} / {1} 条"),
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
                    _detailTitle.text = PermissionUiHelpers.L("perm_audit_select_hint",
                        "Select an entry to view raw JSON",
                        "选择一行查看原始 JSON");
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

        // ===== ListView row 渲染 =====

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
            del.tooltip = PermissionUiHelpers.L("perm_audit_delete_row",
                "Delete this entry", "删除该条");
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

        /// <summary>每条审计事件的强类型投影；只挑出 UI 展示用到的字段，原始 JSON 仍保留在 RawJson 里。</summary>
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
            // 解析失败兜底：ISO 字符串里 "T" 后 8 个字符通常就是 HH:mm:ss。
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
            string ok = PermissionUiHelpers.L("perm_audit_delete_ok",  "Delete", "删除");
            string cancel = PermissionUiHelpers.L("perm_audit_delete_cancel", "Cancel", "取消");
            string title  = PermissionUiHelpers.L("perm_audit_delete_row", "Delete this entry", "删除该条");
            string msg = string.Format(
                PermissionUiHelpers.L("perm_audit_delete_row_confirm_fmt",
                    "Delete this audit entry?\n\n[{0}]  {1}\n{2}",
                    "确定删除这条审计记录吗？\n\n[{0}]  {1}\n{2}"),
                entry.ShortTime, entry.Type ?? "?", entry.Summary ?? "");
            if (!EditorUtility.DisplayDialog(title, msg, ok, cancel)) return;

            try
            {
                int removed = SkillsAuditLog.DeleteEntry(entry.Ts, entry.Type);
                if (removed <= 0)
                {
                    EditorUtility.DisplayDialog(title,
                        PermissionUiHelpers.L("perm_audit_delete_not_found",
                            "Entry not found in the active log (it may have already been rotated).",
                            "未在当前日志中找到该条（可能已被滚动归档）。"),
                        "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
            Reload();
        }

        private void OnClearAllClicked()
        {
            string title = PermissionUiHelpers.L("perm_audit_clear_all", "Clear All", "清空全部");
            string msg = PermissionUiHelpers.L("perm_audit_clear_all_confirm",
                "Permanently delete the entire audit log (including rotated history files)?\n\nThis cannot be undone. The wipe itself will be recorded as a fresh 'audit_cleared' entry.",
                "确定永久删除整个审计日志（含历史滚动文件）吗？\n\n该操作不可撤销。清空动作本身会作为新的 audit_cleared 事件留痕。");
            if (!EditorUtility.DisplayDialog(title, msg,
                    PermissionUiHelpers.L("perm_audit_clear_ok", "Clear All", "清空"),
                    PermissionUiHelpers.L("perm_audit_delete_cancel", "Cancel", "取消")))
                return;

            try
            {
                SkillsAuditLog.ClearAll();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Error", ex.Message, "OK");
            }
            Reload();
        }
    }
}
