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
    /// UI Toolkit 技能——创建/编辑 USS、UXML 文件，并在场景中配置 UIDocument。
    /// 需要 Unity 2022.3+（本包支持的最低版本）。
    /// </summary>
    public static class UIToolkitSkills
    {
        // ============================ 文件操作 ============================

        [UnitySkill("uitk_create_uss", "Create a USS stylesheet file for UI Toolkit",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "uss", "stylesheet", "ui-toolkit", "style" },
            Outputs = new[] { "path", "lines" },
            TracksWorkflow = true,
            MutatesAssets = true)]
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
            TracksWorkflow = true,
            MutatesAssets = true)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("uitk_write_file", "Write or overwrite a USS or UXML file. filePath must end in .uss or .uxml; any other extension is rejected (use script_create for C# files)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "write", "uss", "uxml", "file" },
            Outputs = new[] { "path", "lines" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object UitkWriteFile(string filePath, string content)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(content, "content") is object contentErr) return contentErr;

            // 扩展名是契约，不是形式。本技能声称只写 "USS 或 UXML"，却没人强制，于是它兼职成了无限制文件写入器：
            // 传一个 .cs 路径它就会写出脚本并触发域重载，而它并未声明 MayTriggerReload——
            // 那正是事务性预检与 SemiAuto 闸门用来判断一次调用是否安全的唯一标志。
            // 拒绝发生在创建目录之前，所以不会留下任何残留。
            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".uss", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".uxml", System.StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    error = $"Invalid value '{filePath}' for parameter 'filePath': this skill writes UI Toolkit " +
                            $"markup, so the extension must be .uss or .uxml (got " +
                            $"'{(string.IsNullOrEmpty(extension) ? "no extension" : extension)}'). " +
                            "Use script_create for C# files.",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "filePath",
                    validValues = new[] { ".uss", ".uxml" },
                    relatedSkills = new[] { "script_create" },
                };
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool overwriting = File.Exists(filePath);
            if (overwriting)
            {
                var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
                if (existing != null) WorkflowManager.SnapshotObject(existing);
            }

            File.WriteAllText(filePath, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            // 新建文件记 Created 快照（撤销即删除），使其与覆盖写一样可回滚。
            if (!overwriting)
            {
                var created = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
                if (created != null) WorkflowManager.SnapshotCreatedAsset(created);
            }

            return new { success = true, path = filePath, lines = content.Split('\n').Length };
        }

        [UnitySkill("uitk_delete_file", "Delete a USS or UXML file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Delete,
            Tags = new[] { "delete", "uss", "uxml", "file" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            RiskLevel = "medium")]
        public static object UitkDeleteFile(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath", isDelete: true) is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            if (!WorkflowManager.DeleteAssetToTrash(filePath))
                return new { error = $"Failed to delete file: {filePath}" };
            return new { success = true, deleted = filePath };
        }

        [UnitySkill("uitk_find_files", "Search for USS and/or UXML files in the project (type: uss/uxml/all)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "find", "search", "uss", "uxml" },
            Outputs = new[] { "count", "files" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        // ============================ 场景操作 ============================

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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                sortingOrder = doc.sortingOrder
            };
        }

        [UnitySkill("uitk_create_panel_settings", "Create a PanelSettings asset for UI Toolkit. renderMode/forceGammaRendering/bindingLogLevel/colliderUpdateMode/colliderIsTrigger/vertexBudget need Unity 6 and textureSlotCount needs Unity 6000.3; on an older editor they are rejected by name rather than silently ignored.",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "panel-settings", "asset", "scaling", "resolution" },
            Outputs = new[] { "path", "scaleMode", "referenceResolution", "screenMatchMode" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object UitkCreatePanelSettings(
            string savePath,
            string scaleMode = "ScaleWithScreenSize",
            int referenceResolutionX = 1920,
            int referenceResolutionY = 1080,
            string screenMatchMode = "MatchWidthOrHeight",
            string themeStyleSheetPath = null,
            // 通用属性
            string textSettingsPath = null,
            string targetTexturePath = null,
            int? targetDisplay = null,
            float? sortOrder = null,
            float? scale = null,
            float? match = null,
            float? referenceDpi = null,
            float? fallbackDpi = null,
            float? referenceSpritePixelsPerUnit = null,
            // 动态图集
            int? dynamicAtlasMinSize = null,
            int? dynamicAtlasMaxSize = null,
            int? dynamicAtlasMaxSubTextureSize = null,
            string dynamicAtlasFilters = null,
            // 清屏颜色
            bool? clearColor = null,
            float? colorClearR = null,
            float? colorClearG = null,
            float? colorClearB = null,
            float? colorClearA = null,
            bool? clearDepthStencil = null,
            // Unity 6+ 专属（旧版本按参数名直接拒绝——见 TryResolvePanelSettingsArgs）
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

            // 在资产实例创建之前解析，使被拒的取值不留任何残留。
            if (!SkillParamUtil.TryParseOptionalEnum<PanelScaleMode>(scaleMode, "scaleMode", out var parsedScale, out var scaleModeError))
                return scaleModeError;
            if (!SkillParamUtil.TryParseOptionalEnum<PanelScreenMatchMode>(screenMatchMode, "screenMatchMode", out var parsedMatch, out var screenMatchModeError))
                return screenMatchModeError;

            var settings = ScriptableObject.CreateInstance<PanelSettings>();

            var args = new PanelSettingsArgs
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
            };

            // 该实例存在的唯一目的是让解析器探测其序列化字段；在所有取值确认合法之前不会向它写入任何东西，
            // 一旦被拒就销毁它，而不是留下一个无主 ScriptableObject 让 GC 打日志。
            if (!TryResolvePanelSettingsArgs(settings, args, out var resolved, out var resolveErr))
            {
                Object.DestroyImmediate(settings);
                return resolveErr;
            }

            if (parsedScale.HasValue) settings.scaleMode = parsedScale.Value;

            settings.referenceResolution = new Vector2Int(referenceResolutionX, referenceResolutionY);

            if (parsedMatch.HasValue) settings.screenMatchMode = parsedMatch.Value;

            if (!string.IsNullOrEmpty(themeStyleSheetPath))
            {
                if (Validate.SafePath(themeStyleSheetPath, "themeStyleSheetPath") is object tssErr)
                {
                    Object.DestroyImmediate(settings);
                    return tssErr;
                }
                var tss = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themeStyleSheetPath);
                if (tss != null) settings.themeStyleSheet = tss;
            }

            ApplyPanelSettings(settings, args, resolved);

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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object UitkGetPanelSettings(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(assetPath);
            if (settings == null)
                return new { error = $"PanelSettings not found: {assetPath}" };

            var atlas = settings.dynamicAtlasSettings;
            var cc = settings.colorClearValue;

#if UNITY_6000_0_OR_NEWER
            // renderMode、colliderUpdateMode、colliderIsTrigger 都是 internal，需经 SerializedObject 读取。
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
                // Unity 6+ 属性（renderMode/collider* 经 SerializedObject 读取）
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

        [UnitySkill("uitk_set_panel_settings", "Modify properties on an existing PanelSettings asset. Every value is validated before the first write, so a rejected call leaves the asset untouched. renderMode/forceGammaRendering/bindingLogLevel/colliderUpdateMode/colliderIsTrigger/vertexBudget need Unity 6 and textureSlotCount needs Unity 6000.3; on an older editor they are rejected by name rather than silently ignored.",
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

            // 调用中所有取值型参数都在 Undo.RecordObject 之前解析——此处两个枚举，以及 args 块内经
            // TryResolvePanelSettingsArgs 处理的四个。无论哪个非法，资产都不会被碰：
            // bindingLogLevel 过去是在 apply 流程深处才解析的，非法值返回错误时已经写进去十几个字段了。
            if (!SkillParamUtil.TryParseOptionalEnum<PanelScaleMode>(scaleMode, "scaleMode", out var parsedScale, out var scaleModeError))
                return scaleModeError;
            if (!SkillParamUtil.TryParseOptionalEnum<PanelScreenMatchMode>(screenMatchMode, "screenMatchMode", out var parsedMatch, out var screenMatchModeError))
                return screenMatchModeError;

            var args = new PanelSettingsArgs
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
            };
            if (!TryResolvePanelSettingsArgs(settings, args, out var resolved, out var resolveErr))
                return resolveErr;

            // themeStyleSheetPath 是最后一个可能失败的点，且它在任何写入之前就失败。
            ThemeStyleSheet themeStyleSheet = null;
            if (!string.IsNullOrEmpty(themeStyleSheetPath))
            {
                if (Validate.SafePath(themeStyleSheetPath, "themeStyleSheetPath") is object tssErr) return tssErr;
                themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(themeStyleSheetPath);
                if (themeStyleSheet == null) return new { error = $"ThemeStyleSheet not found: {themeStyleSheetPath}" };
            }

            Undo.RecordObject(settings, "Set PanelSettings");

            if (parsedScale.HasValue) settings.scaleMode = parsedScale.Value;

            if (referenceResolutionX.HasValue || referenceResolutionY.HasValue)
            {
                var cur = settings.referenceResolution;
                settings.referenceResolution = new Vector2Int(
                    referenceResolutionX ?? cur.x,
                    referenceResolutionY ?? cur.y);
            }

            if (parsedMatch.HasValue) settings.screenMatchMode = parsedMatch.Value;

            if (themeStyleSheet != null) settings.themeStyleSheet = themeStyleSheet;

            ApplyPanelSettings(settings, args, resolved);

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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object UitkListDocuments()
        {
            var docs = FindHelper.FindAll<UIDocument>();
            var result = docs.Select(doc => new
            {
                name = doc.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(doc.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(doc.gameObject),
                visualTreeAsset = doc.visualTreeAsset != null ? AssetDatabase.GetAssetPath(doc.visualTreeAsset) : null,
                panelSettings = doc.panelSettings != null ? AssetDatabase.GetAssetPath(doc.panelSettings) : null,
                sortingOrder = doc.sortingOrder,
                active = doc.gameObject.activeInHierarchy
            }).ToArray();

            return new { count = result.Length, documents = result };
        }

        // ============================ 检视 ============================

        [UnitySkill("uitk_inspect_uxml", "Parse and display UXML element hierarchy (depth controls max traversal depth)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "uxml", "hierarchy", "parse" },
            Outputs = new[] { "path", "hierarchy" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        // ============================ 模板 ============================

        [UnitySkill("uitk_create_from_template", "Create a UXML+USS file pair from a template (menu/hud/dialog/settings/inventory/list/tab-view/toolbar/card/notification)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "template", "uxml", "uss", "scaffold" },
            Outputs = new[] { "template", "ussPath", "uxmlPath", "name" },
            TracksWorkflow = true,
            MutatesAssets = true)]
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

        // ============================ 批量 ============================

        [UnitySkill("uitk_create_batch", "Batch create USS/UXML files. items: JSON array of {type,savePath,content?,ussPath?}",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "batch", "uss", "uxml", "bulk" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, MutatesAssets = true)]
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

        // ============================ PanelSettings 辅助 ============================

        // PanelSettings.renderMode 与 .colliderUpdateMode 是 internal，只能经 SerializedObject 按数值写入。
        // 下面这些镜像了序列化顺序，其存在意义是让可接受词表成为一个真枚举：
        // 这样 SkillParamUtil 才能拒绝非法值并列出候选，而不是像旧的 string.Equals 链那样一路落空成空转。
        private enum PanelRenderModeOption
        {
            ScreenSpaceOverlay = 0,
            WorldSpace = 1,
        }

        private enum ColliderUpdateModeOption
        {
            Match3DBoundingBox = 0,
            Match2DDocumentRect = 1,
            KeepExistingCollider = 2,
        }

        /// <summary>
        /// 逗号分隔的 flag 列表，另加枚举本身没有声明的 "Everything" 别名。每一段都必须解析成功：
        /// 旧版本会把能解析的部分 OR 进去、其余丢掉，于是列表里一个拼错的 flag 会悄悄产出
        /// 比调用方所要求更窄的过滤集。
        ///
        /// <para>此处空白表示"未提供"，而这恰是
        /// <see cref="SkillParamUtil.TryParseFlagsParam{TEnum}"/> 唯一表达不了的语义（它把空白当成缺失的必填参数）；
        /// 该检查之后的部分仍是共享实现，所以这里同样享有那道把 "999" 挡在 activeFilters 之外的掩码校验。</para>
        /// </summary>
        private static bool TryParseDynamicAtlasFilters(string filters, out DynamicAtlasFilters result, out object error)
        {
            result = DynamicAtlasFilters.None;
            error = null;
            if (string.IsNullOrWhiteSpace(filters)) return true;

            return SkillParamUtil.TryParseFlagsParam<DynamicAtlasFilters>(
                filters, "dynamicAtlasFilters", out result, out error);
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
        /// <see cref="ApplyPanelSettings"/> 所需的、必须先行校验的全部内容，
        /// 以保证 <see cref="PanelSettingsArgs"/> 里没有任何字段是在写入落地之后才解析的。
        /// </summary>
        private struct PanelSettingsResolved
        {
            public DynamicAtlasFilters atlasFilters;
            public PanelRenderModeOption? renderMode;
            public ColliderUpdateModeOption? colliderUpdateMode;
            public PanelTextSettings textSettings;
            public RenderTexture targetTexture;
#if UNITY_6000_0_OR_NEWER
            public UnityEngine.UIElements.BindingLogLevel? bindingLogLevel;
#endif
        }

        /// <summary>
        /// 在调用方写入任何东西之前，解析所有取值型参数，并拒绝当前 Unity 版本无法兑现的那些。
        ///
        /// <para>这个拆分修的是一次真实存在的半写入。bindingLogLevel 过去在 apply 流程约四分之三处才解析，
        /// 意味着非法值要等到 scaleMode、referenceResolution、screenMatchMode、themeStyleSheet
        /// 以及十几个数值属性都已提交进资产之后才报出来——调用方拿到一个错误和一份改了一半的 PanelSettings。
        /// 那两处 "no serialized field" 检查上移也是同样的理由。</para>
        ///
        /// <para><paramref name="settings"/> 只会被读取（经 SerializedObject），用于确认 internal 字段存在；
        /// 请在写入它之前把实例传进来。</para>
        /// </summary>
        private static bool TryResolvePanelSettingsArgs(PanelSettings settings, in PanelSettingsArgs a,
            out PanelSettingsResolved resolved, out object error)
        {
            resolved = default(PanelSettingsResolved);
            error = null;

            // --- 版本闸门。这些参数在所有 Unity 版本的签名里都有，但它们背后的属性并非都有，
            // 于是在旧编辑器上取值曾被接受后丢弃——正是枚举拒绝机制要防的那种静默丢值。 ---
#if !UNITY_6000_0_OR_NEWER
            var needsUnity6 =
                !string.IsNullOrEmpty(a.renderMode) ? "renderMode" :
                a.forceGammaRendering.HasValue ? "forceGammaRendering" :
                !string.IsNullOrEmpty(a.bindingLogLevel) ? "bindingLogLevel" :
                !string.IsNullOrEmpty(a.colliderUpdateMode) ? "colliderUpdateMode" :
                a.colliderIsTrigger.HasValue ? "colliderIsTrigger" :
                a.vertexBudget.HasValue ? "vertexBudget" :
                a.textureSlotCount.HasValue ? "textureSlotCount" : null;
            if (needsUnity6 != null)
            {
                error = UnsupportedInThisUnityVersion(needsUnity6, "Unity 6.0");
                return false;
            }
#elif !UNITY_6000_3_OR_NEWER
            if (a.textureSlotCount.HasValue)
            {
                error = UnsupportedInThisUnityVersion("textureSlotCount", "Unity 6000.3");
                return false;
            }
#endif

            // --- 过去会被静默丢弃的取值型参数 ---
            if (!TryParseDynamicAtlasFilters(a.dynamicAtlasFilters, out resolved.atlasFilters, out error))
                return false;
            if (!SkillParamUtil.TryParseOptionalEnum<PanelRenderModeOption>(a.renderMode, "renderMode", out resolved.renderMode, out error))
                return false;
            if (!SkillParamUtil.TryParseOptionalEnum<ColliderUpdateModeOption>(a.colliderUpdateMode, "colliderUpdateMode", out resolved.colliderUpdateMode, out error))
                return false;

            // --- 资产引用。在此加载而不放到 apply 流程里，使路径拼写错误同样无法落在写入中途。 ---
            if (!string.IsNullOrEmpty(a.textSettingsPath))
            {
                if (Validate.SafePath(a.textSettingsPath, "textSettingsPath") is object tsErr)
                {
                    error = tsErr;
                    return false;
                }
                resolved.textSettings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(a.textSettingsPath);
                if (resolved.textSettings == null)
                {
                    error = new { error = $"PanelTextSettings not found: {a.textSettingsPath}" };
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(a.targetTexturePath))
            {
                if (Validate.SafePath(a.targetTexturePath, "targetTexturePath") is object ttErr)
                {
                    error = ttErr;
                    return false;
                }
                resolved.targetTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(a.targetTexturePath);
                if (resolved.targetTexture == null)
                {
                    error = new { error = $"RenderTexture not found: {a.targetTexturePath}" };
                    return false;
                }
            }

#if UNITY_6000_0_OR_NEWER
            if (!SkillParamUtil.TryParseOptionalEnum<UnityEngine.UIElements.BindingLogLevel>(
                    a.bindingLogLevel, "bindingLogLevel", out resolved.bindingLogLevel, out error))
                return false;

            // renderMode 与 colliderUpdateMode 是 internal 属性，只能经其序列化字段访问。
            // 这些字段是否存在取决于 Unity 版本而非取值本身，所以在此处判定，不留到写入中途。
            if (resolved.renderMode.HasValue || resolved.colliderUpdateMode.HasValue)
            {
                var probe = new SerializedObject(settings);
                if (resolved.renderMode.HasValue && probe.FindProperty("m_RenderMode") == null)
                {
                    error = new { error = "Cannot set renderMode: PanelSettings has no serialized m_RenderMode field in this Unity version." };
                    return false;
                }
                if (resolved.colliderUpdateMode.HasValue && probe.FindProperty("m_ColliderUpdateMode") == null)
                {
                    error = new { error = "Cannot set colliderUpdateMode: PanelSettings has no serialized m_ColliderUpdateMode field in this Unity version." };
                    return false;
                }
            }
#endif

            return true;
        }

        /// <summary>
        /// 调用方传了一个当前编辑器没有对应属性的参数。以 SEMANTIC_INVALID 报出并点名该参数，
        /// 使修法明确为"去掉这一个参数"，而不是让人去猜。
        /// </summary>
        private static object UnsupportedInThisUnityVersion(string paramName, string minimumVersion)
        {
            return new
            {
                error = $"Invalid value for parameter '{paramName}': it requires {minimumVersion} or newer, " +
                        $"and this editor is {Application.unityVersion}. Omit the parameter.",
                errorCode = SkillParamUtil.SemanticInvalidCode,
                parameter = paramName,
            };
        }

        /// <summary>
        /// PanelSettings 扩展属性的共用写入器（create 与 set 共用）。它写下的每个值都已由
        /// <see cref="TryResolvePanelSettingsArgs"/> 校验过，而调用方必须在首次写入之前跑那个校验——
        /// 这既是本方法不会失败的原因，也是本技能再不可能"报 error 而资产已改一半"的原因。
        /// </summary>
        private static void ApplyPanelSettings(PanelSettings settings, in PanelSettingsArgs a, in PanelSettingsResolved r)
        {
            // --- 资产引用 ---
            if (r.textSettings != null)  settings.textSettings = r.textSettings;
            if (r.targetTexture != null) settings.targetTexture = r.targetTexture;

            // --- 数值属性 ---
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

            // --- 动态图集设置（结构体：读 -> 改 -> 写回） ---
            if (a.dynamicAtlasMinSize.HasValue || a.dynamicAtlasMaxSize.HasValue ||
                a.dynamicAtlasMaxSubTextureSize.HasValue || !string.IsNullOrEmpty(a.dynamicAtlasFilters))
            {
                var atlas = settings.dynamicAtlasSettings;
                if (a.dynamicAtlasMinSize.HasValue)        atlas.minAtlasSize = a.dynamicAtlasMinSize.Value;
                if (a.dynamicAtlasMaxSize.HasValue)        atlas.maxAtlasSize = a.dynamicAtlasMaxSize.Value;
                if (a.dynamicAtlasMaxSubTextureSize.HasValue) atlas.maxSubTextureSize = a.dynamicAtlasMaxSubTextureSize.Value;
                if (!string.IsNullOrEmpty(a.dynamicAtlasFilters)) atlas.activeFilters = r.atlasFilters;
                settings.dynamicAtlasSettings = atlas;
            }

            // --- 清屏颜色 ---
            if (a.clearColor.HasValue)        settings.clearColor = a.clearColor.Value;
            if (a.clearDepthStencil.HasValue) settings.clearDepthStencil = a.clearDepthStencil.Value;

            if (a.colorClearR.HasValue || a.colorClearG.HasValue || a.colorClearB.HasValue || a.colorClearA.HasValue)
            {
                var c = settings.colorClearValue;
                settings.colorClearValue = new Color(
                    a.colorClearR ?? c.r, a.colorClearG ?? c.g, a.colorClearB ?? c.b, a.colorClearA ?? c.a);
            }

            // --- Unity 6+ 专属属性 ---
#if UNITY_6000_0_OR_NEWER
            if (a.forceGammaRendering.HasValue) settings.forceGammaRendering = a.forceGammaRendering.Value;
            if (r.bindingLogLevel.HasValue) settings.bindingLogLevel = r.bindingLogLevel.Value;
            if (a.vertexBudget.HasValue)     settings.vertexBudget = (uint)a.vertexBudget.Value;
#if UNITY_6000_3_OR_NEWER
            if (a.textureSlotCount.HasValue) settings.textureSlotCount = (TextureSlotCount)a.textureSlotCount.Value;
#endif

            // renderMode、colliderUpdateMode、colliderIsTrigger 都是 internal，需经 SerializedObject 更新。
            if (!string.IsNullOrEmpty(a.renderMode) || !string.IsNullOrEmpty(a.colliderUpdateMode) || a.colliderIsTrigger.HasValue)
            {
                var so = new SerializedObject(settings);
                if (r.renderMode.HasValue)
                    so.FindProperty("m_RenderMode").intValue = (int)r.renderMode.Value;
                if (r.colliderUpdateMode.HasValue)
                    so.FindProperty("m_ColliderUpdateMode").intValue = (int)r.colliderUpdateMode.Value;
                if (a.colliderIsTrigger.HasValue)
                {
                    var prop = so.FindProperty("m_ColliderIsTrigger");
                    if (prop != null) prop.boolValue = a.colliderIsTrigger.Value;
                }
                so.ApplyModifiedProperties();
            }
#endif
        }

        // ============================ 私有辅助 ============================

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

        // 默认模板
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

        // --- 主菜单 ---
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

        // --- 对话框 ---
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

        // --- 设置页 ---
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

        // --- 背包 ---
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

        // --- 列表 ---
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

        // --- 标签页 ---
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

        // --- 工具栏 ---
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

        // --- 卡片 ---
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

        // --- 通知 ---
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

        // ============================ UXML 元素操作 ============================

        private static readonly XNamespace EngineNs = "UnityEngine.UIElements";

        [UnitySkill("uitk_add_element", "Add an element to a UXML file (Label/Button/Toggle/Slider/TextField/VisualElement/etc.)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "add", "uxml", "element", "visual-element" },
            Outputs = new[] { "path", "elementType", "elementName", "parentName" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
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
            TracksWorkflow = true,
            RiskLevel = "medium",
            MutatesAssets = true)]
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
            TracksWorkflow = true,
            MutatesAssets = true)]
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
            TracksWorkflow = true,
            MutatesAssets = true)]
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

        // ============================ USS 操作 ============================

        [UnitySkill("uitk_add_uss_rule", "Add or update a USS rule in a stylesheet file",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "uss", "rule", "selector", "style" },
            Outputs = new[] { "path", "selector", "action" },
            RequiresInput = new[] { "filePath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
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

            // 先找现有规则，找到则替换
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
            TracksWorkflow = true,
            RiskLevel = "medium",
            MutatesAssets = true)]
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

            // 同时统计 var() 的引用处
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

        // ============================ 代码生成 ============================

        [UnitySkill("uitk_create_editor_window", "Generate an EditorWindow C# script with UI Toolkit (CreateGUI + UXML/USS binding)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "editor-window", "codegen", "script", "ui-toolkit" },
            Outputs = new[] { "path", "className", "windowTitle", "menuPath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            MayTriggerReload = true)]
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

        [UnitySkill("uitk_create_runtime_ui", "Generate a runtime MonoBehaviour script for UI Toolkit (UIDocument query & binding). elementQueries: comma-separated \"Type:elementName\" pairs, e.g. \"Button:my-button,Label:score-label\" - the type comes first, since it becomes the generated field's C# type",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "runtime", "codegen", "monobehaviour", "ui-document" },
            Outputs = new[] { "path", "className" },
            TracksWorkflow = true,
            MutatesAssets = true,
            MayTriggerReload = true)]
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

            // 解析元素查询串："Button:my-button,Label:score-label"（格式为 Type:elementName——
            // 类型在前，因为它会成为生成字段的 C# 类型）。
            var queryLines = new System.Text.StringBuilder();
            var fields = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(elementQueries))
            {
                foreach (var q in elementQueries.Split(','))
                {
                    var token = q.Trim();
                    if (token.Length == 0) continue;

                    var parts = token.Split(':');
                    if (parts.Length != 2)
                    {
                        // 此处过去只是 "continue"，把畸形记号从生成脚本里静默丢掉，且毫无出错提示。
                        return new
                        {
                            error = $"Invalid elementQueries entry '{token}'. Each entry must be \"Type:elementName\" (e.g. \"Button:my-button\").",
                            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                            parameter = "elementQueries"
                        };
                    }

                    var elType = parts[0].Trim();
                    var elName = parts[1].Trim();

                    // 生成的脚本只 import UnityEngine 与 UnityEngine.UIElements，所以 elType 必须能在那里
                    // 解析成真实的 VisualElement 派生类型——否则这里会静默产出编译失败（CS0246
                    // "找不到类型或命名空间"）的 C#，错误出现在生成文件里，追不回引发它的那次
                    // uitk_create_runtime_ui 调用（例如把类型与名字写反成 "my-button:Button"，
                    // elType 就成了 "my-button" 这个根本不存在的类型）。
                    if (ResolveVisualElementType(elType) == null)
                    {
                        return new
                        {
                            error = $"Unknown UI Toolkit element type '{elType}' in elementQueries entry '{token}'. Expected a VisualElement-derived type from UnityEngine.UIElements, e.g. Button, Label, TextField, Toggle, Slider, ScrollView, ListView, DropdownField.",
                            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                            parameter = "elementQueries",
                            validValues = new[] { "Button", "Label", "TextField", "Toggle", "Slider", "ScrollView", "ListView", "DropdownField", "Foldout", "VisualElement", "Image" }
                        };
                    }

                    var fieldName = "m_" + elName.Replace("-", "").Replace("_", "");
                    if (!ValidIdentifierPattern.IsMatch(fieldName))
                    {
                        return new
                        {
                            error = $"elementQueries entry '{token}' produces an invalid C# field name ('{fieldName}'). elementName must start with a letter or underscore and contain only letters, digits, '-' or '_'.",
                            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                            parameter = "elementQueries"
                        };
                    }

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

        // ============================ 场景检视 ============================

        [UnitySkill("uitk_inspect_document", "Inspect the live VisualElement hierarchy of a UIDocument in the scene",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "inspect", "ui-document", "hierarchy", "visual-element" },
            Outputs = new[] { "gameObject", "instanceId", "hierarchy" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                hierarchy
            };
        }

        // ============================ 运行时数据绑定（Unity 6000.0+） ============================

        [UnitySkill("uitk_runtime_binding_add", "Add or update a runtime data binding on a UXML element (property + bindingMode; optional dataSource/dataSourcePath). Requires Unity 6000.0+",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "binding", "data-binding", "uxml", "runtime", "unity6" },
            Outputs = new[] { "path", "elementName", "property", "bindingMode", "action" },
            RequiresInput = new[] { "filePath", "elementName", "property" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RiskLevel = "medium")]
        public static object UitkRuntimeBindingAdd(
            string filePath,
            string elementName,
            string property,
            string bindingMode = null,
            string dataSource = null,
            string dataSourcePath = null,
            string extraAttributes = null)
        {
#if !UNITY_6000_0_OR_NEWER
            return RequiresUnity("6000.0", "UI Toolkit runtime data binding (<Bindings> in UXML)",
                new[] { "uitk_modify_element" });
#else
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (Validate.Required(elementName, "elementName") is object nameErr) return nameErr;
            if (Validate.Required(property, "property") is object propErr) return propErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };
            if (!filePath.EndsWith(".uxml", System.StringComparison.OrdinalIgnoreCase))
                return new { error = $"Runtime bindings can only be written into .uxml files, got: {filePath}" };

            // BindingMode 取值在此校验，因为未知的 binding-mode 会导致整个 UXML 资产导入失败。
            var validModes = new[] { "TwoWay", "ToSource", "ToTarget", "ToTargetOnce" };
            string mode = null;
            if (!string.IsNullOrEmpty(bindingMode))
            {
                mode = validModes.FirstOrDefault(m => m.Equals(bindingMode.Trim(), System.StringComparison.OrdinalIgnoreCase));
                if (mode == null)
                    return new { error = $"Invalid bindingMode '{bindingMode}'. Valid values: {string.Join(", ", validModes)}" };
            }

            var extras = ParseAttributeJson(extraAttributes, out var extraErr);
            if (extraErr != null) return new { error = extraErr };

            var existingAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (existingAsset != null) WorkflowManager.SnapshotObject(existingAsset);

            XDocument xdoc;
            try
            {
                xdoc = XDocument.Parse(File.ReadAllText(filePath, System.Text.Encoding.UTF8));
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse UXML: {ex.Message}" };
            }

            var target = FindXmlElementByName(xdoc.Root, elementName);
            if (target == null)
                return new { error = $"Element with name '{elementName}' not found in {filePath}" };

            if (dataSource != null) target.SetAttributeValue("data-source", dataSource);
            if (dataSourcePath != null) target.SetAttributeValue("data-source-path", dataSourcePath);

            // <Bindings> 书写时不带命名空间前缀；导入器按 local name 匹配。
            var bindings = target.Elements().FirstOrDefault(e => e.Name.LocalName == "Bindings");
            if (bindings == null)
            {
                bindings = new XElement("Bindings");
                target.AddFirst(bindings);
            }

            var binding = bindings.Elements().FirstOrDefault(e =>
                e.Name.LocalName == "DataBinding" &&
                string.Equals((string)e.Attribute("property"), property, System.StringComparison.Ordinal));

            bool existed = binding != null;
            if (!existed)
            {
                binding = new XElement(EngineNs + "DataBinding");
                bindings.Add(binding);
            }

            binding.SetAttributeValue("property", property);
            if (mode != null) binding.SetAttributeValue("binding-mode", mode);
            foreach (var kv in extras)
                binding.SetAttributeValue(kv.Key, kv.Value);

            File.WriteAllText(filePath, xdoc.ToString(), SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(filePath);

            return new
            {
                success = true,
                path = filePath,
                elementName,
                property,
                bindingMode = mode,
                dataSource,
                dataSourcePath,
                action = existed ? "updated" : "added"
            };
#endif
        }

        [UnitySkill("uitk_runtime_binding_list", "List runtime data bindings declared in a UXML file (<Bindings> blocks plus data-source / data-source-path attributes)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "binding", "data-binding", "uxml", "inspect" },
            Outputs = new[] { "path", "count", "elements" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object UitkRuntimeBindingList(string filePath)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            XDocument xdoc;
            try
            {
                xdoc = XDocument.Parse(File.ReadAllText(filePath, System.Text.Encoding.UTF8));
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse UXML: {ex.Message}" };
            }

            var elements = new System.Collections.Generic.List<object>();
            if (xdoc.Root != null)
            {
                foreach (var el in xdoc.Root.DescendantsAndSelf())
                {
                    if (el.Name.LocalName == "Bindings") continue;

                    var dataSource = (string)el.Attribute("data-source");
                    var dataSourcePath = (string)el.Attribute("data-source-path");
                    var bindingsNode = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Bindings");
                    if (dataSource == null && dataSourcePath == null && bindingsNode == null) continue;

                    var bindings = bindingsNode == null
                        ? new object[0]
                        : bindingsNode.Elements().Select(b => (object)new
                        {
                            bindingType = b.Name.LocalName,
                            property = (string)b.Attribute("property"),
                            bindingMode = (string)b.Attribute("binding-mode"),
                            attributes = b.Attributes()
                                .Where(a => !a.IsNamespaceDeclaration)
                                .ToDictionary(a => a.Name.LocalName, a => a.Value)
                        }).ToArray();

                    elements.Add(new
                    {
                        elementType = el.Name.LocalName,
                        elementName = (string)el.Attribute("name"),
                        dataSource,
                        dataSourcePath,
                        bindingCount = bindings.Length,
                        bindings
                    });
                }
            }

            return new { path = filePath, count = elements.Count, elements };
        }

        // ============================ UXML 升级（Unity 6000.3+） ============================

        [UnitySkill("uitk_uxml_upgrade", "Run registered UXML upgraders over .uxml assets (filePath or folder; listOnly reports available upgraders without writing). Needs an editor that ships UxmlUpgradeService (Unity 6000.3+)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Modify,
            Tags = new[] { "uxml", "upgrade", "migration", "unity6" },
            Outputs = new[] { "upgraders", "assets", "changedCount", "listOnly" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RiskLevel = "medium")]
        public static object UitkUxmlUpgrade(
            string filePath = null,
            string folder = null,
            string upgraderNames = null,
            bool listOnly = false,
            int limit = 200)
        {
            // UxmlUpgradeService 文档上从 6000.3 起提供，但部分 6000.3 构建里并不存在，
            // 所以这个 API 走反射绑定而非编译期引用。
            var serviceType = FindEditorUiType("UnityEditor.UIElements.UxmlUpgradeService");
            var upgraderType = FindEditorUiType("UnityEditor.UIElements.IUxmlUpgrader");
            if (serviceType == null || upgraderType == null)
                return UxmlUpgradeUnavailable(
                    "UnityEditor.UIElements.UxmlUpgradeService is not present. The API is documented for Unity 6000.3+ but only ships in some builds of it.");

            var assetListType = typeof(System.Collections.Generic.List<VisualTreeAsset>);
            var upgraderListType = typeof(System.Collections.Generic.List<>).MakeGenericType(upgraderType);
            var upgradersProp = serviceType.GetProperty("upgraders");
            var applyAllMethod = serviceType.GetMethod("ApplyUpgrades", new[] { assetListType });
            var applySelectedMethod = serviceType.GetMethod("ApplyUpgrades", new[] { assetListType, upgraderListType });
            var getByNameMethod = serviceType.GetMethod("GetUpgraderByName", new[] { typeof(string) });
            var isEnabledMethod = serviceType.GetMethod("IsUpgraderEnabled", new[] { upgraderType });
            if (upgradersProp == null || applyAllMethod == null)
                return UxmlUpgradeUnavailable(
                    "UxmlUpgradeService exists but lacks the expected 'upgraders' property or ApplyUpgrades(List<VisualTreeAsset>) method.");

            var paths = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrEmpty(filePath))
            {
                if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
                if (!File.Exists(filePath))
                    return new { error = $"File not found: {filePath}" };
                paths.Add(filePath);
            }
            else if (!string.IsNullOrEmpty(folder))
            {
                if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
                foreach (var guid in AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { folder }))
                {
                    if (paths.Count >= limit) break;
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.EndsWith(".uxml", System.StringComparison.OrdinalIgnoreCase))
                        paths.Add(p);
                }
                paths.Sort();
            }
            else if (!listOnly)
            {
                return new { error = "Provide filePath or folder (or set listOnly=true to inspect available upgraders)" };
            }

            object service;
            try
            {
                service = System.Activator.CreateInstance(serviceType);
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to create UxmlUpgradeService: {(ex.InnerException ?? ex).Message}" };
            }

            var nameProp = upgraderType.GetProperty("name");
            var descProp = upgraderType.GetProperty("description");

            var registered = new System.Collections.Generic.List<object>();
            if (upgradersProp.GetValue(service) is System.Collections.IEnumerable sequence)
                foreach (var u in sequence)
                    if (u != null) registered.Add(u);

            var enabledNames = new System.Collections.Generic.List<string>();
            var upgraderInfo = new System.Collections.Generic.List<object>();
            foreach (var u in registered)
            {
                var upgraderName = ReadUpgraderString(nameProp, u) ?? u.GetType().Name;
                // 没有 IsUpgraderEnabled 的构建报不出这个标志；把这类 upgrader 视为已启用——
                // 反正 ApplyUpgrades 对它们就是这么处理的。
                bool enabled = isEnabledMethod == null
                    || (isEnabledMethod.Invoke(service, new[] { u }) is bool flag && flag);
                if (enabled) enabledNames.Add(upgraderName);
                upgraderInfo.Add(new { name = upgraderName, description = ReadUpgraderString(descProp, u), enabled });
            }

            if (listOnly)
                return new { listOnly = true, upgraders = upgraderInfo.ToArray(), candidateAssets = paths.ToArray(), count = paths.Count };

            System.Collections.IList selected = null;
            var selectedNames = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(upgraderNames))
            {
                if (getByNameMethod == null || applySelectedMethod == null)
                    return UxmlUpgradeUnavailable(
                        "This editor's UxmlUpgradeService cannot run a named subset of upgraders; omit upgraderNames to run every enabled upgrader.");

                selected = (System.Collections.IList)System.Activator.CreateInstance(upgraderListType);
                foreach (var raw in upgraderNames.Split(','))
                {
                    var wanted = raw.Trim();
                    if (wanted.Length == 0) continue;
                    var found = getByNameMethod.Invoke(service, new object[] { wanted });
                    if (found == null)
                        return new
                        {
                            error = $"Upgrader '{wanted}' not found",
                            availableUpgraders = registered.Select(u => ReadUpgraderString(nameProp, u)).ToArray()
                        };
                    selected.Add(found);
                    selectedNames.Add(ReadUpgraderString(nameProp, found) ?? wanted);
                }
                if (selected.Count == 0)
                    return new { error = "upgraderNames contained no usable names" };
            }

            var assets = new System.Collections.Generic.List<VisualTreeAsset>();
            var before = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var p in paths)
            {
                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(p);
                if (vta == null)
                    return new { error = $"VisualTreeAsset not found (is it a valid .uxml?): {p}" };
                WorkflowManager.SnapshotObject(vta);
                before[p] = File.ReadAllText(p, System.Text.Encoding.UTF8);
                assets.Add(vta);
            }

            if (assets.Count == 0)
                return new { error = "No .uxml assets matched the given filePath/folder" };

            try
            {
                if (selected != null) applySelectedMethod.Invoke(service, new object[] { assets, selected });
                else applyAllMethod.Invoke(service, new object[] { assets });
            }
            catch (System.Exception ex)
            {
                return new { error = $"UXML upgrade failed: {(ex.InnerException ?? ex).Message}" };
            }

            AssetDatabase.SaveAssets();

            // 逐文件上报 upgrader 是否真的改写了 .uxml 源文件，而不是假定它改了。
            int changedCount = 0;
            var results = new System.Collections.Generic.List<object>();
            foreach (var p in paths)
            {
                AssetDatabase.ImportAsset(p);
                var after = File.Exists(p) ? File.ReadAllText(p, System.Text.Encoding.UTF8) : null;
                bool changed = after != null && !string.Equals(after, before[p], System.StringComparison.Ordinal);
                if (changed) changedCount++;
                results.Add(new { path = p, changed });
            }

            return new
            {
                success = true,
                listOnly = false,
                upgradersRun = selected != null ? selectedNames.ToArray() : enabledNames.ToArray(),
                upgraders = upgraderInfo.ToArray(),
                assets = results,
                changedCount
            };
        }

        // ============================ 世界空间面板（Unity 6000.2+） ============================

        [UnitySkill("uitk_worldspace_panel_create", "Create a world-space UI Toolkit panel GameObject (PanelRenderer on Unity 6000.5+, world-space UIDocument on 6000.2-6000.4). Requires Unity 6000.2+",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Create,
            Tags = new[] { "world-space", "panel-renderer", "ui-document", "3d", "unity6" },
            Outputs = new[] { "name", "instanceId", "component", "worldSpaceSizeMode", "worldSpaceSize" },
            TracksWorkflow = true,
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object UitkWorldspacePanelCreate(
            string name = "WorldSpaceUI",
            string uxmlPath = null,
            string panelSettingsPath = null,
            string sizeMode = null,
            float? worldSpaceSizeX = null,
            float? worldSpaceSizeY = null,
            string pivot = null,
            string pivotReferenceSize = null,
            bool setPanelRenderMode = true,
            string parentName = null,
            int parentInstanceId = 0,
            string parentPath = null)
        {
#if !UNITY_6000_2_OR_NEWER
            return RequiresUnity("6000.2", "world-space UI Toolkit panels", new[] { "uitk_create_document" });
#else
            VisualTreeAsset vta = null;
            if (!string.IsNullOrEmpty(uxmlPath))
            {
                if (Validate.SafePath(uxmlPath, "uxmlPath") is object uxmlErr) return uxmlErr;
                vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (vta == null) return new { error = $"VisualTreeAsset not found: {uxmlPath}" };
            }

            PanelSettings ps = null;
            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                if (Validate.SafePath(panelSettingsPath, "panelSettingsPath") is object psErr) return psErr;
                ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (ps == null) return new { error = $"PanelSettings not found: {panelSettingsPath}" };
            }

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentName) || parentInstanceId != 0 || !string.IsNullOrEmpty(parentPath))
            {
                parent = GameObjectFinder.Find(parentName, parentInstanceId, parentPath);
                if (parent == null)
                    return new { error = $"Parent not found: {parentName ?? parentPath}" };
            }

            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent.transform, false);

#if UNITY_6000_5_OR_NEWER
            Component panel = go.AddComponent<PanelRenderer>();
            const string componentUsed = "PanelRenderer";
#else
            Component panel = go.AddComponent<UIDocument>();
            const string componentUsed = "UIDocument";
#endif
            if (panel == null)
            {
                UnityEngine.Object.DestroyImmediate(go);
                return new { error = $"Failed to add {componentUsed} component to '{name}'" };
            }

            // PanelRenderer 与 UIDocument 上的属性名相同，但枚举类型不同
            // （UIDocument.WorldSpaceSizeMode 在 6000.2-6000.4 是嵌套类型，6000.5 变成顶层类型），
            // 所以每次枚举/资产赋值都走反射以保持版本无关。
            if (vta != null) TrySetPanelProperty(panel, "visualTreeAsset", vta);
            if (ps != null) TrySetPanelProperty(panel, "panelSettings", ps);

            foreach (var pair in new[]
                     {
                         new[] { "worldSpaceSizeMode", sizeMode },
                         new[] { "pivot", pivot },
                         new[] { "pivotReferenceSize", pivotReferenceSize }
                     })
            {
                var enumErr = TrySetPanelEnum(panel, pair[0], pair[1]);
                if (enumErr != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                    return new { error = enumErr };
                }
            }

            if (worldSpaceSizeX.HasValue || worldSpaceSizeY.HasValue)
            {
                var current = TryGetPanelProperty(panel, "worldSpaceSize") is Vector2 v ? v : Vector2.zero;
                TrySetPanelProperty(panel, "worldSpaceSize",
                    new Vector2(worldSpaceSizeX ?? current.x, worldSpaceSizeY ?? current.y));
            }

            bool renderModeChanged = false;
            if (setPanelRenderMode && ps != null)
                renderModeChanged = TrySetWorldSpaceRenderMode(ps);

            Undo.RegisterCreatedObjectUndo(go, "Create World-Space UI Panel");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            var appliedSize = TryGetPanelProperty(panel, "worldSpaceSize") is Vector2 sz ? sz : Vector2.zero;
            return new
            {
                success = true,
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                component = componentUsed,
                visualTreeAsset = vta != null ? AssetDatabase.GetAssetPath(vta) : null,
                panelSettings = ps != null ? AssetDatabase.GetAssetPath(ps) : null,
                worldSpaceSizeMode = TryGetPanelProperty(panel, "worldSpaceSizeMode")?.ToString(),
                worldSpaceSize = new { x = appliedSize.x, y = appliedSize.y },
                pivot = TryGetPanelProperty(panel, "pivot")?.ToString(),
                pivotReferenceSize = TryGetPanelProperty(panel, "pivotReferenceSize")?.ToString(),
                panelRenderModeSetToWorldSpace = renderModeChanged
            };
#endif
        }

        [UnitySkill("uitk_worldspace_panel_get", "Read the world-space UI panel settings on a scene GameObject (PanelRenderer on Unity 6000.5+, UIDocument on 6000.2-6000.4)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "world-space", "panel-renderer", "ui-document", "inspect" },
            Outputs = new[] { "gameObject", "component", "worldSpaceSizeMode", "worldSpaceSize", "panelSettings" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object UitkWorldspacePanelGet(string name = null, int instanceId = 0, string path = null)
        {
#if !UNITY_6000_2_OR_NEWER
            return RequiresUnity("6000.2", "world-space UI Toolkit panels", new[] { "uitk_list_documents" });
#else
            var go = GameObjectFinder.Find(name, instanceId, path);
            if (go == null)
                return new { error = $"GameObject not found: {name ?? path}" };

            Component panel = null;
            string componentUsed = null;
#if UNITY_6000_5_OR_NEWER
            panel = go.GetComponent<PanelRenderer>();
            if (panel != null) componentUsed = "PanelRenderer";
#endif
            if (panel == null)
            {
                panel = go.GetComponent<UIDocument>();
                if (panel != null) componentUsed = "UIDocument";
            }

            if (panel == null)
                return new { error = $"No PanelRenderer or UIDocument component on '{go.name}'" };

            var ps = TryGetPanelProperty(panel, "panelSettings") as PanelSettings;
            var vta = TryGetPanelProperty(panel, "visualTreeAsset") as VisualTreeAsset;
            var size = TryGetPanelProperty(panel, "worldSpaceSize") is Vector2 v ? v : Vector2.zero;

            string renderMode = null;
            if (ps != null)
            {
                var rmProp = new SerializedObject(ps).FindProperty("m_RenderMode");
                if (rmProp != null) renderMode = rmProp.intValue == 1 ? "WorldSpace" : "ScreenSpaceOverlay";
            }

            return new
            {
                gameObject = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                component = componentUsed,
                visualTreeAsset = vta != null ? AssetDatabase.GetAssetPath(vta) : null,
                panelSettings = ps != null ? AssetDatabase.GetAssetPath(ps) : null,
                panelRenderMode = renderMode,
                worldSpaceSizeMode = TryGetPanelProperty(panel, "worldSpaceSizeMode")?.ToString(),
                worldSpaceSize = new { x = size.x, y = size.y },
                pivot = TryGetPanelProperty(panel, "pivot")?.ToString(),
                pivotReferenceSize = TryGetPanelProperty(panel, "pivotReferenceSize")?.ToString(),
                worldPosition = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
            };
#endif
        }

        // ============================ authoring-id（VisualElementReference 输入） ============================

        [UnitySkill("uitk_element_reference_get", "List authoring-id values in a UXML file and resolve nested authoring-id paths through <Instance> templates (the path input for VisualElementReference on Unity 6000.5+)",
            Category = SkillCategory.UIToolkit, Operation = SkillOperation.Query,
            Tags = new[] { "authoring-id", "visual-element-reference", "uxml", "inspect" },
            Outputs = new[] { "path", "count", "references", "unresolvedTemplates" },
            RequiresInput = new[] { "filePath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object UitkElementReferenceGet(string filePath, int maxTemplateDepth = 3)
        {
            if (Validate.SafePath(filePath, "filePath") is object pathErr) return pathErr;
            if (!File.Exists(filePath))
                return new { error = $"File not found: {filePath}" };

            var references = new System.Collections.Generic.List<object>();
            var unresolved = new System.Collections.Generic.List<string>();
            var visiting = new System.Collections.Generic.HashSet<string>();

            var collectErr = CollectAuthoringIds(filePath, new System.Collections.Generic.List<string>(),
                0, maxTemplateDepth, references, unresolved, visiting);
            if (collectErr != null) return new { error = collectErr };

            return new
            {
                path = filePath,
                count = references.Count,
                references,
                unresolvedTemplates = unresolved.Distinct().OrderBy(u => u).ToArray()
            };
        }

        // ============================ UITK 私有辅助 ============================

        /// <summary>
        /// 针对仅存在于较新编辑器的 API 给出结构化拒绝，使调用方拿到可行动的响应，
        /// 而不是一次编译失败或一次静默空转。
        /// </summary>
        private static object RequiresUnity(string minVersion, string feature, string[] relatedSkills = null) => new
        {
            error = $"{feature} requires Unity {minVersion} or newer. This editor is {Application.unityVersion}.",
            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
            retryStrategy = SkillErrorResponse.Abort,
            suggestedFixes = new[]
            {
                new SuggestedFix
                {
                    action = "abort",
                    reason = $"The underlying Unity API is not present before {minVersion}; upgrade the editor or use an alternative skill."
                }
            },
            relatedSkills = relatedSkills ?? new string[0],
            requiredUnityVersion = minVersion,
            currentUnityVersion = Application.unityVersion
        };

        /// <summary>解析附加 XML 属性的 JSON 对象，拒绝不是合法 XML 名称的键。</summary>
        private static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            ParseAttributeJson(string json, out string error)
        {
            error = null;
            var result = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(json)) return result;

            Newtonsoft.Json.Linq.JObject parsed;
            try
            {
                parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
            }
            catch (System.Exception ex)
            {
                error = $"extraAttributes must be a JSON object like {{\"update-trigger\":\"OnSourceChanged\"}}: {ex.Message}";
                return result;
            }

            foreach (var prop in parsed.Properties())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(prop.Name, @"^[A-Za-z_][\w.-]*$"))
                {
                    error = $"extraAttributes key '{prop.Name}' is not a valid XML attribute name";
                    return result;
                }
                result.Add(new System.Collections.Generic.KeyValuePair<string, string>(
                    prop.Name, prop.Value?.ToString() ?? ""));
            }
            return result;
        }

        private static readonly System.Text.RegularExpressions.Regex ValidIdentifierPattern =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

        /// <summary>
        /// 在 UnityEngine.UIElements 下解析运行时 UI Toolkit 元素类型名（如 "Button"、"TextField"）——
        /// 那正是生成的运行时脚本实际 import 的命名空间。
        /// 解析不到时回退到扫描已加载程序集，因为部分控件（ListView、TreeView 等）在不同 Unity 版本间
        /// 换过所属程序集。名称解析不出 VisualElement 派生类型时返回 null。
        /// </summary>
        private static System.Type ResolveVisualElementType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var type = typeof(VisualElement).Assembly.GetType($"UnityEngine.UIElements.{typeName}");
            if (type == null)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType($"UnityEngine.UIElements.{typeName}", false);
                    if (type != null) break;
                }
            }
            return type != null && typeof(VisualElement).IsAssignableFrom(type) ? type : null;
        }

        /// <summary>
        /// 按全名解析编辑器侧 UI Toolkit 类型。这些类型在不同版本间曾在 UnityEditor.dll 与
        /// UnityEditor.UIElementsModule.dll 之间搬迁，所以放弃前先回退扫描已加载程序集。
        /// </summary>
        private static System.Type FindEditorUiType(string fullName)
        {
            var type = System.Type.GetType($"{fullName}, UnityEditor.UIElementsModule")
                       ?? System.Type.GetType($"{fullName}, UnityEditor");
            if (type != null) return type;

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static string ReadUpgraderString(System.Reflection.PropertyInfo property, object upgrader)
            => property != null && property.CanRead ? property.GetValue(upgrader) as string : null;

        /// <summary>
        /// 针对 UXML 升级 API 的结构化拒绝：Unity 文档称它从 6000.3 起提供，但并非每个 6000.3 构建都带，
        /// 所以在版本足够新的编辑器上它也可能缺失。
        /// </summary>
        private static object UxmlUpgradeUnavailable(string detail) => new
        {
            error = $"Batch UXML upgrade is unavailable in this editor ({Application.unityVersion}). {detail}",
            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
            retryStrategy = SkillErrorResponse.Abort,
            suggestedFixes = new[]
            {
                new SuggestedFix
                {
                    action = "abort",
                    reason = "Edit the UXML directly with uitk_read_file / uitk_write_file, or run the upgrade in an editor whose UI Toolkit module ships UxmlUpgradeService."
                }
            },
            relatedSkills = new[] { "uitk_read_file", "uitk_write_file" },
            requiredUnityVersion = "6000.3",
            currentUnityVersion = Application.unityVersion
        };

        private static object TryGetPanelProperty(object target, string propertyName)
        {
            if (target == null) return null;
            var prop = target.GetType().GetProperty(propertyName);
            return (prop == null || !prop.CanRead) ? null : prop.GetValue(target);
        }

        private static bool TrySetPanelProperty(object target, string propertyName, object value)
        {
            if (target == null) return false;
            var prop = target.GetType().GetProperty(propertyName);
            if (prop == null || !prop.CanWrite) return false;
            prop.SetValue(target, value);
            return true;
        }

        /// <summary>按名称设置枚举类型的属性。成功返回 null，否则返回错误信息。</summary>
        private static string TrySetPanelEnum(object target, string propertyName, string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var prop = target?.GetType().GetProperty(propertyName);
            if (prop == null || !prop.PropertyType.IsEnum || !prop.CanWrite)
                return $"'{propertyName}' is not available on this Unity version ({Application.unityVersion})";
            try
            {
                prop.SetValue(target, System.Enum.Parse(prop.PropertyType, value.Trim(), true));
                return null;
            }
            catch (System.Exception)
            {
                return $"Invalid {propertyName} '{value}'. Valid values: {string.Join(", ", System.Enum.GetNames(prop.PropertyType))}";
            }
        }

        /// <summary>把 PanelSettings 资产切到世界空间渲染模式。发生变更返回 true。</summary>
        private static bool TrySetWorldSpaceRenderMode(PanelSettings settings)
        {
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("m_RenderMode");
            if (prop == null || prop.intValue == 1) return false;

            WorkflowManager.SnapshotObject(settings);
            Undo.RecordObject(settings, "Set PanelSettings World Space");
            prop.intValue = 1;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>
        /// 遍历 UXML 文件收集 authoring-id，并下钻进 &lt;Instance&gt; 节点，
        /// 使嵌套元素能上报其完整的 authoring-id 路径。
        /// </summary>
        private static string CollectAuthoringIds(
            string uxmlPath,
            System.Collections.Generic.List<string> prefix,
            int depth,
            int maxDepth,
            System.Collections.Generic.List<object> output,
            System.Collections.Generic.List<string> unresolved,
            System.Collections.Generic.HashSet<string> visiting)
        {
            if (!File.Exists(uxmlPath))
            {
                unresolved.Add(uxmlPath);
                return null;
            }
            if (!visiting.Add(uxmlPath)) return null; // 模板循环引用

            try
            {
                XDocument xdoc;
                try
                {
                    xdoc = XDocument.Parse(File.ReadAllText(uxmlPath, System.Text.Encoding.UTF8));
                }
                catch (System.Exception ex)
                {
                    return $"Failed to parse UXML '{uxmlPath}': {ex.Message}";
                }
                if (xdoc.Root == null) return null;

                // <Template name="X" src="..."/> 声明用于解析 <Instance template="X"/>。
                var templates = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var t in xdoc.Root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Template"))
                {
                    var tName = (string)t.Attribute("name");
                    var src = (string)t.Attribute("src");
                    if (!string.IsNullOrEmpty(tName) && !string.IsNullOrEmpty(src) && !templates.ContainsKey(tName))
                        templates[tName] = src;
                }

                var baseDir = Path.GetDirectoryName(uxmlPath)?.Replace('\\', '/') ?? "";

                foreach (var el in xdoc.Root.DescendantsAndSelf())
                {
                    var idAttr = (string)el.Attribute("authoring-id");
                    if (string.IsNullOrEmpty(idAttr)) continue;

                    var fullPath = new System.Collections.Generic.List<string>(prefix) { idAttr };
                    output.Add(new
                    {
                        elementType = el.Name.LocalName,
                        elementName = (string)el.Attribute("name"),
                        authoringId = idAttr,
                        authoringIdPath = fullPath.ToArray(),
                        depth,
                        sourceFile = uxmlPath
                    });

                    if (el.Name.LocalName != "Instance" || depth >= maxDepth) continue;

                    var templateName = (string)el.Attribute("template");
                    if (string.IsNullOrEmpty(templateName)) continue;
                    if (!templates.TryGetValue(templateName, out var src))
                    {
                        unresolved.Add(templateName);
                        continue;
                    }

                    var resolved = ResolveUxmlSrc(src, baseDir);
                    if (resolved == null)
                    {
                        unresolved.Add(src);
                        continue;
                    }

                    var err = CollectAuthoringIds(resolved, fullPath, depth + 1, maxDepth, output, unresolved, visiting);
                    if (err != null) return err;
                }
            }
            finally
            {
                visiting.Remove(uxmlPath);
            }
            return null;
        }

        /// <summary>把 UXML 的 src 属性（相对路径或项目路径）解析为资产路径，失败返回 null。</summary>
        private static string ResolveUxmlSrc(string src, string baseDir)
        {
            if (string.IsNullOrEmpty(src)) return null;
            // project:// URI 里带 GUID，存在时优先用它。
            if (src.StartsWith("project://", System.StringComparison.OrdinalIgnoreCase))
            {
                var guidMatch = System.Text.RegularExpressions.Regex.Match(src, @"guid=([0-9a-fA-F]{32})");
                if (!guidMatch.Success) return null;
                var byGuid = AssetDatabase.GUIDToAssetPath(guidMatch.Groups[1].Value);
                return string.IsNullOrEmpty(byGuid) ? null : byGuid;
            }

            var trimmed = src.Replace('\\', '/');
            if (trimmed.StartsWith("./", System.StringComparison.Ordinal)) trimmed = trimmed.Substring(2);

            if (trimmed.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase))
                return File.Exists(trimmed) ? trimmed : null;

            var combined = string.IsNullOrEmpty(baseDir) ? trimmed : $"{baseDir}/{trimmed}";
            while (combined.Contains("/../"))
            {
                var idx = combined.IndexOf("/../", System.StringComparison.Ordinal);
                var head = combined.Substring(0, idx);
                var lastSlash = head.LastIndexOf('/');
                if (lastSlash < 0) return null;
                combined = head.Substring(0, lastSlash) + combined.Substring(idx + 3);
            }
            return File.Exists(combined) ? combined : null;
        }

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

// Producer:Betsy
