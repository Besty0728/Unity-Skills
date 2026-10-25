using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    public static partial class WorkflowManager
    {
        private const string LeafSeed = "leaf:";
        private const string DiskSeed = "disk";
        private const string SettingSeed = "setting";
        private const string UnknownBy = "unknown";
        private const string UserBy = "user";
        private const string FileChangedWhat = "file changed on disk";
        private const string MergeUndoLabel = "Workflow: merge restore";

        /// <summary>How a work runs once its target has been judged against the state its step left (<see cref="AdjudicateWorks"/>).</summary>
        private enum RestoreWorkMode
        {
            // The P1 behaviour: the state before the step is written back as recorded.
            Plain,
            // The target is in the state the step left, so the restore is clean; it runs as Plain.
            Clean,
            // The target is already in the state before the step, so there is nothing to undo.
            Already,
            // The target changed since the step: its properties are merged, and the changes the merge cannot settle are conflicts.
            Merge
        }

        /// <summary>
        /// Judges each planned work against the state its target is in now, and sets its <see cref="RestoreWork.Mode"/> (design section 6.4).
        /// A work the judgement cannot settle stays Plain, which is how P1 restored it. A judgement that settles a target removes its
        /// target-level conflicts; a merge replaces them with one conflict per property it cannot settle.
        /// </summary>
        private static void AdjudicateWorks(RestorePlanning planning, ISet<string> overwriteIds)
        {
            _restoreScopeAliases = planning.GidAliases;
            try
            {
                foreach (var work in planning.Works)
                {
                    if (work.TargetKey == null)
                        continue;

                    switch (work.Item.Snapshot.type)
                    {
                        case SnapshotType.Modified:
                            AdjudicateModified(work, planning, overwriteIds);
                            break;
                        case SnapshotType.Setting:
                            AdjudicateSetting(work, planning.Scan);
                            break;
                    }
                }
            }
            finally
            {
                _restoreScopeAliases = null;
                _sessionIdFallbackAllowed = true;
            }
            planning.Scan.Conflicts = planning.Scan.Conflicts.OrderBy(conflict => conflict.Seq).ToList();
        }

        private static void AdjudicateModified(RestoreWork work, RestorePlanning planning, ISet<string> overwriteIds)
        {
            var last = work.Chain[work.Chain.Count - 1];
            if (last.Snapshot.type != SnapshotType.Modified || work.PathOverride != null)
                return;

            var snapshot = work.Item.Snapshot;
            if (!string.IsNullOrEmpty(snapshot.assetPath) && !IsJsonAsset(snapshot.assetPath))
            {
                AdjudicateFile(work, last, planning.Scan);
                return;
            }

            UnityEngine.Object live = LiveObjectOf(work.Item);
            if (live != null)
                AdjudicateObject(work, last, live, planning, overwriteIds);
        }

        private static UnityEngine.Object LiveObjectOf(RestoreItem item)
        {
            _sessionIdFallbackAllowed = IsCapturedInCurrentSession(item);
            return TryResolveObject(item.Snapshot.globalObjectId, item.Snapshot.entityId, item.Snapshot.objectInstanceId);
        }

        // Unity serializes a JSON asset itself (a material, a ScriptableObject): such an asset has no importer of its own, so the
        // editor hands back the base AssetImporter for it. Every other asset is a file with a specific importer.
        private static bool IsJsonAsset(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            return importer == null || importer.GetType() == typeof(AssetImporter);
        }

        /// <summary>
        /// A scene object, component or JSON asset, judged by its state hashes. Its state is the one the newest step left: the work is clean
        /// when the target is still in it, already restored when the target is back at the state before the step, and merged otherwise.
        /// </summary>
        private static void AdjudicateObject(RestoreWork work, RestoreItem last, UnityEngine.Object live, RestorePlanning planning,
            ISet<string> overwriteIds)
        {
            var item = work.Item.Snapshot;
            var lastSnapshot = last.Snapshot;
            // The payload fields (the JSON, the references and their captured flags) are read back first; the header's hash comes first.
            if (string.IsNullOrEmpty(lastSnapshot.afterStateHash))
                return;
            if (!TryHydrate(item, out _) || !TryHydrate(lastSnapshot, out _) || item.originalJson == null || lastSnapshot.afterJson == null)
                return;
            if (!item.objectReferencesCaptured || !lastSnapshot.afterObjectReferencesCaptured)
                return;

            var currentRefs = CaptureObjectReferences(live, out bool currentRefsCaptured);
            if (!currentRefsCaptured)
                return;
            string currentJson = EditorJsonUtility.ToJson(live);
            string currentHash = WorkflowStateHash.StateHash(currentJson, currentRefs);

            if (currentHash == lastSnapshot.afterStateHash)
            {
                work.Mode = RestoreWorkMode.Clean;
                planning.Scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == work.TargetKey);
                return;
            }
            if (currentHash == WorkflowStateHash.StateHash(item.originalJson, item.objectReferences))
            {
                // Restoring changes nothing here, so no change since the step can be overwritten.
                work.Mode = RestoreWorkMode.Already;
                planning.Scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == work.TargetKey);
                return;
            }

            MergeWork(work, last, currentJson, currentRefs, planning, overwriteIds);
        }

        /// <summary>
        /// Merges the before state of the work into the current state. The conflicting properties replace the target-level conflicts as
        /// leaf conflicts, attributed to the change that made the target conflict, or to "unknown" when there was none. A property the
        /// overwrite set names is written back to its before value; the others are kept, and reported as kept.
        /// </summary>
        private static void MergeWork(RestoreWork work, RestoreItem last, string currentJson, List<ObjectReferenceData> currentRefs,
            RestorePlanning planning, ISet<string> overwriteIds)
        {
            var item = work.Item.Snapshot;
            var lastSnapshot = last.Snapshot;

            // Which properties conflict does not depend on what is overwritten: a first pass finds them, so the overwrite set can be matched by id.
            var noOverwrite = new HashSet<string>(StringComparer.Ordinal);
            var probe = WorkflowJsonMerge.Merge(item.originalJson, lastSnapshot.afterJson, currentJson, noOverwrite);
            var conflictPaths = new List<string>(probe.ConflictPaths);
            WorkflowJsonMerge.MergeReferences(item.objectReferences, lastSnapshot.afterObjectReferences, currentRefs, noOverwrite,
                conflictPaths, out _);
            var attribution = planning.Scan.Conflicts.FirstOrDefault(conflict => conflict.TargetKey == work.TargetKey && !conflict.IsLeaf);
            // Only the caller itself changed the target since (untracked writes): the restore overwrites them, as P1 did.
            bool ownChangesOnly = attribution == null && planning.Scan.OwnChangeKeys.Contains(work.TargetKey);
            var overwrite = new HashSet<string>(
                conflictPaths.Where(path => ownChangesOnly || overwriteIds.Contains(LeafConflictId(work.TargetKey, path))), StringComparer.Ordinal);

            var merge = WorkflowJsonMerge.Merge(item.originalJson, lastSnapshot.afterJson, currentJson, overwrite);
            var references = WorkflowJsonMerge.MergeReferences(item.objectReferences, lastSnapshot.afterObjectReferences, currentRefs,
                overwrite, merge.ConflictPaths, out _);

            planning.Scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == work.TargetKey && !conflict.IsLeaf);
            long seq = attribution?.Seq ?? last.Step.seq;

            var kept = new List<RestoreConflict>();
            foreach (string path in ownChangesOnly ? Enumerable.Empty<string>() : merge.ConflictPaths)
            {
                var leaf = new RestoreConflict
                {
                    id = LeafConflictId(work.TargetKey, path),
                    target = work.TargetDisplay + " " + path,
                    by = attribution?.by ?? UnknownBy,
                    what = ShortenValue(path + " " + CurrentValueText(merge, currentRefs, path)),
                    at = attribution?.at,
                    confidence = attribution?.confidence,
                    TargetKey = work.TargetKey,
                    Seq = seq,
                    IsLeaf = true
                };
                planning.Scan.Conflicts.Add(leaf);
                if (!overwrite.Contains(path))
                    kept.Add(leaf);
            }

            work.Mode = RestoreWorkMode.Merge;
            work.MergedJson = merge.MergedJson;
            work.MergedReferences = references;
            work.KeptLeaves = kept;
        }

        private static string CurrentValueText(JsonMergeResult merge, List<ObjectReferenceData> currentRefs, string conflictPath)
        {
            if (merge.ConflictValues.TryGetValue(conflictPath, out string value))
                return value;

            string propertyPath = conflictPath.Substring(WorkflowJsonMerge.ReferenceConflictPrefix.Length);
            var reference = currentRefs.FirstOrDefault(candidate => candidate?.propertyPath == propertyPath);
            return reference == null ? "null" : WorkflowStateHash.ReferenceIdentity(reference);
        }

        /// <summary>
        /// A file Unity does not re-save: its bytes are the judge. Its fingerprint is the one the newest step took, so a file that changed
        /// since is a conflict the user answers, rather than one a restore overwrites silently.
        /// </summary>
        private static void AdjudicateFile(RestoreWork work, RestoreItem last, RestoreConflictScan scan)
        {
            string fingerprint = AssetFingerprint(work.Item.Snapshot.assetPath);
            if (fingerprint == null || string.IsNullOrEmpty(last.Snapshot.assetFingerprint))
                return;
            if (last.Snapshot.assetFingerprint == fingerprint)
            {
                work.Mode = RestoreWorkMode.Clean;
                return;
            }
            if (scan.Conflicts.Any(conflict => conflict.TargetKey == work.TargetKey))
                return;

            scan.Conflicts.Add(new RestoreConflict
            {
                id = ConflictId(work.TargetKey, DiskSeed),
                target = work.TargetDisplay,
                by = UserBy,
                what = FileChangedWhat,
                TargetKey = work.TargetKey,
                Seq = last.Step.seq
            });
        }

        /// <summary>
        /// A setting is judged by its value. An unchanged value is clean, one back at its before value is already restored, and any other
        /// value is a conflict the user answers, unless the scan already reported one for the setting.
        /// </summary>
        private static void AdjudicateSetting(RestoreWork work, RestoreConflictScan scan)
        {
            var item = work.Item.Snapshot;
            var last = work.Chain[work.Chain.Count - 1];
            if (last.Snapshot.type != SnapshotType.Setting || !TryHydrate(item, out _) || !TryHydrate(last.Snapshot, out _))
                return;

            string current = WorkflowSettingRestorerRegistry.TryGetCurrentValue(item.settingKey);
            if (current == null)
                return;
            if (current == last.Snapshot.settingAfterValueJson)
            {
                work.Mode = RestoreWorkMode.Clean;
                scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == work.TargetKey);
                return;
            }
            if (current == item.settingOldValueJson)
            {
                work.Mode = RestoreWorkMode.Already;
                scan.Conflicts.RemoveAll(conflict => conflict.TargetKey == work.TargetKey);
                return;
            }
            if (scan.Conflicts.Any(conflict => conflict.TargetKey == work.TargetKey))
                return;

            scan.Conflicts.Add(new RestoreConflict
            {
                id = ConflictId(work.TargetKey, SettingSeed),
                target = work.TargetDisplay,
                by = UnknownBy,
                what = ShortenValue(current),
                TargetKey = work.TargetKey,
                Seq = last.Step.seq
            });
        }

        private static string LeafConflictId(string targetKey, string propertyPath) => ConflictId(targetKey, LeafSeed + propertyPath);

        private static void RunMergeWork(RestoreWork work, WorkflowTask restoreTask, RestoreOutcome outcome)
        {
            var snapshot = work.Item.Snapshot;
            UnityEngine.Object live = LiveObjectOf(work.Item);
            if (live == null)
            {
                RecordRestoreFailure(outcome, snapshot, "object no longer exists");
                return;
            }

            if (RestoreMergedSnapshot(snapshot, live, work.MergedJson, work.MergedReferences, restoreTask))
                outcome.merged++;
            else
                RecordRestoreFailure(outcome, snapshot, "the current state could not be captured");
        }

        /// <summary>
        /// Applies a merge to the live object. The inverse of the restore is captured first, as <see cref="RestoreModifiedSnapshot"/> captures it;
        /// then the merged JSON and references are written onto the object, and an asset is saved.
        /// </summary>
        private static bool RestoreMergedSnapshot(ObjectSnapshot snapshot, UnityEngine.Object live, string mergedJson,
            List<ObjectReferenceData> mergedReferences, WorkflowTask restoreTask)
        {
            if (!CaptureInverseModified(snapshot, live, restoreTask, out _))
                return false;

            Undo.RecordObject(live, MergeUndoLabel);
            EditorJsonUtility.FromJsonOverwrite(mergedJson, live);
            RestoreObjectReferences(live, true, mergedReferences, null);
            EditorUtility.SetDirty(live);
            if (!string.IsNullOrEmpty(snapshot.assetPath))
                AssetDatabase.SaveAssetIfDirty(live);
            return true;
        }
    }
}

// Producer:Betsy
