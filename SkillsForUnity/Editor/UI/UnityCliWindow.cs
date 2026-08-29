using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    /// <summary>
    /// Standalone host for the reusable Unity CLI setup tab.
    /// The setup UI and behavior live in UnityCliTabController so the main window can mount the same subtree.
    /// </summary>
    public sealed class UnityCliWindow : EditorWindow
    {
        private const string ThemeUssPath = "Packages/com.besty.unity-skills/Editor/UI/UnitySkillsWindow.uss";

        private UnityCliTabController _controller;

        public static void ShowWindow()
        {
            var window = GetWindow<UnityCliWindow>(SkillsLocalization.Get("cli_window_title"));
            window.minSize = new Vector2(460, 420);
            window.Focus();
        }

        private void OnEnable()
        {
            SkillsLocalization.LanguageChanged += RebuildForLanguage;
        }

        private void OnDisable()
        {
            SkillsLocalization.LanguageChanged -= RebuildForLanguage;
            _controller?.Dispose();
            _controller = null;
        }

        private void RebuildForLanguage()
        {
            titleContent = new GUIContent(SkillsLocalization.Get("cli_window_title"));
            rootVisualElement.Clear();
            rootVisualElement.styleSheets.Clear();
            CreateGUI();
        }

        private void CreateGUI()
        {
            titleContent = new GUIContent(SkillsLocalization.Get("cli_window_title"));
            var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>(ThemeUssPath);
            if (theme != null) rootVisualElement.styleSheets.Add(theme);
            else SkillsLogger.LogWarning($"Failed to load theme USS: {ThemeUssPath}");

            UISkillsFont.Apply(rootVisualElement);
            _controller?.Dispose();
            _controller = new UnityCliTabController(rootVisualElement, this);
        }
    }
}

// Producer:Betsy
