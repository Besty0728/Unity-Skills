using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Macro Recorder panel — the user-facing surface of MacroRecorderService (demonstration
    /// recording): start/stop, a live operation timeline with the per-record invertibility
    /// verdict, coverage bar, macro-file export/import, batch-JSON copy and parameterized
    /// replay. UI Toolkit / UXML, console-style like UnitySkillsAuditWindow.
    ///
    /// The recorder itself is static: closing the window neither stops the session nor loses
    /// the timeline — reopening rebuilds it from GetTimelineSnapshot(-1). Refresh runs on a
    /// 500ms EditorUiScheduler.RepeatSafe tick (mutations deferred to delayCall, issue #44) and
    /// pulls records incrementally: rows are appended, never rebuilt wholesale.
    ///
    /// Replay self-calls POST /skills/batch over async UnityWebRequest. The completed callback
    /// arrives on a later editor tick — the main thread is NEVER blocked waiting, because the
    /// server's consumer runs on this very thread (a synchronous wait would deadlock).
    /// </summary>
    public sealed class MacroRecorderWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.besty.unity-skills/Editor/UI/MacroRecorderWindow.uxml";
        private const string UssPath  = "Packages/com.besty.unity-skills/Editor/UI/MacroRecorderWindow.uss";
        private const long TickMs = 500;
        private const int MaxFailedStepsShown = 5;
        private const int ReplayTimeoutSec = 300;

        // ----- UI refs -----
        private Button        _recordBtn;
        private Label         _statusLabel;
        private Toggle        _ignoreRestToggle;
        private Label         _interruptedHint;
        private Label         _undoRedoHint;
        private Label         _coverageLabel;
        private VisualElement _covOk;
        private VisualElement _covPartial;
        private VisualElement _covUnsupported;
        private ListView      _timeline;
        private Button        _exportBtn;
        private Button        _copyBtn;
        private Button        _importBtn;
        private Button        _replayBtn;
        private Button        _replayRunBtn;
        private Label         _sourceLabel;
        private VisualElement _paramsBox;
        private Label         _paramsLabel;
        private TextField     _paramsField;
        private VisualElement _resultBox;
        private Label         _resultTitle;
        private TextField     _resultDetail;

        // ----- Timeline incremental cursor -----
        private readonly List<MacroTimelineItem> _items = new List<MacroTimelineItem>();
        private string _sessionMark;   // SessionStartedUtc the list was built from
        private int _lastIndex = -1;

        // ----- Replay / import state -----
        private MacroFile _loadedMacro;        // imported file — wins over the session product
        private string _loadedMacroName;
        private UnityWebRequest _replayRequest; // in-flight guard (one replay at a time)

        private IVisualElementScheduledItem _tickItem;
        private SkillsLocalization.Language _lastLang;

        // Deliberately NO [MenuItem]: per product decision the Window menu carries exactly one
        // UnitySkills entry (the main panel). This window opens implicitly — via the user-bound
        // shortcut (ShortcutActions.OpenMacroRecorderId, configured in Settings ▸ Shortcuts).
        public static void ShowWindow()
        {
            var window = GetWindow<MacroRecorderWindow>(SkillsLocalization.Get("macro_window_title"));
            window.minSize = new Vector2(520, 420);
            window.Focus();
        }

        private void OnDisable()
        {
            // Pause all periodic refresh; the (static) recording itself keeps running.
            _tickItem?.Pause();
            _tickItem = null;
        }

        public void CreateGUI()
        {
            var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (uss != null) rootVisualElement.styleSheets.Add(uss);
            else Debug.LogWarning($"[UnitySkills] Failed to load Macro USS: {UssPath}");

            // Bundled CJK font — fixes the macOS shared-atlas glyph drop (see UISkillsFont).
            UISkillsFont.Apply(rootVisualElement);

            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (uxml == null)
            {
                Debug.LogError($"[UnitySkills] Failed to load Macro UXML: {UxmlPath}");
                return;
            }
            uxml.CloneTree(rootVisualElement);

            _recordBtn        = rootVisualElement.Q<Button>("macro-record-btn");
            _statusLabel      = rootVisualElement.Q<Label>("macro-status-label");
            _ignoreRestToggle = rootVisualElement.Q<Toggle>("macro-ignore-rest-toggle");
            _interruptedHint  = rootVisualElement.Q<Label>("macro-interrupted-hint");
            _undoRedoHint     = rootVisualElement.Q<Label>("macro-undoredo-hint");
            _coverageLabel    = rootVisualElement.Q<Label>("macro-coverage-label");
            _covOk            = rootVisualElement.Q<VisualElement>("macro-coverage-ok");
            _covPartial       = rootVisualElement.Q<VisualElement>("macro-coverage-partial");
            _covUnsupported   = rootVisualElement.Q<VisualElement>("macro-coverage-unsupported");
            _timeline         = rootVisualElement.Q<ListView>("macro-timeline");
            _exportBtn        = rootVisualElement.Q<Button>("macro-export-btn");
            _copyBtn          = rootVisualElement.Q<Button>("macro-copy-btn");
            _importBtn        = rootVisualElement.Q<Button>("macro-import-btn");
            _replayBtn        = rootVisualElement.Q<Button>("macro-replay-btn");
            _replayRunBtn     = rootVisualElement.Q<Button>("macro-replay-run-btn");
            _sourceLabel      = rootVisualElement.Q<Label>("macro-source-label");
            _paramsBox        = rootVisualElement.Q<VisualElement>("macro-params-box");
            _paramsLabel      = rootVisualElement.Q<Label>("macro-params-label");
            _paramsField      = rootVisualElement.Q<TextField>("macro-params-field");
            _resultBox        = rootVisualElement.Q<VisualElement>("macro-result-box");
            _resultTitle      = rootVisualElement.Q<Label>("macro-result-title");
            _resultDetail     = rootVisualElement.Q<TextField>("macro-result-detail");

            if (_timeline != null)
            {
                _timeline.fixedItemHeight = 22;
                _timeline.makeItem = MakeRow;
                _timeline.bindItem = BindRow;
                _timeline.selectionType = SelectionType.Single;
                _timeline.itemsSource = _items;
            }

            if (_recordBtn != null) _recordBtn.clicked += OnRecordClicked;
            if (_exportBtn != null) _exportBtn.clicked += OnExportClicked;
            if (_copyBtn != null) _copyBtn.clicked += OnCopyClicked;
            if (_importBtn != null) _importBtn.clicked += OnImportClicked;
            if (_replayBtn != null) _replayBtn.clicked += OnReplayClicked;
            if (_replayRunBtn != null) _replayRunBtn.clicked += OnReplayRunClicked;

            if (_ignoreRestToggle != null)
            {
                _ignoreRestToggle.SetValueWithoutNotify(MacroRecorderService.IgnoreRestChanges);
                _ignoreRestToggle.RegisterValueChangedCallback(e =>
                    MacroRecorderService.IgnoreRestChanges = e.newValue);
            }

            if (_resultDetail != null) _resultDetail.isReadOnly = true;

            _lastLang = SkillsLocalization.Current;
            RefreshLocalization();
            OnTick(); // initial paint (event context, safe to mutate)

            _tickItem = EditorUiScheduler.RepeatSafe(rootVisualElement, TickMs, OnTick);
        }

        // ===== Periodic refresh (RepeatSafe → runs on delayCall, outside repaint) =====

        private void OnTick()
        {
            // Language can be switched from the main window's footer; follow it live.
            if (SkillsLocalization.Current != _lastLang)
            {
                _lastLang = SkillsLocalization.Current;
                RefreshLocalization();
            }

            var snap = MacroRecorderService.GetTimelineSnapshot(_lastIndex);

            // New session (or first paint after reopen) → rebuild the list from scratch.
            if (!string.Equals(snap.SessionStartedUtc, _sessionMark, StringComparison.Ordinal))
            {
                _sessionMark = snap.SessionStartedUtc;
                _items.Clear();
                _lastIndex = -1;
                _timeline?.RefreshItems();
                snap = MacroRecorderService.GetTimelineSnapshot(-1);
            }

            if (snap.Items.Count > 0)
            {
                // Incremental append — rows are only ever added within a session.
                _items.AddRange(snap.Items);
                _lastIndex = snap.Items[snap.Items.Count - 1].Index;
                if (_timeline != null)
                {
                    _timeline.RefreshItems();
                    _timeline.ScrollToItem(-1);
                }
            }

            RefreshControlBar(snap);
            RefreshCoverage();
            RefreshActionsEnabled(snap);
        }

        private void RefreshControlBar(MacroTimelineSnapshot snap)
        {
            if (snap.Recording) rootVisualElement.AddToClassList("recording");
            else rootVisualElement.RemoveFromClassList("recording");

            if (_recordBtn != null)
                _recordBtn.text = SkillsLocalization.Get(snap.Recording ? "macro_btn_stop" : "macro_btn_record");

            if (_statusLabel != null)
            {
                if (snap.Recording)
                {
                    string elapsed = FormatElapsed(snap.SessionStartedUtc);
                    _statusLabel.text = string.Format(
                        SkillsLocalization.Get("macro_status_recording_fmt"), snap.Total, elapsed);
                }
                else if (snap.HasStoppedSession)
                {
                    string text = string.Format(
                        SkillsLocalization.Get("macro_status_stopped_fmt"), snap.Total);
                    if (snap.StoppedReason == "buffer_full")
                        text += " · " + SkillsLocalization.Get("macro_stopped_buffer_full");
                    _statusLabel.text = text;
                }
                else
                {
                    _statusLabel.text = SkillsLocalization.Get("macro_status_idle");
                }
            }

            SetDisplayed(_interruptedHint, snap.InterruptedByReload);
            SetDisplayed(_undoRedoHint, snap.UndoRedoDetected);
        }

        private void RefreshCoverage()
        {
            var sum = MacroRecorderService.GetCoverageSummary();
            if (_coverageLabel != null)
            {
                _coverageLabel.text = string.Format(SkillsLocalization.Get("macro_coverage_fmt"),
                    sum.Ok, sum.Partial, sum.Unsupported, sum.Total);
            }
            // The one code-driven style (allowed for the ratio bar): flex-grow carries the live
            // proportion of each segment; everything else stays in USS.
            if (_covOk != null) _covOk.style.flexGrow = sum.Ok;
            if (_covPartial != null) _covPartial.style.flexGrow = sum.Partial;
            if (_covUnsupported != null) _covUnsupported.style.flexGrow = sum.Unsupported;
        }

        private void RefreshActionsEnabled(MacroTimelineSnapshot snap)
        {
            bool sessionReady = !snap.Recording && snap.HasStoppedSession;
            bool replaySource = _loadedMacro != null || sessionReady;
            bool replayIdle = _replayRequest == null;

            _exportBtn?.SetEnabled(sessionReady);
            _copyBtn?.SetEnabled(replaySource);
            _importBtn?.SetEnabled(!snap.Recording);
            _replayBtn?.SetEnabled(replaySource && replayIdle && !snap.Recording);
            _replayRunBtn?.SetEnabled(replayIdle);

            if (_sourceLabel != null)
            {
                if (_loadedMacro != null)
                    _sourceLabel.text = string.Format(SkillsLocalization.Get("macro_source_file_fmt"),
                        _loadedMacroName, _loadedMacro.Steps.Count);
                else if (sessionReady)
                    _sourceLabel.text = SkillsLocalization.Get("macro_source_session");
                else
                    _sourceLabel.text = SkillsLocalization.Get("macro_source_none");
            }
        }

        private static void SetDisplayed(VisualElement el, bool shown)
        {
            if (el != null)
                el.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string FormatElapsed(string startedUtcIso)
        {
            if (DateTime.TryParse(startedUtcIso, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var started))
            {
                var t = DateTime.UtcNow - started.ToUniversalTime();
                if (t.TotalSeconds < 0) t = TimeSpan.Zero;
                return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            }
            return "00:00";
        }

        // ===== Timeline rows =====

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("macro-row");

            var icon = new Label { name = "row-icon" };
            icon.AddToClassList("macro-row__icon");
            row.Add(icon);

            var time = new Label { name = "row-time" };
            time.AddToClassList("macro-row__time");
            row.Add(time);

            var kind = new Label { name = "row-kind" };
            kind.AddToClassList("macro-row__kind");
            row.Add(kind);

            var summary = new Label { name = "row-summary" };
            summary.AddToClassList("macro-row__summary");
            row.Add(summary);

            return row;
        }

        private void BindRow(VisualElement el, int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var it = _items[index];

            var icon    = el.Q<Label>("row-icon");
            var time    = el.Q<Label>("row-time");
            var kind    = el.Q<Label>("row-kind");
            var summary = el.Q<Label>("row-summary");

            if (icon != null)
            {
                string glyph;
                string cls;
                switch (it.State)
                {
                    case MacroInvertibility.Ok:      glyph = "✓"; cls = "macro-inv-ok"; break;
                    case MacroInvertibility.Partial: glyph = "!"; cls = "macro-inv-partial"; break;
                    default:                         glyph = "×"; cls = "macro-inv-unsupported"; break;
                }
                if (icon.text != glyph) icon.text = glyph;
                icon.RemoveFromClassList("macro-inv-ok");
                icon.RemoveFromClassList("macro-inv-partial");
                icon.RemoveFromClassList("macro-inv-unsupported");
                icon.AddToClassList(cls);
            }

            if (time != null) time.text = FormatShortTime(it.TsUtc);
            if (kind != null) kind.text = SkillsLocalization.Get("macro_kind_" + it.KindKey);
            if (summary != null)
            {
                string text = string.IsNullOrEmpty(it.Detail)
                    ? it.SubjectName
                    : $"{it.SubjectName} — {it.Detail}";
                if (summary.text != text) summary.text = text;
            }

            el.tooltip = BuildRowTooltip(it);
        }

        private static string BuildRowTooltip(MacroTimelineItem it)
        {
            switch (it.State)
            {
                case MacroInvertibility.Ok:
                    return SkillsLocalization.Get("macro_inv_ok_tip");
                case MacroInvertibility.Partial:
                    return string.Format(SkillsLocalization.Get("macro_inv_partial_tip_fmt"), LocalizedReason(it));
                default:
                    return string.Format(SkillsLocalization.Get("macro_inv_unsupported_tip_fmt"), LocalizedReason(it));
            }
        }

        /// <summary>ReasonKey → localized short reason; falls back to the export's English text.</summary>
        private static string LocalizedReason(MacroTimelineItem it)
        {
            if (!string.IsNullOrEmpty(it.ReasonKey))
            {
                string key = "macro_inv_reason_" + it.ReasonKey;
                string v = SkillsLocalization.Get(key);
                if (!string.Equals(v, key, StringComparison.Ordinal)) return v;
            }
            return it.ReasonEn ?? "";
        }

        private static string FormatShortTime(string isoTs)
        {
            if (string.IsNullOrEmpty(isoTs)) return "";
            if (DateTime.TryParse(isoTs, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt.ToLocalTime().ToString("HH:mm:ss");
            }
            return isoTs.Length >= 19 ? isoTs.Substring(11, 8) : isoTs;
        }

        // ===== Control bar actions =====

        private void OnRecordClicked()
        {
            if (MacroRecorderService.IsRecording)
                MacroRecorderService.Stop();
            else
            {
                MacroRecorderService.Start(null);
                HideResult();
            }
            OnTick();
        }

        // ===== Export / copy / import =====

        private static string DefaultFileDialogDir()
        {
            // Start one level above Assets (the project root).
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }

        private void OnExportClicked()
        {
            string path = EditorUtility.SaveFilePanel(
                SkillsLocalization.Get("macro_btn_export_file"),
                DefaultFileDialogDir(),
                $"macro_{DateTime.Now:yyyyMMdd_HHmmss}",
                "json");
            if (string.IsNullOrEmpty(path)) return;

            if (MacroFileStore.TrySaveCurrentSession(path, out var error))
                ShowResult(string.Format(SkillsLocalization.Get("macro_export_saved_fmt"), path), null, ok: true);
            else
                ShowResult(string.Format(SkillsLocalization.Get("macro_export_failed_fmt"), error), null, ok: false);
        }

        private void OnCopyClicked()
        {
            if (!TryGetReplayBody(out var body, out var error))
            {
                ShowResult(string.Format(SkillsLocalization.Get("macro_export_failed_fmt"), error), null, ok: false);
                return;
            }
            EditorGUIUtility.systemCopyBuffer = body.ToString(Formatting.Indented);
            ShowResult(SkillsLocalization.Get("macro_copied"), null, ok: true);
        }

        private void OnImportClicked()
        {
            string path = EditorUtility.OpenFilePanel(
                SkillsLocalization.Get("macro_btn_import_file"),
                DefaultFileDialogDir(),
                "json");
            if (string.IsNullOrEmpty(path)) return;

            if (!MacroFileStore.TryLoadFile(path, out var file, out var error))
            {
                EditorUtility.DisplayDialog(
                    SkillsLocalization.Get("macro_btn_import_file"),
                    string.Format(SkillsLocalization.Get("macro_import_failed_fmt"), error),
                    SkillsLocalization.Get("macro_dialog_ok"));
                return;
            }

            _loadedMacro = file;
            _loadedMacroName = Path.GetFileName(path);
            HideParamsBox();
            HideResult();
            OnTick();
        }

        // ===== Replay (async self-call to POST /skills/batch) =====

        /// <summary>
        /// The replay body: the loaded macro file when one was imported, otherwise the current
        /// stopped session inverted through the same core macro_export uses.
        /// </summary>
        private bool TryGetReplayBody(out JObject body, out string error)
        {
            body = null;
            if (_loadedMacro != null)
            {
                body = new JObject { ["steps"] = _loadedMacro.Steps.DeepClone() };
                if (_loadedMacro.Params != null && _loadedMacro.Params.Count > 0)
                    body["params"] = _loadedMacro.Params.DeepClone();
                error = null;
                return true;
            }
            if (!MacroRecorderService.TryGetStoppedSessionExport(out var steps, out _, out _, out error))
                return false;
            body = new JObject { ["steps"] = steps };
            return true;
        }

        private void OnReplayClicked()
        {
            if (!SkillsHttpServer.IsRunning)
            {
                ShowResult(SkillsLocalization.Get("macro_server_not_running"), null, ok: false);
                return;
            }
            if (!TryGetReplayBody(out var body, out var error))
            {
                ShowResult(string.Format(SkillsLocalization.Get("macro_replay_request_failed_fmt"), error), null, ok: false);
                return;
            }

            if (HasParamSlot(body["steps"]) || (body["params"] is JObject p && p.Count > 0))
            {
                // Parameterized macro → reveal the params editor (prefilled from the file)
                // and wait for the explicit Run Replay click.
                if (_paramsField != null)
                    _paramsField.value = (body["params"] as JObject ?? new JObject()).ToString(Formatting.Indented);
                SetDisplayed(_paramsBox, true);
                return;
            }

            SendReplay(body);
        }

        private void OnReplayRunClicked()
        {
            if (!SkillsHttpServer.IsRunning)
            {
                ShowResult(SkillsLocalization.Get("macro_server_not_running"), null, ok: false);
                return;
            }
            if (!TryGetReplayBody(out var body, out var error))
            {
                ShowResult(string.Format(SkillsLocalization.Get("macro_replay_request_failed_fmt"), error), null, ok: false);
                return;
            }

            JObject prms;
            try
            {
                string raw = _paramsField != null ? _paramsField.value : "";
                prms = string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw);
            }
            catch (JsonException)
            {
                ShowResult(SkillsLocalization.Get("macro_params_invalid"), null, ok: false);
                return;
            }

            body["params"] = prms;
            SendReplay(body);
        }

        private static bool HasParamSlot(JToken token)
        {
            if (token is JObject obj)
            {
                if (obj.Property("$param") != null) return true;
                foreach (var prop in obj.Properties())
                    if (HasParamSlot(prop.Value)) return true;
            }
            else if (token is JArray arr)
            {
                foreach (var item in arr)
                    if (HasParamSlot(item)) return true;
            }
            return false;
        }

        private void SendReplay(JObject body)
        {
            if (_replayRequest != null) return; // one replay at a time

            HideParamsBox();
            ShowResult(SkillsLocalization.Get("macro_replay_running"), null, ok: true);

            string url = $"http://localhost:{SkillsHttpServer.Port}/skills/batch";
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = ReplayTimeoutSec;
            _replayRequest = req;

            // DEADLOCK RED LINE: never wait synchronously — the server's consumer runs on this
            // same main thread. completed fires on a later editor update tick (main thread,
            // outside repaint), so it may touch the UI directly.
            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                _replayRequest = null;
                try { HandleReplayResponse(req); }
                finally { req.Dispose(); }
            };
        }

        private void HandleReplayResponse(UnityWebRequest req)
        {
            // The window may have been closed while the request was in flight.
            if (rootVisualElement == null || rootVisualElement.panel == null) return;

            string text = req.downloadHandler?.text;
            if (string.IsNullOrEmpty(text))
            {
                ShowResult(string.Format(SkillsLocalization.Get("macro_replay_request_failed_fmt"),
                    req.error ?? req.result.ToString()), null, ok: false);
                return;
            }

            JObject resp;
            try
            {
                resp = JObject.Parse(text);
            }
            catch (JsonException)
            {
                ShowResult(string.Format(SkillsLocalization.Get("macro_replay_request_failed_fmt"), req.error ?? "invalid response"),
                    Truncate(text, 2000), ok: false);
                return;
            }

            // Error envelope (400/503 — batch validation, compiling, ...) has no status field.
            string status = resp["status"]?.ToString();
            if (string.IsNullOrEmpty(status) || resp["results"] == null)
            {
                string msg = resp["error"]?.ToString() ?? resp["message"]?.ToString() ?? req.error ?? "error";
                ShowResult(string.Format(SkillsLocalization.Get("macro_replay_request_failed_fmt"), msg),
                    resp.ToString(Formatting.Indented), ok: false);
                return;
            }

            int executed = resp["executed"]?.Value<int>() ?? 0;
            int failed = resp["failed"]?.Value<int>() ?? 0;
            bool rolledBack = resp["rolledBack"]?.Type == JTokenType.Boolean && resp["rolledBack"].Value<bool>();

            string title = string.Format(SkillsLocalization.Get("macro_replay_result_fmt"), status, executed, failed);
            if (rolledBack || string.Equals(status, "rolled_back", StringComparison.Ordinal))
                title += " (" + SkillsLocalization.Get("macro_replay_rolled_back") + ")";

            string detail = null;
            if (failed > 0 && resp["results"] is JArray results)
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Format(SkillsLocalization.Get("macro_replay_failed_steps"), MaxFailedStepsShown));
                int shown = 0;
                foreach (var entry in results)
                {
                    if (shown >= MaxFailedStepsShown) break;
                    if (!string.Equals(entry["status"]?.ToString(), "error", StringComparison.Ordinal)) continue;
                    string err = entry["error"]?["error"]?.ToString()
                                 ?? entry["error"]?.ToString(Formatting.None) ?? "";
                    sb.AppendLine($"#{entry["index"]} {entry["skill"]}: {Truncate(err, 300)}");
                    shown++;
                }
                detail = sb.ToString().TrimEnd();
            }

            ShowResult(title, detail, ok: failed == 0);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        // ===== Result / params box =====

        private void ShowResult(string title, string detail, bool ok)
        {
            SetDisplayed(_resultBox, true);
            if (_resultTitle != null)
            {
                _resultTitle.text = title;
                _resultTitle.RemoveFromClassList("ok");
                _resultTitle.RemoveFromClassList("error");
                _resultTitle.AddToClassList(ok ? "ok" : "error");
            }
            if (_resultDetail != null)
            {
                SetDisplayed(_resultDetail, !string.IsNullOrEmpty(detail));
                _resultDetail.SetValueWithoutNotify(detail ?? "");
            }
        }

        private void HideResult() => SetDisplayed(_resultBox, false);

        private void HideParamsBox() => SetDisplayed(_paramsBox, false);

        // ===== Localization =====

        private void RefreshLocalization()
        {
            titleContent = new GUIContent(SkillsLocalization.Get("macro_window_title"));

            if (_ignoreRestToggle != null)
            {
                _ignoreRestToggle.label = SkillsLocalization.Get("macro_ignore_rest_toggle");
                _ignoreRestToggle.tooltip = SkillsLocalization.Get("macro_ignore_rest_tooltip");
            }
            if (_interruptedHint != null) _interruptedHint.text = SkillsLocalization.Get("macro_interrupted_hint");
            if (_undoRedoHint != null) _undoRedoHint.text = SkillsLocalization.Get("macro_undo_redo_hint");
            if (_exportBtn != null) _exportBtn.text = SkillsLocalization.Get("macro_btn_export_file");
            if (_copyBtn != null) _copyBtn.text = SkillsLocalization.Get("macro_btn_copy_json");
            if (_importBtn != null) _importBtn.text = SkillsLocalization.Get("macro_btn_import_file");
            if (_replayBtn != null) _replayBtn.text = SkillsLocalization.Get("macro_btn_replay");
            if (_replayRunBtn != null) _replayRunBtn.text = SkillsLocalization.Get("macro_btn_replay_run");
            if (_paramsLabel != null) _paramsLabel.text = SkillsLocalization.Get("macro_params_label");

            // Localized rows / status / coverage refresh on the next tick paint.
            _timeline?.RefreshItems();
        }
    }
}
