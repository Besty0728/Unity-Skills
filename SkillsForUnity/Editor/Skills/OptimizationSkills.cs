using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Optimization skills - texture compression settings, etc.
    /// </summary>
    public static class OptimizationSkills
    {
        [UnitySkill("optimize_textures", "Optimize texture settings (maxSize, compression). Returns list of modified textures.",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Execute | SkillOperation.Modify,
            Tags = new[] { "optimization", "texture", "compression", "crunch" },
            Outputs = new[] { "count", "message", "modified" })]
        public static object OptimizeTextures(int maxTextureSize = 2048, bool enableCrunch = true, int compressionQuality = 50, string filter = "", int limit = 0)
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D " + filter);
            var modified = new List<object>();

            foreach (var guid in guids)
            {
                if (limit > 0 && modified.Count >= limit) break;

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
                success = true,
                count = modified.Count,
                message = $"Optimized {modified.Count} textures",
                modified
            };
        }

        [UnitySkill("optimize_mesh_compression", "Set mesh compression for 3D models",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Execute | SkillOperation.Modify,
            Tags = new[] { "optimization", "mesh", "compression", "model" },
            Outputs = new[] { "count", "compression", "modified" })]
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
                success = true,
                count = modified.Count,
                compression = comp.ToString(),
                modified
            };
        }

        [UnitySkill("optimize_analyze_scene", "Analyze scene for performance bottlenecks (high-poly meshes, excessive materials)",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Analyze,
            Tags = new[] { "optimization", "scene", "performance", "poly", "materials" },
            Outputs = new[] { "totalRenderers", "totalTriangles", "totalMaterialSlots", "issueCount", "issues" },
            ReadOnly = true)]
        public static object OptimizeAnalyzeScene(int polyThreshold = 10000, int materialThreshold = 5)
        {
            var renderers = FindHelper.FindAll<Renderer>();
            var issues = new List<object>();
            int totalTris = 0, totalMats = 0;

            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    int tris = SkillsCommon.GetTriangleCount(mf.sharedMesh);
                    totalTris += tris;
                    if (tris > polyThreshold)
                        issues.Add(new { type = "HighPoly", gameObject = r.name, path = GameObjectFinder.GetPath(r.gameObject), triangles = tris });
                }
                int matCount = r.sharedMaterials.Length;
                totalMats += matCount;
                if (matCount > materialThreshold)
                    issues.Add(new { type = "ExcessiveMaterials", gameObject = r.name, path = GameObjectFinder.GetPath(r.gameObject), materialCount = matCount });
            }

            return new { success = true, totalRenderers = renderers.Length, totalTriangles = totalTris, totalMaterialSlots = totalMats, issueCount = issues.Count, issues };
        }

        [UnitySkill("optimize_find_large_assets", "Find assets exceeding a size threshold (in KB)",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Analyze,
            Tags = new[] { "optimization", "assets", "size", "large" },
            Outputs = new[] { "threshold", "count", "assets" },
            ReadOnly = true)]
        public static object OptimizeFindLargeAssets(int thresholdKB = 1024, string assetType = "", int limit = 50)
        {
            var filter = string.IsNullOrEmpty(assetType) ? "" : $"t:{assetType}";
            var guids = AssetDatabase.FindAssets(filter);
            var large = new List<object>();

            foreach (var guid in guids)
            {
                if (large.Count >= limit) break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;
                var fullPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), path);
                if (!System.IO.File.Exists(fullPath)) continue;
                var sizeKB = (int)(new System.IO.FileInfo(fullPath).Length / 1024);
                if (sizeKB >= thresholdKB)
                    large.Add(new { path, sizeKB, name = System.IO.Path.GetFileName(path) });
            }

            return new { success = true, threshold = $"{thresholdKB}KB", count = large.Count, assets = large };
        }

        [UnitySkill("optimize_set_static_flags", "Set static flags on GameObjects. flags: Everything/Nothing/BatchingStatic/OccludeeStatic/OccluderStatic/NavigationStatic/ReflectionProbeStatic", TracksWorkflow = true,
            Category = SkillCategory.Optimization, Operation = SkillOperation.Modify,
            Tags = new[] { "optimization", "static", "flags", "batching" },
            Outputs = new[] { "gameObject", "flags", "affectedCount" },
            RequiresInput = new[] { "gameObject" })]
        public static object OptimizeSetStaticFlags(string name = null, int instanceId = 0, string path = null, string flags = "Everything", bool includeChildren = false)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            if (!System.Enum.TryParse<StaticEditorFlags>(flags, true, out var staticFlags))
                return new { error = $"Invalid flags: {flags}" };

            var targets = new List<GameObject> { go };
            if (includeChildren)
                targets.AddRange(go.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject));

            foreach (var t in targets)
            {
                Undo.RecordObject(t, "Set Static Flags");
                GameObjectUtility.SetStaticEditorFlags(t, staticFlags);
            }

            return new { success = true, gameObject = go.name, flags = staticFlags.ToString(), affectedCount = targets.Count };
        }

        [UnitySkill("optimize_get_static_flags", "Get static flags of a GameObject",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Query,
            Tags = new[] { "optimization", "static", "flags", "query" },
            Outputs = new[] { "gameObject", "flags", "isStatic" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true)]
        public static object OptimizeGetStaticFlags(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            return new { success = true, gameObject = go.name, flags = flags.ToString(), isStatic = go.isStatic };
        }

        [UnitySkill("optimize_audio_compression", "Batch set audio compression. compressionFormat: PCM/Vorbis/ADPCM. loadType: DecompressOnLoad/CompressedInMemory/Streaming", TracksWorkflow = true,
            Category = SkillCategory.Optimization, Operation = SkillOperation.Execute | SkillOperation.Modify,
            Tags = new[] { "optimization", "audio", "compression", "batch" },
            Outputs = new[] { "count", "compressionFormat", "loadType", "modified" })]
        public static object OptimizeAudioCompression(string compressionFormat = "Vorbis", string loadType = "CompressedInMemory", float quality = 0.5f, string filter = "")
        {
            if (!System.Enum.TryParse<AudioCompressionFormat>(compressionFormat, true, out var cf))
                return new { error = $"Invalid compressionFormat: {compressionFormat}" };
            if (!System.Enum.TryParse<AudioClipLoadType>(loadType, true, out var lt))
                return new { error = $"Invalid loadType: {loadType}" };

            var guids = AssetDatabase.FindAssets("t:AudioClip " + filter);
            var modified = new List<object>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(p) as AudioImporter;
                    if (importer == null) continue;
                    var ss = importer.defaultSampleSettings;
                    if (ss.compressionFormat == cf && ss.loadType == lt) continue;
                    ss.compressionFormat = cf;
                    ss.loadType = lt;
                    ss.quality = Mathf.Clamp01(quality);
                    importer.defaultSampleSettings = ss;
                    importer.SaveAndReimport();
                    modified.Add(new { path = p });
                }
            }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); }

            return new { success = true, count = modified.Count, compressionFormat, loadType, modified };
        }

        [UnitySkill("optimize_find_duplicate_materials", "Find materials with identical shader and properties",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Analyze,
            Tags = new[] { "optimization", "materials", "duplicates", "shader" },
            Outputs = new[] { "duplicateGroups", "groups" },
            ReadOnly = true)]
        public static object OptimizeFindDuplicateMaterials(int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:Material");
            var matInfos = new List<(string path, string key)>();

            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (mat == null || mat.shader == null) continue;
                var colorStr = mat.HasProperty("_Color") ? mat.color.ToString() : (mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor").ToString() : "none");
                matInfos.Add((p, mat.shader.name + "|" + colorStr + "|" + mat.renderQueue));
            }

            var duplicates = matInfos.GroupBy(m => m.key).Where(g => g.Count() > 1).Take(limit)
                .Select(g => new { shader = g.Key.Split('|')[0], count = g.Count(), paths = g.Select(m => m.path).ToArray() }).ToArray();

            return new { success = true, duplicateGroups = duplicates.Length, groups = duplicates, note = "Comparison is approximate (color/texture similarity). Manual review recommended." };
        }

        [UnitySkill("optimize_analyze_overdraw", "Analyze transparent objects that may cause overdraw",
            Category = SkillCategory.Optimization, Operation = SkillOperation.Analyze,
            Tags = new[] { "optimization", "overdraw", "transparent", "rendering" },
            Outputs = new[] { "transparentObjectCount", "objects" },
            ReadOnly = true)]
        public static object OptimizeAnalyzeOverdraw(int limit = 50)
        {
            var renderers = FindHelper.FindAll<Renderer>();
            var transparent = new List<object>();

            foreach (var r in renderers)
            {
                if (transparent.Count >= limit) break;
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null && mat.renderQueue >= 2500)
                    {
                        transparent.Add(new { gameObject = r.name, path = GameObjectFinder.GetPath(r.gameObject), material = mat.name, renderQueue = mat.renderQueue, shader = mat.shader != null ? mat.shader.name : "null" });
                        break;
                    }
                }
            }

            return new { success = true, transparentObjectCount = transparent.Count, objects = transparent };
        }

        [UnitySkill("optimize_set_lod_group", "Add or configure LOD Group. lodDistances: comma-separated screen-relative heights (e.g. '0.6,0.3,0.1')", TracksWorkflow = true,
            Category = SkillCategory.Optimization, Operation = SkillOperation.Modify | SkillOperation.Create,
            Tags = new[] { "optimization", "lod", "level-of-detail", "performance" },
            Outputs = new[] { "gameObject", "lodLevels", "distances" },
            RequiresInput = new[] { "gameObject" })]
        public static object OptimizeSetLodGroup(string name = null, int instanceId = 0, string path = null, string lodDistances = "0.6,0.3,0.1")
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var distanceParts = lodDistances.Split(',');
            var distances = new List<float>();
            foreach (var part in distanceParts)
            {
                if (!float.TryParse(part.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dist))
                    return new { error = $"Invalid LOD distance value: '{part.Trim()}'" };
                distances.Add(dist);
            }
            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup == null)
                lodGroup = Undo.AddComponent<LODGroup>(go);
            else
                Undo.RecordObject(lodGroup, "Set LOD Group");

            var renderers = go.GetComponentsInChildren<Renderer>();
            var lods = new LOD[distances.Count + 1];
            for (int i = 0; i < distances.Count; i++)
                lods[i] = new LOD(distances[i], i == 0 ? renderers : new Renderer[0]);
            lods[distances.Count] = new LOD(0, new Renderer[0]);

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();

            return new { success = true, gameObject = go.name, lodLevels = lods.Length, distances = lodDistances };
        }
    }
}
