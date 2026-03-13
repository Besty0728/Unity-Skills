using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// One-click skill installer for mainstream AI IDEs: Claude Code, Antigravity, Gemini CLI, Codex, and Cursor.
    /// </summary>
    public static class SkillInstaller
    {
        // Claude Code paths - Claude supports any folder name
        public static string ClaudeProjectPath => Path.Combine(Application.dataPath, "..", ".claude", "skills", "unity-skills");
        public static string ClaudeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "unity-skills");
        
        // Antigravity paths
        public static string AntigravityProjectPath => Path.Combine(Application.dataPath, "..", ".agent", "skills", "unity-skills");
        public static string AntigravityGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "skills", "unity-skills");
        public static string AntigravityWorkflowProjectPath => Path.Combine(Application.dataPath, "..", ".agent", "workflows");
        public static string AntigravityWorkflowGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "workflows");

        // Gemini CLI paths - folder name should match SKILL.md name field for proper recognition
        public static string GeminiProjectPath => Path.Combine(Application.dataPath, "..", ".gemini", "skills", "unity-skills");
        public static string GeminiGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "skills", "unity-skills");

        // Codex paths - https://developers.openai.com/codex/skills
        public static string CodexProjectPath => Path.Combine(Application.dataPath, "..", ".codex", "skills", "unity-skills");
        public static string CodexGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", "unity-skills");

        // Cursor paths - https://cursor.com/docs/context/skills
        public static string CursorProjectPath => Path.Combine(Application.dataPath, "..", ".cursor", "skills", "unity-skills");
        public static string CursorGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "skills", "unity-skills");

        public static bool IsClaudeProjectInstalled => Directory.Exists(ClaudeProjectPath) && File.Exists(Path.Combine(ClaudeProjectPath, "SKILL.md"));
        public static bool IsClaudeGlobalInstalled => Directory.Exists(ClaudeGlobalPath) && File.Exists(Path.Combine(ClaudeGlobalPath, "SKILL.md"));
        public static bool IsAntigravityProjectInstalled => Directory.Exists(AntigravityProjectPath) && File.Exists(Path.Combine(AntigravityProjectPath, "SKILL.md"));
        public static bool IsAntigravityGlobalInstalled => Directory.Exists(AntigravityGlobalPath) && File.Exists(Path.Combine(AntigravityGlobalPath, "SKILL.md"));
        public static bool IsGeminiProjectInstalled => Directory.Exists(GeminiProjectPath) && File.Exists(Path.Combine(GeminiProjectPath, "SKILL.md"));
        public static bool IsGeminiGlobalInstalled => Directory.Exists(GeminiGlobalPath) && File.Exists(Path.Combine(GeminiGlobalPath, "SKILL.md"));
        public static bool IsCodexProjectInstalled => Directory.Exists(CodexProjectPath) && File.Exists(Path.Combine(CodexProjectPath, "SKILL.md"));
        public static bool IsCodexGlobalInstalled => Directory.Exists(CodexGlobalPath) && File.Exists(Path.Combine(CodexGlobalPath, "SKILL.md"));
        public static bool IsCursorProjectInstalled => Directory.Exists(CursorProjectPath) && File.Exists(Path.Combine(CursorProjectPath, "SKILL.md"));
        public static bool IsCursorGlobalInstalled => Directory.Exists(CursorGlobalPath) && File.Exists(Path.Combine(CursorGlobalPath, "SKILL.md"));

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
                var res = InstallSkill(targetPath, "Antigravity", "Antigravity");
                if (!res.success) return res;

                // Install Workflow for Antigravity slash commands
                var workflowPath = global ? AntigravityWorkflowGlobalPath : AntigravityWorkflowProjectPath;
                if (!Directory.Exists(workflowPath))
                    Directory.CreateDirectory(workflowPath);
                
                var workflowMd = GenerateAntigravityWorkflow();
                var utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(workflowPath, "unity-skills.md"), workflowMd.Replace("\r\n", "\n"), utf8NoBom);
                
                return (true, targetPath);
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
                var res = UninstallSkill(targetPath, "Antigravity");

                // Uninstall Workflow
                var workflowPath = global ? AntigravityWorkflowGlobalPath : AntigravityWorkflowProjectPath;
                var workflowFile = Path.Combine(workflowPath, "unity-skills.md");
                if (File.Exists(workflowFile))
                    File.Delete(workflowFile);

                return res;
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) InstallGemini(bool global)
        {
            try
            {
                var targetPath = global ? GeminiGlobalPath : GeminiProjectPath;
                return InstallSkill(targetPath, "Gemini CLI", "Gemini");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public static (bool success, string message) UninstallGemini(bool global)
        {
            try
            {
                var targetPath = global ? GeminiGlobalPath : GeminiProjectPath;
                return UninstallSkill(targetPath, "Gemini CLI");
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
                var res = InstallSkill(targetPath, "Codex", "Codex");
                if (!res.success) return res;

                // For project-level install, also update AGENTS.md
                if (!global)
                {
                    UpdateAgentsMd();
                }
                
                return res;
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
                var res = UninstallSkill(targetPath, "Codex");

                // For project-level uninstall, also remove from AGENTS.md
                if (!global)
                {
                    RemoveFromAgentsMd();
                }

                return res;
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

        private static string AgentsMdPath => Path.Combine(Application.dataPath, "..", "AGENTS.md");
        private const string UnitySkillsEntry = "- unity-skills: Unity Editor automation via REST API";

        private static void UpdateAgentsMd()
        {
            var agentsPath = AgentsMdPath;
            var utf8NoBom = new UTF8Encoding(false);

            if (File.Exists(agentsPath))
            {
                // File exists, check if unity-skills is already declared
                var content = File.ReadAllText(agentsPath, Encoding.UTF8);
                if (!content.Contains("unity-skills"))
                {
                    // Append unity-skills entry
                    var appendContent = "\n\n## UnitySkills\n" + UnitySkillsEntry + "\n";
                    File.AppendAllText(agentsPath, appendContent.Replace("\r\n", "\n"), utf8NoBom);
                    SkillsLogger.Log("Added unity-skills to existing AGENTS.md");
                }
                else
                {
                    SkillsLogger.LogVerbose("unity-skills already declared in AGENTS.md");
                }
            }
            else
            {
                // Create new AGENTS.md
                var newContent = @"# AGENTS.md

This file declares available skills for AI agents like Codex.

## UnitySkills
" + UnitySkillsEntry + "\n";
                File.WriteAllText(agentsPath, newContent.Replace("\r\n", "\n"), utf8NoBom);
                SkillsLogger.Log("Created AGENTS.md with unity-skills declaration");
            }
        }

        private static void RemoveFromAgentsMd()
        {
            var agentsPath = AgentsMdPath;
            if (!File.Exists(agentsPath)) return;

            var content = File.ReadAllText(agentsPath, Encoding.UTF8);
            if (content.Contains("unity-skills"))
            {
                // Remove unity-skills related lines
                var lines = content.Split('\n').ToList();
                lines.RemoveAll(l => l.Contains("unity-skills") || l.Trim() == "## UnitySkills");
                
                // Clean up empty consecutive lines
                var cleanedContent = string.Join("\n", lines).Trim() + "\n";
                var utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(agentsPath, cleanedContent.Replace("\r\n", "\n"), utf8NoBom);
                SkillsLogger.Log("Removed unity-skills from AGENTS.md");
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

            // CRITICAL: Use UTF-8 WITHOUT BOM for Gemini CLI compatibility
            // Gemini CLI cannot parse YAML frontmatter if BOM (EF BB BF) is present at start of file
            var utf8NoBom = new UTF8Encoding(false);
            CopyTemplateDirectory(GetSkillTemplateRoot(), targetPath, utf8NoBom);

            // Write agent config for automatic agent identification
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
            // 1. Try project root (development / local clone)
            string templateRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "unity-skills"));
            if (Directory.Exists(templateRoot))
                return templateRoot;

            // 2. Try relative to UPM package location (git/local package installs)
            // For git packages with ?path=SkillsForUnity, resolvedPath points to SkillsForUnity/
            // The unity-skills template folder is at the repo root (parent of package path)
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SkillInstaller).Assembly);
            if (packageInfo != null && !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                templateRoot = Path.GetFullPath(Path.Combine(packageInfo.resolvedPath, "..", "unity-skills"));
                if (Directory.Exists(templateRoot))
                    return templateRoot;
            }

            throw new DirectoryNotFoundException(
                "unity-skills template folder not found. " +
                "Checked: project root and package path. " +
                "If installed via UPM, ensure the git URL includes the full repository.");
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

        private static string GenerateAntigravityWorkflow()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("description: Control Unity Editor via REST API. Create GameObjects, manage scenes, components, materials, and more. 100+ automation tools.");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# unity-skills");
            sb.AppendLine();
            sb.AppendLine("AI-powered Unity Editor automation through REST API. This workflow enables intelligent control of Unity Editor including GameObject manipulation, scene management, asset handling, and much more.");
            sb.AppendLine();
            sb.AppendLine("## Available modules");
            sb.AppendLine();
            sb.AppendLine("| Module | Description |");
            sb.AppendLine("|--------|-------------|");
            sb.AppendLine("| **gameobject** | Create, modify, find GameObjects |");
            sb.AppendLine("| **component** | Add, remove, configure components |");
            sb.AppendLine("| **scene** | Scene loading, saving, management |");
            sb.AppendLine("| **material** | Material creation, HDR emission, keywords |");
            sb.AppendLine("| **light** | Lighting setup and configuration |");
            sb.AppendLine("| **animator** | Animation controller management |");
            sb.AppendLine("| **ui** | UI Canvas and element creation |");
            sb.AppendLine("| **validation**| Project validation and checking |");
            sb.AppendLine("| **prefab** | Prefab creation and instantiation |");
            sb.AppendLine("| **asset** | Asset import, organize, search |");
            sb.AppendLine("| **editor** | Editor state, play mode, selection |");
            sb.AppendLine("| **console** | Log capture and debugging |");
            sb.AppendLine("| **script** | C# script creation and search |");
            sb.AppendLine("| **shader** | Shader creation and listing |");
            sb.AppendLine("| **workflow** | Time-machine revert, history tracking, auto-save |");
            sb.AppendLine();
            sb.AppendLine("## How to Use");
            sb.AppendLine();
            sb.AppendLine("1. **Check Unity Connection**: Ensure Unity Editor is running with the `SkillsForUnity` plugin.");
            sb.AppendLine("2. **Invoke Skills**: Use `unity_skills.py` (located in the skill's scripts directory) to call Unity functions.");
            sb.AppendLine();
            sb.AppendLine("### Example Prompt");
            sb.AppendLine("`/unity-skills create a red cube at (0, 0, 0)`");
            sb.AppendLine();
            sb.AppendLine("## Best Practices");
            sb.AppendLine();
            sb.AppendLine("- **Save Progress**: Frequently call `scene_save` during automation.");
            sb.AppendLine("- **Undo Support**: Operations are usually undoable in Unity.");
            sb.AppendLine("- **Domain Reload**: Be aware that creating scripts triggers a domain reload.");
            
            return sb.ToString();
        }
    }
}
