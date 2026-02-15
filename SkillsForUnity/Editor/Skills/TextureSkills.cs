using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Texture import settings skills - get/set texture importer properties.
    /// </summary>
    public static class TextureSkills
    {
        [UnitySkill("texture_get_settings", "Get texture import settings for an image asset")]
        public static object TextureGetSettings(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return new { error = $"Not a texture or asset not found: {assetPath}" };

            var platformSettings = importer.GetDefaultPlatformTextureSettings();

            return new
            {
                success = true,
                path = assetPath,
                textureType = importer.textureType.ToString(),
                textureShape = importer.textureShape.ToString(),
                sRGB = importer.sRGBTexture,
                alphaSource = importer.alphaSource.ToString(),
                alphaIsTransparency = importer.alphaIsTransparency,
                readable = importer.isReadable,
                mipmapEnabled = importer.mipmapEnabled,
                filterMode = importer.filterMode.ToString(),
                wrapMode = importer.wrapMode.ToString(),
                maxTextureSize = platformSettings.maxTextureSize,
                compression = platformSettings.textureCompression.ToString(),
                spriteMode = importer.spriteImportMode.ToString(),
                spritePixelsPerUnit = importer.spritePixelsPerUnit,
                npotScale = importer.npotScale.ToString()
            };
        }

        [UnitySkill("texture_set_settings", "Set texture import settings. textureType: Default/NormalMap/Sprite/Editor GUI/Cursor/Cookie/Lightmap/SingleChannel. maxSize: 32-8192. filterMode: Point/Bilinear/Trilinear. compression: None/LowQuality/Normal/HighQuality")]
        public static object TextureSetSettings(
            string assetPath,
            string textureType = null,
            int? maxSize = null,
            string filterMode = null,
            string compression = null,
            bool? mipmapEnabled = null,
            bool? sRGB = null,
            bool? readable = null,
            bool? alphaIsTransparency = null,
            float? spritePixelsPerUnit = null,
            string wrapMode = null,
            string npotScale = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                return new { error = $"Not a texture or asset not found: {assetPath}" };

            // 修改前记录资产状态
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var changes = new List<string>();

            // Texture Type
            if (!string.IsNullOrEmpty(textureType))
            {
                if (System.Enum.TryParse<TextureImporterType>(textureType.Replace(" ", ""), true, out var tt))
                {
                    importer.textureType = tt;
                    changes.Add($"textureType={tt}");
                }
                else
                {
                    return new { error = $"Invalid textureType: {textureType}. Valid: Default, NormalMap, Sprite, EditorGUI, Cursor, Cookie, Lightmap, SingleChannel" };
                }
            }

            // Filter Mode
            if (!string.IsNullOrEmpty(filterMode))
            {
                if (System.Enum.TryParse<FilterMode>(filterMode, true, out var fm))
                {
                    importer.filterMode = fm;
                    changes.Add($"filterMode={fm}");
                }
            }

            // Wrap Mode
            if (!string.IsNullOrEmpty(wrapMode))
            {
                if (System.Enum.TryParse<TextureWrapMode>(wrapMode, true, out var wm))
                {
                    importer.wrapMode = wm;
                    changes.Add($"wrapMode={wm}");
                }
            }

            // NPOT Scale
            if (!string.IsNullOrEmpty(npotScale))
            {
                if (System.Enum.TryParse<TextureImporterNPOTScale>(npotScale, true, out var ns))
                {
                    importer.npotScale = ns;
                    changes.Add($"npotScale={ns}");
                }
            }

            // Boolean settings
            if (mipmapEnabled.HasValue)
            {
                importer.mipmapEnabled = mipmapEnabled.Value;
                changes.Add($"mipmapEnabled={mipmapEnabled.Value}");
            }

            if (sRGB.HasValue)
            {
                importer.sRGBTexture = sRGB.Value;
                changes.Add($"sRGB={sRGB.Value}");
            }

            if (readable.HasValue)
            {
                importer.isReadable = readable.Value;
                changes.Add($"readable={readable.Value}");
            }

            if (alphaIsTransparency.HasValue)
            {
                importer.alphaIsTransparency = alphaIsTransparency.Value;
                changes.Add($"alphaIsTransparency={alphaIsTransparency.Value}");
            }

            // Sprite settings
            if (spritePixelsPerUnit.HasValue)
            {
                importer.spritePixelsPerUnit = spritePixelsPerUnit.Value;
                changes.Add($"spritePixelsPerUnit={spritePixelsPerUnit.Value}");
            }

            // Platform-specific settings (maxSize, compression)
            if (maxSize.HasValue || !string.IsNullOrEmpty(compression))
            {
                var platformSettings = importer.GetDefaultPlatformTextureSettings();

                if (maxSize.HasValue)
                {
                    platformSettings.maxTextureSize = maxSize.Value;
                    changes.Add($"maxSize={maxSize.Value}");
                }

                if (!string.IsNullOrEmpty(compression))
                {
                    if (System.Enum.TryParse<TextureImporterCompression>(compression, true, out var tc))
                    {
                        platformSettings.textureCompression = tc;
                        changes.Add($"compression={tc}");
                    }
                }

                importer.SetPlatformTextureSettings(platformSettings);
            }

            // Apply changes
            importer.SaveAndReimport();

            return new
            {
                success = true,
                path = assetPath,
                changesApplied = changes.Count,
                changes
            };
        }

        [UnitySkill("texture_set_settings_batch", "Set texture import settings for multiple images. items: JSON array of {assetPath, textureType, maxSize, filterMode, ...}")]
        public static object TextureSetSettingsBatch(string items)
        {
            return BatchExecutor.Execute<BatchTextureItem>(items, item =>
            {
                var importer = AssetImporter.GetAtPath(item.assetPath) as TextureImporter;
                if (importer == null)
                    throw new System.Exception("Not a texture");

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                if (asset != null) WorkflowManager.SnapshotObject(asset);

                if (!string.IsNullOrEmpty(item.textureType) &&
                    System.Enum.TryParse<TextureImporterType>(item.textureType.Replace(" ", ""), true, out var tt))
                    importer.textureType = tt;

                if (!string.IsNullOrEmpty(item.filterMode) &&
                    System.Enum.TryParse<FilterMode>(item.filterMode, true, out var fm))
                    importer.filterMode = fm;

                if (item.mipmapEnabled.HasValue) importer.mipmapEnabled = item.mipmapEnabled.Value;
                if (item.sRGB.HasValue) importer.sRGBTexture = item.sRGB.Value;
                if (item.readable.HasValue) importer.isReadable = item.readable.Value;
                if (item.spritePixelsPerUnit.HasValue) importer.spritePixelsPerUnit = item.spritePixelsPerUnit.Value;

                if (item.maxSize.HasValue || !string.IsNullOrEmpty(item.compression))
                {
                    var ps = importer.GetDefaultPlatformTextureSettings();
                    if (item.maxSize.HasValue) ps.maxTextureSize = item.maxSize.Value;
                    if (!string.IsNullOrEmpty(item.compression) &&
                        System.Enum.TryParse<TextureImporterCompression>(item.compression, true, out var tc))
                        ps.textureCompression = tc;
                    importer.SetPlatformTextureSettings(ps);
                }

                importer.SaveAndReimport();
                return new { path = item.assetPath, success = true };
            }, item => item.assetPath,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchTextureItem
        {
            public string assetPath { get; set; }
            public string textureType { get; set; }
            public int? maxSize { get; set; }
            public string filterMode { get; set; }
            public string compression { get; set; }
            public bool? mipmapEnabled { get; set; }
            public bool? sRGB { get; set; }
            public bool? readable { get; set; }
            public float? spritePixelsPerUnit { get; set; }
        }

        [UnitySkill("texture_find_assets", "Search for texture assets in the project")]
        public static object TextureFindAssets(string filter = "", int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D " + filter);
            var textures = guids.Take(limit).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return new { path, name = tex != null ? tex.name : System.IO.Path.GetFileNameWithoutExtension(path),
                    width = tex != null ? tex.width : 0, height = tex != null ? tex.height : 0 };
            }).ToArray();
            return new { success = true, totalFound = guids.Length, showing = textures.Length, textures };
        }

        [UnitySkill("texture_get_info", "Get detailed texture information (dimensions, format, memory)")]
        public static object TextureGetInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null) return new { error = $"Texture not found: {assetPath}" };

            long memSize = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(tex);
            return new { success = true, name = tex.name, path = assetPath, width = tex.width, height = tex.height,
                format = tex.format.ToString(), mipmapCount = tex.mipmapCount, isReadable = tex.isReadable,
                filterMode = tex.filterMode.ToString(), wrapMode = tex.wrapMode.ToString(), memorySizeKB = memSize / 1024f };
        }

        [UnitySkill("texture_set_type", "Set texture type. textureType: Default/NormalMap/Sprite/EditorGUI/Cursor/Cookie/Lightmap/SingleChannel")]
        public static object TextureSetType(string assetPath, string textureType)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };
            if (!System.Enum.TryParse<TextureImporterType>(textureType.Replace(" ", ""), true, out var tt))
                return new { error = $"Invalid textureType: {textureType}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);
            importer.textureType = tt;
            importer.SaveAndReimport();
            return new { success = true, path = assetPath, textureType = tt.ToString() };
        }

        [UnitySkill("texture_set_platform_settings", "Set platform-specific texture settings. platform: Standalone/iPhone/Android/WebGL")]
        public static object TextureSetPlatformSettings(string assetPath, string platform, int? maxSize = null, string format = null, int? compressionQuality = null, bool? overridden = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(platform, "platform") is object err2) return err2;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            var ps = importer.GetPlatformTextureSettings(platform);
            if (overridden.HasValue) ps.overridden = overridden.Value;
            else ps.overridden = true;
            if (maxSize.HasValue) ps.maxTextureSize = maxSize.Value;
            if (!string.IsNullOrEmpty(format) && System.Enum.TryParse<TextureImporterFormat>(format, true, out var tf))
                ps.format = tf;
            if (compressionQuality.HasValue) ps.compressionQuality = compressionQuality.Value;

            importer.SetPlatformTextureSettings(ps);
            importer.SaveAndReimport();
            return new { success = true, path = assetPath, platform, maxSize = ps.maxTextureSize, format = ps.format.ToString() };
        }

        [UnitySkill("texture_get_platform_settings", "Get platform-specific texture settings. platform: Standalone/iPhone/Android/WebGL")]
        public static object TextureGetPlatformSettings(string assetPath, string platform)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(platform, "platform") is object err2) return err2;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            var ps = importer.GetPlatformTextureSettings(platform);
            return new { success = true, path = assetPath, platform, overridden = ps.overridden,
                maxTextureSize = ps.maxTextureSize, format = ps.format.ToString(), compressionQuality = ps.compressionQuality };
        }

        [UnitySkill("texture_set_sprite_settings", "Configure Sprite-specific settings (pixelsPerUnit, meshType, spriteMode)")]
        public static object TextureSetSpriteSettings(string assetPath, float? pixelsPerUnit = null, string spriteMode = null, string meshType = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            if (pixelsPerUnit.HasValue) importer.spritePixelsPerUnit = pixelsPerUnit.Value;
            if (!string.IsNullOrEmpty(spriteMode) && System.Enum.TryParse<SpriteImportMode>(spriteMode, true, out var sm))
                importer.spriteImportMode = sm;
            if (!string.IsNullOrEmpty(meshType) && System.Enum.TryParse<SpriteMeshType>(meshType, true, out var mt))
                importer.spriteMeshType = mt;

            importer.SaveAndReimport();
            return new { success = true, path = assetPath, pixelsPerUnit = importer.spritePixelsPerUnit,
                spriteMode = importer.spriteImportMode.ToString(), meshType = importer.spriteMeshType.ToString() };
        }

        [UnitySkill("texture_find_by_size", "Find textures by dimension range (minSize/maxSize in pixels)")]
        public static object TextureFindBySize(int minSize = 0, int maxSize = 99999, int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D");
            var results = new List<object>();

            foreach (var guid in guids)
            {
                if (results.Count >= limit) break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                int maxDim = Mathf.Max(tex.width, tex.height);
                if (maxDim >= minSize && maxDim <= maxSize)
                    results.Add(new { path, name = tex.name, width = tex.width, height = tex.height });
            }

            return new { success = true, count = results.Count, textures = results };
        }
    }
}
