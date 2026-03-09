using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Globalization;

namespace UnitySkills
{
    /// <summary>
    /// UI Toolkit skills - create/edit USS/UXML files and configure UIDocument in scenes.
    /// Requires Unity 2021.3+ (UI Toolkit runtime support).
    /// </summary>
    public static class UIToolkitSkills
    {
        // ============================ FILE OPERATIONS ============================

        [UnitySkill("uitk_create_uss", "Create a USS stylesheet file for UI Toolkit")]
        public static object UitkCreateUss(string savePath, string content = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var fileContent = content ?? DefaultUss(Path.GetFileNameWithoutExtension(savePath));
            File.WriteAllText(savePath, fileContent, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, lines = fileContent.Split('\n').Length };
        }

        [UnitySkill("uitk_create_uxml", "Create a UXML layout file for UI Toolkit")]
        public static object UitkCreateUxml(string savePath, string content = null, string ussPath = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string relUss = null;
            if (!string.IsNullOrEmpty(ussPath))
            {
                var uxmlDir = Path.GetDirectoryName(savePath)?.Replace('\\', '/') ?? "";
                var ussDir  = Path.GetDirectoryName(ussPath)?.Replace('\\', '/') ?? "";
                relUss = (uxmlDir == ussDir) ? Path.GetFileName(ussPath) : ussPath;
            }
            string fileContent = content ?? (relUss != null ? DefaultUxml(relUss) : DefaultUxml());
            File.WriteAllText(savePath, fileContent, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, lines = fileContent.Split('\n').Length };
        }

        [UnitySkill("uitk_read_file", "Read USS or UXML file content")]
        public static object UitkReadFile(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var content = File.ReadAllText(filePath);
            return new
            {
                path = filePath,
                type = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                lines = content.Split('\n').Length,
                content
            };
        }

