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
        private ListView      _skillsList;

        private sealed class SkillsListItem
        {
            public bool IsCategory;
            public string CategoryName;
            public int SkillCount;
            public UnitySkillsWindow.SkillInfo Skill;
            public string FoldKey;
        }

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
        private TokenLevelSliderWidget _tokenLevelWidget;

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
            _tokenLevelWidget = new TokenLevelSliderWidget(_root);
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
            _skillsList   = _root.Q<ListView>("skills-list");

            if (_skillsList != null)
            {
                _skillsList.fixedItemHeight = 24f;
                _skillsList.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
                _skillsList.selectionType = SelectionType.None;
                _skillsList.makeItem = MakeSkillsListItem;
                _skillsList.bindItem = BindSkillsListItem;
            }

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
            if (_skillsList == null) return;

            var dict = _window.SkillsByCategory;
            if (dict == null) return;

            // A surface-profile switch (or a token-level preset that changes it) can drop the
            // selected skill's whole category from the catalog while the detail pane / Run
            // button stay live on a skill that no longer exists. FindSkill checks the full
            // catalog rather than the search-filtered rows, so an ordinary search-filter
            // refresh where the skill still exists (just hidden by the current query) never
            // trips this and leaves the selection alone.
            if (!string.IsNullOrEmpty(_selectedSkillName) && FindSkill(_selectedSkillName) == null)
            {
                _selectedSkillName = null;
                ShowEmpty();
            }

            var items = new List<SkillsListItem>();

            int totalShown = 0;
            int categoriesShown = 0;

            foreach (var kvp in dict.OrderBy(k => k.Key))
            {
                var filtered = kvp.Value.Where(MatchesFilter).ToList();
                if (filtered.Count == 0) continue;

                categoriesShown++;
                totalShown += filtered.Count;

                string foldKey = $"UnitySkills_Foldout_{kvp.Key}";
                items.Add(new SkillsListItem
                {
                    IsCategory = true,
                    CategoryName = kvp.Key,
                    SkillCount = filtered.Count,
                    FoldKey = foldKey,
                });

                // The old implementation created a VisualElement for every skill, even when
                // most rows were below the fold. Maximum exposes the full skill surface, so a
                // normal resize could force hundreds of text rows through Yoga on every width
                // change. Keep the same foldout semantics but only materialize visible rows via
                // ListView's fixed-height virtualization.
                if (EditorPrefs.GetBool(foldKey, false))
                {
                    foreach (var skill in filtered)
                    {
                        items.Add(new SkillsListItem
                        {
                            Skill = skill,
                        });
                    }
                }
            }

            _skillsList.itemsSource = items;
            _skillsList.Rebuild();

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

        private VisualElement MakeSkillsListItem()
        {
            var item = new VisualElement();
            item.AddToClassList("skills-list-item");

            var header = new VisualElement { name = "skills-list-category" };
            header.AddToClassList("category-header");
            header.RegisterCallback<ClickEvent>(_ => ToggleCategory(header.userData as SkillsListItem));

            var chevron = new Label { name = "category-chevron" };
            chevron.AddToClassList("chevron");
            header.Add(chevron);

            var nameLabel = new Label { name = "category-name" };
            nameLabel.AddToClassList("flex-grow");
            header.Add(nameLabel);

            var countLabel = new Label { name = "category-count" };
            countLabel.AddToClassList("cat-count");
            header.Add(countLabel);
            item.Add(header);

            var row = new VisualElement { name = "skills-list-skill" };
            row.AddToClassList("skill-row");
            row.RegisterCallback<ClickEvent>(_ =>
            {
                var listItem = row.userData as SkillsListItem;
                if (listItem?.Skill != null) OnSkillSelected(listItem.Skill);
            });

            var skillName = new Label { name = "skill-name" };
            skillName.AddToClassList("skill-row__name");
            row.Add(skillName);

            var badge = new Label { name = "skill-risk-badge" };
            badge.AddToClassList("risk-badge");
            row.Add(badge);
            item.Add(row);

            return item;
        }

        private void BindSkillsListItem(VisualElement element, int index)
        {
            var items = _skillsList?.itemsSource as List<SkillsListItem>;
            if (items == null || index < 0 || index >= items.Count) return;

            var listItem = items[index];
            var header = element.Q<VisualElement>("skills-list-category");
            var row = element.Q<VisualElement>("skills-list-skill");
            if (header == null || row == null) return;

            bool isCategory = listItem.IsCategory;
            header.SetVisible(isCategory);
            row.SetVisible(!isCategory);

            if (isCategory)
            {
                header.userData = listItem;
                var chevron = header.Q<Label>("category-chevron");
                var nameLabel = header.Q<Label>("category-name");
                var countLabel = header.Q<Label>("category-count");
                bool expanded = EditorPrefs.GetBool(listItem.FoldKey, false);
                if (chevron != null) chevron.text = expanded ? "▼" : "▶";
                if (nameLabel != null) nameLabel.text = listItem.CategoryName;
                if (countLabel != null) countLabel.text = listItem.SkillCount.ToString();
                return;
            }

            row.userData = listItem;
            var skill = listItem.Skill;
            var skillName = row.Q<Label>("skill-name");
            var badge = row.Q<Label>("skill-risk-badge");
            bool highRisk = IsHighRisk(skill);
            if (skillName != null) skillName.text = skill.Name;
            if (badge != null)
            {
                badge.text = highRisk ? SkillsLocalization.Get("skills_tag_danger") : "";
                badge.SetVisible(highRisk);
            }
            row.EnableInClassList("selected", skill.Name == _selectedSkillName);
        }

        private void ToggleCategory(SkillsListItem category)
        {
            if (category == null || !category.IsCategory) return;
            bool expanded = EditorPrefs.GetBool(category.FoldKey, false);
            EditorPrefs.SetBool(category.FoldKey, !expanded);
            RebuildList();
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
            if (skill == null) return;
            _selectedSkillName = skill.Name;
            _skillsList?.RefreshItems();

            PopulateDetail(skill, _window.BuildDefaultParams(skill.Method));
        }

        /// <summary>
        /// Redraws the rows from the window's catalog after the catalog itself changed underneath
        /// us — currently a surface-profile switch, which adds or removes whole modules at once.
        /// The window rebuilds its catalog first, then calls this.
        /// </summary>
        public void RefreshCatalog() => RebuildList();

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
            _emptyLabel.SetVisible(false);
            _detailContent.SetVisible(true);

            if (_skillTitle != null) _skillTitle.text = skill.Name;
            RelocalizeDetail(skill);

            if (_paramsField != null) _paramsField.value = defaultParams ?? "{}";
            if (_resultField != null) _resultField.value = "";
            ClearResultError();

            if (_dryRunBtn != null)
            {
                var attr = skill.Method?.GetCustomAttribute<UnitySkillAttribute>();
                _dryRunBtn.SetEnabled(attr == null || attr.SupportsDryRun);
            }
        }

        /// <summary>
        /// Re-resolves the description and tags for the currently displayed skill. Split out of
        /// <see cref="PopulateDetail"/> so a language switch can refresh this text without touching
        /// the params field or a result the user has not cleared yet.
        /// </summary>
        private void RelocalizeDetail(UnitySkillsWindow.SkillInfo skill)
        {
            // Description: prefer localized description by skill name key
            string desc = SkillsLocalization.Get(skill.Name);
            if (desc == skill.Name) desc = skill.Description;
            if (_skillDesc != null) _skillDesc.text = desc ?? "";

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
        }

        private void ShowEmpty()
        {
            _emptyLabel.SetVisible(true);
            _detailContent.SetVisible(false);
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
            _tokenLevelWidget?.RefreshTokenLevelLocalization();

            // Rebuild list to refresh badge texts in active language
            RebuildList();

            // The selected skill's description/tags were resolved once at selection time; re-resolve
            // them here rather than leaving them frozen in whichever language was active back then.
            // Params/result fields are left untouched -- the user may be mid-edit.
            if (!string.IsNullOrEmpty(_selectedSkillName))
            {
                var skill = FindSkill(_selectedSkillName);
                if (skill != null) RelocalizeDetail(skill);
            }
        }

        public void Dispose()
        {
            _tokenLevelWidget?.Dispose();
            _tokenLevelWidget = null;
        }
    }
}

// Producer:Betsy
