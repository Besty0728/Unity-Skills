using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Profiler skills - FPS, memory, stats.
    /// </summary>
    public static class ProfilerSkills
    {
        // 使用反射访问 UnityStats，因为它是内部 API，不同 Unity 版本属性可能不同
        private static readonly Type s_UnityStatsType =
            typeof(Editor).Assembly.GetType("UnityEditor.UnityStats");

        private static float? GetStatFloat(string name)
        {
            var prop = s_UnityStatsType?.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return null;
            try { return Convert.ToSingle(prop.GetValue(null)); }
            catch { return null; }
        }

        private static int GetStatInt(string name)
        {
            var prop = s_UnityStatsType?.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return -1;
            try { return Convert.ToInt32(prop.GetValue(null)); }
            catch { return -1; }
        }

        [UnitySkill("profiler_get_stats", "Get performance statistics (FPS, Memory, Batches)",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "fps", "memory", "batches", "performance" },
            Outputs = new[] { "fps", "frameTime", "triangles", "batches", "memory" },
            ReadOnly = true)]
        public static object ProfilerGetStats()
        {
            long totalAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            long totalReservedMemory = Profiler.GetTotalReservedMemoryLong();
            long totalUnusedReservedMemory = Profiler.GetTotalUnusedReservedMemoryLong();

            float? frameTime = GetStatFloat("frameTime");
            float? fps = frameTime.HasValue && frameTime.Value > 0 ? 1000f / frameTime.Value : null;

            int visibleSkinnedMeshes = 0;
            foreach (var smr in FindHelper.FindAll<SkinnedMeshRenderer>())
                if (smr.isVisible) visibleSkinnedMeshes++;

            int visibleAnimators = 0;
            foreach (var anim in FindHelper.FindAll<Animator>())
            {
                var renderer = anim.GetComponent<Renderer>();
                if (renderer != null && renderer.isVisible) visibleAnimators++;
            }

            return new
            {
                success = true,
                fps, frameTime,
                renderTime = GetStatFloat("renderTime"),
                triangles = GetStatInt("triangles"),
                vertices = GetStatInt("vertices"),
                batches = GetStatInt("batches"),
                setPassCalls = GetStatInt("setPassCalls"),
                drawCalls = GetStatInt("drawCalls"),
                dynamicBatchedDrawCalls = GetStatInt("dynamicBatchedDrawCalls"),
                staticBatchedDrawCalls = GetStatInt("staticBatchedDrawCalls"),
                instancedBatchedDrawCalls = GetStatInt("instancedBatchedDrawCalls"),
                visibleSkinnedMeshes, visibleAnimators,
                memory = new
                {
                    totalAllocatedMB = totalAllocatedMemory / (1024f * 1024f),
                    totalReservedMB = totalReservedMemory / (1024f * 1024f),
                    unusedReservedMB = totalUnusedReservedMemory / (1024f * 1024f),
                    monoHeapMB = Profiler.GetMonoHeapSizeLong() / (1024f * 1024f),
                    monoUsedMB = Profiler.GetMonoUsedSizeLong() / (1024f * 1024f)
                }
            };
        }

        [UnitySkill("profiler_get_memory", "Get memory usage overview (total allocated, reserved, mono heap)",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "heap", "allocated" },
            Outputs = new[] { "totalAllocatedMB", "totalReservedMB", "unusedReservedMB", "monoHeapMB", "monoUsedMB" },
            ReadOnly = true)]
        public static object ProfilerGetMemory()
        {
            return new
            {
                success = true,
                totalAllocatedMB = Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f),
                totalReservedMB = Profiler.GetTotalReservedMemoryLong() / (1024f * 1024f),
                unusedReservedMB = Profiler.GetTotalUnusedReservedMemoryLong() / (1024f * 1024f),
                monoHeapMB = Profiler.GetMonoHeapSizeLong() / (1024f * 1024f),
                monoUsedMB = Profiler.GetMonoUsedSizeLong() / (1024f * 1024f)
            };
        }

        [UnitySkill("profiler_get_runtime_memory", "Get top N objects by runtime memory usage in the scene",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "runtime", "objects" },
            Outputs = new[] { "totalTrackedMB", "showing", "objects" },
            ReadOnly = true)]
        public static object ProfilerGetRuntimeMemory(int limit = 20)
        {
            var allObjects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var items = new List<(string name, string type, long size)>();
            foreach (var obj in allObjects)
            {
                if (obj == null || obj.hideFlags.HasFlag(HideFlags.HideAndDontSave)) continue;
                long size = Profiler.GetRuntimeMemorySizeLong(obj);
                if (size > 1024) // > 1KB
                    items.Add((obj.name, obj.GetType().Name, size));
            }
            var top = items.OrderByDescending(i => i.size).Take(limit)
                .Select(i => new { name = i.name, type = i.type, sizeKB = i.size / 1024f }).ToArray();
            long totalMem = items.Sum(i => i.size);
            return new { success = true, totalTrackedMB = totalMem / (1024f * 1024f), showing = top.Length, objects = top };
        }

        [UnitySkill("profiler_get_texture_memory", "Get memory usage of all loaded textures",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "texture", "vram" },
            Outputs = new[] { "totalCount", "totalMB", "topTextures" },
            ReadOnly = true)]
        public static object ProfilerGetTextureMemory(int limit = 50)
        {
            var textures = Resources.FindObjectsOfTypeAll<Texture>();
            long total = 0;
            var items = new List<(long size, object info)>();
            foreach (var t in textures)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(t);
                total += size;
                items.Add((size, new { name = t.name, type = t.GetType().Name, sizeKB = size / 1024f,
                    width = t is Texture2D t2 ? t2.width : 0, height = t is Texture2D t3 ? t3.height : 0 }));
            }
            return new { success = true, totalCount = textures.Length, totalMB = total / (1024f * 1024f),
                topTextures = items.OrderByDescending(i => i.size).Take(limit).Select(i => i.info).ToArray() };
        }

        [UnitySkill("profiler_get_mesh_memory", "Get memory usage of all loaded meshes",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "mesh", "vertices" },
            Outputs = new[] { "totalCount", "totalMB", "topMeshes" },
            ReadOnly = true)]
        public static object ProfilerGetMeshMemory(int limit = 50)
        {
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            long total = 0;
            var items = new List<(long size, object info)>();
            foreach (var m in meshes)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(m);
                total += size;
                items.Add((size, new { name = m.name, sizeKB = size / 1024f, vertices = m.vertexCount, triangles = SkillsCommon.GetTriangleCount(m) }));
            }
            return new { success = true, totalCount = meshes.Length, totalMB = total / (1024f * 1024f),
                topMeshes = items.OrderByDescending(i => i.size).Take(limit).Select(i => i.info).ToArray() };
        }

        [UnitySkill("profiler_get_material_memory", "Get memory usage of all loaded materials",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "material", "shader" },
            Outputs = new[] { "totalCount", "totalMB", "topMaterials" },
            ReadOnly = true)]
        public static object ProfilerGetMaterialMemory(int limit = 50)
        {
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            long total = 0;
            var items = new List<(long size, object info)>();
            foreach (var m in materials)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(m);
                total += size;
                items.Add((size, new { name = m.name, shader = m.shader != null ? m.shader.name : "null", sizeKB = size / 1024f }));
            }
            return new { success = true, totalCount = materials.Length, totalMB = total / (1024f * 1024f),
                topMaterials = items.OrderByDescending(i => i.size).Take(limit).Select(i => i.info).ToArray() };
        }

        [UnitySkill("profiler_get_audio_memory", "Get memory usage of all loaded AudioClips",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "memory", "audio", "clips" },
            Outputs = new[] { "totalCount", "totalMB", "topClips" },
            ReadOnly = true)]
        public static object ProfilerGetAudioMemory(int limit = 50)
        {
            var clips = Resources.FindObjectsOfTypeAll<AudioClip>();
            long total = 0;
            var items = new List<(long size, object info)>();
            foreach (var c in clips)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(c);
                total += size;
                items.Add((size, new { name = c.name, sizeKB = size / 1024f, length = c.length, channels = c.channels, frequency = c.frequency }));
            }
            return new { success = true, totalCount = clips.Length, totalMB = total / (1024f * 1024f),
                topClips = items.OrderByDescending(i => i.size).Take(limit).Select(i => i.info).ToArray() };
        }

        [UnitySkill("profiler_get_object_count", "Count all loaded objects grouped by type",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "objects", "count", "types" },
            Outputs = new[] { "totalObjects", "topTypes" },
            ReadOnly = true)]
        public static object ProfilerGetObjectCount(int topN = 20)
        {
            var all = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var groups = all.GroupBy(o => o.GetType().Name)
                .Select(g => new { type = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count).Take(topN).ToArray();
            return new { success = true, totalObjects = all.Length, topTypes = groups };
        }

        [UnitySkill("profiler_get_rendering_stats", "Get rendering statistics (batches, triangles, vertices, etc.)",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "rendering", "drawcalls", "batches" },
            Outputs = new[] { "frameTime", "renderTime", "triangles", "vertices", "batches", "drawCalls" },
            ReadOnly = true)]
        public static object ProfilerGetRenderingStats()
        {
            return new
            {
                success = true,
                frameTime = GetStatFloat("frameTime"),
                renderTime = GetStatFloat("renderTime"),
                triangles = GetStatInt("triangles"),
                vertices = GetStatInt("vertices"),
                batches = GetStatInt("batches"),
                setPassCalls = GetStatInt("setPassCalls"),
                drawCalls = GetStatInt("drawCalls"),
                dynamicBatchedDrawCalls = GetStatInt("dynamicBatchedDrawCalls"),
                staticBatchedDrawCalls = GetStatInt("staticBatchedDrawCalls"),
                instancedBatchedDrawCalls = GetStatInt("instancedBatchedDrawCalls"),
                shadowCasters = GetStatInt("shadowCasters")
            };
        }

        [UnitySkill("profiler_get_asset_bundle_stats", "Get information about all loaded AssetBundles",
            Category = SkillCategory.Profiler, Operation = SkillOperation.Query,
            Tags = new[] { "profiler", "assetbundle", "loaded", "stats" },
            Outputs = new[] { "count", "bundles" },
            ReadOnly = true)]
        public static object ProfilerGetAssetBundleStats()
        {
            var bundles = AssetBundle.GetAllLoadedAssetBundles().ToArray();
            var info = bundles.Select(b => new { name = b.name, isStreamedSceneAssetBundle = b.isStreamedSceneAssetBundle }).ToArray();
            return new { success = true, count = bundles.Length, bundles = info };
        }
    }
}
