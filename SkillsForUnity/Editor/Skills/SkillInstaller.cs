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

        // Gemini CLI paths - folder name should match SKILL.md name field for proper recognition
        public static string GeminiProjectPath => Path.Combine(Application.dataPath, "..", ".gemini", "skills", "unity-skills");
        public static string GeminiGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "skills", "unity-skills");

        public static bool IsClaudeProjectInstalled => Directory.Exists(ClaudeProjectPath) && File.Exists(Path.Combine(ClaudeProjectPath, "SKILL.md"));
        public static bool IsClaudeGlobalInstalled => Directory.Exists(ClaudeGlobalPath) && File.Exists(Path.Combine(ClaudeGlobalPath, "SKILL.md"));
        public static bool IsAntigravityProjectInstalled => Directory.Exists(AntigravityProjectPath) && File.Exists(Path.Combine(AntigravityProjectPath, "SKILL.md"));
        public static bool IsAntigravityGlobalInstalled => Directory.Exists(AntigravityGlobalPath) && File.Exists(Path.Combine(AntigravityGlobalPath, "SKILL.md"));
        public static bool IsGeminiProjectInstalled => Directory.Exists(GeminiProjectPath) && File.Exists(Path.Combine(GeminiProjectPath, "SKILL.md"));
        public static bool IsGeminiGlobalInstalled => Directory.Exists(GeminiGlobalPath) && File.Exists(Path.Combine(GeminiGlobalPath, "SKILL.md"));

        public static (bool success, string message) InstallClaude(bool global)
        {
            try
            {
                var targetPath = global ? ClaudeGlobalPath : ClaudeProjectPath;
                return InstallSkill(targetPath, "Claude Code");
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
                return InstallSkill(targetPath, "Antigravity");
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

        public static (bool success, string message) InstallGemini(bool global)
        {
            try
            {
                var targetPath = global ? GeminiGlobalPath : GeminiProjectPath;
                return InstallSkill(targetPath, "Gemini CLI");
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

        private static (bool success, string message) UninstallSkill(string targetPath, string name)
        {
            if (!Directory.Exists(targetPath))
                return (false, $"{name} skill not installed at this location");

            Directory.Delete(targetPath, true);
            Debug.Log("[UnitySkills] Uninstalled skill from: " + targetPath);
            return (true, targetPath);
        }

        private static (bool success, string message) InstallSkill(string targetPath, string name)
        {
            if (!Directory.Exists(targetPath))
                Directory.CreateDirectory(targetPath);

            var skillMd = GenerateSkillMd();
            File.WriteAllText(Path.Combine(targetPath, "SKILL.md"), skillMd, Encoding.UTF8);

            var pythonHelper = GeneratePythonHelper();
            var scriptsPath = Path.Combine(targetPath, "scripts");
            if (!Directory.Exists(scriptsPath))
                Directory.CreateDirectory(scriptsPath);
            File.WriteAllText(Path.Combine(scriptsPath, "unity_skills.py"), pythonHelper, Encoding.UTF8);

            Debug.Log("[UnitySkills] Installed skill to: " + targetPath);
            return (true, targetPath);
        }

        private static string GenerateSkillMd()
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            // Gemini CLI requires: lowercase, alphanumeric and dashes only
            sb.AppendLine("name: unity-skills");
            // CRITICAL: Description must be detailed enough for Gemini to understand WHEN to activate this skill
            sb.AppendLine("description: |");
            sb.AppendLine("  Expert Unity Editor automation via REST API. Use this skill when the user asks to:");
            sb.AppendLine("  - Create, modify, delete, or find GameObjects in Unity scenes");
            sb.AppendLine("  - Add, remove, or configure components (Rigidbody, Collider, Light, etc.)");
            sb.AppendLine("  - Manage scenes (create, load, save, take screenshots)");
            sb.AppendLine("  - Create and modify materials, textures, colors, and shaders");
            sb.AppendLine("  - Work with prefabs (create, instantiate, apply changes)");
            sb.AppendLine("  - Set up lighting (directional, point, spot lights)");
            sb.AppendLine("  - Create animation controllers and manage animator parameters");
            sb.AppendLine("  - Build UI elements (Canvas, buttons, text, panels, sliders)");
            sb.AppendLine("  - Control Unity Editor (play/stop, selection, undo/redo)");
            sb.AppendLine("  - Create or modify C# scripts and shaders");
            sb.AppendLine("  - Validate projects and clean up assets");
            sb.AppendLine("  This skill provides 100+ Unity automation tools accessible via Python scripts in the scripts/ directory.");
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

        private static string GeneratePythonHelper()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Unity Skills Python Helper");
            sb.AppendLine("# Auto-generated by UnitySkills");
            sb.AppendLine("import requests");
            sb.AppendLine("import time");
            sb.AppendLine("from typing import Any, Dict, Optional");
            sb.AppendLine();
            sb.AppendLine("UNITY_URL = 'http://localhost:8090'");
            sb.AppendLine();
            sb.AppendLine("def call_skill(skill_name: str, **kwargs) -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"Call a Unity skill with automatic retry on connection errors.\"\"\"");
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.post(f'{UNITY_URL}/skill/{skill_name}', json=kwargs, timeout=30)");
            sb.AppendLine("        return response.json()");
            sb.AppendLine("    except requests.exceptions.ConnectionError:");
            sb.AppendLine("        return {");
            sb.AppendLine("            'error': 'Cannot connect to Unity REST server.',");
            sb.AppendLine("            'suggestion': 'Unity may be recompiling scripts (Domain Reload). Wait 3-5 seconds and retry.',");
            sb.AppendLine("            'hint': 'Check if server is running: Window > UnitySkills > Start Server'");
            sb.AppendLine("        }");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        return {'error': str(e)}");
            sb.AppendLine();
            sb.AppendLine("def call_skill_with_retry(skill_name: str, max_retries: int = 3, retry_delay: float = 2.0, **kwargs) -> Dict[str, Any]:");
            sb.AppendLine("    \"\"\"Call a Unity skill with automatic retry logic for Domain Reload scenarios.\"\"\"");
            sb.AppendLine("    for attempt in range(max_retries):");
            sb.AppendLine("        result = call_skill(skill_name, **kwargs)");
            sb.AppendLine("        if 'error' not in result or 'Cannot connect' not in result.get('error', ''):");
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
            sb.AppendLine("    try:");
            sb.AppendLine("        response = requests.get(f'{UNITY_URL}/skills', timeout=10)");
            sb.AppendLine("        return response.json()");
            sb.AppendLine("    except:");
            sb.AppendLine("        return {'error': 'Cannot connect to Unity'}");
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
            sb.AppendLine("        return response.json()");
            sb.AppendLine("    except requests.exceptions.ConnectionError:");
            sb.AppendLine("        return {'status': 'offline', 'reason': 'Server not running or Unity recompiling'}");
            sb.AppendLine("    except Exception as e:");
            sb.AppendLine("        return {'status': 'error', 'reason': str(e)}");
            sb.AppendLine();
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
            return sb.ToString();
        }
    }
}
