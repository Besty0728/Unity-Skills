using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace UnitySkills
{
    public class HistoryTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/HistoryTab.uxml";
        private const float TimelineRowHeight = 26f;
        private const long RefreshIntervalMs = 500;

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label         _historyTitle;
        private Toggle        _autoCheckpointToggle;
        private Button        _refreshBtn;
        private Button        _clearBtn;
        private HelpBox       _cacheWarning;
        private HelpBox       _resultHelpBox;
        private Button        _undoRestoreBtn;
        private DropdownField _agentFilter;
        private ListView      _timeline;
        private VisualElement _restorePreview;
        private Label         _summaryLabel;
        private Label         _conflictsTitle;
        private VisualElement _conflictList;
        private HelpBox       _coverageWarning;
        private Label         _unresolvableLabel;
        private Button        _previewCancelBtn;
        private Button        _previewRestoreBtn;

        private Foldout      _autoCleanFoldout;
        private Toggle       _autoCleanEnabled;
        private VisualElement _autoCleanFields;
        private IntegerField _autoCleanCapacityMb;
        private IntegerField _autoCleanMaxAge;
        private IntegerField _idleGap;
        private Label        _autoCleanUsage;
        private VisualElement _autoCleanLegacyRow;
        private Label        _autoCleanLegacyLabel;
        private Button       _autoCleanLegacyDeleteBtn;
        private Button       _autoCleanRunBtn;
        private Button       _autoCleanResetBtn;

        private readonly List<HistoryTimelineRow> _timelineRows = new List<HistoryTimelineRow>();
        private readonly List<string> _agentChoices = new List<string>();
        private readonly List<(Toggle Overwrite, string Id)> _conflictToggles = new List<(Toggle, string)>();
        private string _agentFilterName;
        private bool _timelineDirty = true;
        private string _previewTarget;
        private long _undoRestoreSeq;

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
            LoadCheckpointValues();
            ApplyAutoCleanLocalization();
            ApplyTimelineLocalization();
            RefreshHistory();

            _root.RegisterCallback<AttachToPanelEvent>(OnRootAttached);
            _root.RegisterCallback<DetachFromPanelEvent>(OnRootDetached);
            SubscribeHistoryChanged();
            EditorUiScheduler.RepeatSafe(_root, RefreshIntervalMs, RefreshTimelineIfDirty);
        }

        private void CacheUiReferences()
        {
            _historyTitle      = _root.Q<Label>("history-title");
            _autoCheckpointToggle = _root.Q<Toggle>("auto-checkpoint-toggle");
            _refreshBtn        = _root.Q<Button>("refresh-btn");
            _clearBtn          = _root.Q<Button>("clear-btn");
            _cacheWarning      = _root.Q<HelpBox>("cache-warning");
            _resultHelpBox     = _root.Q<HelpBox>("result-help-box");
            _undoRestoreBtn    = _root.Q<Button>("undo-restore-btn");
            _agentFilter       = _root.Q<DropdownField>("history-agent-filter");
            _timeline          = _root.Q<ListView>("timeline-list");
            _restorePreview    = _root.Q<VisualElement>("restore-preview");
            _summaryLabel      = _root.Q<Label>("summary-label");
            _conflictsTitle    = _root.Q<Label>("conflicts-title");
            _conflictList      = _root.Q<VisualElement>("conflict-list");
            _coverageWarning   = _root.Q<HelpBox>("coverage-warning");
            _unresolvableLabel = _root.Q<Label>("unresolvable-label");
            _previewCancelBtn  = _root.Q<Button>("preview-cancel-btn");
            _previewRestoreBtn = _root.Q<Button>("preview-restore-btn");

            _autoCleanFoldout      = _root.Q<Foldout>("autoclean-foldout");
            _autoCleanEnabled      = _root.Q<Toggle>("autoclean-enabled");
            _autoCleanFields       = _root.Q<VisualElement>("autoclean-fields");
            _autoCleanCapacityMb      = _root.Q<IntegerField>("autoclean-capacity-mb");
            _autoCleanMaxAge          = _root.Q<IntegerField>("autoclean-max-age");
            _idleGap                  = _root.Q<IntegerField>("history-idle-gap");
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

            if (_timeline != null)
            {
                _timeline.fixedItemHeight = TimelineRowHeight;
                _timeline.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
                _timeline.selectionType = SelectionType.None;
                _timeline.makeItem = MakeTimelineItem;
                _timeline.bindItem = BindTimelineItem;
            }

            _restorePreview.SetVisible(false);
            _undoRestoreBtn.SetVisible(false);
            _coverageWarning.SetVisible(false);
            _unresolvableLabel.SetVisible(false);

            UISkillsEditorIcons.Apply(_refreshBtn, "d_Refresh", "Refresh", "TreeEditor.Refresh");
        }

        private void BindEvents()
        {
            if (_refreshBtn != null) _refreshBtn.clicked += RefreshHistory;
            if (_clearBtn   != null) _clearBtn.clicked   += ClearHistory;

            _autoCheckpointToggle?.RegisterValueChangedCallback(evt =>
                WorkflowCheckpointConfig.AutoCheckpoints = evt.newValue);
            _idleGap?.RegisterValueChangedCallback(evt =>
            {
                WorkflowCheckpointConfig.IdleGapSeconds = evt.newValue;
                _idleGap.SetValueWithoutNotify(WorkflowCheckpointConfig.IdleGapSeconds);
            });
            _agentFilter?.RegisterValueChangedCallback(evt => Guard(() => ApplyAgentFilter(evt.newValue)));

            if (_undoRestoreBtn != null) _undoRestoreBtn.clicked += () => Guard(UndoLastRestore);
            if (_previewCancelBtn != null) _previewCancelBtn.clicked += () => _restorePreview.SetVisible(false);
            if (_previewRestoreBtn != null) _previewRestoreBtn.clicked += () => Guard(RestorePreviewed);

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
                WorkflowCheckpointConfig.IdleGapSeconds = WorkflowCheckpointConfig.DefaultIdleGapSeconds;
                LoadAutoCleanValues();
                LoadCheckpointValues();
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

        private void LoadCheckpointValues()
        {
            _autoCheckpointToggle?.SetValueWithoutNotify(WorkflowCheckpointConfig.AutoCheckpoints);
            _idleGap?.SetValueWithoutNotify(WorkflowCheckpointConfig.IdleGapSeconds);
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
            int stepCount = history?.tasks?.Count ?? 0;
            long logBytes = WorkflowManager.GetHistoryFileSizeBytes();
            long blobBytes = WorkflowFileStore.GetStoreSizeBytes();

            _autoCleanUsage.text = string.Format(
                SkillsLocalization.Get("autoclean_usage_format"),
                stepCount, FormatBytes(logBytes), FormatBytes(blobBytes));
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

        /// <summary>Called when the History tab is shown: the timeline is rebuilt now rather than on the next tick.</summary>
        public void OnTabSelected()
        {
            _timelineDirty = true;
            RefreshHistory();
        }

        private void RefreshHistory()
        {
            // The in-memory history is authoritative (every change is saved as it happens); re-reading the whole file
            // here only cost a full parse and trim per click.
            RebuildTimeline();
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
                _restorePreview.SetVisible(false);
                _undoRestoreBtn.SetVisible(false);
                RefreshHistory();
            }
        }

        private void SubscribeHistoryChanged()
        {
            // Subscribing twice is harmless: -= first keeps one handler, as the panel can attach again after a detach.
            WorkflowManager.HistoryChanged -= OnHistoryChanged;
            WorkflowManager.HistoryChanged += OnHistoryChanged;
        }

        private void OnRootAttached(AttachToPanelEvent _) => SubscribeHistoryChanged();

        private void OnRootDetached(DetachFromPanelEvent _) => WorkflowManager.HistoryChanged -= OnHistoryChanged;

        private void OnHistoryChanged() => _timelineDirty = true;

        private void RefreshTimelineIfDirty()
        {
            if (_timelineDirty && _root.IsVisible())
                RebuildTimeline();
        }

        private void RebuildTimeline()
        {
            _timelineDirty = false;
            var history = WorkflowManager.History;
            RebuildAgentFilter(history);

            _timelineRows.Clear();
            _timelineRows.AddRange(BuildTimelineRows(history, _agentFilterName, WorkflowManager.ResolveCallerAgent()));
            if (_timeline == null) return;
            _timeline.itemsSource = _timelineRows;
            _timeline.RefreshItems();
        }

        private void RebuildAgentFilter(WorkflowHistoryData history)
        {
            if (_agentFilter == null) return;

            var agents = history.tasks.Select(task => task.agent)
                .Concat(history.checkpoints.Select(checkpoint => checkpoint.agent))
                .Where(agent => !string.IsNullOrEmpty(agent))
                .Distinct()
                .OrderBy(agent => agent, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_agentFilterName != null && !agents.Contains(_agentFilterName))
                _agentFilterName = null;

            _agentChoices.Clear();
            _agentChoices.Add(SkillsLocalization.Get("history_agent_filter_all"));
            _agentChoices.AddRange(agents);
            _agentFilter.choices = new List<string>(_agentChoices);
            _agentFilter.SetValueWithoutNotify(_agentFilterName ?? _agentChoices[0]);
        }

        private void ApplyAgentFilter(string choice)
        {
            _agentFilterName = _agentChoices.IndexOf(choice) > 0 ? choice : null;
            RebuildTimeline();
        }

        /// <summary>
        /// The timeline the History tab lists, newest first. Each checkpoint heads the steps after it, so a checkpoint sits
        /// above step s&lt;n&gt; for its seq n; the agent filter keeps only the rows of that agent.
        /// </summary>
        internal static List<HistoryTimelineRow> BuildTimelineRows(WorkflowHistoryData history, string agentFilter, string callerAgent)
        {
            var rows = new List<HistoryTimelineRow>();
            if (history == null) return rows;

            var checkpoints = history.checkpoints.OrderBy(checkpoint => checkpoint.seq).ToList();
            // groups[0] holds the steps before the first checkpoint; groups[k] the steps from checkpoints[k - 1] up to the next one.
            var groups = Enumerable.Range(0, checkpoints.Count + 1).Select(_ => new List<WorkflowTask>()).ToList();
            int group = 0;
            foreach (var step in history.tasks.OrderBy(task => task.seq))
            {
                while (group < checkpoints.Count && checkpoints[group].seq <= step.seq)
                    group++;
                groups[group].Add(step);
            }

            var undone = WorkflowManager.UndoneSteps(history.restores);
            for (int k = checkpoints.Count; k >= 0; k--)
            {
                var steps = groups[k].OrderByDescending(task => task.seq).ToList();
                if (k > 0)
                {
                    var checkpoint = checkpoints[k - 1];
                    if (MatchesAgent(checkpoint.agent, agentFilter))
                    {
                        int visibleSteps = steps.Count(step => MatchesAgent(step.agent, agentFilter));
                        rows.Add(CheckpointRow(checkpoint, visibleSteps, callerAgent));
                    }
                }

                foreach (var step in steps)
                {
                    if (MatchesAgent(step.agent, agentFilter))
                        rows.Add(StepRow(step, undone.Contains(step.seq), callerAgent));
                }
            }
            return rows;
        }

        private static bool MatchesAgent(string agent, string agentFilter) =>
            string.IsNullOrEmpty(agentFilter) || (agent ?? string.Empty) == agentFilter;

        private static HistoryTimelineRow CheckpointRow(WorkflowCheckpoint checkpoint, int stepCount, string callerAgent)
        {
            string agent = checkpoint.agent ?? string.Empty;
            return new HistoryTimelineRow
            {
                IsCheckpoint = true,
                IsAuto = checkpoint.kind == WorkflowCheckpoint.KindAuto,
                Id = checkpoint.id,
                Label = checkpoint.label,
                Agent = agent,
                IsOtherAgent = agent != callerAgent,
                TimestampSeconds = checkpoint.timestampMs / 1000,
                StepCount = stepCount,
            };
        }

        private static HistoryTimelineRow StepRow(WorkflowTask step, bool isReverted, string callerAgent)
        {
            string agent = step.agent ?? string.Empty;
            return new HistoryTimelineRow
            {
                Id = step.DisplayId,
                Label = step.tag ?? step.id,
                Target = step.target,
                Agent = agent,
                IsOtherAgent = agent != callerAgent,
                IsReverted = isReverted,
                TimestampSeconds = step.timestamp,
            };
        }

        private VisualElement MakeTimelineItem()
        {
            var row = new VisualElement();
            row.AddToClassList("timeline-row");

            var text = new Label();
            text.AddToClassList("timeline-row__text");

            var preview = new Button();
            preview.AddToClassList("mini-btn");
            preview.AddToClassList("timeline-row__preview");

            var view = new TimelineItemView(text, preview);
            preview.clicked += () => Guard(() =>
            {
                if (view.Row != null) ShowPreview(view.Row.Id);
            });

            row.userData = view;
            row.Add(text);
            row.Add(preview);
            return row;
        }

        private void BindTimelineItem(VisualElement row, int index)
        {
            var view = (TimelineItemView)row.userData;
            var item = _timelineRows[index];
            view.Row = item;

            row.EnableInClassList("timeline-row--checkpoint", item.IsCheckpoint);
            row.EnableInClassList("timeline-row--step", !item.IsCheckpoint);
            row.EnableInClassList("timeline-row--auto", item.IsAuto);
            row.EnableInClassList("timeline-row--reverted", item.IsReverted);

            view.Text.text = RowText(item);
            view.Preview.text = SkillsLocalization.Get("history_preview_btn");
            view.Preview.SetVisible(item.IsCheckpoint);
        }

        private static string RowText(HistoryTimelineRow row)
        {
            string time = FormatTime(row.TimestampSeconds);
            if (row.IsCheckpoint)
            {
                string label = !string.IsNullOrEmpty(row.Label) ? row.Label
                    : row.IsAuto ? SkillsLocalization.Get("history_checkpoint_auto") : null;
                return JoinParts(row.Id, time, label, row.Agent,
                    $"{row.StepCount} {SkillsLocalization.Get("history_steps_suffix")}");
            }
            return JoinParts(row.Id, time, row.Label, row.Target, row.IsOtherAgent ? "@" + row.Agent : null);
        }

        private static string JoinParts(params string[] parts) =>
            string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part)));

        private static string FormatTime(long unixSeconds) =>
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("HH:mm:ss");

        private void ShowPreview(string checkpointId)
        {
            _previewTarget = checkpointId;
            var preview = WorkflowManager.PreviewTo(checkpointId, true, WorkflowManager.ResolveCallerAgent());
            if (preview.error != null)
            {
                _restorePreview.SetVisible(false);
                ShowResultMessage(preview.error, true);
                return;
            }

            _summaryLabel.text = FormatRestoreSummary(preview.plan);
            BuildConflictRows(preview.conflicts);
            _coverageWarning.SetVisible(preview.coverage == "partial");
            _unresolvableLabel.SetVisible(preview.unresolvable.Count > 0);
            _unresolvableLabel.text = string.Format(
                SkillsLocalization.Get("history_unresolvable_fmt"), preview.unresolvable.Count);
            _restorePreview.SetVisible(true);
        }

        private static string FormatRestoreSummary(RestorePlanCounts plan)
        {
            string summary = string.Format(SkillsLocalization.Get("history_restore_summary_fmt"),
                plan.modify, plan.delete, plan.recreate);

            var extras = new List<string>();
            if (plan.move > 0) extras.Add(string.Format(SkillsLocalization.Get("history_restore_move_fmt"), plan.move));
            if (plan.settings > 0) extras.Add(string.Format(SkillsLocalization.Get("history_restore_settings_fmt"), plan.settings));
            return extras.Count == 0 ? summary : $"{summary} [{string.Join(", ", extras)}]";
        }

        private void BuildConflictRows(List<RestoreConflict> conflicts)
        {
            _conflictList.Clear();
            _conflictToggles.Clear();
            _conflictsTitle.text = string.Format(SkillsLocalization.Get("history_conflicts_title"), conflicts.Count);

            if (conflicts.Count == 0)
            {
                var none = new Label(SkillsLocalization.Get("history_no_conflicts"));
                none.AddToClassList("muted-label");
                _conflictList.Add(none);
                return;
            }

            foreach (var conflict in conflicts)
            {
                var row = new VisualElement();
                row.AddToClassList("conflict-row");

                var overwrite = new Toggle(SkillsLocalization.Get("history_conflict_overwrite"));
                var target = new Label($"{conflict.target} — {ConflictOwner(conflict)}");
                target.AddToClassList("conflict-row__target");
                var at = new Label(conflict.at);
                at.AddToClassList("conflict-row__at");

                row.Add(overwrite);
                row.Add(target);
                row.Add(at);
                _conflictList.Add(row);
                _conflictToggles.Add((overwrite, conflict.id));
            }
        }

        private static string ConflictOwner(RestoreConflict conflict) => conflict.by == "user"
            ? SkillsLocalization.Get("history_conflict_by_user")
            : string.Format(SkillsLocalization.Get("history_conflict_by_agent"), conflict.by);

        private void RestorePreviewed()
        {
            string conflicts = null;
            if (_conflictToggles.Count > 0)
            {
                var chosen = _conflictToggles.Where(entry => entry.Overwrite.value).Select(entry => entry.Id).ToList();
                conflicts = chosen.Count == 0 ? "keep" : string.Join(",", chosen);
            }

            int restoreCountBefore = WorkflowManager.History.restores.Count;
            var outcome = WorkflowManager.RestoreTo(_previewTarget, true, WorkflowManager.ResolveCallerAgent(), conflicts);
            if (outcome.error != null)
            {
                // Something changed since the preview (or the position is gone): show why and offer the fresh conflicts.
                ShowResultMessage(outcome.error, true);
                ShowPreview(_previewTarget);
                return;
            }

            _restorePreview.SetVisible(false);
            int applied = outcome.restored + outcome.deleted + outcome.recreated;
            ShowResultMessage(string.Format(SkillsLocalization.Get("history_restore_done_fmt"),
                outcome.to, applied, applied + outcome.failed), outcome.failed > 0);
            foreach (string item in outcome.failedItems)
                SkillsLogger.LogWarning($"Restore failed for {item}");

            var restores = WorkflowManager.History.restores;
            bool recorded = restores.Count > restoreCountBefore;
            _undoRestoreSeq = recorded ? restores[restores.Count - 1].seq : 0;
            _undoRestoreBtn.SetVisible(recorded);
            RefreshHistory();
        }

        private void UndoLastRestore()
        {
            var result = WorkflowManager.RedoTask("s" + _undoRestoreSeq);
            if (!string.IsNullOrEmpty(result.error))
                ShowResultMessage(result.error, true);
            else
                ShowResult(result, SkillsLocalization.Get("history_undo_restore_btn"));
            _undoRestoreBtn.SetVisible(false);
            RefreshHistory();
        }

        /// <summary>UI callbacks never throw into the editor loop; an unexpected failure is logged and the tab keeps working.</summary>
        private static void Guard(Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"History tab action failed: {ex}");
            }
        }

        private void ApplyAutoCleanLocalization()
        {
            if (_autoCleanFoldout != null) _autoCleanFoldout.text = SkillsLocalization.Get("autoclean_title");
            if (_autoCleanEnabled != null) _autoCleanEnabled.label = SkillsLocalization.Get("autoclean_enabled");

            SetFieldLabel(_autoCleanCapacityMb, "autoclean_capacity_mb", "autoclean_tip_capacity_mb");
            SetFieldLabel(_autoCleanMaxAge,     "autoclean_max_age",     "autoclean_tip_max_age");

            if (_idleGap != null) _idleGap.label = SkillsLocalization.Get("history_idle_gap_label");

            if (_autoCleanLegacyDeleteBtn != null) _autoCleanLegacyDeleteBtn.text = SkillsLocalization.Get("autoclean_legacy_delete");
            if (_autoCleanRunBtn          != null) _autoCleanRunBtn.text          = SkillsLocalization.Get("autoclean_run_now");
            if (_autoCleanResetBtn        != null) _autoCleanResetBtn.text        = SkillsLocalization.Get("autoclean_reset");
        }

        private void ApplyTimelineLocalization()
        {
            if (_autoCheckpointToggle != null)
            {
                _autoCheckpointToggle.label = SkillsLocalization.Get("history_auto_checkpoint_toggle");
                _autoCheckpointToggle.tooltip = SkillsLocalization.Get("history_auto_checkpoint_tip");
            }
            if (_undoRestoreBtn != null) _undoRestoreBtn.text = SkillsLocalization.Get("history_undo_restore_btn");
            if (_previewCancelBtn != null) _previewCancelBtn.text = SkillsLocalization.Get("history_cancel_btn");
            if (_previewRestoreBtn != null) _previewRestoreBtn.text = SkillsLocalization.Get("history_restore_btn");
            if (_coverageWarning != null) _coverageWarning.text = SkillsLocalization.Get("history_coverage_partial");
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
            ApplyTimelineLocalization();
            UpdateAutoCleanUsage();
            UpdateLegacyBackup();
            RebuildTimeline();
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

        private sealed class TimelineItemView
        {
            public readonly Label Text;
            public readonly Button Preview;
            public HistoryTimelineRow Row;

            public TimelineItemView(Label text, Button preview)
            {
                Text = text;
                Preview = preview;
            }
        }
    }

    /// <summary>One line of the History timeline: a checkpoint heading its steps, or a step.</summary>
    internal sealed class HistoryTimelineRow
    {
        public bool IsCheckpoint;
        public bool IsAuto;
        public bool IsReverted;
        public bool IsOtherAgent;
        public string Id;
        public string Label;
        public string Target;
        public string Agent;
        public long TimestampSeconds;
        public int StepCount;
    }
}

// Producer:Betsy
