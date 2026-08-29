using System;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Controller for the "Shortcuts" section of the Settings Drawer.
    ///
    /// One row per UnitySkills panel command: display name + current binding text + [Edit] / [Clear].
    /// Clicking [Edit] enters capture mode: captures <see cref="KeyDownEvent"/> via TrickleDown on the
    /// drawer root, reads keyCode + modifiers, with live conflict detection; with no conflict, [Apply]
    /// (<c>RebindShortcut</c>) is enabled, with a conflict it shows red text and disables Apply; Esc /
    /// [Cancel] / clicking outside the row all exit. Binding persistence goes to ShortcutManager's profile (no EditorPrefs writes); styling is all USS classes.
    ///
    /// Relation to main-window issue #44: this section only mutates the visual tree on user events
    /// (click / keypress) and explicit Rebuild, and has no periodic tick, so it needs no EditorUiScheduler fallback.
    /// </summary>
    public class ShortcutsSettingsController
    {
        private readonly VisualElement _root;   // drawer container: boundary for capture registration and hit testing
        private readonly Label _title;
        private readonly Label _hint;
        private readonly VisualElement _list;

        // Capture mode: _capturingId==null means not capturing.
        private string _capturingId;
        private KeyCombination _captured;
        private bool _hasCaptured;              // Whether a valid combination has already been pressed (decides whether the row shows prompt or preview)
        private string _conflictName;           // Non-null = conflicts with this command, refuse to save
        private VisualElement _capturingRow;    // Hit testing: a pointer down outside this row cancels capture

        public ShortcutsSettingsController(VisualElement drawerContainer)
        {
            _root  = drawerContainer;
            _title = drawerContainer.Q<Label>("group-shortcuts-title");
            _hint  = drawerContainer.Q<Label>("shortcuts-hint");
            _list  = drawerContainer.Q<VisualElement>("shortcuts-list");

            // Capture-mode key handling: TrickleDown on the drawer root, gets KeyDown before focused child nodes.
            _root.RegisterCallback<KeyDownEvent>(OnCaptureKeyDown, TrickleDown.TrickleDown);
            // Clicking outside the row in capture mode = cancel (includes clicking another setting item / the mask).
            _root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);

            RefreshLocalization();
        }

        /// <summary>Refreshes static copy and rebuilds rows on language switch / first assembly.</summary>
        public void RefreshLocalization()
        {
            if (_title != null) _title.text = SkillsLocalization.Get("shortcut_section_title");
            if (_hint  != null) _hint.text  = SkillsLocalization.Get("shortcut_section_hint");
            Rebuild();
        }

        /// <summary>Called every time the drawer opens: pulls the latest bindings and rebuilds, overriding external changes made in Edit ▸ Shortcuts.</summary>
        public void Refresh() => Rebuild();

        // ── Rendering ────────────────────────────────────────────────────

        private void Rebuild()
        {
            if (_list == null) return;
            _capturingRow = null;
            _list.Clear();
            foreach (var cmd in ShortcutActions.Commands)
                _list.Add(BuildRow(cmd));

            // The capturing row needs focus to receive KeyDown; defer Focus by one frame to dodge an attach-timing issue (mirrors what drawer-mask does).
            if (_capturingId != null && _capturingRow != null)
            {
                var row = _capturingRow;
                row.schedule.Execute(() => row.Focus()).StartingIn(0);
            }
        }

        private VisualElement BuildRow(ShortcutCommand cmd)
            => _capturingId == cmd.Id ? BuildCaptureRow(cmd) : BuildNormalRow(cmd);

        private VisualElement BuildNormalRow(ShortcutCommand cmd)
        {
            var row = new VisualElement();
            row.AddToClassList("setting-row");

            var name = new Label(SkillsLocalization.Get(cmd.LocKey));
            name.AddToClassList("setting-row__label");
            row.Add(name);

            var binding = new Label(CurrentBindingText(cmd.Id, out bool unset));
            binding.AddToClassList("shortcut-binding");
            if (unset) binding.AddToClassList("shortcut-binding--unset");
            row.Add(binding);

            var editBtn = new Button(() => BeginCapture(cmd)) { text = SkillsLocalization.Get("shortcut_btn_edit") };
            editBtn.AddToClassList("mini-btn");
            row.Add(editBtn);

            var clearBtn = new Button(() => ClearBinding(cmd)) { text = SkillsLocalization.Get("shortcut_btn_clear") };
            clearBtn.AddToClassList("mini-btn");
            clearBtn.SetEnabled(!unset);
            row.Add(clearBtn);

            return row;
        }

        private VisualElement BuildCaptureRow(ShortcutCommand cmd)
        {
            // Vertical container: top row + optional red conflict text. The container itself is focusable, serving as the KeyDown focus host and hit-test boundary.
            var container = new VisualElement { style = { flexDirection = FlexDirection.Column, marginBottom = 6 } };
            container.focusable = true;
            _capturingRow = container;

            var line = new VisualElement();
            line.AddToClassList("setting-row");
            line.style.marginBottom = 0;
            container.Add(line);

            var name = new Label(SkillsLocalization.Get(cmd.LocKey));
            name.AddToClassList("setting-row__label");
            line.Add(name);

            if (!_hasCaptured)
            {
                var prompt = new Label(SkillsLocalization.Get("shortcut_capture_prompt"));
                prompt.AddToClassList("shortcut-capture-prompt");
                line.Add(prompt);
            }
            else
            {
                var preview = new Label(_captured.ToString());
                preview.AddToClassList("shortcut-preview");
                line.Add(preview);

                var applyBtn = new Button(() => ApplyCapture(cmd)) { text = SkillsLocalization.Get("shortcut_btn_apply") };
                applyBtn.AddToClassList("mini-btn");
                applyBtn.SetEnabled(_conflictName == null);
                line.Add(applyBtn);
            }

            var cancelBtn = new Button(CancelCapture) { text = SkillsLocalization.Get("shortcut_btn_cancel") };
            cancelBtn.AddToClassList("mini-btn");
            line.Add(cancelBtn);

            if (_hasCaptured && _conflictName != null)
            {
                var conflict = new Label(
                    string.Format(SkillsLocalization.Get("shortcut_conflict_fmt"), _conflictName));
                conflict.AddToClassList("shortcut-conflict");
                container.Add(conflict);
            }

            return container;
        }

        // ── Capture state machine ────────────────────────────────────────

        private void BeginCapture(ShortcutCommand cmd)
        {
            _capturingId = cmd.Id;
            _hasCaptured = false;
            _conflictName = null;
            Rebuild();
        }

        private void OnCaptureKeyDown(KeyDownEvent evt)
        {
            if (_capturingId == null) return;

            var kc = evt.keyCode;
            if (kc == KeyCode.Escape)
            {
                ConsumeKeyEvent(evt);
                CancelCapture();
                return;
            }
            // Pure modifier / no key: doesn't count as a combination, swallow the event and wait for a real key.
            if (IsModifierOrNone(kc))
            {
                ConsumeKeyEvent(evt);
                return;
            }

            var mods = ShortcutModifiers.None;
            if (evt.altKey)    mods |= ShortcutModifiers.Alt;
            if (evt.shiftKey)  mods |= ShortcutModifiers.Shift;
            if (evt.actionKey) mods |= ShortcutModifiers.Action; // Ctrl(Win/Linux) / Cmd(macOS)

            _captured = new KeyCombination(kc, mods);
            _hasCaptured = true;
            _conflictName = ShortcutActions.FindConflictDisplayName(_capturingId, _captured);

            ConsumeKeyEvent(evt);
            Rebuild();
        }

        private static void ConsumeKeyEvent(KeyDownEvent evt)
        {
            evt.StopImmediatePropagation();
        }

        private void OnRootPointerDown(PointerDownEvent evt)
        {
            if (_capturingId == null || _capturingRow == null) return;
            if (evt.target is VisualElement ve && IsDescendantOf(ve, _capturingRow)) return;
            CancelCapture();
        }

        private void ApplyCapture(ShortcutCommand cmd)
        {
            if (!_hasCaptured || _conflictName != null) return;
            try
            {
                ShortcutManager.instance.RebindShortcut(cmd.Id, new ShortcutBinding(_captured));
            }
            catch (InvalidOperationException)
            {
                ShowProfileReadonlyDialog(); // Active profile is read-only
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"RebindShortcut('{cmd.Id}') failed: {ex.Message}");
            }
            CancelCapture();
        }

        private void ClearBinding(ShortcutCommand cmd)
        {
            try
            {
                ShortcutManager.instance.ClearShortcutOverride(cmd.Id);
            }
            catch (InvalidOperationException)
            {
                ShowProfileReadonlyDialog();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"ClearShortcutOverride('{cmd.Id}') failed: {ex.Message}");
            }
            Rebuild();
        }

        private void CancelCapture()
        {
            _capturingId = null;
            _hasCaptured = false;
            _conflictName = null;
            _capturingRow = null;
            Rebuild();
        }

        // ── Helpers ────────────────────────────────────────────────────

        /// <summary>Current binding text; when there is no binding, <paramref name="unset"/>=true and the localized "not set" is returned.</summary>
        private static string CurrentBindingText(string id, out bool unset)
        {
            unset = true;
            try
            {
                var binding = ShortcutManager.instance.GetShortcutBinding(id);
                var seq = binding.keyCombinationSequence;
                if (seq != null && seq.Any())
                {
                    unset = false;
                    return binding.ToString();
                }
            }
            catch { /* binding lookup exception -> treat as unset */ }
            return SkillsLocalization.Get("shortcut_not_set");
        }

        /// <summary>Whether this is a pure modifier or no key — these don't form a bindable combination.</summary>
        private static bool IsModifierOrNone(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None:
                case KeyCode.LeftShift:   case KeyCode.RightShift:
                case KeyCode.LeftControl: case KeyCode.RightControl:
                case KeyCode.LeftAlt:     case KeyCode.RightAlt:    case KeyCode.AltGr:
                case KeyCode.LeftCommand: case KeyCode.RightCommand:   // == LeftApple / RightApple
                case KeyCode.LeftWindows: case KeyCode.RightWindows:
                case KeyCode.Menu:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDescendantOf(VisualElement node, VisualElement ancestor)
        {
            for (var p = node; p != null; p = p.parent)
                if (p == ancestor) return true;
            return false;
        }

        private static void ShowProfileReadonlyDialog()
        {
            EditorUtility.DisplayDialog(
                SkillsLocalization.Get("shortcut_section_title"),
                SkillsLocalization.Get("shortcut_profile_readonly"),
                SkillsLocalization.Get("dialog_ok"));
        }
    }
}

// Producer:Betsy
