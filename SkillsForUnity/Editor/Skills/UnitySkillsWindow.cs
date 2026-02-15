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
        private string _customAgentName = "Custom";

        // Colors
        private static readonly Color SuccessColor = new Color(0.3f, 0.8f, 0.4f);
        private static readonly Color ErrorColor = new Color(0.9f, 0.3f, 0.3f);
        private static readonly Color WarningColor = new Color(1f, 0.7f, 0.2f);
        private static readonly Color AccentColor = new Color(0.4f, 0.6f, 0.9f);
        private static readonly Color MutedColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color HeaderBgColor = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color CardBgColor = new Color(0.25f, 0.25f, 0.25f);

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
            // Server Status Card
            DrawColoredBox(HeaderBgColor, () =>
            {
                EditorGUILayout.BeginHorizontal();

                // Status indicator
                var statusColor = _serverRunning ? SuccessColor : ErrorColor;
                var statusText = _serverRunning ? L("server_running") : L("server_stopped");
                DrawColoredLabel(statusText, statusColor, true);

                GUILayout.FlexibleSpace();

                // Control button
                var originalBg = GUI.backgroundColor;
                GUI.backgroundColor = _serverRunning ? new Color(0.9f, 0.5f, 0.5f) : new Color(0.5f, 0.9f, 0.5f);
                if (_serverRunning)
                {
                    if (GUILayout.Button(L("stop_server"), GUILayout.Width(100), GUILayout.Height(24)))
                    {
                        SkillsHttpServer.StopPermanent();
                        _serverRunning = false;
                    }
                }
                else
                {
                    if (GUILayout.Button(L("start_server"), GUILayout.Width(100), GUILayout.Height(24)))
                    {
                        SkillsHttpServer.Start(SkillsHttpServer.PreferredPort);
                        _serverRunning = true;
                    }
                }
                GUI.backgroundColor = originalBg;
                EditorGUILayout.EndHorizontal();
            });

            if (_serverRunning)
            {
                EditorGUILayout.Space(8);

                // Connection Info Card
                DrawColoredBox(CardBgColor, () =>
                {
                    DrawColoredLabel(Localization.Current == Localization.Language.Chinese ? "连接信息" : "Connection Info", AccentColor, true);
                    EditorGUILayout.Space(4);

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
                });

                EditorGUILayout.Space(8);

                // Statistics Card
                DrawColoredBox(CardBgColor, () =>
                {
                    DrawColoredLabel(L("server_stats"), AccentColor, true);
                    EditorGUILayout.Space(4);

                    EditorGUILayout.LabelField($"{L("queued_requests")}: {SkillsHttpServer.QueuedRequests}");

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"{L("total_processed")}: {SkillsHttpServer.TotalProcessed}");
                    if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "重置" : "Reset", GUILayout.Width(50)))
                    {
                        SkillsHttpServer.ResetStatistics();
                    }
                    EditorGUILayout.EndHorizontal();
                });
            }

            EditorGUILayout.Space(8);

            // Settings Card
            DrawColoredBox(CardBgColor, () =>
            {
                DrawColoredLabel(Localization.Current == Localization.Language.Chinese ? "设置" : "Settings", AccentColor, true);
                EditorGUILayout.Space(4);

                var newAutoStart = EditorGUILayout.Toggle(L("auto_restart"), SkillsHttpServer.AutoStart);
                if (newAutoStart != SkillsHttpServer.AutoStart)
                {
                    SkillsHttpServer.AutoStart = newAutoStart;
                }
                DrawColoredLabel(L("auto_restart_hint"), MutedColor, false);

                // Preferred Port
                EditorGUILayout.BeginHorizontal();
                var portLabel = Localization.Current == Localization.Language.Chinese ? "启动端口" : "Port";
                EditorGUILayout.LabelField(portLabel + ":", GUILayout.Width(60));
                var portOptions = new[] { "Auto", "8090", "8091", "8092", "8093", "8094", "8095", "8096", "8097", "8098", "8099", "8100" };
                var currentIdx = SkillsHttpServer.PreferredPort == 0 ? 0 : SkillsHttpServer.PreferredPort - 8089;
                var newIdx = EditorGUILayout.Popup(currentIdx, portOptions);
                if (newIdx != currentIdx)
                {
                    SkillsHttpServer.PreferredPort = newIdx == 0 ? 0 : 8089 + newIdx;
                }
                EditorGUILayout.EndHorizontal();

                // Request Timeout
                EditorGUILayout.BeginHorizontal();
                var timeoutLabel = Localization.Current == Localization.Language.Chinese ? "请求超时" : "Timeout";
                EditorGUILayout.LabelField(timeoutLabel + ":", GUILayout.Width(60));
                var newTimeout = EditorGUILayout.IntField(SkillsHttpServer.RequestTimeoutMinutes, GUILayout.Width(40));
                EditorGUILayout.LabelField(L("timeout_unit"), GUILayout.Width(30));
                if (newTimeout != SkillsHttpServer.RequestTimeoutMinutes)
                {
                    SkillsHttpServer.RequestTimeoutMinutes = newTimeout;
                }
                EditorGUILayout.EndHorizontal();

                // Log Level
                EditorGUILayout.BeginHorizontal();
                var logLabel = Localization.Current == Localization.Language.Chinese ? "日志级别" : "Log Level";
                EditorGUILayout.LabelField(logLabel + ":", GUILayout.Width(60));
                var logOptions = new[] { "Off", "Error", "Warning", "Info", "Agent", "Verbose" };
                var newLogLevel = (LogLevel)EditorGUILayout.Popup((int)SkillsLogger.Level, logOptions);
                if (newLogLevel != SkillsLogger.Level)
                {
                    SkillsLogger.Level = newLogLevel;
                }
                EditorGUILayout.EndHorizontal();
            });

            EditorGUILayout.Space(10);

            // Test Skill Section
            DrawColoredLabel(L("test_skill"), AccentColor, true);
            EditorGUILayout.Space(4);

            DrawColoredBox(CardBgColor, () =>
            {
                _testSkillName = EditorGUILayout.TextField(L("skill_name"), _testSkillName);
                EditorGUILayout.LabelField(L("parameters_json") + ":");
                _testSkillParams = EditorGUILayout.TextArea(_testSkillParams, GUILayout.Height(60));

                var originalBg = GUI.backgroundColor;
                GUI.backgroundColor = AccentColor;
                if (GUILayout.Button(L("execute_skill"), GUILayout.Height(26)))
                {
                    _testResult = SkillRouter.Execute(_testSkillName, _testSkillParams);
                }
                GUI.backgroundColor = originalBg;

                if (!string.IsNullOrEmpty(_testResult))
                {
                    EditorGUILayout.Space(5);
                    EditorGUILayout.LabelField(L("result") + ":");
                    EditorGUILayout.TextArea(_testResult, GUILayout.Height(80));
                }
            });
        }

        private void DrawSkillsTab()
        {
            // Header
            EditorGUILayout.BeginHorizontal();
            DrawColoredLabel(L("available_skills"), AccentColor, true);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L("refresh"), GUILayout.Width(60)))
            {
                RefreshSkillsList();
                SkillRouter.Refresh();
            }
            EditorGUILayout.EndHorizontal();

            if (_skillsByCategory != null)
            {
                int totalSkills = _skillsByCategory.Values.Sum(l => l.Count);
                DrawColoredLabel(string.Format(L("total_skills"), totalSkills, _skillsByCategory.Count), MutedColor, false);
            }

            EditorGUILayout.Space(8);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            if (_skillsByCategory != null)
            {
                foreach (var kvp in _skillsByCategory.OrderBy(k => k.Key))
                {
                    DrawColoredBox(CardBgColor, () =>
                    {
                        EditorGUILayout.BeginHorizontal();
                        _categoryFoldouts[kvp.Key] = EditorGUILayout.Foldout(_categoryFoldouts[kvp.Key], "", true);
                        DrawColoredLabel($"{kvp.Key}", AccentColor, true);
                        GUILayout.FlexibleSpace();
                        DrawColoredLabel($"{kvp.Value.Count} skills", MutedColor, false);
                        EditorGUILayout.EndHorizontal();

                        if (_categoryFoldouts[kvp.Key])
                        {
                            EditorGUILayout.Space(4);
                            foreach (var skill in kvp.Value)
                            {
                                EditorGUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField(skill.Name, EditorStyles.boldLabel);
                                var originalBg = GUI.backgroundColor;
                                GUI.backgroundColor = AccentColor;
                                if (GUILayout.Button(L("use"), GUILayout.Width(45)))
                                {
                                    _testSkillName = skill.Name;
                                    _testSkillParams = BuildDefaultParams(skill.Method);
                                    _selectedTab = 0;
                                }
                                GUI.backgroundColor = originalBg;
                                EditorGUILayout.EndHorizontal();

                                var desc = Localization.Get(skill.Name);
                                if (desc == skill.Name) desc = skill.Description;
                                if (!string.IsNullOrEmpty(desc))
                                {
                                    DrawColoredLabel("  " + desc, MutedColor, false);
                                }
                                EditorGUILayout.Space(4);
                            }
                        }
                    });
                    EditorGUILayout.Space(4);
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

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Localization.Current == Localization.Language.Chinese ? "Agent:" : "Agent:", GUILayout.Width(50));
            _customAgentName = EditorGUILayout.TextField(_customAgentName);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "安装 / 更新" : "Install / Update"))
            {
                if (string.IsNullOrEmpty(_customInstallPath))
                {
                    EditorUtility.DisplayDialog("Error", Localization.Current == Localization.Language.Chinese ? "路径不能为空" : "Path cannot be empty", "OK");
                }
                else
                {
                    var result = SkillInstaller.InstallCustom(_customInstallPath, _customAgentName);
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
            DrawColoredLabel(Localization.Current == Localization.Language.Chinese ? "AI 操作历史" : "AI Operation History", AccentColor, true);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "刷新" : "Refresh", GUILayout.Width(60)))
            {
                WorkflowManager.LoadHistory();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            // Warning box about workflow cache
            EditorGUILayout.HelpBox(
                Localization.Current == Localization.Language.Chinese
                    ? "⚠️ 工作流缓存说明：\n• 缓存包含资产文件的完整备份（Base64编码），可能占用较大空间\n• 清除缓存后将无法撤销/恢复之前的 AI 操作\n• 脚本文件(.cs)不会被备份，仅记录元数据"
                    : "⚠️ Workflow Cache Info:\n• Cache contains full asset backups (Base64 encoded), may use significant space\n• Clearing cache will prevent undo/redo of previous AI operations\n• Script files (.cs) are not backed up, only metadata is recorded",
                MessageType.Warning
            );

            EditorGUILayout.Space(4);

            // Clear cache button
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var originalBg = GUI.backgroundColor;
            GUI.backgroundColor = ErrorColor;
            if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "🗑️ 清除工作流缓存" : "🗑️ Clear Workflow Cache", GUILayout.Width(160), GUILayout.Height(24)))
            {
                var confirmMsg = Localization.Current == Localization.Language.Chinese
                    ? "确定要清除所有工作流缓存吗？\n\n⚠️ 警告：此操作不可逆！\n• 所有 AI 操作历史将被删除\n• 无法再撤销/恢复之前的操作\n• 资产备份数据将被清除"
                    : "Are you sure you want to clear all workflow cache?\n\n⚠️ Warning: This action is irreversible!\n• All AI operation history will be deleted\n• Cannot undo/redo previous operations\n• Asset backup data will be cleared";

                if (EditorUtility.DisplayDialog(
                    Localization.Current == Localization.Language.Chinese ? "清除工作流缓存" : "Clear Workflow Cache",
                    confirmMsg,
                    Localization.Current == Localization.Language.Chinese ? "确定清除" : "Clear",
                    Localization.Current == Localization.Language.Chinese ? "取消" : "Cancel"))
                {
                    WorkflowManager.ClearHistory();
                    EditorUtility.DisplayDialog(
                        "Success",
                        Localization.Current == Localization.Language.Chinese ? "工作流缓存已清除" : "Workflow cache cleared",
                        "OK");
                }
            }
            GUI.backgroundColor = originalBg;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8);

            _historyScrollPosition = EditorGUILayout.BeginScrollView(_historyScrollPosition);

            // Active tasks section
            if (history.tasks.Count > 0)
            {
                DrawColoredLabel(Localization.Current == Localization.Language.Chinese ? "活动任务" : "Active Tasks", SuccessColor, true);
                EditorGUILayout.Space(4);

                for (int i = history.tasks.Count - 1; i >= 0; i--)
                {
                    var task = history.tasks[i];
                    DrawColoredBox(CardBgColor, () =>
                    {
                        EditorGUILayout.BeginHorizontal();
                        DrawColoredLabel($"[{task.GetFormattedTime()}]", MutedColor, false);
                        GUILayout.Space(8);
                        EditorGUILayout.LabelField(task.tag, EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        DrawColoredLabel($"{task.snapshots.Count} changes", AccentColor, false);
                        EditorGUILayout.EndHorizontal();

                        if (!string.IsNullOrEmpty(task.description))
                        {
                            DrawColoredLabel(task.description, MutedColor, false);
                        }

                        EditorGUILayout.Space(4);
                        EditorGUILayout.BeginHorizontal();

                        var bg = GUI.backgroundColor;
                        GUI.backgroundColor = WarningColor;
                        if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "撤销" : "Undo", GUILayout.Height(22)))
                        {
                            if (EditorUtility.DisplayDialog("Confirm", $"Undo '{task.tag}'?", "Undo", "Cancel"))
                            {
                                bool success = WorkflowManager.UndoTask(task.id);
                                if (success) EditorUtility.DisplayDialog("Success", "Undo completed!", "OK");
                                else EditorUtility.DisplayDialog("Error", "Undo failed.", "OK");
                            }
                        }

                        GUI.backgroundColor = ErrorColor;
                        if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "删除" : "Delete", GUILayout.Width(60), GUILayout.Height(22)))
                        {
                            WorkflowManager.DeleteTask(task.id);
                        }
                        GUI.backgroundColor = bg;

                        EditorGUILayout.EndHorizontal();
                    });
                    EditorGUILayout.Space(4);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(Localization.Current == Localization.Language.Chinese ? "暂无活动任务" : "No active tasks.", MessageType.Info);
            }

            // Undone tasks section (for redo)
            if (history.undoneStack != null && history.undoneStack.Count > 0)
            {
                EditorGUILayout.Space(12);
                DrawColoredLabel(Localization.Current == Localization.Language.Chinese ? "已撤销任务 (可恢复)" : "Undone Tasks (Can Redo)", MutedColor, true);
                EditorGUILayout.Space(4);

                for (int i = history.undoneStack.Count - 1; i >= 0; i--)
                {
                    var task = history.undoneStack[i];
                    DrawColoredBox(new Color(0.2f, 0.2f, 0.2f), () =>
                    {
                        EditorGUILayout.BeginHorizontal();
                        DrawColoredLabel($"[{task.GetFormattedTime()}]", new Color(0.45f, 0.45f, 0.45f), false);
                        GUILayout.Space(8);
                        DrawColoredLabel(task.tag, MutedColor, true);
                        GUILayout.FlexibleSpace();
                        DrawColoredLabel($"{task.snapshots.Count} changes", new Color(0.45f, 0.45f, 0.45f), false);
                        EditorGUILayout.EndHorizontal();

                        if (!string.IsNullOrEmpty(task.description))
                        {
                            DrawColoredLabel(task.description, new Color(0.45f, 0.45f, 0.45f), false);
                        }

                        EditorGUILayout.Space(4);
                        var bg = GUI.backgroundColor;
                        GUI.backgroundColor = SuccessColor;
                        if (GUILayout.Button(Localization.Current == Localization.Language.Chinese ? "恢复" : "Redo", GUILayout.Height(22)))
                        {
                            if (EditorUtility.DisplayDialog("Confirm", $"Redo '{task.tag}'?", "Redo", "Cancel"))
                            {
                                bool success = WorkflowManager.RedoTask(task.id);
                                if (success) EditorUtility.DisplayDialog("Success", "Redo completed!", "OK");
                                else EditorUtility.DisplayDialog("Error", "Redo failed.", "OK");
                            }
                        }
                        GUI.backgroundColor = bg;
                    });
                    EditorGUILayout.Space(4);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private string L(string key) => Localization.Get(key);

        // Helper methods for colored UI elements
        private void DrawColoredLabel(string text, Color color, bool bold)
        {
            var style = bold ? new GUIStyle(EditorStyles.boldLabel) : new GUIStyle(EditorStyles.label);
            style.normal.textColor = color;
            style.wordWrap = true;
            EditorGUILayout.LabelField(text, style);
        }

        private void DrawColoredBox(Color bgColor, System.Action content)
        {
            var originalBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = originalBg;
            content?.Invoke();
            EditorGUILayout.EndVertical();
        }

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
