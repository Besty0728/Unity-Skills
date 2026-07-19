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
        private IntegerField _autoCleanMaxTasks;
        private IntegerField _autoCleanMaxHistoryMb;
        private IntegerField _autoCleanMaxTaskAge;
        private IntegerField _autoCleanMaxStoreMb;
        private IntegerField _autoCleanStoreMaxAge;
        private Label        _autoCleanUsage;
        private Button       _autoCleanRunBtn;
        private Button       _autoCleanResetBtn;

        public HistoryTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root;
            _window = window;

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load HistoryTab UXML: {TabUxmlPath}");
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
            _autoCleanMaxTasks     = _root.Q<IntegerField>("autoclean-max-tasks");
            _autoCleanMaxHistoryMb = _root.Q<IntegerField>("autoclean-max-history-mb");
            _autoCleanMaxTaskAge   = _root.Q<IntegerField>("autoclean-max-task-age");
            _autoCleanMaxStoreMb   = _root.Q<IntegerField>("autoclean-max-store-mb");
            _autoCleanStoreMaxAge  = _root.Q<IntegerField>("autoclean-store-max-age");
            _autoCleanUsage        = _root.Q<Label>("autoclean-usage");
            _autoCleanRunBtn       = _root.Q<Button>("autoclean-run-btn");
            _autoCleanResetBtn     = _root.Q<Button>("autoclean-reset-btn");

            if (_resultHelpBox == null && _cacheWarning != null)
            {
                _resultHelpBox = new HelpBox("", HelpBoxMessageType.Info)
                {
                    name = "result-help-box"
                };
                _resultHelpBox.style.display = DisplayStyle.None;
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
            _autoCleanMaxTasks?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.MaxTasks = Mathf.Max(0, evt.newValue));
            _autoCleanMaxHistoryMb?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.MaxHistoryMB = Mathf.Max(0, evt.newValue));
            _autoCleanMaxTaskAge?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.MaxTaskAgeDays = Mathf.Max(0, evt.newValue));
            _autoCleanMaxStoreMb?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.MaxStoreMB = Mathf.Max(0, evt.newValue));
            _autoCleanStoreMaxAge?.RegisterValueChangedCallback(evt =>
                WorkflowAutoCleanConfig.StoreMaxAgeDays = Mathf.Max(0, evt.newValue));

            if (_autoCleanRunBtn != null) _autoCleanRunBtn.clicked += () =>
            {
                var report = WorkflowManager.TrimHistoryIfNeeded(force: true);
                ShowResultMessage(string.Format(
                    SkillsLocalization.Get("autoclean_run_result_format"),
                    report.removedTasks, FormatBytes(report.reclaimedBytes)), false);
                RefreshHistory();
            };

            if (_autoCleanResetBtn != null) _autoCleanResetBtn.clicked += () =>
            {
                WorkflowAutoCleanConfig.ResetToDefaults();
                LoadAutoCleanValues();
            };
        }

        private void LoadAutoCleanValues()
        {
            _autoCleanEnabled?.SetValueWithoutNotify(WorkflowAutoCleanConfig.Enabled);
            _autoCleanMaxTasks?.SetValueWithoutNotify(WorkflowAutoCleanConfig.MaxTasks);
            _autoCleanMaxHistoryMb?.SetValueWithoutNotify(WorkflowAutoCleanConfig.MaxHistoryMB);
            _autoCleanMaxTaskAge?.SetValueWithoutNotify(WorkflowAutoCleanConfig.MaxTaskAgeDays);
            _autoCleanMaxStoreMb?.SetValueWithoutNotify(WorkflowAutoCleanConfig.MaxStoreMB);
            _autoCleanStoreMaxAge?.SetValueWithoutNotify(WorkflowAutoCleanConfig.StoreMaxAgeDays);
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
            long historyBytes = WorkflowManager.GetHistoryFileSizeBytes();
            long storeBytes = WorkflowFileStore.GetStoreSizeBytes();

            _autoCleanUsage.text = string.Format(
                SkillsLocalization.Get("autoclean_usage_format"),
                taskCount, FormatBytes(historyBytes), FormatBytes(storeBytes));
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
            WorkflowManager.LoadHistory();
            RebuildHistoryList();
            UpdateAutoCleanUsage();
        }

        private void ClearHistory()
        {
            string title = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                ? "清除历史" : "Clear History";
            string msg = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                ? "确定要清除所有历史记录吗？这也会删除磁盘上的工作流缓存快照。"
                : "Are you sure you want to clear all history? This will also delete workflow cached snapshots on disk.";

            if (EditorUtility.DisplayDialog(title, msg, "Yes", "No"))
            {
                WorkflowManager.ClearHistory();
                RefreshHistory();
            }
        }

        private void RebuildHistoryList()
        {
            var history = WorkflowManager.History;
            if (history == null)
            {
                WorkflowManager.LoadHistory();
                history = WorkflowManager.History;
            }

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
                empty.style.marginLeft = 6;
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

            // Head
            var head = new VisualElement();
            head.AddToClassList("task-card__head");
            head.style.flexDirection = FlexDirection.Row;
            head.style.alignItems = Align.Center;

            var nameLabel = new Label(task.tag ?? task.id ?? "(unnamed)");
            nameLabel.AddToClassList("task-card__name");
            head.Add(nameLabel);

            int changeCount = task.snapshots?.Count ?? 0;
            if (changeCount > 0)
            {
                var changesLabel = new Label(
                    $"  ({changeCount} {SkillsLocalization.Get("history_changes_suffix")})");
                changesLabel.AddToClassList("muted-label");
                changesLabel.style.fontSize = 10;
                head.Add(changesLabel);
            }

            var timeLabel = new Label(task.GetFormattedTime());
            timeLabel.AddToClassList("task-card__time");
            head.Add(timeLabel);

            card.Add(head);

            // Description
            if (!string.IsNullOrEmpty(task.description))
            {
                var desc = new Label(task.description);
                desc.AddToClassList("task-card__summary");
                card.Add(desc);
            }

            // Actions
            var actions = new VisualElement();
            actions.AddToClassList("task-card__actions");
            actions.style.flexDirection = FlexDirection.Row;

            if (isActive)
            {
                var undoBtn = new Button(() =>
                {
                    var result = WorkflowManager.UndoTask(task.id);
                    ShowResult(result, "Undo");
                    RefreshHistory();
                });
                undoBtn.AddToClassList("mini-btn");
                undoBtn.text = "Undo";
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
                    ShowResult(result, "Redo");
                    RefreshHistory();
                });
                redoBtn.AddToClassList("mini-btn");
                redoBtn.AddToClassList("install");
                redoBtn.text = "Redo";
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

            SetFieldLabel(_autoCleanMaxTasks,     "autoclean_max_tasks",      "autoclean_tip_max_tasks");
            SetFieldLabel(_autoCleanMaxHistoryMb, "autoclean_max_history_mb", "autoclean_tip_max_history_mb");
            SetFieldLabel(_autoCleanMaxTaskAge,   "autoclean_max_task_age",   "autoclean_tip_max_task_age");
            SetFieldLabel(_autoCleanMaxStoreMb,   "autoclean_max_store_mb",   "autoclean_tip_max_store_mb");
            SetFieldLabel(_autoCleanStoreMaxAge,  "autoclean_store_max_age",  "autoclean_tip_store_max_age");

            if (_autoCleanRunBtn   != null) _autoCleanRunBtn.text   = SkillsLocalization.Get("autoclean_run_now");
            if (_autoCleanResetBtn != null) _autoCleanResetBtn.text = SkillsLocalization.Get("autoclean_reset");
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
                _historyTitle.text = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "工作流历史" : "Workflow History";
            if (_refreshBtn != null) _refreshBtn.tooltip = SkillsLocalization.Get("refresh");
            if (_clearBtn   != null) _clearBtn.text      = SkillsLocalization.Get("history_clear_all");

            if (_cacheWarning != null)
            {
                _cacheWarning.text = SkillsLocalization.Current == SkillsLocalization.Language.Chinese
                    ? "工作流缓存警告：撤销操作仅恢复场景状态和文件快照，不会撤销如包管理器操作或外部系统的副作用。"
                    : "Workflow Cache Warning: undo restores scene hierarchies and asset snapshots. External side effects (e.g. Package Manager) cannot be reverted.";
            }

            ApplyAutoCleanLocalization();
            UpdateAutoCleanUsage();
            RebuildHistoryList();
        }

        private void ShowResult(TaskUndoResult result, string operation)
        {
            if (result.total == 0)
            {
                ShowResultMessage($"{operation}: no snapshots to process", false);
                return;
            }

            if (result.failed == 0)
            {
                ShowResultMessage($"{operation} succeeded: {result.succeeded}/{result.total}", false);
            }
            else
            {
                ShowResultMessage($"{operation} completed with {result.failed} failure(s) ({result.succeeded}/{result.total})", true);
                foreach (var detail in result.details)
                {
                    if (!detail.success)
                        Debug.LogWarning($"{SkillsLogger.PREFIX_WARNING} {operation} failed for {detail.objectName}: {detail.error}");
                }
            }
        }

        private void ShowResultMessage(string message, bool isError)
        {
            if (_resultHelpBox == null) return;
            _resultHelpBox.text = message;
            _resultHelpBox.messageType = isError ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
            _resultHelpBox.style.display = DisplayStyle.Flex;
        }
    }
}

// Producer:Betsy
