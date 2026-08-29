using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Texture import settings skills — read/write TextureImporter properties.
    /// </summary>
    public static class TextureSkills
    {
        /// <summary>
        /// The platform ID strings <c>TextureImporter.Get/SetPlatformTextureSettings</c> actually recognizes.
        /// They are not <c>BuildTarget</c>/<c>BuildTargetGroup</c> enum names — the importer keeps its own
        /// historical word list, most notably "iPhone" (not "iOS") and "Windows Store Apps" (not "WSA").
        /// Both the getter and setter do exact string matching, and silently no-op rather than error on a
        /// mismatch: get returns <c>overridden=false</c> with unrelated default settings attached, and set
        /// writes to an override group no build will ever read. "DefaultTexturePlatform" is Unity's own
        /// sentinel value for the shared/default group — a legitimate value, not a typo.
        /// </summary>
        private static readonly string[] ValidTexturePlatforms =
        {
            "DefaultTexturePlatform", "Standalone", "iPhone", "Android", "WebGL", "Windows Store Apps",
            "PS4", "PS5", "XboxOne", "GameCoreXboxOne", "GameCoreXboxSeries", "Switch", "tvOS",
            "VisionOS", "EmbeddedLinux", "QNX", "Lumin", "WiiU", "Nintendo 3DS", "PSP2",
        };

        private static object InvalidPlatformError(string platform) =>
            SkillParamUtil.InvalidValueError(platform, "platform", ValidTexturePlatforms);


        [UnitySkill("texture_get_settings", "Get texture import settings for an image asset",
            Category = SkillCategory.Texture, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "import", "settings", "inspect" },
            Outputs = new[] { "textureType", "sRGB", "maxTextureSize", "compression", "filterMode" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("texture_set_settings", "Set texture import settings. textureType: Default/NormalMap/GUI/Sprite/Cursor/Cookie/Lightmap/SingleChannel/Shadowmask/DirectionalLightmap (Inspector alias: 'Editor GUI' = GUI). maxSize: 32-8192. filterMode: Point/Bilinear/Trilinear. compression: Uncompressed/Compressed/CompressedHQ/CompressedLQ (Inspector aliases: None=Uncompressed, Normal or NormalQuality=Compressed, HighQuality=CompressedHQ, LowQuality=CompressedLQ)",
            Category = SkillCategory.Texture, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "import", "settings", "compression" },
            Outputs = new[] { "changesApplied", "changes" },
            RequiresInput = new[] { "assetPath" },
            MutatesAssets = true)]
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

            // Every enum is parsed before the first assignment — and before the undo snapshot too, otherwise a
            // call that changes nothing would still leave a restore point. Of these, filterMode/wrapMode/
            // npotScale/compression are silently dropped if parsing fails: on a typo, the other parameters
            // still report changesApplied>0, with zero indication the value was dropped. textureType and
            // compression additionally carry an alias table — the docs teach the Inspector's own wording
            // ("Editor GUI", "Low Quality"), and neither is the CLR name.
            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterType>(
                    textureType, "textureType", SkillParamUtil.TextureTypeAliases,
                    out var tt, out var ttError)) return ttError;
            if (!SkillParamUtil.TryParseOptionalEnum<FilterMode>(
                    filterMode, "filterMode", out var fm, out var fmError)) return fmError;
            if (!SkillParamUtil.TryParseOptionalEnum<TextureWrapMode>(
                    wrapMode, "wrapMode", out var wm, out var wmError)) return wmError;
            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterNPOTScale>(
                    npotScale, "npotScale", out var ns, out var nsError)) return nsError;
            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterCompression>(
                    compression, "compression", SkillParamUtil.TextureCompressionAliases,
                    out var tc, out var tcError)) return tcError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var changes = new List<string>();

            if (tt.HasValue)
            {
                importer.textureType = tt.Value;
                changes.Add($"textureType={tt.Value}");
            }

            if (fm.HasValue)
            {
                importer.filterMode = fm.Value;
                changes.Add($"filterMode={fm.Value}");
            }

            if (wm.HasValue)
            {
                importer.wrapMode = wm.Value;
                changes.Add($"wrapMode={wm.Value}");
            }

            if (ns.HasValue)
            {
                importer.npotScale = ns.Value;
                changes.Add($"npotScale={ns.Value}");
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

            // Sprite-related settings
            if (spritePixelsPerUnit.HasValue)
            {
                importer.spritePixelsPerUnit = spritePixelsPerUnit.Value;
                changes.Add($"spritePixelsPerUnit={SkillParamUtil.FormatFloatR(spritePixelsPerUnit.Value)}");
            }

            // Platform-specific settings (maxSize, compression)
            if (maxSize.HasValue || tc.HasValue)
            {
                var platformSettings = importer.GetDefaultPlatformTextureSettings();

                if (maxSize.HasValue)
                {
                    platformSettings.maxTextureSize = maxSize.Value;
                    changes.Add($"maxSize={maxSize.Value}");
                }

                if (tc.HasValue)
                {
                    platformSettings.textureCompression = tc.Value;
                    changes.Add($"compression={tc.Value}");
                }

                importer.SetPlatformTextureSettings(platformSettings);
            }

            importer.SaveAndReimport();

            return new
            {
                success = true,
                path = assetPath,
                changesApplied = changes.Count,
                changes
            };
        }

        [UnitySkill("texture_set_settings_batch", "Set texture import settings for multiple images. items: JSON array of {assetPath, textureType, maxSize, filterMode, ...}",
            Category = SkillCategory.Texture, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "import", "batch", "settings" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            MutatesAssets = true)]
        public static object TextureSetSettingsBatch(string items)
        {
            return BatchExecutor.Execute<BatchTextureItem>(items, item =>
            {
                var importer = AssetImporter.GetAtPath(item.assetPath) as TextureImporter;
                if (importer == null)
                    throw new System.Exception("Not a texture");

                // Parse up front and pin the error to this item's assetPath, so a bad value can be pinpointed
                // instead of silently vanishing while the item still reports success. The alias table is
                // identical to the single-object setter — a word list that only works on one of the two would be
                // worse than neither recognizing it.
                if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterType>(
                        item.textureType, "textureType", SkillParamUtil.TextureTypeAliases, out var tt, out _))
                    return SkillParamUtil.InvalidEnumError<TextureImporterType>(
                        item.textureType, "textureType", SkillParamUtil.TextureTypeAliases, item.assetPath);
                if (!SkillParamUtil.TryParseOptionalEnum<FilterMode>(item.filterMode, "filterMode", out var fm, out _))
                    return SkillParamUtil.InvalidEnumError<FilterMode>(item.filterMode, "filterMode", item.assetPath);
                if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterCompression>(
                        item.compression, "compression", SkillParamUtil.TextureCompressionAliases, out var tc, out _))
                    return SkillParamUtil.InvalidEnumError<TextureImporterCompression>(
                        item.compression, "compression", SkillParamUtil.TextureCompressionAliases, item.assetPath);

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                if (asset != null) WorkflowManager.SnapshotObject(asset);

                if (tt.HasValue) importer.textureType = tt.Value;
                if (fm.HasValue) importer.filterMode = fm.Value;

                if (item.mipmapEnabled.HasValue) importer.mipmapEnabled = item.mipmapEnabled.Value;
                if (item.sRGB.HasValue) importer.sRGBTexture = item.sRGB.Value;
                if (item.readable.HasValue) importer.isReadable = item.readable.Value;
                if (item.spritePixelsPerUnit.HasValue) importer.spritePixelsPerUnit = item.spritePixelsPerUnit.Value;

                if (item.maxSize.HasValue || tc.HasValue)
                {
                    var ps = importer.GetDefaultPlatformTextureSettings();
                    if (item.maxSize.HasValue) ps.maxTextureSize = item.maxSize.Value;
                    if (tc.HasValue) ps.textureCompression = tc.Value;
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

        [UnitySkill("texture_find_assets", "Search for texture assets in the project",
            Category = SkillCategory.Texture, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "search", "find", "asset" },
            Outputs = new[] { "totalFound", "textures" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("texture_get_info", "Get detailed texture information (dimensions, format, memory)",
            Category = SkillCategory.Texture, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "info", "dimensions", "memory" },
            Outputs = new[] { "width", "height", "format", "mipmapCount", "memorySizeKB" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("texture_set_type", "Set texture type. textureType: Default/NormalMap/GUI/Sprite/Cursor/Cookie/Lightmap/SingleChannel/Shadowmask/DirectionalLightmap (Inspector alias: 'Editor GUI' = GUI)",
            Category = SkillCategory.Texture, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "type", "import" },
            Outputs = new[] { "path", "textureType" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object TextureSetType(string assetPath, string textureType)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };
            if (!SkillParamUtil.TryParseRequiredEnum<TextureImporterType>(
                    textureType, "textureType", SkillParamUtil.TextureTypeAliases, out var tt, out var ttError))
                return ttError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);
            importer.textureType = tt;
            importer.SaveAndReimport();
            return new { success = true, path = assetPath, textureType = tt.ToString() };
        }

        [UnitySkill("texture_set_platform_settings", "Set platform-specific texture settings. platform: Standalone/iPhone/Android/WebGL",
            Category = SkillCategory.Texture, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "platform", "compression", "optimization" },
            Outputs = new[] { "path", "platform", "maxSize", "format" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object TextureSetPlatformSettings(string assetPath, string platform, int? maxSize = null, string format = null, int? compressionQuality = null, bool? overridden = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(platform, "platform") is object err2) return err2;
            if (System.Array.IndexOf(ValidTexturePlatforms, platform) < 0) return InvalidPlatformError(platform);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            if (!SkillParamUtil.TryParseOptionalEnum<TextureImporterFormat>(format, "format", out var tf, out var tfError))
                return tfError;

            var ps = importer.GetPlatformTextureSettings(platform);
            if (overridden.HasValue) ps.overridden = overridden.Value;
            else ps.overridden = true;
            if (maxSize.HasValue) ps.maxTextureSize = maxSize.Value;
            if (tf.HasValue) ps.format = tf.Value;
            if (compressionQuality.HasValue) ps.compressionQuality = compressionQuality.Value;

            importer.SetPlatformTextureSettings(ps);
            importer.SaveAndReimport();
            return new { success = true, path = assetPath, platform, maxSize = ps.maxTextureSize, format = ps.format.ToString() };
        }

        [UnitySkill("texture_get_platform_settings", "Get platform-specific texture settings. platform: Standalone/iPhone/Android/WebGL",
            Category = SkillCategory.Texture, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "platform", "settings", "inspect" },
            Outputs = new[] { "overridden", "maxTextureSize", "format", "compressionQuality" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object TextureGetPlatformSettings(string assetPath, string platform)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(platform, "platform") is object err2) return err2;
            if (System.Array.IndexOf(ValidTexturePlatforms, platform) < 0) return InvalidPlatformError(platform);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            var ps = importer.GetPlatformTextureSettings(platform);
            return new { success = true, path = assetPath, platform, overridden = ps.overridden,
                maxTextureSize = ps.maxTextureSize, format = ps.format.ToString(), compressionQuality = ps.compressionQuality };
        }

        [UnitySkill("texture_set_sprite_settings", "Configure Sprite-specific settings (pixelsPerUnit, spriteMode)",
            Category = SkillCategory.Texture, Operation = SkillOperation.Modify,
            Tags = new[] { "sprite", "texture", "2d", "import" },
            Outputs = new[] { "pixelsPerUnit", "spriteMode" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object TextureSetSpriteSettings(string assetPath, float? pixelsPerUnit = null, string spriteMode = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return new { error = $"Not a texture: {assetPath}" };

            if (!SkillParamUtil.TryParseOptionalEnum<SpriteImportMode>(spriteMode, "spriteMode", out var sm, out var smError))
                return smError;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            if (pixelsPerUnit.HasValue) importer.spritePixelsPerUnit = pixelsPerUnit.Value;
            if (sm.HasValue) importer.spriteImportMode = sm.Value;

            importer.SaveAndReimport();
            return new { success = true, path = assetPath, pixelsPerUnit = importer.spritePixelsPerUnit,
                spriteMode = importer.spriteImportMode.ToString() };
        }

        [UnitySkill("texture_find_by_size", "Find textures by dimension range (minSize/maxSize in pixels)",
            Category = SkillCategory.Texture, Operation = SkillOperation.Query,
            Tags = new[] { "texture", "search", "size", "dimensions" },
            Outputs = new[] { "count", "textures" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

// Producer:Betsy
