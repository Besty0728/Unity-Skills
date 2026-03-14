using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using System.Linq;
using System.Reflection;
using System;
using System.Collections.Generic;

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
        private static Type _tmpDropdownType;
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
                _tmpDropdownType = Type.GetType("TMPro.TMP_Dropdown, Unity.TextMeshPro");
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
        [UnitySkill("ui_create_canvas", "Create a new Canvas", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_panel", "Create a Panel UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_button", "Create a Button UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_text", "Create a Text UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_image", "Create an Image UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_batch", "Create multiple UI elements (Efficient). items: JSON array of {type, name, parent, text, width, height, ...}", TracksWorkflow = true)]
        public static object UICreateBatch(string items)
        {
            return BatchExecutor.Execute<BatchUIItem>(items, item =>
            {
                object result;
                switch ((item.type ?? "").ToLower())
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
                    case "dropdown":
                        result = UICreateDropdown(item.name, item.parent, item.options, item.width, item.height);
                        break;
                    case "scrollview":
                        result = UICreateScrollview(item.name, item.parent, item.width, item.height);
                        break;
                    case "rawimage":
                        result = UICreateRawImage(item.name, item.parent, item.texturePath, item.width, item.height);
                        break;
                    case "scrollbar":
                        result = UICreateScrollbar(item.name, item.parent, item.direction ?? "BottomToTop", item.value, item.size, (int)item.numberOfSteps);
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
            public string options { get; set; }
            public string texturePath { get; set; }
            public string direction { get; set; }
            public float size { get; set; } = 0.2f;
            public float numberOfSteps { get; set; } = 0;
        }

        [UnitySkill("ui_create_inputfield", "Create an InputField UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_slider", "Create a Slider UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_create_toggle", "Create a Toggle UI element", TracksWorkflow = true)]
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

        [UnitySkill("ui_set_text", "Set text content on a UI Text element (supports name/instanceId/path)", TracksWorkflow = true)]
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
            var canvases = FindHelper.FindAll<Canvas>();
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

        [UnitySkill("ui_set_anchor", "Set anchor preset for a UI element (TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight, StretchHorizontal, StretchVertical, StretchAll)", TracksWorkflow = true)]
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

        [UnitySkill("ui_set_rect", "Set RectTransform size, position, and padding (offsets)", TracksWorkflow = true)]
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
            string name = null, int instanceId = 0, string path = null,
            string layoutType = "Vertical",  // Vertical, Horizontal, Grid
            float spacing = 10f,
            float paddingLeft = 0, float paddingRight = 0, float paddingTop = 0, float paddingBottom = 0,
            int gridColumns = 3,
            bool childForceExpandWidth = false, bool childForceExpandHeight = false)
        {
            var (parentGo, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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

        // ==================================================================================
        // New UI Element Creation Skills
        // ==================================================================================

        [UnitySkill("ui_create_dropdown", "Create a Dropdown UI element with options", TracksWorkflow = true)]
        public static object UICreateDropdown(string name = "Dropdown", string parent = null, string options = null, float width = 160, float height = 30)
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

            Component dropdownComp;
            bool usingTmpDropdown = IsTMPAvailable() && _tmpDropdownType != null;

            if (usingTmpDropdown)
                dropdownComp = go.AddComponent(_tmpDropdownType);
            else
                dropdownComp = go.AddComponent<Dropdown>();

            // Caption label
            var captionGo = new GameObject("Label");
            captionGo.transform.SetParent(go.transform, false);
            var captionRect = captionGo.AddComponent<RectTransform>();
            captionRect.anchorMin = Vector2.zero;
            captionRect.anchorMax = Vector2.one;
            captionRect.offsetMin = new Vector2(10, 0);
            captionRect.offsetMax = new Vector2(-25, 0);
            var captionText = AddTextComponent(captionGo, "", 14, Color.black);

            // Arrow
            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(go.transform, false);
            var arrowRect = arrowGo.AddComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0);
            arrowRect.anchorMax = new Vector2(1, 1);
            arrowRect.pivot = new Vector2(1, 0.5f);
            arrowRect.sizeDelta = new Vector2(20, 0);
            var arrowImage = arrowGo.AddComponent<Image>();
            arrowImage.color = new Color(0.2f, 0.2f, 0.2f);

            // Template (dropdown list)
            var templateGo = new GameObject("Template");
            templateGo.transform.SetParent(go.transform, false);
            var templateRect = templateGo.AddComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.sizeDelta = new Vector2(0, 150);
            var templateImage = templateGo.AddComponent<Image>();
            templateImage.color = Color.white;
            var scrollRect = templateGo.AddComponent<ScrollRect>();

            // Viewport
            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(templateGo.transform, false);
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportGo.AddComponent<RectMask2D>();
            var viewportImage = viewportGo.AddComponent<Image>();
            viewportImage.color = new Color(1, 1, 1, 0);

            // Content
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 28);

            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            // Item
            var itemGo = new GameObject("Item");
            itemGo.transform.SetParent(contentGo.transform, false);
            var itemRect = itemGo.AddComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0.5f);
            itemRect.anchorMax = new Vector2(1, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 28);
            var itemToggle = itemGo.AddComponent<Toggle>();

            // Item background
            var itemBgGo = new GameObject("Item Background");
            itemBgGo.transform.SetParent(itemGo.transform, false);
            var itemBgRect = itemBgGo.AddComponent<RectTransform>();
            itemBgRect.anchorMin = Vector2.zero;
            itemBgRect.anchorMax = Vector2.one;
            itemBgRect.sizeDelta = Vector2.zero;
            var itemBgImage = itemBgGo.AddComponent<Image>();
            itemBgImage.color = new Color(0.96f, 0.96f, 0.96f);

            // Item checkmark
            var checkGo = new GameObject("Item Checkmark");
            checkGo.transform.SetParent(itemGo.transform, false);
            var checkRect = checkGo.AddComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0, 0.5f);
            checkRect.anchorMax = new Vector2(0, 0.5f);
            checkRect.sizeDelta = new Vector2(20, 20);
            checkRect.anchoredPosition = new Vector2(10, 0);
            var checkImage = checkGo.AddComponent<Image>();
            checkImage.color = new Color(0.3f, 0.6f, 1f);

            itemToggle.targetGraphic = itemBgImage;
            itemToggle.graphic = checkImage;
            itemToggle.isOn = true;

            // Item label
            var itemLabelGo = new GameObject("Item Label");
            itemLabelGo.transform.SetParent(itemGo.transform, false);
            var itemLabelRect = itemLabelGo.AddComponent<RectTransform>();
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(25, 0);
            itemLabelRect.offsetMax = Vector2.zero;
            var itemLabelText = AddTextComponent(itemLabelGo, "Option", 14, Color.black);

            // Set dropdown references via reflection (TMP) or direct (Legacy)
            if (usingTmpDropdown)
            {
                _tmpDropdownType.GetProperty("captionText")?.SetValue(dropdownComp, captionText);
                _tmpDropdownType.GetProperty("itemText")?.SetValue(dropdownComp, itemLabelText);
                _tmpDropdownType.GetProperty("template")?.SetValue(dropdownComp, templateRect);
            }
            else
            {
                var dd = (Dropdown)dropdownComp;
                dd.captionText = captionText as Text;
                dd.itemText = itemLabelText as Text;
                dd.template = templateRect;
            }

            templateGo.SetActive(false);

            // Add options
            var optionList = new List<string>();
            if (!string.IsNullOrEmpty(options))
            {
                foreach (var opt in options.Split(','))
                {
                    var trimmed = opt.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        optionList.Add(trimmed);
                }
            }
            if (optionList.Count == 0)
                optionList.AddRange(new[] { "Option A", "Option B", "Option C" });

            if (usingTmpDropdown)
            {
                var addMethod = _tmpDropdownType.GetMethod("AddOptions", new[] { typeof(List<string>) });
                addMethod?.Invoke(dropdownComp, new object[] { optionList });
            }
            else
            {
                ((Dropdown)dropdownComp).AddOptions(optionList);
            }

            Undo.RegisterCreatedObjectUndo(go, "Create Dropdown");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, optionCount = optionList.Count };
        }

        [UnitySkill("ui_create_scrollview", "Create a ScrollRect (ScrollView) UI element", TracksWorkflow = true)]
        public static object UICreateScrollview(
            string name = "ScrollView", string parent = null,
            float width = 300, float height = 200,
            bool horizontal = false, bool vertical = true,
            string movementType = "Elastic")
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var scrollRect = go.AddComponent<ScrollRect>();
            scrollRect.horizontal = horizontal;
            scrollRect.vertical = vertical;
            if (Enum.TryParse<ScrollRect.MovementType>(movementType, true, out var mt))
                scrollRect.movementType = mt;

            var bgImage = go.AddComponent<Image>();
            bgImage.color = new Color(0.1f, 0.1f, 0.1f, 0.5f);

            // Viewport
            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(go.transform, false);
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportGo.AddComponent<RectMask2D>();
            var viewportImage = viewportGo.AddComponent<Image>();
            viewportImage.color = new Color(1, 1, 1, 0);

            // Content
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = Vector2.one;
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 400);

            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;

            Undo.RegisterCreatedObjectUndo(go, "Create ScrollView");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, horizontal, vertical };
        }

        [UnitySkill("ui_create_rawimage", "Create a RawImage UI element (for Texture2D/RenderTexture)", TracksWorkflow = true)]
        public static object UICreateRawImage(string name = "RawImage", string parent = null, string texturePath = null, float width = 100, float height = 100)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(width, height);

            var rawImage = go.AddComponent<RawImage>();

            if (!string.IsNullOrEmpty(texturePath))
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (texture != null)
                    rawImage.texture = texture;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create RawImage");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, hasTexture = rawImage.texture != null };
        }

        [UnitySkill("ui_create_scrollbar", "Create a standalone Scrollbar UI element", TracksWorkflow = true)]
        public static object UICreateScrollbar(
            string name = "Scrollbar", string parent = null,
            string direction = "BottomToTop", float value = 0, float size = 0.2f, int numberOfSteps = 0)
        {
            var parentGo = FindOrCreateCanvas(parent);
            if (parentGo == null)
                return new { error = "Parent not found and could not create Canvas" };

            var go = new GameObject(name);
            go.transform.SetParent(parentGo.transform, false);

            var rectTransform = go.AddComponent<RectTransform>();
            var isHorizontal = direction.Contains("Left") || direction.Contains("Right");
            rectTransform.sizeDelta = isHorizontal ? new Vector2(160, 20) : new Vector2(20, 160);

            var bgImage = go.AddComponent<Image>();
            bgImage.color = new Color(0.8f, 0.8f, 0.8f);

            // Sliding Area
            var slideAreaGo = new GameObject("Sliding Area");
            slideAreaGo.transform.SetParent(go.transform, false);
            var slideAreaRect = slideAreaGo.AddComponent<RectTransform>();
            slideAreaRect.anchorMin = Vector2.zero;
            slideAreaRect.anchorMax = Vector2.one;
            slideAreaRect.offsetMin = new Vector2(10, 10);
            slideAreaRect.offsetMax = new Vector2(-10, -10);

            // Handle
            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(slideAreaGo.transform, false);
            var handleRect = handleGo.AddComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20, 20);
            var handleImage = handleGo.AddComponent<Image>();
            handleImage.color = Color.white;

            var scrollbar = go.AddComponent<Scrollbar>();
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleImage;
            scrollbar.value = value;
            scrollbar.size = size;
            scrollbar.numberOfSteps = numberOfSteps;

            if (Enum.TryParse<Scrollbar.Direction>(direction, true, out var dir))
                scrollbar.direction = dir;

            Undo.RegisterCreatedObjectUndo(go, "Create Scrollbar");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), parent = parentGo.name, direction };
        }

        // ==================================================================================
        // UI Property Configuration Skills
        // ==================================================================================

        [UnitySkill("ui_set_image", "Set Image properties (type, fillMethod, fillAmount, preserveAspect, sprite)", TracksWorkflow = true)]
        public static object UISetImage(
            string name = null, int instanceId = 0, string path = null,
            string type = null,
            string fillMethod = null, float? fillAmount = null, bool? fillClockwise = null, int? fillOrigin = null,
            bool? preserveAspect = null, string spritePath = null, float? pixelsPerUnitMultiplier = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var image = go.GetComponent<Image>();
            if (image == null) return new { error = "No Image component found" };

            WorkflowManager.SnapshotObject(image);
            Undo.RecordObject(image, "Set Image");

            if (!string.IsNullOrEmpty(type) && Enum.TryParse<Image.Type>(type, true, out var imgType))
                image.type = imgType;
            if (!string.IsNullOrEmpty(fillMethod) && Enum.TryParse<Image.FillMethod>(fillMethod, true, out var fm))
                image.fillMethod = fm;
            if (fillAmount.HasValue)
                image.fillAmount = fillAmount.Value;
            if (fillClockwise.HasValue)
                image.fillClockwise = fillClockwise.Value;
            if (fillOrigin.HasValue)
                image.fillOrigin = fillOrigin.Value;
            if (preserveAspect.HasValue)
                image.preserveAspect = preserveAspect.Value;
            if (pixelsPerUnitMultiplier.HasValue)
                image.pixelsPerUnitMultiplier = pixelsPerUnitMultiplier.Value;

            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null)
                    image.sprite = sprite;
                else
                    return new { error = $"Sprite not found: {spritePath}" };
            }

            return new
            {
                success = true, name = go.name,
                type = image.type.ToString(),
                fillMethod = image.fillMethod.ToString(),
                fillAmount = image.fillAmount,
                preserveAspect = image.preserveAspect
            };
        }

        [UnitySkill("ui_add_layout_element", "Add or configure LayoutElement on a UI element", TracksWorkflow = true)]
        public static object UIAddLayoutElement(
            string name = null, int instanceId = 0, string path = null,
            float? minWidth = null, float? minHeight = null,
            float? preferredWidth = null, float? preferredHeight = null,
            float? flexibleWidth = null, float? flexibleHeight = null,
            bool? ignoreLayout = null, int? layoutPriority = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var layout = go.GetComponent<LayoutElement>() ?? Undo.AddComponent<LayoutElement>(go);
            WorkflowManager.SnapshotObject(layout);
            Undo.RecordObject(layout, "Set LayoutElement");

            if (minWidth.HasValue) layout.minWidth = minWidth.Value;
            if (minHeight.HasValue) layout.minHeight = minHeight.Value;
            if (preferredWidth.HasValue) layout.preferredWidth = preferredWidth.Value;
            if (preferredHeight.HasValue) layout.preferredHeight = preferredHeight.Value;
            if (flexibleWidth.HasValue) layout.flexibleWidth = flexibleWidth.Value;
            if (flexibleHeight.HasValue) layout.flexibleHeight = flexibleHeight.Value;
            if (ignoreLayout.HasValue) layout.ignoreLayout = ignoreLayout.Value;
            if (layoutPriority.HasValue) layout.layoutPriority = layoutPriority.Value;

            return new
            {
                success = true, name = go.name,
                minWidth = layout.minWidth, minHeight = layout.minHeight,
                preferredWidth = layout.preferredWidth, preferredHeight = layout.preferredHeight,
                flexibleWidth = layout.flexibleWidth, flexibleHeight = layout.flexibleHeight,
                ignoreLayout = layout.ignoreLayout
            };
        }

        [UnitySkill("ui_add_canvas_group", "Add or configure CanvasGroup on a UI element (alpha, interactable, blocksRaycasts)", TracksWorkflow = true)]
        public static object UIAddCanvasGroup(
            string name = null, int instanceId = 0, string path = null,
            float? alpha = null, bool? interactable = null,
            bool? blocksRaycasts = null, bool? ignoreParentGroups = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var group = go.GetComponent<CanvasGroup>() ?? Undo.AddComponent<CanvasGroup>(go);
            WorkflowManager.SnapshotObject(group);
            Undo.RecordObject(group, "Set CanvasGroup");

            if (alpha.HasValue) group.alpha = alpha.Value;
            if (interactable.HasValue) group.interactable = interactable.Value;
            if (blocksRaycasts.HasValue) group.blocksRaycasts = blocksRaycasts.Value;
            if (ignoreParentGroups.HasValue) group.ignoreParentGroups = ignoreParentGroups.Value;

            return new
            {
                success = true, name = go.name,
                alpha = group.alpha, interactable = group.interactable,
                blocksRaycasts = group.blocksRaycasts, ignoreParentGroups = group.ignoreParentGroups
            };
        }

        [UnitySkill("ui_add_mask", "Add Mask or RectMask2D to a UI element (maskType: Mask or RectMask2D)", TracksWorkflow = true)]
        public static object UIAddMask(
            string name = null, int instanceId = 0, string path = null,
            string maskType = "RectMask2D", bool showMaskGraphic = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Add Mask");

            string applied;
            if (maskType.Equals("Mask", StringComparison.OrdinalIgnoreCase))
            {
                // Mask requires an Image component
                if (go.GetComponent<Image>() == null)
                    Undo.AddComponent<Image>(go);
                var mask = go.GetComponent<Mask>() ?? Undo.AddComponent<Mask>(go);
                mask.showMaskGraphic = showMaskGraphic;
                applied = "Mask";
            }
            else
            {
                var rectMask = go.GetComponent<RectMask2D>() ?? Undo.AddComponent<RectMask2D>(go);
                applied = "RectMask2D";
            }

            return new { success = true, name = go.name, maskType = applied, showMaskGraphic };
        }

        [UnitySkill("ui_add_outline", "Add Shadow or Outline effect to a UI element", TracksWorkflow = true)]
        public static object UIAddOutline(
            string name = null, int instanceId = 0, string path = null,
            string effectType = "Outline",
            float r = 0, float g = 0, float b = 0, float a = 0.5f,
            float distanceX = 1, float distanceY = -1,
            bool useGraphicAlpha = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(go, "Add Effect");

            var effectColor = new Color(r, g, b, a);
            var effectDistance = new Vector2(distanceX, distanceY);

            string applied;
            if (effectType.Equals("Shadow", StringComparison.OrdinalIgnoreCase))
            {
                var shadow = Undo.AddComponent<Shadow>(go);
                shadow.effectColor = effectColor;
                shadow.effectDistance = effectDistance;
                shadow.useGraphicAlpha = useGraphicAlpha;
                applied = "Shadow";
            }
            else
            {
                var outline = Undo.AddComponent<Outline>(go);
                outline.effectColor = effectColor;
                outline.effectDistance = effectDistance;
                outline.useGraphicAlpha = useGraphicAlpha;
                applied = "Outline";
            }

            return new { success = true, name = go.name, effectType = applied, effectColor = $"({r},{g},{b},{a})", effectDistance = $"({distanceX},{distanceY})" };
        }

        [UnitySkill("ui_configure_selectable", "Configure Selectable properties (transition, colors, navigation) on a UI element", TracksWorkflow = true)]
        public static object UIConfigureSelectable(
            string name = null, int instanceId = 0, string path = null,
            string transition = null,
            bool? interactable = null,
            string navigationMode = null,
            // ColorBlock properties
            float? normalR = null, float? normalG = null, float? normalB = null,
            float? highlightedR = null, float? highlightedG = null, float? highlightedB = null,
            float? pressedR = null, float? pressedG = null, float? pressedB = null,
            float? disabledR = null, float? disabledG = null, float? disabledB = null,
            float? colorMultiplier = null, float? fadeDuration = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var selectable = go.GetComponent<Selectable>();
            if (selectable == null) return new { error = "No Selectable component found (Button, Toggle, Slider, etc.)" };

            WorkflowManager.SnapshotObject(selectable);
            Undo.RecordObject(selectable, "Configure Selectable");

            if (interactable.HasValue)
                selectable.interactable = interactable.Value;

            if (!string.IsNullOrEmpty(transition) && Enum.TryParse<Selectable.Transition>(transition, true, out var trans))
                selectable.transition = trans;

            if (!string.IsNullOrEmpty(navigationMode))
            {
                if (Enum.TryParse<Navigation.Mode>(navigationMode, true, out var navMode))
                {
                    var nav = selectable.navigation;
                    nav.mode = navMode;
                    selectable.navigation = nav;
                }
            }

            // Update colors if any color param is provided
            if (normalR.HasValue || highlightedR.HasValue || pressedR.HasValue || disabledR.HasValue ||
                colorMultiplier.HasValue || fadeDuration.HasValue)
            {
                var colors = selectable.colors;
                if (normalR.HasValue || normalG.HasValue || normalB.HasValue)
                    colors.normalColor = new Color(normalR ?? colors.normalColor.r, normalG ?? colors.normalColor.g, normalB ?? colors.normalColor.b);
                if (highlightedR.HasValue || highlightedG.HasValue || highlightedB.HasValue)
                    colors.highlightedColor = new Color(highlightedR ?? colors.highlightedColor.r, highlightedG ?? colors.highlightedColor.g, highlightedB ?? colors.highlightedColor.b);
                if (pressedR.HasValue || pressedG.HasValue || pressedB.HasValue)
                    colors.pressedColor = new Color(pressedR ?? colors.pressedColor.r, pressedG ?? colors.pressedColor.g, pressedB ?? colors.pressedColor.b);
                if (disabledR.HasValue || disabledG.HasValue || disabledB.HasValue)
                    colors.disabledColor = new Color(disabledR ?? colors.disabledColor.r, disabledG ?? colors.disabledColor.g, disabledB ?? colors.disabledColor.b);
                if (colorMultiplier.HasValue)
                    colors.colorMultiplier = colorMultiplier.Value;
                if (fadeDuration.HasValue)
                    colors.fadeDuration = fadeDuration.Value;
                selectable.colors = colors;
            }

            return new
            {
                success = true, name = go.name,
                transition = selectable.transition.ToString(),
                interactable = selectable.interactable,
                navigationMode = selectable.navigation.mode.ToString()
            };
        }
    }
}
