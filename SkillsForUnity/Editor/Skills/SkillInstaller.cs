using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// One-click skill installer for Claude Code, Antigravity, and Gemini CLI.
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

        public static bool IsClaudeProjectInstalled => Directory.Exists(ClaudeProjectPath) && File.Exists(Path.Combine(ClaudeProjectPath, "SKILL.md"));
        public static bool IsClaudeGlobalInstalled => Directory.Exists(ClaudeGlobalPath) && File.Exists(Path.Combine(ClaudeGlobalPath, "SKILL.md"));
        public static bool IsAntigravityProjectInstalled => Directory.Exists(AntigravityProjectPath) && File.Exists(Path.Combine(AntigravityProjectPath, "SKILL.md"));
        public static bool IsAntigravityGlobalInstalled => Directory.Exists(AntigravityGlobalPath) && File.Exists(Path.Combine(AntigravityGlobalPath, "SKILL.md"));
        public static bool IsGeminiProjectInstalled => Directory.Exists(GeminiProjectPath) && File.Exists(Path.Combine(GeminiProjectPath, "SKILL.md"));
        public static bool IsGeminiGlobalInstalled => Directory.Exists(GeminiGlobalPath) && File.Exists(Path.Combine(GeminiGlobalPath, "SKILL.md"));
        public static bool IsCodexProjectInstalled => Directory.Exists(CodexProjectPath) && File.Exists(Path.Combine(CodexProjectPath, "SKILL.md"));
        public static bool IsCodexGlobalInstalled => Directory.Exists(CodexGlobalPath) && File.Exists(Path.Combine(CodexGlobalPath, "SKILL.md"));

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
                var content = File.ReadAllText(agentsPath);
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

            var content = File.ReadAllText(agentsPath);
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

            var skillMd = GenerateSkillMd();
            // Normalize line endings to LF for cross-platform compatibility
            skillMd = skillMd.Replace("\r\n", "\n");
            File.WriteAllText(Path.Combine(targetPath, "SKILL.md"), skillMd, utf8NoBom);

            var pythonHelper = GeneratePythonHelper();
            pythonHelper = pythonHelper.Replace("\r\n", "\n");
            var scriptsPath = Path.Combine(targetPath, "scripts");
            if (!Directory.Exists(scriptsPath))
                Directory.CreateDirectory(scriptsPath);
            File.WriteAllText(Path.Combine(scriptsPath, "unity_skills.py"), pythonHelper, utf8NoBom);

            // Write agent config for automatic agent identification
            var agentConfig = $"{{\"agentId\": \"{agentId}\", \"installedAt\": \"{DateTime.UtcNow:O}\"}}";
            File.WriteAllText(Path.Combine(scriptsPath, "agent_config.json"), agentConfig, utf8NoBom);

            SkillsLogger.Log($"Installed skill to: {targetPath} (Agent: {agentId})");
            return (true, targetPath);
        }

        private static string GenerateSkillMd()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            // Gemini CLI requires: lowercase, alphanumeric and dashes only
            sb.AppendLine("name: unity-skills");
            // CRITICAL: Description must be single-line double-quoted string for Gemini CLI compatibility
            sb.AppendLine("description: \"Unity Editor automation via REST API. Control GameObjects, components, scenes, materials, prefabs, lights, and more with 100+ professional tools.\"");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# Unity Editor Control Skill");
            sb.AppendLine();
            sb.AppendLine("You are an expert Unity developer. This skill enables you to directly control Unity Editor through a REST API.");
            sb.AppendLine("Use the Python helper script in `scripts/unity_skills.py` to execute Unity operations.");
            sb.AppendLine();
            sb.AppendLine("## Prerequisites");
            sb.AppendLine();
            sb.AppendLine("1. Unity Editor must be running with the UnitySkills package installed");
            sb.AppendLine("2. REST server must be started: **Window > UnitySkills > Start Server**");
            sb.AppendLine("3. Server endpoint: `http://localhost:8090`");
            sb.AppendLine();
            sb.AppendLine("## Quick Start");
            sb.AppendLine();
            sb.AppendLine("```python");
            sb.AppendLine("# Import the helper from the scripts/ directory");
            sb.AppendLine("import sys");
            sb.AppendLine("sys.path.insert(0, 'scripts')  # Adjust path to skill's scripts directory");
            sb.AppendLine("from unity_skills import call_skill, is_unity_running, wait_for_unity");
            sb.AppendLine();
            sb.AppendLine("# Check if Unity is ready");
            sb.AppendLine("if is_unity_running():");
            sb.AppendLine("    # Create a cube");
            sb.AppendLine("    call_skill('gameobject_create', name='MyCube', primitiveType='Cube', x=0, y=1, z=0)");
            sb.AppendLine("    # Set its color to red");
            sb.AppendLine("    call_skill('material_set_color', gameObjectName='MyCube', r=1, g=0, b=0)");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## ⚠️ Important: Script Creation & Domain Reload");
            sb.AppendLine();
            sb.AppendLine("When creating C# scripts with `script_create`, Unity recompiles all scripts (Domain Reload).");
            sb.AppendLine("The server temporarily stops during compilation and auto-restarts.");
            sb.AppendLine();
            sb.AppendLine("```python");
            sb.AppendLine("# After creating a script, wait for Unity to recompile");
            sb.AppendLine("result = call_skill('script_create', name='MyScript', template='MonoBehaviour')");
            sb.AppendLine("if result.get('success'):");
            sb.AppendLine("    wait_for_unity(timeout=10)  # Wait for server to come back");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Workflow Examples");
            sb.AppendLine();
            sb.AppendLine("### Create a Game Scene");
            sb.AppendLine("```python");
            sb.AppendLine("# 1. Create ground");
            sb.AppendLine("call_skill('gameobject_create', name='Ground', primitiveType='Plane', x=0, y=0, z=0)");
            sb.AppendLine("call_skill('gameobject_set_transform', name='Ground', scaleX=5, scaleY=1, scaleZ=5)");
            sb.AppendLine();
            sb.AppendLine("# 2. Create player");
            sb.AppendLine("call_skill('gameobject_create', name='Player', primitiveType='Capsule', x=0, y=1, z=0)");
            sb.AppendLine("call_skill('component_add', name='Player', componentType='Rigidbody')");
            sb.AppendLine();
            sb.AppendLine("# 3. Add lighting");
            sb.AppendLine("call_skill('light_create', name='Sun', lightType='Directional', intensity=1.5)");
            sb.AppendLine();
            sb.AppendLine("# 4. Save the scene");
            sb.AppendLine("call_skill('scene_save', scenePath='Assets/Scenes/GameScene.unity')");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("### Create UI Menu");
            sb.AppendLine("```python");
            sb.AppendLine("call_skill('ui_create_canvas', name='MainMenu')");
            sb.AppendLine("call_skill('ui_create_text', name='Title', parent='MainMenu', text='My Game', fontSize=48)");
            sb.AppendLine("call_skill('ui_create_button', name='PlayBtn', parent='MainMenu', text='Play', width=200, height=50)");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Available Skills");
            
            // Dynamic Reflection Logic
            var skillsByCategory = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Reflection.MethodInfo>>();
            
            var allTypes = System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } });

            foreach (var type in allTypes)
            {
                foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    var attr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<UnitySkillAttribute>(method);
                    if (attr != null)
                    {
                        var category = type.Name.Replace("Skills", "");
                        if (!skillsByCategory.ContainsKey(category))
                            skillsByCategory[category] = new System.Collections.Generic.List<System.Reflection.MethodInfo>();

                        skillsByCategory[category].Add(method);
                    }
                }
            }

            foreach (var category in skillsByCategory.Keys.OrderBy(k => k))
            {
                sb.AppendLine();
                sb.AppendLine($"### {category}");
                foreach (var method in skillsByCategory[category])
                {
                    var attr = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<UnitySkillAttribute>(method);
                    var skillName = attr.Name ?? method.Name;
                    var description = attr.Description ?? "";
                    
                    var parameters = method.GetParameters()
                        .Select(p => p.Name)
                        .ToArray();
                    var paramStr = string.Join(", ", parameters);
                    
                    sb.AppendLine($"- `{skillName}({paramStr})` - {description}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Skill Directory Structure");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("unity-skills/");
            sb.AppendLine("├── SKILL.md          # This file - skill entry point");
            sb.AppendLine("└── scripts/");
            sb.AppendLine("    └── unity_skills.py  # Python helper with call_skill(), is_unity_running(), etc.");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Direct REST API");
            sb.AppendLine();
            sb.AppendLine("```bash");
            sb.AppendLine("# Health check");
            sb.AppendLine("curl http://localhost:8090/health");
            sb.AppendLine();
            sb.AppendLine("# List all available skills");
            sb.AppendLine("curl http://localhost:8090/skills");
            sb.AppendLine();
            sb.AppendLine("# Execute a skill");
            sb.AppendLine("curl -X POST http://localhost:8090/skill/gameobject_create \\");
            sb.AppendLine("  -H 'Content-Type: application/json' \\");
            sb.AppendLine("  -d '{\"name\":\"MyCube\", \"primitiveType\":\"Cube\"}'");
            sb.AppendLine("```");
            return sb.ToString();
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

        private static string GeneratePythonHelper()
        {
            var sb = new StringBuilder();
            // File header with shebang and encoding declaration
            sb.AppendLine("#!/usr/bin/env python3");
            sb.AppendLine("# -*- coding: utf-8 -*-");
            sb.AppendLine("# Unity Skills Python Helper");
            sb.AppendLine("# Auto-generated by UnitySkills");
            sb.AppendLine();

            // CRITICAL: Windows console encoding fix - must be at the very top
            sb.AppendLine("# CRITICAL: Fix Windows console encoding BEFORE any other imports or print statements");
            sb.AppendLine("# This must be at the very top of the module to work correctly");
            sb.AppendLine("import sys");
            sb.AppendLine("if sys.platform == 'win32':");
            sb.AppendLine("    import codecs");
            sb.AppendLine("    if hasattr(sys.stdout, 'buffer'):");
            sb.AppendLine("        sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'replace')");
            sb.AppendLine("    if hasattr(sys.stderr, 'buffer'):");
            sb.AppendLine("        sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'replace')");
            sb.AppendLine();

            // Imports
            sb.AppendLine("import requests");
            sb.AppendLine("import time");
            sb.AppendLine("import json");
            sb.AppendLine("import os");
            sb.AppendLine("from typing import Any, Dict, Optional");
            sb.AppendLine();
            sb.AppendLine("UNITY_URL = \"http://localhost:8090\"");
            sb.AppendLine("DEFAULT_PORT = 8090");
            sb.AppendLine("DEFAULT_TIMEOUT = 3600");
            sb.AppendLine();

            // Auto-workflow configuration
            sb.AppendLine("# Auto-workflow configuration");
            sb.AppendLine("_auto_workflow_enabled = True  # Enable auto-workflow");
            sb.AppendLine("_current_workflow_active = False  # Is a workflow currently active?");
            sb.AppendLine();
            sb.AppendLine("# Skills that should trigger auto-workflow (modification operations)");
            sb.AppendLine("_workflow_tracked_skills = {");
            sb.AppendLine("    'gameobject_create', 'gameobject_delete', 'gameobject_rename',");
            sb.AppendLine("    'gameobject_set_transform', 'gameobject_duplicate', 'gameobject_set_parent',");
            sb.AppendLine("    'gameobject_set_active', 'gameobject_create_batch', 'gameobject_delete_batch',");
            sb.AppendLine("    'gameobject_rename_batch', 'gameobject_set_transform_batch',");
            sb.AppendLine("    'component_add', 'component_remove', 'component_set_property',");
            sb.AppendLine("    'component_add_batch', 'component_remove_batch', 'component_set_property_batch',");
            sb.AppendLine("    'material_create', 'material_assign', 'material_set_color', 'material_set_texture',");
            sb.AppendLine("    'material_set_emission', 'material_set_float', 'material_set_shader',");
            sb.AppendLine("    'material_create_batch', 'material_assign_batch', 'material_set_colors_batch',");
            sb.AppendLine("    'light_create', 'light_set_properties', 'light_set_enabled',");
            sb.AppendLine("    'prefab_create', 'prefab_instantiate', 'prefab_apply', 'prefab_unpack',");
            sb.AppendLine("    'prefab_instantiate_batch',");
            sb.AppendLine("    'ui_create_canvas', 'ui_create_panel', 'ui_create_button', 'ui_create_text',");
            sb.AppendLine("    'ui_create_image', 'ui_create_inputfield', 'ui_create_slider', 'ui_create_toggle',");
            sb.AppendLine("    'ui_create_batch', 'ui_set_text', 'ui_set_anchor', 'ui_set_rect',");
            sb.AppendLine("    'script_create', 'script_delete', 'script_create_batch',");
            sb.AppendLine("    'terrain_create', 'terrain_set_height', 'terrain_set_heights_batch', 'terrain_paint_texture',");
            sb.AppendLine("    'asset_import', 'asset_delete', 'asset_move', 'asset_duplicate',");
            sb.AppendLine("    'scene_create', 'scene_save',");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("def set_auto_workflow(enabled: bool):");
            sb.AppendLine("    \"\"\"Enable or disable auto-workflow recording.\"\"\"");
            sb.AppendLine("    global _auto_workflow_enabled");
            sb.AppendLine("    _auto_workflow_enabled = enabled");
            sb.AppendLine();
            sb.AppendLine("def is_auto_workflow_enabled() -> bool:");
            sb.AppendLine("    \"\"\"Check if auto-workflow is enabled.\"\"\"");
            sb.AppendLine("    return _auto_workflow_enabled");
            sb.AppendLine();

            // Registry path helper
            sb.AppendLine("def get_registry_path():");
            sb.AppendLine("    return os.path.join(os.path.expanduser(\"~\"), \".unity_skills\", \"registry.json\")");
            sb.AppendLine();
            sb.AppendLine("def _get_agent_id():");
            sb.AppendLine("    \"\"\"Read agent ID from agent_config.json in the same directory as this script.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        config_path = os.path.join(os.path.dirname(__file__), 'agent_config.json')");
            sb.AppendLine("        if os.path.exists(config_path):");
            sb.AppendLine("            with open(config_path, 'r') as f:");
            sb.AppendLine("                return json.load(f).get('agentId', 'Unknown')");
            sb.AppendLine("    except: pass");
            sb.AppendLine("    return 'Unknown'");
            sb.AppendLine();
            sb.AppendLine("AGENT_ID = _get_agent_id()");
            sb.AppendLine();

            // UnitySkills class
            sb.AppendLine("class UnitySkills:");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    Client for interacting with a specific Unity Editor instance.");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    def __init__(self, port: int = None, target: str = None, url: str = None):");
            sb.AppendLine("        \"\"\"");
            sb.AppendLine("        Initialize client.");
            sb.AppendLine("        Args:");
            sb.AppendLine("            port: Connect to specific localhost port (e.g. 8091)");
            sb.AppendLine("            target: Connect to instance by Name or ID (e.g. \"MyGame\" or \"MyGame_A1B2\") - auto-discovers port.");
            sb.AppendLine("            url: Full URL override.");
            sb.AppendLine("        \"\"\"");
            sb.AppendLine("        self.url = url");
            sb.AppendLine("        self.timeout = DEFAULT_TIMEOUT");
            sb.AppendLine();
            sb.AppendLine("        if not self.url:");
            sb.AppendLine("            if port:");
            sb.AppendLine("                self.url = f\"http://localhost:{port}\"");
            sb.AppendLine("            elif target:");
            sb.AppendLine("                found_port = self._find_port_by_target(target)");
            sb.AppendLine("                if found_port:");
            sb.AppendLine("                    self.url = f\"http://localhost:{found_port}\"");
            sb.AppendLine("                else:");
            sb.AppendLine("                    raise ValueError(f\"Could not find Unity instance matching '{target}' in registry.\")");
            sb.AppendLine("            else:");
            sb.AppendLine("                self.url = f\"http://localhost:{DEFAULT_PORT}\"");
            sb.AppendLine();
            sb.AppendLine("        # Sync timeout from Unity server");
            sb.AppendLine("        try:");
            sb.AppendLine("            resp = requests.get(f\"{self.url}/health\", timeout=2)");
            sb.AppendLine("            if resp.status_code == 200:");
            sb.AppendLine("                minutes = resp.json().get('requestTimeoutMinutes')");
            sb.AppendLine("                if minutes and minutes > 0:");
            sb.AppendLine("                    self.timeout = int(minutes) * 60");
            sb.AppendLine("        except: pass");
            sb.AppendLine();
            sb.AppendLine("    def _find_port_by_target(self, target: str) -> Optional[int]:");
            sb.AppendLine("        reg_path = get_registry_path()");
            sb.AppendLine("        if not os.path.exists(reg_path):");
            sb.AppendLine("            return None");
            sb.AppendLine("        try:");
            sb.AppendLine("            with open(reg_path, 'r') as f:");
            sb.AppendLine("                data = json.load(f)");
            sb.AppendLine("                # target can be ID or Name");
            sb.AppendLine("                # 1. Exact ID match");
            sb.AppendLine("                for path, info in data.items():");
            sb.AppendLine("                    if info.get('id') == target:");
            sb.AppendLine("                        return info.get('port')");
            sb.AppendLine("                # 2. Exact Name match (if unique?) - return first found");
            sb.AppendLine("                for path, info in data.items():");
            sb.AppendLine("                    if info.get('name') == target:");
            sb.AppendLine("                        return info.get('port')");
            sb.AppendLine("                return None");
            sb.AppendLine("        except:");
            sb.AppendLine("            return None");
            sb.AppendLine();
            sb.AppendLine("    def call(self, skill_name: str, verbose: bool = False, **kwargs) -> Dict[str, Any]:");
            sb.AppendLine("        \"\"\"");
            sb.AppendLine("        Call a skill on this instance.");
            sb.AppendLine();
            sb.AppendLine("        Returns a normalized response with 'success' field and flattened result data.");
            sb.AppendLine("        \"\"\"");
            sb.AppendLine("        try:");
            sb.AppendLine("            # Combine verbose into kwargs for JSON body");
            sb.AppendLine("            kwargs['verbose'] = verbose");
            sb.AppendLine("            headers = {'X-Agent-Id': AGENT_ID, 'Content-Type': 'application/json; charset=utf-8'}");
            sb.AppendLine("            body = json.dumps(kwargs, ensure_ascii=False).encode('utf-8')");
            sb.AppendLine("            response = requests.post(f\"{self.url}/skill/{skill_name}\", data=body, headers=headers, timeout=self.timeout)");
            sb.AppendLine("            response.encoding = 'utf-8'  # Ensure correct UTF-8 decoding");
            sb.AppendLine();
            sb.AppendLine("            try:");
            sb.AppendLine("                data = response.json()");
            sb.AppendLine("            except ValueError:");
            sb.AppendLine("                return {'success': False, 'error': f\"Invalid JSON response: {response.text}\"}");
            sb.AppendLine();
            sb.AppendLine("            # Normalize response format");
            sb.AppendLine("            if data.get('status') == 'success':");
            sb.AppendLine("                result = data.get('result', {})");
            sb.AppendLine("                normalized = {'success': True}");
            sb.AppendLine("                if isinstance(result, dict):");
            sb.AppendLine("                    normalized.update(result)");
            sb.AppendLine("                else:");
            sb.AppendLine("                    normalized['result'] = result");
            sb.AppendLine("                return normalized");
            sb.AppendLine("            elif data.get('status') == 'error':");
            sb.AppendLine("                return {");
            sb.AppendLine("                    'success': False,");
            sb.AppendLine("                    'error': data.get('error', 'Unknown error'),");
            sb.AppendLine("                    'message': data.get('message', '')");
            sb.AppendLine("                }");
            sb.AppendLine("            else:");
            sb.AppendLine("                return data");
            sb.AppendLine();
            sb.AppendLine("        except requests.exceptions.ConnectionError:");
            sb.AppendLine("             return {");
            sb.AppendLine("                'success': False,");
            sb.AppendLine("                'error': f\"Cannot connect to {self.url}. Unity instance may be down.\",");
            sb.AppendLine("                'suggestion': 'Unity may be recompiling scripts (Domain Reload). Wait 3-5 seconds and retry.',");
            sb.AppendLine("                'hint': 'Check if server is running: Window > UnitySkills > Start Server'");
            sb.AppendLine("            }");
            sb.AppendLine("        except Exception as e:");
            sb.AppendLine("            return {'success': False, 'error': str(e)}");
            sb.AppendLine();
            sb.AppendLine("    # --- Proxies for common skills ---");
            sb.AppendLine("    def create_cube(self, x=0, y=0, z=0, name=\"Cube\"): return self.call(\"create_cube\", x=x, y=y, z=z, name=name)");
            sb.AppendLine("    def create_sphere(self, x=0, y=0, z=0, name=\"Sphere\"): return self.call(\"create_sphere\", x=x, y=y, z=z, name=name)");
            sb.AppendLine("    def delete_object(self, name): return self.call(\"delete_object\", objectName=name)");
            sb.AppendLine();
            sb.AppendLine();

            sb.AppendLine("class WorkflowContext:");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    Workflow context manager for batching multiple operations into a single workflow task.");
            sb.AppendLine();
            sb.AppendLine("    Usage:");
            sb.AppendLine("        with WorkflowContext('Create Scene', 'Build player and environment'):");
            sb.AppendLine("            call_skill('gameobject_create', name='Player')");
            sb.AppendLine("            call_skill('component_add', name='Player', componentType='Rigidbody')");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    def __init__(self, tag: str, description: str = ''):");
            sb.AppendLine("        self.tag = tag");
            sb.AppendLine("        self.description = description");
            sb.AppendLine();
            sb.AppendLine("    def __enter__(self):");
            sb.AppendLine("        global _current_workflow_active");
            sb.AppendLine("        _current_workflow_active = True");
            sb.AppendLine("        call_skill('workflow_task_start', tag=self.tag, description=self.description)");
            sb.AppendLine("        return self");
            sb.AppendLine();
            sb.AppendLine("    def __exit__(self, exc_type, exc_val, exc_tb):");
            sb.AppendLine("        global _current_workflow_active");
            sb.AppendLine("        call_skill('workflow_task_end')");
            sb.AppendLine("        _current_workflow_active = False");
            sb.AppendLine("        return False  # Do not suppress exceptions");
            sb.AppendLine();
            sb.AppendLine("def workflow_context(tag: str, description: str = '') -> WorkflowContext:");
            sb.AppendLine("    \"\"\"Convenience function to create a WorkflowContext.\"\"\"");
            sb.AppendLine("    return WorkflowContext(tag, description)");
            sb.AppendLine();

            // Global default client and module-level functions
            sb.AppendLine("# Global Default Client");
            sb.AppendLine("_default_client = UnitySkills()");
            sb.AppendLine();
            sb.AppendLine("def connect(port: int = None, target: str = None) -> UnitySkills:");
            sb.AppendLine("    return UnitySkills(port=port, target=target)");
            sb.AppendLine();
            sb.AppendLine("def list_instances() -> list:");
            sb.AppendLine("    \"\"\"Return list of active Unity instances from registry.\"\"\"");
            sb.AppendLine("    reg_path = get_registry_path()");
            sb.AppendLine("    if not os.path.exists(reg_path):");
            sb.AppendLine("        return []");
            sb.AppendLine("    try:");
            sb.AppendLine("        with open(reg_path, 'r') as f:");
            sb.AppendLine("            data = json.load(f)");
            sb.AppendLine("            return list(data.values())");
            sb.AppendLine("    except:");
            sb.AppendLine("        return []");
            sb.AppendLine();
            sb.AppendLine("def call_skill(skill_name: str, **kwargs) -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    Call a Unity skill, supporting auto-workflow recording.");
            sb.AppendLine();
            sb.AppendLine("    If auto-workflow is enabled (default), it will automatically:");
            sb.AppendLine("    1. Start a workflow task before a modification operation");
            sb.AppendLine("    2. Execute the operation");
            sb.AppendLine("    3. End the workflow task");
            sb.AppendLine("    \"\"\"");
            sb.AppendLine("    global _current_workflow_active");
            sb.AppendLine();
            sb.AppendLine("    # Check if we should track this call");
            sb.AppendLine("    should_track = (");
            sb.AppendLine("        _auto_workflow_enabled and");
            sb.AppendLine("        skill_name in _workflow_tracked_skills and");
            sb.AppendLine("        not _current_workflow_active and");
            sb.AppendLine("        not skill_name.startswith('workflow_')  # Avoid recursion");
            sb.AppendLine("    )");
            sb.AppendLine();
            sb.AppendLine("    if should_track:");
            sb.AppendLine("        # Start workflow");
            sb.AppendLine("        _current_workflow_active = True");
            sb.AppendLine("        _default_client.call(");
            sb.AppendLine("            'workflow_task_start',");
            sb.AppendLine("            tag=skill_name,");
            sb.AppendLine("            description=f\"Auto: {skill_name} - {str(kwargs)[:100]}\"");
            sb.AppendLine("        )");
            sb.AppendLine();
            sb.AppendLine("        # Execute actual operation");
            sb.AppendLine("        result = _default_client.call(skill_name, **kwargs)");
            sb.AppendLine();
            sb.AppendLine("        # End workflow");
            sb.AppendLine("        _default_client.call('workflow_task_end')");
            sb.AppendLine("        _current_workflow_active = False");
            sb.AppendLine();
            sb.AppendLine("        return result");
            sb.AppendLine("    else:");
            sb.AppendLine("        return _default_client.call(skill_name, **kwargs)");
            sb.AppendLine();
            sb.AppendLine("def call_skill_with_retry(skill_name: str, max_retries: int = 3, retry_delay: float = 2.0, **kwargs) -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"Call a Unity skill with automatic retry logic for Domain Reload scenarios.\"\"\"");
            sb.AppendLine("    for attempt in range(max_retries):");
            sb.AppendLine("        result = call_skill(skill_name, **kwargs)");
            sb.AppendLine("        if result.get('success') or ('error' not in result or 'Cannot connect' not in result.get('error', '')):");
            sb.AppendLine("            return result");
            sb.AppendLine("        if attempt < max_retries - 1:");
            sb.AppendLine("            time.sleep(retry_delay)");
            sb.AppendLine("    return result");
            sb.AppendLine();
            sb.AppendLine("def wait_for_unity(timeout: float = 10.0, check_interval: float = 1.0) -> bool:");
            sb.AppendLine("    \"\"\"Wait for Unity REST server to become available. Useful after script creation.\"\"\"");
            sb.AppendLine("    start_time = time.time()");
            sb.AppendLine("    while time.time() - start_time < timeout:");
            sb.AppendLine("        if is_unity_running():");
            sb.AppendLine("            return True");
            sb.AppendLine("        time.sleep(check_interval)");
            sb.AppendLine("    return False");
            sb.AppendLine();
            sb.AppendLine("def get_skills() -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"Get list of all available skills.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.get(f\"{UNITY_URL}/skills\", timeout=5)");
            sb.AppendLine("        response.encoding = 'utf-8'");
            sb.AppendLine("        return response.json()");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        return {\"status\": \"error\", \"error\": str(e)}");
            sb.AppendLine();
            sb.AppendLine("def health() -> bool:");
            sb.AppendLine("    \"\"\"Check if Unity server is running.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.get(f\"{UNITY_URL}/health\", timeout=2)");
            sb.AppendLine("        response.encoding = 'utf-8'");
            sb.AppendLine("        return response.json().get(\"status\") == \"ok\"");
            sb.AppendLine("    except:");
            sb.AppendLine("        return False");
            sb.AppendLine();
            sb.AppendLine("def is_unity_running() -> bool:");
            sb.AppendLine("    \"\"\"Check if Unity REST server is running and ready.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.get(f'{UNITY_URL}/health', timeout=2)");
            sb.AppendLine("        return response.status_code == 200");
            sb.AppendLine("    except:");
            sb.AppendLine("        return False");
            sb.AppendLine();
            sb.AppendLine("def get_server_status() -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"Get detailed server status including version and stats.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.get(f'{UNITY_URL}/health', timeout=5)");
            sb.AppendLine("        response.encoding = 'utf-8'");
            sb.AppendLine("        return response.json()");
            sb.AppendLine("    except requests.exceptions.ConnectionError:");
            sb.AppendLine("        return {'status': 'offline', 'reason': 'Server not running or Unity recompiling'}");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        return {'status': 'error', 'reason': str(e)}");
            sb.AppendLine();

            // Convenience functions
            sb.AppendLine("# Convenience functions");
            sb.AppendLine("def create_gameobject(name, primitive_type=None, x=0, y=0, z=0):");
            sb.AppendLine("    return call_skill('gameobject_create', name=name, primitiveType=primitive_type, x=x, y=y, z=z)");
            sb.AppendLine();
            sb.AppendLine("def delete_gameobject(name):");
            sb.AppendLine("    return call_skill('gameobject_delete', name=name)");
            sb.AppendLine();
            sb.AppendLine("def set_color(game_object, r, g, b, a=1):");
            sb.AppendLine("    return call_skill('material_set_color', gameObjectName=game_object, r=r, g=g, b=b, a=a)");
            sb.AppendLine();
            sb.AppendLine("def create_script(name, template='MonoBehaviour', wait_for_compile=True):");
            sb.AppendLine("    \"\"\"Create a C# script and optionally wait for Unity to recompile.\"\"\"");
            sb.AppendLine("    result = call_skill('script_create', name=name, template=template)");
            sb.AppendLine("    if result.get('success') and wait_for_compile:");
            sb.AppendLine("        print(f'Script {name} created. Waiting for Unity to recompile...')");
            sb.AppendLine("        time.sleep(2)  # Give Unity time to detect the new file");
            sb.AppendLine("        if wait_for_unity(timeout=10):");
            sb.AppendLine("            print('Unity recompiled successfully.')");
            sb.AppendLine("        else:");
            sb.AppendLine("            print('Warning: Unity might still be compiling. Wait a moment before next operation.')");
            sb.AppendLine("    return result");
            sb.AppendLine();
            sb.AppendLine("def play():");
            sb.AppendLine("    return call_skill('editor_play')");
            sb.AppendLine();
            sb.AppendLine("def stop():");
            sb.AppendLine("    return call_skill('editor_stop')");
            sb.AppendLine();

            // CLI entry point
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# Main CLI Entry Point");
            sb.AppendLine("# ============================================================");
            sb.AppendLine("def main():");
            sb.AppendLine("    \"\"\"Command-line interface for Unity Skills.\"\"\"");
            sb.AppendLine("    if len(sys.argv) < 2:");
            sb.AppendLine("        print('Usage: python unity_skills.py <skill_name> [param1=value1] [param2=value2] ...')");
            sb.AppendLine("        print('Example: python unity_skills.py editor_get_selection')");
            sb.AppendLine("        print('Example: python unity_skills.py gameobject_create name=MyCube primitiveType=Cube')");
            sb.AppendLine("        sys.exit(1)");
            sb.AppendLine();
            sb.AppendLine("    if sys.argv[1] == \"--list\":");
            sb.AppendLine("        print(json.dumps(get_skills(), ensure_ascii=False, indent=2))");
            sb.AppendLine("        return");
            sb.AppendLine("    elif sys.argv[1] == \"--list-instances\":");
            sb.AppendLine("        print(json.dumps(list_instances(), ensure_ascii=False, indent=2))");
            sb.AppendLine("        return");
            sb.AppendLine();
            sb.AppendLine("    skill_name = sys.argv[1]");
            sb.AppendLine();
            sb.AppendLine("    # Parse parameters");
            sb.AppendLine("    params = {}");
            sb.AppendLine("    for arg in sys.argv[2:]:");
            sb.AppendLine("        if '=' in arg:");
            sb.AppendLine("            key, value = arg.split('=', 1)");
            sb.AppendLine("            # Try to parse as number or boolean");
            sb.AppendLine("            if value.lower() == 'true':");
            sb.AppendLine("                value = True");
            sb.AppendLine("            elif value.lower() == 'false':");
            sb.AppendLine("                value = False");
            sb.AppendLine("            elif value.replace('.', '', 1).replace('-', '', 1).isdigit():");
            sb.AppendLine("                try:");
            sb.AppendLine("                    value = float(value) if '.' in value else int(value)");
            sb.AppendLine("                except ValueError:");
            sb.AppendLine("                    pass");
            sb.AppendLine("            params[key] = value");
            sb.AppendLine();
            sb.AppendLine("    # Call the skill");
            sb.AppendLine("    result = call_skill(skill_name, **params)");
            sb.AppendLine();
            sb.AppendLine("    # Pretty print the result");
            sb.AppendLine("    print(json.dumps(result, ensure_ascii=False, indent=2))");
            sb.AppendLine();
            sb.AppendLine("if __name__ == '__main__':");
            sb.AppendLine("    main()");

            return sb.ToString();
        }
    }
}
