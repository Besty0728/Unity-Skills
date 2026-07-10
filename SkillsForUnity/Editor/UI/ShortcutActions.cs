using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills 面板快捷键命令注册 + 供设置 UI 取用的命令清单。
    ///
    /// 每个命令用 Unity 官方 <see cref="ShortcutAttribute"/> 注册，但不提供出厂默认键。
    /// 用户在设置抽屉 Shortcuts 节自行绑定；持久化由 ShortcutManager 的 profile
    /// 自管（不写 EditorPrefs）。
    ///
    /// 新增一个可绑定面板 = 加一个 [Shortcut] 静态方法 + 在 <see cref="Commands"/> 追加一条。
    /// </summary>
    internal static class ShortcutActions
    {
        // Shortcut ID —— 用 "UnitySkills/..." 前缀在 Edit ▸ Shortcuts 里归组显示。
        // const 以便直接用作 [Shortcut] 特性实参。
        public const string OpenMainPanelId = "UnitySkills/Open Main Panel";
        public const string OpenAuditLogId  = "UnitySkills/Open Audit Log";

        [Shortcut(OpenMainPanelId)]
        private static void OpenMainPanel() => UnitySkillsWindow.ShowWindow();

        [Shortcut(OpenAuditLogId)]
        private static void OpenAuditLog() => UnitySkillsAuditWindow.ShowWindow();

        /// <summary>
        /// 设置 UI 逐行渲染的命令清单，顺序即 UI 顺序。
        /// LocKey 走 <see cref="SkillsLocalization"/> 双表；新面板命令按同样格式追加。
        /// </summary>
        public static readonly IReadOnlyList<ShortcutCommand> Commands = new List<ShortcutCommand>
        {
            new ShortcutCommand(OpenMainPanelId, "shortcut_cmd_open_main"),
            new ShortcutCommand(OpenAuditLogId,  "shortcut_cmd_open_audit"),
        };

        /// <summary>
        /// 遍历 ShortcutManager 全部已注册 shortcut，找出与候选组合冲突的那一个并返回其展示名；
        /// 无冲突返回 null。比对用纯静态 <see cref="ShortcutConflictUtil"/>，本方法只负责枚举取值
        /// （不可纯化的运行时部分）。UnitySkills 自家命令之间同样参与检测。
        /// </summary>
        /// <param name="excludeId">排除的 shortcut id（正在改绑的命令自身，避免自我冲突）。</param>
        /// <param name="candidate">候选单键组合。</param>
        public static string FindConflictDisplayName(string excludeId, KeyCombination candidate)
        {
            var candidateSeq = new[] { candidate };
            var mgr = ShortcutManager.instance;
            if (mgr == null) return null;

            foreach (var id in mgr.GetAvailableShortcutIds())
            {
                if (string.Equals(id, excludeId, StringComparison.Ordinal)) continue;

                List<KeyCombination> existing;
                try { existing = mgr.GetShortcutBinding(id).keyCombinationSequence?.ToList(); }
                catch { continue; } // 个别 id 取绑定异常时跳过，不阻断整体检测

                if (ShortcutConflictUtil.SequencesConflict(candidateSeq, existing))
                    return DisplayNameForId(id);
            }
            return null;
        }

        /// <summary>UnitySkills 自家命令 → 本地化展示名；其它（Unity 内建 / 第三方）→ 原始 id。</summary>
        public static string DisplayNameForId(string id)
        {
            foreach (var cmd in Commands)
                if (string.Equals(cmd.Id, id, StringComparison.Ordinal))
                    return SkillsLocalization.Get(cmd.LocKey);
            return id;
        }
    }

    /// <summary>设置 UI 用的命令元数据：shortcut id + 展示名本地化 key。</summary>
    internal sealed class ShortcutCommand
    {
        public readonly string Id;
        public readonly string LocKey;

        public ShortcutCommand(string id, string locKey)
        {
            Id = id;
            LocKey = locKey;
        }
    }

    /// <summary>
    /// 快捷键组合比对纯逻辑（不触碰 ShortcutManager 运行时，可 EditMode 单测）。
    ///
    /// "冲突" = 两个绑定的 KeyCombination 序列逐项相等。空绑定（长度 0）永不与任何绑定冲突，
    /// 保证"未设置"的命令彼此不误报，也不会与出厂未绑定的内建命令冲突。
    /// </summary>
    public static class ShortcutConflictUtil
    {
        /// <summary>单个组合相等：非修饰键 keyCode 与修饰键集合都相同。</summary>
        public static bool CombinationsEqual(KeyCombination a, KeyCombination b)
            => a.keyCode == b.keyCode && a.modifiers == b.modifiers;

        /// <summary>
        /// 两个组合序列是否冲突。任一为 null/空 → 不冲突；长度不同 → 不冲突；逐项相等 → 冲突。
        /// </summary>
        public static bool SequencesConflict(
            IReadOnlyList<KeyCombination> a, IReadOnlyList<KeyCombination> b)
        {
            if (a == null || b == null) return false;
            if (a.Count == 0 || b.Count == 0) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!CombinationsEqual(a[i], b[i])) return false;
            return true;
        }
    }
}
