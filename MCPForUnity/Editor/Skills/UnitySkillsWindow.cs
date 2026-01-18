using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Unity Editor Window for UnitySkills REST API control.
    /// </summary>
    public class UnitySkillsWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private bool _serverRunning;
        private string _testSkillName = "";
        private string _testSkillParams = "{}";
        private string _testResult = "";
        private Dictionary<string, List<SkillInfo>> _skillsByCategory;
        private Dictionary<string, bool> _categoryFoldouts = new Dictionary<string, bool>();
        private bool _showSkillConfig = true;
        private int _selectedTab = 0;
        private string[] _tabNames = new[] { "Server", "Skills", "AI Config" };

        private class SkillInfo
        {
            public string Name;
            public string Description;
            public MethodInfo Method;
        }

        [MenuItem("Window/UnitySkills")]
        public static void ShowWindow()
        {
            var window = GetWindow<UnitySkillsWindow>("UnitySkills");
            window.minSize = new Vector2(450, 550);
        }

        private void OnEnable()
        {
            RefreshSkillsList();
            _serverRunning = SkillsHttpServer.IsRunning;
        }

        private void RefreshSkillsList()
        {
            _skillsByCategory = new Dictionary<string, List<SkillInfo>>();

            var allTypes = System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new System.Type[0]; } });

            foreach (var type in allTypes)
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var attr = method.GetCustomAttribute<UnitySkillAttribute>();
                    if (attr != null)
                    {
                        var category = type.Name.Replace("Skills", "");
                        if (!_skillsByCategory.ContainsKey(category))
                            _skillsByCategory[category] = new List<SkillInfo>();

                        _skillsByCategory[category].Add(new SkillInfo
                        {
                            Name = attr.Name ?? method.Name,
                            Description = attr.Description ?? "",
                            Method = method
                        });
                    }
                }
            }

            foreach (var cat in _skillsByCategory.Keys)
            {
                if (!_categoryFoldouts.ContainsKey(cat))
                    _categoryFoldouts[cat] = false;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            // Header with language toggle
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("UnitySkills", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            
            // Language toggle
            var langLabel = Localization.Current == Localization.Language.English ? "EN" : "中";
            if (GUILayout.Button(langLabel, GUILayout.Width(35)))
            {
                Localization.Current = Localization.Current == Localization.Language.English 
                    ? Localization.Language.Chinese 
                    : Localization.Language.English;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Tab bar
            _selectedTab = GUILayout.Toolbar(_selectedTab, new[] {
                Localization.Current == Localization.Language.Chinese ? "服务器" : "Server",
                Localization.Current == Localization.Language.Chinese ? "Skills" : "Skills",
                Localization.Current == Localization.Language.Chinese ? "AI配置" : "AI Config"
            });

            EditorGUILayout.Space(10);

            switch (_selectedTab)
            {
                case 0: DrawServerTab(); break;
                case 1: DrawSkillsTab(); break;
                case 2: DrawAIConfigTab(); break;
            }
        }

        private void DrawServerTab()
        {
            // Server Status
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            
            var statusStyle = new GUIStyle(EditorStyles.boldLabel);
            statusStyle.normal.textColor = _serverRunning ? Color.green : Color.red;
            GUILayout.Label(_serverRunning ? L("server_running") : L("server_stopped"), statusStyle);
            
            GUILayout.FlexibleSpace();
            
            if (_serverRunning)
            {
                if (GUILayout.Button(L("stop_server"), GUILayout.Width(100)))
                {
                    SkillsHttpServer.Stop();
                    _serverRunning = false;
                }
            }
            else
            {
                if (GUILayout.Button(L("start_server"), GUILayout.Width(100)))
                {
                    SkillsHttpServer.Start();
                    _serverRunning = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_serverRunning)
            {
                EditorGUILayout.SelectableLabel(SkillsHttpServer.Url, EditorStyles.miniLabel, GUILayout.Height(18));
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Test Skill Section
            EditorGUILayout.LabelField(L("test_skill"), EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            _testSkillName = EditorGUILayout.TextField(L("skill_name"), _testSkillName);
            EditorGUILayout.LabelField(L("parameters_json") + ":");
            _testSkillParams = EditorGUILayout.TextArea(_testSkillParams, GUILayout.Height(60));
            
            if (GUILayout.Button(L("execute_skill")))
            {
                _testResult = SkillRouter.Execute(_testSkillName, _testSkillParams);
            }

            if (!string.IsNullOrEmpty(_testResult))
            {
                EditorGUILayout.LabelField(L("result") + ":");
                EditorGUILayout.TextArea(_testResult, GUILayout.Height(80));
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawSkillsTab()
        {
            // Skills List
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("available_skills"), EditorStyles.boldLabel);
            if (GUILayout.Button(L("refresh"), GUILayout.Width(60)))
            {
                RefreshSkillsList();
                SkillRouter.Refresh();
            }
            EditorGUILayout.EndHorizontal();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            if (_skillsByCategory != null)
            {
                int totalSkills = _skillsByCategory.Values.Sum(l => l.Count);
                EditorGUILayout.LabelField(string.Format(L("total_skills"), totalSkills, _skillsByCategory.Count), EditorStyles.miniLabel);

                foreach (var kvp in _skillsByCategory.OrderBy(k => k.Key))
                {
                    _categoryFoldouts[kvp.Key] = EditorGUILayout.Foldout(_categoryFoldouts[kvp.Key], $"{kvp.Key} ({kvp.Value.Count})", true);
                    
                    if (_categoryFoldouts[kvp.Key])
                    {
                        EditorGUI.indentLevel++;
                        foreach (var skill in kvp.Value)
                        {
                            EditorGUILayout.BeginHorizontal();
                            EditorGUILayout.LabelField(skill.Name, EditorStyles.boldLabel);
                            if (GUILayout.Button(L("use"), GUILayout.Width(40)))
                            {
                                _testSkillName = skill.Name;
                                _testSkillParams = BuildDefaultParams(skill.Method);
                                _selectedTab = 0; // Switch to server tab
                            }
                            EditorGUILayout.EndHorizontal();
                            
                            // Use localized description if available
                            var desc = Localization.Get(skill.Name);
                            if (desc == skill.Name) desc = skill.Description; // Fallback to original
                            EditorGUILayout.LabelField(desc, EditorStyles.miniLabel);
                            EditorGUILayout.Space(3);
                        }
                        EditorGUI.indentLevel--;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawAIConfigTab()
        {
            EditorGUILayout.LabelField(L("skill_config"), EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            // Claude Code
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Claude Code", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_project") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsClaudeProjectInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(80));
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Claude Code (Project)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallClaude(false);
                        if (result.success)
                            EditorUtility.DisplayDialog("Success", L("uninstall_success"), "OK");
                        else
                            EditorUtility.DisplayDialog("Error", string.Format(L("uninstall_failed"), result.message), "OK");
                    }
                }
            }
            else
            {
                if (GUILayout.Button(L("install_project"), GUILayout.Width(120)))
                {
                    var result = SkillInstaller.InstallClaude(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message, "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_global") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsClaudeGlobalInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(80));
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Claude Code (Global)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallClaude(true);
                        if (result.success)
                            EditorUtility.DisplayDialog("Success", L("uninstall_success"), "OK");
                        else
                            EditorUtility.DisplayDialog("Error", string.Format(L("uninstall_failed"), result.message), "OK");
                    }
                }
            }
            else
            {
                if (GUILayout.Button(L("install_global"), GUILayout.Width(120)))
                {
                    var result = SkillInstaller.InstallClaude(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message, "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Antigravity
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Antigravity", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_project") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsAntigravityProjectInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(80));
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Antigravity (Project)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallAntigravity(false);
                        if (result.success)
                            EditorUtility.DisplayDialog("Success", L("uninstall_success"), "OK");
                        else
                            EditorUtility.DisplayDialog("Error", string.Format(L("uninstall_failed"), result.message), "OK");
                    }
                }
            }
            else
            {
                if (GUILayout.Button(L("install_project"), GUILayout.Width(120)))
                {
                    var result = SkillInstaller.InstallAntigravity(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message, "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_global") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsAntigravityGlobalInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(80));
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Antigravity (Global)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallAntigravity(true);
                        if (result.success)
                            EditorUtility.DisplayDialog("Success", L("uninstall_success"), "OK");
                        else
                            EditorUtility.DisplayDialog("Error", string.Format(L("uninstall_failed"), result.message), "OK");
                    }
                }
            }
            else
            {
                if (GUILayout.Button(L("install_global"), GUILayout.Width(120)))
                {
                    var result = SkillInstaller.InstallAntigravity(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message, "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(20);
            
            // Help text
            EditorGUILayout.HelpBox(
                Localization.Current == Localization.Language.Chinese
                    ? "项目安装：将 Skill 安装到当前 Unity 项目目录\n全局安装：将 Skill 安装到用户目录，所有项目可用"
                    : "Project Install: Install skill to current Unity project\nGlobal Install: Install skill to user folder, available for all projects",
                MessageType.Info
            );
        }

        private string L(string key) => Localization.Get(key);

        private string BuildDefaultParams(MethodInfo method)
        {
            var ps = method.GetParameters();
            if (ps.Length == 0) return "{}";

            var parts = ps.Select(p =>
            {
                var defaultVal = p.HasDefaultValue ? p.DefaultValue : GetDefaultForType(p.ParameterType);
                var valStr = defaultVal == null ? "null" :
                    p.ParameterType == typeof(string) ? $"\"{defaultVal}\"" :
                    defaultVal.ToString().ToLower();
                return $"\"{p.Name}\": {valStr}";
            });

            return "{\n  " + string.Join(",\n  ", parts) + "\n}";
        }

        private object GetDefaultForType(System.Type t)
        {
            if (t == typeof(string)) return "";
            if (t == typeof(int) || t == typeof(float)) return 0;
            if (t == typeof(bool)) return false;
            return null;
        }

        private void OnInspectorUpdate()
        {
            if (_serverRunning != SkillsHttpServer.IsRunning)
            {
                _serverRunning = SkillsHttpServer.IsRunning;
                Repaint();
            }
        }
    }
}
