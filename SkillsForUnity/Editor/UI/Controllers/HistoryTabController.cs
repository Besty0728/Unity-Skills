using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using UnitySkills.Internal;

namespace UnitySkills
{
    public class HistoryTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/HistoryTab.uxml";
        private const long RefreshIntervalMs = 500;
        private const int GroupPageSize = 30;
        private const int MaxNamesPerVerb = 3;
        // Group key of the steps before the first checkpoint; checkpoint groups are keyed by their id (c<n>).
        internal const string PreGroupKey = "pre";
        private const string Separator = " · ";

        private readonly VisualElement _root;
        private readonly UnitySkillsWindow _window;

        private Label         _historyTitle;
        private Toggle        _autoCheckpointToggle;
        private Button        _refreshBtn;
        private Button        _clearBtn;
        private HelpBox       _cacheWarning;
        private HelpBox       _resultHelpBox;
        private DropdownField _agentFilter;
        private ScrollView    _groupList;
        private VisualElement _restorePreview;
        private VisualElement _previewHome;
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

        private readonly List<string> _agentChoices = new List<string>();
        private readonly List<(Toggle Overwrite, string Id)> _conflictToggles = new List<(Toggle, string)>();
        private readonly HashSet<string> _openGroups = new HashSet<string>();
        private string _agentFilterName;
        private bool _timelineDirty = true;
        private int _visibleGroups = GroupPageSize;
        // The preview panel belongs to one group: a restore to its checkpoint, or an undo of the restore (taskId) it heads.
        private PreviewKind _previewKind;
        private string _previewGroupKey;
        private string _previewTaskId;

        private enum PreviewKind { None, Restore, Undo }

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
            _agentFilter       = _root.Q<DropdownField>("history-agent-filter");
            _groupList         = _root.Q<ScrollView>("history-groups");
            _restorePreview    = _root.Q<VisualElement>("restore-preview");
            _previewHome       = _restorePreview.parent;
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

            _restorePreview.SetVisible(false);
            _coverageWarning.SetVisible(false);
            _unresolvableLabel.SetVisible(false);

            VectorGlyphs.SetIcon(_refreshBtn, VectorGlyph.Refresh);
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

            if (_previewCancelBtn != null) _previewCancelBtn.clicked += () => Guard(CancelPreview);
            if (_previewRestoreBtn != null) _previewRestoreBtn.clicked += () => Guard(ConfirmPreview);

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
                ClearPreview();
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

            var groups = BuildTimelineGroups(history, _agentFilterName, WorkflowManager.ResolveCallerAgent(),
                WorkflowManager.GetUndoableRestores(), WorkflowManager.FindCheckpoint);
            if (_groupList == null) return;

            // The preview panel moves into the group it belongs to; it goes back to its own place when no group takes it.
            _restorePreview.RemoveFromHierarchy();
            _groupList.contentContainer.Clear();
            bool previewPlaced = false;
            foreach (var group in groups.Take(_visibleGroups))
            {
                bool placePreview = _previewKind != PreviewKind.None && group.Key == _previewGroupKey;
                _groupList.Add(BuildGroup(group, placePreview));
                previewPlaced |= placePreview;
            }
            if (groups.Count > _visibleGroups)
                _groupList.Add(BuildShowEarlierButton(groups.Count - _visibleGroups));

