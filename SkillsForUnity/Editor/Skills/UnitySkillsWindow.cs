using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Unity Editor Window for UnitySkills REST API control.
    /// Also acts as a backup heartbeat to ensure server responsiveness.
    /// </summary>
    public class UnitySkillsWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private Vector2 _historyScrollPosition;
        private bool _serverRunning;
        private string _testSkillName = "";
        private string _testSkillParams = "{}";
        private string _testResult = "";
        private Dictionary<string, List<SkillInfo>> _skillsByCategory;
        private Dictionary<string, bool> _categoryFoldouts = new Dictionary<string, bool>();
        private bool _showSkillConfig = true;
        private int _selectedTab = 0;

        // Server monitoring
        private double _lastRepaintTime;
        private const double RepaintInterval = 0.5;
        private bool _autoStartServer = true;
        private string _customInstallPath = "";

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
            window.minSize = new Vector2(450, 500);
        }

        private void OnEnable()
        {
            RefreshSkillsList();
            _serverRunning = SkillsHttpServer.IsRunning;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            _serverRunning = SkillsHttpServer.IsRunning;
            if (_serverRunning && _selectedTab == 0)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - _lastRepaintTime > RepaintInterval)
                {
                    _lastRepaintTime = now;
                    Repaint();
                }
            }
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
            EditorGUILayout.Space(5);

            // Tab bar
            _selectedTab = GUILayout.Toolbar(_selectedTab, new[] {
                Localization.Current == Localization.Language.Chinese ? "服务器" : "Server",
                "Skills",
                Localization.Current == Localization.Language.Chinese ? "AI配置" : "AI Config",
                Localization.Current == Localization.Language.Chinese ? "历史记录" : "History"
            });

            EditorGUILayout.Space(10);

            switch (_selectedTab)
            {
                case 0: DrawServerTab(); break;
                case 1: DrawSkillsTab(); break;
                case 2: DrawAIConfigTab(); break;
                case 3: DrawHistoryTab(); break;
            }

            // Language toggle at bottom
            EditorGUILayout.Space(10);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var langLabel = Localization.Current == Localization.Language.English ? "EN / 中文" : "中文 / EN";
            if (GUILayout.Button(langLabel, EditorStyles.miniButton, GUILayout.Width(70)))
            {
                Localization.Current = Localization.Current == Localization.Language.English
                    ? Localization.Language.Chinese
                    : Localization.Language.English;
            }
            EditorGUILayout.EndHorizontal();
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
                    SkillsHttpServer.StopPermanent();
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
                EditorGUILayout.Space(5);

                // Connection Info
                var portLabel = Localization.Current == Localization.Language.Chinese ? "端口" : "Port";
                EditorGUILayout.LabelField($"{portLabel}: {SkillsHttpServer.Port}");
                EditorGUILayout.LabelField($"ID: {RegistryService.InstanceId}");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("URL:", GUILayout.Width(35));
                EditorGUILayout.SelectableLabel(SkillsHttpServer.Url, EditorStyles.textField, GUILayout.Height(18));
                if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "复制" : "Copy", GUILayout.Width(50)))
                {
                    EditorGUIUtility.systemCopyBuffer = RegistryService.InstanceId;
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                // Statistics
                EditorGUILayout.LabelField(L("server_stats"), EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"{L("queued_requests")}: {SkillsHttpServer.QueuedRequests}");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{L("total_processed")}: {SkillsHttpServer.TotalProcessed}");
                if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "重置" : "Reset", GUILayout.Width(50)))
                {
                    SkillsHttpServer.ResetStatistics();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(5);

            // Auto-restart setting
            var newAutoStart = EditorGUILayout.Toggle(L("auto_restart"), SkillsHttpServer.AutoStart);
            if (newAutoStart != SkillsHttpServer.AutoStart)
            {
                SkillsHttpServer.AutoStart = newAutoStart;
            }
            EditorGUILayout.LabelField(L("auto_restart_hint"), EditorStyles.miniLabel);

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
            // Header
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

                EditorGUILayout.Space(5);

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
                                _selectedTab = 0;
                            }
                            EditorGUILayout.EndHorizontal();

                            var desc = Localization.Get(skill.Name);
                            if (desc == skill.Name) desc = skill.Description;
                            if (!string.IsNullOrEmpty(desc))
                            {
                                EditorGUILayout.LabelField(desc, EditorStyles.miniLabel);
                            }
                            EditorGUILayout.Space(2);
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
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallClaude(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
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
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallClaude(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
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
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallAntigravity(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
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
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallAntigravity(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
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

            EditorGUILayout.Space(10);

            // Gemini CLI
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Gemini CLI", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_project") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsGeminiProjectInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallGemini(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Gemini CLI (Project)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallGemini(false);
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
                    var result = SkillInstaller.InstallGemini(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message, "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_global") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsGeminiGlobalInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallGemini(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Gemini CLI (Global)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallGemini(true);
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
                    var result = SkillInstaller.InstallGemini(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success") + "\n" + result.message + L("gemini_enable_hint"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Codex
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Codex", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            var experimentalStyle = new GUIStyle(EditorStyles.miniLabel);
            experimentalStyle.normal.textColor = new Color(1f, 0.6f, 0f); // Orange warning color
            EditorGUILayout.LabelField(L("install_project") + " (!):", GUILayout.Width(100));
            if (Localization.Current == Localization.Language.Chinese)
                EditorGUILayout.LabelField("实验性", experimentalStyle, GUILayout.Width(40));
            else
                EditorGUILayout.LabelField("Exp.", experimentalStyle, GUILayout.Width(30));
            if (SkillInstaller.IsCodexProjectInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallCodex(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Codex (Project)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallCodex(false);
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
                    var result = SkillInstaller.InstallCodex(false);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", 
                            Localization.Current == Localization.Language.Chinese
                                ? "安装成功！\n" + result.message + "\n\n如有问题请查看项目根目录的 AGENTS.md"
                                : "Install success!\n" + result.message + "\n\nIf issues occur, check AGENTS.md in project root.",
                            "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("install_global") + ":", GUILayout.Width(100));
            if (SkillInstaller.IsCodexGlobalInstalled)
            {
                EditorGUILayout.LabelField(L("installed"), EditorStyles.miniLabel, GUILayout.Width(60));
                if (GUILayout.Button(L("update"), GUILayout.Width(50)))
                {
                    var result = SkillInstaller.InstallCodex(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("update_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("update_failed"), result.message), "OK");
                }
                if (GUILayout.Button(L("uninstall"), GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(L("uninstall"), string.Format(L("uninstall_confirm"), "Codex (Global)"), "OK", "Cancel"))
                    {
                        var result = SkillInstaller.UninstallCodex(true);
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
                    var result = SkillInstaller.InstallCodex(true);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", 
                            Localization.Current == Localization.Language.Chinese
                                ? "安装成功！\n" + result.message + "\n\n请重启 Codex 以加载新 Skill。"
                                : "Install success!\n" + result.message + "\n\nPlease restart Codex to load new skills.",
                            "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            // Custom Installation
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(Localization.Current == Localization.Language.Chinese ? "自定义安装位置" : "Custom Install Location", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L("path") + ":", GUILayout.Width(50));
            _customInstallPath = EditorGUILayout.TextField(_customInstallPath);
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                string selectedPath = EditorUtility.OpenFolderPanel(
                    Localization.Current == Localization.Language.Chinese ? "选择安装目录" : "Select Install Directory", 
                    _customInstallPath, 
                    "");
                if (!string.IsNullOrEmpty(selectedPath))
                {
                    _customInstallPath = selectedPath;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "安装 / 更新" : "Install / Update"))
            {
                if (string.IsNullOrEmpty(_customInstallPath))
                {
                    EditorUtility.DisplayDialog("Error", Localization.Current == Localization.Language.Chinese ? "路径不能为空" : "Path cannot be empty", "OK");
                }
                else
                {
                    var result = SkillInstaller.InstallCustom(_customInstallPath);
                    if (result.success)
                        EditorUtility.DisplayDialog("Success", L("install_success"), "OK");
                    else
                        EditorUtility.DisplayDialog("Error", string.Format(L("install_failed"), result.message), "OK");
                }
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(20);
            
            // Help text
            EditorGUILayout.HelpBox(
                Localization.Current == Localization.Language.Chinese
                    ? "项目安装：将 Skill 安装到当前 Unity 项目目录\n全局安装：将 Skill 安装到用户目录，所有项目可用\n\n注意：Gemini CLI 需要在 /settings 中启用 experimental.skills\n注意：Codex 需要重启后才会加载新 Skill"
                    : "Project Install: Install skill to current Unity project\nGlobal Install: Install skill to user folder, available for all projects\n\nNote: Gemini CLI requires enabling experimental.skills in /settings\nNote: Codex requires restart to load new skills",
                MessageType.Info
            );
        }

        private void DrawHistoryTab()
        {
            var history = WorkflowManager.History;

            // Header
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Localization.Current == Localization.Language.Chinese ? "AI 操作历史" : "AI Operation History", EditorStyles.boldLabel);
            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "刷新" : "Refresh", GUILayout.Width(60)))
            {
                WorkflowManager.LoadHistory();
            }
            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "清空" : "Clear", GUILayout.Width(60)))
            {
                if (EditorUtility.DisplayDialog("Confirm", "Clear all history?", "Yes", "No"))
                    WorkflowManager.ClearHistory();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            _historyScrollPosition = EditorGUILayout.BeginScrollView(_historyScrollPosition);

            // Active tasks section
            if (history.tasks.Count > 0)
            {
                EditorGUILayout.LabelField(Localization.Current == Localization.Language.Chinese ? "活动任务" : "Active Tasks", EditorStyles.miniBoldLabel);

                for (int i = history.tasks.Count - 1; i >= 0; i--)
                {
                    var task = history.tasks[i];
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label($"[{task.GetFormattedTime()}]", EditorStyles.miniLabel, GUILayout.Width(65));
                    GUILayout.Label(task.tag, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{task.snapshots.Count} changes", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(task.description))
                    {
                        EditorGUILayout.LabelField(task.description, EditorStyles.wordWrappedMiniLabel);
                    }

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "撤销" : "Undo"))
                    {
                        if (EditorUtility.DisplayDialog("Confirm", $"Undo '{task.tag}'?", "Undo", "Cancel"))
                        {
                            bool success = WorkflowManager.UndoTask(task.id);
                            if (success) EditorUtility.DisplayDialog("Success", "Undo completed!", "OK");
                            else EditorUtility.DisplayDialog("Error", "Undo failed.", "OK");
                        }
                    }
                    if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "删除" : "Delete", GUILayout.Width(60)))
                    {
                        WorkflowManager.DeleteTask(task.id);
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(Localization.Current == Localization.Language.Chinese ? "暂无活动任务" : "No active tasks.", MessageType.Info);
            }

            // Undone tasks section (for redo)
            if (history.undoneStack != null && history.undoneStack.Count > 0)
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField(Localization.Current == Localization.Language.Chinese ? "已撤销任务 (可恢复)" : "Undone Tasks (Can Redo)", EditorStyles.miniBoldLabel);

                for (int i = history.undoneStack.Count - 1; i >= 0; i--)
                {
                    var task = history.undoneStack[i];
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    EditorGUILayout.BeginHorizontal();
                    var grayStyle = new GUIStyle(EditorStyles.miniLabel);
                    grayStyle.normal.textColor = Color.gray;
                    GUILayout.Label($"[{task.GetFormattedTime()}]", grayStyle, GUILayout.Width(65));
                    var grayBoldStyle = new GUIStyle(EditorStyles.boldLabel);
                    grayBoldStyle.normal.textColor = Color.gray;
                    GUILayout.Label(task.tag, grayBoldStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"{task.snapshots.Count} changes", grayStyle);
                    EditorGUILayout.EndHorizontal();

                    if (!string.IsNullOrEmpty(task.description))
                    {
                        var descStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
                        descStyle.normal.textColor = Color.gray;
                        EditorGUILayout.LabelField(task.description, descStyle);
                    }

                    if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "恢复" : "Redo"))
                    {
                        if (EditorUtility.DisplayDialog("Confirm", $"Redo '{task.tag}'?", "Redo", "Cancel"))
                        {
                            bool success = WorkflowManager.RedoTask(task.id);
                            if (success) EditorUtility.DisplayDialog("Success", "Redo completed!", "OK");
                            else EditorUtility.DisplayDialog("Error", "Redo failed.", "OK");
                        }
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(2);
                }
            }

            EditorGUILayout.EndScrollView();
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
