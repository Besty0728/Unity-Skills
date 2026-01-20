using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System;

namespace UnitySkills
{
    /// <summary>
    /// UI management skills - create and configure UI elements.
    /// Dynamically uses TextMeshPro if available, falls back to Legacy UI Text.
    /// </summary>
    public static class UISkills
    {
        // Cache TMP types for performance
        private static Type _tmpTextType;
        private static Type _tmpInputFieldType;
        private static bool _tmpChecked = false;
        private static bool _tmpAvailable = false;

        /// <summary>
        /// Check if TextMeshPro is available in the project
        /// </summary>
        private static bool IsTMPAvailable()
        {
            if (!_tmpChecked)
            {
                _tmpChecked = true;
                _tmpTextType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro");
                _tmpInputFieldType = Type.GetType("TMPro.TMP_InputField, Unity.TextMeshPro");
                _tmpAvailable = _tmpTextType != null;
            }
            return _tmpAvailable;
        }

        /// <summary>
        /// Add text component - uses TMP if available, otherwise Legacy Text
        /// </summary>
        private static Component AddTextComponent(GameObject go, string text, int fontSize, Color color, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            if (IsTMPAvailable())
            {
                var tmp = go.AddComponent(_tmpTextType);
                // Set properties via reflection
                _tmpTextType.GetProperty("text")?.SetValue(tmp, text);
                _tmpTextType.GetProperty("fontSize")?.SetValue(tmp, (float)fontSize);
                _tmpTextType.GetProperty("color")?.SetValue(tmp, color);
                
                // Convert TextAnchor to TMP alignment
                var alignmentOptionsType = Type.GetType("TMPro.TextAlignmentOptions, Unity.TextMeshPro");
                if (alignmentOptionsType != null)
                {
                    object tmpAlignment = alignment switch
                    {
                        TextAnchor.UpperLeft => Enum.Parse(alignmentOptionsType, "TopLeft"),
                        TextAnchor.UpperCenter => Enum.Parse(alignmentOptionsType, "Top"),
                        TextAnchor.UpperRight => Enum.Parse(alignmentOptionsType, "TopRight"),
                        TextAnchor.MiddleLeft => Enum.Parse(alignmentOptionsType, "Left"),
                        TextAnchor.MiddleCenter => Enum.Parse(alignmentOptionsType, "Center"),
                        TextAnchor.MiddleRight => Enum.Parse(alignmentOptionsType, "Right"),
                        TextAnchor.LowerLeft => Enum.Parse(alignmentOptionsType, "BottomLeft"),
                        TextAnchor.LowerCenter => Enum.Parse(alignmentOptionsType, "Bottom"),
                        TextAnchor.LowerRight => Enum.Parse(alignmentOptionsType, "BottomRight"),
                        _ => Enum.Parse(alignmentOptionsType, "Center")
                    };
                    _tmpTextType.GetProperty("alignment")?.SetValue(tmp, tmpAlignment);
                }
                return tmp;
            }
            else
            {
                var textComp = go.AddComponent<Text>();
                textComp.text = text;
                textComp.fontSize = fontSize;
                textComp.color = color;
                textComp.alignment = alignment;
                textComp.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (textComp.font == null)
                    textComp.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                return textComp;
            }
        }

        /// <summary>
        /// Set text on a component (TMP or Legacy)
        /// </summary>
        private static bool SetTextOnComponent(Component comp, string text)
        {
            if (comp == null) return false;
            
            var textProp = comp.GetType().GetProperty("text");
            if (textProp != null)
            {
                textProp.SetValue(comp, text);
                return true;
            }
            return false;
        }
        [UnitySkill("ui_create_canvas", "Create a new Canvas")]
        public static object UICreateCanvas(string name = "Canvas", string renderMode = "ScreenSpaceOverlay")
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            switch (renderMode.ToLower())
            {
                case "screenspaceoverlay":
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    break;
                case "screenspacecamera":
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    break;
                case "worldspace":
                    canvas.renderMode = RenderMode.WorldSpace;
                    break;
                default:
                    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                    break;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create Canvas");

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                renderMode = canvas.renderMode.ToString()
            };
        }

        [UnitySkill("ui_create_panel", "Create a Panel UI element")]
        public static object UICreatePanel(string name = "Panel", string parent = null, float r = 1, float g = 1, float b = 1, float a = 0.5f)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.color = new Color(r, g, b, a);