        [UnitySkill("uitk_write_file", "Write or overwrite a USS or UXML file")]
        public static object UitkWriteFile(string filePath, string content)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(content, "content") is object contentErr) return contentErr;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(filePath))
            {
                var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
                if (existing != null) WorkflowManager.SnapshotObject(existing);
            }

            File.WriteAllText(filePath, content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, lines = content.Split('\n').Length };
        }

        [UnitySkill("uitk_delete_file", "Delete a USS or UXML file")]
        public static object UitkDeleteFile(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath", isDelete: true) is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            AssetDatabase.DeleteAsset(filePath);
            return new { success = true, deleted = filePath };
        }

        [UnitySkill("uitk_find_files", "Search for USS and/or UXML files in the project (type: uss/uxml/all)")]
        public static object UitkFindFiles(string type = "all", string folder = null, string filter = null, int limit = 200)
        {
            var searchFolder = string.IsNullOrEmpty(folder) ? "Assets" : folder;
            var typeLower = type.ToLowerInvariant();
            var ussGuids = (typeLower == "uxml") ? new string[0] : AssetDatabase.FindAssets("t:StyleSheet", new[] { searchFolder });
            var uxmlGuids = (typeLower == "uss") ? new string[0] : AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { searchFolder });

            var seen = new System.Collections.Generic.HashSet<string>();
            var filteredPaths = new System.Collections.Generic.List<string>();

            foreach (var g in ussGuids.Concat(uxmlGuids))
            {
                if (filteredPaths.Count >= limit) break;
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (!seen.Add(p)) continue;
                var ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
                if (typeLower == "uss" && ext != "uss") continue;
                if (typeLower == "uxml" && ext != "uxml") continue;
                if (ext != "uss" && ext != "uxml") continue;
                if (!string.IsNullOrEmpty(filter) && !p.Contains(filter)) continue;
                filteredPaths.Add(p);
            }

            filteredPaths.Sort();
            var files = filteredPaths.Select(p => new
            {
                path = p,
                type = Path.GetExtension(p).TrimStart('.').ToLowerInvariant(),
                name = Path.GetFileNameWithoutExtension(p)
            }).ToArray();

            return new { count = files.Length, files };
        }

        // ============================ SCENE OPERATIONS ============================

        [UnitySkill("uitk_create_document", "Create a GameObject with UIDocument component in the scene")]
        public static object UitkCreateDocument(
            string name = "UIDocument",
            string uxmlPath = null,
            string panelSettingsPath = null,
            int sortOrder = 0,
            string parentName = null,
            int parentInstanceId = 0,
            string parentPath = null)
        {
            var go = new GameObject(name);

            if (!string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                var parent = GameObjectFinder.Find(parentName, parentInstanceId, parentPath);
                if (parent == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return new { error = $"Parent not found: {parentName ?? parentPath}" };
                }
                go.transform.SetParent(parent.transform, false);
            }

            var doc = go.AddComponent<UIDocument>();

            if (!string.IsNullOrEmpty(uxmlPath))
            {
                if (Validate.SafePath(uxmlPath, "uxmlPath") is object uxmlErr)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return uxmlErr;
                }
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (vta == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return new { error = $"VisualTreeAsset not found: {uxmlPath}" };
                }
                doc.visualTreeAsset = vta;
            }

            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                if (Validate.SafePath(panelSettingsPath, "panelSettingsPath") is object psErr)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return psErr;
                }
                var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (ps == null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return new { error = $"PanelSettings not found: {panelSettingsPath}" };
                }
                doc.panelSettings = ps;
            }

            doc.sortingOrder = sortOrder;
            Undo.RegisterCreatedObjectUndo(go, "Create UIDocument");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                hasUxml = doc.visualTreeAsset != null,
                hasPanelSettings = doc.panelSettings != null,
                sortOrder
            };
        }

        [UnitySkill("uitk_set_document", "Set UIDocument properties on an existing scene GameObject")]
        public static object UitkSetDocument(
            string name = null,
            int instanceId = 0,
            string path = null,
            string uxmlPath = null,
            string panelSettingsPath = null,
            int? sortOrder = null)
        {
            var go = GameObjectFinder.Find(name, instanceId, path);
            if (go == null)
                return new { error = $"GameObject not found: {name ?? path}" };

            var doc = go.GetComponent<UIDocument>() ?? go.AddComponent<UIDocument>();
            Undo.RecordObject(doc, "Set UIDocument");

            if (!string.IsNullOrEmpty(uxmlPath))
            {
                if (Validate.SafePath(uxmlPath, "uxmlPath") is object uxmlErr) return uxmlErr;
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (vta == null) return new { error = $"VisualTreeAsset not found: {uxmlPath}" };
                doc.visualTreeAsset = vta;
            }

            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                if (Validate.SafePath(panelSettingsPath, "panelSettingsPath") is object psErr) return psErr;
                var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (ps == null) return new { error = $"PanelSettings not found: {panelSettingsPath}" };
                doc.panelSettings = ps;
            }

            if (sortOrder.HasValue)
                doc.sortingOrder = sortOrder.Value;

            WorkflowManager.SnapshotObject(go);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                sortingOrder = doc.sortingOrder
            };
        }

        [UnitySkill("uitk_create_panel_settings", "Create a PanelSettings asset for UI Toolkit")]
        public static object UitkCreatePanelSettings(
            string savePath,
            string scaleMode = "ScaleWithScreenSize",
            int referenceResolutionX = 1920,
            int referenceResolutionY = 1080,
            string screenMatchMode = "MatchWidthOrHeight",
            string themeStyleSheetPath = null,
            // General properties (Unity 2021.3+)
            string textSettingsPath = null,
            string targetTexturePath = null,
            int? targetDisplay = null,
            float? sortOrder = null,
            float? scale = null,
            float? match = null,
            float? referenceDpi = null,
            float? fallbackDpi = null,
            float? referenceSpritePixelsPerUnit = null,
            // Dynamic Atlas
            int? dynamicAtlasMinSize = null,
            int? dynamicAtlasMaxSize = null,
            int? dynamicAtlasMaxSubTextureSize = null,
            string dynamicAtlasFilters = null,
            // Color Clear
            bool? clearColor = null,
            float? colorClearR = null,
            float? colorClearG = null,
            float? colorClearB = null,
            float? colorClearA = null,
            bool? clearDepthStencil = null,
            // Unity 6+ (ignored on older versions)
            string renderMode = null,
            bool? forceGammaRendering = null,
            string bindingLogLevel = null,
            string colliderUpdateMode = null,
            bool? colliderIsTrigger = null,
            int? vertexBudget = null,
            int? textureSlotCount = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = ScriptableObject.CreateInstance<PanelSettings>();

            if (System.Enum.TryParse<PanelScaleMode>(scaleMode, true, out var parsedScale))
                settings.scaleMode = parsedScale;

            settings.referenceResolution = new Vector2Int(referenceResolutionX, referenceResolutionY);

            if (System.Enum.TryParse<PanelScreenMatchMode>(screenMatchMode, true, out var parsedMatch))
                settings.screenMatchMode = parsedMatch;

            if (!string.IsNullOrEmpty(themeStyleSheetPath))
            {
                if (Validate.SafePath(themeStyleSheetPath, "themeStyleSheetPath") is object tssErr) return tssErr;
                var tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themeStyleSheetPath);
                if (tss != null) settings.themeStyleSheet = tss;
            }

            var applyErr = ApplyPanelSettings(settings,
                textSettingsPath, targetTexturePath, targetDisplay,
                sortOrder, scale, match, referenceDpi, fallbackDpi, referenceSpritePixelsPerUnit,
                dynamicAtlasMinSize, dynamicAtlasMaxSize, dynamicAtlasMaxSubTextureSize, dynamicAtlasFilters,
                clearColor, colorClearR, colorClearG, colorClearB, colorClearA, clearDepthStencil,
                renderMode, forceGammaRendering, bindingLogLevel, colliderUpdateMode, colliderIsTrigger,
                vertexBudget, textureSlotCount);
            if (applyErr != null) return applyErr;

            AssetDatabase.CreateAsset(settings, savePath);
            AssetDatabase.SaveAssets();
            WorkflowManager.SnapshotObject(settings, SnapshotType.Created);

            return new
            {
                success = true,
                path = savePath,
                scaleMode = settings.scaleMode.ToString(),
                referenceResolution = $"{referenceResolutionX}x{referenceResolutionY}",
                screenMatchMode = settings.screenMatchMode.ToString()
            };
        }

        [UnitySkill("uitk_get_panel_settings", "Read all properties of a PanelSettings asset")]
        public static object UitkGetPanelSettings(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(assetPath);
            if (settings == null)
                return new { error = $"PanelSettings not found: {assetPath}" };

            var atlas = settings.dynamicAtlasSettings;
            var cc = settings.colorClearValue;

#if UNITY_6000_0_OR_NEWER
            // renderMode, colliderUpdateMode, colliderIsTrigger are internal — read via SerializedObject
            var so = new SerializedObject(settings);
            var rmProp = so.FindProperty("m_RenderMode");
            int rmVal = rmProp != null ? rmProp.intValue : 0;
            string renderModeStr = rmVal == 1 ? "WorldSpace" : "ScreenSpaceOverlay";
            var cuProp = so.FindProperty("m_ColliderUpdateMode");
            int cuVal = cuProp != null ? cuProp.intValue : 0;
            string colliderUpdateStr = cuVal == 2 ? "KeepExistingCollider" : cuVal == 1 ? "Match2DDocumentRect" : "Match3DBoundingBox";
            var ctProp = so.FindProperty("m_ColliderIsTrigger");
            bool colliderIsTriggerVal = ctProp != null ? ctProp.boolValue : true;

            return new
            {
                path = assetPath,
                scaleMode = settings.scaleMode.ToString(),
                referenceResolution = new { x = settings.referenceResolution.x, y = settings.referenceResolution.y },
                screenMatchMode = settings.screenMatchMode.ToString(),
                themeStyleSheet = settings.themeStyleSheet != null ? AssetDatabase.GetAssetPath(settings.themeStyleSheet) : null,
                textSettings = settings.textSettings != null ? AssetDatabase.GetAssetPath(settings.textSettings) : null,
                targetTexture = settings.targetTexture != null ? AssetDatabase.GetAssetPath(settings.targetTexture) : null,
                targetDisplay = settings.targetDisplay,
                sortingOrder = settings.sortingOrder,
                scale = settings.scale,
                match = settings.match,
                referenceDpi = settings.referenceDpi,
                fallbackDpi = settings.fallbackDpi,
                referenceSpritePixelsPerUnit = settings.referenceSpritePixelsPerUnit,
                dynamicAtlasSettings = new
                {
                    minAtlasSize = atlas.minAtlasSize,
                    maxAtlasSize = atlas.maxAtlasSize,
                    maxSubTextureSize = atlas.maxSubTextureSize,
                    activeFilters = atlas.activeFilters.ToString()
                },
                clearColor = settings.clearColor,
                colorClearValue = new { r = cc.r, g = cc.g, b = cc.b, a = cc.a },
                clearDepthStencil = settings.clearDepthStencil,
                // Unity 6+ properties (renderMode/collider* read via SerializedObject)
                renderMode = renderModeStr,
                forceGammaRendering = settings.forceGammaRendering,
                bindingLogLevel = settings.bindingLogLevel.ToString(),
                colliderUpdateMode = colliderUpdateStr,
                colliderIsTrigger = colliderIsTriggerVal,
                vertexBudget = settings.vertexBudget,
                textureSlotCount = (int)settings.textureSlotCount
            };
#else
            return new
            {
                path = assetPath,
                scaleMode = settings.scaleMode.ToString(),
                referenceResolution = new { x = settings.referenceResolution.x, y = settings.referenceResolution.y },
                screenMatchMode = settings.screenMatchMode.ToString(),
                themeStyleSheet = settings.themeStyleSheet != null ? AssetDatabase.GetAssetPath(settings.themeStyleSheet) : null,
                textSettings = settings.textSettings != null ? AssetDatabase.GetAssetPath(settings.textSettings) : null,
                targetTexture = settings.targetTexture != null ? AssetDatabase.GetAssetPath(settings.targetTexture) : null,
                targetDisplay = settings.targetDisplay,
                sortingOrder = settings.sortingOrder,
                scale = settings.scale,
                match = settings.match,
                referenceDpi = settings.referenceDpi,
                fallbackDpi = settings.fallbackDpi,
                referenceSpritePixelsPerUnit = typeof(PanelSettings).GetProperty("referenceSpritePixelsPerUnit")?.GetValue(settings),
                dynamicAtlasSettings = new
                {
                    minAtlasSize = atlas.minAtlasSize,
                    maxAtlasSize = atlas.maxAtlasSize,
                    maxSubTextureSize = atlas.maxSubTextureSize,
                    activeFilters = atlas.activeFilters.ToString()
                },
                clearColor = settings.clearColor,
                colorClearValue = new { r = cc.r, g = cc.g, b = cc.b, a = cc.a },
                clearDepthStencil = settings.clearDepthStencil
            };
#endif
        }

        [UnitySkill("uitk_set_panel_settings", "Modify properties on an existing PanelSettings asset")]
        public static object UitkSetPanelSettings(
            string assetPath,
            string scaleMode = null,
            int? referenceResolutionX = null,
            int? referenceResolutionY = null,
            string screenMatchMode = null,
            string themeStyleSheetPath = null,
            string textSettingsPath = null,
            string targetTexturePath = null,
            int? targetDisplay = null,
            float? sortOrder = null,
            float? scale = null,
            float? match = null,
            float? referenceDpi = null,
            float? fallbackDpi = null,
            float? referenceSpritePixelsPerUnit = null,
            int? dynamicAtlasMinSize = null,
            int? dynamicAtlasMaxSize = null,
            int? dynamicAtlasMaxSubTextureSize = null,
            string dynamicAtlasFilters = null,
            bool? clearColor = null,
            float? colorClearR = null,
            float? colorClearG = null,
            float? colorClearB = null,
            float? colorClearA = null,
            bool? clearDepthStencil = null,
            string renderMode = null,
            bool? forceGammaRendering = null,
            string bindingLogLevel = null,
            string colliderUpdateMode = null,
            bool? colliderIsTrigger = null,
            int? vertexBudget = null,
            int? textureSlotCount = null)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(assetPath);
            if (settings == null)
                return new { error = $"PanelSettings not found: {assetPath}" };

            Undo.RecordObject(settings, "Set PanelSettings");

            if (!string.IsNullOrEmpty(scaleMode) && System.Enum.TryParse<PanelScaleMode>(scaleMode, true, out var parsedScale))
                settings.scaleMode = parsedScale;

            if (referenceResolutionX.HasValue || referenceResolutionY.HasValue)
            {
                var cur = settings.referenceResolution;
                settings.referenceResolution = new Vector2Int(
                    referenceResolutionX ?? cur.x,
                    referenceResolutionY ?? cur.y);
            }

            if (!string.IsNullOrEmpty(screenMatchMode) && System.Enum.TryParse<PanelScreenMatchMode>(screenMatchMode, true, out var parsedMatch))
                settings.screenMatchMode = parsedMatch;

            if (!string.IsNullOrEmpty(themeStyleSheetPath))
            {
                if (Validate.SafePath(themeStyleSheetPath, "themeStyleSheetPath") is object tssErr) return tssErr;
                var tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themeStyleSheetPath);
                if (tss == null) return new { error = $"ThemeStyleSheet not found: {themeStyleSheetPath}" };
                settings.themeStyleSheet = tss;
            }

            var applyErr = ApplyPanelSettings(settings,
                textSettingsPath, targetTexturePath, targetDisplay,
                sortOrder, scale, match, referenceDpi, fallbackDpi, referenceSpritePixelsPerUnit,
                dynamicAtlasMinSize, dynamicAtlasMaxSize, dynamicAtlasMaxSubTextureSize, dynamicAtlasFilters,
                clearColor, colorClearR, colorClearG, colorClearB, colorClearA, clearDepthStencil,
                renderMode, forceGammaRendering, bindingLogLevel, colliderUpdateMode, colliderIsTrigger,
                vertexBudget, textureSlotCount);
            if (applyErr != null) return applyErr;

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            WorkflowManager.SnapshotObject(settings);

            return new
            {
                success = true,
                path = assetPath,
                scaleMode = settings.scaleMode.ToString(),
                referenceResolution = $"{settings.referenceResolution.x}x{settings.referenceResolution.y}",
                screenMatchMode = settings.screenMatchMode.ToString()
            };
        }

        [UnitySkill("uitk_list_documents", "List all UIDocument components in the active scene")]
        public static object UitkListDocuments()
        {
            var docs = UnityEngine.Object.FindObjectsOfType<UIDocument>();
            var result = docs.Select(doc => new
            {
                name = doc.gameObject.name,
                instanceId = doc.gameObject.GetInstanceID(),
                visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                sortingOrder = doc.sortingOrder,
                active = doc.gameObject.activeInHierarchy
            }).ToArray();

            return new { count = result.Length, documents = result };
        }

        // ============================ INSPECTION ============================

        [UnitySkill("uitk_inspect_uxml", "Parse and display UXML element hierarchy (depth controls max traversal depth)")]
        public static object UitkInspectUxml(string filePath, int depth = 5)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            try
            {
                var content = File.ReadAllText(filePath);
                var xdoc = XDocument.Parse(content);
                var hierarchy = ParseXmlNode(xdoc.Root, 0, depth);
                return new { path = filePath, hierarchy };
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse UXML: {ex.Message}" };
            }
        }

        // ============================ TEMPLATES ============================

        [UnitySkill("uitk_create_from_template", "Create a UXML+USS file pair from a template (menu/hud/dialog/settings/inventory/list)")]
        public static object UitkCreateFromTemplate(string template, string savePath, string name = null)
        {
            if (Validate.Required(template, "template") is object tErr) return tErr;
            if (Validate.SafePath(savePath, "savePath") is object pErr) return pErr;

            var dir = savePath.TrimEnd('/', '\\');
            var uiName = !string.IsNullOrEmpty(name)
                ? name
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(template.ToLower());

            var ussFilePath = $"{dir}/{uiName}.uss";
            var uxmlFilePath = $"{dir}/{uiName}.uxml";

            if (File.Exists(ussFilePath) || File.Exists(uxmlFilePath))
                return new { error = $"Files already exist at {dir}/{uiName}.[uss|uxml]" };

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            GetTemplateContent(template.ToLower(), uiName, $"{uiName}.uss", out var ussContent, out var uxmlContent);

            File.WriteAllText(ussFilePath, ussContent, System.Text.Encoding.UTF8);
            File.WriteAllText(uxmlFilePath, uxmlContent, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(ussFilePath);
            AssetDatabase.ImportAsset(uxmlFilePath);

            var ussAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ussFilePath);
            var uxmlAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(uxmlFilePath);
            if (ussAsset != null) WorkflowManager.SnapshotObject(ussAsset, SnapshotType.Created);
            if (uxmlAsset != null) WorkflowManager.SnapshotObject(uxmlAsset, SnapshotType.Created);

            return new { success = true, template, ussPath = ussFilePath, uxmlPath = uxmlFilePath, name = uiName };
        }

        // ============================ BATCH ============================

        [UnitySkill("uitk_create_batch", "Batch create USS/UXML files. items: JSON array of {type,savePath,content?,ussPath?}")]
        public static object UitkCreateBatch(string items)
        {
            return BatchExecutor.Execute<UitkFileItem>(
                items,
                item =>
                {
                    if (string.IsNullOrEmpty(item.type))
                        return new { error = "type is required ('uss' or 'uxml')" };
                    if (string.IsNullOrEmpty(item.savePath))
                        return new { error = "savePath is required" };

                    return item.type.ToLowerInvariant() == "uss"
                        ? UitkCreateUss(item.savePath, item.content)
                        : item.type.ToLowerInvariant() == "uxml"
                            ? UitkCreateUxml(item.savePath, item.content, item.ussPath)
                            : (object)new { error = $"Unknown type '{item.type}', expected 'uss' or 'uxml'" };
                },
                item => item.savePath,
                AssetDatabase.StartAssetEditing,
                AssetDatabase.StopAssetEditing
            );
        }

        // ============================ PANEL SETTINGS HELPERS ============================

        private static DynamicAtlasFilters ParseDynamicAtlasFilters(string filters)
        {
            if (string.IsNullOrEmpty(filters)) return DynamicAtlasFilters.None;
            var trimmed = filters.Trim();
            if (string.Equals(trimmed, "Everything", System.StringComparison.OrdinalIgnoreCase))
                return (DynamicAtlasFilters)(-1);
            if (string.Equals(trimmed, "None", System.StringComparison.OrdinalIgnoreCase))
                return DynamicAtlasFilters.None;

            DynamicAtlasFilters result = DynamicAtlasFilters.None;
            foreach (var part in trimmed.Split(','))
            {
                if (System.Enum.TryParse<DynamicAtlasFilters>(part.Trim(), true, out var flag))
                    result |= flag;
            }
            return result;
        }

        /// <summary>
        /// Shared helper to apply extended PanelSettings properties (used by create and set).
        /// Returns null on success, or an error object on failure.
        /// </summary>
        private static object ApplyPanelSettings(PanelSettings settings,
            string textSettingsPath, string targetTexturePath, int? targetDisplay,
            float? sortOrder, float? scale, float? match,
            float? referenceDpi, float? fallbackDpi, float? referenceSpritePixelsPerUnit,
            int? dynamicAtlasMinSize, int? dynamicAtlasMaxSize, int? dynamicAtlasMaxSubTextureSize,
            string dynamicAtlasFilters,
            bool? clearColor, float? colorClearR, float? colorClearG, float? colorClearB, float? colorClearA,
            bool? clearDepthStencil,
            string renderMode, bool? forceGammaRendering, string bindingLogLevel,
            string colliderUpdateMode, bool? colliderIsTrigger,
            int? vertexBudget, int? textureSlotCount)
        {
            // --- Asset references ---
            if (!string.IsNullOrEmpty(textSettingsPath))
            {
                if (Validate.SafePath(textSettingsPath, "textSettingsPath") is object tsErr) return tsErr;
                var ts = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(textSettingsPath);
                if (ts == null) return new { error = $"PanelTextSettings not found: {textSettingsPath}" };
                settings.textSettings = ts;
            }

            if (!string.IsNullOrEmpty(targetTexturePath))
            {
                if (Validate.SafePath(targetTexturePath, "targetTexturePath") is object ttErr) return ttErr;
                var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(targetTexturePath);
                if (rt == null) return new { error = $"RenderTexture not found: {targetTexturePath}" };
                settings.targetTexture = rt;
            }

            // --- Numeric properties ---
            if (targetDisplay.HasValue)  settings.targetDisplay = targetDisplay.Value;
            if (sortOrder.HasValue)      settings.sortingOrder = sortOrder.Value;
            if (scale.HasValue)          settings.scale = scale.Value;
            if (match.HasValue)          settings.match = match.Value;
            if (referenceDpi.HasValue)   settings.referenceDpi = referenceDpi.Value;
            if (fallbackDpi.HasValue)    settings.fallbackDpi = fallbackDpi.Value;
            if (referenceSpritePixelsPerUnit.HasValue)
            {
                var rsppu = typeof(PanelSettings).GetProperty("referenceSpritePixelsPerUnit");
                rsppu?.SetValue(settings, referenceSpritePixelsPerUnit.Value);
            }

            // --- Dynamic Atlas Settings (struct: read → modify → write back) ---
            if (dynamicAtlasMinSize.HasValue || dynamicAtlasMaxSize.HasValue ||
                dynamicAtlasMaxSubTextureSize.HasValue || !string.IsNullOrEmpty(dynamicAtlasFilters))
            {
                var atlas = settings.dynamicAtlasSettings;
                if (dynamicAtlasMinSize.HasValue)        atlas.minAtlasSize = dynamicAtlasMinSize.Value;
                if (dynamicAtlasMaxSize.HasValue)        atlas.maxAtlasSize = dynamicAtlasMaxSize.Value;
                if (dynamicAtlasMaxSubTextureSize.HasValue) atlas.maxSubTextureSize = dynamicAtlasMaxSubTextureSize.Value;
                if (!string.IsNullOrEmpty(dynamicAtlasFilters)) atlas.activeFilters = ParseDynamicAtlasFilters(dynamicAtlasFilters);
                settings.dynamicAtlasSettings = atlas;
            }

            // --- Color Clear ---
            if (clearColor.HasValue)        settings.clearColor = clearColor.Value;
            if (clearDepthStencil.HasValue) settings.clearDepthStencil = clearDepthStencil.Value;

            if (colorClearR.HasValue || colorClearG.HasValue || colorClearB.HasValue || colorClearA.HasValue)
            {
                var c = settings.colorClearValue;
                settings.colorClearValue = new Color(
                    colorClearR ?? c.r, colorClearG ?? c.g, colorClearB ?? c.b, colorClearA ?? c.a);
            }

            // --- Unity 6+ properties ---
#if UNITY_6000_0_OR_NEWER
            if (forceGammaRendering.HasValue) settings.forceGammaRendering = forceGammaRendering.Value;
            if (!string.IsNullOrEmpty(bindingLogLevel) && System.Enum.TryParse<UnityEngine.UIElements.BindingLogLevel>(bindingLogLevel, true, out var parsedLogLevel))
                settings.bindingLogLevel = parsedLogLevel;
            if (vertexBudget.HasValue)     settings.vertexBudget = (uint)vertexBudget.Value;
            if (textureSlotCount.HasValue) settings.textureSlotCount = (TextureSlotCount)textureSlotCount.Value;

            // renderMode, colliderUpdateMode, colliderIsTrigger are internal — use SerializedObject
            if (!string.IsNullOrEmpty(renderMode) || !string.IsNullOrEmpty(colliderUpdateMode) || colliderIsTrigger.HasValue)
            {
                var so = new SerializedObject(settings);
                if (!string.IsNullOrEmpty(renderMode))
                {
                    var prop = so.FindProperty("m_RenderMode");
                    if (prop != null)
                    {
                        if (renderMode.Equals("ScreenSpaceOverlay", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 0;
                        else if (renderMode.Equals("WorldSpace", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 1;
                    }
                }
                if (!string.IsNullOrEmpty(colliderUpdateMode))
                {
                    var prop = so.FindProperty("m_ColliderUpdateMode");
                    if (prop != null)
                    {
                        if (colliderUpdateMode.Equals("Match3DBoundingBox", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 0;
                        else if (colliderUpdateMode.Equals("Match2DDocumentRect", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 1;
                        else if (colliderUpdateMode.Equals("KeepExistingCollider", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 2;
                    }
                }
                if (colliderIsTrigger.HasValue)
                {
                    var prop = so.FindProperty("m_ColliderIsTrigger");
                    if (prop != null) prop.boolValue = colliderIsTrigger.Value;
                }
                so.ApplyModifiedProperties();
            }
#endif

            return null; // success
        }

        // ============================ PRIVATE HELPERS ============================

        private class UitkFileItem
        {
            public string type { get; set; }
            public string savePath { get; set; }
            public string content { get; set; }
            public string ussPath { get; set; }
        }

        private static object ParseXmlNode(XElement element, int currentDepth, int maxDepth)
        {
            var tag = element.Name.LocalName;
            var attrs = element.Attributes()
                .Where(a => !a.IsNamespaceDeclaration)
                .ToDictionary(a => a.Name.LocalName, a => a.Value);

            var childElements = element.Elements().ToArray();
            if (currentDepth >= maxDepth && childElements.Length > 0)
                return new { tag, attributes = attrs, children = new[] { new { note = $"[{childElements.Length} children — truncated at depth {maxDepth}]" } } };

            var children = childElements
                .Select(c => ParseXmlNode(c, currentDepth + 1, maxDepth))
                .ToArray();

            return new { tag, attributes = attrs, children };
        }

        private static void GetTemplateContent(string template, string uiName, string ussFilePath,
            out string ussContent, out string uxmlContent)
        {
            switch (template)
            {
                case "menu":    ussContent = MenuUss(uiName);      uxmlContent = MenuUxml(uiName, ussFilePath);      break;
                case "hud":     ussContent = HudUss(uiName);       uxmlContent = HudUxml(uiName, ussFilePath);       break;
                case "dialog":  ussContent = DialogUss(uiName);    uxmlContent = DialogUxml(uiName, ussFilePath);    break;
                case "settings":ussContent = SettingsUss(uiName);  uxmlContent = SettingsUxml(uiName, ussFilePath);  break;
                case "inventory":ussContent = InventoryUss(uiName);uxmlContent = InventoryUxml(uiName, ussFilePath); break;
                case "list":    ussContent = ListUss(uiName);      uxmlContent = ListUxml(uiName, ussFilePath);      break;
                default:        ussContent = DefaultUss(uiName);   uxmlContent = DefaultUxml(ussFilePath);           break;
            }
        }

        // Default templates
        private static string DefaultUss(string name) =>
$@"/* {name} Stylesheet */
:root {{
    --primary-color: #2D2D2D;
    --text-color: #E0E0E0;
    --accent-color: #4A90D9;
}}
";

        private static string DefaultUxml(string ussPath = null)
        {
            if (ussPath != null)
                return $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<engine:UXML xmlns:engine=\"UnityEngine.UIElements\">\n    <Style src=\"{ussPath}\" />\n</engine:UXML>\n";
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<engine:UXML xmlns:engine=\"UnityEngine.UIElements\">\n</engine:UXML>\n";
        }

        // --- Menu ---
        private static string MenuUss(string n) =>
$@"/* {n} Menu */
:root {{ --bg: #1A1A2E; --btn-bg: #16213E; --btn-hover: #0F3460; --text: #E0E0E0; --accent: #E94560; }}
.menu-root {{ width: 100%; height: 100%; background-color: var(--bg); align-items: center; justify-content: center; }}
.menu-title {{ font-size: 48px; color: var(--text); margin-bottom: 40px; -unity-font-style: bold; }}
.menu-btn {{ width: 200px; height: 50px; margin-bottom: 10px; background-color: var(--btn-bg); border-color: var(--accent); border-width: 1px; border-radius: 4px; color: var(--text); font-size: 18px; }}
.menu-btn:hover {{ background-color: var(--btn-hover); }}
";

        private static string MenuUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""menu-root"">
        <engine:Label class=""menu-title"" text=""{n}"" />
        <engine:Button class=""menu-btn"" text=""Play"" name=""btn-play"" />
        <engine:Button class=""menu-btn"" text=""Settings"" name=""btn-settings"" />
        <engine:Button class=""menu-btn"" text=""Quit"" name=""btn-quit"" />
    </engine:VisualElement>
</engine:UXML>
";

        // --- HUD ---
        private static string HudUss(string n) =>
$@"/* {n} HUD */
.hud-root {{ width: 100%; height: 100%; position: absolute; }}
.minimap {{ position: absolute; top: 10px; left: 10px; width: 150px; height: 150px; background-color: rgba(0,0,0,0.6); border-width: 2px; border-color: rgba(255,255,255,0.3); border-radius: 4px; }}
.score-label {{ position: absolute; top: 10px; right: 20px; color: white; font-size: 24px; -unity-font-style: bold; }}
.health-bar-bg {{ position: absolute; left: 20px; bottom: 20px; width: 200px; height: 20px; background-color: rgba(0,0,0,0.5); border-radius: 4px; }}
.health-bar-fill {{ width: 100%; height: 100%; background-color: #4CAF50; border-radius: 4px; }}
";

        private static string HudUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""hud-root"" name=""{n}"">
        <engine:VisualElement class=""minimap"" name=""minimap"" />
        <engine:Label class=""score-label"" text=""Score: 0"" name=""score-label"" />
        <engine:VisualElement class=""health-bar-bg"">
            <engine:VisualElement class=""health-bar-fill"" name=""health-bar"" />
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // --- Dialog ---
        private static string DialogUss(string n) =>
$@"/* {n} Dialog */
.dialog-overlay {{ width: 100%; height: 100%; background-color: rgba(0,0,0,0.5); align-items: center; justify-content: center; }}
.dialog-box {{ width: 400px; background-color: #2D2D2D; border-radius: 8px; padding: 24px; border-width: 1px; border-color: #555; }}
.dialog-title {{ font-size: 20px; color: white; -unity-font-style: bold; margin-bottom: 12px; }}
.dialog-msg {{ font-size: 14px; color: #CCC; white-space: normal; margin-bottom: 24px; }}
.dialog-btns {{ flex-direction: row; justify-content: flex-end; }}
.dialog-btn {{ height: 36px; padding-left: 16px; padding-right: 16px; margin-left: 8px; border-radius: 4px; font-size: 14px; }}
.dialog-btn-ok {{ background-color: #4A90D9; color: white; border-width: 0; }}
.dialog-btn-cancel {{ background-color: transparent; color: #AAA; border-color: #555; border-width: 1px; }}
";

        private static string DialogUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""dialog-overlay"">
        <engine:VisualElement class=""dialog-box"">
            <engine:Label class=""dialog-title"" text=""{n}"" name=""dialog-title"" />
            <engine:Label class=""dialog-msg"" text=""Are you sure?"" name=""dialog-msg"" />
            <engine:VisualElement class=""dialog-btns"">
                <engine:Button class=""dialog-btn dialog-btn-cancel"" text=""Cancel"" name=""btn-cancel"" />
                <engine:Button class=""dialog-btn dialog-btn-ok"" text=""OK"" name=""btn-ok"" />
            </engine:VisualElement>
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // --- Settings ---
        private static string SettingsUss(string n) =>
$@"/* {n} Settings */
.settings-root {{ width: 100%; height: 100%; background-color: #1E1E1E; padding: 24px; }}
.settings-title {{ font-size: 28px; color: white; -unity-font-style: bold; margin-bottom: 24px; }}
.settings-row {{ flex-direction: row; align-items: center; margin-bottom: 16px; height: 40px; }}
.settings-label {{ width: 150px; color: #CCC; font-size: 14px; }}
.settings-control {{ flex-grow: 1; }}
";

        private static string SettingsUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""settings-root"">
        <engine:Label class=""settings-title"" text=""{n}"" />
        <engine:VisualElement class=""settings-row"">
            <engine:Label class=""settings-label"" text=""Music Volume"" />
            <engine:Slider class=""settings-control"" name=""music-vol"" low-value=""0"" high-value=""1"" value=""0.8"" />
        </engine:VisualElement>
        <engine:VisualElement class=""settings-row"">
            <engine:Label class=""settings-label"" text=""SFX Volume"" />
            <engine:Slider class=""settings-control"" name=""sfx-vol"" low-value=""0"" high-value=""1"" value=""1"" />
        </engine:VisualElement>
        <engine:VisualElement class=""settings-row"">
            <engine:Label class=""settings-label"" text=""Fullscreen"" />
            <engine:Toggle class=""settings-control"" name=""fullscreen"" value=""true"" />
        </engine:VisualElement>
        <engine:VisualElement class=""settings-row"">
            <engine:Label class=""settings-label"" text=""Quality"" />
            <engine:DropdownField class=""settings-control"" name=""quality"" />
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // --- Inventory ---
        private static string InventoryUss(string n) =>
$@"/* {n} Inventory */
.inv-root {{ width: 100%; height: 100%; background-color: rgba(20,20,20,0.95); padding: 16px; }}
.inv-title {{ font-size: 22px; color: #E0E0E0; -unity-font-style: bold; margin-bottom: 12px; }}
.inv-scroll {{ flex-grow: 1; }}
.inv-grid {{ flex-direction: row; flex-wrap: wrap; }}
.inv-slot {{ width: 64px; height: 64px; margin: 4px; background-color: #2A2A2A; border-color: #444; border-width: 1px; border-radius: 4px; align-items: center; justify-content: center; }}
.inv-slot:hover {{ border-color: #4A90D9; }}
";

        private static string InventoryUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""inv-root"">
        <engine:Label class=""inv-title"" text=""{n}"" />
        <engine:ScrollView class=""inv-scroll"" name=""scroll"">
            <engine:VisualElement class=""inv-grid"" name=""grid"">
                <engine:VisualElement class=""inv-slot"" name=""slot-0"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-1"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-2"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-3"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-4"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-5"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-6"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-7"" />
                <engine:VisualElement class=""inv-slot"" name=""slot-8"" />
            </engine:VisualElement>
        </engine:ScrollView>
    </engine:VisualElement>
</engine:UXML>
";

        // --- List ---
        private static string ListUss(string n) =>
$@"/* {n} List */
.list-root {{ width: 100%; height: 100%; background-color: #1A1A1A; padding: 16px; }}
.list-title {{ font-size: 20px; color: #E0E0E0; -unity-font-style: bold; margin-bottom: 12px; }}
.list-scroll {{ flex-grow: 1; background-color: #222; border-radius: 4px; }}
.list-item {{ height: 48px; padding-left: 16px; padding-right: 16px; border-bottom-width: 1px; border-color: #333; align-items: center; flex-direction: row; }}
.list-item:hover {{ background-color: #2A3A4A; }}
.list-item-label {{ color: #CCC; font-size: 14px; flex-grow: 1; }}
";

        private static string ListUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""list-root"">
        <engine:Label class=""list-title"" text=""{n}"" />
        <engine:ScrollView class=""list-scroll"" name=""scroll"">
            <engine:VisualElement class=""list-item""><engine:Label class=""list-item-label"" text=""Item 1"" /></engine:VisualElement>
            <engine:VisualElement class=""list-item""><engine:Label class=""list-item-label"" text=""Item 2"" /></engine:VisualElement>
            <engine:VisualElement class=""list-item""><engine:Label class=""list-item-label"" text=""Item 3"" /></engine:VisualElement>
        </engine:ScrollView>
    </engine:VisualElement>
</engine:UXML>
";
    }
}
