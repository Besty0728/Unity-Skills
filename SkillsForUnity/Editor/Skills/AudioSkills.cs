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
            return BatchExecutor.Execute<BatchAudioItem>(items, item =>
            {
                var importer = AssetImporter.GetAtPath(item.assetPath) as AudioImporter;
                if (importer == null)
                    throw new System.Exception("Not an audio file");

                if (item.forceToMono.HasValue)
                    importer.forceToMono = item.forceToMono.Value;
                if (item.loadInBackground.HasValue)
                    importer.loadInBackground = item.loadInBackground.Value;

                var ss = importer.defaultSampleSettings;
                bool ssChanged = false;

                if (!string.IsNullOrEmpty(item.loadType) &&
                    System.Enum.TryParse<AudioClipLoadType>(item.loadType, true, out var lt))
                { ss.loadType = lt; ssChanged = true; }

                if (!string.IsNullOrEmpty(item.compressionFormat) &&
                    System.Enum.TryParse<AudioCompressionFormat>(item.compressionFormat, true, out var cf))
                { ss.compressionFormat = cf; ssChanged = true; }

                if (item.quality.HasValue)
                { ss.quality = Mathf.Clamp01(item.quality.Value); ssChanged = true; }

                if (ssChanged)
                    importer.defaultSampleSettings = ss;

                importer.SaveAndReimport();
                return new { path = item.assetPath, success = true };
            }, item => item.assetPath);
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

        [UnitySkill("audio_find_clips", "Search for AudioClip assets in the project")]
        public static object AudioFindClips(string filter = "", int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:AudioClip " + filter);
            var clips = guids.Take(limit).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                return new { path, name = clip != null ? clip.name : System.IO.Path.GetFileNameWithoutExtension(path), length = clip != null ? clip.length : 0f };
            }).ToArray();
            return new { success = true, totalFound = guids.Length, showing = clips.Length, clips };
        }

        [UnitySkill("audio_get_clip_info", "Get detailed information about an AudioClip asset")]
        public static object AudioGetClipInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null) return new { error = $"AudioClip not found: {assetPath}" };

            return new { success = true, name = clip.name, path = assetPath, length = clip.length, channels = clip.channels,
                frequency = clip.frequency, samples = clip.samples, loadType = clip.loadType.ToString(),
                loadState = clip.loadState.ToString(), ambisonic = clip.ambisonic };
        }

        [UnitySkill("audio_add_source", "Add an AudioSource component to a GameObject")]
        public static object AudioAddSource(string name = null, int instanceId = 0, string path = null, string clipPath = null, bool playOnAwake = false, bool loop = false, float volume = 1f)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var source = Undo.AddComponent<AudioSource>(go);
            source.playOnAwake = playOnAwake;
            source.loop = loop;
            source.volume = volume;

            if (!string.IsNullOrEmpty(clipPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip != null) source.clip = clip;
            }

            return new { success = true, gameObject = go.name, instanceId = go.GetInstanceID() };
        }

        [UnitySkill("audio_get_source_info", "Get AudioSource configuration")]
        public static object AudioGetSourceInfo(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var source = go.GetComponent<AudioSource>();
            if (source == null) return new { error = $"No AudioSource on {go.name}" };

            return new { success = true, gameObject = go.name, clip = source.clip != null ? source.clip.name : "null",
                volume = source.volume, pitch = source.pitch, loop = source.loop, playOnAwake = source.playOnAwake,
                mute = source.mute, spatialBlend = source.spatialBlend, minDistance = source.minDistance,
                maxDistance = source.maxDistance, priority = source.priority };
        }

        [UnitySkill("audio_set_source_properties", "Set AudioSource properties")]
        public static object AudioSetSourceProperties(string name = null, int instanceId = 0, string path = null, string clipPath = null,
            float? volume = null, float? pitch = null, bool? loop = null, bool? playOnAwake = null, bool? mute = null, float? spatialBlend = null, int? priority = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var source = go.GetComponent<AudioSource>();
            if (source == null) return new { error = $"No AudioSource on {go.name}" };

            Undo.RecordObject(source, "Set AudioSource Properties");
            if (!string.IsNullOrEmpty(clipPath)) { var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath); if (clip != null) source.clip = clip; }
            if (volume.HasValue) source.volume = volume.Value;
            if (pitch.HasValue) source.pitch = pitch.Value;
            if (loop.HasValue) source.loop = loop.Value;
            if (playOnAwake.HasValue) source.playOnAwake = playOnAwake.Value;
            if (mute.HasValue) source.mute = mute.Value;
            if (spatialBlend.HasValue) source.spatialBlend = spatialBlend.Value;
            if (priority.HasValue) source.priority = priority.Value;

            return new { success = true, gameObject = go.name };
        }

        [UnitySkill("audio_find_sources_in_scene", "Find all AudioSource components in the current scene")]
        public static object AudioFindSourcesInScene(int limit = 50)
        {
            var sources = Object.FindObjectsOfType<AudioSource>();
            var results = sources.Take(limit).Select(s => new { gameObject = s.gameObject.name, path = GameObjectFinder.GetPath(s.gameObject),
                clip = s.clip != null ? s.clip.name : "null", volume = s.volume, loop = s.loop, enabled = s.enabled }).ToArray();
            return new { success = true, totalFound = sources.Length, showing = results.Length, sources = results };
        }

        [UnitySkill("audio_create_mixer", "Create a new AudioMixer asset")]
        public static object AudioCreateMixer(string mixerName = "NewAudioMixer", string folder = "Assets")
        {
            if (Validate.SafePath(folder, "folder") is object pathErr) return pathErr;
            if (!System.IO.Directory.Exists(folder)) System.IO.Directory.CreateDirectory(folder);

            var savePath = System.IO.Path.Combine(folder, mixerName + ".mixer").Replace("\\", "/");
            if (System.IO.File.Exists(savePath)) return new { error = $"Mixer already exists: {savePath}" };

            // Search AudioMixerController across all assemblies (moved in Unity 6)
            System.Type mixerType = typeof(Editor).Assembly.GetType("UnityEditor.Audio.AudioMixerController");
            if (mixerType == null)
            {
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    mixerType = asm.GetType("UnityEditor.Audio.AudioMixerController");
                    if (mixerType != null) break;
                }
            }
            if (mixerType == null) return new { error = "AudioMixerController type not found" };

            var mixer = ScriptableObject.CreateInstance(mixerType);
            if (mixer == null)
            {
                // Fallback: try internal constructor
                try { mixer = (ScriptableObject)System.Activator.CreateInstance(mixerType, true); } catch { }
            }
            if (mixer == null)
            {
                // Fallback: try static factory method
                try
                {
                    var createMethod = mixerType.GetMethod("CreateMixerControllerAtPath",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (createMethod != null)
                    {
                        mixer = createMethod.Invoke(null, new object[] { savePath }) as ScriptableObject;
                        if (mixer != null) { AssetDatabase.SaveAssets(); return new { success = true, path = savePath, name = mixerName }; }
                    }
                }
                catch { }
                return new { error = "Failed to create AudioMixer instance" };
            }

            AssetDatabase.CreateAsset(mixer, savePath);
            AssetDatabase.SaveAssets();
            return new { success = true, path = savePath, name = mixerName };
        }
    }
}
