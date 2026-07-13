using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Skills Tab — master-detail.
    /// Left:  search + refresh/validate + category foldouts + per-skill row.
    /// Right: selected skill detail + JSON param editor + execute/dryRun + result.
    /// </summary>
    public class SkillsTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/SkillsTab.uxml";

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        // Left pane
        private TextField     _searchField;
        private Button        _refreshBtn;
        private Button        _validateBtn;
        private Label         _countBar;
        private VisualElement _container;

        // Right pane
        private Label         _emptyLabel;
        private VisualElement _detailContent;
        private Label         _skillTitle;
        private VisualElement _skillMeta;
        private Label         _skillDesc;
        private Label         _paramsLabel;
        private TextField     _paramsField;
        private Button        _execBtn;
        private Button        _dryRunBtn;
        private Button        _clearBtn;
        private Label         _resultLabel;
        private TextField     _resultField;

        private string _selectedSkillName;
        private string _filterText = "";

        public SkillsTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load SkillsTab UXML: {TabUxmlPath}");
                return;
            }
            uxml.CloneTree(_root);

            CacheUiReferences();
            BindEvents();
            RebuildList();
            ShowEmpty();
        }

        private void CacheUiReferences()
        {
            _searchField  = _root.Q<TextField>("search-field");
            _refreshBtn   = _root.Q<Button>("refresh-btn");
            _validateBtn  = _root.Q<Button>("validate-btn");
            _countBar     = _root.Q<Label>("skills-count-bar");
            _container    = _root.Q<VisualElement>("skills-container");

            _emptyLabel    = _root.Q<Label>("detail-empty");
            _detailContent = _root.Q<VisualElement>("detail-content");
            _skillTitle    = _root.Q<Label>("skill-title");
            _skillMeta     = _root.Q<VisualElement>("skill-meta");
            _skillDesc     = _root.Q<Label>("skill-desc");
            _paramsLabel   = _root.Q<Label>("detail-params-label");
            _paramsField   = _root.Q<TextField>("detail-params-field");
            _execBtn       = _root.Q<Button>("detail-execute-btn");
            _dryRunBtn     = _root.Q<Button>("detail-dryrun-btn");
            _clearBtn      = _root.Q<Button>("detail-clear-btn");
            _resultLabel   = _root.Q<Label>("detail-result-label");
            _resultField   = _root.Q<TextField>("detail-result-field");

            UISkillsEditorIcons.Apply(_refreshBtn, "d_Refresh", "Refresh", "TreeEditor.Refresh");
        }

        private void BindEvents()
        {
            if (_refreshBtn != null)
                _refreshBtn.clicked += () =>
                {
                    _window.RefreshSkillsList();
                    SkillRouter.Refresh();
                    RebuildList();
                };

            if (_validateBtn != null) _validateBtn.clicked += ValidateSkills;

            if (_searchField != null)
                _searchField.RegisterValueChangedCallback(evt =>
                {
                    _filterText = (evt.newValue ?? "").Trim().ToLowerInvariant();
                    RebuildList();
                });

            if (_execBtn   != null) _execBtn.clicked   += () => Execute(dryRun: false);
            if (_dryRunBtn != null) _dryRunBtn.clicked += () => Execute(dryRun: true);
            if (_clearBtn  != null) _clearBtn.clicked  += () =>
            {
                if (_paramsField != null) _paramsField.value = "";
                if (_resultField != null) _resultField.value = "";
            };
        }

        private void ValidateSkills()
        {
            var issues = SkillRouter.ValidateMetadata();
            if (issues.Count == 0)
            {
                SkillsLogger.Log(SkillsLocalization.Get("metadata_validation_passed"));
            }
            else
            {
                SkillsLogger.Log(string.Format(SkillsLocalization.Get("metadata_validation_found"), issues.Count));
                foreach (var msg in issues)
                {
                    if (msg.StartsWith("[ERROR]")) Debug.LogError($"[UnitySkills] {msg}");
                    else                            Debug.LogWarning($"[UnitySkills] {msg}");
                }
            }
        }

        private void RebuildList()
        {
            if (_container == null) return;
            _container.Clear();

            var dict = _window.SkillsByCategory;
            if (dict == null) return;

            int totalShown = 0;
            int categoriesShown = 0;

            foreach (var kvp in dict.OrderBy(k => k.Key))
            {
                var filtered = kvp.Value.Where(MatchesFilter).ToList();
                if (filtered.Count == 0) continue;

                categoriesShown++;
                totalShown += filtered.Count;

                BuildCategory(kvp.Key, filtered);
            }

            if (_countBar != null)
            {
                _countBar.text = string.Format(
                    SkillsLocalization.Get("skills_count_format"),
                    totalShown, categoriesShown);
            }
        }

        private bool MatchesFilter(UnitySkillsWindow.SkillInfo skill)
        {
            if (string.IsNullOrEmpty(_filterText)) return true;
            if (!string.IsNullOrEmpty(skill.Name) &&
                skill.Name.ToLowerInvariant().Contains(_filterText)) return true;
            if (!string.IsNullOrEmpty(skill.Description) &&
                skill.Description.ToLowerInvariant().Contains(_filterText)) return true;
            return false;
        }

        private void BuildCategory(string categoryName, List<UnitySkillsWindow.SkillInfo> skills)
        {
            string foldKey = $"UnitySkills_Foldout_{categoryName}";
            bool collapsed = !EditorPrefs.GetBool(foldKey, false);

            // Header
            var header = new VisualElement();
            header.AddToClassList("category-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;

            var chevron = new Label(collapsed ? "▶" : "▼");
            chevron.AddToClassList("chevron");
            header.Add(chevron);

            var nameLabel = new Label(categoryName);
            nameLabel.style.flexGrow = 1;
            header.Add(nameLabel);

            var countLabel = new Label(skills.Count.ToString());
            countLabel.AddToClassList("cat-count");
            header.Add(countLabel);

            // Body
            var body = new VisualElement();
            body.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;

            foreach (var skill in skills)
            {
                body.Add(BuildSkillRow(skill));
            }

            header.RegisterCallback<ClickEvent>(_ =>
            {
                bool nowCollapsed = body.style.display == DisplayStyle.Flex;
                body.style.display = nowCollapsed ? DisplayStyle.None : DisplayStyle.Flex;
                chevron.text = nowCollapsed ? "▶" : "▼";
                EditorPrefs.SetBool(foldKey, !nowCollapsed);
            });

            _container.Add(header);
            _container.Add(body);
        }

        private VisualElement BuildSkillRow(UnitySkillsWindow.SkillInfo skill)
        {
            var row = new VisualElement();
            row.AddToClassList("skill-row");
            row.userData = skill;

            var nameLabel = new Label(skill.Name);
            nameLabel.AddToClassList("skill-row__name");
            row.Add(nameLabel);

            if (IsHighRisk(skill))
            {
                var badge = new Label(SkillsLocalization.Get("skills_tag_danger"));
                badge.AddToClassList("risk-badge");
                row.Add(badge);
            }

            if (skill.Name == _selectedSkillName) row.AddToClassList("selected");

            row.RegisterCallback<ClickEvent>(_ => OnSkillSelected(skill));
            return row;
        }

        private bool IsHighRisk(UnitySkillsWindow.SkillInfo skill)
        {
            var attr = skill.Method?.GetCustomAttribute<UnitySkillAttribute>();
            if (attr == null) return false;
            if (attr.RiskLevel == "high") return true;
            if ((attr.Operation & SkillOperation.Delete) != 0) return true;
            return false;
        }

        private void OnSkillSelected(UnitySkillsWindow.SkillInfo skill)
        {
            _selectedSkillName = skill.Name;

            // Update visual selection
            foreach (var r in _root.Query<VisualElement>(className: "skill-row").ToList())
            {
                if (r.userData is UnitySkillsWindow.SkillInfo si && si.Name == skill.Name)
                    r.AddToClassList("selected");
                else
                    r.RemoveFromClassList("selected");
            }

            PopulateDetail(skill, _window.BuildDefaultParams(skill.Method));
        }

        /// <summary>External API — called by main window for SelectTestSkill.</summary>
        public void SelectSkillByName(string skillName, string defaultParams)
        {
            var skill = FindSkill(skillName);
            if (skill == null) return;
            _selectedSkillName = skillName;
            // Refresh row highlight in case category is collapsed/filter hides it
            RebuildList();
            PopulateDetail(skill, defaultParams);
        }

        private UnitySkillsWindow.SkillInfo FindSkill(string name)
        {
            var dict = _window.SkillsByCategory;
            if (dict == null) return null;
            foreach (var list in dict.Values)
            foreach (var s in list)
                if (s.Name == name) return s;
            return null;
        }

        private void PopulateDetail(UnitySkillsWindow.SkillInfo skill, string defaultParams)
        {
            if (_emptyLabel != null)    _emptyLabel.style.display    = DisplayStyle.None;
            if (_detailContent != null) _detailContent.style.display = DisplayStyle.Flex;

            if (_skillTitle != null) _skillTitle.text = skill.Name;

            // Description: prefer localized description by skill name key
            string desc = SkillsLocalization.Get(skill.Name);
            if (desc == skill.Name) desc = skill.Description;
            if (_skillDesc != null) _skillDesc.text = desc ?? "";

            // Meta tags
            if (_skillMeta != null)
            {
                _skillMeta.Clear();
                var attr = skill.Method?.GetCustomAttribute<UnitySkillAttribute>();
                if (attr != null)
                {
                    var catTag = new Label(attr.Category.ToString());
                    catTag.AddToClassList("tag");
                    _skillMeta.Add(catTag);

                    if (IsHighRisk(skill))
                    {
                        var risk = new Label(SkillsLocalization.Get("skills_tag_danger"));
                        risk.AddToClassList("tag");
                        risk.AddToClassList("tag-danger");
                        _skillMeta.Add(risk);
                    }
                }
            }

            if (_paramsField != null) _paramsField.value = defaultParams ?? "{}";
            if (_resultField != null) _resultField.value = "";
            ClearResultError();

            // Enable/disable DryRun based on metadata
            if (_dryRunBtn != null)
            {
                var attr = skill.Method?.GetCustomAttribute<UnitySkillAttribute>();
                _dryRunBtn.SetEnabled(attr == null || attr.SupportsDryRun);
            }
        }

        private void ShowEmpty()
        {
            if (_emptyLabel != null)    _emptyLabel.style.display    = DisplayStyle.Flex;
            if (_detailContent != null) _detailContent.style.display = DisplayStyle.None;
        }

        private void Execute(bool dryRun)
        {
            if (string.IsNullOrEmpty(_selectedSkillName) || _paramsField == null) return;

            string json = _paramsField.value ?? "{}";

            if (dryRun)
            {
                // Inject "dryRun": true into the JSON payload (simple heuristic).
                json = InjectDryRun(json);
            }

            string result = SkillRouter.Execute(_selectedSkillName, json);
            if (_resultField != null) _resultField.value = result ?? "";

            // Heuristic error detection — color the result block
            ClearResultError();
            if (!string.IsNullOrEmpty(result) &&
                (result.Contains("\"ok\": false") || result.Contains("\"error\"")))
            {
                if (_resultField != null) _resultField.AddToClassList("error");
            }
        }

        private void ClearResultError()
        {
            if (_resultField != null) _resultField.RemoveFromClassList("error");
        }

        private static string InjectDryRun(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return "{ \"dryRun\": true }";
            var trimmed = json.TrimEnd();
            int idx = trimmed.LastIndexOf('}');
            if (idx < 0) return json; // malformed — caller will get a clear error from router

            string body = trimmed.Substring(0, idx).TrimEnd();
            bool needsComma = body.Length > 0 && body[body.Length - 1] != '{';
            string sep = needsComma ? "," : "";
            return body + sep + "\n  \"dryRun\": true\n}";
        }

        public void RefreshLocalization()
        {
            if (_searchField != null)
            {
                // TextField has no native placeholder; use tooltip
                _searchField.tooltip = SkillsLocalization.Get("skills_search_placeholder");
            }
            if (_paramsLabel != null) _paramsLabel.text = SkillsLocalization.Get("skills_detail_params_label");
            if (_execBtn != null)     _execBtn.text     = SkillsLocalization.Get("skills_detail_execute");
            if (_dryRunBtn != null)   _dryRunBtn.text   = SkillsLocalization.Get("skills_detail_dryrun");
            if (_clearBtn != null)    _clearBtn.text    = SkillsLocalization.Get("skills_detail_clear");
            if (_resultLabel != null) _resultLabel.text = SkillsLocalization.Get("skills_detail_result_label");
            if (_emptyLabel != null)  _emptyLabel.text  = SkillsLocalization.Get("skills_detail_empty");

            // Rebuild list to refresh badge texts in active language
            RebuildList();
        }
    }
}

// Producer:Betsy
