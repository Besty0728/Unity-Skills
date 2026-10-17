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
    /// The Evaluation tab (contract 7.6). It prepares the environment, launches runner commands through
    /// <see cref="EvaluationRunnerService"/> and shows their outcomes; session progress is read straight from the
    /// runner's state files and never includes a score, which only appears from report.json once a session ended.
    /// Prompt bodies are copied or opened, never rendered: the bundled CJK atlas cannot guarantee arbitrary text.
    /// Pending commands are followed by one <see cref="EditorUiScheduler.RepeatSafe"/> tick that runs only while
    /// the tab is visible and a command is pending; everything else refreshes on activation, a button or a result.
    /// </summary>
    internal sealed class EvaluationTabController
    {
        private const string TabUxmlPath = "Packages/com.besty.unity-skills/Editor/UI/Tabs/EvaluationTab.uxml";
        private const string ConfigPrefKey = "UnitySkills_EvalPanelConfig";
        internal const string ProblemKeyPrefix = "tab_eval_problem_";
        private const int TrackIntervalMs = 1000;
        private const int HistoryLimit = 10;

        private static readonly string[] Tracks = { "ops", "dev" };
        private static readonly string[] Tiers = { "basic", "standard", "challenge" };
        private static readonly string[] RunModes = { "suite", "strict" };
        private static readonly string[] PromptLanguages = { "zh-CN", "en" };
        private static readonly string[] Hosts = { "", "claude-code", "codex", "cursor", "other" };
        private static readonly string[] ModelOutcomes = { "pass", "partial", "fail" };

        /// <summary>The remembered run configuration: a per-project setting (ProjectPrefs), prompt language excepted.</summary>
        internal sealed class PanelConfig
        {
            public string Track = "ops";
            public string Tier = "basic";
            public string RunMode = "suite";
            public string Host = "";
            public string Model = "";
            public string Effort = "";
            public bool UsageReading = true;
        }

        private enum ModeNote { None, Restored, KeyCleared }

        private readonly VisualElement _root;
        private readonly string _projectRoot;
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

        private bool _markerConfirmOpen;
        private string _markerErrorText;
        private bool _abortConfirmOpen;
        private string _viewedSessionId;
        private string _copiedSessionId;
        private string _copyFailedPath;
        private string _usageEditorAttempt;
        private ModeNote _modeNote;
        private SkillsOperatingMode _restoredMode;

        private ScrollView _scroll;
        private Label _title, _activity, _intro;
        private Label _envTitle, _markerLabel, _markerValue, _markerConfirmText, _markerError;
        private Button _markerBtn, _markerConfirmOk, _markerConfirmCancel;
        private VisualElement _markerConfirm;
        private Label _pythonLabel, _pythonValue, _pythonOverrideLabel;
        private TextField _pythonOverride;
        private Button _pythonDetectBtn, _pythonUseBtn, _pythonClearBtn;
        private Label _modeLabel, _modeValue, _modeNoteLabel;
        private Button _modeSwitchBtn, _modeRestoreBtn;
        private Button _checkBtn, _cleanupBtn;
        private Label _checkSummary;
        private VisualElement _envMessages;
        private Label _configTitle, _trackLabel, _tierLabel, _runModeLabel, _langLabel, _hostLabel, _modelLabel, _effortLabel;
        private Label _declaredHint, _usageLabel, _usageHint;
        private DropdownField _track, _tier, _runMode, _lang, _host;
        private TextField _model, _effort;
        private Toggle _usageToggle;
        private Label _startTitle, _startHint, _copied;
        private Button _startBtn, _copyPromptBtn, _openPromptBtn;
        private VisualElement _startMessages;
        private Label _progressTitle, _sessionLine, _taskLine, _submitHint, _abortConfirmText;
        private Button _refreshBtn, _submitBtn, _abortBtn, _abortConfirmOk, _abortConfirmCancel;
        private VisualElement _taskList, _abortConfirm, _progressMessages;
        private Label _reportTitle, _reportSession, _reportSummary, _reportMetrics, _reportUnofficial, _historyTitle;
        private Button _reportBtn, _openReportBtn, _revealReportBtn;
        private VisualElement _reportTasks, _reportMessages, _historyList;

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
            _activity = _root.Q<Label>("eval-activity");
            _intro = _root.Q<Label>("eval-intro");

            _envTitle = _root.Q<Label>("eval-env-title");
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
            _checkBtn = _root.Q<Button>("eval-check-btn");
            _cleanupBtn = _root.Q<Button>("eval-cleanup-btn");
            _checkSummary = _root.Q<Label>("eval-check-summary");
            _envMessages = _root.Q<VisualElement>("eval-env-messages");

            _configTitle = _root.Q<Label>("eval-config-title");
            _trackLabel = _root.Q<Label>("eval-track-label");
            _tierLabel = _root.Q<Label>("eval-tier-label");
            _runModeLabel = _root.Q<Label>("eval-run-mode-label");
            _langLabel = _root.Q<Label>("eval-lang-label");
            _hostLabel = _root.Q<Label>("eval-host-label");
            _modelLabel = _root.Q<Label>("eval-model-label");
            _effortLabel = _root.Q<Label>("eval-effort-label");
            _track = _root.Q<DropdownField>("eval-track");
            _tier = _root.Q<DropdownField>("eval-tier");
            _runMode = _root.Q<DropdownField>("eval-run-mode");
            _lang = _root.Q<DropdownField>("eval-lang");
            _host = _root.Q<DropdownField>("eval-host");
            _model = _root.Q<TextField>("eval-model");
            _effort = _root.Q<TextField>("eval-effort");
            _declaredHint = _root.Q<Label>("eval-declared-hint");
            _usageLabel = _root.Q<Label>("eval-usage-label");
            _usageToggle = _root.Q<Toggle>("eval-usage-toggle");
            _usageHint = _root.Q<Label>("eval-usage-hint");

            _startTitle = _root.Q<Label>("eval-start-title");
            _startHint = _root.Q<Label>("eval-start-hint");
            _startBtn = _root.Q<Button>("eval-start-btn");
            _copyPromptBtn = _root.Q<Button>("eval-copy-prompt-btn");
            _openPromptBtn = _root.Q<Button>("eval-open-prompt-btn");
            _copied = _root.Q<Label>("eval-copied");
            _startMessages = _root.Q<VisualElement>("eval-start-messages");

            _progressTitle = _root.Q<Label>("eval-progress-title");
            _refreshBtn = _root.Q<Button>("eval-refresh-btn");
            _sessionLine = _root.Q<Label>("eval-session-line");
            _taskLine = _root.Q<Label>("eval-task-line");
            _taskList = _root.Q<VisualElement>("eval-task-list");
            _submitBtn = _root.Q<Button>("eval-submit-btn");
            _abortBtn = _root.Q<Button>("eval-abort-btn");
            _submitHint = _root.Q<Label>("eval-submit-hint");
            _abortConfirm = _root.Q<VisualElement>("eval-abort-confirm");
            _abortConfirmText = _root.Q<Label>("eval-abort-confirm-text");
            _abortConfirmOk = _root.Q<Button>("eval-abort-confirm-ok");
            _abortConfirmCancel = _root.Q<Button>("eval-abort-confirm-cancel");
            _progressMessages = _root.Q<VisualElement>("eval-progress-messages");

            _reportTitle = _root.Q<Label>("eval-report-title");
            _reportSession = _root.Q<Label>("eval-report-session");
            _reportSummary = _root.Q<Label>("eval-report-summary");
            _reportMetrics = _root.Q<Label>("eval-report-metrics");
            _reportUnofficial = _root.Q<Label>("eval-report-unofficial");
            _reportTasks = _root.Q<VisualElement>("eval-report-tasks");
            _reportBtn = _root.Q<Button>("eval-report-btn");
            _openReportBtn = _root.Q<Button>("eval-open-report-btn");
            _revealReportBtn = _root.Q<Button>("eval-reveal-report-btn");
            _reportMessages = _root.Q<VisualElement>("eval-report-messages");
            _historyTitle = _root.Q<Label>("eval-history-title");
            _historyList = _root.Q<VisualElement>("eval-history-list");

            UISkillsEditorIcons.Apply(_refreshBtn, "d_Refresh", "Refresh", "TreeEditor.Refresh");
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
            OnChoice(_runMode, RunModes, value => _config.RunMode = value);
            OnChoice(_host, Hosts, value => _config.Host = value);
            _lang?.RegisterValueChangedCallback(evt =>
            {
                var value = ValueOf(_lang, PromptLanguages, evt.newValue);
                if (value == null) return;
                _promptLang = value;
                _promptLangChosen = true;
            });
            _model?.RegisterValueChangedCallback(evt =>
            {
                _config.Model = evt.newValue ?? string.Empty;
                SaveConfig();
            });
            _effort?.RegisterValueChangedCallback(evt =>
            {
                _config.Effort = evt.newValue ?? string.Empty;
                SaveConfig();
            });
            _usageToggle?.RegisterValueChangedCallback(evt =>
            {
                _config.UsageReading = evt.newValue;
                SaveConfig();
            });

            OnClick(_startBtn, StartSession);
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
                if (_session != null)
                    Launch("abort", new List<string> { "--session", _session.SessionId, "--reason", "user" });
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
            if (!_disposed && _scroll != null) RenderMode();
        }

        // ===== Configuration persistence =====

        private void LoadConfig() => _config = ReadPanelConfig();

        private void SaveConfig() => WritePanelConfig(_config);

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
                config.RunMode = Pick(RunModes, value.Value<string>("mode"), config.RunMode);
                config.Host = Pick(Hosts, value.Value<string>("host"), config.Host);
                config.Model = value.Value<string>("model") ?? string.Empty;
                config.Effort = value.Value<string>("effort") ?? string.Empty;
                config.UsageReading = value["usageReading"]?.Type != JTokenType.Boolean || value.Value<bool>("usageReading");
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
                ["mode"] = config.RunMode,
                ["host"] = config.Host,
                ["model"] = config.Model,
                ["effort"] = config.Effort,
                ["usageReading"] = config.UsageReading,
            }.ToString(Formatting.None));
        }

        private static string Pick(string[] allowed, string value, string fallback)
        {
            return value != null && Array.IndexOf(allowed, value) >= 0 ? value : fallback;
        }

        // ===== Commands =====

        private bool PythonReady => PythonLocator.Last != null && PythonLocator.Last.Found;
        private bool CanLaunch => !_disposed && _pending.Count == 0 && PythonReady;

        private void Launch(string command, List<string> args, string declaredJson = null)
        {
            if (!CanLaunch) return;
            var result = EvaluationRunnerService.Launch(_projectRoot, PythonLocator.Last.Executable, command, args, declaredJson);
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

        private void StartSession()
        {
            var args = new List<string>
            {
                "--track", _config.Track, "--tier", _config.Tier, "--mode", _config.RunMode, "--lang", _promptLang,
            };
            if (!_config.UsageReading) args.Add("--no-usage");
            _copiedSessionId = null;
            _copyFailedPath = null;
            Launch("start", args, EvaluationRunnerService.BuildDeclaredJson(_config.Host, _config.Model, _config.Effort));
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
            var path = PromptFile(_session);
            _copiedSessionId = null;
            _copyFailedPath = null;
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
                    _copiedSessionId = _session.SessionId;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _copyFailedPath = path;
                }
            }
            RenderStart();
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

        private static string PromptLanguageOf(EvaluationState.SessionInfo session)
        {
            return Array.IndexOf(PromptLanguages, session.Lang) >= 0 ? session.Lang : "en";
        }

        private static void OpenFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) EditorUtility.OpenWithDefaultApp(path);
        }

        // ===== Tracking =====

        private void UpdateTracking()
        {
            bool wanted = !_disposed && _pending.Count > 0 && _root.IsVisible();
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
            Refresh(PollPending());
            if (_pending.Count == 0) _tick?.Pause();
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
            RenderStart();
            RenderProgress();
            if (full) RenderReport();
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

            var status = _session?.Status;
            if (_lastSessionStatus == "active" && status != null && status != "active") CheckModeRestore(false);
            _lastSessionStatus = status;
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
            SetText(_intro, SkillsLocalization.Get("tab_eval_intro"));

            SetText(_envTitle, SkillsLocalization.Get("tab_eval_env_title"));
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
            SetText(_checkBtn, SkillsLocalization.Get("tab_eval_check_btn"));
            SetText(_cleanupBtn, SkillsLocalization.Get("tab_eval_cleanup_btn"));

            SetText(_configTitle, SkillsLocalization.Get("tab_eval_config_title"));
            SetText(_trackLabel, SkillsLocalization.Get("tab_eval_track_label"));
            SetText(_tierLabel, SkillsLocalization.Get("tab_eval_tier_label"));
            SetText(_runModeLabel, SkillsLocalization.Get("tab_eval_run_mode_label"));
            SetText(_langLabel, SkillsLocalization.Get("tab_eval_lang_label"));
            SetText(_hostLabel, SkillsLocalization.Get("tab_eval_host_label"));
            SetText(_modelLabel, SkillsLocalization.Get("tab_eval_model_label"));
            SetText(_effortLabel, SkillsLocalization.Get("tab_eval_effort_label"));
            SetText(_declaredHint, SkillsLocalization.Get("tab_eval_declared_hint"));
            SetText(_usageLabel, SkillsLocalization.Get("tab_eval_usage_label"));
            SetText(_usageHint, SkillsLocalization.Get("tab_eval_usage_hint"));

            SetText(_startTitle, SkillsLocalization.Get("tab_eval_start_title"));
            SetText(_startBtn, SkillsLocalization.Get("tab_eval_start_btn"));
            SetText(_copyPromptBtn, SkillsLocalization.Get("tab_eval_copy_prompt_btn"));
            SetText(_openPromptBtn, SkillsLocalization.Get("tab_eval_open_prompt_btn"));

            SetText(_progressTitle, SkillsLocalization.Get("tab_eval_progress_title"));
            if (_refreshBtn != null) _refreshBtn.tooltip = SkillsLocalization.Get("tab_eval_refresh");
            SetText(_abortBtn, SkillsLocalization.Get("tab_eval_abort_btn"));
            SetText(_submitHint, SkillsLocalization.Get("tab_eval_submit_hint"));
            SetText(_abortConfirmText, SkillsLocalization.Get("tab_eval_abort_confirm_text"));
            SetText(_abortConfirmOk, SkillsLocalization.Get("tab_eval_abort_confirm_ok"));
            SetText(_abortConfirmCancel, SkillsLocalization.Get("tab_eval_cancel"));

            SetText(_reportTitle, SkillsLocalization.Get("tab_eval_report_title"));
            SetText(_reportBtn, SkillsLocalization.Get("tab_eval_report_btn"));
            SetText(_openReportBtn, SkillsLocalization.Get("tab_eval_open_report_btn"));
            SetText(_revealReportBtn, SkillsLocalization.Get("tab_eval_reveal_btn"));
            SetText(_reportUnofficial, SkillsLocalization.Get("tab_eval_report_unofficial"));
            SetText(_historyTitle, SkillsLocalization.Get("tab_eval_history_title"));
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
            SetChoices(_runMode, RunModes, new List<string>
            {
                SkillsLocalization.Get("tab_eval_run_mode_suite"), SkillsLocalization.Get("tab_eval_run_mode_strict"),
            }, _config.RunMode);
            SetChoices(_lang, PromptLanguages, new List<string>
            {
                SkillsLocalization.Get("tab_eval_lang_zh"), SkillsLocalization.Get("tab_eval_lang_en"),
            }, _promptLang);
            SetChoices(_host, Hosts, new List<string>
            {
                SkillsLocalization.Get("tab_eval_host_unknown"), SkillsLocalization.Get("tab_eval_host_claude_code"),
                SkillsLocalization.Get("tab_eval_host_codex"), SkillsLocalization.Get("tab_eval_host_cursor"),
                SkillsLocalization.Get("tab_eval_host_other"),
            }, _config.Host);
            _model?.SetValueWithoutNotify(_config.Model);
            _effort?.SetValueWithoutNotify(_config.Effort);
            _usageToggle?.SetValueWithoutNotify(_config.UsageReading);
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
            bool active = _session?.Status == "active";
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

        private void RenderStart()
        {
            bool active = _session?.Status == "active";
            _startBtn?.SetEnabled(CanLaunch && !_runnerBusy && !active);
            string hint;
            if (!PythonReady) hint = SkillsLocalization.Get("tab_eval_start_needs_python");
            else if (active) hint = SkillsLocalization.Get("tab_eval_start_active", _session.SessionId);
            else hint = SkillsLocalization.Get("tab_eval_start_hint");
            SetText(_startHint, hint);

            bool hasPrompt = PromptFile(_session) != null;
            _copyPromptBtn?.SetEnabled(hasPrompt);
            _openPromptBtn?.SetEnabled(hasPrompt);

            string copyNote = null;
            if (_copyFailedPath != null)
                copyNote = SkillsLocalization.Get("tab_eval_prompt_missing", _copyFailedPath);
            else if (active && _copiedSessionId == _session.SessionId)
                copyNote = SkillsLocalization.Get("tab_eval_copied");
            SetText(_copied, copyNote ?? string.Empty);
            _copied.SetVisible(copyNote != null);
            RenderMessages(_startMessages, "start");
        }

        private void RenderProgress()
        {
            _taskList?.Clear();
            bool active = _session?.Status == "active";
            if (_session == null)
            {
                SetText(_sessionLine, SkillsLocalization.Get("tab_eval_no_session"));
                _taskLine.SetVisible(false);
            }
            else
            {
                int total = _session.Order.Count;
                int finished = 0;
                foreach (var taskId in _session.Order)
                {
                    var state = _session.Tasks.TryGetValue(taskId, out var entry) ? entry.State : null;
                    if (state == "done" || state == "invalid" || state == "cancelled") finished++;
                    AddListRow(_taskList, taskId, TaskStateLabel(state));
                }
                SetText(_sessionLine, SkillsLocalization.Get("tab_eval_session_line", _session.SessionId,
                    SessionStatusLabel(_session.Status), finished, total, RunModeShort(_session.Mode),
                    _session.Track ?? "?", _session.Tier ?? "?"));

                string taskText = null;
                if (active && _attempt != null)
                {
                    var issued = EvaluationState.ParseIso(_attempt.IssuedAt);
                    var elapsed = issued.HasValue ? Math.Max(0, (int)(DateTime.UtcNow - issued.Value).TotalMinutes) : 0;
                    taskText = SkillsLocalization.Get("tab_eval_task_line", _attempt.TaskId ?? "?", _attempt.K, total,
                        PhaseLabel(_attempt.Phase), elapsed,
                        _attempt.Minutes?.ToString(CultureInfo.InvariantCulture) ?? "?");
                }
                else if (active)
                {
                    taskText = SkillsLocalization.Get("tab_eval_waiting_next");
                }
                SetText(_taskLine, taskText ?? string.Empty);
                _taskLine.SetVisible(taskText != null);
            }

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

        private void RenderReport()
        {
            _reportTasks?.Clear();
            var viewed = ViewedSession();
            bool hasBody = false;
            bool hasMarkdown = false;
            bool canBuild = false;
            string summary = null;
            string metrics = null;
            bool unofficial = false;

            if (viewed == null)
            {
                SetText(_reportSession, SkillsLocalization.Get("tab_eval_report_none"));
            }
            else
            {
                SetText(_reportSession, SkillsLocalization.Get("tab_eval_report_session", viewed.SessionId,
                    SessionStatusLabel(viewed.Status), FormatLocal(viewed.CreatedAt, "yyyy-MM-dd HH:mm")));
                var report = EvaluationState.ReadReport(_projectRoot, viewed);
                if (report.Status == EvaluationState.ReadStatus.Ok)
                {
                    hasBody = true;
                    var totals = report.Value["summary"] as JObject;
                    summary = SkillsLocalization.Get("tab_eval_report_summary", SummaryCounts(totals));
                    var passRate = Number(totals?["strictPassRate"], "0.0%");
                    var meanCompletion = Number(totals?["meanCompletion"], "0.0");
                    var wallSeconds = Number(totals?["wallSeconds"], "0.0");
                    var processed = Number((totals?["usage"] as JObject)?["processed"], "N0");
                    metrics = SkillsLocalization.Get("tab_eval_report_metrics", passRate, meanCompletion, wallSeconds, processed);
                    unofficial = report.Value["official"]?.Type == JTokenType.Boolean && !report.Value.Value<bool>("official");
                    if (report.Value["tasks"] is JArray rows)
                    {
                        foreach (var row in rows)
                            if (row is JObject task) AddReportTask(task, viewed.SessionId);
                    }
                }
                else if (report.Status == EvaluationState.ReadStatus.Missing)
                {
                    summary = SkillsLocalization.Get("tab_eval_report_missing");
                    canBuild = viewed.Status != "active";
                }
                else
                {
                    summary = LocalizeProblem(EvaluationProblem.Local(report.Status == EvaluationState.ReadStatus.Unsupported
                        ? "SCHEMA_UNSUPPORTED"
                        : "STATE_CORRUPT"));
                }
                var markdown = EvaluationState.ReportMarkdownPath(_projectRoot, viewed);
                hasMarkdown = hasBody && markdown != null && File.Exists(markdown);
            }

            SetText(_reportSummary, summary ?? string.Empty);
            _reportSummary.SetVisible(summary != null);
            SetText(_reportMetrics, metrics ?? string.Empty);
            _reportMetrics.SetVisible(metrics != null);
            _reportUnofficial.SetVisible(unofficial);
            _reportBtn.SetVisible(canBuild);
            _reportBtn?.SetEnabled(CanLaunch && !_runnerBusy);
            _openReportBtn.SetVisible(hasMarkdown);
            _revealReportBtn.SetVisible(hasMarkdown);
            RenderMessages(_reportMessages, "report", "usage");
            RenderHistory(viewed?.SessionId);
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
                    RenderReport();
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
                RenderReport();
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

        private void RenderHistory(string viewedId)
        {
            if (_historyList == null) return;
            _historyList.Clear();
            if (_history.Count == 0)
            {
                _historyList.Add(SubLine(SkillsLocalization.Get("tab_eval_history_empty")));
                return;
            }
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
                    RenderReport();
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

        /// <summary>report.json summary counts in the order of tab_eval_report_summary's placeholders.</summary>
        private static object[] SummaryCounts(JObject totals)
        {
            var keys = new[] { "tasks", "pass", "partial", "fail", "invalid", "contaminated", "cancelled" };
            var counts = new object[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var token = totals?[keys[i]];
                counts[i] = token != null && token.Type == JTokenType.Integer ? token.Value<int>() : 0;
            }
            return counts;
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
