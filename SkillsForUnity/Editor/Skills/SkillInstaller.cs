using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// One-click skill installer for mainstream AI IDEs: Claude Code, Antigravity, Codex, Cursor, OpenCode, Kimi Code.
    /// </summary>
    public static class SkillInstaller
    {
        // Claude Code path: Claude accepts any folder name
        public static string ClaudeProjectPath => Path.Combine(Application.dataPath, "..", ".claude", "skills", "unity-skills");
        public static string ClaudeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "unity-skills");

        // Antigravity path - https://antigravity.google/docs/skills
        // The workspace path is shared with Codex via .agents/skills (the open Agent Skills standard)
        public static string AntigravityProjectPath => Path.Combine(Application.dataPath, "..", ".agents", "skills", "unity-skills");
        public static string AntigravityGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "skills", "unity-skills");

        // Codex path - https://developers.openai.com/codex/skills
        // The workspace path is shared with Antigravity via .agents/skills (the open Agent Skills standard)
        public static string CodexProjectPath => Path.Combine(Application.dataPath, "..", ".agents", "skills", "unity-skills");
        public static string CodexGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills", "unity-skills");

        // Cursor path - https://cursor.com/docs/context/skills
        public static string CursorProjectPath => Path.Combine(Application.dataPath, "..", ".cursor", "skills", "unity-skills");
        public static string CursorGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "skills", "unity-skills");

        // OpenCode path - https://opencode.ai/docs/skills
        // The workspace path is shared via .agents/skills (the open Agent Skills standard)
        public static string OpenCodeProjectPath => Path.Combine(Application.dataPath, "..", ".opencode", "skills", "unity-skills");
        public static string OpenCodeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "skills", "unity-skills");

        // Kimi Code path - https://www.kimi.com/code/docs/kimi-code-cli/customization/skills.html
        // Kimi Code CLI scans four scopes; this points only at its own dedicated directory (not the
        // .agents/skills shared with Codex/Antigravity), so each tool's install state and uninstall
        // stay independent of each other. A global Codex copy installed under ~/.agents/skills will still be picked up incidentally by Kimi Code.
        // The user-level root takes this value if the Editor inherits KIMI_CODE_HOME, otherwise ~/.kimi-code.
        public static string KimiCodeProjectPath => Path.Combine(Application.dataPath, "..", ".kimi-code", "skills", "unity-skills");
        public static string KimiCodeGlobalPath => Path.Combine(KimiCodeHome, "skills", "unity-skills");

        /// <summary>
        /// Resolves $KIMI_CODE_HOME (default ~/.kimi-code). Only visible when Unity was launched
        /// from a shell that exported this variable; otherwise falls back to the documented default.
        /// Internal so AgentInstructionService can point at the same home for the AGENTS.md guide line.
        /// </summary>
        internal static string KimiCodeHome
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
                if (string.IsNullOrWhiteSpace(configured))
                    return Path.Combine(home, ".kimi-code");

                configured = configured.Trim();
                // A shell doesn't expand a leading "~" when it's inside quotes, so it's handled here manually,
                // otherwise a directory literally named "~" would get created next to the project.
                if (configured == "~")
                    return home;
                if (configured.StartsWith("~/", StringComparison.Ordinal) || configured.StartsWith("~\\", StringComparison.Ordinal))
                    return Path.Combine(home, configured.Substring(2));

                return configured;
            }
        }

        public static bool IsClaudeProjectInstalled => Directory.Exists(ClaudeProjectPath) && File.Exists(Path.Combine(ClaudeProjectPath, "SKILL.md"));
        public static bool IsClaudeGlobalInstalled => Directory.Exists(ClaudeGlobalPath) && File.Exists(Path.Combine(ClaudeGlobalPath, "SKILL.md"));
        public static bool IsAntigravityProjectInstalled => Directory.Exists(AntigravityProjectPath) && File.Exists(Path.Combine(AntigravityProjectPath, "SKILL.md"));
        public static bool IsAntigravityGlobalInstalled => Directory.Exists(AntigravityGlobalPath) && File.Exists(Path.Combine(AntigravityGlobalPath, "SKILL.md"));
        public static bool IsCodexProjectInstalled => Directory.Exists(CodexProjectPath) && File.Exists(Path.Combine(CodexProjectPath, "SKILL.md"));
        public static bool IsCodexGlobalInstalled => Directory.Exists(CodexGlobalPath) && File.Exists(Path.Combine(CodexGlobalPath, "SKILL.md"));
        public static bool IsCursorProjectInstalled => Directory.Exists(CursorProjectPath) && File.Exists(Path.Combine(CursorProjectPath, "SKILL.md"));
        public static bool IsCursorGlobalInstalled => Directory.Exists(CursorGlobalPath) && File.Exists(Path.Combine(CursorGlobalPath, "SKILL.md"));
        public static bool IsOpenCodeProjectInstalled => Directory.Exists(OpenCodeProjectPath) && File.Exists(Path.Combine(OpenCodeProjectPath, "SKILL.md"));
        public static bool IsOpenCodeGlobalInstalled => Directory.Exists(OpenCodeGlobalPath) && File.Exists(Path.Combine(OpenCodeGlobalPath, "SKILL.md"));
        public static bool IsKimiCodeProjectInstalled => Directory.Exists(KimiCodeProjectPath) && File.Exists(Path.Combine(KimiCodeProjectPath, "SKILL.md"));
        public static bool IsKimiCodeGlobalInstalled => Directory.Exists(KimiCodeGlobalPath) && File.Exists(Path.Combine(KimiCodeGlobalPath, "SKILL.md"));

        public static (bool success, string message) InstallClaude(bool global)
        {
            try
            {
                var targetPath = global ? ClaudeGlobalPath : ClaudeProjectPath;
                return InstallSkill(targetPath, "Claude Code", AgentInstructionService.AgentClaudeCode,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentClaudeCode, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallAntigravity(bool global)
        {
            try
            {
                var targetPath = global ? AntigravityGlobalPath : AntigravityProjectPath;
                return InstallSkill(targetPath, "Antigravity", AgentInstructionService.AgentAntigravity,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentAntigravity, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallClaude(bool global)
        {
            try
            {
                var targetPath = global ? ClaudeGlobalPath : ClaudeProjectPath;
                return UninstallSkill(targetPath, "Claude Code");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallAntigravity(bool global)
        {
            try
            {
                var targetPath = global ? AntigravityGlobalPath : AntigravityProjectPath;
                return UninstallSkill(targetPath, "Antigravity");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallCodex(bool global)
        {
            try
            {
                var targetPath = global ? CodexGlobalPath : CodexProjectPath;
                return InstallSkill(targetPath, "Codex", AgentInstructionService.AgentCodex,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentCodex, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallCodex(bool global)
        {
            try
            {
                var targetPath = global ? CodexGlobalPath : CodexProjectPath;
                return UninstallSkill(targetPath, "Codex");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallCursor(bool global)
        {
            try
            {
                var targetPath = global ? CursorGlobalPath : CursorProjectPath;
                return InstallSkill(targetPath, "Cursor", AgentInstructionService.AgentCursor,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentCursor, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallCursor(bool global)
        {
            try
            {
                var targetPath = global ? CursorGlobalPath : CursorProjectPath;
                return UninstallSkill(targetPath, "Cursor");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallOpenCode(bool global)
        {
            try
            {
                var targetPath = global ? OpenCodeGlobalPath : OpenCodeProjectPath;
                return InstallSkill(targetPath, "OpenCode", AgentInstructionService.AgentOpenCode,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentOpenCode, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallOpenCode(bool global)
        {
            try
            {
                var targetPath = global ? OpenCodeGlobalPath : OpenCodeProjectPath;
                return UninstallSkill(targetPath, "OpenCode");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallKimiCode(bool global)
        {
            try
            {
                var targetPath = global ? KimiCodeGlobalPath : KimiCodeProjectPath;
                return InstallSkill(targetPath, "Kimi Code", AgentInstructionService.AgentKimiCode,
                    AgentInstructionService.GetInstructionFilePath(AgentInstructionService.AgentKimiCode, global));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallKimiCode(bool global)
        {
            try
            {
                var targetPath = global ? KimiCodeGlobalPath : KimiCodeProjectPath;
                return UninstallSkill(targetPath, "Kimi Code");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Runtime description of an install target (tool x scope). The panel and the auto-sync
        /// share this single detect/install entry point, avoiding two separate copies of the copy logic drifting apart.
        /// </summary>
        public sealed class InstallTarget
        {
            public string DisplayName;
            public string Path;
            /// <summary>Agent id understood by <see cref="AgentInstructionService"/> (e.g. "ClaudeCode").</summary>
            public string AgentId;
            /// <summary>Project scope installs sit under the project root; global ones under the user's home.</summary>
            public bool IsGlobal;
            public Func<bool> IsInstalled;
            /// <summary>Version stamped on the installed copy, or null when it can't be determined (see <see cref="ReadInstalledVersion"/>).</summary>
            public Func<string> InstalledVersion;
            public Func<(bool success, string message)> Install;
        }

        /// <summary>
        /// Enumerates all built-in install targets (6 tools x project/global scope).
        /// Note that Codex and Antigravity's project-level paths are both .agents/skills (a shared directory per the open standard); callers that need to de-duplicate by path
        /// must handle that themselves.
        /// </summary>
        public static IEnumerable<InstallTarget> EnumerateTargets()
        {
            yield return MakeTarget(AgentInstructionService.AgentClaudeCode, false, "Claude Code (Project)", ClaudeProjectPath, () => IsClaudeProjectInstalled, () => InstallClaude(false));
            yield return MakeTarget(AgentInstructionService.AgentClaudeCode, true, "Claude Code (Global)", ClaudeGlobalPath, () => IsClaudeGlobalInstalled, () => InstallClaude(true));
            yield return MakeTarget(AgentInstructionService.AgentCodex, false, "Codex (Project)", CodexProjectPath, () => IsCodexProjectInstalled, () => InstallCodex(false));
            yield return MakeTarget(AgentInstructionService.AgentCodex, true, "Codex (Global)", CodexGlobalPath, () => IsCodexGlobalInstalled, () => InstallCodex(true));
            yield return MakeTarget(AgentInstructionService.AgentAntigravity, false, "Antigravity (Project)", AntigravityProjectPath, () => IsAntigravityProjectInstalled, () => InstallAntigravity(false));
            yield return MakeTarget(AgentInstructionService.AgentAntigravity, true, "Antigravity (Global)", AntigravityGlobalPath, () => IsAntigravityGlobalInstalled, () => InstallAntigravity(true));
            yield return MakeTarget(AgentInstructionService.AgentCursor, false, "Cursor (Project)", CursorProjectPath, () => IsCursorProjectInstalled, () => InstallCursor(false));
            yield return MakeTarget(AgentInstructionService.AgentCursor, true, "Cursor (Global)", CursorGlobalPath, () => IsCursorGlobalInstalled, () => InstallCursor(true));
            yield return MakeTarget(AgentInstructionService.AgentOpenCode, false, "OpenCode (Project)", OpenCodeProjectPath, () => IsOpenCodeProjectInstalled, () => InstallOpenCode(false));
            yield return MakeTarget(AgentInstructionService.AgentOpenCode, true, "OpenCode (Global)", OpenCodeGlobalPath, () => IsOpenCodeGlobalInstalled, () => InstallOpenCode(true));
            yield return MakeTarget(AgentInstructionService.AgentKimiCode, false, "Kimi Code (Project)", KimiCodeProjectPath, () => IsKimiCodeProjectInstalled, () => InstallKimiCode(false));
            yield return MakeTarget(AgentInstructionService.AgentKimiCode, true, "Kimi Code (Global)", KimiCodeGlobalPath, () => IsKimiCodeGlobalInstalled, () => InstallKimiCode(true));
        }

        private static InstallTarget MakeTarget(string agentId, bool isGlobal, string displayName, string path, Func<bool> isInstalled, Func<(bool, string)> install)
        {
            return new InstallTarget
            {
                DisplayName = displayName,
                Path = path,
                AgentId = agentId,
                IsGlobal = isGlobal,
                IsInstalled = isInstalled,
                InstalledVersion = () => ReadInstalledVersion(path),
                Install = install
            };
        }

        /// <summary>How an installed copy's version relates to the current package.</summary>
        public enum InstalledVersionState
        {
            /// <summary>No readable version stamp; treated like an old copy.</summary>
            Unknown,
            Older,
            Current,
            Newer
        }

        /// <summary>
        /// Compares an installed copy's version stamp with the current package version. Both sides must parse as
        /// System.Version; otherwise the result is Unknown. Shared by the panel's Install/Update buttons and the
        /// post-upgrade auto-sync so both apply the same "never downgrade, never re-copy the same version" rule.
        /// </summary>
        public static InstalledVersionState CompareInstalledVersion(string installedVersion, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(installedVersion) ||
                !Version.TryParse(installedVersion.Trim(), out var installed) ||
                !Version.TryParse(currentVersion?.Trim(), out var current))
                return InstalledVersionState.Unknown;

            var order = installed.CompareTo(current);
            return order < 0 ? InstalledVersionState.Older
                 : order > 0 ? InstalledVersionState.Newer
                 : InstalledVersionState.Current;
        }

        private static readonly Regex PyVersionPattern =
            new Regex("^__version__\\s*=\\s*\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// Reads the package version an installed copy was produced from. Primary source is the "version" field
        /// written to scripts/agent_config.json at install time; copies made by older packages lack that field,
        /// so it falls back to the __version__ constant in scripts/unity_skills.py, which every copy ships.
        /// Returns null when neither is readable — callers treat unknown as "old copy".
        /// </summary>
        public static string ReadInstalledVersion(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath))
                return null;

            var scriptsPath = Path.Combine(targetPath, "scripts");
            try
            {
                var configPath = Path.Combine(scriptsPath, "agent_config.json");
                if (File.Exists(configPath))
                {
                    var version = JObject.Parse(File.ReadAllText(configPath, Encoding.UTF8))["version"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(version))
                        return version.Trim();
                }
            }
            catch
            {
                // Corrupt or hand-edited config: fall through to the script constant.
            }

            try
            {
                var scriptPath = Path.Combine(scriptsPath, "unity_skills.py");
                if (File.Exists(scriptPath))
                {
                    var match = PyVersionPattern.Match(File.ReadAllText(scriptPath, Encoding.UTF8));
                    if (match.Success)
                        return match.Groups[1].Value.Trim();
                }
            }
            catch
            {
                // Unreadable script: version unknown.
            }

            return null;
        }

        public static (bool success, string message) InstallCustom(string path, string agentName = "Custom")
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return (false, "Path cannot be empty");

                return InstallSkill(path, "Custom Path", agentName);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // internal: the uninstall regression tests drive this against a temp target directly (tests are
        // InternalsVisibleTo this assembly), so the fixture never has to reach a real install location.
        internal static (bool success, string message) UninstallSkill(string targetPath, string name)
        {
            if (!Directory.Exists(targetPath))
                return (false, $"{name} skill not installed at this location");

            string templateRoot = null;
            try { templateRoot = GetSkillTemplateRoot(); }
            catch (DirectoryNotFoundException)
            {
                // Exotic setup without a resolvable template: fall back to removing the whole
                // destination directory, which may also take files the user added into it.
                SkillsLogger.LogWarning("Uninstalling via full directory delete because the skill template root could not be resolved; user-added files under " + targetPath + " may be removed.");
                Directory.Delete(targetPath, true);
                SkillsLogger.Log("Uninstalled skill from: " + targetPath);
                return (true, targetPath);
            }

            DeleteInstalledTemplateTree(templateRoot, targetPath);
            SkillsLogger.Log("Uninstalled skill from: " + targetPath);
            return (true, targetPath);
        }

        /// <summary>
        /// Removes only the paths the installed template owns: files an earlier template installed that the current
        /// one no longer ships (<see cref="RemoveRetiredFiles"/>), every template file mirrored under
        /// <paramref name="targetPath"/>, the installer-written scripts/agent_config.json and install manifest (not part
        /// of the template), then the now-empty directories of the template tree deepest-first. Files the
        /// user added keep their directories — and the destination root — alive.
        /// </summary>
        private static void DeleteInstalledTemplateTree(string templateRoot, string targetPath)
        {
            var templateFiles = Directory.GetFiles(templateRoot, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(templateRoot, file))
                .Where(relativePath => !ShouldSkipTemplatePath(relativePath))
                .Select(ToManifestPath);
            RemoveRetiredFiles(targetPath, ReadInstallManifest(targetPath), templateFiles);

            var templateDirectories = Directory.GetDirectories(templateRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(templateRoot, path))
                .Where(relativePath => !ShouldSkipTemplatePath(relativePath))
                .ToList();

            var templateDirectoriesSet = new HashSet<string>(
                templateDirectories.Select(NormalizeRelativePath), StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(templateRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(templateRoot, file);
                if (ShouldSkipTemplatePath(relativePath))
                    continue;

                // A template file inside a skipped directory can never match the copy contract; it only
                // occurs when the template itself violates it, and the destination file may be user content.
                string parent = NormalizeRelativePath(Path.GetDirectoryName(relativePath));
                while (!string.IsNullOrEmpty(parent))
                {
                    if (!templateDirectoriesSet.Contains(parent))
                        goto NextFile;
                    parent = NormalizeRelativePath(Path.GetDirectoryName(parent));
                }

                string destination = Path.Combine(targetPath, relativePath);
                if (File.Exists(destination))
                    File.Delete(destination);

                NextFile:;
            }

            // Installer-written version stamp and manifest, skipped from the template copy itself.
            string agentConfig = Path.Combine(targetPath, "scripts", "agent_config.json");
            if (File.Exists(agentConfig))
                File.Delete(agentConfig);
            string manifest = InstallManifestPath(targetPath);
            if (File.Exists(manifest))
                File.Delete(manifest);

            foreach (var relativePath in templateDirectories
                .OrderByDescending(path => path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
                .ThenByDescending(path => path.Length))
            {
                string directory = Path.Combine(targetPath, relativePath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }

            if (!Directory.EnumerateFileSystemEntries(targetPath).Any())
                Directory.Delete(targetPath);
        }

        private static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }

        private static (bool success, string message) InstallSkill(string targetPath, string name, string agentId, string instructionFilePath = null)
        {
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);

            // Must use UTF-8 without BOM: if a BOM (EF BB BF) appears before the leading `---`, some agents refuse to parse the YAML frontmatter.
            var utf8NoBom = SkillsCommon.Utf8NoBom;
            var previousManifest = ReadInstallManifest(targetPath);
            var installed = CopyTemplateDirectory(GetSkillTemplateRoot(), targetPath, utf8NoBom);

            // Write agent config for automatic agent identity detection
            var scriptsPath = Path.Combine(targetPath, "scripts");
            if (!Directory.Exists(scriptsPath))
                Directory.CreateDirectory(scriptsPath);
            // "version" lets SkillInstallSyncService tell whether a copy shared between projects (global scope) is
            // already newer than this project's package, so a lagging project never downgrades it.
            var agentConfig = $"{{\"agentId\": \"{agentId}\", \"version\": \"{SkillsLogger.Version}\", \"installedAt\": \"{DateTime.UtcNow:O}\"}}";
            File.WriteAllText(Path.Combine(scriptsPath, "agent_config.json"), agentConfig, utf8NoBom);

            RemoveRetiredFiles(targetPath, previousManifest, installed.Keys);
            var manifest = new InstallManifest { version = SkillsLogger.Version };
            foreach (var pair in installed.OrderBy(p => p.Key, StringComparer.Ordinal))
                manifest.files[pair.Key] = pair.Value;
            File.WriteAllText(InstallManifestPath(targetPath), JsonConvert.SerializeObject(manifest, Formatting.Indented), utf8NoBom);

            // Optional "prefer Unity Skills" guide line in the tool's root instruction file
            // (CLAUDE.md / AGENTS.md / GEMINI.md). Gated by the feature toggle inside the service.
            if (instructionFilePath != null)
                AgentInstructionService.UpsertIfEnabled(instructionFilePath);

            SkillsLogger.Log($"Installed skill to: {targetPath} (Agent: {agentId})");
            return (true, targetPath);
        }

        private static string GetSkillTemplateRoot()
        {
            string templateRoot;

            // 1. Project root (development / local clone)
            templateRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "unity-skills"));
            if (Directory.Exists(templateRoot))
                return templateRoot;

            // 2. Inside the UPM package (unity-skills~ is the tilde hidden directory shipped with the package)
            PackageRootLocator.TryGet(out var resolvedPath);

            if (!string.IsNullOrEmpty(resolvedPath))
            {
                // Tilde hidden directory inside the package
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "unity-skills~"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;

                // Sibling of the package root (the case of a full-repo clone via git ?path=)
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "..", "unity-skills"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;

                // Subdirectory of the package root
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "unity-skills"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;
            }

            throw new DirectoryNotFoundException(
                $"unity-skills template folder not found. " +
                $"Checked: project root, package path ({resolvedPath ?? "N/A"}). " +
                $"Please reinstall the package.");
        }

        /// <summary>Copies the template and returns each written file's manifest path and content hash.</summary>
        private static Dictionary<string, string> CopyTemplateDirectory(string sourceRoot, string targetRoot, Encoding encoding)
        {
            var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, directory);
                if (ShouldSkipTemplatePath(relativePath))
                    continue;

                Directory.CreateDirectory(Path.Combine(targetRoot, relativePath));
            }

            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRoot, file);
                if (ShouldSkipTemplatePath(relativePath))
                    continue;

                string destination = Path.Combine(targetRoot, relativePath);
                string destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                WriteTemplateFile(file, destination, encoding);
                installed[ToManifestPath(relativePath)] = HashFile(destination);
            }
            return installed;
        }

        private static bool ShouldSkipTemplatePath(string relativePath)
        {
            string normalized = relativePath.Replace('\\', '/');
            return normalized.Contains("/__pycache__/") ||
                   normalized.EndsWith("/__pycache__", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("agent_config.json", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(InstallManifestFileName, StringComparison.OrdinalIgnoreCase);
        }

        internal const string InstallManifestFileName = "install_manifest.json";

        /// <summary>
        /// Template files removed before install manifests existed; copies made by 2.9.x and earlier carry no manifest,
        /// so these names are how such a copy sheds them. Frozen: from 3.0 on, the manifest finds removed files.
        /// </summary>
        private static readonly string[] LegacyRetiredTemplatePaths =
        {
            "references/SKILL_FULL.md",
            "references/getting_started.md",
            "skills/detailed_report.txt",
            "test_cinemachine_deep.py",
            "test_cinemachine_v16.py",
        };

        /// <summary>What an install wrote: template paths (forward slashes) and the SHA256 of each installed file.</summary>
        private sealed class InstallManifest
        {
            public int schemaVersion = 1;
            public string version;
            public Dictionary<string, string> files = new Dictionary<string, string>();
        }

        private static string InstallManifestPath(string targetPath) =>
            Path.Combine(targetPath, "scripts", InstallManifestFileName);

        private static string ToManifestPath(string relativePath) => relativePath.Replace('\\', '/');

        /// <summary>The manifest of the copy at <paramref name="targetPath"/>, or null when it has none or it is unreadable.</summary>
        private static InstallManifest ReadInstallManifest(string targetPath)
        {
            try
            {
                string path = InstallManifestPath(targetPath);
                if (!File.Exists(path))
                    return null;
                var manifest = JsonConvert.DeserializeObject<InstallManifest>(File.ReadAllText(path, Encoding.UTF8));
                return manifest?.files != null ? manifest : null;
            }
            catch (Exception)
            {
                // A corrupt manifest is treated like a copy made before manifests existed.
                return null;
            }
        }

        /// <summary>
        /// Deletes files an earlier install wrote that are not in <paramref name="keep"/>, then the directories that
        /// leaves empty (never <paramref name="targetPath"/> itself). With a manifest, a file goes only while its
        /// content still matches what was installed, so a hand-edited retired file survives; a copy without one is
        /// cleaned by <see cref="LegacyRetiredTemplatePaths"/>. <paramref name="keep"/> is compared case-insensitively:
        /// on a case-insensitive file system a case-only rename names the file just written.
        /// </summary>
        private static void RemoveRetiredFiles(string targetPath, InstallManifest previous, IEnumerable<string> keep)
        {
            var kept = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            var candidates = previous != null
                ? previous.files.Select(pair => (path: pair.Key, hash: pair.Value))
                : LegacyRetiredTemplatePaths.Select(path => (path, hash: (string)null));

            foreach (var (relativePath, hash) in candidates)
            {
                if (string.IsNullOrEmpty(relativePath) || kept.Contains(relativePath) || !IsContainedRelativePath(relativePath))
                    continue;

                string file = Path.Combine(targetPath, relativePath);
                if (!File.Exists(file))
                    continue;
                if (previous != null && !string.Equals(HashFile(file), hash, StringComparison.OrdinalIgnoreCase))
                {
                    SkillsLogger.LogVerbose($"Kept a retired skill file changed since it was installed: {file}");
                    continue;
                }

                File.Delete(file);
                DeleteEmptyParents(Path.GetDirectoryName(file), targetPath);
            }
        }

        /// <summary>A manifest is a file in the user's folder: only relative paths that stay inside the copy are honoured.</summary>
        private static bool IsContainedRelativePath(string relativePath) =>
            !Path.IsPathRooted(relativePath) && !relativePath.Split('/', '\\').Contains("..");

        private static void DeleteEmptyParents(string directory, string targetPath)
        {
            string root = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(directory);
            while (current.Length > root.Length &&
                   current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static void WriteTemplateFile(string sourceFile, string destinationFile, Encoding encoding)
        {
            string extension = Path.GetExtension(sourceFile);
            bool isTextTemplate =
                extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);

            if (!isTextTemplate)
            {
                File.Copy(sourceFile, destinationFile, true);
                return;
            }

            var content = File.ReadAllText(sourceFile, Encoding.UTF8).Replace("\r\n", "\n");
            File.WriteAllText(destinationFile, content, encoding);
        }
    }
}

// Producer:Betsy
