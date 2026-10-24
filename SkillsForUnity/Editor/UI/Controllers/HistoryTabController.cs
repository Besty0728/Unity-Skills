using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    public class HistoryTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/HistoryTab.uxml";

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label         _historyTitle;
        private Button        _refreshBtn;
        private Button        _clearBtn;
        private HelpBox       _cacheWarning;
        private HelpBox       _resultHelpBox;
        private Label         _activeTitle;
        private VisualElement _activeContainer;
        private Label         _undoneTitle;
        private VisualElement _undoneContainer;

        private Foldout      _autoCleanFoldout;
        private Toggle       _autoCleanEnabled;
        private VisualElement _autoCleanFields;
        private IntegerField _autoCleanCapacityMb;
        private IntegerField _autoCleanMaxAge;
        private Label        _autoCleanUsage;
        private VisualElement _autoCleanLegacyRow;
        private Label        _autoCleanLegacyLabel;
        private Button       _autoCleanLegacyDeleteBtn;
        private Button       _autoCleanRunBtn;
        private Button       _autoCleanResetBtn;

        public HistoryTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                SkillsLogger.LogError($"Failed to load HistoryTab UXML: {TabUxmlPath}");
                return;
            }
            uxml.CloneTree(_root);

            CacheUiReferences();
            BindEvents();
            LoadAutoCleanValues();
            ApplyAutoCleanLocalization();
            RefreshHistory();
        }

        private void CacheUiReferences()
        {
            _historyTitle    = _root.Q<Label>("history-title");
            _refreshBtn      = _root.Q<Button>("refresh-btn");
            _clearBtn        = _root.Q<Button>("clear-btn");
            _cacheWarning    = _root.Q<HelpBox>("cache-warning");
            _resultHelpBox   = _root.Q<HelpBox>("result-help-box");
            _activeTitle     = _root.Q<Label>("active-tasks-title");
            _activeContainer = _root.Q<VisualElement>("active-tasks-container");
            _undoneTitle     = _root.Q<Label>("undone-tasks-title");
            _undoneContainer = _root.Q<VisualElement>("undone-tasks-container");

            _autoCleanFoldout      = _root.Q<Foldout>("autoclean-foldout");
            _autoCleanEnabled      = _root.Q<Toggle>("autoclean-enabled");
            _autoCleanFields       = _root.Q<VisualElement>("autoclean-fields");
            _autoCleanCapacityMb      = _root.Q<IntegerField>("autoclean-capacity-mb");
            _autoCleanMaxAge          = _root.Q<IntegerField>("autoclean-max-age");
            _autoCleanUsage           = _root.Q<Label>("autoclean-usage");
            _autoCleanLegacyRow       = _root.Q<VisualElement>("autoclean-legacy-row");
            _autoCleanLegacyLabel     = _root.Q<Label>("autoclean-legacy-label");
            _autoCleanLegacyDeleteBtn = _root.Q<Button>("autoclean-legacy-delete-btn");
            _autoCleanRunBtn          = _root.Q<Button>("autoclean-run-btn");
            _autoCleanResetBtn        = _root.Q<Button>("autoclean-reset-btn");

            if (_resultHelpBox == null && _cacheWarning != null)
            {
                _resultHelpBox = new HelpBox("", HelpBoxMessageType.Info)
                {
                    name = "result-help-box"
                };
                _resultHelpBox.SetVisible(false);
                _cacheWarning.parent.Insert(_cacheWarning.parent.IndexOf(_cacheWarning) + 1, _resultHelpBox);
            }

            UISkillsEditorIcons.Apply(_refreshBtn, "d_Refresh", "Refresh", "TreeEditor.Refresh");
        }

        private void BindEvents()
        {
            if (_refreshBtn != null) _refreshBtn.clicked += RefreshHistory;
            if (_clearBtn   != null) _clearBtn.clicked   += ClearHistory;

            BindAutoCleanEvents();
        }

        private void BindAutoCleanEvents()
        {
            _autoCleanEnabled?.RegisterValueChangedCallback(evt =>
            {
                WorkflowAutoCleanConfig.Enabled = evt.newValue;
                UpdateAutoCleanFieldsEnabled();
            });
            _autoCleanMaxAge?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.MaxAgeDays = Mathf.Max(0, evt.newValue));
            _autoCleanCapacityMb?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.CapacityMB = Mathf.Max(0, evt.newValue));

            if (_autoCleanRunBtn != null) _autoCleanRunBtn.clicked += () =>
            {
                var report = WorkflowManager.TrimHistoryIfNeeded(force: true);
                ShowResultMessage(string.Format(
                    SkillsLocalization.Get("autoclean_run_result_format"),
                    report.removedSegments, FormatBytes(report.reclaimedBytes)), false);
                RefreshHistory();
            };

            if (_autoCleanResetBtn != null) _autoCleanResetBtn.clicked += () =>
            {
                WorkflowAutoCleanConfig.ResetToDefaults();
                LoadAutoCleanValues();
            };

            if (_autoCleanLegacyDeleteBtn != null) _autoCleanLegacyDeleteBtn.clicked += () =>
            {
                WorkflowManager.DeleteLegacyBackup();
                RefreshHistory();
            };
        }

        private void LoadAutoCleanValues()
        {
            _autoCleanEnabled?.SetValueWithoutNotify(WorkflowAutoCleanConfig.Enabled);
            _autoCleanMaxAge?.SetValueWithoutNotify(WorkflowAutoCleanConfig.MaxAgeDays);
            _autoCleanCapacityMb?.SetValueWithoutNotify(WorkflowAutoCleanConfig.CapacityMB);
            UpdateAutoCleanFieldsEnabled();
        }

        private void UpdateAutoCleanFieldsEnabled()
        {
            bool enabled = WorkflowAutoCleanConfig.Enabled;
            _autoCleanFields?.SetEnabled(enabled);
            _autoCleanRunBtn?.SetEnabled(enabled);
        }

        private void UpdateAutoCleanUsage()
        {
            if (_autoCleanUsage == null) return;

            var history = WorkflowManager.History;
            int taskCount = (history?.tasks?.Count ?? 0) + (history?.undoneStack?.Count ?? 0);
            long logBytes = WorkflowManager.GetHistoryFileSizeBytes();
            long blobBytes = WorkflowFileStore.GetStoreSizeBytes();

            _autoCleanUsage.text = string.Format(
                SkillsLocalization.Get("autoclean_usage_format"),
                taskCount, FormatBytes(logBytes), FormatBytes(blobBytes));
        }

        private void UpdateLegacyBackup()
        {
            if (_autoCleanLegacyRow == null) return;

            var legacy = WorkflowManager.GetLegacyBackupInfo();
            UiVisibility.SetVisible(_autoCleanLegacyRow, legacy != null);
            if (legacy == null) return;

            DateTime deleteOnLocal = legacy.MovedAtUtc.ToLocalTime()
                .AddDays(WorkflowStore.LegacyBackupKeepDays);
            _autoCleanLegacyLabel.text = string.Format(
                SkillsLocalization.Get("autoclean_legacy_format"),
                FormatBytes(legacy.TotalBytes), deleteOnLocal.ToString("yyyy-MM-dd"));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }

        private void RefreshHistory()
        {
            // The in-memory history is authoritative (every change is saved as it happens); re-reading the whole file
            // here only cost a full parse and trim per click.
            RebuildHistoryList();
            UpdateAutoCleanUsage();
            UpdateLegacyBackup();
        }

        private void ClearHistory()
        {
            string title = SkillsLocalization.Get("history_clear_title");
            string msg = SkillsLocalization.Get("history_clear_confirm_msg");

            if (EditorUtility.DisplayDialog(title, msg, SkillsLocalization.Get("dialog_yes"), SkillsLocalization.Get("dialog_no")))
            {
                WorkflowManager.ClearHistory();
                RefreshHistory();
            }
        }

        private void RebuildHistoryList()
        {
            var history = WorkflowManager.History;

            BuildSection(_activeContainer, _activeTitle, history?.tasks,
                isActive: true,
                titleFormatKey: "history_active_format",
                emptyKey: "history_no_active");

            BuildSection(_undoneContainer, _undoneTitle, history?.undoneStack,
                isActive: false,
                titleFormatKey: "history_undone_format",
                emptyKey: "history_no_undone");
        }

        private void BuildSection(VisualElement container, Label title,
                                  List<WorkflowTask> tasks, bool isActive,
                                  string titleFormatKey, string emptyKey)
        {
            if (container == null) return;
            container.Clear();

            int count = tasks?.Count ?? 0;
            if (title != null)
                title.text = string.Format(SkillsLocalization.Get(titleFormatKey), count);

            if (tasks == null || tasks.Count == 0)
            {
                var empty = new Label(SkillsLocalization.Get(emptyKey));
                empty.AddToClassList("muted-label");
                empty.AddToClassList("muted-label--indent");
                container.Add(empty);
                return;
            }

            for (int i = tasks.Count - 1; i >= 0; i--)
                container.Add(BuildTaskCard(tasks[i], isActive));
        }

        private VisualElement BuildTaskCard(WorkflowTask task, bool isActive)
        {
            var card = new VisualElement();
            card.AddToClassList("task-card");
            if (!isActive) card.AddToClassList("undone");

            var head = new VisualElement();
            head.AddToClassList("task-card__head");
            // .task-card__head already declares flex-direction:row + align-items:center in USS.

            var nameLabel = new Label(task.tag ?? task.id ?? "(unnamed)");
            nameLabel.AddToClassList("task-card__name");
            head.Add(nameLabel);

            int changeCount = task.snapshots?.Count ?? 0;
            if (changeCount > 0)
            {
                var changesLabel = new Label(
                    $"  ({changeCount} {SkillsLocalization.Get("history_changes_suffix")})");
                changesLabel.AddToClassList("muted-label");
                changesLabel.AddToClassList("muted-label--small");
                head.Add(changesLabel);
            }

            var timeLabel = new Label(task.GetFormattedTime());
            timeLabel.AddToClassList("task-card__time");
            head.Add(timeLabel);

            card.Add(head);

            if (!string.IsNullOrEmpty(task.description))
            {
                var desc = new Label(task.description);
                desc.AddToClassList("task-card__summary");
                card.Add(desc);
            }

            var actions = new VisualElement();
            actions.AddToClassList("task-card__actions");
            // .task-card__actions already declares flex-direction:row in USS.

            if (isActive)
            {
                var undoBtn = new Button(() =>
                {
                    var result = WorkflowManager.UndoTask(task.id);
                    ShowResult(result, SkillsLocalization.Get("btn_undo"));
                    RefreshHistory();
                });
                undoBtn.AddToClassList("mini-btn");
                undoBtn.text = SkillsLocalization.Get("btn_undo");
                actions.Add(undoBtn);

                var delBtn = new Button(() => { WorkflowManager.DeleteTask(task.id); RefreshHistory(); });
                delBtn.AddToClassList("mini-btn");
                delBtn.AddToClassList("danger");
                delBtn.text = "×";
                actions.Add(delBtn);
            }
            else
            {
                var redoBtn = new Button(() =>
                {
                    var result = WorkflowManager.RedoTask(task.id);
                    ShowResult(result, SkillsLocalization.Get("btn_redo"));
                    RefreshHistory();
                });
                redoBtn.AddToClassList("mini-btn");
                redoBtn.AddToClassList("install");
                redoBtn.text = SkillsLocalization.Get("btn_redo");
                actions.Add(redoBtn);

                var delBtn = new Button(() => { WorkflowManager.DeleteTask(task.id); RefreshHistory(); });
                delBtn.AddToClassList("mini-btn");
                delBtn.AddToClassList("danger");
                delBtn.text = "×";
                actions.Add(delBtn);
            }

            card.Add(actions);
            return card;
        }

        private void ApplyAutoCleanLocalization()
        {
            if (_autoCleanFoldout != null) _autoCleanFoldout.text = SkillsLocalization.Get("autoclean_title");
            if (_autoCleanEnabled != null) _autoCleanEnabled.label = SkillsLocalization.Get("autoclean_enabled");

            SetFieldLabel(_autoCleanCapacityMb, "autoclean_capacity_mb", "autoclean_tip_capacity_mb");
            SetFieldLabel(_autoCleanMaxAge,     "autoclean_max_age",     "autoclean_tip_max_age");

            if (_autoCleanLegacyDeleteBtn != null) _autoCleanLegacyDeleteBtn.text = SkillsLocalization.Get("autoclean_legacy_delete");
            if (_autoCleanRunBtn          != null) _autoCleanRunBtn.text          = SkillsLocalization.Get("autoclean_run_now");
            if (_autoCleanResetBtn        != null) _autoCleanResetBtn.text        = SkillsLocalization.Get("autoclean_reset");
        }

        private static void SetFieldLabel(IntegerField field, string labelKey, string tooltipKey)
        {
            if (field == null) return;
            field.label = SkillsLocalization.Get(labelKey);
            field.tooltip = SkillsLocalization.Get(tooltipKey);
        }

        public void RefreshLocalization()
        {
            if (_historyTitle != null)
                _historyTitle.text = SkillsLocalization.Get("history_tab_title");
            if (_refreshBtn != null) _refreshBtn.tooltip = SkillsLocalization.Get("refresh");
            if (_clearBtn   != null) _clearBtn.text      = SkillsLocalization.Get("history_clear_all");

            if (_cacheWarning != null)
            {
                _cacheWarning.text = SkillsLocalization.Get("history_cache_warning");
            }

            ApplyAutoCleanLocalization();
            UpdateAutoCleanUsage();
            UpdateLegacyBackup();
            RebuildHistoryList();
        }

        private void ShowResult(TaskUndoResult result, string operation)
        {
            if (result.total == 0)
            {
                ShowResultMessage(string.Format(SkillsLocalization.Get("history_result_none_fmt"), operation), false);
                return;
            }

            if (result.failed == 0)
            {
                ShowResultMessage(string.Format(SkillsLocalization.Get("history_result_ok_fmt"), operation, result.succeeded, result.total), false);
            }
            else
            {
                ShowResultMessage(string.Format(SkillsLocalization.Get("history_result_fail_fmt"), operation, result.failed, result.succeeded, result.total), true);
                foreach (var detail in result.details)
                {
                    if (!detail.success)
                        SkillsLogger.LogWarning($"{operation} failed for {detail.objectName}: {detail.error}");
                }
            }
        }

        private void ShowResultMessage(string message, bool isError)
        {
            if (_resultHelpBox == null) return;
            _resultHelpBox.text = message;
            _resultHelpBox.messageType = isError ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
            _resultHelpBox.SetVisible(true);
        }
    }
}

// Producer:Betsy
