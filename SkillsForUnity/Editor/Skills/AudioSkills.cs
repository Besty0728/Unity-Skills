using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Audio import settings skills - get/set audio importer properties.
    /// </summary>
    public static class AudioSkills
    {
        [UnitySkill("audio_get_settings", "Get audio import settings for an audio asset")]
        public static object AudioGetSettings(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null)
                return new { error = $"Not an audio file or asset not found: {assetPath}" };

            var defaultSettings = importer.defaultSampleSettings;

            return new
            {
                success = true,
                path = assetPath,
                forceToMono = importer.forceToMono,
                loadInBackground = importer.loadInBackground,
                ambisonic = importer.ambisonic,
                loadType = defaultSettings.loadType.ToString(),
                compressionFormat = defaultSettings.compressionFormat.ToString(),
                quality = defaultSettings.quality,
                sampleRateSetting = defaultSettings.sampleRateSetting.ToString()
            };
        }

        [UnitySkill("audio_set_settings", "Set audio import settings. loadType: DecompressOnLoad/CompressedInMemory/Streaming. compressionFormat: PCM/Vorbis/ADPCM. quality: 0.0-1.0")]
        public static object AudioSetSettings(
            string assetPath,
            bool? forceToMono = null,
            bool? loadInBackground = null,
            bool? ambisonic = null,
            string loadType = null,
            string compressionFormat = null,
            float? quality = null,
            string sampleRateSetting = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
            if (importer == null)
                return new { error = $"Not an audio file or asset not found: {assetPath}" };

            var changes = new List<string>();

            // Basic settings
            if (forceToMono.HasValue)
            {
                importer.forceToMono = forceToMono.Value;
                changes.Add($"forceToMono={forceToMono.Value}");
            }

            if (loadInBackground.HasValue)
            {
                importer.loadInBackground = loadInBackground.Value;
                changes.Add($"loadInBackground={loadInBackground.Value}");
            }

            if (ambisonic.HasValue)
            {
                importer.ambisonic = ambisonic.Value;
                changes.Add($"ambisonic={ambisonic.Value}");
            }

            // Sample settings
            var sampleSettings = importer.defaultSampleSettings;
            bool sampleSettingsChanged = false;

            if (!string.IsNullOrEmpty(loadType))
            {
                if (System.Enum.TryParse<AudioClipLoadType>(loadType, true, out var lt))
                {
                    sampleSettings.loadType = lt;
                    changes.Add($"loadType={lt}");
                    sampleSettingsChanged = true;
                }
                else
                {
                    return new { error = $"Invalid loadType: {loadType}. Valid: DecompressOnLoad, CompressedInMemory, Streaming" };
                }
            }

            if (!string.IsNullOrEmpty(compressionFormat))
            {
                if (System.Enum.TryParse<AudioCompressionFormat>(compressionFormat, true, out var cf))
                {
                    sampleSettings.compressionFormat = cf;
                    changes.Add($"compressionFormat={cf}");
                    sampleSettingsChanged = true;
                }
                else
                {
                    return new { error = $"Invalid compressionFormat: {compressionFormat}. Valid: PCM, Vorbis, ADPCM" };
                }
            }

            if (quality.HasValue)
            {
                sampleSettings.quality = Mathf.Clamp01(quality.Value);
                changes.Add($"quality={sampleSettings.quality}");
                sampleSettingsChanged = true;
            }

            if (!string.IsNullOrEmpty(sampleRateSetting))
            {
                if (System.Enum.TryParse<AudioSampleRateSetting>(sampleRateSetting, true, out var srs))
                {
                    sampleSettings.sampleRateSetting = srs;
                    changes.Add($"sampleRateSetting={srs}");
                    sampleSettingsChanged = true;
                }
            }

            if (sampleSettingsChanged)
            {
                importer.defaultSampleSettings = sampleSettings;
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

        [UnitySkill("audio_set_settings_batch", "Set audio import settings for multiple audio files. items: JSON array of {assetPath, forceToMono, loadType, compressionFormat, quality, ...}")]
        public static object AudioSetSettingsBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"assetPath\":\"Assets/Audio/bgm.mp3\",\"loadType\":\"Streaming\"}]" };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchAudioItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                AssetDatabase.StartAssetEditing();

                try
                {
                    foreach (var item in itemList)
                    {
                        try
                        {
                            var importer = AssetImporter.GetAtPath(item.assetPath) as AudioImporter;
                            if (importer == null)
                            {
                                results.Add(new { path = item.assetPath, success = false, error = "Not an audio file" });
                                failCount++;
                                continue;
                            }

                            // Apply basic settings
                            if (item.forceToMono.HasValue)
                                importer.forceToMono = item.forceToMono.Value;
                            if (item.loadInBackground.HasValue)
                                importer.loadInBackground = item.loadInBackground.Value;

                            // Apply sample settings
                            var ss = importer.defaultSampleSettings;
                            bool ssChanged = false;

                            if (!string.IsNullOrEmpty(item.loadType) &&
                                System.Enum.TryParse<AudioClipLoadType>(item.loadType, true, out var lt))
                            {
                                ss.loadType = lt;
                                ssChanged = true;
                            }

                            if (!string.IsNullOrEmpty(item.compressionFormat) &&
                                System.Enum.TryParse<AudioCompressionFormat>(item.compressionFormat, true, out var cf))
                            {
                                ss.compressionFormat = cf;
                                ssChanged = true;
                            }

                            if (item.quality.HasValue)
                            {
                                ss.quality = Mathf.Clamp01(item.quality.Value);
                                ssChanged = true;
                            }

                            if (ssChanged)
                                importer.defaultSampleSettings = ss;

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

        private class BatchAudioItem
        {
            public string assetPath { get; set; }
            public bool? forceToMono { get; set; }
            public bool? loadInBackground { get; set; }
            public string loadType { get; set; }
            public string compressionFormat { get; set; }
            public float? quality { get; set; }
        }
    }
}
