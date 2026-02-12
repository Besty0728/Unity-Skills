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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name };
        }

        [UnitySkill("ui_create_batch", "Create multiple UI elements (Efficient). items: JSON array of {type, name, parent, text, width, height, ...}")]
        public static object UICreateBatch(string items)
        {
            return BatchExecutor.Execute<BatchUIItem>(items, item =>
            {
                object result;
                switch (item.type.ToLower())
                {
                    case "canvas":
                        result = UICreateCanvas(item.name, item.renderMode ?? "ScreenSpaceOverlay");
                        break;
                    case "panel":
                        result = UICreatePanel(item.name, item.parent, item.r, item.g, item.b, item.a);
                        break;
                    case "button":
                        result = UICreateButton(item.name, item.parent, item.text ?? "Button", item.width, item.height);
                        break;
                    case "text":
                        result = UICreateText(item.name, item.parent, item.text ?? "Text", (int)item.fontSize, item.r, item.g, item.b);
                        break;
                    case "image":
                        result = UICreateImage(item.name, item.parent, item.spritePath, item.width, item.height);
                        break;
                    case "inputfield":
                        result = UICreateInputField(item.name, item.parent, item.placeholder ?? "Enter text...", item.width, item.height);
                        break;
                    case "slider":
                        result = UICreateSlider(item.name, item.parent, item.minValue, item.maxValue, item.value, item.width, item.height);
                        break;
                    case "toggle":
                        result = UICreateToggle(item.name, item.parent, item.label ?? "Toggle", item.isOn);
                        break;
                    default:
                        throw new System.Exception($"Unknown UI type: {item.type}");
                }
                return result;
            }, item => item.type);
        }

        private class BatchUIItem
        {
            public string type { get; set; } // Button, Text, Image, etc.
            public string name { get; set; } = "UI Element";
            public string parent { get; set; }
            public string text { get; set; }
            public float width { get; set; } = 100;
            public float height { get; set; } = 30;
            public float fontSize { get; set; } = 14;
            public float r { get; set; } = 1; // Default white/visible
            public float g { get; set; } = 1;
            public float b { get; set; } = 1;
            public float a { get; set; } = 1;
            public string spritePath { get; set; }
            public string placeholder { get; set; }
            public string label { get; set; }
            public bool isOn { get; set; }
            public float minValue { get; set; } = 0;
            public float maxValue { get; set; } = 1;
            public float value { get; set; } = 0.5f;
            public string renderMode { get; set; }
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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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
                    WorkflowManager.SnapshotObject(tmpComp);
                    Undo.RecordObject(tmpComp, "Set Text");
                    SetTextOnComponent(tmpComp, text);
                    return new { success = true, name = go.name, text, usingTMP = true };
                }
            }

            // Fallback to Legacy Text
            var textComp = go.GetComponent<Text>();
            if (textComp != null)
            {
                WorkflowManager.SnapshotObject(textComp);
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
                var parent = GameObjectFinder.Find(name: parentName);
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
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

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

        // ==================================================================================
        // Advanced UI Layout Skills
        // ==================================================================================

        [UnitySkill("ui_set_anchor", "Set anchor preset for a UI element (TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight, StretchHorizontal, StretchVertical, StretchAll)")]
        public static object UISetAnchor(string name = null, int instanceId = 0, string path = null, string preset = "MiddleCenter", bool setPivot = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var rect = go.GetComponent<RectTransform>();
            if (rect == null) return new { error = "GameObject has no RectTransform" };

            WorkflowManager.SnapshotObject(rect);
            Undo.RecordObject(rect, "Set Anchor");

            Vector2 anchorMin, anchorMax, pivot;
            switch (preset.ToLower().Replace(" ", ""))
            {
                case "topleft":
                    anchorMin = anchorMax = new Vector2(0, 1); pivot = new Vector2(0, 1); break;
                case "topcenter":
                    anchorMin = anchorMax = new Vector2(0.5f, 1); pivot = new Vector2(0.5f, 1); break;
                case "topright":
                    anchorMin = anchorMax = new Vector2(1, 1); pivot = new Vector2(1, 1); break;
                case "middleleft":
                    anchorMin = anchorMax = new Vector2(0, 0.5f); pivot = new Vector2(0, 0.5f); break;
                case "middlecenter":
                    anchorMin = anchorMax = new Vector2(0.5f, 0.5f); pivot = new Vector2(0.5f, 0.5f); break;
                case "middleright":
                    anchorMin = anchorMax = new Vector2(1, 0.5f); pivot = new Vector2(1, 0.5f); break;
                case "bottomleft":
                    anchorMin = anchorMax = new Vector2(0, 0); pivot = new Vector2(0, 0); break;
                case "bottomcenter":
                    anchorMin = anchorMax = new Vector2(0.5f, 0); pivot = new Vector2(0.5f, 0); break;
                case "bottomright":
                    anchorMin = anchorMax = new Vector2(1, 0); pivot = new Vector2(1, 0); break;
                case "stretchhorizontal":
                    anchorMin = new Vector2(0, 0.5f); anchorMax = new Vector2(1, 0.5f); pivot = new Vector2(0.5f, 0.5f); break;
                case "stretchvertical":
                    anchorMin = new Vector2(0.5f, 0); anchorMax = new Vector2(0.5f, 1); pivot = new Vector2(0.5f, 0.5f); break;
                case "stretchall":
                    anchorMin = Vector2.zero; anchorMax = Vector2.one; pivot = new Vector2(0.5f, 0.5f); break;
                default:
                    return new { error = $"Unknown anchor preset: {preset}" };
            }

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            if (setPivot) rect.pivot = pivot;

            return new { success = true, name = go.name, preset, anchorMin = $"({anchorMin.x}, {anchorMin.y})", anchorMax = $"({anchorMax.x}, {anchorMax.y})" };
        }

        [UnitySkill("ui_set_rect", "Set RectTransform size, position, and padding (offsets)")]
        public static object UISetRect(
            string name = null, int instanceId = 0, string path = null,
            float? width = null, float? height = null,
            float? posX = null, float? posY = null,
            float? left = null, float? right = null, float? top = null, float? bottom = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var rect = go.GetComponent<RectTransform>();
            if (rect == null) return new { error = "GameObject has no RectTransform" };

            WorkflowManager.SnapshotObject(rect);
            Undo.RecordObject(rect, "Set Rect");

            // Size
            if (width.HasValue || height.HasValue)
            {
                var size = rect.sizeDelta;
                if (width.HasValue) size.x = width.Value;
                if (height.HasValue) size.y = height.Value;
                rect.sizeDelta = size;
            }

            // Position
            if (posX.HasValue || posY.HasValue)
            {
                var pos = rect.anchoredPosition;
                if (posX.HasValue) pos.x = posX.Value;
                if (posY.HasValue) pos.y = posY.Value;
                rect.anchoredPosition = pos;
            }

            // Offsets (padding for stretched elements)
            if (left.HasValue || bottom.HasValue)
            {
                var min = rect.offsetMin;
                if (left.HasValue) min.x = left.Value;
                if (bottom.HasValue) min.y = bottom.Value;
                rect.offsetMin = min;
            }
            if (right.HasValue || top.HasValue)
            {
                var max = rect.offsetMax;
                if (right.HasValue) max.x = -right.Value;
                if (top.HasValue) max.y = -top.Value;
                rect.offsetMax = max;
            }

            return new { success = true, name = go.name, sizeDelta = $"({rect.sizeDelta.x}, {rect.sizeDelta.y})", anchoredPosition = $"({rect.anchoredPosition.x}, {rect.anchoredPosition.y})" };
        }

        [UnitySkill("ui_layout_children", "Arrange child UI elements in a layout (Vertical, Horizontal, Grid)")]
        public static object UILayoutChildren(
            string parentName = null, int parentInstanceId = 0,
            string layoutType = "Vertical",  // Vertical, Horizontal, Grid
            float spacing = 10f,
            float paddingLeft = 0, float paddingRight = 0, float paddingTop = 0, float paddingBottom = 0,
            int gridColumns = 3,
            bool childForceExpandWidth = false, bool childForceExpandHeight = false)
        {
            var (parentGo, findErr) = GameObjectFinder.FindOrError(name: parentName, instanceId: parentInstanceId);
            if (findErr != null) return findErr;

            var rect = parentGo.GetComponent<RectTransform>();
            if (rect == null) return new { error = "Parent has no RectTransform" };

            Undo.RecordObject(parentGo, "Add Layout");

            // Remove existing layout groups
            var existingV = parentGo.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            var existingH = parentGo.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            var existingG = parentGo.GetComponent<UnityEngine.UI.GridLayoutGroup>();
            if (existingV) Undo.DestroyObjectImmediate(existingV);
            if (existingH) Undo.DestroyObjectImmediate(existingH);
            if (existingG) Undo.DestroyObjectImmediate(existingG);

            var padding = new RectOffset((int)paddingLeft, (int)paddingRight, (int)paddingTop, (int)paddingBottom);

            switch (layoutType.ToLower())
            {
                case "vertical":
                    var vLayout = Undo.AddComponent<UnityEngine.UI.VerticalLayoutGroup>(parentGo);
                    vLayout.spacing = spacing;
                    vLayout.padding = padding;
                    vLayout.childForceExpandWidth = childForceExpandWidth;
                    vLayout.childForceExpandHeight = childForceExpandHeight;
                    break;
                case "horizontal":
                    var hLayout = Undo.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>(parentGo);
                    hLayout.spacing = spacing;
                    hLayout.padding = padding;
                    hLayout.childForceExpandWidth = childForceExpandWidth;
                    hLayout.childForceExpandHeight = childForceExpandHeight;
                    break;
                case "grid":
                    var gLayout = Undo.AddComponent<UnityEngine.UI.GridLayoutGroup>(parentGo);
                    gLayout.spacing = new Vector2(spacing, spacing);
                    gLayout.padding = padding;
                    gLayout.constraint = UnityEngine.UI.GridLayoutGroup.Constraint.FixedColumnCount;
                    gLayout.constraintCount = gridColumns;
                    // Auto-calculate cell size based on first child
                    if (rect.childCount > 0)
                    {
                        var firstChild = rect.GetChild(0).GetComponent<RectTransform>();
                        if (firstChild != null)
                            gLayout.cellSize = firstChild.sizeDelta;
                    }
                    break;
                default:
                    return new { error = $"Unknown layout type: {layoutType}" };
            }

            // Add ContentSizeFitter if not present
            if (parentGo.GetComponent<UnityEngine.UI.ContentSizeFitter>() == null)
            {
                var fitter = Undo.AddComponent<UnityEngine.UI.ContentSizeFitter>(parentGo);
                fitter.verticalFit = layoutType.ToLower() == "vertical" 
                    ? UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize 
                    : UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
                fitter.horizontalFit = layoutType.ToLower() == "horizontal" 
                    ? UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize 
                    : UnityEngine.UI.ContentSizeFitter.FitMode.Unconstrained;
            }

            return new { success = true, parent = parentGo.name, layoutType, childCount = rect.childCount };
        }

        [UnitySkill("ui_align_selected", "Align selected UI elements (Left, Center, Right, Top, Middle, Bottom)")]
        public static object UIAlignSelected(string alignment = "Center")
        {
            var selected = Selection.gameObjects.Where(g => g.GetComponent<RectTransform>() != null).ToList();
            if (selected.Count < 2) return new { error = "Select at least 2 UI elements" };

            Undo.RecordObjects(selected.Select(g => g.GetComponent<RectTransform>()).Cast<UnityEngine.Object>().ToArray(), "Align UI");

            var rects = selected.Select(g => g.GetComponent<RectTransform>()).ToList();
            
            switch (alignment.ToLower())
            {
                case "left":
                    float minX = rects.Min(r => r.anchoredPosition.x - r.rect.width * r.pivot.x);
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(minX + r.rect.width * r.pivot.x, r.anchoredPosition.y);
                    break;
                case "right":
                    float maxX = rects.Max(r => r.anchoredPosition.x + r.rect.width * (1 - r.pivot.x));
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(maxX - r.rect.width * (1 - r.pivot.x), r.anchoredPosition.y);
                    break;
                case "center":
                    float avgX = rects.Average(r => r.anchoredPosition.x);
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(avgX, r.anchoredPosition.y);
                    break;
                case "top":
                    float maxY = rects.Max(r => r.anchoredPosition.y + r.rect.height * (1 - r.pivot.y));
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(r.anchoredPosition.x, maxY - r.rect.height * (1 - r.pivot.y));
                    break;
                case "bottom":
                    float minY = rects.Min(r => r.anchoredPosition.y - r.rect.height * r.pivot.y);
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(r.anchoredPosition.x, minY + r.rect.height * r.pivot.y);
                    break;
                case "middle":
                    float avgY = rects.Average(r => r.anchoredPosition.y);
                    foreach (var r in rects)
                        r.anchoredPosition = new Vector2(r.anchoredPosition.x, avgY);
                    break;
                default:
                    return new { error = $"Unknown alignment: {alignment}" };
            }

            return new { success = true, alignment, count = selected.Count };
        }

        [UnitySkill("ui_distribute_selected", "Distribute selected UI elements evenly (Horizontal, Vertical)")]
        public static object UIDistributeSelected(string direction = "Horizontal")
        {
            var selected = Selection.gameObjects
                .Where(g => g.GetComponent<RectTransform>() != null)
                .OrderBy(g => direction.ToLower() == "horizontal" 
                    ? g.GetComponent<RectTransform>().anchoredPosition.x 
                    : g.GetComponent<RectTransform>().anchoredPosition.y)
                .ToList();

            if (selected.Count < 3) return new { error = "Select at least 3 UI elements to distribute" };

            Undo.RecordObjects(selected.Select(g => g.GetComponent<RectTransform>()).Cast<UnityEngine.Object>().ToArray(), "Distribute UI");

            var rects = selected.Select(g => g.GetComponent<RectTransform>()).ToList();

            if (direction.ToLower() == "horizontal")
            {
                float minX = rects.First().anchoredPosition.x;
                float maxX = rects.Last().anchoredPosition.x;
                float step = (maxX - minX) / (rects.Count - 1);
                
                for (int i = 0; i < rects.Count; i++)
                    rects[i].anchoredPosition = new Vector2(minX + step * i, rects[i].anchoredPosition.y);
            }
            else
            {
                float minY = rects.First().anchoredPosition.y;
                float maxY = rects.Last().anchoredPosition.y;
                float step = (maxY - minY) / (rects.Count - 1);
                
                for (int i = 0; i < rects.Count; i++)
                    rects[i].anchoredPosition = new Vector2(rects[i].anchoredPosition.x, minY + step * i);
            }

            return new { success = true, direction, count = selected.Count };
        }
    }
}
