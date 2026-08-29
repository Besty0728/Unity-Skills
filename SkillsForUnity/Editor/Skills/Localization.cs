using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// SkillsLocalization for UnitySkills.
    /// Loads language strings from decoupled JSON locale files (en.json, zh-CN.json, ru.json)
    /// and persists language preferences across Domain Reloads.
    /// </summary>
    [InitializeOnLoad]
    public static class SkillsLocalization
    {
        public enum Language { English, Chinese, Russian }

        private const string PREF_LANGUAGE = "UnitySkills_Language";
        private const string PREF_PINNED_PRIMARY = "UnitySkills_PinnedLanguagePrimary";
        private const string PREF_PINNED_SECONDARY = "UnitySkills_PinnedLanguageSecondary";

        private static bool _initialized = false;
        private static Language _current = Language.English;

        private static readonly Dictionary<string, string> _english = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _chinese = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _russian = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static SkillsLocalization()
        {
            EnsureLoaded();
        }

        /// <summary>
        /// Language-change notification. Standalone sub-windows (audit / Unity CLI) subscribe to this
        /// and rebuild their whole tree to follow the main panel; the main window itself still calls RefreshLocalization directly.
        /// </summary>
        public static event Action LanguageChanged;

        public static Language Current
        {
            get
            {
                if (!_initialized)
                {
                    // Restore from EditorPrefs on first access
                    int saved = EditorPrefs.GetInt(PREF_LANGUAGE, (int)Language.English);
                    _current = Enum.IsDefined(typeof(Language), saved) ? (Language)saved : Language.English;
                    _initialized = true;
                    EnsureLoaded();
                }
                return _current;
            }
            set
            {
                bool changed = _current != value;
                _current = value;
                _initialized = true;
                // Persist to EditorPrefs
                EditorPrefs.SetInt(PREF_LANGUAGE, (int)value);
                if (changed)
                {
                    try { LanguageChanged?.Invoke(); }
                    catch (Exception ex) { SkillsLogger.LogWarning($"LanguageChanged handler failed: {ex.Message}"); }
                }
            }
        }

        public static Language PinnedPrimary
        {
            get => ReadPinned(PREF_PINNED_PRIMARY, Language.Chinese);
            set => SetPinned(PREF_PINNED_PRIMARY, PREF_PINNED_SECONDARY, value);
        }

        public static Language PinnedSecondary
        {
            get => ReadPinned(PREF_PINNED_SECONDARY, Language.English);
            set => SetPinned(PREF_PINNED_SECONDARY, PREF_PINNED_PRIMARY, value);
        }

        private static Language ReadPinned(string key, Language fallback)
        {
            int saved = EditorPrefs.GetInt(key, (int)fallback);
            return Enum.IsDefined(typeof(Language), saved) ? (Language)saved : fallback;
        }

        private static void SetPinned(string key, string otherKey, Language value)
        {
            var previous = ReadPinned(key, key == PREF_PINNED_PRIMARY ? Language.Chinese : Language.English);
            if (ReadPinned(otherKey, otherKey == PREF_PINNED_PRIMARY ? Language.Chinese : Language.English) == value)
                EditorPrefs.SetInt(otherKey, (int)previous);
            EditorPrefs.SetInt(key, (int)value);
            LanguageChanged?.Invoke();
        }

        public static void Reload()
        {
            LoadLocale("en.json", _english);
            LoadLocale("zh-CN.json", _chinese);
            LoadLocale("ru.json", _russian);
        }

        private static void EnsureLoaded()
        {
            if (_english.Count == 0) LoadLocale("en.json", _english);
            if (_chinese.Count == 0) LoadLocale("zh-CN.json", _chinese);
            if (_russian.Count == 0) LoadLocale("ru.json", _russian);
        }

        private static void LoadLocale(string fileName, Dictionary<string, string> targetDict)
        {
            targetDict.Clear();
            string json = LoadLocaleJson(fileName);
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    foreach (var kv in dict)
                    {
                        targetDict[kv.Key] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnitySkills] Failed to parse locale file {fileName}: {ex.Message}");
            }
        }

        private static string LoadLocaleJson(string fileName)
        {
            // 1. Direct package / asset paths via AssetDatabase
            string[] searchPaths = new[]
            {
                $"Packages/com.besty.unity-skills/Editor/Locales/{fileName}",
                $"Packages/com.betsy.unityskills/Editor/Locales/{fileName}",
                $"Assets/SkillsForUnity/Editor/Locales/{fileName}",
                $"Assets/Plugins/SkillsForUnity/Editor/Locales/{fileName}"
            };

            foreach (var sp in searchPaths)
            {
                var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(sp);
                if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
                    return textAsset.text;
            }

            // 2. Direct file system search in project and package locations
            string[] fileCandidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "SkillsForUnity", "Editor", "Locales", fileName),
                Path.Combine(Application.dataPath, "..", "SkillsForUnity", "Editor", "Locales", fileName),
                Path.Combine(Application.dataPath, "SkillsForUnity", "Editor", "Locales", fileName),
                Path.Combine(Application.dataPath, "..", "Packages", "com.besty.unity-skills", "Editor", "Locales", fileName),
                Path.Combine(Application.dataPath, "..", "Packages", "com.betsy.unityskills", "Editor", "Locales", fileName)
            };

            foreach (var candidate in fileCandidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        string content = File.ReadAllText(candidate);
                        if (!string.IsNullOrEmpty(content)) return content;
                    }
                }
                catch { }
            }

            // 3. Fallback: Search all TextAssets matching Locales path
            string[] guids = AssetDatabase.FindAssets("t:TextAsset");
            foreach (var guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (assetPath.EndsWith("/Locales/" + fileName, StringComparison.OrdinalIgnoreCase) ||
                    assetPath.EndsWith("\\Locales\\" + fileName, StringComparison.OrdinalIgnoreCase))
                {
                    var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
                    if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
                        return textAsset.text;
                }
            }

            return string.Empty;
        }

        public static bool TryGet(string key, out string value)
        {
            EnsureLoaded();
            if (Current == Language.Russian && _russian.TryGetValue(key, out value))
                return true;
            if (Current == Language.Chinese && _chinese.TryGetValue(key, out value))
                return true;
            if (_english.TryGetValue(key, out value))
                return true;

            value = key;
            return false;
        }

        public static bool Has(string key)
        {
            EnsureLoaded();
            return _english.ContainsKey(key) || _chinese.ContainsKey(key) || _russian.ContainsKey(key);
        }

        public static string Get(string key)
        {
            EnsureLoaded();
            if (Current == Language.Russian && _russian.TryGetValue(key, out var ru))
                return ru;
            if (Current == Language.Chinese && _chinese.TryGetValue(key, out var cn))
                return cn;
            if (_english.TryGetValue(key, out var en))
                return en;
            return key;
        }

        public static string Get(string key, params object[] args)
        {
            string fmt = Get(key);
            if (args == null || args.Length == 0) return fmt;
            try
            {
                return string.Format(fmt, args);
            }
            catch (FormatException)
            {
                return fmt;
            }
        }
    }
}

// Producer:Betsy