            if (_previewKind != PreviewKind.None && !previewPlaced)
                ClearPreview();
            if (!previewPlaced)
                _previewHome.Add(_restorePreview);
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
            _visibleGroups = GroupPageSize;
            RebuildTimeline();
        }

        /// <summary>
        /// The groups the History tab lists, newest first: each checkpoint heads the steps after it up to the next checkpoint, and the
        /// steps before the first checkpoint form a group of their own. The agent filter keeps a checkpoint of that agent (with its steps
        /// of that agent) and otherwise only the steps of that agent. Restores name their target checkpoints through
        /// <paramref name="findCheckpoint"/>; the undoable ones come from <paramref name="undoableRestores"/>, newest first.
        /// </summary>
        internal static List<HistoryGroup> BuildTimelineGroups(WorkflowHistoryData history, string agentFilter, string callerAgent,
            IList<WorkflowRestoreRecord> undoableRestores, Func<string, WorkflowCheckpoint> findCheckpoint)
        {
            var groups = new List<HistoryGroup>();
            if (history == null) return groups;

            var checkpoints = history.checkpoints.OrderBy(checkpoint => checkpoint.seq).ToList();
            // buckets[0] holds the steps before the first checkpoint; buckets[k] the steps from checkpoints[k - 1] up to the next one.
            var buckets = Enumerable.Range(0, checkpoints.Count + 1).Select(_ => new List<WorkflowTask>()).ToList();
            int bucket = 0;
            foreach (var step in history.tasks.OrderBy(task => task.seq))
            {
                while (bucket < checkpoints.Count && checkpoints[bucket].seq <= step.seq)
                    bucket++;
                buckets[bucket].Add(step);
            }

            var restoredHere = RestoredCheckpoints(undoableRestores, findCheckpoint);
            var undone = WorkflowManager.UndoneSteps(history.restores);
            for (int k = checkpoints.Count; k >= 0; k--)
            {
                var checkpoint = k > 0 ? checkpoints[k - 1] : null;
                var steps = buckets[k].OrderByDescending(task => task.seq)
                    .Where(task => MatchesAgent(task.agent, agentFilter))
                    .ToList();
                bool checkpointShown = checkpoint != null && MatchesAgent(checkpoint.agent, agentFilter);
                if (!checkpointShown && steps.Count == 0)
                    continue;

                var group = new HistoryGroup
                {
                    Checkpoint = checkpoint,
                    TitleKind = TitleKindOf(checkpoint),
                    Label = checkpoint?.label,
                    Agents = checkpoint != null
                        ? new List<string> { checkpoint.agent ?? string.Empty }
                        : steps.Select(step => step.agent ?? string.Empty).Distinct().ToList(),
                    UndoRestoreTaskId = checkpoint != null && restoredHere.TryGetValue(checkpoint.id, out string taskId) ? taskId : null,
                };
                group.Steps.AddRange(steps.Select(step => StepOf(step, undone.Contains(step.seq), callerAgent)));
                AddTimes(group, checkpoint, steps);
                AddSummary(group, steps);
                groups.Add(group);
            }
            return groups;
        }

        /// <summary>The checkpoint each restore went back to, mapped to the taskId (s&lt;seq&gt;) that undoes it. Newest restore wins.</summary>
        private static Dictionary<string, string> RestoredCheckpoints(IList<WorkflowRestoreRecord> undoableRestores,
            Func<string, WorkflowCheckpoint> findCheckpoint)
        {
            var taskIds = new Dictionary<string, string>();
            foreach (var restore in undoableRestores)
            {
                var target = findCheckpoint(restore.to);
                if (target != null && !taskIds.ContainsKey(target.id))
                    taskIds[target.id] = "s" + restore.seq;
            }
            return taskIds;
        }

        internal static HistoryTitleKind TitleKindOf(WorkflowCheckpoint checkpoint)
        {
            if (checkpoint == null) return HistoryTitleKind.BeforeFirstCheckpoint;
            if (checkpoint.kind == WorkflowCheckpoint.KindGuard) return HistoryTitleKind.Guard;
            if (checkpoint.kind == WorkflowCheckpoint.KindAuto) return HistoryTitleKind.Auto;
            return string.IsNullOrEmpty(checkpoint.label) ? HistoryTitleKind.Unnamed : HistoryTitleKind.Label;
        }

        private static bool MatchesAgent(string agent, string agentFilter) =>
            string.IsNullOrEmpty(agentFilter) || (agent ?? string.Empty) == agentFilter;

        private static void AddTimes(HistoryGroup group, WorkflowCheckpoint checkpoint, List<WorkflowTask> steps)
        {
            var times = steps.Select(step => step.timestamp).ToList();
            if (checkpoint != null)
                times.Add(checkpoint.timestampMs / 1000);
            group.FirstTimeSeconds = times.Count == 0 ? 0 : times.Min();
            group.LastTimeSeconds = times.Count == 0 ? 0 : times.Max();
        }

        /// <summary>The summary line of a group: per verb, the names of its objects, newest first, at most <see cref="MaxNamesPerVerb"/> of them.</summary>
        private static void AddSummary(HistoryGroup group, List<WorkflowTask> steps)
        {
            var verbs = new List<HistoryVerb>();
            var namesByVerb = new Dictionary<HistoryVerb, List<string>>();
            foreach (var step in steps)
            {
                foreach (var (verb, name) in VerbsOf(step))
                {
                    if (!namesByVerb.TryGetValue(verb, out var names))
                    {
                        names = new List<string>();
                        namesByVerb[verb] = names;
                        verbs.Add(verb);
                    }
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        names.Add(name);
                }
            }
            group.SummaryParts.AddRange(verbs.Select(verb => SummaryPart(verb, namesByVerb[verb])));
        }

        private static string SummaryPart(HistoryVerb verb, List<string> names)
        {
            string verbText = VerbText(verb);
            if (names.Count == 0)
                return verbText;

            string shown = string.Join(", ", names.Take(MaxNamesPerVerb));
            int more = names.Count - MaxNamesPerVerb;
            if (more > 0)
                shown += " " + string.Format(SkillsLocalization.Get("history_names_more_fmt"), more);
            return string.Format(SkillsLocalization.Get("history_summary_part_fmt"), verbText, shown);
        }

        /// <summary>
        /// What a step did, per object: its snapshots give the verb of each object; a step without snapshots takes the verb of its skill
        /// and names its main target. Never empty.
        /// </summary>
        private static List<(HistoryVerb Verb, string Name)> VerbsOf(WorkflowTask step)
        {
            var verbs = new List<(HistoryVerb Verb, string Name)>();
            if (step.snapshots != null)
            {
                foreach (var snapshot in step.snapshots)
                {
                    if (snapshot != null)
                        verbs.Add((VerbOf(snapshot.type), ObjectName(snapshot)));
                }
            }
            if (verbs.Count == 0)
                verbs.Add((FallbackVerb(step.tag), step.target));
            return verbs;
        }

        private static HistoryVerb VerbOf(SnapshotType type)
        {
            switch (type)
            {
                case SnapshotType.Created: return HistoryVerb.Create;
                case SnapshotType.Deleted: return HistoryVerb.Delete;
                case SnapshotType.Moved: return HistoryVerb.Move;
                default: return HistoryVerb.Modify;
            }
        }

        private static HistoryVerb FallbackVerb(string tag)
        {
            if (string.IsNullOrEmpty(tag) || !SkillRouter.TryGetSkill(tag, out var skill))
                return HistoryVerb.Generic;
            if ((skill.Operation & SkillOperation.Create) != 0) return HistoryVerb.Create;
            if ((skill.Operation & SkillOperation.Modify) != 0) return HistoryVerb.Modify;
            if ((skill.Operation & SkillOperation.Delete) != 0) return HistoryVerb.Delete;
            if ((skill.Operation & SkillOperation.Execute) != 0) return HistoryVerb.Run;
            return HistoryVerb.Generic;
        }

        private static string ObjectName(ObjectSnapshot snapshot)
        {
            if (!string.IsNullOrEmpty(snapshot.objectName))
                return snapshot.objectName;
            return string.IsNullOrEmpty(snapshot.assetPath) ? null : Path.GetFileName(snapshot.assetPath);
        }

        private static string VerbText(HistoryVerb verb)
        {
            switch (verb)
            {
                case HistoryVerb.Create: return SkillsLocalization.Get("history_verb_create");
                case HistoryVerb.Modify: return SkillsLocalization.Get("history_verb_modify");
                case HistoryVerb.Delete: return SkillsLocalization.Get("history_verb_delete");
                case HistoryVerb.Move: return SkillsLocalization.Get("history_verb_move");
                case HistoryVerb.Run: return SkillsLocalization.Get("history_verb_run");
                default: return SkillsLocalization.Get("history_verb_generic");
            }
        }

        private static HistoryStep StepOf(WorkflowTask step, bool isReverted, string callerAgent)
        {
            string agent = step.agent ?? string.Empty;
            return new HistoryStep
            {
                Id = step.DisplayId,
                Label = step.tag ?? step.id,
                Verb = VerbsOf(step)[0].Verb,
                Target = step.target,
                Agent = agent,
                IsOtherAgent = agent != callerAgent,
                IsReverted = isReverted,
                TimeSeconds = step.timestamp,
            };
        }

        private VisualElement BuildGroup(HistoryGroup group, bool placePreview)
        {
            string key = group.Key;
            bool open = placePreview || _openGroups.Contains(key);

            var root = new VisualElement();
            root.AddToClassList("history-group");
            root.EnableInClassList("history-group--pre", group.Checkpoint == null);
            root.EnableInClassList("history-group--open", open);

            var body = new VisualElement();
            body.AddToClassList("history-group__body");
            UiVisibility.SetVisible(body, open);

            var button = BuildGroupButton(group);
            var header = BuildGroupHeader(group, button, open);
            header.RegisterCallback<ClickEvent>(evt =>
            {
                // The header toggles the group; its own button keeps its action.
                if (button != null && IsWithin(evt.target as VisualElement, button))
                    return;
                Guard(() => ToggleGroup(key, root, body));
            });
            root.Add(header);

            if (placePreview)
            {
                body.Add(_restorePreview);
                _restorePreview.SetVisible(true);
            }
            if (group.Steps.Count == 0)
                body.Add(StyledLabel(SkillsLocalization.Get("history_group_no_steps"), "history-group__empty"));
            foreach (var step in group.Steps)
                body.Add(BuildStepRow(step));

            root.Add(body);
            return root;
        }

        /// <summary>The header row: chevron, title with its chips, the summary line, the step count, then the group's action.</summary>
        private static VisualElement BuildGroupHeader(HistoryGroup group, Button button, bool open)
        {
            var header = new VisualElement();
            header.AddToClassList("history-group__header");

            header.Add(VectorGlyphs.Disclosure(open, "history-group-chevron"));

            var text = new VisualElement();
            text.AddToClassList("history-group__text");
            var line = new VisualElement();
            line.AddToClassList("history-group__line");
            line.Add(StyledLabel(TitleText(group), "history-group__title"));
            if (group.Id != null)
                line.Add(StyledLabel(group.Id, "history-group__chip"));
            if (group.RestoredHere)
                line.Add(StyledLabel(SkillsLocalization.Get("history_restored_here"), "history-group__chip", "history-group__chip--restored"));
            int undone = group.Steps.Count(step => step.IsReverted);
            if (undone > 0)
                line.Add(StyledLabel(string.Format(SkillsLocalization.Get("history_group_undone_fmt"), undone),
                    "history-group__chip", "history-group__chip--undone"));
            text.Add(line);

            string meta = JoinWith(Separator, FormatTime(group.FirstTimeSeconds), string.Join(", ", group.Agents),
                string.Join(Separator, group.SummaryParts));
            text.Add(StyledLabel(meta, "history-group__summary"));
            header.Add(text);

            header.Add(StyledLabel(string.Format(SkillsLocalization.Get("history_group_steps_fmt"), group.StepCount), "history-group__count"));
            if (button != null)
                header.Add(button);
            return header;
        }

        private static string TitleText(HistoryGroup group)
        {
            switch (group.TitleKind)
            {
                case HistoryTitleKind.Label: return group.Label;
                case HistoryTitleKind.Auto: return SkillsLocalization.Get("history_checkpoint_auto_title");
                case HistoryTitleKind.Guard: return SkillsLocalization.Get("history_checkpoint_guard");
                case HistoryTitleKind.Unnamed: return SkillsLocalization.Get("history_checkpoint_unnamed");
                default: return SkillsLocalization.Get("history_group_before_first");
            }
        }

        /// <summary>The group's action: Undo restore when a restore went back to it, else Preview restore. Null for the steps before the first checkpoint.</summary>
        private Button BuildGroupButton(HistoryGroup group)
        {
            if (group.Checkpoint == null)
                return null;

            var button = new Button();
            button.AddToClassList("mini-btn");
            if (group.RestoredHere)
            {
                string taskId = group.UndoRestoreTaskId;
                string groupKey = group.Key;
                button.text = SkillsLocalization.Get("history_group_undo_btn");
                button.AddToClassList("history-group__undo-btn");
                button.clicked += () => Guard(() => ShowUndoPreview(taskId, groupKey));
            }
            else
            {
                string checkpointId = group.Id;
                button.text = SkillsLocalization.Get("history_preview_btn");
                button.AddToClassList("history-group__preview-btn");
                button.clicked += () => Guard(() => ShowPreview(checkpointId));
            }
            return button;
        }

        private static bool IsWithin(VisualElement element, VisualElement ancestor)
        {
            for (var current = element; current != null; current = current.parent)
            {
                if (current == ancestor)
                    return true;
            }
            return false;
        }

        private VisualElement BuildStepRow(HistoryStep step)
        {
            var row = new VisualElement();
            row.AddToClassList("history-step");
            row.EnableInClassList("history-step--reverted", step.IsReverted);

            var dot = new VisualElement();
            dot.AddToClassList("history-step__dot");
            row.Add(dot);
            row.Add(StyledLabel(VerbText(step.Verb), "history-step__verb"));

            var target = StyledLabel(ShortName(step.Target), "history-step__target");
            target.tooltip = step.Target ?? string.Empty;
            row.Add(target);
            row.Add(StyledLabel(JoinParts(step.Label, step.Id), "history-step__tag"));

            if (step.IsOtherAgent)
                row.Add(StyledLabel("@" + step.Agent, "history-step__agent"));
            if (step.IsReverted)
                row.Add(StyledLabel(SkillsLocalization.Get("history_step_undone"), "history-step__undone"));
            row.Add(StyledLabel(FormatTime(step.TimeSeconds), "history-step__time"));
            return row;
        }

        private static string ShortName(string target) =>
            string.IsNullOrEmpty(target) ? string.Empty : target.Substring(target.LastIndexOf('/') + 1);

        private Button BuildShowEarlierButton(int remaining)
        {
            var button = new Button();
            button.AddToClassList("mini-btn");
            button.AddToClassList("history-show-earlier");
            button.text = string.Format(SkillsLocalization.Get("history_show_earlier_fmt"), Math.Min(GroupPageSize, remaining));
            button.clicked += () => Guard(() =>
            {
                _visibleGroups += GroupPageSize;
                RebuildTimeline();
            });
            return button;
        }

        /// <summary>Opens or closes a group in place; collapsing a group closes its preview, whose panel stays hidden inside it.</summary>
        private void ToggleGroup(string key, VisualElement root, VisualElement body)
        {
            bool open = !_openGroups.Contains(key);
            if (open)
                _openGroups.Add(key);
            else
                _openGroups.Remove(key);
            root.EnableInClassList("history-group--open", open);
            VectorGlyphs.SetOpen(root.Q("history-group-chevron"), open);
            UiVisibility.SetVisible(body, open);
            if (!open && _previewGroupKey == key)
                ClearPreview();
        }

        private static string JoinWith(string separator, params string[] parts) =>
            string.Join(separator, parts.Where(part => !string.IsNullOrEmpty(part)));

        private static string JoinParts(params string[] parts) =>
            string.Join(" ", parts.Where(part => !string.IsNullOrEmpty(part)));

        private static string FormatTime(long unixSeconds) =>
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("HH:mm:ss");

        private static Label StyledLabel(string text, params string[] classNames)
        {
            var label = new Label(text ?? string.Empty);
            foreach (string className in classNames)
                label.AddToClassList(className);
            return label;
        }

        private void ShowPreview(string checkpointId)
        {
            var preview = WorkflowManager.PreviewTo(checkpointId, true, WorkflowManager.ResolveCallerAgent());
            OpenPreview(preview, PreviewKind.Restore, checkpointId, null);
        }

        private void ShowUndoPreview(string taskId, string groupKey)
        {
            var preview = WorkflowManager.PreviewRedo(taskId, WorkflowManager.ResolveCallerAgent());
            OpenPreview(preview, PreviewKind.Undo, groupKey, taskId);
        }

        private void OpenPreview(RestorePreview preview, PreviewKind kind, string groupKey, string taskId)
        {
            if (preview.error != null)
            {
                ClearPreview();
                ShowResultMessage(preview.error, true);
                RebuildTimeline();
                return;
            }

            _previewKind = kind;
            _previewGroupKey = groupKey;
            _previewTaskId = taskId;
            _openGroups.Add(groupKey);

            _summaryLabel.text = FormatRestoreSummary(preview.plan);
            BuildConflictRows(preview.conflicts);
            _coverageWarning.SetVisible(preview.coverage == "partial");
            _unresolvableLabel.SetVisible(preview.unresolvable.Count > 0);
            _unresolvableLabel.text = string.Format(
                SkillsLocalization.Get("history_unresolvable_fmt"), preview.unresolvable.Count);
            _previewRestoreBtn.text = SkillsLocalization.Get(kind == PreviewKind.Undo ? "history_group_undo_btn" : "history_restore_btn");
            RebuildTimeline();
        }

        private void ClearPreview()
        {
            _previewKind = PreviewKind.None;
            _previewGroupKey = null;
            _previewTaskId = null;
            _restorePreview.SetVisible(false);
        }

        private void CancelPreview()
        {
            ClearPreview();
            RebuildTimeline();
        }

        private void ConfirmPreview()
        {
            if (_previewKind == PreviewKind.Restore)
                RestorePreviewed();
            else if (_previewKind == PreviewKind.Undo)
                UndoPreviewed();
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

        /// <summary>The conflicts the user chose to overwrite: null when the preview had none, "keep" when none was chosen.</summary>
        private string SelectedConflicts()
        {
            if (_conflictToggles.Count == 0)
                return null;
            var chosen = _conflictToggles.Where(entry => entry.Overwrite.value).Select(entry => entry.Id).ToList();
            return chosen.Count == 0 ? "keep" : string.Join(",", chosen);
        }

        private void RestorePreviewed()
        {
            string checkpointId = _previewGroupKey;
            var outcome = WorkflowManager.RestoreTo(checkpointId, true, WorkflowManager.ResolveCallerAgent(), SelectedConflicts());
            if (outcome.error != null)
            {
                // Something changed since the preview (or the position is gone): show why and offer the fresh conflicts.
                ShowResultMessage(outcome.error, true);
                ShowPreview(checkpointId);
                return;
            }

            ClearPreview();
            int applied = outcome.restored + outcome.deleted + outcome.recreated;
            ShowResultMessage(string.Format(SkillsLocalization.Get("history_restore_done_fmt"),
                outcome.to, applied, applied + outcome.failed), outcome.failed > 0);
            foreach (string item in outcome.failedItems)
                SkillsLogger.LogWarning($"Restore failed for {item}");
            RefreshHistory();
        }

        private void UndoPreviewed()
        {
            string taskId = _previewTaskId;
            string groupKey = _previewGroupKey;
            var outcome = WorkflowManager.RedoRestore(taskId, SelectedConflicts());
            if (outcome.error != null)
            {
                ShowResultMessage(outcome.error, true);
                ShowUndoPreview(taskId, groupKey);
                return;
            }

            ClearPreview();
            int applied = outcome.restored + outcome.deleted + outcome.recreated;
            ShowResultMessage(string.Format(SkillsLocalization.Get("history_undo_done_fmt"),
                taskId, applied, applied + outcome.failed), outcome.failed > 0);
            foreach (string item in outcome.failedItems)
                SkillsLogger.LogWarning($"Undo restore failed for {item}");
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

        private void ShowResultMessage(string message, bool isError)
        {
            if (_resultHelpBox == null) return;
            _resultHelpBox.text = message;
            _resultHelpBox.messageType = isError ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
            _resultHelpBox.SetVisible(true);
        }
    }

    internal enum HistoryTitleKind { BeforeFirstCheckpoint, Label, Auto, Guard, Unnamed }

    /// <summary>What a step did to one of its objects, or the skill's operation when the step recorded no object.</summary>
    internal enum HistoryVerb { Generic, Create, Modify, Delete, Move, Run }

    /// <summary>One group of the History tab: a checkpoint (null for the steps before the first one) and the steps it heads.</summary>
    internal sealed class HistoryGroup
    {
        public WorkflowCheckpoint Checkpoint;
        public HistoryTitleKind TitleKind;
        public string Label;
        public readonly List<HistoryStep> Steps = new List<HistoryStep>();
        public List<string> Agents = new List<string>();
        public readonly List<string> SummaryParts = new List<string>();
        public long FirstTimeSeconds;
        public long LastTimeSeconds;
        // The taskId (s<seq>) of the undoable restore that went back to this checkpoint; null when none is undoable.
        public string UndoRestoreTaskId;

        public string Key => Checkpoint?.id ?? HistoryTabController.PreGroupKey;
        public string Id => Checkpoint?.id;
        public int StepCount => Steps.Count;
        public bool RestoredHere => UndoRestoreTaskId != null;
    }

    /// <summary>One step of a group, as the History tab lists it.</summary>
    internal sealed class HistoryStep
    {
        public string Id;
        public string Label;
        public HistoryVerb Verb;
        public string Target;
        public string Agent;
        public bool IsOtherAgent;
        public bool IsReverted;
        public long TimeSeconds;
    }
}

// Producer:Betsy
