using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// 资源导入技能：重新导入与导入器配置。
    /// </summary>
    public static class AssetImportSkills
    {
        [UnitySkill("asset_reimport", "Force reimport of an asset",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Execute,
            Tags = new[] { "asset", "reimport", "refresh", "import" },
            Outputs = new[] { "reimported" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object AssetReimport(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return new { success = false, error = "assetPath is required" };
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;

            if (!SkillsCommon.PathExists(assetPath))
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                var fullPath = Path.Combine(projectRoot, assetPath);
                if (!SkillsCommon.PathExists(fullPath))
                    return new { success = false, error = $"Asset not found: {assetPath}" };
            }

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["reimported"] = assetPath
            };

            if (ServerAvailabilityHelper.AffectsScriptDomain(assetPath))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Reimported script-domain asset: {assetPath}. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Asset reimport completed: {assetPath}. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("asset_reimport_batch", "Reimport multiple assets matching a pattern",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Execute,
            Tags = new[] { "asset", "reimport", "batch", "import", "refresh" },
            Outputs = new[] { "count", "assets" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object AssetReimportBatch(string searchFilter = "*", string folder = "Assets", int limit = 100)
        {
            if (Validate.SafePath(folder, "folder") is object folderErr) return folderErr;

            var guids = AssetDatabase.FindAssets(searchFilter, new[] { folder });
            var reimported = new List<string>();

            foreach (var guid in guids.Take(limit))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) WorkflowManager.SnapshotObject(asset);

                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                reimported.Add(path);
            }

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["count"] = reimported.Count,
                ["assets"] = reimported
            };

            if (reimported.Any(ServerAvailabilityHelper.AffectsScriptDomain))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    "Batch reimport included script-domain assets. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    "Batch reimport completed. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("texture_set_import_settings", "Set texture import settings (maxSize, compression, readable). compression: Uncompressed/Compressed/CompressedHQ/CompressedLQ (Inspector aliases: None=Uncompressed, Normal or NormalQuality=Compressed, HighQuality=CompressedHQ, LowQuality=CompressedLQ)",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "import", "settings", "compression", "mipmap" },
            Outputs = new[] { "assetPath", "maxSize", "compression", "readable", "mipmaps" },
            RequiresInput = new[] { "textureAsset" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object TextureSetImportSettings(
            string assetPath,
            int? maxSize = null,
            string compression = null,
            bool? readable = null,
            bool? generateMipMaps = null,
            string textureType = null)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return new { success = false, error = $"Not a texture or not found: {assetPath}" };

            // 两个枚举都必须在第一次写入前解析完：任一解析失败若被放过，会置 changed=true
            // 并触发一次什么都没改的 SaveAndReimport。别名表用的是共享表，保证本技能与
            // texture_set_settings、texture_set_settings_batch 接受完全相同的词汇
            // （Inspector 显示名与 CLR 枚举名都收）。
            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterCompression>(
                    compression, "compression", SkillParamUtil.TextureCompressionAliases,
                    out var parsedCompression, out var compressionError))
                return compressionError;

            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterType>(
                    textureType, "textureType", SkillParamUtil.TextureTypeAliases,
                    out var parsedTextureType, out var textureTypeError))
                return textureTypeError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            bool changed = false;

            if (maxSize.HasValue)
            {
                importer.maxTextureSize = maxSize.Value;
                changed = true;
            }

            if (parsedCompression.HasValue)
            {
                importer.textureCompression = parsedCompression.Value;
                changed = true;
            }

            if (readable.HasValue)
            {
                importer.isReadable = readable.Value;
                changed = true;
            }

            if (generateMipMaps.HasValue)
            {
                importer.mipmapEnabled = generateMipMaps.Value;
                changed = true;
            }

            if (parsedTextureType.HasValue)
            {
                importer.textureType = parsedTextureType.Value;
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();

            return new
            {
                success = true,
                assetPath,
                maxSize = importer.maxTextureSize,
                compression = importer.textureCompression.ToString(),
                readable = importer.isReadable,
                mipmaps = importer.mipmapEnabled
            };
        }

        [UnitySkill("model_set_import_settings", "Set model (FBX) import settings",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Modify,
            Tags = new[] { "model", "fbx", "import", "settings", "mesh" },
            Outputs = new[] { "assetPath", "globalScale", "importAnimation", "meshCompression" },
            RequiresInput = new[] { "modelAsset" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object ModelSetImportSettings(
            string assetPath,
            float? globalScale = null,
            bool? importMaterials = null,
            bool? importAnimation = null,
            bool? generateColliders = null,
            bool? readable = null,
            string meshCompression = null)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                return new { success = false, error = $"Not a model or not found: {assetPath}" };

            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterMeshCompression>(meshCompression, "meshCompression", out var parsedMeshCompression, out var meshCompressionError))
                return meshCompressionError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            bool changed = false;

            if (globalScale.HasValue)
            {
                importer.globalScale = globalScale.Value;
                changed = true;
            }

            if (importMaterials.HasValue)
            {
                importer.materialImportMode = importMaterials.Value
                    ? ModelImporterMaterialImportMode.ImportViaMaterialDescription
                    : ModelImporterMaterialImportMode.None;
                changed = true;
            }

            if (importAnimation.HasValue)
            {
                importer.importAnimation = importAnimation.Value;
                changed = true;
            }

            if (generateColliders.HasValue)
            {
                importer.addCollider = generateColliders.Value;
                changed = true;
            }

            if (readable.HasValue)
            {
                importer.isReadable = readable.Value;
                changed = true;
            }

            if (parsedMeshCompression.HasValue)
            {
                importer.meshCompression = parsedMeshCompression.Value;
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();

            return new
            {
                success = true,
                assetPath,
                globalScale = importer.globalScale,
                importAnimation = importer.importAnimation,
                meshCompression = importer.meshCompression.ToString()
            };
        }

        [UnitySkill("audio_set_import_settings", "Set audio clip import settings",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Modify,
            Tags = new[] { "audio", "import", "settings", "compression", "clip" },
            Outputs = new[] { "assetPath", "forceToMono", "loadType", "compressionFormat" },
            RequiresInput = new[] { "audioAsset" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object AudioSetImportSettings(
            string assetPath,
            bool? forceToMono = null,
            bool? loadInBackground = null,
            string loadType = null,
            string compressionFormat = null,
            int? quality = null)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null) return new { error = $"Not an audio asset: {assetPath}" };

            if (!SkillParamUtil.TryParseOptionalEnum<AudioClipLoadType>(loadType, "loadType", out var parsedLoadType, out var loadTypeError))
                return loadTypeError;
            if (!SkillParamUtil.TryParseOptionalEnum<AudioCompressionFormat>(compressionFormat, "compressionFormat", out var parsedCompression, out var compressionError))
                return compressionError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            if (forceToMono.HasValue) importer.forceToMono = forceToMono.Value;
            if (loadInBackground.HasValue) importer.loadInBackground = loadInBackground.Value;

            var settings = importer.defaultSampleSettings;
            if (parsedLoadType.HasValue) settings.loadType = parsedLoadType.Value;
            if (parsedCompression.HasValue) settings.compressionFormat = parsedCompression.Value;
            if (quality.HasValue) settings.quality = quality.Value / 100f;

            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();

            return new
            {
                success = true,
                assetPath,
                forceToMono = importer.forceToMono,
                loadType = settings.loadType.ToString(),
                compressionFormat = settings.compressionFormat.ToString()
            };
        }

        [UnitySkill("sprite_set_import_settings", "Set sprite import settings (mode, pivot, packingTag, pixelsPerUnit). spriteMode: Single/Multiple/Polygon force textureType to Sprite; spriteMode=None leaves the texture type alone (a Sprite-typed texture with no sprite is unusable). Response echoes the resulting textureType.",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Modify,
            Tags = new[] { "sprite", "import", "settings", "2d", "texture" },
            Outputs = new[] { "assetPath", "spriteMode", "pixelsPerUnit", "textureType" },
            RequiresInput = new[] { "textureAsset" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object SpriteSetImportSettings(
            string assetPath,
            string spriteMode = null,
            float? pixelsPerUnit = null,
            string packingTag = null,
            string pivotX = null,
            string pivotY = null)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            // 必须在下面强制 textureType=Sprite 之前校验：否则非法 spriteMode 会让资源
            // 已被转成 Sprite，而请求的模式被静默丢弃。
            if (!SkillParamUtil.TryParseOptionalEnum<SpriteImportMode>(spriteMode, "spriteMode", out var parsedSpriteMode, out var spriteModeError))
                return spriteModeError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            // spriteMode=None 意为"这张贴图没有 sprite"，此时不能强制 textureType=Sprite：
            // 那会产出一个 Sprite 类型却没有 Sprite 子对象的资源，谁都引用不了，
            // Inspector 里也退不回去。其余模式仍隐含 Sprite 类型，这是调用方依赖的便利行为。
            if (parsedSpriteMode != SpriteImportMode.None)
                importer.textureType = TextureImporterType.Sprite;
            if (parsedSpriteMode.HasValue)
                importer.spriteImportMode = parsedSpriteMode.Value;

            if (pixelsPerUnit.HasValue) importer.spritePixelsPerUnit = pixelsPerUnit.Value;
            if (!string.IsNullOrEmpty(packingTag))
            {
#if !UNITY_2023_1_OR_NEWER
#pragma warning disable CS0618
                importer.spritePackingTag = packingTag;
#pragma warning restore CS0618
#endif
            }
            if (pivotX != null && pivotY != null)
            {
                importer.spritePivot = new Vector2(
                    float.Parse(pivotX, System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(pivotY, System.Globalization.CultureInfo.InvariantCulture));
            }

            importer.SaveAndReimport();

            return new
            {
                success = true,
                assetPath,
                spriteMode = importer.spriteImportMode.ToString(),
                pixelsPerUnit = importer.spritePixelsPerUnit,
                textureType = importer.textureType.ToString()
            };
        }

        [UnitySkill("texture_get_import_settings", "Get current texture import settings",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "import", "settings", "inspect" },
            Outputs = new[] { "assetPath", "textureType", "maxSize", "compression", "readable", "mipmaps" },
            RequiresInput = new[] { "textureAsset" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TextureGetImportSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            return new
            {
                success = true,
                assetPath,
                textureType = importer.textureType.ToString(),
                maxSize = importer.maxTextureSize,
                compression = importer.textureCompression.ToString(),
                readable = importer.isReadable,
                mipmaps = importer.mipmapEnabled,
                spriteMode = importer.spriteImportMode.ToString(),
                pixelsPerUnit = importer.spritePixelsPerUnit
            };
        }

        [UnitySkill("model_get_import_settings", "Get current model import settings",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Query,
            Tags = new[] { "model", "fbx", "import", "settings", "inspect" },
            Outputs = new[] { "assetPath", "globalScale", "importAnimation", "meshCompression", "readable", "generateColliders" },
            RequiresInput = new[] { "modelAsset" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ModelGetImportSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            return new
            {
                success = true,
                assetPath,
                globalScale = importer.globalScale,
                importAnimation = importer.importAnimation,
                importMaterials = importer.materialImportMode != ModelImporterMaterialImportMode.None,
                meshCompression = importer.meshCompression.ToString(),
                readable = importer.isReadable,
                generateColliders = importer.addCollider
            };
        }

        [UnitySkill("audio_get_import_settings", "Get current audio import settings",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Query,
            Tags = new[] { "audio", "import", "settings", "inspect", "clip" },
            Outputs = new[] { "assetPath", "forceToMono", "loadInBackground", "loadType", "compressionFormat", "quality" },
            RequiresInput = new[] { "audioAsset" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object AudioGetImportSettings(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null) return new { error = $"Not an audio asset: {assetPath}" };

            var settings = importer.defaultSampleSettings;
            return new
            {
                success = true,
                assetPath,
                forceToMono = importer.forceToMono,
                loadInBackground = importer.loadInBackground,
                loadType = settings.loadType.ToString(),
                compressionFormat = settings.compressionFormat.ToString(),
                quality = settings.quality
            };
        }

        [UnitySkill("asset_set_labels", "Set labels on an asset",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Modify,
            Tags = new[] { "asset", "labels", "tag", "metadata" },
            Outputs = new[] { "assetPath", "labels" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object AssetSetLabels(string assetPath, string labels)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return new { error = $"Asset not found: {assetPath}" };

            var labelArray = labels.Split(',').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            AssetDatabase.SetLabels(asset, labelArray);
            return new { success = true, assetPath, labels = labelArray };
        }

        [UnitySkill("asset_get_labels", "Get labels of an asset",
            Category = SkillCategory.AssetImport, Operation = SkillOperation.Query,
            Tags = new[] { "asset", "labels", "metadata", "inspect" },
            Outputs = new[] { "assetPath", "labels" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object AssetGetLabels(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null) return new { error = $"Asset not found: {assetPath}" };

            var labels = AssetDatabase.GetLabels(asset);
            return new { success = true, assetPath, labels };
        }
    }
}

// Producer:Betsy
