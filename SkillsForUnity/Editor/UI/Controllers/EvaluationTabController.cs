using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// The Evaluation tab (contract 7.6) in four steps: choose tasks, hand the prompt to the user's own agent, follow
    /// progress, read the result. The panel never starts an agent. Current tier and Pick tasks run the runner's
    /// <c>start</c> here; All tasks writes a group record and gives the agent a prompt that starts the six sessions in
    /// order. Progress is read straight from the runner's state files and a score only appears from report.json.
    /// Prompt bodies are copied or opened, never rendered, except the English group prompt: the bundled CJK atlas
    /// cannot guarantee arbitrary text. Pending commands and a running group are followed by one
    /// <see cref="EditorUiScheduler.RepeatSafe"/> tick while the tab is visible; everything else refreshes on
    /// activation, a button or a result.
    /// </summary>
    internal sealed class EvaluationTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/EvaluationTab.uxml";
        private const string ConfigPrefKey = "UnitySkills_EvalPanelConfig";
        internal const string ProblemKeyPrefix = "tab_eval_problem_";
        private const int TrackIntervalMs = 1000;
        private const int HistoryLimit = 10;
        private const int GroupSessionScan = 12;
        private const string SessionMode = "suite";
        private const string ScopeTier = "tier";
        private const string ScopeAll = "all";
        private const string ScopePick = "pick";
        private const string GroupCopyKey = "group";
        private const string ChipDone = "ev-chip--done";

        private static readonly string[] Tracks = EvaluationState.TrackIds;
        private static readonly string[] Tiers = EvaluationState.TierIds;
        private static readonly string[] Scopes = { ScopeTier, ScopeAll, ScopePick };
        private static readonly string[] PromptLanguages = { "zh-CN", "en" };
        private static readonly string[] ModelOutcomes = { "pass", "partial", "fail" };

        /// <summary>The remembered choices of step 1: a per-project setting (ProjectPrefs), prompt language excepted.</summary>
        internal sealed class PanelConfig
        {
            public string Track = "ops";
            public string Tier = "basic";
            public string Scope = ScopeTier;
        }

        private enum ModeNote { None, Restored, KeyCleared }

        private readonly VisualElement _root;
        private readonly string _projectRoot;
        private readonly JObject _manifest;
        private readonly string _pipeline;
        private readonly List<PanelCommand> _pending = new List<PanelCommand>();
        private readonly Dictionary<string, EvaluationOutcome> _outcomes = new Dictionary<string, EvaluationOutcome>(StringComparer.Ordinal);

        private IVisualElementScheduledItem _tick;
        private bool _disposed;
        private PanelConfig _config = new PanelConfig();
        private string _promptLang;
        private bool _promptLangChosen;

        private EvaluationState.CurrentInfo _current;
        private EvaluationState.SessionInfo _session;
        private EvaluationState.AttemptInfo _attempt;
        private EvaluationProblem _stateIssue;
        private EvaluationState.LockInfo _lock;
        private bool _runnerBusy;
        private string _lastSessionStatus;
        private List<EvaluationState.SessionInfo> _history = new List<EvaluationState.SessionInfo>();
        private EvaluationMarker.MarkerState _marker;

        private EvaluationState.GroupRecord _group;
        private EvaluationState.GroupProgress _groupProgress;
        private bool _groupResultHidden;
        private EvaluationState.SessionInfo _resultSession;

        private HashSet<string> _picks;
        private string _picksKey;

        private bool _markerConfirmOpen;
        private string _markerErrorText;
        private bool _abortConfirmOpen;
        private string _viewedSessionId;
        private string _copiedKey;
        private string _copyFailedPath;
        private string _usageEditorAttempt;
        private ModeNote _modeNote;
        private SkillsOperatingMode _restoredMode;

        private ScrollView _scroll;
        private Label _title, _state, _envLine, _activity;
        private Button _checkBtn;
        private VisualElement _envSection, _envMessages;
        private Label _markerLabel, _markerValue, _markerConfirmText, _markerError;
        private Button _markerBtn, _markerConfirmOk, _markerConfirmCancel;
        private VisualElement _markerConfirm;
        private Label _pythonLabel, _pythonValue, _pythonOverrideLabel;
        private TextField _pythonOverride;
        private Button _pythonDetectBtn, _pythonUseBtn, _pythonClearBtn;
        private Label _modeLabel, _modeValue, _modeNoteLabel;
        private Button _modeSwitchBtn, _modeRestoreBtn, _cleanupBtn;
        private Label _checkSummary;
        private VisualElement _intro;
        private Label _introPanelK, _introPanelV, _introAgentK, _introAgentV, _introBoth;

        private VisualElement _result, _resultTiers, _resultFill, _reportTasks, _reportMessages;
        private Label _resultTitle, _resultSessionLabel, _resultScore, _resultDen, _resultGrade, _resultMeta, _resultModels;
        private Label _resultVerdict, _reportUnofficial;
        private Button _reportBtn, _openReportBtn, _revealReportBtn;

        private VisualElement _trackRow, _tierRow, _picksBox, _startMessages;
        private Label _configTitle, _configCount, _trackLabel, _tierLabel, _langLabel, _allHint, _configLocked;
        private DropdownField _track, _tier, _lang;
        private readonly Dictionary<string, Button> _scopeButtons = new Dictionary<string, Button>(StringComparer.Ordinal);
        private Button _generateBtn, _clearBtn;

        private VisualElement _prompt, _promptScroll;
        private Label _promptTitle, _promptHint, _promptText, _promptNote, _copied;
        private Button _copyPromptBtn, _openPromptBtn;

        private VisualElement _progress, _progressFill, _taskList, _abortConfirm, _progressMessages;
        private Label _progressTitle, _progressCount, _sessionLine, _taskLine, _submitHint, _abortConfirmText;
        private Button _refreshBtn, _submitBtn, _abortBtn, _abortConfirmOk, _abortConfirmCancel;

        private Foldout _historyFoldout;
        private VisualElement _historyList;

        public EvaluationTabController(VisualElement root, UnitySkillsWindow window)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(TabUxmlPath);
            if (uxml == null)
            {
                SkillsLogger.LogError($"Failed to load Evaluation tab UXML: {TabUxmlPath}");
                return;
            }
            uxml.CloneTree(_root);

            _manifest = EvaluationState.ReadPackManifest();
            _pipeline = ProjectSkills.DetectRenderPipeline().ToString();
            CacheUiReferences();
            LoadConfig();
            _promptLang = DefaultPromptLanguage();
            BindEvents();
            SkillsModeManager.OnChanged += OnModeChanged;
            _pythonOverride?.SetValueWithoutNotify(PythonLocator.OverridePath);

            // A domain reload drops every Process object: re-attach to the commands whose result was not consumed.
            _pending.AddRange(EvaluationRunnerService.LoadPending(_projectRoot));
            foreach (var pair in EvaluationRunnerService.LoadLatestOutcomes(_projectRoot))
                _outcomes[pair.Key] = pair.Value;
            _group = EvaluationState.ReadPanelGroup(_projectRoot);

            CheckModeRestore(false);
            ApplyStaticTexts();
            ApplyChoices();
            Refresh(true);
            UpdateTracking();
        }

        /// <summary>Tab activation: detect Python once, consume finished commands, re-read everything.</summary>
        public void OnTabSelected()
        {
            if (_disposed || _scroll == null) return;
            if (PythonLocator.Last == null && !PythonLocator.IsLocating) DetectPython();
            CheckModeRestore(false);
            PollPending();
            Refresh(true);
            UpdateTracking();
        }

        public void RefreshLocalization()
        {
            if (_disposed || _scroll == null) return;
            if (!_promptLangChosen) _promptLang = DefaultPromptLanguage();
            ApplyStaticTexts();
            ApplyChoices();
            Refresh(true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tick?.Pause();
            _tick = null;
            SkillsModeManager.OnChanged -= OnModeChanged;
        }

        // ===== Problem localization (contract 3: tab_eval_problem_<code_lower>, args fill {0}{1}...) =====

        internal static string LocalizeProblem(EvaluationProblem problem)
        {
            if (problem == null) return string.Empty;
            if (string.IsNullOrEmpty(problem.Code)) return problem.Fix ?? string.Empty;
            var key = ProblemKeyPrefix + problem.Code.ToLowerInvariant();
            if (!SkillsLocalization.Has(key)) return string.IsNullOrEmpty(problem.Fix) ? problem.Code : problem.Fix;

            // string.Format throws on a placeholder past the array, so absent args become empty strings.
            var args = new object[Math.Max(4, problem.Args.Count)];
            for (int i = 0; i < args.Length; i++) args[i] = i < problem.Args.Count ? problem.Args[i] : string.Empty;
            return SkillsLocalization.Get(key, args);
        }

        // ===== Wiring =====

        private void CacheUiReferences()
        {
            _scroll = _root.Q<ScrollView>("eval-scroll");
            _title = _root.Q<Label>("eval-title");
            _state = _root.Q<Label>("eval-state");
            _envLine = _root.Q<Label>("eval-env-line");
            _checkBtn = _root.Q<Button>("eval-check-btn");
            _activity = _root.Q<Label>("eval-activity");

            _envSection = _root.Q<VisualElement>("eval-env-section");
            _markerLabel = _root.Q<Label>("eval-marker-label");
            _markerValue = _root.Q<Label>("eval-marker-value");
            _markerBtn = _root.Q<Button>("eval-marker-btn");
            _markerConfirm = _root.Q<VisualElement>("eval-marker-confirm");
            _markerConfirmText = _root.Q<Label>("eval-marker-confirm-text");
            _markerError = _root.Q<Label>("eval-marker-error");
            _markerConfirmOk = _root.Q<Button>("eval-marker-confirm-ok");
            _markerConfirmCancel = _root.Q<Button>("eval-marker-confirm-cancel");
            _pythonLabel = _root.Q<Label>("eval-python-label");
            _pythonValue = _root.Q<Label>("eval-python-value");
            _pythonDetectBtn = _root.Q<Button>("eval-python-detect-btn");
            _pythonOverrideLabel = _root.Q<Label>("eval-python-override-label");
            _pythonOverride = _root.Q<TextField>("eval-python-override");
            _pythonUseBtn = _root.Q<Button>("eval-python-use-btn");
            _pythonClearBtn = _root.Q<Button>("eval-python-clear-btn");
            _modeLabel = _root.Q<Label>("eval-mode-label");
            _modeValue = _root.Q<Label>("eval-mode-value");
            _modeSwitchBtn = _root.Q<Button>("eval-mode-switch-btn");
            _modeRestoreBtn = _root.Q<Button>("eval-mode-restore-btn");
            _modeNoteLabel = _root.Q<Label>("eval-mode-note");
            _cleanupBtn = _root.Q<Button>("eval-cleanup-btn");
            _checkSummary = _root.Q<Label>("eval-check-summary");
            _envMessages = _root.Q<VisualElement>("eval-env-messages");

            _intro = _root.Q<VisualElement>("eval-intro");
            _introPanelK = _root.Q<Label>("eval-intro-panel-k");
            _introPanelV = _root.Q<Label>("eval-intro-panel-v");
            _introAgentK = _root.Q<Label>("eval-intro-agent-k");
            _introAgentV = _root.Q<Label>("eval-intro-agent-v");
            _introBoth = _root.Q<Label>("eval-intro-both");

            _result = _root.Q<VisualElement>("eval-result");
            _resultTitle = _root.Q<Label>("eval-result-title");
            _resultSessionLabel = _root.Q<Label>("eval-result-session");
            _resultScore = _root.Q<Label>("eval-result-score");
            _resultDen = _root.Q<Label>("eval-result-den");
            _resultGrade = _root.Q<Label>("eval-result-grade");
            _resultFill = _root.Q<VisualElement>("eval-result-fill");
            _resultMeta = _root.Q<Label>("eval-result-meta");
            _resultModels = _root.Q<Label>("eval-result-models");
            _resultVerdict = _root.Q<Label>("eval-result-verdict");
            _resultTiers = _root.Q<VisualElement>("eval-result-tiers");
            _reportUnofficial = _root.Q<Label>("eval-report-unofficial");
            _reportTasks = _root.Q<VisualElement>("eval-report-tasks");
            _reportBtn = _root.Q<Button>("eval-report-btn");
            _openReportBtn = _root.Q<Button>("eval-open-report-btn");
            _revealReportBtn = _root.Q<Button>("eval-reveal-report-btn");
            _reportMessages = _root.Q<VisualElement>("eval-report-messages");

            _configTitle = _root.Q<Label>("eval-config-title");
            _configCount = _root.Q<Label>("eval-config-count");
            _scopeButtons[ScopeTier] = _root.Q<Button>("eval-scope-tier");
            _scopeButtons[ScopeAll] = _root.Q<Button>("eval-scope-all");
            _scopeButtons[ScopePick] = _root.Q<Button>("eval-scope-pick");
            _trackRow = _root.Q<VisualElement>("eval-track-row");
            _tierRow = _root.Q<VisualElement>("eval-tier-row");
            _trackLabel = _root.Q<Label>("eval-track-label");
            _tierLabel = _root.Q<Label>("eval-tier-label");
            _langLabel = _root.Q<Label>("eval-lang-label");
            _track = _root.Q<DropdownField>("eval-track");
            _tier = _root.Q<DropdownField>("eval-tier");
            _lang = _root.Q<DropdownField>("eval-lang");
            _allHint = _root.Q<Label>("eval-all-hint");
            _picksBox = _root.Q<VisualElement>("eval-picks");
            _generateBtn = _root.Q<Button>("eval-generate-btn");
            _clearBtn = _root.Q<Button>("eval-clear-btn");
            _configLocked = _root.Q<Label>("eval-config-locked");
            _startMessages = _root.Q<VisualElement>("eval-start-messages");

            _prompt = _root.Q<VisualElement>("eval-prompt");
            _promptTitle = _root.Q<Label>("eval-prompt-title");
            _promptHint = _root.Q<Label>("eval-prompt-hint");
            _promptScroll = _root.Q<VisualElement>("eval-prompt-scroll");
            _promptText = _root.Q<Label>("eval-prompt-text");
            _promptNote = _root.Q<Label>("eval-prompt-note");
            _copyPromptBtn = _root.Q<Button>("eval-copy-prompt-btn");
            _openPromptBtn = _root.Q<Button>("eval-open-prompt-btn");
            _copied = _root.Q<Label>("eval-copied");

            _progress = _root.Q<VisualElement>("eval-progress");
            _progressTitle = _root.Q<Label>("eval-progress-title");
            _refreshBtn = _root.Q<Button>("eval-refresh-btn");
            _progressCount = _root.Q<Label>("eval-progress-count");
            _progressFill = _root.Q<VisualElement>("eval-progress-fill");
            _sessionLine = _root.Q<Label>("eval-session-line");
            _taskLine = _root.Q<Label>("eval-task-line");
            _taskList = _root.Q<VisualElement>("eval-task-list");
            _submitHint = _root.Q<Label>("eval-submit-hint");
            _submitBtn = _root.Q<Button>("eval-submit-btn");
            _abortBtn = _root.Q<Button>("eval-abort-btn");
            _abortConfirm = _root.Q<VisualElement>("eval-abort-confirm");
            _abortConfirmText = _root.Q<Label>("eval-abort-confirm-text");
            _abortConfirmOk = _root.Q<Button>("eval-abort-confirm-ok");
            _abortConfirmCancel = _root.Q<Button>("eval-abort-confirm-cancel");
            _progressMessages = _root.Q<VisualElement>("eval-progress-messages");

            _historyFoldout = _root.Q<Foldout>("eval-history");
            _historyList = _root.Q<VisualElement>("eval-history-list");

            VectorGlyphs.SetIcon(_refreshBtn, VectorGlyph.Refresh);
        }

        private void BindEvents()
        {
            OnClick(_markerBtn, () =>
            {
                _markerConfirmOpen = true;
                _markerErrorText = null;
                RenderMarker();
            });
            OnClick(_markerConfirmOk, WriteMarker);
            OnClick(_markerConfirmCancel, () =>
            {
                _markerConfirmOpen = false;
                RenderMarker();
            });
            OnClick(_pythonDetectBtn, DetectPython);
            OnClick(_pythonUseBtn, () =>
            {
                PythonLocator.OverridePath = _pythonOverride?.value;
                DetectPython();
            });
            OnClick(_pythonClearBtn, () =>
            {
                PythonLocator.OverridePath = null;
                _pythonOverride?.SetValueWithoutNotify(string.Empty);
                DetectPython();
            });
            OnClick(_modeSwitchBtn, SwitchToBypass);
            OnClick(_modeRestoreBtn, () => CheckModeRestore(true));
            OnClick(_checkBtn, () => Launch("check", new List<string> { "--track", _config.Track, "--tier", _config.Tier }));
            OnClick(_cleanupBtn, () => Launch("cleanup", new List<string> { "--stale" }));

            OnChoice(_track, Tracks, value => _config.Track = value);
            OnChoice(_tier, Tiers, value => _config.Tier = value);
            _lang?.RegisterValueChangedCallback(evt =>
            {
                var value = ValueOf(_lang, PromptLanguages, evt.newValue);
                if (value == null) return;
                _promptLang = value;
                _promptLangChosen = true;
            });

            foreach (var pair in _scopeButtons)
            {
                var scope = pair.Key;
                OnClick(pair.Value, () => SetScope(scope));
            }
            OnClick(_generateBtn, Generate);
            OnClick(_clearBtn, ClearGroup);

            OnClick(_copyPromptBtn, CopyPrompt);
            OnClick(_openPromptBtn, () => OpenFile(PromptFile(_session)));
            OnClick(_refreshBtn, () =>
            {
                PollPending();
                Refresh(true);
            });
            OnClick(_submitBtn, SubmitForAgent);
            OnClick(_abortBtn, () =>
            {
                _abortConfirmOpen = true;
                RenderProgress();
            });
            OnClick(_abortConfirmOk, () =>
            {
                _abortConfirmOpen = false;
                if (_session == null) return;
                Launch("abort", new List<string> { "--session", _session.SessionId, "--reason", "user" });
                // Aborting ends the All-tasks run too: the agent's next session would start from the prompt otherwise.
                if (IsPending("abort") && _group != null) ClearGroup();
            });
            OnClick(_abortConfirmCancel, () =>
            {
                _abortConfirmOpen = false;
                RenderProgress();
            });
            OnClick(_reportBtn, () =>
            {
                var viewed = ViewedSession();
                if (viewed != null) Launch("report", new List<string> { "--session", viewed.SessionId });
            });
            OnClick(_openReportBtn, () => OpenFile(EvaluationState.ReportMarkdownPath(_projectRoot, ViewedSession())));
            OnClick(_revealReportBtn, () =>
            {
                var path = EvaluationState.ReportMarkdownPath(_projectRoot, ViewedSession());
                if (path != null && File.Exists(path)) EditorUtility.RevealInFinder(path);
            });
        }

        private static void OnClick(Button button, Action action)
        {
            if (button != null) button.clicked += action;
        }

        private void OnChoice(DropdownField field, string[] values, Action<string> apply)
        {
            field?.RegisterValueChangedCallback(evt =>
            {
                var value = ValueOf(field, values, evt.newValue);
                if (value == null) return;
                apply(value);
                SaveConfig();
                Refresh(false);
            });
        }

        private static string ValueOf(DropdownField field, string[] values, string label)
        {
            var index = field.choices?.IndexOf(label) ?? -1;
            return index >= 0 && index < values.Length ? values[index] : null;
        }

        private static void SetChoices(DropdownField field, string[] values, List<string> labels, string selected)
        {
            if (field == null) return;
            field.choices = labels;
            field.SetValueWithoutNotify(labels[Math.Max(0, Array.IndexOf(values, selected))]);
        }

        private static string DefaultPromptLanguage()
        {
            return SkillsLocalization.Current == SkillsLocalization.Language.Chinese ? "zh-CN" : "en";
        }

        private void OnModeChanged()
        {
            if (!_disposed && _scroll != null) RenderEnvironment();
        }

        // ===== Configuration persistence =====

        private void LoadConfig() => _config = ReadPanelConfig();

        private void SaveConfig() => WritePanelConfig(_config);

        /// <summary>Old panel settings may carry fields this version no longer has; they are ignored.</summary>
        internal static PanelConfig ReadPanelConfig()
        {
            var config = new PanelConfig();
            var json = ProjectPrefs.GetString(ConfigPrefKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return config;
            try
            {
                var value = EvaluationState.ParseObject(json);
                config.Track = Pick(Tracks, value.Value<string>("track"), config.Track);
                config.Tier = Pick(Tiers, value.Value<string>("tier"), config.Tier);
                config.Scope = Pick(Scopes, value.Value<string>("scope"), config.Scope);
                return config;
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidCastException)
            {
                return new PanelConfig();
            }
        }

        internal static void WritePanelConfig(PanelConfig config)
        {
            ProjectPrefs.SetString(ConfigPrefKey, new JObject
            {
                ["track"] = config.Track,
                ["tier"] = config.Tier,
                ["scope"] = config.Scope,
            }.ToString(Formatting.None));
        }

        private static string Pick(string[] allowed, string value, string fallback)
        {
            return value != null && Array.IndexOf(allowed, value) >= 0 ? value : fallback;
        }

        // ===== Commands =====

        private bool PythonReady => PythonLocator.Last != null && PythonLocator.Last.Found;
        private bool CanLaunch => !_disposed && _pending.Count == 0 && PythonReady;

        /// <summary>A session or an All-tasks run is in progress; step 1 is locked meanwhile.</summary>
        /// <summary>An All-tasks run whose sessions are not all reported yet.</summary>
        private bool GroupRunning => _group != null && (_groupProgress == null || !_groupProgress.Complete);

        /// <summary>A finished All-tasks run is the result, until the user opens a history session.</summary>
        private bool GroupResultShown => _group != null && _groupProgress != null && _groupProgress.Complete && !_groupResultHidden;

        private bool Working => _session?.Status == "active" || GroupRunning || IsPending("start");

        private void Launch(string command, List<string> args)
        {
            if (!CanLaunch) return;
            var result = EvaluationRunnerService.Launch(_projectRoot, PythonLocator.Last.Executable, command, args);
            if (result.Started)
            {
                _pending.Add(result.Command);
                _outcomes.Remove(command);
            }
            else
            {
                _outcomes[command] = result.Failure;
            }
            Refresh(false);
            UpdateTracking();
        }

        private void SetScope(string scope)
        {
            _config.Scope = scope;
            SaveConfig();
            Refresh(false);
        }

        /// <summary>Generate: Current tier and Pick tasks run <c>start</c>; All tasks only writes the group record.</summary>
        private void Generate()
        {
            _copiedKey = null;
            _copyFailedPath = null;
            ClearGroupRecord();
            if (_config.Scope == ScopeAll)
            {
                StartGroup();
                return;
            }

            var args = new List<string>
            {
                "--track", _config.Track, "--tier", _config.Tier, "--mode", SessionMode, "--lang", _promptLang,
            };
            if (_config.Scope == ScopePick) args.AddRange(new[] { "--tasks", string.Join(",", SelectedTasks()) });
            Launch("start", args);
        }

        /// <summary>All tasks: no runner command here. The record lets the progress follow the sessions the agent starts.</summary>
        private void StartGroup()
        {
            var record = new EvaluationState.GroupRecord
            {
                StartedUtc = DateTime.UtcNow,
                Lang = _promptLang,
                Combos = EvaluationState.AllCombos(_manifest, _pipeline),
            };
            try
            {
                EvaluationState.WritePanelGroup(_projectRoot, record);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _outcomes["start"] = EvaluationOutcome.Failure("start", null, null,
                    EvaluationProblem.Local("IO_ERROR", EvaluationState.PanelGroupFile(_projectRoot), ex.Message));
                Refresh(false);
                return;
            }
            _group = record;
            _groupProgress = null;
            Refresh(false);
            UpdateTracking();
        }

        /// <summary>Ends the All-tasks run and forgets its result; the sessions themselves stay in the history.</summary>
        private void ClearGroup()
        {
            ClearGroupRecord();
            _copiedKey = null;
            Refresh(true);
            UpdateTracking();
        }

        /// <summary>The record lives until Clear, a new Generate or an abort, so a finished run survives a domain reload.</summary>
        private void ClearGroupRecord()
        {
            EvaluationState.DeletePanelGroup(_projectRoot);
            _group = null;
            _groupProgress = null;
            _groupResultHidden = false;
        }

        /// <summary>Submits for the agent (contract 7.6): its answer file when present, otherwise --no-answer.</summary>
        private void SubmitForAgent()
        {
            if (_session == null || _attempt == null) return;
            var args = new List<string> { "--session", _session.SessionId, "--attempt", _attempt.AttemptId };
            if (_attempt.Phase == "pending_verification")
                args.Add("--retry-verify");
            else if (_attempt.Phase == "ready" && !string.IsNullOrEmpty(_attempt.AnswerPath) && File.Exists(_attempt.AnswerPath))
                args.AddRange(new[] { "--answer", _attempt.AnswerPath });
            else
                args.Add("--no-answer");
            Launch("submit", args);
        }

        private void SaveUsage(string sessionId, string attemptId, int input, int output)
        {
            Launch("usage", new List<string>
            {
                "--session", sessionId, "--attempt", attemptId, "--manual",
                "input=" + Math.Max(0, input).ToString(CultureInfo.InvariantCulture),
                "output=" + Math.Max(0, output).ToString(CultureInfo.InvariantCulture),
            });
        }

        private void DetectPython()
        {
            PythonLocator.LocateAsync(_ =>
            {
                if (!_disposed && _scroll != null) Refresh(false);
            });
            Refresh(false);
        }

        private void WriteMarker()
        {
            try
            {
                EvaluationMarker.Write(_projectRoot, DateTime.UtcNow);
                _markerConfirmOpen = false;
                _markerErrorText = null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _markerErrorText = SkillsLocalization.Get("tab_eval_marker_failed", ex.Message);
            }
            _marker = EvaluationMarker.Read(_projectRoot);
            RenderMarker();
        }

        private void SwitchToBypass()
        {
            _modeNote = ModeNote.None;
            EvaluationModeGuard.Arm(EvaluationModeGuard.Editor, DateTime.UtcNow);
            RenderMode();
        }

        /// <summary>Contract 7.5 check points: creation, activation, a session ending, finished commands, "restore now".</summary>
        private void CheckModeRestore(bool userForced)
        {
            var pending = EvaluationModeGuard.Pending(EvaluationModeGuard.Editor);
            switch (EvaluationModeGuard.Check(EvaluationModeGuard.Editor, _projectRoot, userForced))
            {
                case EvaluationModeGuard.CheckResult.Restored:
                    _modeNote = ModeNote.Restored;
                    _restoredMode = EvaluationModeGuard.ParseMode(pending?.PreviousMode);
                    SkillsLogger.LogVerbose($"Evaluation session over: operating mode restored to {_restoredMode}.");
                    break;
                case EvaluationModeGuard.CheckResult.KeyCleared:
                    _modeNote = ModeNote.KeyCleared;
                    break;
            }
            if (_scroll != null) RenderMode();
        }

        private void CopyPrompt()
        {
            _copiedKey = null;
            _copyFailedPath = null;
            if (GroupRunning)
            {
                var text = GroupPromptText();
                if (text == null) return;
                EditorGUIUtility.systemCopyBuffer = text;
                _copiedKey = GroupCopyKey;
                RenderPrompt();
                return;
            }

            var path = PromptFile(_session);
            if (path == null)
            {
                _copyFailedPath = _session != null
                    ? EvaluationState.SessionPromptFile(_projectRoot, _session.SessionId, PromptLanguageOf(_session))
                    : string.Empty;
            }
            else
            {
                try
                {
                    EditorGUIUtility.systemCopyBuffer = File.ReadAllText(path, Encoding.UTF8);
                    _copiedKey = _session.SessionId;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _copyFailedPath = path;
                }
            }
            RenderPrompt();
        }

        /// <summary>The panel's copy of the session prompt (state dir), else the agent-facing one in the IO dir.</summary>
        private string PromptFile(EvaluationState.SessionInfo session)
        {
            if (session == null || session.Status != "active") return null;
            var statePath = EvaluationState.SessionPromptFile(_projectRoot, session.SessionId, PromptLanguageOf(session));
            if (File.Exists(statePath)) return statePath;
            var ioPath = EvaluationState.IoSessionPromptFile(_projectRoot, session.SessionId);
            return File.Exists(ioPath) ? ioPath : null;
        }

        /// <summary>The All-tasks prompt for the current run, rebuilt from the group record; null without Python.</summary>
        private string GroupPromptText()
        {
            if (_group == null || !PythonReady) return null;
            var runner = EvaluationRunnerService.ResolveRunnerPath(out _);
            if (runner == null) return null;
            var lang = Pick(PromptLanguages, _group.Lang, "en");
            return EvaluationState.BuildGroupPrompt(PythonLocator.Last.Executable, runner, _projectRoot, lang, _group.Combos);
        }

        private static string PromptLanguageOf(EvaluationState.SessionInfo session)
        {
            return Array.IndexOf(PromptLanguages, session.Lang) >= 0 ? session.Lang : "en";
        }

        private static void OpenFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) EditorUtility.OpenWithDefaultApp(path);
        }

        /// <summary>The task ids the current scope runs: the tier's tasks, every tier's tasks, or the ticked ones.</summary>
        private List<string> SelectedTasks()
        {
            var ids = new List<string>();
            if (_config.Scope == ScopeAll)
            {
                foreach (var combo in EvaluationState.AllCombos(_manifest, _pipeline))
                    ids.AddRange(EvaluationState.ManifestTaskIds(_manifest, combo.Track, combo.Tier));
                return ids;
            }
            var tierIds = EvaluationState.ManifestTaskIds(_manifest, _config.Track, _config.Tier);
            if (_config.Scope != ScopePick || _picks == null) return tierIds;
            return tierIds.FindAll(id => _picks.Contains(id));
        }

        // ===== Tracking =====

        private void UpdateTracking()
        {
            bool wanted = !_disposed && (_pending.Count > 0 || GroupRunning) && _root.IsVisible();
            if (!wanted)
            {
                _tick?.Pause();
                return;
            }
            if (_tick == null) _tick = EditorUiScheduler.RepeatSafe(_root, TrackIntervalMs, Tick);
            else _tick.Resume();
        }

        private void Tick()
        {
            if (_disposed) return;
            if (!_root.IsVisible())
            {
                _tick?.Pause();
                return;
            }
            Refresh(PollPending() || GroupRunning);
            if (_pending.Count == 0 && !GroupRunning) _tick?.Pause();
        }

        /// <summary>Consumes every finished pending command; true when at least one finished.</summary>
        private bool PollPending()
        {
            var now = DateTime.UtcNow;
            bool consumed = false;
            for (int i = 0; i < _pending.Count; i++)
            {
                var record = _pending[i];
                var outcome = EvaluationRunnerService.Poll(record, now);
                if (outcome == null) continue;
                _pending.RemoveAt(i--);
                EvaluationRunnerService.Acknowledge(_projectRoot, record, now);
                Consume(outcome);
                consumed = true;
            }
            return consumed;
        }

        private void Consume(EvaluationOutcome outcome)
        {
            _outcomes[outcome.Command] = outcome;
            switch (outcome.Command)
            {
                case "start":
                    if (outcome.Ok) _viewedSessionId = null;
                    break;
                case "report":
                    var sessionId = outcome.Data?.Value<string>("sessionId");
                    if (EvaluationState.IsSessionId(sessionId)) _viewedSessionId = sessionId;
                    break;
                case "usage":
                    if (outcome.Ok) _usageEditorAttempt = null;
                    break;
            }
            CheckModeRestore(false);
        }

        // ===== State =====

        private void Refresh(bool full)
        {
            if (_disposed || _scroll == null) return;
            ReadState();
            if (full)
            {
                _marker = EvaluationMarker.Read(_projectRoot);
                _history = EvaluationState.ListSessions(_projectRoot, HistoryLimit);
            }
            RenderHeader();
            RenderEnvironment();
            RenderConfig();
            RenderPrompt();
            RenderProgress();
            RenderResult();
            if (full) RenderHistory();
        }

        private void ReadState()
        {
            _stateIssue = null;
            _session = null;
            _attempt = null;
            _current = EvaluationState.ReadCurrent(_projectRoot, out var currentRaw);
            NoteIssue(currentRaw);
            if (_current != null)
            {
                _session = EvaluationState.ReadSession(_projectRoot, _current.SessionId, out var sessionRaw);
                NoteIssue(sessionRaw);
                if (_session != null && _session.Status == "active" && _current.ActiveAttemptId != null)
                {
                    _attempt = EvaluationState.ReadAttempt(_projectRoot, _session.SessionId, _current.ActiveAttemptId, out var attemptRaw);
                    NoteIssue(attemptRaw);
                }
            }
            _runnerBusy = EvaluationRunnerService.IsRunnerBusy(_projectRoot, DateTime.UtcNow, out _lock);
            ReadGroup();

            var status = _session?.Status;
            if (_lastSessionStatus == "active" && status != null && status != "active") CheckModeRestore(false);
            _lastSessionStatus = status;

            // The result is shown only once nothing is in progress: a finished group, else the newest finished session.
            _resultSession = null;
            if (!Working && !GroupResultShown)
            {
                var viewed = ViewedSession();
                if (viewed != null && viewed.Status != "active") _resultSession = viewed;
            }
        }

        /// <summary>Maps the All-tasks run onto the sessions. Its record stays after completion, so the result re-renders after a reload.</summary>
        private void ReadGroup()
        {
            if (_group == null) return;
            var sessions = EvaluationState.ListSessions(_projectRoot, GroupSessionScan);
            _groupProgress = EvaluationState.MergeGroup(_group, sessions, ReportOf, _manifest);
        }

        private JObject ReportOf(EvaluationState.SessionInfo session)
        {
            var report = EvaluationState.ReadReport(_projectRoot, session);
            return report.Status == EvaluationState.ReadStatus.Ok ? report.Value : null;
        }

        private void NoteIssue(EvaluationState.ReadResult raw)
        {
            if (_stateIssue != null) return;
            if (raw.Status == EvaluationState.ReadStatus.Corrupt) _stateIssue = EvaluationProblem.Local("STATE_CORRUPT");
            else if (raw.Status == EvaluationState.ReadStatus.Unsupported) _stateIssue = EvaluationProblem.Local("SCHEMA_UNSUPPORTED");
        }

        private EvaluationState.SessionInfo ViewedSession()
        {
            if (_viewedSessionId != null)
            {
                if (_session != null && _session.SessionId == _viewedSessionId) return _session;
                return EvaluationState.ReadSession(_projectRoot, _viewedSessionId, out _);
            }
            if (_session != null && _session.Status != "active") return _session;
            foreach (var session in _history)
            {
                if (session.Status != "active") return session;
            }
            return null;
        }

        private bool IsPending(string command) => _pending.Exists(record => record.Command == command);

        // ===== Rendering =====

        private void ApplyStaticTexts()
        {
            SetText(_title, SkillsLocalization.Get("tab_eval_title"));
            SetText(_checkBtn, SkillsLocalization.Get("tab_eval_recheck_btn"));
            SetText(_introPanelK, SkillsLocalization.Get("tab_eval_intro_panel_k"));
            SetText(_introPanelV, SkillsLocalization.Get("tab_eval_intro_panel_v"));
            SetText(_introAgentK, SkillsLocalization.Get("tab_eval_intro_agent_k"));
            SetText(_introAgentV, SkillsLocalization.Get("tab_eval_intro_agent_v"));
            SetText(_introBoth, SkillsLocalization.Get("tab_eval_intro_both"));

            SetText(_markerLabel, SkillsLocalization.Get("tab_eval_marker_label"));
            SetText(_markerBtn, SkillsLocalization.Get("tab_eval_marker_btn"));
            SetText(_markerConfirmText, SkillsLocalization.Get("tab_eval_marker_confirm_text"));
            SetText(_markerConfirmOk, SkillsLocalization.Get("tab_eval_marker_confirm_ok"));
            SetText(_markerConfirmCancel, SkillsLocalization.Get("tab_eval_cancel"));
            SetText(_pythonLabel, SkillsLocalization.Get("tab_eval_python_label"));
            SetText(_pythonDetectBtn, SkillsLocalization.Get("tab_eval_python_detect_btn"));
            SetText(_pythonOverrideLabel, SkillsLocalization.Get("tab_eval_python_override_label"));
            if (_pythonOverride != null) _pythonOverride.tooltip = SkillsLocalization.Get("tab_eval_python_override_tip");
            SetText(_pythonUseBtn, SkillsLocalization.Get("tab_eval_python_use_btn"));
            SetText(_pythonClearBtn, SkillsLocalization.Get("tab_eval_python_clear_btn"));
            SetText(_modeLabel, SkillsLocalization.Get("tab_eval_mode_label"));
            SetText(_modeSwitchBtn, SkillsLocalization.Get("tab_eval_mode_switch_btn"));
            SetText(_modeRestoreBtn, SkillsLocalization.Get("tab_eval_mode_restore_btn"));
            SetText(_cleanupBtn, SkillsLocalization.Get("tab_eval_cleanup_btn"));

            SetText(_configTitle, SkillsLocalization.Get("tab_eval_step_choose"));
            SetText(_scopeButtons[ScopeTier], SkillsLocalization.Get("tab_eval_scope_tier"));
            SetText(_scopeButtons[ScopeAll], SkillsLocalization.Get("tab_eval_scope_all"));
            SetText(_scopeButtons[ScopePick], SkillsLocalization.Get("tab_eval_scope_pick"));
            SetText(_trackLabel, SkillsLocalization.Get("tab_eval_track_label"));
            SetText(_tierLabel, SkillsLocalization.Get("tab_eval_tier_label"));
            SetText(_langLabel, SkillsLocalization.Get("tab_eval_lang_label"));
            SetText(_generateBtn, SkillsLocalization.Get("tab_eval_generate_btn"));
            SetText(_clearBtn, SkillsLocalization.Get("tab_eval_clear_btn"));

            SetText(_promptTitle, SkillsLocalization.Get("tab_eval_step_hand"));
            SetText(_copyPromptBtn, SkillsLocalization.Get("tab_eval_copy_btn"));
            SetText(_openPromptBtn, SkillsLocalization.Get("tab_eval_open_prompt_btn"));

            SetText(_progressTitle, SkillsLocalization.Get("tab_eval_progress_title"));
            if (_refreshBtn != null) _refreshBtn.tooltip = SkillsLocalization.Get("tab_eval_refresh");
            SetText(_abortBtn, SkillsLocalization.Get("tab_eval_abort_btn"));
            SetText(_submitHint, SkillsLocalization.Get("tab_eval_submit_hint"));
            SetText(_abortConfirmText, SkillsLocalization.Get("tab_eval_abort_confirm_text"));
            SetText(_abortConfirmOk, SkillsLocalization.Get("tab_eval_abort_confirm_ok"));
            SetText(_abortConfirmCancel, SkillsLocalization.Get("tab_eval_cancel"));

            SetText(_resultTitle, SkillsLocalization.Get("tab_eval_step_result"));
            SetText(_reportBtn, SkillsLocalization.Get("tab_eval_report_btn"));
            SetText(_openReportBtn, SkillsLocalization.Get("tab_eval_open_report_btn"));
            SetText(_revealReportBtn, SkillsLocalization.Get("tab_eval_reveal_btn"));
            SetText(_reportUnofficial, SkillsLocalization.Get("tab_eval_report_unofficial"));
            if (_historyFoldout != null) _historyFoldout.text = SkillsLocalization.Get("tab_eval_history_title");
        }

        private void ApplyChoices()
        {
            SetChoices(_track, Tracks, new List<string>
            {
                SkillsLocalization.Get("tab_eval_track_ops"), SkillsLocalization.Get("tab_eval_track_dev"),
            }, _config.Track);
            SetChoices(_tier, Tiers, new List<string>
            {
                SkillsLocalization.Get("tab_eval_tier_basic"), SkillsLocalization.Get("tab_eval_tier_standard"),
                SkillsLocalization.Get("tab_eval_tier_challenge"),
            }, _config.Tier);
            SetChoices(_lang, PromptLanguages, new List<string>
            {
                SkillsLocalization.Get("tab_eval_lang_zh"), SkillsLocalization.Get("tab_eval_lang_en"),
            }, _promptLang);
        }

        private void RenderHeader()
        {
            string text = null;
            if (_pending.Count > 0)
                text = SkillsLocalization.Get("tab_eval_activity_running", CommandLabel(_pending[0].Command));
            else if (_runnerBusy)
                text = SkillsLocalization.Get("tab_eval_activity_busy", _lock?.Command ?? "?",
                    _lock?.Pid?.ToString(CultureInfo.InvariantCulture) ?? "?");
            SetText(_activity, text ?? string.Empty);
            _activity.SetVisible(text != null);

            string state = null;
            if (Working) state = SkillsLocalization.Get("tab_eval_state_running");
            else if (GroupResultShown || _resultSession != null) state = SkillsLocalization.Get("tab_eval_state_done");
            else state = SkillsLocalization.Get("tab_eval_state_none");
            SetText(_state, state);
            _state.EnableInClassList("ev-state--active", Working);
            _state.EnableInClassList("ev-state--done", !Working && (GroupResultShown || _resultSession != null));
            _intro.SetVisible(_session == null && _group == null);
        }

        private void RenderEnvironment()
        {
            RenderMarker();
            RenderPython();
            RenderMode();
            _checkBtn?.SetEnabled(CanLaunch);
            _cleanupBtn?.SetEnabled(CanLaunch && !_runnerBusy);

            string summary = null;
            if (!IsPending("check") && _outcomes.TryGetValue("check", out var check) && check.Data?["instance"] is JObject instance)
            {
                var plugin = instance.Value<string>("pluginVersion") ?? "?";
                var runner = (check.Data["package"] as JObject)?.Value<string>("runnerVersion") ?? "?";
                var pipeline = (check.Data["pipeline"] as JObject)?.Value<string>("type") ?? "?";
                var taskCount = ((check.Data["selection"] as JObject)?["tasks"] as JArray)?.Count ?? 0;
                summary = SkillsLocalization.Get("tab_eval_check_summary", plugin, runner, pipeline, taskCount);
            }
            SetText(_checkSummary, summary ?? string.Empty);
            _checkSummary.SetVisible(summary != null);
            RenderMessages(_envMessages, "check", "cleanup");
            RenderEnvLine();
        }

        /// <summary>The one-line environment state; the detail rows show only when something needs attention.</summary>
        private void RenderEnvLine()
        {
            var pythonFailed = PythonLocator.Last != null && !PythonLocator.Last.Found;
            var modeWrong = SkillsModeManager.CurrentMode != SkillsOperatingMode.Bypass;
            _outcomes.TryGetValue("check", out var check);
            bool checkFailed = check != null && !check.Ok && !IsPending("check");
            bool attention = pythonFailed || modeWrong || checkFailed;
            bool ready = !attention && check != null && check.Ok && !IsPending("check");

            string key = attention ? "tab_eval_env_attention" : ready ? "tab_eval_env_ready" : "tab_eval_env_unchecked";
            SetText(_envLine, SkillsLocalization.Get(key));
            _envLine.EnableInClassList("ev-env--ok", ready);
            _envLine.EnableInClassList("ev-env--warn", attention);

            _envSection.SetVisible(attention || IsPending("cleanup") || _outcomes.ContainsKey("cleanup"));
        }

        private void RenderMarker()
        {
            switch (_marker)
            {
                case EvaluationMarker.MarkerState.Valid:
                    SetText(_markerValue, SkillsLocalization.Get("tab_eval_marker_yes"));
                    SetState(_markerValue, "ok");
                    break;
                case EvaluationMarker.MarkerState.Invalid:
                    SetText(_markerValue, SkillsLocalization.Get("tab_eval_marker_invalid"));
                    SetState(_markerValue, "warn");
                    break;
                default:
                    SetText(_markerValue, SkillsLocalization.Get("tab_eval_marker_no"));
                    SetState(_markerValue, null);
                    break;
            }
            _markerBtn.SetVisible(_marker != EvaluationMarker.MarkerState.Valid && !_markerConfirmOpen);
            _markerConfirm.SetVisible(_markerConfirmOpen);
            SetText(_markerError, _markerErrorText ?? string.Empty);
            _markerError.SetVisible(_markerErrorText != null);
        }

        private void RenderPython()
        {
            var last = PythonLocator.Last;
            string text = string.Empty;
            string state = null;
            if (PythonLocator.IsLocating)
            {
                text = SkillsLocalization.Get("tab_eval_python_detecting");
            }
            else if (last != null && last.Found)
            {
                text = SkillsLocalization.Get("tab_eval_python_found", last.Version, last.Executable);
                state = "ok";
            }
            else if (last != null && last.FromOverride)
            {
                text = SkillsLocalization.Get("tab_eval_python_override_failed", last.Candidate ?? string.Empty, last.Error ?? string.Empty);
                state = "error";
            }
            else if (last != null)
            {
                text = SkillsLocalization.Get("tab_eval_python_missing");
                state = "error";
            }
            SetText(_pythonValue, text);
            SetState(_pythonValue, state);
            _pythonDetectBtn?.SetEnabled(!PythonLocator.IsLocating);
        }

        private void RenderMode()
        {
            var mode = SkillsModeManager.CurrentMode;
            bool bypass = mode == SkillsOperatingMode.Bypass;
            SetText(_modeValue, bypass
                ? SkillsLocalization.Get("tab_eval_mode_ok", ModeName(mode))
                : SkillsLocalization.Get("tab_eval_mode_needs_bypass", ModeName(mode)));
            SetState(_modeValue, bypass ? "ok" : "warn");

            var pending = EvaluationModeGuard.Pending(EvaluationModeGuard.Editor);
            bool active = Working;
            _modeSwitchBtn.SetVisible(!bypass);
            _modeRestoreBtn.SetVisible(pending != null);
            _modeRestoreBtn?.SetEnabled(!active);
            if (_modeRestoreBtn != null)
                _modeRestoreBtn.tooltip = active ? SkillsLocalization.Get("tab_eval_mode_restore_blocked") : string.Empty;

            string note = null;
            if (pending != null)
                note = SkillsLocalization.Get("tab_eval_mode_restore_pending", ModeName(EvaluationModeGuard.ParseMode(pending.PreviousMode)));
            else if (_modeNote == ModeNote.Restored)
                note = SkillsLocalization.Get("tab_eval_mode_restored", ModeName(_restoredMode));
            else if (_modeNote == ModeNote.KeyCleared)
                note = SkillsLocalization.Get("tab_eval_mode_key_cleared");
            SetText(_modeNoteLabel, note ?? string.Empty);
            _modeNoteLabel.SetVisible(note != null);
        }

        /// <summary>Step 1: scope, fields, picks, the Generate and Clear buttons, and the lock while work runs.</summary>
        private void RenderConfig()
        {
            bool locked = Working;
            bool all = _config.Scope == ScopeAll;
            bool pick = _config.Scope == ScopePick;

            foreach (var pair in _scopeButtons)
            {
                pair.Value.EnableInClassList("is-selected", pair.Key == _config.Scope);
                pair.Value.SetEnabled(!locked);
            }
            _trackRow.SetVisible(!all);
            _tierRow.SetVisible(!all);
            _track?.SetEnabled(!locked);
            _tier?.SetEnabled(!locked);
            _lang?.SetEnabled(!locked);
            _allHint.SetVisible(all);
            if (all) RenderAllHint();
            if (pick) RenderPicks();
            _picksBox.SetVisible(pick);
            foreach (var child in _picksBox.Children()) child.SetEnabled(!locked);

            var count = SelectedTasks().Count;
            SetText(_configCount, SkillsLocalization.Get("tab_eval_task_count", count));

            bool canGenerate = !locked && (all ? PythonReady && !_disposed : CanLaunch && !_runnerBusy);
            if ((pick || all) && count == 0) canGenerate = false;
            _generateBtn.SetEnabled(canGenerate);
            _clearBtn.SetVisible(_group != null);
            _clearBtn.SetEnabled(!_disposed);

            SetText(_configLocked, locked ? SkillsLocalization.Get("tab_eval_config_locked") : string.Empty);
            _configLocked.SetVisible(locked);
            RenderMessages(_startMessages, "start");
        }

        /// <summary>The All-tasks scope names what this project's render pipeline runs, so the count shows before Generate.</summary>
        private void RenderAllHint()
        {
            var combos = EvaluationState.AllCombos(_manifest, _pipeline);
            var names = combos.ConvertAll(combo => combo.Track + " " + combo.Tier);
            SetText(_allHint, combos.Count == 0
                ? SkillsLocalization.Get("tab_eval_all_none", _pipeline)
                : SkillsLocalization.Get("tab_eval_all_hint", SelectedTasks().Count, _pipeline, string.Join(", ", names)));
        }

        /// <summary>The ticked tasks of the chosen tier. Every task starts ticked; the list rebuilds when the tier changes.</summary>
        private void RenderPicks()
        {
            var key = _config.Track + "/" + _config.Tier;
            if (_picksKey == key) return;
            _picksKey = key;
            var ids = EvaluationState.ManifestTaskIds(_manifest, _config.Track, _config.Tier);
            _picks = new HashSet<string>(ids, StringComparer.Ordinal);
            _picksBox.Clear();
            foreach (var id in ids)
            {
                var taskId = id;
                var toggle = new Toggle(taskId) { value = true };
                toggle.AddToClassList("ev-pick");
                toggle.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue) _picks.Add(taskId);
                    else _picks.Remove(taskId);
                    RenderConfig();
                });
                _picksBox.Add(toggle);
            }
        }

        /// <summary>Step 2: the All-tasks prompt, or the session prompt copied or opened from its file.</summary>
        private void RenderPrompt()
        {
            bool group = GroupRunning;
            bool session = !group && _session?.Status == "active";
            _prompt.SetVisible(group || session);
            if (!group && !session) return;

            if (group)
            {
                var text = GroupPromptText();
                SetText(_promptHint, SkillsLocalization.Get("tab_eval_prompt_hint_group"));
                SetText(_promptText, text ?? string.Empty);
                _promptScroll.SetVisible(text != null);
                SetText(_promptNote, text == null ? SkillsLocalization.Get("tab_eval_start_needs_python") : string.Empty);
                _promptNote.SetVisible(text == null);
                _copyPromptBtn.SetEnabled(text != null);
                _openPromptBtn.SetVisible(false);
            }
            else
            {
                var hasPrompt = PromptFile(_session) != null;
                SetText(_promptHint, SkillsLocalization.Get("tab_eval_prompt_hint_session"));
                _promptScroll.SetVisible(false);
                string note = null;
                if (_copyFailedPath != null) note = SkillsLocalization.Get("tab_eval_prompt_missing", _copyFailedPath);
                else if (hasPrompt) note = SkillsLocalization.Get("tab_eval_prompt_lang_note", LanguageLabel(PromptLanguageOf(_session)));
                else note = SkillsLocalization.Get("tab_eval_prompt_missing",
                    EvaluationState.SessionPromptFile(_projectRoot, _session.SessionId, PromptLanguageOf(_session)));
                SetText(_promptNote, note);
                _promptNote.SetVisible(true);
                _copyPromptBtn.SetEnabled(hasPrompt);
                _openPromptBtn.SetVisible(true);
                _openPromptBtn.SetEnabled(hasPrompt);
            }

            var copiedNow = _copiedKey != null && _copiedKey == (group ? GroupCopyKey : _session?.SessionId);
            SetText(_copied, copiedNow ? SkillsLocalization.Get("tab_eval_copied") : string.Empty);
            _copied.SetVisible(copiedNow);
        }

        private static string LanguageLabel(string lang)
        {
            return SkillsLocalization.Get(lang == PromptLanguages[0] ? "tab_eval_lang_zh" : "tab_eval_lang_en");
        }

        /// <summary>Step 3: the session's or the run's tasks as chips, one bar, the current task, submit and abort.</summary>
        private void RenderProgress()
        {
            _taskList?.Clear();
            bool group = _group != null && _groupProgress != null;
            bool session = _session != null;
            _progress.SetVisible(session || group);
            if (!session && !group)
            {
                _abortConfirmOpen = false;
                return;
            }

            bool active = _session?.Status == "active";
            List<EvaluationState.GroupTask> rows;
            if (group)
            {
                rows = _groupProgress.Tasks;
                SetText(_sessionLine, SkillsLocalization.Get("tab_eval_group_line",
                    _groupProgress.FinishedSessions, _groupProgress.Combos.Count));
            }
            else
            {
                rows = SessionRows(_session);
                SetText(_sessionLine, SkillsLocalization.Get("tab_eval_session_line", _session.SessionId,
                    SessionStatusLabel(_session.Status), CountFinished(rows), rows.Count, RunModeShort(_session.Mode),
                    _session.Track ?? "?", _session.Tier ?? "?"));
            }

            int finished = CountFinished(rows);
            SetText(_progressCount, SkillsLocalization.Get("tab_eval_progress_count", finished, rows.Count));
            _progressFill.style.width = new Length(rows.Count > 0 ? (float)(100.0 * finished / rows.Count) : 0f, LengthUnit.Percent);
            RenderTaskChips(rows, group);

            string taskText = null;
            if (active && _attempt != null)
            {
                var issued = EvaluationState.ParseIso(_attempt.IssuedAt);
                var elapsed = issued.HasValue ? Math.Max(0, (int)(DateTime.UtcNow - issued.Value).TotalMinutes) : 0;
                taskText = SkillsLocalization.Get("tab_eval_task_line", _attempt.TaskId ?? "?", _attempt.K, rows.Count,
                    PhaseLabel(_attempt.Phase), elapsed, _attempt.Minutes?.ToString(CultureInfo.InvariantCulture) ?? "?");
            }
            else if (active)
            {
                taskText = SkillsLocalization.Get("tab_eval_waiting_next");
            }
            SetText(_taskLine, taskText ?? string.Empty);
            _taskLine.SetVisible(taskText != null);

            var phase = active ? _attempt?.Phase : null;
            bool submittable = phase == "ready" || phase == "submitted" || phase == "verifying" || phase == "pending_verification";
            SetText(_submitBtn, phase == "pending_verification"
                ? SkillsLocalization.Get("tab_eval_retry_verify_btn")
                : SkillsLocalization.Get("tab_eval_submit_btn"));
            _submitBtn?.SetEnabled(submittable && CanLaunch && !_runnerBusy);
            _abortBtn?.SetEnabled(active && CanLaunch && !_runnerBusy);
            if (!active) _abortConfirmOpen = false;
            _abortConfirm.SetVisible(_abortConfirmOpen);

            RenderMessages(_progressMessages, "submit", "abort");
            if (_stateIssue != null && _progressMessages != null)
                _progressMessages.Insert(0, MessageLabel(LocalizeProblem(_stateIssue), "eval-message--problem"));
        }

        private static List<EvaluationState.GroupTask> SessionRows(EvaluationState.SessionInfo session)
        {
            var rows = new List<EvaluationState.GroupTask>();
            foreach (var taskId in session.Order)
            {
                var state = session.Tasks.TryGetValue(taskId, out var entry) ? entry.State : null;
                rows.Add(new EvaluationState.GroupTask { Track = session.Track, Tier = session.Tier, TaskId = taskId, State = state });
            }
            return rows;
        }

        private static int CountFinished(List<EvaluationState.GroupTask> rows)
        {
            int count = 0;
            foreach (var row in rows)
            {
                if (row.Status != null || row.State == "done" || row.State == "invalid" || row.State == "cancelled") count++;
            }
            return count;
        }

        /// <summary>One chip per task; an All-tasks run groups them under a heading per track and tier.</summary>
        private void RenderTaskChips(List<EvaluationState.GroupTask> rows, bool byCombo)
        {
            VisualElement line = null;
            string current = null;
            foreach (var row in rows)
            {
                var combo = row.Track + "/" + row.Tier;
                if (line == null || (byCombo && combo != current))
                {
                    current = combo;
                    if (byCombo)
                    {
                        var heading = new Label(SkillsLocalization.Get("tab_eval_combo_label", row.Track ?? "?", row.Tier ?? "?"));
                        heading.AddToClassList("ev-combo");
                        _taskList.Add(heading);
                    }
                    line = new VisualElement();
                    line.AddToClassList("ev-chips");
                    _taskList.Add(line);
                }

                var chip = new Label(row.TaskId);
                chip.AddToClassList("ev-chip");
                var chipClass = ChipClass(row);
                if (chipClass != null) chip.AddToClassList(chipClass);
                chip.tooltip = row.Status != null ? ResultLabel(row.Status) : TaskStateLabel(row.State);
                line.Add(chip);
            }
        }

        private static string ChipClass(EvaluationState.GroupTask row)
        {
            switch (row.Status)
            {
                case "pass": return "ev-chip--pass";
                case "partial": return "ev-chip--partial";
                case "fail": return "ev-chip--fail";
            }
            switch (row.State)
            {
                case "done": return ChipDone;
                case "issued": return "ev-chip--current";
                default: return null;
            }
        }

        /// <summary>Step 4: the finished group's or the viewed session's result, with its per-task rows.</summary>
        private void RenderResult()
        {
            _reportTasks?.Clear();
            _resultTiers?.Clear();
            _reportUnofficial.SetVisible(false);
            _resultVerdict.SetVisible(false);
            _resultModels.SetVisible(false);

            bool shown = false;
            if (GroupResultShown)
            {
                RenderGroupResult(_groupProgress);
                shown = true;
            }
            else if (_resultSession != null)
            {
                RenderSessionResult(_resultSession);
                shown = true;
            }

            _result.SetVisible(shown);
            RenderMessages(_reportMessages, "report", "usage");
        }

        private void RenderSessionResult(EvaluationState.SessionInfo viewed)
        {
            SetText(_resultSessionLabel, SkillsLocalization.Get("tab_eval_report_session", viewed.SessionId,
                SessionStatusLabel(viewed.Status), FormatLocal(viewed.CreatedAt, "yyyy-MM-dd HH:mm")));
            var report = EvaluationState.ReadReport(_projectRoot, viewed);
            var markdown = EvaluationState.ReportMarkdownPath(_projectRoot, viewed);
            if (report.Status != EvaluationState.ReadStatus.Ok)
            {
                string text = report.Status == EvaluationState.ReadStatus.Missing
                    ? SkillsLocalization.Get("tab_eval_report_missing")
                    : LocalizeProblem(EvaluationProblem.Local(report.Status == EvaluationState.ReadStatus.Unsupported
                        ? "SCHEMA_UNSUPPORTED"
                        : "STATE_CORRUPT"));
                SetResultNumbers(string.Empty, text);
                SetGrade(null);
                SetFill(_resultFill, 0);
                SetText(_resultMeta, string.Empty);
                _resultMeta.SetVisible(false);
                _reportBtn.SetVisible(report.Status == EvaluationState.ReadStatus.Missing && viewed.Status != "active");
                _reportBtn.SetEnabled(CanLaunch && !_runnerBusy);
                _openReportBtn.SetVisible(false);
                _revealReportBtn.SetVisible(false);
                return;
            }

            var totals = report.Value["summary"] as JObject;
            int tasks = CountOf(totals, "tasks");
            int pass = CountOf(totals, "pass");
            int partial = CountOf(totals, "partial");
            int fail = CountOf(totals, "fail");
            var rate = pass + partial + fail > 0 ? (double)pass / (pass + partial + fail) : 0;
            var rows = new List<JObject>();
            if (report.Value["tasks"] is JArray taskRows)
            {
                foreach (var row in taskRows)
                {
                    if (row is JObject task)
                    {
                        rows.Add(task);
                        AddReportTask(task, viewed.SessionId);
                    }
                }
            }

            SetResultNumbers(pass.ToString(CultureInfo.InvariantCulture),
                SkillsLocalization.Get("tab_eval_result_den", tasks));
            SetGrade(report.Value.Value<string>("grade") ?? EvaluationState.GradeFor(pass, partial, fail));
            SetFill(_resultFill, rate);
            ShowMeta(rate, Number(totals?["meanCompletion"], "0"));

            var models = EvaluationState.ModelNames(rows);
            _resultModels.SetVisible(models.Count > 0);
            if (models.Count > 0) SetText(_resultModels, SkillsLocalization.Get("tab_eval_result_models", string.Join(", ", models)));

            var verdict = report.Value.Value<string>("verdict");
            _resultVerdict.SetVisible(!string.IsNullOrEmpty(verdict));
            if (!string.IsNullOrEmpty(verdict)) SetText(_resultVerdict, verdict);

            _reportUnofficial.SetVisible(report.Value["official"]?.Type == JTokenType.Boolean && !report.Value.Value<bool>("official"));
            _reportBtn.SetVisible(false);
            bool hasMarkdown = markdown != null && File.Exists(markdown);
            _openReportBtn.SetVisible(hasMarkdown);
            _revealReportBtn.SetVisible(hasMarkdown);
        }

        /// <summary>The All-tasks result: counts over all six sessions' tasks, the grade, and each tier's runner verdict.</summary>
        private void RenderGroupResult(EvaluationState.GroupProgress progress)
        {
            var summary = EvaluationState.SummarizeGroup(progress);
            SetText(_resultSessionLabel, SkillsLocalization.Get("tab_eval_result_group", progress.Combos.Count, summary.Tasks));
            SetResultNumbers(summary.Pass.ToString(CultureInfo.InvariantCulture),
                SkillsLocalization.Get("tab_eval_result_den", summary.Tasks));
            SetGrade(summary.Grade);
            var gradable = summary.Pass + summary.Partial + summary.Fail;
            SetFill(_resultFill, gradable > 0 ? (double)summary.Pass / gradable : 0);
            ShowMeta(gradable > 0 ? (double)summary.Pass / gradable : 0,
                summary.MeanCompletion?.ToString("0", CultureInfo.InvariantCulture) ?? "-");

            _resultModels.SetVisible(summary.Models.Count > 0);
            if (summary.Models.Count > 0) SetText(_resultModels, SkillsLocalization.Get("tab_eval_result_models", string.Join(", ", summary.Models)));
            _resultVerdict.SetVisible(false);
            _reportUnofficial.SetVisible(false);

            foreach (var combo in progress.Combos)
            {
                var verdict = combo.Report?.Value<string>("verdict");
                if (string.IsNullOrEmpty(verdict)) continue;
                var line = new Label(SkillsLocalization.Get("tab_eval_combo_verdict",
                    SkillsLocalization.Get("tab_eval_combo_label", combo.Combo.Track, combo.Combo.Tier), verdict));
                line.AddToClassList("ev-verdict");
                _resultTiers.Add(line);
            }

            foreach (var task in progress.Tasks)
            {
                var completion = task.Completion?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
                var line = SkillsLocalization.Get("tab_eval_report_task", ResultLabel(task.Status), completion);
                AddListRow(_reportTasks, task.TaskId, line);
            }
            _reportBtn.SetVisible(false);
            _openReportBtn.SetVisible(false);
            _revealReportBtn.SetVisible(false);
        }

        private void SetResultNumbers(string score, string den)
        {
            SetText(_resultScore, score);
            _resultScore.SetVisible(!string.IsNullOrEmpty(score));
            SetText(_resultDen, den);
        }

        private void ShowMeta(double rate, string meanCompletion)
        {
            _resultMeta.SetVisible(true);
            SetText(_resultMeta, SkillsLocalization.Get("tab_eval_result_meta",
                rate.ToString("0%", CultureInfo.InvariantCulture), meanCompletion));
        }

        private void SetGrade(string grade)
        {
            var text = GradeLabel(grade);
            SetText(_resultGrade, text == null ? string.Empty : SkillsLocalization.Get("tab_eval_result_overall", text));
            _resultGrade.SetVisible(text != null);
            _resultGrade.EnableInClassList("ev-grade--excellent", grade == "excellent");
            _resultGrade.EnableInClassList("ev-grade--good", grade == "good");
            _resultGrade.EnableInClassList("ev-grade--pass", grade == "pass");
            _resultGrade.EnableInClassList("ev-grade--needs-work", grade == "needs_work");
        }

        private static string GradeLabel(string grade)
        {
            switch (grade)
            {
                case "excellent": return SkillsLocalization.Get("tab_eval_grade_excellent");
                case "good": return SkillsLocalization.Get("tab_eval_grade_good");
                case "pass": return SkillsLocalization.Get("tab_eval_grade_pass");
                case "needs_work": return SkillsLocalization.Get("tab_eval_grade_needs_work");
                default: return null;
            }
        }

        private static void SetFill(VisualElement fill, double fraction)
        {
            fill.style.width = new Length((float)(Math.Max(0, Math.Min(1, fraction)) * 100), LengthUnit.Percent);
        }

        private static int CountOf(JObject totals, string key)
        {
            var token = totals?[key];
            return token != null && token.Type == JTokenType.Integer ? token.Value<int>() : 0;
        }

        private void AddReportTask(JObject task, string sessionId)
        {
            var taskId = task.Value<string>("taskId") ?? "?";
            var attemptId = task.Value<string>("attemptId");
            var status = task.Value<string>("status");
            var completion = Number(task["completion"], "0.0");
            var line = SkillsLocalization.Get("tab_eval_report_task", ResultLabel(status), completion);
            var row = AddListRow(_reportTasks, taskId, line);

            var failed = task["requirementsFailed"] as JArray;
            if (failed != null && failed.Count > 0)
            {
                var ids = new List<string>();
                foreach (var id in failed) ids.Add(id.ToString());
                _reportTasks.Add(SubLine(SkillsLocalization.Get("tab_eval_report_failed_reqs", string.Join(", ", ids))));
            }

            var tokens = (task["usage"] as JObject)?["tokens"] as JObject;
            if (tokens != null)
            {
                var processed = Number(tokens["processed"], "N0");
                _reportTasks.Add(SubLine(SkillsLocalization.Get("tab_eval_report_tokens", processed)));
                return;
            }
            if (!EvaluationState.IsAttemptId(attemptId) || Array.IndexOf(ModelOutcomes, status) < 0) return;

            if (_usageEditorAttempt != attemptId)
            {
                var enter = new Button(() =>
                {
                    _usageEditorAttempt = attemptId;
                    RenderResult();
                }) { text = SkillsLocalization.Get("tab_eval_usage_btn") };
                enter.AddToClassList("mini-btn");
                enter.SetEnabled(CanLaunch && !_runnerBusy);
                row.Add(enter);
                return;
            }

            var editor = new VisualElement();
            editor.AddToClassList("eval-usage-editor");
            var input = UsageField(editor, SkillsLocalization.Get("tab_eval_usage_input"));
            var output = UsageField(editor, SkillsLocalization.Get("tab_eval_usage_output"));
            var save = new Button(() => SaveUsage(sessionId, attemptId, input.value, output.value))
            {
                text = SkillsLocalization.Get("tab_eval_usage_save"),
            };
            save.AddToClassList("mini-btn");
            save.AddToClassList("eval-btn-primary");
            save.SetEnabled(CanLaunch && !_runnerBusy);
            var cancel = new Button(() =>
            {
                _usageEditorAttempt = null;
                RenderResult();
            }) { text = SkillsLocalization.Get("tab_eval_cancel") };
            cancel.AddToClassList("mini-btn");
            editor.Add(save);
            editor.Add(cancel);
            _reportTasks.Add(editor);
        }

        private static IntegerField UsageField(VisualElement editor, string label)
        {
            var caption = new Label(label);
            caption.AddToClassList("eval-usage-editor__label");
            editor.Add(caption);
            var field = new IntegerField();
            field.AddToClassList("eval-usage-editor__field");
            editor.Add(field);
            return field;
        }

        private void RenderHistory()
        {
            if (_historyList == null) return;
            _historyList.Clear();
            if (_history.Count == 0)
            {
                _historyList.Add(SubLine(SkillsLocalization.Get("tab_eval_history_empty")));
                return;
            }
            var viewedId = _resultSession?.SessionId;
            foreach (var session in _history)
            {
                var row = AddListRow(_historyList, session.SessionId, SkillsLocalization.Get("tab_eval_history_row",
                    FormatLocal(session.CreatedAt, "yyyy-MM-dd HH:mm"), SessionStatusLabel(session.Status),
                    session.Track ?? "?", session.Tier ?? "?"));
                row.AddToClassList("eval-list-row--history");
                var id = session.SessionId;
                var view = new Button(() =>
                {
                    _viewedSessionId = id;
                    _groupResultHidden = true;
                    Refresh(false);
                }) { text = SkillsLocalization.Get("tab_eval_view_btn") };
                view.AddToClassList("mini-btn");
                view.SetEnabled(id != viewedId);
                row.Add(view);
            }
        }

        private void RenderMessages(VisualElement container, params string[] commands)
        {
            if (container == null) return;
            container.Clear();
            foreach (var command in commands)
            {
                if (IsPending(command))
                {
                    container.Add(MessageLabel(SkillsLocalization.Get("tab_eval_activity_running", CommandLabel(command)), "eval-message-head"));
                    continue;
                }
                if (_outcomes.TryGetValue(command, out var outcome)) AddOutcome(container, outcome);
            }
        }

        private void AddOutcome(VisualElement container, EvaluationOutcome outcome)
        {
            var head = new VisualElement();
            head.AddToClassList("eval-message-row");
            var title = new Label(SkillsLocalization.Get("tab_eval_finished_fmt", CommandLabel(outcome.Command),
                FormatLocal(outcome.FinishedAt, "HH:mm:ss")));
            title.AddToClassList("eval-message-head");
            head.Add(title);
            if (outcome.Problems.Count > 0 && !string.IsNullOrEmpty(outcome.LogFile) && File.Exists(outcome.LogFile))
            {
                var log = new Button(() => OpenFile(outcome.LogFile)) { text = SkillsLocalization.Get("tab_eval_open_log") };
                log.AddToClassList("mini-btn");
                head.Add(log);
            }
            container.Add(head);

            var summary = OutcomeSummary(outcome, out var summaryClass);
            if (summary != null) container.Add(MessageLabel(summary, summaryClass));
            foreach (var problem in outcome.Problems)
                container.Add(MessageLabel(LocalizeProblem(problem), "eval-message--problem"));
            foreach (var notice in outcome.Notices)
                container.Add(MessageLabel(LocalizeProblem(notice), "eval-message--notice"));
        }

        private static string OutcomeSummary(EvaluationOutcome outcome, out string cssClass)
        {
            cssClass = "eval-message--ok";
            var data = outcome.Data;
            // No data: the runner never answered (launch failure, crash); its problem says why.
            if (data == null && !outcome.Ok) return null;
            switch (outcome.Command)
            {
                case "check":
                    if (outcome.Ok) return SkillsLocalization.Get("tab_eval_check_ready");
                    cssClass = "eval-message--problem";
                    return SkillsLocalization.Get("tab_eval_check_blocked", outcome.Problems.Count);
                case "start":
                    if (!outcome.Ok || data == null) return null;
                    var sessionId = data.Value<string>("sessionId") ?? "?";
                    var taskCount = data.Value<int?>("n") ?? 0;
                    return data.Value<bool?>("created") == true
                        ? SkillsLocalization.Get("tab_eval_session_started", sessionId, taskCount)
                        : SkillsLocalization.Get("tab_eval_session_resumed", sessionId);
                case "submit":
                    if (!outcome.Ok || data == null) return null;
                    var remaining = data.Value<int?>("remaining") ?? 0;
                    return data.Value<bool?>("done") == true
                        ? SkillsLocalization.Get("tab_eval_session_done")
                        : SkillsLocalization.Get("tab_eval_submitted", remaining);
                case "abort":
                    return outcome.Ok ? SkillsLocalization.Get("tab_eval_aborted") : null;
                case "report":
                    return outcome.Ok ? SkillsLocalization.Get("tab_eval_report_built") : null;
                case "usage":
                    return outcome.Ok ? SkillsLocalization.Get("tab_eval_usage_saved", data?.Value<string>("attemptId") ?? "?") : null;
                case "cleanup":
                    if (!outcome.Ok || data == null) return null;
                    var removed = (data["removed"] as JArray)?.Count ?? 0;
                    var skipped = (data["skipped"] as JArray)?.Count ?? 0;
                    return SkillsLocalization.Get("tab_eval_cleanup_done", removed, skipped);
                default:
                    return null;
            }
        }

        // ===== Labels =====

        private static string CommandLabel(string command)
        {
            switch (command)
            {
                case "check": return SkillsLocalization.Get("tab_eval_cmd_check");
                case "start": return SkillsLocalization.Get("tab_eval_cmd_start");
                case "submit": return SkillsLocalization.Get("tab_eval_cmd_submit");
                case "abort": return SkillsLocalization.Get("tab_eval_cmd_abort");
                case "report": return SkillsLocalization.Get("tab_eval_cmd_report");
                case "usage": return SkillsLocalization.Get("tab_eval_cmd_usage");
                case "cleanup": return SkillsLocalization.Get("tab_eval_cmd_cleanup");
                default: return command ?? "?";
            }
        }

        private static string ModeName(SkillsOperatingMode mode)
        {
            switch (mode)
            {
                case SkillsOperatingMode.Approval: return SkillsLocalization.Get("perm_mode_approval_short");
                case SkillsOperatingMode.Auto: return SkillsLocalization.Get("perm_mode_auto_short");
                default: return SkillsLocalization.Get("perm_mode_bypass_short");
            }
        }

        private static string SessionStatusLabel(string status)
        {
            switch (status)
            {
                case "active": return SkillsLocalization.Get("tab_eval_status_active");
                case "completed": return SkillsLocalization.Get("tab_eval_status_completed");
                case "aborted": return SkillsLocalization.Get("tab_eval_status_aborted");
                default: return status ?? "?";
            }
        }

        private static string RunModeShort(string mode)
        {
            switch (mode)
            {
                case "suite": return SkillsLocalization.Get("tab_eval_run_mode_suite_short");
                case "strict": return SkillsLocalization.Get("tab_eval_run_mode_strict_short");
                default: return mode ?? "?";
            }
        }

        private static string PhaseLabel(string phase)
        {
            switch (phase)
            {
                case "building": return SkillsLocalization.Get("tab_eval_phase_building");
                case "ready": return SkillsLocalization.Get("tab_eval_phase_ready");
                case "submitted": return SkillsLocalization.Get("tab_eval_phase_submitted");
                case "verifying": return SkillsLocalization.Get("tab_eval_phase_verifying");
                case "pending_verification": return SkillsLocalization.Get("tab_eval_phase_pending_verification");
                case "scored": return SkillsLocalization.Get("tab_eval_phase_scored");
                case "torn_down": return SkillsLocalization.Get("tab_eval_phase_torn_down");
                default: return phase ?? "?";
            }
        }

        private static string TaskStateLabel(string state)
        {
            switch (state)
            {
                case "pending": return SkillsLocalization.Get("tab_eval_task_pending");
                case "issued": return SkillsLocalization.Get("tab_eval_task_issued");
                case "done": return SkillsLocalization.Get("tab_eval_task_done");
                case "invalid": return SkillsLocalization.Get("tab_eval_task_invalid");
                case "cancelled": return SkillsLocalization.Get("tab_eval_task_cancelled");
                default: return state ?? "?";
            }
        }

        private static string ResultLabel(string status)
        {
            switch (status)
            {
                case "pass": return SkillsLocalization.Get("tab_eval_result_pass");
                case "partial": return SkillsLocalization.Get("tab_eval_result_partial");
                case "fail": return SkillsLocalization.Get("tab_eval_result_fail");
                case "invalid": return SkillsLocalization.Get("tab_eval_result_invalid");
                case "contaminated": return SkillsLocalization.Get("tab_eval_result_contaminated");
                case "cancelled": return SkillsLocalization.Get("tab_eval_result_cancelled");
                default: return SkillsLocalization.Get("tab_eval_result_none");
            }
        }

        // ===== Small UI helpers =====

        private static void SetText(TextElement element, string text)
        {
            if (element != null && element.text != text) element.text = text;
        }

        private static void SetState(VisualElement element, string state)
        {
            if (element == null) return;
            element.EnableInClassList("eval-state--ok", state == "ok");
            element.EnableInClassList("eval-state--warn", state == "warn");
            element.EnableInClassList("eval-state--error", state == "error");
        }

        private static Label MessageLabel(string text, string modifier)
        {
            var label = new Label(text);
            label.AddToClassList(modifier == "eval-message-head" ? "eval-message-head" : "eval-message");
            if (modifier != "eval-message-head") label.AddToClassList(modifier);
            return label;
        }

        private static Label SubLine(string text)
        {
            var label = new Label(text);
            label.AddToClassList("eval-list-subline");
            return label;
        }

        private static VisualElement AddListRow(VisualElement container, string id, string text)
        {
            var row = new VisualElement();
            row.AddToClassList("eval-list-row");
            var idLabel = new Label(id);
            idLabel.AddToClassList("eval-list-row__id");
            row.Add(idLabel);
            var textLabel = new Label(text);
            textLabel.AddToClassList("eval-list-row__text");
            row.Add(textLabel);
            container?.Add(row);
            return row;
        }

        private static string Number(JToken token, string format)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return "-";
            return token.Value<double>().ToString(format, CultureInfo.InvariantCulture);
        }

        private static string FormatLocal(string iso, string format)
        {
            var parsed = EvaluationState.ParseIso(iso);
            return parsed.HasValue ? parsed.Value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture) : "-";
        }
    }
}

// Producer:Betsy