            Undo.RegisterCreatedObjectUndo(go, "Create Panel");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name };
        }

        [UnitySkill("ui_create_button", "Create a Button UI element")]
        public static object UICreateButton(string name = "Button", string parent = null, string text = "Button", float width = 160, float height = 30)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var image = go.AddComponent<Image>();
            image.color = Color.white;

            var button = go.AddComponent<Button>();

            // Add text child
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;

            AddTextComponent(textGo, text, 14, Color.black, TextAnchor.MiddleCenter);

            Undo.RegisterCreatedObjectUndo(go, "Create Button");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, text };
        }

        [UnitySkill("ui_create_text", "Create a Text UI element")]
        public static object UICreateText(string name = "Text", string parent = null, string text = "New Text", int fontSize = 14, float r = 0, float g = 0, float b = 0)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(200, 50);

            AddTextComponent(go, text, fontSize, new Color(r, g, b));

            Undo.RegisterCreatedObjectUndo(go, "Create Text");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, usingTMP = IsTMPAvailable() };
        }

        [UnitySkill("ui_create_image", "Create an Image UI element")]
        public static object UICreateImage(string name = "Image", string parent = null, string spritePath = null, float width = 100, float height = 100)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var image = go.AddComponent<Image>();

            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null)
                    image.sprite = sprite;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create Image");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name };
        }

        [UnitySkill("ui_create_inputfield", "Create an InputField UI element")]
        public static object UICreateInputField(string name = "InputField", string parent = null, string placeholder = "Enter text...", float width = 200, float height = 30)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var image = go.AddComponent<Image>();
            image.color = Color.white;

            if (IsTMPAvailable())
            {
                // Use TMP InputField
                var inputField = go.AddComponent(_tmpInputFieldType);

                // Create text area
                var textAreaGo = new GameObject("Text Area");
                textAreaGo.transform.SetParent(go.transform, false);
                var textAreaRect = textAreaGo.AddComponent<RectTransform>();
                textAreaRect.anchorMin = Vector2.zero;
                textAreaRect.anchorMax = Vector2.one;
                textAreaRect.offsetMin = new Vector2(10, 6);
                textAreaRect.offsetMax = new Vector2(-10, -7);
                textAreaGo.AddComponent<RectMask2D>();

                // Placeholder
                var placeholderGo = new GameObject("Placeholder");
                placeholderGo.transform.SetParent(textAreaGo.transform, false);
                var placeholderRect = placeholderGo.AddComponent<RectTransform>();
                placeholderRect.anchorMin = Vector2.zero;
                placeholderRect.anchorMax = Vector2.one;
                placeholderRect.sizeDelta = Vector2.zero;
                var placeholderComp = AddTextComponent(placeholderGo, placeholder, 14, new Color(0.5f, 0.5f, 0.5f));
                // Set italic style
                var fontStyleType = Type.GetType("TMPro.FontStyles, Unity.TextMeshPro");
                if (fontStyleType != null)
                    _tmpTextType.GetProperty("fontStyle")?.SetValue(placeholderComp, Enum.Parse(fontStyleType, "Italic"));

                // Text
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(textAreaGo.transform, false);
                var textRect = textGo.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.sizeDelta = Vector2.zero;
                var textComp = AddTextComponent(textGo, "", 14, Color.black);

                // Set TMP_InputField properties
                _tmpInputFieldType.GetProperty("textViewport")?.SetValue(inputField, textAreaRect);
                _tmpInputFieldType.GetProperty("textComponent")?.SetValue(inputField, textComp);
                _tmpInputFieldType.GetProperty("placeholder")?.SetValue(inputField, placeholderComp);
            }
            else
            {
                // Use Legacy InputField
                var inputField = go.AddComponent<InputField>();

                // Placeholder
                var placeholderGo = new GameObject("Placeholder");
                placeholderGo.transform.SetParent(go.transform, false);
                var placeholderRect = placeholderGo.AddComponent<RectTransform>();
                placeholderRect.anchorMin = Vector2.zero;
                placeholderRect.anchorMax = Vector2.one;
                placeholderRect.offsetMin = new Vector2(10, 6);
                placeholderRect.offsetMax = new Vector2(-10, -7);
                var placeholderText = placeholderGo.AddComponent<Text>();
                placeholderText.text = placeholder;
                placeholderText.color = new Color(0.5f, 0.5f, 0.5f);
                placeholderText.fontStyle = FontStyle.Italic;
                placeholderText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (placeholderText.font == null)
                    placeholderText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

                // Text
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(go.transform, false);
                var textRect = textGo.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = new Vector2(10, 6);
                textRect.offsetMax = new Vector2(-10, -7);
                var text = textGo.AddComponent<Text>();
                text.color = Color.black;
                text.supportRichText = false;
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (text.font == null)
                    text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

                inputField.textComponent = text;
                inputField.placeholder = placeholderText;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create InputField");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, placeholder, usingTMP = IsTMPAvailable() };
        }

        [UnitySkill("ui_create_slider", "Create a Slider UI element")]
        public static object UICreateSlider(string name = "Slider", string parent = null, float minValue = 0, float maxValue = 1, float value = 0.5f, float width = 160, float height = 20)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var slider = go.AddComponent<Slider>();
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.value = value;

            // Background
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(go.transform, false);
            var bgRect = bgGo.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.8f, 0.8f, 0.8f);

            // Fill Area
            var fillAreaGo = new GameObject("Fill Area");
            fillAreaGo.transform.SetParent(go.transform, false);
            var fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.sizeDelta = new Vector2(-20, 0);

            // Fill
            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            var fillRect = fillGo.AddComponent<RectTransform>();
            fillRect.sizeDelta = new Vector2(10, 0);
            var fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.3f, 0.6f, 1f);

            slider.fillRect = fillRect;

            // Handle
            var handleAreaGo = new GameObject("Handle Slide Area");
            handleAreaGo.transform.SetParent(go.transform, false);
            var handleAreaRect = handleAreaGo.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.sizeDelta = new Vector2(-20, 0);

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            var handleRect = handleGo.AddComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20, 0);
            var handleImage = handleGo.AddComponent<Image>();
            handleImage.color = Color.white;

            slider.handleRect = handleRect;

            Undo.RegisterCreatedObjectUndo(go, "Create Slider");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, minValue, maxValue, value };
        }

        [UnitySkill("ui_create_toggle", "Create a Toggle UI element")]
        public static object UICreateToggle(string name = "Toggle", string parent = null, string label = "Toggle", bool isOn = false)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(160, 20);

            var toggle = go.AddComponent<Toggle>();
            toggle.isOn = isOn;

            // Background
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(go.transform, false);
            var bgRect = bgGo.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 1);
            bgRect.anchorMax = new Vector2(0, 1);
            bgRect.pivot = new Vector2(0, 1);
            bgRect.sizeDelta = new Vector2(20, 20);
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = Color.white;

            // Checkmark
            var checkGo = new GameObject("Checkmark");
            checkGo.transform.SetParent(bgGo.transform, false);
            var checkRect = checkGo.AddComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.sizeDelta = Vector2.zero;
            var checkImage = checkGo.AddComponent<Image>();
            checkImage.color = new Color(0.3f, 0.6f, 1f);

            toggle.targetGraphic = bgImage;
            toggle.graphic = checkImage;

            // Label
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(25, 0);
            labelRect.offsetMax = Vector2.zero;

            AddTextComponent(labelGo, label, 14, Color.black);

            Undo.RegisterCreatedObjectUndo(go, "Create Toggle");

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, label, isOn };
        }

        [UnitySkill("ui_set_text", "Set text content on a UI Text element (supports name/instanceId/path)")]
        public static object UISetText(string name = null, int instanceId = 0, string path = null, string text = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            // Try TMP first if available
            if (IsTMPAvailable())
            {
                var tmpComp = go.GetComponent(_tmpTextType);
                if (tmpComp != null)
                {
                    Undo.RecordObject(tmpComp, "Set Text");
                    SetTextOnComponent(tmpComp, text);
                    return new { success = true, name = go.name, text, usingTMP = true };
                }
            }

            // Fallback to Legacy Text
            var textComp = go.GetComponent<Text>();
            if (textComp != null)
            {
                Undo.RecordObject(textComp, "Set Text");
                textComp.text = text;
                return new { success = true, name = go.name, text, usingTMP = false };
            }

            return new { error = "No Text component found (checked both TMP and Legacy UI)" };
        }

        [UnitySkill("ui_find_all", "Find all UI elements in the scene")]
        public static object UIFindAll(string uiType = null, int limit = 50)
        {
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            var results = new System.Collections.Generic.List<object>();

            foreach (var canvas in canvases)
            {
                var elements = canvas.GetComponentsInChildren<RectTransform>(true);
                foreach (var element in elements)
                {
                    if (results.Count >= limit) break;

                    var type = GetUIType(element.gameObject);
                    if (!string.IsNullOrEmpty(uiType) && type.ToLower() != uiType.ToLower())
                        continue;

                    results.Add(new
                    {
                        name = element.name,
                        instanceId = element.gameObject.GetInstanceID(),
                        path = GameObjectFinder.GetPath(element.gameObject),
                        uiType = type,
                        active = element.gameObject.activeInHierarchy
                    });
                }
            }

            return new { count = results.Count, elements = results };
        }

        private static GameObject FindOrCreateCanvas(string parentName)
        {
            if (!string.IsNullOrEmpty(parentName))
            {
                var parent = GameObject.Find(parentName);
                if (parent != null) return parent;
            }

            // Find existing canvas
            var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
            if (canvas != null) return canvas.gameObject;

            // Create new canvas
            var go = new GameObject("Canvas");
            var canvasComp = go.AddComponent<Canvas>();
            canvasComp.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            Undo.RegisterCreatedObjectUndo(go, "Create Canvas");

            return go;
        }

        private static string GetUIType(GameObject go)
        {
            if (go.GetComponent<Canvas>()) return "Canvas";
            if (go.GetComponent<Button>()) return "Button";
            if (go.GetComponent<Slider>()) return "Slider";
            if (go.GetComponent<Toggle>()) return "Toggle";
            
            // Check TMP types first if available
            if (IsTMPAvailable())
            {
                if (_tmpInputFieldType != null && go.GetComponent(_tmpInputFieldType) != null) return "InputField";
                if (_tmpTextType != null && go.GetComponent(_tmpTextType) != null) return "Text";
            }
            
            if (go.GetComponent<InputField>()) return "InputField";
            if (go.GetComponent<Text>()) return "Text";
            if (go.GetComponent<Image>()) return "Image";
            if (go.GetComponent<RawImage>()) return "RawImage";
            if (go.GetComponent<RectTransform>()) return "RectTransform";
            return "Unknown";
        }
    }
}
