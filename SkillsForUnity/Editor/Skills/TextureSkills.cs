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
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"assetPath\":\"Assets/Textures/a.png\",\"textureType\":\"Sprite\"}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchTextureItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                // Start asset editing batch for performance
                AssetDatabase.StartAssetEditing();

                try
                {
                    foreach (var item in itemList)
                    {
                        try
                        {
                            var importer = AssetImporter.GetAtPath(item.assetPath) as TextureImporter;
                            if (importer == null)
                            {
                                results.Add(new { path = item.assetPath, success = false, error = "Not a texture" });
                                failCount++;
                                continue;
                            }

                            // 修改前记录资产状态
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                            if (asset != null) WorkflowManager.SnapshotObject(asset);

                            // Apply settings
                            if (!string.IsNullOrEmpty(item.textureType) &&
                                System.Enum.TryParse<TextureImporterType>(item.textureType.Replace(" ", ""), true, out var tt))
                                importer.textureType = tt;

                            if (!string.IsNullOrEmpty(item.filterMode) &&
                                System.Enum.TryParse<FilterMode>(item.filterMode, true, out var fm))
                                importer.filterMode = fm;

                            if (item.mipmapEnabled.HasValue)
                                importer.mipmapEnabled = item.mipmapEnabled.Value;

                            if (item.sRGB.HasValue)
                                importer.sRGBTexture = item.sRGB.Value;

                            if (item.readable.HasValue)
                                importer.isReadable = item.readable.Value;

                            if (item.spritePixelsPerUnit.HasValue)
                                importer.spritePixelsPerUnit = item.spritePixelsPerUnit.Value;

                            // Platform settings
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
                            results.Add(new { path = item.assetPath, success = true });
                            successCount++;
                        }
                        catch (System.Exception ex)
                        {
                            results.Add(new { path = item.assetPath, success = false, error = ex.Message });
                            failCount++;
                        }
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                    AssetDatabase.Refresh();
                }

                return new
                {
                    success = failCount == 0,
                    totalItems = itemList.Count,
                    successCount,
                    failCount,
                    results
                };
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}" };
            }
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
    }
}
