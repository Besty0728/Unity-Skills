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
            button.AddToClassList("toolbar-glyph-btn");
            if (icon == null)
                return;

            // A fixed 14px icon centered in the button, the size VectorGlyphs paints its toolbar glyphs at.
            button.style.backgroundImage = new StyleBackground(icon);
            button.style.backgroundSize = new StyleBackgroundSize(new BackgroundSize(new Length(14), new Length(14)));
            button.style.backgroundPositionX = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            button.style.backgroundPositionY = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
        }
    }
}

// Producer:Betsy
