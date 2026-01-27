using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Optimization skills - texture compression settings, etc.
    /// </summary>
    public static class OptimizationSkills
    {
        [UnitySkill("optimize_textures", "Optimize texture settings (maxSize, compression). Returns list of modified textures.")]
        public static object OptimizeTextures(int maxTextureSize = 2048, bool enableCrunch = true, int compressionQuality = 50, string filter = "")
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D " + filter);
            var modified = new List<object>();
            
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                bool changed = false;

                // Check max size
                if (importer.maxTextureSize > maxTextureSize)
                {
                    importer.maxTextureSize = maxTextureSize;
                    changed = true;
                }

                // Check compression
                if (importer.textureCompression != TextureImporterCompression.Compressed)
                {
                    // Only enforce if it was uncompressed or custom? 
                    // Let's be careful not to break UI textures.
                    // Usually we only optimize "Default" texture type or 3D models.
                    if (importer.textureType == TextureImporterType.Default) 
                    {
                         importer.textureCompression = TextureImporterCompression.Compressed;
                         changed = true;
                    }
                }

                if (enableCrunch && importer.crunchedCompression != true)
                {
                     if (importer.textureType == TextureImporterType.Default)
                     {
                        importer.crunchedCompression = true;
                        importer.compressionQuality = compressionQuality;
                        changed = true;
                     }
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                    modified.Add(new { path, name = System.IO.Path.GetFileName(path) });
                }
            }

            return new
            {
                count = modified.Count,
                message = $"Optimized {modified.Count} textures",
                modified
            };
        }

        [UnitySkill("optimize_mesh_compression", "Set mesh compression for 3D models")]
        public static object OptimizeMeshCompression(string compressionLevel = "Medium", string filter = "")
        {
            ModelImporterMeshCompression comp;
            switch (compressionLevel.ToLower())
            {
                case "off": comp = ModelImporterMeshCompression.Off; break;
                case "low": comp = ModelImporterMeshCompression.Low; break;
                case "medium": comp = ModelImporterMeshCompression.Medium; break;
                case "high": comp = ModelImporterMeshCompression.High; break;
                default: comp = ModelImporterMeshCompression.Medium; break;
            }

            var guids = AssetDatabase.FindAssets("t:Model " + filter);
            var modified = new List<object>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;

                if (importer.meshCompression != comp)
                {
                    importer.meshCompression = comp;
                    importer.SaveAndReimport();
                    modified.Add(new { path, name = System.IO.Path.GetFileName(path) });
                }
            }

            return new
            {
                count = modified.Count,
                compression = comp.ToString(),
                modified
            };
        }
    }
}
