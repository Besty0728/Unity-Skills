using System.Threading;
using UnityEditor;

namespace UnitySkills.Internal
{
    /// <summary>
    /// The first check of every editor entry point in this package: <c>if (ImportWorkerGate.IsWorkerProcess) return;</c>.
    /// <para>Unity's AssetImportWorker processes load the editor assemblies against the same project and Library folder,
    /// so [InitializeOnLoad] constructors, [InitializeOnLoadMethod]s and asset postprocessors run there too, once per
    /// worker domain reload. A worker serves no request and owns no state. Left ungated, it reads files the main editor is
    /// rewriting, appends to its JSONL logs under seq counters of its own, saves a stale job table over the editor's,
    /// resumes jobs, and writes prefs, manifests and defines. ImportWorkerGateTests keeps every entry point gated.</para>
    /// </summary>
    internal static class ImportWorkerGate
    {
        /// <summary>Test seam: forces <see cref="IsWorkerProcess"/>; null asks Unity.</summary>
        internal static bool? OverrideForTests;

        // 0 = not resolved yet, 1 = main editor, 2 = asset import worker.
        private static int _role;

        /// <summary>
        /// True inside an AssetImportWorker process. Resolved once through Unity by the first caller, an entry point on the
        /// main thread; the cached answer is safe to read from any thread afterwards.
        /// </summary>
        internal static bool IsWorkerProcess
        {
            get
            {
                if (OverrideForTests.HasValue)
                    return OverrideForTests.Value;

                int role = Volatile.Read(ref _role);
                if (role == 0)
                {
                    // Entry points read this outside their try blocks, and a throw from a static constructor would
                    // disable its type for the whole domain: an unanswerable question counts as the main editor.
                    try { role = AssetDatabase.IsAssetImportWorkerProcess() ? 2 : 1; }
                    catch (System.Exception) { role = 1; }
                    Volatile.Write(ref _role, role);
                }
                return role == 2;
            }
        }
    }
}

// Producer:Betsy
