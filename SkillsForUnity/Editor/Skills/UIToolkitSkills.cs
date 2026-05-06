using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Globalization;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// UI Toolkit skills - create/edit USS/UXML files and configure UIDocument in scenes.
    /// Requires Unity 2022.3+ (package minimum supported version).
    /// </summary>
    public static class UIToolkitSkills
    {
        // ============================ FILE OPERATIONS ============================

        [UnitySkill("uitk_create_uss", "Create a USS stylesheet file for UI Toolkit",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "uss", "stylesheet", "ui-toolkit", "style" },
            Outputs = new[] { "path", "lines" },
            TracksWorkflow = true)]
        public static object UitkCreateUss(string savePath, string content = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var fileContent = content ?? DefaultUss(Path.GetFileNameWithoutExtension(savePath));
            File.WriteAllText(savePath, fileContent, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, lines = fileContent.Split('\n').Length };
        }

        [UnitySkill("uitk_create_uxml", "Create a UXML layout file for UI Toolkit",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "uxml", "layout", "ui-toolkit", "visual-tree" },
            Outputs = new[] { "path", "lines" },
            TracksWorkflow = true)]
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
            File.WriteAllText(savePath, fileContent, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, lines = fileContent.Split('\n').Length };
        }

        [UnitySkill("uitk_read_file", "Read USS or UXML file content",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "read", "uss", "uxml", "file" },
            Outputs = new[] { "path", "type", "lines", "content" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true)]
        public static object UitkReadFile(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            return new
            {
                path = filePath,
                type = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
                lines = content.Split('\n').Length,
                content
            };
        }

        [UnitySkill("uitk_write_file", "Write or overwrite a USS or UXML file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "write", "uss", "uxml", "file" },
            Outputs = new[] { "path", "lines" },
            TracksWorkflow = true)]
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

            File.WriteAllText(filePath, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, lines = content.Split('\n').Length };
        }

        [UnitySkill("uitk_delete_file", "Delete a USS or UXML file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Delete,
            Tags = new[] { "delete", "uss", "uxml", "file" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true)]
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

        [UnitySkill("uitk_find_files", "Search for USS and/or UXML files in the project (type: uss/uxml/all)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "find", "search", "uss", "uxml" },
            Outputs = new[] { "count", "files" },
            ReadOnly = true)]
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

        [UnitySkill("uitk_create_document", "Create a GameObject with UIDocument component in the scene",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "ui-document", "scene", "ui-toolkit", "visual-tree" },
            Outputs = new[] { "name", "instanceId", "hasUxml", "hasPanelSettings", "sortOrder" },
            TracksWorkflow = true)]
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

        [UnitySkill("uitk_set_document", "Set UIDocument properties on an existing scene GameObject",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "ui-document", "configure", "uxml", "panel-settings" },
            Outputs = new[] { "name", "instanceId", "visualTreeAsset", "panelSettings", "sortingOrder" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
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

        [UnitySkill("uitk_create_panel_settings", "Create a PanelSettings asset for UI Toolkit",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "panel-settings", "asset", "scaling", "resolution" },
            Outputs = new[] { "path", "scaleMode", "referenceResolution", "screenMatchMode" },
            TracksWorkflow = true)]
        public static object UitkCreatePanelSettings(
            string savePath,
            string scaleMode = "ScaleWithScreenSize",
            int referenceResolutionX = 1920,
            int referenceResolutionY = 1080,
            string screenMatchMode = "MatchWidthOrHeight",
            string themeStyleSheetPath = null,
            // General properties (Unity 2022.3+)
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

            var applyErr = ApplyPanelSettings(settings, new PanelSettingsArgs
            {
                textSettingsPath = textSettingsPath, targetTexturePath = targetTexturePath,
                targetDisplay = targetDisplay, sortOrder = sortOrder, scale = scale, match = match,
                referenceDpi = referenceDpi, fallbackDpi = fallbackDpi, referenceSpritePixelsPerUnit = referenceSpritePixelsPerUnit,
                dynamicAtlasMinSize = dynamicAtlasMinSize, dynamicAtlasMaxSize = dynamicAtlasMaxSize,
                dynamicAtlasMaxSubTextureSize = dynamicAtlasMaxSubTextureSize, dynamicAtlasFilters = dynamicAtlasFilters,
                clearColor = clearColor, colorClearR = colorClearR, colorClearG = colorClearG,
                colorClearB = colorClearB, colorClearA = colorClearA, clearDepthStencil = clearDepthStencil,
                renderMode = renderMode, forceGammaRendering = forceGammaRendering, bindingLogLevel = bindingLogLevel,
                colliderUpdateMode = colliderUpdateMode, colliderIsTrigger = colliderIsTrigger,
                vertexBudget = vertexBudget, textureSlotCount = textureSlotCount
            });
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

        [UnitySkill("uitk_get_panel_settings", "Read all properties of a PanelSettings asset",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "panel-settings", "inspect", "read", "properties" },
            Outputs = new[] { "path", "scaleMode", "referenceResolution", "screenMatchMode", "dynamicAtlasSettings" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true)]
        public static object UitkGetPanelSettings(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(assetPath);
            if (settings == null)
                return new { error = $"PanelSettings not found: {assetPath}" };

            var atlas = settings.dynamicAtlasSettings;
            var cc = settings.colorClearValue;

#if UNITY_6000_0_OR_NEWER
            // renderMode, colliderUpdateMode, and colliderIsTrigger are internal; read them via SerializedObject.
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
#if UNITY_6000_3_OR_NEWER
                vertexBudget = settings.vertexBudget,
                textureSlotCount = (int)settings.textureSlotCount
#else
                vertexBudget = settings.vertexBudget
#endif
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

        [UnitySkill("uitk_set_panel_settings", "Modify properties on an existing PanelSettings asset",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "panel-settings", "configure", "scaling", "resolution" },
            Outputs = new[] { "path", "scaleMode", "referenceResolution", "screenMatchMode" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
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

            var applyErr = ApplyPanelSettings(settings, new PanelSettingsArgs
            {
                textSettingsPath = textSettingsPath, targetTexturePath = targetTexturePath,
                targetDisplay = targetDisplay, sortOrder = sortOrder, scale = scale, match = match,
                referenceDpi = referenceDpi, fallbackDpi = fallbackDpi, referenceSpritePixelsPerUnit = referenceSpritePixelsPerUnit,
                dynamicAtlasMinSize = dynamicAtlasMinSize, dynamicAtlasMaxSize = dynamicAtlasMaxSize,
                dynamicAtlasMaxSubTextureSize = dynamicAtlasMaxSubTextureSize, dynamicAtlasFilters = dynamicAtlasFilters,
                clearColor = clearColor, colorClearR = colorClearR, colorClearG = colorClearG,
                colorClearB = colorClearB, colorClearA = colorClearA, clearDepthStencil = clearDepthStencil,
                renderMode = renderMode, forceGammaRendering = forceGammaRendering, bindingLogLevel = bindingLogLevel,
                colliderUpdateMode = colliderUpdateMode, colliderIsTrigger = colliderIsTrigger,
                vertexBudget = vertexBudget, textureSlotCount = textureSlotCount
            });
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

        [UnitySkill("uitk_list_documents", "List all UIDocument components in the active scene",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "list", "ui-document", "scene", "inspect" },
            Outputs = new[] { "count", "documents" },
            ReadOnly = true)]
        public static object UitkListDocuments()
        {
            var docs = FindHelper.FindAll<UIDocument>();
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

        [UnitySkill("uitk_inspect_uxml", "Parse and display UXML element hierarchy (depth controls max traversal depth)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "uxml", "hierarchy", "parse" },
            Outputs = new[] { "path", "hierarchy" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true)]
        public static object UitkInspectUxml(string filePath, int depth = 5)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            try
            {
                var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
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

        [UnitySkill("uitk_create_from_template", "Create a UXML+USS file pair from a template (menu/hud/dialog/settings/inventory/list/tab-view/toolbar/card/notification)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "template", "uxml", "uss", "scaffold" },
            Outputs = new[] { "template", "ussPath", "uxmlPath", "name" },
            TracksWorkflow = true)]
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

            File.WriteAllText(ussFilePath, ussContent, SkillsCommon.Utf8NoBom);
            File.WriteAllText(uxmlFilePath, uxmlContent, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(ussFilePath);
            AssetDatabase.ImportAsset(uxmlFilePath);

            var ussAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ussFilePath);
            var uxmlAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(uxmlFilePath);
            if (ussAsset != null) WorkflowManager.SnapshotObject(ussAsset, SnapshotType.Created);
            if (uxmlAsset != null) WorkflowManager.SnapshotObject(uxmlAsset, SnapshotType.Created);

            return new { success = true, template, ussPath = ussFilePath, uxmlPath = uxmlFilePath, name = uiName };
        }

        // ============================ BATCH ============================

        [UnitySkill("uitk_create_batch", "Batch create USS/UXML files. items: JSON array of {type,savePath,content?,ussPath?}",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "batch", "uss", "uxml", "bulk" },
            Outputs = new[] { "totalRequested", "succeeded", "failed", "results" },
            TracksWorkflow = true)]
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

        private struct PanelSettingsArgs
        {
            public string textSettingsPath;
            public string targetTexturePath;
            public int? targetDisplay;
            public float? sortOrder;
            public float? scale;
            public float? match;
            public float? referenceDpi;
            public float? fallbackDpi;
            public float? referenceSpritePixelsPerUnit;
            public int? dynamicAtlasMinSize;
            public int? dynamicAtlasMaxSize;
            public int? dynamicAtlasMaxSubTextureSize;
            public string dynamicAtlasFilters;
            public bool? clearColor;
            public float? colorClearR;
            public float? colorClearG;
            public float? colorClearB;
            public float? colorClearA;
            public bool? clearDepthStencil;
            public string renderMode;
            public bool? forceGammaRendering;
            public string bindingLogLevel;
            public string colliderUpdateMode;
            public bool? colliderIsTrigger;
            public int? vertexBudget;
            public int? textureSlotCount;
        }

        /// <summary>
        /// Shared helper to apply extended PanelSettings properties (used by create and set).
        /// Returns null on success, or an error object on failure.
        /// </summary>
        private static object ApplyPanelSettings(PanelSettings settings, in PanelSettingsArgs a)
        {
            // --- Asset references ---
            if (!string.IsNullOrEmpty(a.textSettingsPath))
            {
                if (Validate.SafePath(a.textSettingsPath, "textSettingsPath") is object tsErr) return tsErr;
                var ts = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(a.textSettingsPath);
                if (ts == null) return new { error = $"PanelTextSettings not found: {a.textSettingsPath}" };
                settings.textSettings = ts;
            }

            if (!string.IsNullOrEmpty(a.targetTexturePath))
            {
                if (Validate.SafePath(a.targetTexturePath, "targetTexturePath") is object ttErr) return ttErr;
                var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(a.targetTexturePath);
                if (rt == null) return new { error = $"RenderTexture not found: {a.targetTexturePath}" };
                settings.targetTexture = rt;
            }

            // --- Numeric properties ---
            if (a.targetDisplay.HasValue)  settings.targetDisplay = a.targetDisplay.Value;
            if (a.sortOrder.HasValue)      settings.sortingOrder = a.sortOrder.Value;
            if (a.scale.HasValue)          settings.scale = a.scale.Value;
            if (a.match.HasValue)          settings.match = a.match.Value;
            if (a.referenceDpi.HasValue)   settings.referenceDpi = a.referenceDpi.Value;
            if (a.fallbackDpi.HasValue)    settings.fallbackDpi = a.fallbackDpi.Value;
            if (a.referenceSpritePixelsPerUnit.HasValue)
            {
                var rsppu = typeof(PanelSettings).GetProperty("referenceSpritePixelsPerUnit");
                rsppu?.SetValue(settings, a.referenceSpritePixelsPerUnit.Value);
            }

            // --- Dynamic Atlas Settings (struct: read -> modify -> write back) ---
            if (a.dynamicAtlasMinSize.HasValue || a.dynamicAtlasMaxSize.HasValue ||
                a.dynamicAtlasMaxSubTextureSize.HasValue || !string.IsNullOrEmpty(a.dynamicAtlasFilters))
            {
                var atlas = settings.dynamicAtlasSettings;
                if (a.dynamicAtlasMinSize.HasValue)        atlas.minAtlasSize = a.dynamicAtlasMinSize.Value;
                if (a.dynamicAtlasMaxSize.HasValue)        atlas.maxAtlasSize = a.dynamicAtlasMaxSize.Value;
                if (a.dynamicAtlasMaxSubTextureSize.HasValue) atlas.maxSubTextureSize = a.dynamicAtlasMaxSubTextureSize.Value;
                if (!string.IsNullOrEmpty(a.dynamicAtlasFilters)) atlas.activeFilters = ParseDynamicAtlasFilters(a.dynamicAtlasFilters);
                settings.dynamicAtlasSettings = atlas;
            }

            // --- Color Clear ---
            if (a.clearColor.HasValue)        settings.clearColor = a.clearColor.Value;
            if (a.clearDepthStencil.HasValue) settings.clearDepthStencil = a.clearDepthStencil.Value;

            if (a.colorClearR.HasValue || a.colorClearG.HasValue || a.colorClearB.HasValue || a.colorClearA.HasValue)
            {
                var c = settings.colorClearValue;
                settings.colorClearValue = new Color(
                    a.colorClearR ?? c.r, a.colorClearG ?? c.g, a.colorClearB ?? c.b, a.colorClearA ?? c.a);
            }

            // --- Unity 6+ properties ---
#if UNITY_6000_0_OR_NEWER
            if (a.forceGammaRendering.HasValue) settings.forceGammaRendering = a.forceGammaRendering.Value;
            if (!string.IsNullOrEmpty(a.bindingLogLevel) && System.Enum.TryParse<UnityEngine.UIElements.BindingLogLevel>(a.bindingLogLevel, true, out var parsedLogLevel))
                settings.bindingLogLevel = parsedLogLevel;
            if (a.vertexBudget.HasValue)     settings.vertexBudget = (uint)a.vertexBudget.Value;
#if UNITY_6000_3_OR_NEWER
            if (a.textureSlotCount.HasValue) settings.textureSlotCount = (TextureSlotCount)a.textureSlotCount.Value;
#endif

            // renderMode, colliderUpdateMode, and colliderIsTrigger are internal; update them via SerializedObject.
            if (!string.IsNullOrEmpty(a.renderMode) || !string.IsNullOrEmpty(a.colliderUpdateMode) || a.colliderIsTrigger.HasValue)
            {
                var so = new SerializedObject(settings);
                if (!string.IsNullOrEmpty(a.renderMode))
                {
                    var prop = so.FindProperty("m_RenderMode");
                    if (prop != null)
                    {
                        if (a.renderMode.Equals("ScreenSpaceOverlay", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 0;
                        else if (a.renderMode.Equals("WorldSpace", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 1;
                    }
                }
                if (!string.IsNullOrEmpty(a.colliderUpdateMode))
                {
                    var prop = so.FindProperty("m_ColliderUpdateMode");
                    if (prop != null)
                    {
                        if (a.colliderUpdateMode.Equals("Match3DBoundingBox", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 0;
                        else if (a.colliderUpdateMode.Equals("Match2DDocumentRect", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 1;
                        else if (a.colliderUpdateMode.Equals("KeepExistingCollider", System.StringComparison.OrdinalIgnoreCase)) prop.intValue = 2;
                    }
                }
                if (a.colliderIsTrigger.HasValue)
                {
                    var prop = so.FindProperty("m_ColliderIsTrigger");
                    if (prop != null) prop.boolValue = a.colliderIsTrigger.Value;
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
                return new { tag, attributes = attrs, children = new[] { new { note = $"[{childElements.Length} children; truncated at depth {maxDepth}]" } } };

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
                case "tab-view":ussContent = TabViewUss(uiName);   uxmlContent = TabViewUxml(uiName, ussFilePath);   break;
                case "toolbar": ussContent = ToolbarUss(uiName);   uxmlContent = ToolbarUxml(uiName, ussFilePath);   break;
                case "card":    ussContent = CardUss(uiName);      uxmlContent = CardUxml(uiName, ussFilePath);      break;
                case "notification": ussContent = NotificationUss(uiName); uxmlContent = NotificationUxml(uiName, ussFilePath); break;
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

        // --- Tab View ---
        private static string TabViewUss(string n) =>
$@"/* {n} Tab View */
.tab-root {{ width: 100%; height: 100%; background-color: #1E1E1E; }}
.tab-bar {{ flex-direction: row; background-color: #2D2D2D; border-bottom-width: 2px; border-color: #444; }}
.tab {{ padding: 8px 16px; color: #999; font-size: 14px; border-bottom-width: 2px; border-color: transparent; }}
.tab:hover {{ color: #CCC; }}
.tab--active {{ color: #FFF; border-color: #4A90D9; }}
.tab-content {{ flex-grow: 1; padding: 16px; display: none; }}
.tab-content--active {{ display: flex; }}
";

        private static string TabViewUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""tab-root"">
        <engine:VisualElement class=""tab-bar"">
            <engine:Label class=""tab tab--active"" text=""Tab 1"" name=""tab-1"" />
            <engine:Label class=""tab"" text=""Tab 2"" name=""tab-2"" />
            <engine:Label class=""tab"" text=""Tab 3"" name=""tab-3"" />
        </engine:VisualElement>
        <engine:VisualElement class=""tab-content tab-content--active"" name=""content-1"">
            <engine:Label text=""Tab 1 content"" />
        </engine:VisualElement>
        <engine:VisualElement class=""tab-content"" name=""content-2"">
            <engine:Label text=""Tab 2 content"" />
        </engine:VisualElement>
        <engine:VisualElement class=""tab-content"" name=""content-3"">
            <engine:Label text=""Tab 3 content"" />
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // --- Toolbar ---
        private static string ToolbarUss(string n) =>
$@"/* {n} Toolbar */
.toolbar-root {{ width: 100%; flex-direction: row; background-color: #333; height: 40px; align-items: center; padding: 0 8px; border-bottom-width: 1px; border-color: #555; }}
.toolbar-btn {{ height: 28px; padding: 0 12px; margin-right: 4px; background-color: #444; border-width: 0; border-radius: 4px; color: #DDD; font-size: 12px; }}
.toolbar-btn:hover {{ background-color: #555; }}
.toolbar-separator {{ width: 1px; height: 24px; background-color: #555; margin: 0 8px; }}
.toolbar-spacer {{ flex-grow: 1; }}
.toolbar-label {{ color: #AAA; font-size: 12px; margin-right: 8px; }}
";

        private static string ToolbarUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""toolbar-root"" name=""{n}"">
        <engine:Button class=""toolbar-btn"" text=""File"" name=""btn-file"" />
        <engine:Button class=""toolbar-btn"" text=""Edit"" name=""btn-edit"" />
        <engine:Button class=""toolbar-btn"" text=""View"" name=""btn-view"" />
        <engine:VisualElement class=""toolbar-separator"" />
        <engine:Button class=""toolbar-btn"" text=""Build"" name=""btn-build"" />
        <engine:VisualElement class=""toolbar-spacer"" />
        <engine:Label class=""toolbar-label"" text=""Ready"" name=""status-label"" />
    </engine:VisualElement>
</engine:UXML>
";

        // --- Card ---
        private static string CardUss(string n) =>
$@"/* {n} Card */
.card-container {{ flex-direction: row; flex-wrap: wrap; padding: 16px; }}
.card {{ width: 240px; margin: 8px; background-color: #2A2A2A; border-radius: 8px; border-width: 1px; border-color: #444; overflow: hidden; }}
.card:hover {{ border-color: #4A90D9; }}
.card-image {{ width: 100%; height: 140px; background-color: #3A3A3A; }}
.card-body {{ padding: 12px; }}
.card-title {{ font-size: 16px; color: #E0E0E0; -unity-font-style: bold; margin-bottom: 6px; }}
.card-desc {{ font-size: 12px; color: #999; white-space: normal; }}
.card-footer {{ flex-direction: row; padding: 8px 12px; border-top-width: 1px; border-color: #444; }}
.card-tag {{ padding: 2px 8px; background-color: #3A3A5A; border-radius: 10px; color: #8A8ACA; font-size: 10px; margin-right: 4px; }}
";

        private static string CardUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""card-container"">
        <engine:VisualElement class=""card"">
            <engine:VisualElement class=""card-image"" />
            <engine:VisualElement class=""card-body"">
                <engine:Label class=""card-title"" text=""Card Title"" />
                <engine:Label class=""card-desc"" text=""A short description of this card item."" />
            </engine:VisualElement>
            <engine:VisualElement class=""card-footer"">
                <engine:Label class=""card-tag"" text=""Tag 1"" />
                <engine:Label class=""card-tag"" text=""Tag 2"" />
            </engine:VisualElement>
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // --- Notification ---
        private static string NotificationUss(string n) =>
$@"/* {n} Notification */
.notif-container {{ position: absolute; top: 16px; right: 16px; width: 320px; }}
.notif {{ padding: 12px 16px; margin-bottom: 8px; border-radius: 6px; border-left-width: 4px; flex-direction: row; align-items: center; }}
.notif--info {{ background-color: rgba(74,144,217,0.15); border-color: #4A90D9; }}
.notif--success {{ background-color: rgba(76,175,80,0.15); border-color: #4CAF50; }}
.notif--warning {{ background-color: rgba(255,152,0,0.15); border-color: #FF9800; }}
.notif--error {{ background-color: rgba(244,67,54,0.15); border-color: #F44336; }}
.notif-text {{ flex-grow: 1; color: #E0E0E0; font-size: 13px; white-space: normal; }}
.notif-close {{ width: 20px; height: 20px; color: #888; font-size: 16px; -unity-text-align: middle-center; }}
.notif-close:hover {{ color: #FFF; }}
";

        private static string NotificationUxml(string n, string uss) =>
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<engine:UXML xmlns:engine=""UnityEngine.UIElements"">
    <Style src=""{uss}"" />
    <engine:VisualElement class=""notif-container"" name=""{n}"">
        <engine:VisualElement class=""notif notif--info"">
            <engine:Label class=""notif-text"" text=""Information message."" />
            <engine:Label class=""notif-close"" text=""x"" />
        </engine:VisualElement>
        <engine:VisualElement class=""notif notif--success"">
            <engine:Label class=""notif-text"" text=""Operation completed!"" />
            <engine:Label class=""notif-close"" text=""x"" />
        </engine:VisualElement>
        <engine:VisualElement class=""notif notif--warning"">
            <engine:Label class=""notif-text"" text=""Something needs attention."" />
            <engine:Label class=""notif-close"" text=""x"" />
        </engine:VisualElement>
    </engine:VisualElement>
</engine:UXML>
";

        // ============================ UXML ELEMENT OPERATIONS ============================

        private static readonly XNamespace EngineNs = "UnityEngine.UIElements";

        [UnitySkill("uitk_add_element", "Add an element to a UXML file (Label/Button/Toggle/Slider/TextField/VisualElement/etc.)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "add", "uxml", "element", "visual-element" },
            Outputs = new[] { "path", "elementType", "elementName", "parentName" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true)]
        public static object UitkAddElement(
            string filePath, string elementType, string parentName = null,
            string elementName = null, string text = null,
            string classes = null, string style = null,
            string bindingPath = null)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(elementType, "elementType") is object typeErr) return typeErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var xdoc = XDocument.Parse(content);

            var parent = string.IsNullOrEmpty(parentName)
                ? xdoc.Root
                : FindXmlElementByName(xdoc.Root, parentName);

            if (parent == null)
                return new { error = $"Parent element with name '{parentName}' not found" };

            var newElement = new XElement(EngineNs + elementType);
            if (!string.IsNullOrEmpty(elementName))
                newElement.SetAttributeValue("name", elementName);
            if (!string.IsNullOrEmpty(text))
                newElement.SetAttributeValue("text", text);
            if (!string.IsNullOrEmpty(classes))
                newElement.SetAttributeValue("class", classes);
            if (!string.IsNullOrEmpty(style))
                newElement.SetAttributeValue("style", style);
            if (!string.IsNullOrEmpty(bindingPath))
                newElement.SetAttributeValue("binding-path", bindingPath);

            parent.Add(newElement);
            File.WriteAllText(filePath, xdoc.ToString(), SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, elementType, elementName, parentName = parentName ?? "(root)" };
        }

        [UnitySkill("uitk_remove_element", "Remove an element from a UXML file by name",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Delete,
            Tags = new[] { "remove", "uxml", "element", "delete" },
            Outputs = new[] { "path", "removedElement" },
            RequiresInput = new[] { "filePath", "elementName" },
            TracksWorkflow = true)]
        public static object UitkRemoveElement(string filePath, string elementName)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(elementName, "elementName") is object nameErr) return nameErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var xdoc = XDocument.Parse(content);

            var target = FindXmlElementByName(xdoc.Root, elementName);
            if (target == null)
                return new { error = $"Element with name '{elementName}' not found" };

            target.Remove();
            File.WriteAllText(filePath, xdoc.ToString(), SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, removedElement = elementName };
        }

        [UnitySkill("uitk_modify_element", "Modify attributes of a UXML element by name",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "modify", "uxml", "element", "attribute" },
            Outputs = new[] { "path", "element" },
            RequiresInput = new[] { "filePath", "elementName" },
            TracksWorkflow = true)]
        public static object UitkModifyElement(
            string filePath, string elementName,
            string text = null, string classes = null, string style = null,
            string newName = null, string bindingPath = null,
            string setAttribute = null, string setAttributeValue = null)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(elementName, "elementName") is object nameErr) return nameErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var xdoc = XDocument.Parse(content);

            var target = FindXmlElementByName(xdoc.Root, elementName);
            if (target == null)
                return new { error = $"Element with name '{elementName}' not found" };

            if (text != null) target.SetAttributeValue("text", text);
            if (classes != null) target.SetAttributeValue("class", classes);
            if (style != null) target.SetAttributeValue("style", style);
            if (newName != null) target.SetAttributeValue("name", newName);
            if (bindingPath != null) target.SetAttributeValue("binding-path", bindingPath);
            if (!string.IsNullOrEmpty(setAttribute))
                target.SetAttributeValue(setAttribute, setAttributeValue ?? "");

            File.WriteAllText(filePath, xdoc.ToString(), SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, element = newName ?? elementName };
        }

        [UnitySkill("uitk_clone_element", "Clone (duplicate) an element in a UXML file by name",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "clone", "uxml", "duplicate", "element" },
            Outputs = new[] { "path", "clonedFrom", "newName" },
            RequiresInput = new[] { "filePath", "elementName" },
            TracksWorkflow = true)]
        public static object UitkCloneElement(string filePath, string elementName, string newName = null)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(elementName, "elementName") is object nameErr) return nameErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var xdoc = XDocument.Parse(content);

            var target = FindXmlElementByName(xdoc.Root, elementName);
            if (target == null)
                return new { error = $"Element with name '{elementName}' not found" };

            var clone = new XElement(target);
            if (!string.IsNullOrEmpty(newName))
                clone.SetAttributeValue("name", newName);

            target.AddAfterSelf(clone);
            File.WriteAllText(filePath, xdoc.ToString(), SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, clonedFrom = elementName, newName = newName ?? "(copy)" };
        }

        // ============================ USS OPERATIONS ============================

        [UnitySkill("uitk_add_uss_rule", "Add or update a USS rule in a stylesheet file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "uss", "rule", "selector", "style" },
            Outputs = new[] { "path", "selector", "action" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true)]
        public static object UitkAddUssRule(string filePath, string selector, string properties)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(selector, "selector") is object selErr) return selErr;
            if (Validate.Required(properties, "properties") is object propErr) return propErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var normalizedSelector = selector.Trim();

            // Try to find existing rule and replace it
            var pattern = System.Text.RegularExpressions.Regex.Escape(normalizedSelector) + @"\s*\{[^}]*\}";
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.FromSeconds(1));
            var newRule = $"{normalizedSelector} {{\n{FormatUssProperties(properties)}\n}}";

            string result;
            bool existed = regex.IsMatch(content);
            if (existed)
            {
                result = regex.Replace(content, newRule, 1);
            }
            else
            {
                result = content.TrimEnd() + "\n\n" + newRule + "\n";
            }

            File.WriteAllText(filePath, result, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, selector = normalizedSelector, action = existed ? "updated" : "added" };
        }

        [UnitySkill("uitk_remove_uss_rule", "Remove a USS rule by selector from a stylesheet file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Delete,
            Tags = new[] { "uss", "rule", "remove", "selector" },
            Outputs = new[] { "path", "removedSelector" },
            RequiresInput = new[] { "filePath", "selector" },
            TracksWorkflow = true)]
        public static object UitkRemoveUssRule(string filePath, string selector)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(selector, "selector") is object selErr) return selErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existing != null) WorkflowManager.SnapshotObject(existing);

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var normalizedSelector = selector.Trim();

            var pattern = @"\n?" + System.Text.RegularExpressions.Regex.Escape(normalizedSelector) + @"\s*\{[^}]*\}\n?";
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, System.TimeSpan.FromSeconds(1));

            if (!regex.IsMatch(content))
                return new { error = $"Selector '{normalizedSelector}' not found in {filePath}" };

            var result = regex.Replace(content, "\n", 1);
            File.WriteAllText(filePath, result, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new { success = true, path = filePath, removedSelector = normalizedSelector };
        }

        [UnitySkill("uitk_list_uss_variables", "Extract all CSS custom properties (--var-name) from a USS file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "uss", "variables", "custom-properties", "css" },
            Outputs = new[] { "path", "definedCount", "variables", "referencedVariables" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true)]
        public static object UitkListUssVariables(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var regex = new System.Text.RegularExpressions.Regex(
                @"(--[\w-]+)\s*:\s*([^;]+);",
                System.Text.RegularExpressions.RegexOptions.None,
                System.TimeSpan.FromSeconds(1));

            var variables = new System.Collections.Generic.List<object>();
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
            {
                var varName = match.Groups[1].Value.Trim();
                var varValue = match.Groups[2].Value.Trim();
                if (seen.Add(varName))
                    variables.Add(new { name = varName, value = varValue });
            }

            // Also find usages of var()
            var usageRegex = new System.Text.RegularExpressions.Regex(
                @"var\((--[\w-]+)\)",
                System.Text.RegularExpressions.RegexOptions.None,
                System.TimeSpan.FromSeconds(1));
            var usages = new System.Collections.Generic.HashSet<string>();
            foreach (System.Text.RegularExpressions.Match match in usageRegex.Matches(content))
                usages.Add(match.Groups[1].Value.Trim());

            return new
            {
                path = filePath,
                definedCount = variables.Count,
                variables,
                referencedVariables = usages.OrderBy(v => v).ToArray()
            };
        }

        // ============================ CODE GENERATION ============================

        [UnitySkill("uitk_create_editor_window", "Generate an EditorWindow C# script with UI Toolkit (CreateGUI + UXML/USS binding)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "editor-window", "codegen", "script", "ui-toolkit" },
            Outputs = new[] { "path", "className", "windowTitle", "menuPath" },
            TracksWorkflow = true)]
        public static object UitkCreateEditorWindow(
            string savePath, string className, string windowTitle = null,
            string uxmlPath = null, string ussPath = null,
            string menuPath = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (Validate.Required(className, "className") is object classErr) return classErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var title = windowTitle ?? className;
            var menu = menuPath ?? $"Window/{className}";

            var code = $@"using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class {className} : EditorWindow
{{
    [MenuItem(""{menu}"")]
    public static void ShowWindow()
    {{
        var wnd = GetWindow<{className}>();
        wnd.titleContent = new GUIContent(""{title}"");
        wnd.minSize = new Vector2(400, 300);
    }}

    public void CreateGUI()
    {{
        var root = rootVisualElement;
{(string.IsNullOrEmpty(ussPath) ? "" : $@"
        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(""{ussPath}"");
        if (styleSheet != null) root.styleSheets.Add(styleSheet);
")}
{(string.IsNullOrEmpty(uxmlPath) ? $@"
        // Build UI in code
        root.Add(new Label(""{title}""));
" : $@"
        var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(""{uxmlPath}"");
        if (visualTree != null) visualTree.CloneTree(root);
")}
        // Query elements and register callbacks
        // var button = root.Q<Button>(""my-button"");
        // button?.RegisterCallback<ClickEvent>(OnButtonClicked);
    }}
}}
";

            File.WriteAllText(savePath, code, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, className, windowTitle = title, menuPath = menu };
        }

        [UnitySkill("uitk_create_runtime_ui", "Generate a runtime MonoBehaviour script for UI Toolkit (UIDocument query & binding)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "runtime", "codegen", "monobehaviour", "ui-document" },
            Outputs = new[] { "path", "className" },
            TracksWorkflow = true)]
        public static object UitkCreateRuntimeUi(
            string savePath, string className,
            string elementQueries = null)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (Validate.Required(className, "className") is object classErr) return classErr;
            if (File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Parse element queries: "Button:my-button,Label:score-label"
            var queryLines = new System.Text.StringBuilder();
            var fields = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(elementQueries))
            {
                foreach (var q in elementQueries.Split(','))
                {
                    var parts = q.Trim().Split(':');
                    if (parts.Length != 2) continue;
                    var elType = parts[0].Trim();
                    var elName = parts[1].Trim();
                    var fieldName = "m_" + elName.Replace("-", "").Replace("_", "");
                    fields.AppendLine($"    private {elType} {fieldName};");
                    queryLines.AppendLine($"        {fieldName} = root.Q<{elType}>(\"{elName}\");");
                }
            }

            var code = $@"using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class {className} : MonoBehaviour
{{
{fields}
    private void OnEnable()
    {{
        var uiDocument = GetComponent<UIDocument>();
        var root = uiDocument.rootVisualElement;

{queryLines}
        // Register callbacks
        // m_myButton?.RegisterCallback<ClickEvent>(OnButtonClicked);
    }}

    private void OnDisable()
    {{
        // Unregister callbacks to prevent memory leaks
        // m_myButton?.UnregisterCallback<ClickEvent>(OnButtonClicked);
    }}
}}
";

            File.WriteAllText(savePath, code, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path = savePath, className };
        }

        // ============================ SCENE INSPECTION ============================

        [UnitySkill("uitk_inspect_document", "Inspect the live VisualElement hierarchy of a UIDocument in the scene",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "ui-document", "hierarchy", "visual-element" },
            Outputs = new[] { "gameObject", "instanceId", "hierarchy" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true)]
        public static object UitkInspectDocument(
            string name = null, int instanceId = 0, string path = null,
            int depth = 5)
        {
            var go = GameObjectFinder.Find(name, instanceId, path);
            if (go == null)
                return new { error = $"GameObject not found: {name ?? path}" };

            var doc = go.GetComponent<UIDocument>();
            if (doc == null)
                return new { error = $"No UIDocument component on '{go.name}'" };

            var root = doc.rootVisualElement;
            if (root == null)
                return new { error = "UIDocument has no rootVisualElement (document may not be active)" };

            var hierarchy = InspectVisualElement(root, 0, depth);
            return new
            {
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                hierarchy
            };
        }

        // ============================ PRIVATE UITK HELPERS ============================

        private static XElement FindXmlElementByName(XElement root, string elementName)
        {
            if (root == null) return null;
            var nameAttr = root.Attribute("name");
            if (nameAttr != null && nameAttr.Value == elementName)
                return root;
            foreach (var child in root.Elements())
            {
                var found = FindXmlElementByName(child, elementName);
                if (found != null) return found;
            }
            return null;
        }

        private static string FormatUssProperties(string properties)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var prop in properties.Split(';'))
            {
                var trimmed = prop.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    sb.AppendLine($"    {trimmed};");
            }
            return sb.ToString().TrimEnd();
        }

        private static object InspectVisualElement(UnityEngine.UIElements.VisualElement element, int currentDepth, int maxDepth)
        {
            var typeName = element.GetType().Name;
            var elName = element.name;
            var classes = element.GetClasses().ToArray();
            var childCount = element.childCount;

            if (currentDepth >= maxDepth && childCount > 0)
            {
                return new
                {
                    type = typeName, name = elName,
                    classes, childCount,
                    note = $"[{childCount} children; truncated at depth {maxDepth}]"
                };
            }

            var children = new System.Collections.Generic.List<object>();
            for (int i = 0; i < element.childCount; i++)
                children.Add(InspectVisualElement(element[i], currentDepth + 1, maxDepth));

            return new { type = typeName, name = elName, classes, children };
        }
    }
}
