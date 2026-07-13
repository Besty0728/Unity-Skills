using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnitySkills
{
    internal static class UISkillsEditorIcons
    {
        public static void Apply(Button button, params string[] iconNames)
        {
            if (button == null)
                return;

            Texture2D icon = null;
            foreach (var iconName in iconNames)
            {
                icon = EditorGUIUtility.IconContent(iconName)?.image as Texture2D;
                if (icon != null)
                    break;
            }

            button.text = string.Empty;
            if (icon == null)
                return;

            button.style.backgroundImage = new StyleBackground(icon);
            button.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
        }
    }
}

// Producer:Betsy
