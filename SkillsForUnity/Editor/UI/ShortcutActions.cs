using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// UnitySkills panel shortcut command registration + the command list consumed by the settings UI.
    ///
    /// Each command is registered with Unity's official <see cref="ShortcutAttribute"/>, but ships no
    /// default key. The user binds it in the settings drawer's Shortcuts section; persistence is
    /// self-managed by ShortcutManager's profile (no EditorPrefs writes).
    ///
    /// To add a new bindable panel = add one [Shortcut] static method + append one entry in <see cref="Commands"/>.
    /// </summary>
    internal static class ShortcutActions
    {
        // Shortcut ID — grouped for display in Edit ▸ Shortcuts under the "UnitySkills/..." prefix.
        // const so it can be used directly as a [Shortcut] attribute argument.
        public const string OpenMainPanelId = "UnitySkills/Open Main Panel";
        public const string OpenAuditLogId  = "UnitySkills/Open Audit Log";
        public const string OpenUnityCliId  = "UnitySkills/Open Unity CLI Setup";

        [Shortcut(OpenMainPanelId)]
        private static void OpenMainPanel() => UnitySkillsWindow.ShowWindow();

        [Shortcut(OpenAuditLogId)]
        private static void OpenAuditLog() => UnitySkillsAuditWindow.ShowWindow();

        [Shortcut(OpenUnityCliId)]
        private static void OpenUnityCli() => UnityCliWindow.ShowWindow();

        /// <summary>
        /// The command list rendered row by row by the settings UI, in UI order.
        /// LocKey goes through the <see cref="SkillsLocalization"/> pair of tables; append new panel commands in the same format.
        /// </summary>
        public static readonly IReadOnlyList<ShortcutCommand> Commands = new List<ShortcutCommand>
        {
            new ShortcutCommand(OpenMainPanelId, "shortcut_cmd_open_main"),
            new ShortcutCommand(OpenAuditLogId,  "shortcut_cmd_open_audit"),
            new ShortcutCommand(OpenUnityCliId,  "shortcut_cmd_open_cli"),
        };

        /// <summary>
        /// Walks every shortcut registered with ShortcutManager, finds the one that conflicts with the candidate
        /// combination, and returns its display name (null if none). Comparison uses the pure-static
        /// <see cref="ShortcutConflictUtil"/>; this method only enumerates values — UnitySkills's own commands participate too.
        /// </summary>
        /// <param name="excludeId">The shortcut id to exclude (the command currently being rebound itself, to avoid self-conflict).</param>
        /// <param name="candidate">The candidate single-key combination.</param>
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
                catch { continue; } // Skip individual ids whose binding lookup throws, without blocking the overall check

                if (ShortcutConflictUtil.SequencesConflict(candidateSeq, existing))
                    return DisplayNameForId(id);
            }
            return null;
        }

        /// <summary>UnitySkills's own commands -> localized display name; anything else (Unity built-in / third-party) -> the raw id.</summary>
        public static string DisplayNameForId(string id)
        {
            foreach (var cmd in Commands)
                if (string.Equals(cmd.Id, id, StringComparison.Ordinal))
                    return SkillsLocalization.Get(cmd.LocKey);
            return id;
        }
    }

    /// <summary>Command metadata for the settings UI: shortcut id + localization key for the display name.</summary>
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
    /// Pure logic for comparing key combinations (never touches the ShortcutManager runtime, unit-testable in EditMode).
    ///
    /// "Conflict" = two bound KeyCombination sequences are equal item by item. An empty binding (length 0)
    /// never conflicts with any binding, so "unset" commands never false-positive against each other or a factory-unbound built-in command.
    /// </summary>
    public static class ShortcutConflictUtil
    {
        public static bool CombinationsEqual(KeyCombination a, KeyCombination b)
            => a.keyCode == b.keyCode && a.modifiers == b.modifiers;

        /// <summary>
        /// Whether two combination sequences conflict. Either being null/empty -> no conflict; different lengths -> no conflict; equal item by item -> conflict.
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

// Producer:Betsy
