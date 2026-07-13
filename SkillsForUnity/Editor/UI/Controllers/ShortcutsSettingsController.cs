using System;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Settings Drawer 的 "Shortcuts" 节控制器。
    ///
    /// 每个 UnitySkills 面板命令一行：展示名 + 当前绑定文字 + [修改] / [清除]。
    /// 点 [修改] 进入捕获态：在抽屉根上以 TrickleDown 捕获 <see cref="KeyDownEvent"/>，读
    /// keyCode + 修饰键，实时冲突检测；无冲突可 [应用]（<c>RebindShortcut</c>），有冲突红字提示并
    /// 禁用应用；Esc / [取消] / 点击行外均退出。绑定持久化交给 ShortcutManager 的 profile
    /// （不写 EditorPrefs）。样式全走 USS class。
    ///
    /// 与主窗口 issue #44 的关系：本节只在用户事件（点击 / 按键）与显式 Rebuild 时 mutate 视觉树，
    /// 不挂周期性 tick，因此无需 EditorUiScheduler 兜底。
    /// </summary>
    public class ShortcutsSettingsController
    {
        private readonly VisualElement _root;   // drawer container：capture 注册与命中判定的边界
        private readonly Label _title;
        private readonly Label _hint;
        private readonly VisualElement _list;

        // 捕获态：_capturingId==null 表示未捕获。
        private string _capturingId;
        private KeyCombination _captured;
        private bool _hasCaptured;              // 是否已按下有效组合（决定行显示 prompt 还是 preview）
        private string _conflictName;           // 非 null = 与该命令冲突，拒绝保存
        private VisualElement _capturingRow;    // 命中判定：pointer down 落在此行之外即取消

        public ShortcutsSettingsController(VisualElement drawerContainer)
        {
            _root  = drawerContainer;
            _title = drawerContainer.Q<Label>("group-shortcuts-title");
            _hint  = drawerContainer.Q<Label>("shortcuts-hint");
            _list  = drawerContainer.Q<VisualElement>("shortcuts-list");

            // 捕获态按键：抽屉根 TrickleDown，先于焦点子节点拿到 KeyDown。
            _root.RegisterCallback<KeyDownEvent>(OnCaptureKeyDown, TrickleDown.TrickleDown);
            // 捕获态点击行外 = 取消（含点到别的设置项 / 遮罩）。
            _root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);

            RefreshLocalization();
        }

        /// <summary>语言切换 / 首次组装时刷新静态文案并重建行。</summary>
        public void RefreshLocalization()
        {
            if (_title != null) _title.text = SkillsLocalization.Get("shortcut_section_title");
            if (_hint  != null) _hint.text  = SkillsLocalization.Get("shortcut_section_hint");
            Rebuild();
        }

        /// <summary>抽屉每次打开时调用：拉取最新绑定重建，覆盖 Edit ▸ Shortcuts 里的外部改动。</summary>
        public void Refresh() => Rebuild();

        // ── 渲染 ────────────────────────────────────────────────────

        private void Rebuild()
        {
            if (_list == null) return;
            _capturingRow = null;
            _list.Clear();
            foreach (var cmd in ShortcutActions.Commands)
                _list.Add(BuildRow(cmd));

            // 捕获行需焦点才能收到 KeyDown；延后一帧 Focus 规避 attach 时机问题（对齐 drawer-mask 的做法）。
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
            clearBtn.SetEnabled(!unset); // 无绑定时清除无意义
            row.Add(clearBtn);

            return row;
        }

        private VisualElement BuildCaptureRow(ShortcutCommand cmd)
        {
            // 纵向容器：顶部横行 + 可选冲突红字。容器自身 focusable，作为 KeyDown 焦点宿主与命中边界。
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
                applyBtn.SetEnabled(_conflictName == null); // 有冲突拒绝保存
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

        // ── 捕获态机 ────────────────────────────────────────────────

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
            // 纯修饰键 / 无键：不算一次组合，吞掉事件等待真正的键。
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
            evt.PreventDefault();
        }

        private void OnRootPointerDown(PointerDownEvent evt)
        {
            if (_capturingId == null || _capturingRow == null) return;
            if (evt.target is VisualElement ve && IsDescendantOf(ve, _capturingRow)) return; // 点在捕获行内
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
                ShowProfileReadonlyDialog(); // 活动 profile 只读
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

        // ── 辅助 ────────────────────────────────────────────────────

        /// <summary>当前绑定文字；无绑定时 <paramref name="unset"/>=true 并返回本地化"未设置"。</summary>
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
            catch { /* 取绑定异常 → 视作未设置 */ }
            return SkillsLocalization.Get("shortcut_not_set");
        }

        /// <summary>是否为纯修饰键或无键——这些不构成一次可绑定组合。</summary>
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
                "OK");
        }
    }
}

// Producer:Betsy
