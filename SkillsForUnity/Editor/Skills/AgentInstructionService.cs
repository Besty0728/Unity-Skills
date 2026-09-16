using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnitySkills
{
    /// <summary>
    /// Optional "use Unity Skills" guide line injected into each AI tool's root instruction file
    /// (CLAUDE.md for Claude Code, AGENTS.md for most others, GEMINI.md for Antigravity's global scope).
    /// Off by default; the settings drawer toggle flips <see cref="Enabled"/>.
    ///
    /// The injected content is one plain sentence with no marker comments, so it costs the user as
    /// few context tokens as possible. Detection matches the exact known wording(s): upsert is
    /// idempotent, toggle-off removes exactly our line (plus the blank separator line we added) and
    /// deletes the file when it held nothing else. One trade-off of marker-less detection: a line the
    /// user hand-edited is no longer recognized as ours and is left alone. Wording from pre-release
    /// dev iterations (which shipped wrapped in <!-- unity-skills:begin/end --> markers) is still
    /// recognized, so those blocks get replaced by the current line or removed cleanly.
    ///
    /// Verified per-tool instruction files (2026-09, official docs):
    ///   Claude Code  project: CLAUDE.md                 global: ~/.claude/CLAUDE.md
    ///   Codex        project: AGENTS.md                 global: $CODEX_HOME/AGENTS.md (~/.codex)
    ///   Antigravity  project: AGENTS.md                 global: ~/.gemini/GEMINI.md (shared with Gemini CLI)
    ///   Cursor       project: AGENTS.md                 global: none reliable (~/.cursor/rules is not auto-loaded)
    ///   OpenCode     project: AGENTS.md                 global: ~/.config/opencode/AGENTS.md
    ///   Kimi Code    project: AGENTS.md                 global: $KIMI_CODE_HOME/AGENTS.md (~/.kimi-code)
    /// </summary>
    public static class AgentInstructionService
    {
        internal const string AgentClaudeCode = "ClaudeCode";
        internal const string AgentCodex = "Codex";
        internal const string AgentAntigravity = "Antigravity";
        internal const string AgentCursor = "Cursor";
        internal const string AgentOpenCode = "OpenCode";
        internal const string AgentKimiCode = "KimiCode";

        internal static readonly string[] KnownAgentIds =
        {
            AgentClaudeCode, AgentCodex, AgentAntigravity, AgentCursor, AgentOpenCode, AgentKimiCode
        };

        // Single short English line on purpose: instruction files outlive any panel language choice,
        // English steering is the most reliable across models, and brevity costs fewer context tokens.
        // Path-free because the project-scope AGENTS.md is shared by five tools with different skill
        // directories, and global-scope files serve every project. Its only job is to bias skill
        // selection; once picked, the tool's own skill system loads SKILL.md for the details.
        internal const string InstructionLine = "When working on a Unity project, use Unity Skills.";

        // Pre-release dev wordings, which shipped wrapped in marker comments. Kept so installs from
        // that window get migrated (upsert replaces them) instead of leaving orphaned text behind.
        private static readonly string[] LegacyLines =
        {
            "When working on this Unity project, prefer the \"unity-skills\" skill and follow its SKILL.md for Editor operations.",
            "When working on this Unity project, prefer using the installed Unity Skills (\"unity-skills\" skill, REST API on localhost:8090+) for Editor operations over manual scene/asset edits."
        };
        private const string LegacyBeginMarker = "<!-- unity-skills:begin -->";
        private const string LegacyEndMarker = "<!-- unity-skills:end -->";

        private static string _prefEnabled;

        // Same key pattern as SkillInstallSyncService: includes InstanceId, so it's isolated per project.
        internal static string PrefEnabled =>
            _prefEnabled ??= $"UnitySkills_{RegistryService.InstanceId}_AgentRootInstruction";

        /// <summary>Whether installs/auto-sync also write the guide line into root instruction files. Off by default.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, false);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        /// <summary>Test-only: redirects project-scope paths to a temp directory.</summary>
        internal static string ProjectRootOverride;

        private static string ProjectRoot =>
            ProjectRootOverride ?? Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// Resolves the instruction file for one tool at one scope. Returns null when the tool has no
        /// reliable file at that scope (Cursor global) or the agent id is unknown (e.g. Custom installs).
        /// </summary>
        internal static string GetInstructionFilePath(string agentId, bool global)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (agentId)
            {
                case AgentClaudeCode:
                    return global
                        ? Path.Combine(home, ".claude", "CLAUDE.md")
                        : Path.Combine(ProjectRoot, "CLAUDE.md");
                case AgentCodex:
                    return global
                        ? Path.Combine(ResolveEnvHome("CODEX_HOME", ".codex"), "AGENTS.md")
                        : Path.Combine(ProjectRoot, "AGENTS.md");
                case AgentAntigravity:
                    return global
                        ? Path.Combine(home, ".gemini", "GEMINI.md")
                        : Path.Combine(ProjectRoot, "AGENTS.md");
                case AgentCursor:
                    // No dependable user-level instruction file: ~/.cursor/rules is documented but
                    // widely reported not to auto-load, and global AGENTS.md is unsupported.
                    return global ? null : Path.Combine(ProjectRoot, "AGENTS.md");
                case AgentOpenCode:
                    return global
                        ? Path.Combine(home, ".config", "opencode", "AGENTS.md")
                        : Path.Combine(ProjectRoot, "AGENTS.md");
                case AgentKimiCode:
                    return global
                        ? Path.Combine(SkillInstaller.KimiCodeHome, "AGENTS.md")
                        : Path.Combine(ProjectRoot, "AGENTS.md");
                default:
                    return null;
            }
        }

        // Mirrors SkillInstaller.KimiCodeHome: honors the env var, with manual "~" expansion.
        private static string ResolveEnvHome(string envVar, string defaultFolderName)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configured = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(configured))
                return Path.Combine(home, defaultFolderName);

            configured = configured.Trim();
            if (configured == "~")
                return home;
            if (configured.StartsWith("~/", StringComparison.Ordinal) || configured.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(home, configured.Substring(2));

            return configured;
        }

        // ===== Entry points =====

        /// <summary>Install-time hook: writes the line only when the feature is on. Never throws.</summary>
        public static void UpsertIfEnabled(string filePath)
        {
            if (!Enabled || string.IsNullOrEmpty(filePath))
                return;

            try
            {
                if (UpsertBlock(filePath))
                    SkillsLogger.Log("Wrote Unity Skills guide line to: " + filePath);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("Failed to write guide line to " + filePath + ": " + ex.Message);
            }
        }

        /// <summary>Toggle-on path: applies the line to every already-installed target, deduped by file path.</summary>
        public static void ApplyToAllInstalled()
        {
            try
            {
                var written = 0;
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in SkillInstaller.EnumerateTargets())
                {
                    if (target == null || string.IsNullOrEmpty(target.AgentId))
                        continue;

                    bool installed;
                    try { installed = target.IsInstalled != null && target.IsInstalled(); }
                    catch { continue; }
                    if (!installed)
                        continue;

                    var path = GetInstructionFilePath(target.AgentId, target.IsGlobal);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    // Five tools share the project-scope AGENTS.md; write each physical file once.
                    if (!seenPaths.Add(Path.GetFullPath(path)))
                        continue;

                    if (UpsertBlock(path))
                        written++;
                }

                if (written > 0)
                    SkillsLogger.Log($"Agent instruction guide line written to {written} file(s).");
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("ApplyToAllInstalled aborted: " + ex.Message);
            }
        }

        /// <summary>Toggle-off path: removes the line from every known instruction file.</summary>
        public static void RemoveAll()
        {
            try
            {
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var agentId in KnownAgentIds)
                {
                    foreach (var global in new[] { false, true })
                    {
                        var path = GetInstructionFilePath(agentId, global);
                        if (string.IsNullOrEmpty(path))
                            continue;
                        if (!seenPaths.Add(Path.GetFullPath(path)))
                            continue;
                        RemoveBlock(path);
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning("RemoveAll aborted: " + ex.Message);
            }
        }

        // ===== Core file ops (explicit path, kept pure for tests) =====

        /// <summary>
        /// Creates the file with the line when missing, appends it after a blank separator otherwise.
        /// If the current line is already present, only stale dev-iteration lines are stripped;
        /// a legacy marker-wrapped block is replaced in place by the bare current line. Returns true
        /// only when the file actually changed. Newline style of a pre-existing CRLF file is preserved.
        /// </summary>
        internal static bool UpsertBlock(string filePath)
        {
            if (!File.Exists(filePath))
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, InstructionLine + "\n", SkillsCommon.Utf8NoBom);
                return true;
            }

            var text = File.ReadAllText(filePath, Encoding.UTF8);
            bool crlf = text.Contains("\r\n");
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            string result;
            if (ContainsOurLine(lines))
            {
                // Our line exists (current or legacy wording): migrate in place — the last
                // occurrence becomes the current line, every other copy of ours is dropped,
                // user content untouched. A lone current line means nothing to do.
                var list = new List<string>(lines);
                bool seenOur = false, changed = false;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var trimmed = list[i].Trim();
                    if (!IsOurLine(trimmed))
                        continue;
                    if (!seenOur)
                    {
                        seenOur = true;
                        if (trimmed != InstructionLine)
                        {
                            list[i] = InstructionLine;
                            changed = true;
                        }
                    }
                    else
                    {
                        list.RemoveAt(i);
                        changed = true;
                    }
                }
                if (!changed)
                    return false;
                result = string.Join("\n", list);
            }
            else
            {
                var body = normalized.TrimEnd();
                result = body.Length == 0 ? InstructionLine + "\n" : body + "\n\n" + InstructionLine + "\n";
            }

            if (crlf)
                result = result.Replace("\n", "\r\n");
            File.WriteAllText(filePath, result, SkillsCommon.Utf8NoBom);
            return true;
        }

        /// <summary>
        /// Removes our line (current or legacy wording, plus any legacy marker lines) and the blank
        /// separator line we add before it. Deletes the file when nothing else remains. Returns false
        /// when the file is missing or holds nothing of ours.
        /// </summary>
        internal static bool RemoveBlock(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            var text = File.ReadAllText(filePath, Encoding.UTF8);
            bool crlf = text.Contains("\r\n");
            var lines = text.Replace("\r\n", "\n").Split('\n');

            int firstIdx = FirstOurLineIndex(lines);
            if (firstIdx < 0)
                return false;

            var list = new List<string>();
            foreach (var line in lines)
            {
                if (!IsOurLine(line.Trim()))
                    list.Add(line);
            }
            // Drop the blank separator line UpsertBlock inserts before our line. Everything before
            // firstIdx survived, so the separator candidate sits at the same index in the new list.
            if (firstIdx > 0 && firstIdx - 1 < list.Count && string.IsNullOrWhiteSpace(list[firstIdx - 1]))
                list.RemoveAt(firstIdx - 1);

            var joined = string.Join("\n", list);
            if (joined.Trim().Length == 0)
            {
                File.Delete(filePath);
                return true;
            }

            var result = joined.TrimEnd('\n') + "\n";
            if (crlf)
                result = result.Replace("\n", "\r\n");
            File.WriteAllText(filePath, result, SkillsCommon.Utf8NoBom);
            return true;
        }

        private static bool IsOurLine(string trimmedLine)
        {
            if (trimmedLine == InstructionLine ||
                trimmedLine == LegacyBeginMarker ||
                trimmedLine == LegacyEndMarker)
                return true;
            foreach (var legacy in LegacyLines)
            {
                if (trimmedLine == legacy)
                    return true;
            }
            return false;
        }

        private static bool ContainsOurLine(string[] lines) => FirstOurLineIndex(lines) >= 0;

        private static int FirstOurLineIndex(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsOurLine(lines[i].Trim()))
                    return i;
            }
            return -1;
        }
    }
}

// Producer:Betsy
