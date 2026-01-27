using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;

namespace UnitySkills
{
    /// <summary>
    /// Profiler skills - FPS, memory, stats.
    /// </summary>
    public static class ProfilerSkills
    {
        [UnitySkill("profiler_get_stats", "Get performance statistics (FPS, Memory, Batches)")]
        public static object ProfilerGetStats()
        {
            // Note: UnityStats is only accurate in the Game View when it is visible
            
            long totalAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            long totalReservedMemory = Profiler.GetTotalReservedMemoryLong();
            long totalUnusedReservedMemory = Profiler.GetTotalUnusedReservedMemoryLong();
            
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
        
        // Helper to access internal TextureUtil via reflection if public API is missing in older versions,
        // but TextureUtil is generally public in Editor namespace in recent versions? 
        // Actually UnityEditor.TextureUtil is internal.
        // We might need to handle that if compilation fails. 
        // Let's assume for now we use Profiler.supported which returns bool.
        // Wait, TextureUtil is internal. I should use ProfilerRecorder or safe public APIs.
        // But "UnityStats" is fine.
        // Texture memory: System.GC.GetTotalMemory(false) is C# heap.
        // Let's rely on standard Profiler calls.
    }
}

// Internal helper for TextureUtil if needed, but for simplicity let's stick to safe Profiler API.
// Actually, UnityEditor.UnityStats provides render stats.
// UnityEngine.Profiling.Profiler provides memory stats.
