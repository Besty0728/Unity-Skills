using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Analytics tab — human-facing view over the same skill-execution telemetry that
    /// feeds <c>GET /analytics</c>. Reuses <see cref="SkillTelemetryService.BuildAnalyticsJson"/>
    /// (shared 30s cache), so opening the tab never re-aggregates on top of an in-flight
    /// REST request. Data is pulled only on construction, tab activation, window change,
    /// and explicit Refresh — never on the window's 500ms live tick — so the full-log
    /// read can't land on a repaint frame.
    /// </summary>
    public class AnalyticsTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/AnalyticsTab.uxml";

        // dropdown index → window token passed to BuildAnalyticsJson.
        private static readonly string[] _windowOrder = { "1h", "24h", "7d", "all" };

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label         _title;
        private Label         _windowLabel;
        private DropdownField _windowDropdown;
        private Button        _clearBtn;
        private Button        _revealBtn;
        private Button        _refreshBtn;
        private HelpBox       _disabledBanner;
        private HelpBox       _emptyBanner;
        private VisualElement _summary;
        private Label         _topTitle;
        private VisualElement _topList;
        private Label         _errorProneTitle;
        private VisualElement _errorProneList;
        private Label         _slowestTitle;
        private VisualElement _slowestList;
        private Label         _errorCodesTitle;
        private VisualElement _errorCodesList;
        private Label         _recentTitle;
        private VisualElement _recentList;

        private string _windowToken = "24h";

        public AnalyticsTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load AnalyticsTab UXML: {TabUxmlPath}");
                return;
            }
            uxml.CloneTree(_root);

            CacheUiReferences();
            BindEvents();
            RefreshLocalization(); // ends with Refresh(), which renders the initial data
        }

        private void CacheUiReferences()
        {
            _title            = _root.Q<Label>("analytics-title");
            _windowLabel      = _root.Q<Label>("analytics-window-label");
            _windowDropdown   = _root.Q<DropdownField>("analytics-window-dropdown");
            _clearBtn         = _root.Q<Button>("analytics-clear-btn");
            _revealBtn        = _root.Q<Button>("analytics-reveal-btn");
            _refreshBtn       = _root.Q<Button>("analytics-refresh-btn");
            _disabledBanner   = _root.Q<HelpBox>("analytics-disabled");
            _emptyBanner      = _root.Q<HelpBox>("analytics-empty");
            _summary          = _root.Q<VisualElement>("analytics-summary");
            _topTitle         = _root.Q<Label>("analytics-top-title");
            _topList          = _root.Q<VisualElement>("analytics-top-list");
            _errorProneTitle  = _root.Q<Label>("analytics-errorprone-title");
            _errorProneList   = _root.Q<VisualElement>("analytics-errorprone-list");
            _slowestTitle     = _root.Q<Label>("analytics-slowest-title");
            _slowestList      = _root.Q<VisualElement>("analytics-slowest-list");
            _errorCodesTitle  = _root.Q<Label>("analytics-errorcodes-title");
            _errorCodesList   = _root.Q<VisualElement>("analytics-errorcodes-list");
            _recentTitle      = _root.Q<Label>("analytics-recent-title");
            _recentList       = _root.Q<VisualElement>("analytics-recent-list");

            UISkillsEditorIcons.Apply(_refreshBtn, "d_Refresh", "Refresh", "TreeEditor.Refresh");
            // Trash icon only — long "Clear window" text is reserved for the tooltip / dialog.
            UISkillsEditorIcons.Apply(_clearBtn,
                "TreeEditor.Trash", "d_TreeEditor.Trash", "d_Grid.TrashTool", "Trash");
        }

        private void BindEvents()
        {
            if (_windowDropdown != null)
                _windowDropdown.RegisterValueChangedCallback(evt =>
                {
                    int idx = _windowDropdown.choices.IndexOf(evt.newValue);
                    if (idx < 0 || idx >= _windowOrder.Length) return;
                    if (_windowToken == _windowOrder[idx]) return;
                    _windowToken = _windowOrder[idx];
                    Refresh();
                });

            if (_refreshBtn != null) _refreshBtn.clicked += Refresh;
            if (_clearBtn != null) _clearBtn.clicked += OnClearWindowClicked;

            if (_revealBtn != null)
                _revealBtn.clicked += () =>
                {
                    try
                    {
                        var path = SkillTelemetryService.GetLogPath();
                        if (!string.IsNullOrEmpty(path))
                            EditorUtility.RevealInFinder(path);
                    }
                    catch (Exception ex) { SkillsLogger.LogWarning($"Reveal telemetry log failed: {ex.Message}"); }
                };
        }

        /// <summary>
        /// Clear telemetry for the currently selected window (1h / 24h / 7d / all), after a
        /// confirmation dialog that names the window in the active language. On success the
        /// tab re-aggregates so the empty/summary state updates immediately.
        /// </summary>
        private void OnClearWindowClicked()
        {
            string windowLabel = WindowLabelForToken(_windowToken);
            string title = SkillsLocalization.Get("analytics_clear_confirm_title");
            string msg = string.Equals(_windowToken, "all", StringComparison.Ordinal)
                ? string.Format(SkillsLocalization.Get("analytics_clear_confirm_all_fmt"), windowLabel)
                : string.Format(SkillsLocalization.Get("analytics_clear_confirm_fmt"), windowLabel);
            string ok = SkillsLocalization.Get("analytics_clear_ok");
            string cancel = SkillsLocalization.Get("analytics_clear_cancel");

            if (!EditorUtility.DisplayDialog(title, msg, ok, cancel))
                return;

            object result;
            try
            {
                result = SkillTelemetryService.DeleteWindow(_windowToken);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(title,
                    string.Format(SkillsLocalization.Get("analytics_clear_failed_fmt"), ex.Message),
                    "OK");
                return;
            }

            // DeleteWindow returns an anonymous object; parse via JObject so we don't couple
            // the UI to a private DTO. Any shape mismatch falls through to a soft refresh.
            try
            {
                var jo = result == null ? null : JObject.FromObject(result);
                bool success = jo?["success"]?.Value<bool>() ?? false;
                if (!success)
                {
                    string err = jo?["error"]?.ToString() ?? "unknown error";
                    EditorUtility.DisplayDialog(title,
                        string.Format(SkillsLocalization.Get("analytics_clear_failed_fmt"), err),
                        "OK");
                }
                else
                {
                    int removed = jo?["removed"]?.Value<int>() ?? 0;
                    int remaining = jo?["remaining"]?.Value<int>() ?? 0;
                    // Soft toast via HelpBox empty-banner path: just refresh; the summary
                    // cards will show 0 / empty tables. A dialog is fine for destructive
                    // feedback so the user sees the count.
                    EditorUtility.DisplayDialog(title,
                        string.Format(SkillsLocalization.Get("analytics_clear_done_fmt"), removed, remaining),
                        "OK");
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Analytics clear-result parse failed: {ex.Message}");
            }

            Refresh();
        }

        private static string WindowLabelForToken(string token)
        {
            switch (token)
            {
                case "1h":  return SkillsLocalization.Get("analytics_window_1h");
                case "7d":  return SkillsLocalization.Get("analytics_window_7d");
                case "all": return SkillsLocalization.Get("analytics_window_all");
                default:    return SkillsLocalization.Get("analytics_window_24h");
            }
        }

        /// <summary>Called by the window when this tab becomes active — pulls fresh aggregates.</summary>
        public void OnTabSelected() => Refresh();

        public void Refresh()
        {
            if (_root == null) return;

            JObject data;
            try
            {
                var json = SkillTelemetryService.BuildAnalyticsJson(_windowToken);
                data = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"Analytics UI parse failed: {ex.Message}");
                data = null;
            }

            bool enabled = data?["telemetryEnabled"]?.Value<bool>() ?? true;
            int totalCalls = data?["summary"]?["totalCalls"]?.Value<int>() ?? 0;

            SetDisplay(_disabledBanner, !enabled);
            // "Empty" only when telemetry IS on but nothing landed in the window; when it's off
            // the disabled banner already explains the blank state.
            SetDisplay(_emptyBanner, enabled && totalCalls == 0);

            RebuildSummary(data?["summary"] as JObject);
            RebuildTopSkills(data?["topSkills"] as JArray);
            RebuildErrorProne(data?["errorProneSkills"] as JArray);
            RebuildSlowest(data?["slowestSkills"] as JArray);
            RebuildErrorCodes(data?["errorCodes"] as JArray);
            RebuildRecentErrors(data?["recentErrors"] as JArray);
        }

        public void RefreshLocalization()
        {
            if (_title != null)       _title.text       = SkillsLocalization.Get("analytics_title");
            if (_windowLabel != null) _windowLabel.text = SkillsLocalization.Get("analytics_window_label");
            if (_clearBtn != null)
            {
                // Icon button: never re-fill text (would cover the trash glyph); tooltip carries the label.
                _clearBtn.text = string.Empty;
                _clearBtn.tooltip = SkillsLocalization.Get("analytics_clear_window");
            }
            if (_revealBtn != null)   _revealBtn.text   = SkillsLocalization.Get("analytics_reveal_log");

            if (_disabledBanner != null) _disabledBanner.text = SkillsLocalization.Get("analytics_disabled");
            if (_emptyBanner != null)    _emptyBanner.text    = SkillsLocalization.Get("analytics_empty");

            if (_topTitle != null)        _topTitle.text        = SkillsLocalization.Get("analytics_section_top");
            if (_errorProneTitle != null) _errorProneTitle.text = SkillsLocalization.Get("analytics_section_errorprone");
            if (_slowestTitle != null)    _slowestTitle.text    = SkillsLocalization.Get("analytics_section_slowest");
            if (_errorCodesTitle != null) _errorCodesTitle.text = SkillsLocalization.Get("analytics_section_errorcodes");
            if (_recentTitle != null)     _recentTitle.text     = SkillsLocalization.Get("analytics_section_recent");

            // Dropdown choices are localized labels; preserve the selected window across a language flip.
            if (_windowDropdown != null)
            {
                _windowDropdown.choices = new List<string>
                {
                    SkillsLocalization.Get("analytics_window_1h"),
                    SkillsLocalization.Get("analytics_window_24h"),
                    SkillsLocalization.Get("analytics_window_7d"),
                    SkillsLocalization.Get("analytics_window_all"),
                };
                int idx = Array.IndexOf(_windowOrder, _windowToken);
                if (idx < 0 || idx >= _windowDropdown.choices.Count) idx = 1; // default 24h
                _windowDropdown.SetValueWithoutNotify(_windowDropdown.choices[idx]);
            }

            // Re-render data rows so localized column headers / "none" labels update in place.
            Refresh();
        }

        // ===== section builders =====

        private void RebuildSummary(JObject summary)
        {
            if (_summary == null) return;
            _summary.Clear();
            if (summary == null) return;

            int total   = summary["totalCalls"]?.Value<int>() ?? 0;
            int errors  = summary["errorCalls"]?.Value<int>() ?? 0;
            double rate = summary["errorRate"]?.Value<double>() ?? 0.0;
            int skills  = summary["uniqueSkills"]?.Value<int>() ?? 0;

            _summary.Add(BuildStatCard(SkillsLocalization.Get("analytics_summary_calls"), total.ToString(), null));
            _summary.Add(BuildStatCard(SkillsLocalization.Get("analytics_summary_errors"), errors.ToString(),
                errors > 0 ? "analytics-stat__value--warn" : null));
            _summary.Add(BuildStatCard(SkillsLocalization.Get("analytics_summary_errorrate"), FormatPct(rate),
                rate > 0 ? "analytics-stat__value--warn" : null));
            _summary.Add(BuildStatCard(SkillsLocalization.Get("analytics_summary_skills"), skills.ToString(), null));
        }

        private static VisualElement BuildStatCard(string label, string value, string valueModifierClass)
        {
            var card = new VisualElement();
            card.AddToClassList("analytics-stat");

            var v = new Label(value);
            v.AddToClassList("analytics-stat__value");
            if (!string.IsNullOrEmpty(valueModifierClass)) v.AddToClassList(valueModifierClass);
            card.Add(v);

            var l = new Label(label);
            l.AddToClassList("analytics-stat__label");
            card.Add(l);
            return card;
        }

        private void RebuildTopSkills(JArray rows)
        {
            BuildTable(_topList, rows,
                new[]
                {
                    SkillsLocalization.Get("analytics_col_skill"),
                    SkillsLocalization.Get("analytics_col_calls"),
                    SkillsLocalization.Get("analytics_col_errorrate"),
                    SkillsLocalization.Get("analytics_col_avgms"),
                },
                new[] { 3, 1, 1, 1 },
                r => new[]
                {
                    r["skill"]?.ToString() ?? "",
                    (r["calls"]?.Value<int>() ?? 0).ToString(),
                    FormatPct(r["errorRate"]?.Value<double>() ?? 0.0),
                    (r["avgMs"]?.Value<long>() ?? 0).ToString(),
                },
                r => (r["errorRate"]?.Value<double>() ?? 0.0) > 0);
        }

        private void RebuildErrorProne(JArray rows)
        {
            BuildTable(_errorProneList, rows,
                new[]
                {
                    SkillsLocalization.Get("analytics_col_skill"),
                    SkillsLocalization.Get("analytics_col_calls"),
                    SkillsLocalization.Get("analytics_col_errors"),
                    SkillsLocalization.Get("analytics_col_errorrate"),
                },
                new[] { 3, 1, 1, 1 },
                r => new[]
                {
                    r["skill"]?.ToString() ?? "",
                    (r["calls"]?.Value<int>() ?? 0).ToString(),
                    (r["errors"]?.Value<int>() ?? 0).ToString(),
                    FormatPct(r["errorRate"]?.Value<double>() ?? 0.0),
                },
                _ => true); // every row here is by definition error-prone → warn tint
        }

        private void RebuildSlowest(JArray rows)
        {
            BuildTable(_slowestList, rows,
                new[]
                {
                    SkillsLocalization.Get("analytics_col_skill"),
                    SkillsLocalization.Get("analytics_col_avgms"),
                    SkillsLocalization.Get("analytics_col_maxms"),
                    SkillsLocalization.Get("analytics_col_calls"),
                },
                new[] { 3, 1, 1, 1 },
                r => new[]
                {
                    r["skill"]?.ToString() ?? "",
                    (r["avgMs"]?.Value<long>() ?? 0).ToString(),
                    (r["maxMs"]?.Value<long>() ?? 0).ToString(),
                    (r["calls"]?.Value<int>() ?? 0).ToString(),
                },
                null);
        }

        private void RebuildErrorCodes(JArray rows)
        {
            BuildTable(_errorCodesList, rows,
                new[]
                {
                    SkillsLocalization.Get("analytics_col_code"),
                    SkillsLocalization.Get("analytics_col_count"),
                    SkillsLocalization.Get("analytics_col_skill"),
                },
                new[] { 2, 1, 3 },
                r =>
                {
                    var top = r["topSkills"] as JArray;
                    string skills = top != null ? string.Join(", ", top) : "";
                    return new[]
                    {
                        r["code"]?.ToString() ?? "",
                        (r["count"]?.Value<int>() ?? 0).ToString(),
                        skills,
                    };
                },
                _ => true);
        }

        private void RebuildRecentErrors(JArray rows)
        {
            BuildTable(_recentList, rows,
                new[]
                {
                    "",
                    SkillsLocalization.Get("analytics_col_skill"),
                    SkillsLocalization.Get("analytics_col_code"),
                },
                new[] { 2, 3, 3 },
                r => new[]
                {
                    FormatShortTime(r["ts"]?.ToString()),
                    r["skill"]?.ToString() ?? "",
                    r["errorCode"]?.ToString() ?? "",
                },
                _ => true);
        }

        /// <summary>
        /// Render a header row plus one row per JSON entry into <paramref name="container"/>.
        /// <paramref name="weights"/> are flex-grow ratios per column. <paramref name="warnRow"/>
        /// optionally tints a whole data row (null = never). An empty source shows a single
        /// muted "none" line so the section never looks broken.
        /// </summary>
        private static void BuildTable(VisualElement container, JArray rows, string[] headers, int[] weights,
            Func<JToken, string[]> project, Func<JToken, bool> warnRow)
        {
            if (container == null) return;
            container.Clear();

            container.Add(BuildRow(headers, weights, isHeader: true, warn: false));

            if (rows == null || rows.Count == 0)
            {
                var none = new Label(SkillsLocalization.Get("analytics_none"));
                none.AddToClassList("setting-hint");
                none.style.marginLeft = 4;
                none.style.marginTop = 2;
                container.Add(none);
                return;
            }

            foreach (var r in rows)
            {
                string[] cells = project(r);
                bool warn = warnRow != null && warnRow(r);
                container.Add(BuildRow(cells, weights, isHeader: false, warn: warn));
            }
        }

        private static VisualElement BuildRow(string[] cells, int[] weights, bool isHeader, bool warn)
        {
            var row = new VisualElement();
            row.AddToClassList("analytics-row");
            if (isHeader) row.AddToClassList("analytics-row--header");
            if (warn)     row.AddToClassList("analytics-row--warn");

            for (int i = 0; i < cells.Length; i++)
            {
                var cell = new Label(cells[i]);
                cell.AddToClassList("analytics-cell");
                // First column holds names (skills/codes) — left aligned with ellipsis;
                // the rest are numeric → right aligned via the num modifier.
                if (i > 0) cell.AddToClassList("analytics-cell--num");
                cell.style.flexGrow = (i < weights.Length) ? weights[i] : 1;
                cell.style.flexBasis = 0;
                row.Add(cell);
            }
            return row;
        }

        // ===== helpers =====

        private static string FormatPct(double fraction)
        {
            if (fraction <= 0.0) return "0%";
            double pct = fraction * 100.0;
            // sub-10% error rates keep one decimal — the difference between 0.5% and 5% matters.
            return pct < 10.0 ? pct.ToString("0.#") + "%" : Math.Round(pct).ToString("0") + "%";
        }

        private static string FormatShortTime(string isoTs)
        {
            if (string.IsNullOrEmpty(isoTs)) return "";
            if (DateTime.TryParse(isoTs, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt.ToLocalTime().ToString("HH:mm:ss");
            return isoTs.Length >= 19 ? isoTs.Substring(11, 8) : isoTs;
        }

        private static void SetDisplay(VisualElement el, bool visible)
        {
            if (el == null) return;
            el.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}

// Producer:Betsy
