using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnitySkills
{
    /// <summary>
    /// 面向主流 AI IDE 的一键 skill 安装器：Claude Code、Antigravity、Codex、Cursor、OpenCode、Kimi Code。
    /// </summary>
    public static class SkillInstaller
    {
        // Claude Code 路径：Claude 接受任意文件夹名
        public static string ClaudeProjectPath => Path.Combine(Application.dataPath, "..", ".claude", "skills", "unity-skills");
        public static string ClaudeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "unity-skills");

        // Antigravity 路径 - https://antigravity.google/docs/skills
        // 工作区路径经 .agents/skills 与 Codex 共用（开放的 Agent Skills 标准）
        public static string AntigravityProjectPath => Path.Combine(Application.dataPath, "..", ".agents", "skills", "unity-skills");
        public static string AntigravityGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "skills", "unity-skills");

        // Codex 路径 - https://developers.openai.com/codex/skills
        // 工作区路径经 .agents/skills 与 Antigravity 共用（开放的 Agent Skills 标准）
        public static string CodexProjectPath => Path.Combine(Application.dataPath, "..", ".agents", "skills", "unity-skills");
        public static string CodexGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills", "unity-skills");

        // Cursor 路径 - https://cursor.com/docs/context/skills
        public static string CursorProjectPath => Path.Combine(Application.dataPath, "..", ".cursor", "skills", "unity-skills");
        public static string CursorGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "skills", "unity-skills");

        // OpenCode 路径 - https://opencode.ai/docs/skills
        // 工作区路径经 .agents/skills 共用（开放的 Agent Skills 标准）
        public static string OpenCodeProjectPath => Path.Combine(Application.dataPath, "..", ".opencode", "skills", "unity-skills");
        public static string OpenCodeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode", "skills", "unity-skills");

        // Kimi Code 路径 - https://www.kimi.com/code/docs/kimi-code-cli/customization/skills.html
        // Kimi Code CLI 会扫描四个作用域；这里只指向它的专属目录（不用 Codex/Antigravity 共享的
        // .agents/skills），以保证各工具的安装状态与卸载互不干扰。装在 ~/.agents/skills 下的全局
        // Codex 副本仍会被 Kimi Code 顺带发现。
        // 用户级根目录在 Editor 继承到 KIMI_CODE_HOME 时取该值，否则取 ~/.kimi-code。
        public static string KimiCodeProjectPath => Path.Combine(Application.dataPath, "..", ".kimi-code", "skills", "unity-skills");
        public static string KimiCodeGlobalPath => Path.Combine(KimiCodeHome, "skills", "unity-skills");

        /// <summary>
        /// 解析 $KIMI_CODE_HOME（默认 ~/.kimi-code）。只有当 Unity 由导出过该变量的 shell 启动时
        /// 才看得到它，否则采用文档中的默认值。
        /// </summary>
        private static string KimiCodeHome
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var configured = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
                if (string.IsNullOrWhiteSpace(configured))
                    return Path.Combine(home, ".kimi-code");

                configured = configured.Trim();
                // shell 不会展开被引号包住的开头 "~"，因此在此自行处理，
                // 否则会在工程旁边建出一个名为 "~" 的目录。
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
                return InstallSkill(targetPath, "Claude Code", "ClaudeCode");
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
                return InstallSkill(targetPath, "Antigravity", "Antigravity");
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
                return InstallSkill(targetPath, "Codex", "Codex");
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
                return InstallSkill(targetPath, "Cursor", "Cursor");
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
                return InstallSkill(targetPath, "OpenCode", "OpenCode");
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
                return InstallSkill(targetPath, "Kimi Code", "KimiCode");
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
        /// 一个安装目标（工具 × 作用域）的运行时描述。面板与自动同步共用同一份检测/安装入口，
        /// 避免两处各写一套复制逻辑而彼此漂移。
        /// </summary>
        public sealed class InstallTarget
        {
            public string DisplayName;
            public string Path;
            public Func<bool> IsInstalled;
            public Func<(bool success, string message)> Install;
        }

        /// <summary>
        /// 枚举全部内置安装目标（6 个工具 × 项目/全局两个作用域）。
        /// 注意 Codex 与 Antigravity 的项目级路径同为 .agents/skills（开放标准共用目录），
        /// 需要按路径去重的调用方请自行处理。
        /// </summary>
        public static IEnumerable<InstallTarget> EnumerateTargets()
        {
            yield return MakeTarget("Claude Code (Project)", ClaudeProjectPath, () => IsClaudeProjectInstalled, () => InstallClaude(false));
            yield return MakeTarget("Claude Code (Global)", ClaudeGlobalPath, () => IsClaudeGlobalInstalled, () => InstallClaude(true));
            yield return MakeTarget("Codex (Project)", CodexProjectPath, () => IsCodexProjectInstalled, () => InstallCodex(false));
            yield return MakeTarget("Codex (Global)", CodexGlobalPath, () => IsCodexGlobalInstalled, () => InstallCodex(true));
            yield return MakeTarget("Antigravity (Project)", AntigravityProjectPath, () => IsAntigravityProjectInstalled, () => InstallAntigravity(false));
            yield return MakeTarget("Antigravity (Global)", AntigravityGlobalPath, () => IsAntigravityGlobalInstalled, () => InstallAntigravity(true));
            yield return MakeTarget("Cursor (Project)", CursorProjectPath, () => IsCursorProjectInstalled, () => InstallCursor(false));
            yield return MakeTarget("Cursor (Global)", CursorGlobalPath, () => IsCursorGlobalInstalled, () => InstallCursor(true));
            yield return MakeTarget("OpenCode (Project)", OpenCodeProjectPath, () => IsOpenCodeProjectInstalled, () => InstallOpenCode(false));
            yield return MakeTarget("OpenCode (Global)", OpenCodeGlobalPath, () => IsOpenCodeGlobalInstalled, () => InstallOpenCode(true));
            yield return MakeTarget("Kimi Code (Project)", KimiCodeProjectPath, () => IsKimiCodeProjectInstalled, () => InstallKimiCode(false));
            yield return MakeTarget("Kimi Code (Global)", KimiCodeGlobalPath, () => IsKimiCodeGlobalInstalled, () => InstallKimiCode(true));
        }

        private static InstallTarget MakeTarget(string displayName, string path, Func<bool> isInstalled, Func<(bool, string)> install)
        {
            return new InstallTarget
            {
                DisplayName = displayName,
                Path = path,
                IsInstalled = isInstalled,
                Install = install
            };
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

        private static (bool success, string message) UninstallSkill(string targetPath, string name)
        {
            if (!Directory.Exists(targetPath))
                return (false, $"{name} skill not installed at this location");

            Directory.Delete(targetPath, true);
            SkillsLogger.Log("Uninstalled skill from: " + targetPath);
            return (true, targetPath);
        }

        private static (bool success, string message) InstallSkill(string targetPath, string name, string agentId)
        {
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);

            // 必须用不带 BOM 的 UTF-8：若 BOM（EF BB BF）出现在开头的 `---` 之前，部分 agent 会拒绝解析 YAML frontmatter。
            var utf8NoBom = SkillsCommon.Utf8NoBom;
            CopyTemplateDirectory(GetSkillTemplateRoot(), targetPath, utf8NoBom);

            // 写入 agent 配置，供自动识别 agent 身份
            var scriptsPath = Path.Combine(targetPath, "scripts");
            if (!Directory.Exists(scriptsPath))
                Directory.CreateDirectory(scriptsPath);
            var agentConfig = $"{{\"agentId\": \"{agentId}\", \"installedAt\": \"{DateTime.UtcNow:O}\"}}";
            File.WriteAllText(Path.Combine(scriptsPath, "agent_config.json"), agentConfig, utf8NoBom);

            SkillsLogger.Log($"Installed skill to: {targetPath} (Agent: {agentId})");
            return (true, targetPath);
        }

        private static string GetSkillTemplateRoot()
        {
            string templateRoot;

            // 1. 工程根目录（开发 / 本地克隆）
            templateRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "unity-skills"));
            if (Directory.Exists(templateRoot))
                return templateRoot;

            // 2. UPM 包内部（unity-skills~ 是随包分发的波浪号隐藏目录）
            string resolvedPath = null;
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SkillInstaller).Assembly);
            if (packageInfo != null)
                resolvedPath = packageInfo.resolvedPath;

            if (string.IsNullOrEmpty(resolvedPath))
            {
                packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.besty.unity-skills");
                if (packageInfo != null)
                    resolvedPath = packageInfo.resolvedPath;
            }

            if (!string.IsNullOrEmpty(resolvedPath))
            {
                // 包内的波浪号隐藏目录
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "unity-skills~"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;

                // 与包根同级（git ?path= 全仓库克隆的情形）
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "..", "unity-skills"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;

                // 包根的子目录
                templateRoot = Path.GetFullPath(Path.Combine(resolvedPath, "unity-skills"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;
            }

            throw new DirectoryNotFoundException(
                $"unity-skills template folder not found. " +
                $"Checked: project root, package path ({resolvedPath ?? "N/A"}). " +
                $"Please reinstall the package.");
        }

        private static void CopyTemplateDirectory(string sourceRoot, string targetRoot, Encoding encoding)
        {
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
            }
        }

        private static bool ShouldSkipTemplatePath(string relativePath)
        {
            string normalized = relativePath.Replace('\\', '/');
            return normalized.Contains("/__pycache__/") ||
                   normalized.EndsWith("/__pycache__", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                   normalized.EndsWith("agent_config.json", StringComparison.OrdinalIgnoreCase);
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
