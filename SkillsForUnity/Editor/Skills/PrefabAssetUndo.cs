using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills.Internal
{
    /// <summary>Undo serializes asset bytes, rather than disposable LoadPrefabContents objects.</summary>
    internal sealed class PrefabAssetUndo : ScriptableObject
    {
        [SerializeField] private string assetPath;
        [SerializeField] private string fileBytes;
        [SerializeField] private string metaBytes;
        [NonSerialized] private string appliedBytes;
        [NonSerialized] private string appliedMeta;
        private static readonly List<PrefabAssetUndo> Records = new List<PrefabAssetUndo>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            Records.Clear();
            Records.AddRange(Resources.FindObjectsOfTypeAll<PrefabAssetUndo>());
            foreach (var record in Records) { record.appliedBytes = record.fileBytes; record.appliedMeta = record.metaBytes; }
            Undo.undoRedoPerformed -= RestoreChangedRecords;
            Undo.undoRedoPerformed += RestoreChangedRecords;
        }

        internal static void Record(string path, byte[] before, byte[] metaBefore, byte[] after, byte[] metaAfter)
        {
            if (Convert.ToBase64String(before) == Convert.ToBase64String(after) &&
                (metaBefore == null ? null : Convert.ToBase64String(metaBefore)) == (metaAfter == null ? null : Convert.ToBase64String(metaAfter))) return;
            Records.RemoveAll(r => r == null);
            var record = Records.FirstOrDefault(r => r.assetPath == path) ?? CreateInstance<PrefabAssetUndo>();
            record.hideFlags = HideFlags.HideAndDontSave;
            record.assetPath = path;
            record.fileBytes = Convert.ToBase64String(before);
            record.metaBytes = metaBefore == null ? null : Convert.ToBase64String(metaBefore);
            Undo.RegisterCompleteObjectUndo(record, "Edit Prefab Asset");
            record.fileBytes = Convert.ToBase64String(after);
            record.metaBytes = metaAfter == null ? null : Convert.ToBase64String(metaAfter);
            record.appliedBytes = record.fileBytes; record.appliedMeta = record.metaBytes;
            if (!Records.Contains(record)) Records.Add(record);
        }

        private static void RestoreChangedRecords()
        {
            Records.RemoveAll(r => r == null);
            // One proxy per asset lets Unity keep the earliest before-state and latest after-state in each group.
            var changed = new Dictionary<string, PrefabAssetUndo>(StringComparer.Ordinal);
            foreach (var record in Records)
                if (record.fileBytes != record.appliedBytes || record.metaBytes != record.appliedMeta)
                    changed[record.assetPath] = record;
            foreach (var pair in changed)
            {
                var record = pair.Value;
                try
                {
                    if (!WorkflowFileStore.TryGetSafeAssetFullPath(record.assetPath, out var fullPath)) continue;
                    File.WriteAllBytes(fullPath, Convert.FromBase64String(record.fileBytes));
                    if (record.metaBytes != null) File.WriteAllBytes(fullPath + ".meta", Convert.FromBase64String(record.metaBytes));
                    AssetDatabase.ImportAsset(record.assetPath, ImportAssetOptions.ForceUpdate);
                }
                catch (Exception ex) { SkillsLogger.LogError($"{SkillsLogger.PREFIX_ERROR} Prefab Undo failed: {ex.Message}"); }
            }
            foreach (var record in Records) { record.appliedBytes = record.fileBytes; record.appliedMeta = record.metaBytes; }
        }
    }
}

// Producer:Betsy
