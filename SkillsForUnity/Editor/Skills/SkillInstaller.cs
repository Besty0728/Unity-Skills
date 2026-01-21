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
        // Claude Code paths
        public static string ClaudeProjectPath => Path.Combine(Application.dataPath, "..", ".claude", "skills", "unityskills");
        public static string ClaudeGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "unityskills");
        
        // Antigravity paths
        public static string AntigravityProjectPath => Path.Combine(Application.dataPath, "..", ".agent", "skills", "unityskills");
        public static string AntigravityGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity", "skills", "unityskills");

        // Gemini CLI paths - folder name must match SKILL.md name field
        public static string GeminiProjectPath => Path.Combine(Application.dataPath, "..", ".gemini", "skills", "unityskills");
        public static string GeminiGlobalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "skills", "unityskills");

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
            // Gemini CLI requires: lowercase, alphanumeric only (no dashes for simplicity)
            sb.AppendLine("name: unityskills");
            sb.AppendLine("description: Control Unity Editor via REST API. Create GameObjects, manage scenes, materials, prefabs, scripts. Use when user asks for Unity projects, scene manipulation, GameObject creation, material setup, UI building, lighting, animation.");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# Unity Editor Control Skill");
            sb.AppendLine();
            sb.AppendLine("This skill enables AI to control Unity Editor through a REST API.");
            sb.AppendLine();
            sb.AppendLine("## Prerequisites");
            sb.AppendLine();
            sb.AppendLine("1. Install the UnitySkills package in Unity");
            sb.AppendLine("2. Start the REST server: Window > UnitySkills > Start Server");
            sb.AppendLine("3. Server runs at: http://localhost:8090");
            sb.AppendLine();
            sb.AppendLine("## ⚠️ Important: Server Auto-Restart Behavior");
            sb.AppendLine();
            sb.AppendLine("When you create or modify C# scripts using `script_create` skill, Unity will **recompile all scripts**.");
            sb.AppendLine("During recompilation (Domain Reload), the REST server temporarily stops and automatically restarts.");
            sb.AppendLine();
            sb.AppendLine("**What happens:**");
            sb.AppendLine("1. `script_create` succeeds and returns success");
            sb.AppendLine("2. Unity starts compiling scripts (2-5 seconds)");
            sb.AppendLine("3. Server stops briefly → **Connection Refused** errors are NORMAL");
            sb.AppendLine("4. Server auto-restarts after compilation completes");
            sb.AppendLine();
            sb.AppendLine("**How to handle:**");
            sb.AppendLine("- If you get `Connection Refused` after creating a script, **wait 3-5 seconds and retry**");
            sb.AppendLine("- Use the `/health` endpoint to check if server is ready: `curl http://localhost:8090/health`");
            sb.AppendLine("- This is normal Unity behavior, not an error");
            sb.AppendLine();
            sb.AppendLine("```python");
            sb.AppendLine("# Recommended: Add retry logic after script creation");
            sb.AppendLine("import time");
            sb.AppendLine("result = call_skill('script_create', name='MyScript', template='MonoBehaviour')");
            sb.AppendLine("if result.get('success'):");
            sb.AppendLine("    print('Script created, waiting for Unity to recompile...')");
            sb.AppendLine("    time.sleep(3)  # Wait for Domain Reload");
            sb.AppendLine("    # Then continue with other operations");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Usage");
            sb.AppendLine();
            sb.AppendLine("```python");
            sb.AppendLine("import sys");
            sb.AppendLine("sys.path.insert(0, 'scripts')");
            sb.AppendLine("from unity_skills import call_skill");
            sb.AppendLine();
            sb.AppendLine("# Create a cube");
            sb.AppendLine("call_skill('gameobject_create', name='MyCube', primitiveType='Cube', x=0, y=1, z=0)");
            sb.AppendLine();
            sb.AppendLine("# Set color");
            sb.AppendLine("call_skill('material_set_color', gameObjectName='MyCube', r=1, g=0, b=0)");
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
            sb.AppendLine("## Direct REST API");
            sb.AppendLine();
            sb.AppendLine("```bash");
            sb.AppendLine("curl http://localhost:8090/skills");
            sb.AppendLine("curl -X POST http://localhost:8090/skill/gameobject_create -d '{\"name\":\"Cube\"}'");
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
