using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// 多选弹窗：批量把 skill 加入白名单。UXML 模板驱动，i18n 文案。
    /// 支持搜索过滤、按 Category 折叠分组、整组一键勾选；提交时若含高危项合并一次确认。
    /// </summary>
    public class AllowlistPickerWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/AllowlistPickerWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/AllowlistPickerWindow.uss";

        private string _search = string.Empty;
        private readonly HashSet<string> _selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 按 category 分组的候选；CreateGUI 时计算，搜索/勾选变更按需局部刷新。
        private List<IGrouping<string, SkillRouter.SkillInfo>> _grouped;

        private ToolbarSearchField _searchField;
        private ScrollView _scroll;
        private Label _summaryLabel;
        private Button _addBtn;
        private Button _cancelBtn;

        public static void Open()
        {
            var w = GetWindow<AllowlistPickerWindow>(true, SkillsLocalization.Get("perm_picker_title"), true);
            w.minSize = new Vector2(460, 520);
            w.Show();
            w.Focus();
        }

        private void CreateGUI()
        {
            // 先加载候选 —— GetWindow 触发 CreateGUI 时 _grouped 必须就绪，避免首次显示 "All in allowlist" 假象。
            LoadCandidates();

            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);

            // Bundled CJK font — fixes the macOS shared-atlas glyph drop (see UISkillsFont).
            UISkillsFont.Apply(rootVisualElement);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load AllowlistPicker UXML: {UxmlPath}");
                return;
            }
            uxml.CloneTree(rootVisualElement);

            _searchField  = rootVisualElement.Q<ToolbarSearchField>("picker-search");
            _scroll       = rootVisualElement.Q<ScrollView>("picker-scroll");
            _summaryLabel = rootVisualElement.Q<Label>("picker-summary");
            _addBtn       = rootVisualElement.Q<Button>("picker-add-btn");
            _cancelBtn    = rootVisualElement.Q<Button>("picker-cancel-btn");

            if (_cancelBtn != null)
            {
                _cancelBtn.text = SkillsLocalization.Get("perm_picker_cancel");
                _cancelBtn.clicked += Close;
            }
            if (_addBtn != null)
            {
                _addBtn.clicked += OnConfirmAdd;
            }
            if (_searchField != null)
            {
                _searchField.value = _search;
                _searchField.RegisterValueChangedCallback(evt =>
                {
                    _search = evt.newValue ?? string.Empty;
                    RebuildList();
                });
            }

            BuildPresetBar();
            RebuildList();
            UpdateFooter();
        }

        // 预置包快捷栏：在搜索栏与列表之间插入"勾选辅助代码编写包"按钮。
        // 点击预填勾选预置项，用户可继续叠加自选，再走统一的 OnConfirmAdd 提交。
        private void BuildPresetBar()
        {
            var root = rootVisualElement.Q<VisualElement>("picker-root");
            var searchBar = rootVisualElement.Q<VisualElement>("picker-search-bar");
            if (root == null || searchBar == null) return;

            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = 8;
            bar.style.paddingRight = 8;
            bar.style.paddingTop = 4;
            bar.style.paddingBottom = 4;

            var btn = new Button(SelectCodingAssistPreset)
            {
                text = string.Format(
                    SkillsLocalization.Get("perm_picker_preset_coding"),
                    AllowlistPresets.CodingAssist.Length),
                tooltip = SkillsLocalization.Get("perm_picker_preset_tip")
            };
            btn.AddToClassList("picker-btn");
            bar.Add(btn);

            root.Insert(root.IndexOf(searchBar) + 1, bar);
        }

        // 把「辅助代码编写包」中"当前可选"的 skill 预填到勾选集；已在白名单的跳过。
        private void SelectCodingAssistPreset()
        {
            var candidateNames = new HashSet<string>(
                (_grouped ?? new List<IGrouping<string, SkillRouter.SkillInfo>>())
                    .SelectMany(g => g).Select(s => s.Name),
                StringComparer.OrdinalIgnoreCase);

            int already = 0, missing = 0;
            foreach (var name in AllowlistPresets.CodingAssist)
            {
                if (candidateNames.Contains(name)) _selected.Add(name);
                else if (SkillsModeManager.IsInAllowlist(name)) already++;
                else missing++;
            }

            RebuildList();
            UpdateFooter();

            // UpdateFooter 已设"已选 N 个"；若有已在白名单/缺失项，补充说明。
            if ((already > 0 || missing > 0) && _summaryLabel != null)
            {
                var extra = new List<string>();
                if (already > 0)
                    extra.Add(string.Format(SkillsLocalization.Get("perm_picker_preset_already"), already));
                if (missing > 0)
                    extra.Add(string.Format(SkillsLocalization.Get("perm_picker_preset_missing"), missing));
                _summaryLabel.text += "  (" + string.Join(", ", extra) + ")";
            }
        }

        private void LoadCandidates()
        {
            SkillRouter.SkillInfo[] all = null;
            try { all = SkillRouter.GetAllSkillsSnapshot(); }
            catch (Exception ex) { Debug.LogWarning($"[UnitySkills] Picker snapshot failed: {ex.Message}"); }
            all = all ?? Array.Empty<SkillRouter.SkillInfo>();

            _grouped = all
                .Where(s => s != null && !string.IsNullOrEmpty(s.Name) && !SkillsModeManager.IsInAllowlist(s.Name))
                .GroupBy(s => s.Category.ToString())
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RebuildList()
        {
            if (_scroll == null) return;
            _scroll.Clear();

            if (_grouped == null || _grouped.Count == 0)
            {
                AppendEmptyLabel(SkillsLocalization.Get("perm_picker_all_in_allowlist"));
                return;
            }

            string q = _search?.Trim() ?? string.Empty;
            bool hasFilter = q.Length > 0;
            int totalShown = 0;

            foreach (var group in _grouped)
            {
                var visible = group
                    .Where(s => !hasFilter
                                || s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                || group.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (visible.Count == 0) continue;
                totalShown += visible.Count;

                int selectedInGroup = visible.Count(s => _selected.Contains(s.Name));
                string headerSuffix = selectedInGroup > 0
                    ? string.Format(SkillsLocalization.Get("perm_picker_selected_suffix"), selectedInGroup)
                    : string.Empty;
                var foldout = new Foldout
                {
                    text = $"{group.Key}  ({visible.Count}){headerSuffix}",
                    value = hasFilter, // 搜索时默认展开，方便看结果
                };
                foldout.AddToClassList("picker-group");

                var groupOps = new VisualElement();
                groupOps.AddToClassList("picker-group-ops");
                var allBtn = new Button(() => ToggleGroup(visible, true))
                {
                    text = SkillsLocalization.Get("perm_picker_select_all")
                };
                allBtn.AddToClassList("picker-group-btn");
                var noneBtn = new Button(() => ToggleGroup(visible, false))
                {
                    text = SkillsLocalization.Get("perm_picker_clear_group")
                };
                noneBtn.AddToClassList("picker-group-btn");
                groupOps.Add(allBtn);
                groupOps.Add(noneBtn);
                foldout.Add(groupOps);

                foreach (var skill in visible.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                {
                    foldout.Add(BuildSkillRow(skill));
                }
                _scroll.Add(foldout);
            }

            if (totalShown == 0)
            {
                AppendEmptyLabel(string.Format(
                    SkillsLocalization.Get("perm_picker_no_match"), q));
            }
        }

        private void AppendEmptyLabel(string text)
        {
            var lbl = new Label(text);
            lbl.AddToClassList("picker-empty");
            _scroll.Add(lbl);
        }

        private VisualElement BuildSkillRow(SkillRouter.SkillInfo skill)
        {
            var row = new VisualElement();
            row.AddToClassList("picker-row");

            bool highRisk = IsHighRisk(skill);
            var toggle = new Toggle { value = _selected.Contains(skill.Name) };
            toggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue) _selected.Add(skill.Name);
                else _selected.Remove(skill.Name);
                UpdateFooter();
            });
            row.Add(toggle);

            var nameLabel = new Label(skill.Name);
            nameLabel.AddToClassList("picker-row-name");
            row.Add(nameLabel);

            if (highRisk)
            {
                var tag = new Label(SkillsLocalization.Get("perm_picker_high_risk_tag"));
                tag.AddToClassList("picker-row-risk");
                row.Add(tag);
            }
            return row;
        }

        private void ToggleGroup(List<SkillRouter.SkillInfo> skills, bool select)
        {
            foreach (var s in skills)
            {
                if (select) _selected.Add(s.Name);
                else _selected.Remove(s.Name);
            }
            RebuildList();
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (_summaryLabel == null || _addBtn == null) return;
            int n = _selected.Count;
            _summaryLabel.text = n == 0
                ? SkillsLocalization.Get("perm_picker_none_selected")
                : string.Format(SkillsLocalization.Get("perm_picker_n_selected"), n);
            _addBtn.text = n == 0
                ? SkillsLocalization.Get("perm_picker_add_selected")
                : string.Format(SkillsLocalization.Get("perm_picker_add_selected_n"), n);
            _addBtn.SetEnabled(n > 0);
        }

        private void OnConfirmAdd()
        {
            if (_selected.Count == 0) return;

            var lookup = (_grouped ?? new List<IGrouping<string, SkillRouter.SkillInfo>>())
                .SelectMany(g => g)
                .ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

            var highRiskNames = _selected
                .Where(n => lookup.TryGetValue(n, out var s) && IsHighRisk(s))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (highRiskNames.Count > 0)
            {
                string list = string.Join("\n  • ", highRiskNames);
                string title = SkillsLocalization.Get("perm_picker_confirm_title");
                string msg = string.Format(
                    SkillsLocalization.Get("perm_picker_confirm_msg"),
                    highRiskNames.Count, list, _selected.Count);
                string ok = SkillsLocalization.Get("perm_picker_confirm_ok");
                string cancel = SkillsLocalization.Get("perm_picker_confirm_cancel");
                if (!EditorUtility.DisplayDialog(title, msg, ok, cancel))
                    return;
            }

            foreach (var name in _selected)
                SkillsModeManager.AddToAllowlist(name);

            Close();
        }

        private static bool IsHighRisk(SkillRouter.SkillInfo s)
        {
            if (s == null) return false;
            return s.Operation.HasFlag(SkillOperation.Delete)
                || s.MayEnterPlayMode
                || s.MayTriggerReload
                || string.Equals(s.RiskLevel, "high", StringComparison.OrdinalIgnoreCase);
        }
    }
}

// Producer:Betsy
