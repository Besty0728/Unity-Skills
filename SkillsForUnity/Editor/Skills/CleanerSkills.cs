using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace UnitySkills
{
    /// <summary>
    /// Cleaner skills - find unused assets, duplicates, and missing references.
    /// </summary>
    public static class CleanerSkills
    {
        [UnitySkill("cleaner_find_unused_assets", "Find potentially unused assets of a specific type")]
        public static object CleanerFindUnusedAssets(
            string assetType = "Material",
            string searchPath = "Assets",
            int limit = 100)
        {
            if (Validate.SafePath(searchPath, "searchPath") is object pathErr) return pathErr;

            var filter = $"t:{assetType}";
            var guids = AssetDatabase.FindAssets(filter, new[] { searchPath });
            var potentiallyUnused = new List<object>();

            var candidatePaths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToArray();
            var candidateSet = new HashSet<string>(candidatePaths, System.StringComparer.OrdinalIgnoreCase);
            var referencedCandidates = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var assetGuid in AssetDatabase.FindAssets("t:Object", new[] { searchPath }))
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(assetPath) || assetPath.Contains("/Resources/"))
                    continue;

                foreach (var dependency in AssetDatabase.GetDependencies(assetPath, true))
                {
                    if (dependency == assetPath)
                        continue;

                    if (candidateSet.Contains(dependency))
                        referencedCandidates.Add(dependency);
                }
            }

            foreach (var path in candidatePaths)
            {
                if (potentiallyUnused.Count >= limit) break;

                if (path.Contains("/Resources/") || referencedCandidates.Contains(path))
                    continue;

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                var fileInfo = new FileInfo(path);
                potentiallyUnused.Add(new
                {
                    path,
                    name = asset?.name,
                    type = asset?.GetType().Name,
                    sizeBytes = fileInfo.Exists ? fileInfo.Length : 0
                });
            }

            return new
            {
                success = true,
                assetType,
                searchPath,
                potentiallyUnusedCount = potentiallyUnused.Count,
                note = "Assets may still be used via Resources.Load or Addressables",
                assets = potentiallyUnused
            };
        }

        [UnitySkill("cleaner_find_duplicates", "Find duplicate files by content hash")]
        public static object CleanerFindDuplicates(
            string assetType = "Texture2D",
            string searchPath = "Assets",
            int limit = 50)
        {
            var filter = $"t:{assetType}";
            var guids = AssetDatabase.FindAssets(filter, new[] { searchPath });
            
            // Group by file size first (fast filter)
            var sizeGroups = new Dictionary<long, List<string>>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists) continue;

                var size = fileInfo.Length;
                if (!sizeGroups.ContainsKey(size))
                    sizeGroups[size] = new List<string>();
                sizeGroups[size].Add(path);
            }

            // Only check files with same size
            var duplicateGroups = new List<object>();
            using (var md5 = MD5.Create())
            {
                foreach (var group in sizeGroups.Values.Where(g => g.Count > 1))
                {
                    if (duplicateGroups.Count >= limit) break;

                    var hashGroups = new Dictionary<string, List<string>>();
                    foreach (var path in group)
                    {
                        try
                        {
                            using (var stream = File.OpenRead(path))
                            {
                                var hash = System.BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");
                                if (!hashGroups.ContainsKey(hash))
                                    hashGroups[hash] = new List<string>();
                                hashGroups[hash].Add(path);
                            }
                        }
                        catch { }
                    }

                    foreach (var hashGroup in hashGroups.Values.Where(g => g.Count > 1))
                    {
                        var fileInfo = new FileInfo(hashGroup[0]);
                        duplicateGroups.Add(new
                        {
                            count = hashGroup.Count,
                            sizeBytes = fileInfo.Length,
                            wastedBytes = fileInfo.Length * (hashGroup.Count - 1),
                            files = hashGroup
                        });
                    }
                }
            }

            var totalWasted = duplicateGroups.Sum(d => (long)((dynamic)d).wastedBytes);

            return new
            {
                success = true,
                assetType,
                duplicateGroupCount = duplicateGroups.Count,
                totalWastedBytes = totalWasted,
                totalWastedMB = totalWasted / (1024.0 * 1024.0),
                groups = duplicateGroups
            };
        }

        [UnitySkill("cleaner_find_missing_references", "Find components with missing script or asset references")]
        public static object CleanerFindMissingReferences(bool includeInactive = true)
        {
            var issues = new List<object>();
            var allObjects = includeInactive 
                ? Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(go => !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None)
                    .ToArray()
                : FindHelper.FindAll<GameObject>();

            foreach (var go in allObjects)
            {
                // Check for missing scripts
                var components = go.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == null)
                    {
                        issues.Add(new
                        {
                            type = "MissingScript",
                            gameObject = go.name,
                            path = GameObjectFinder.GetPath(go),
                            componentIndex = i
                        });
                    }
                }

                // Check serialized properties for missing references
                foreach (var component in components.Where(c => c != null))
                {
                    var so = new SerializedObject(component);
                    var prop = so.GetIterator();
                    while (prop.NextVisible(true))
                    {
                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (prop.objectReferenceValue == null && prop.objectReferenceInstanceIDValue != 0)
                            {
                                issues.Add(new
                                {
                                    type = "MissingReference",
                                    gameObject = go.name,
                                    path = GameObjectFinder.GetPath(go),
                                    component = component.GetType().Name,
                                    property = prop.propertyPath
                                });
                            }
                        }
                    }
                }
            }

            return new
            {
                success = true,
                issueCount = issues.Count,
                missingScripts = issues.Count(i => ((dynamic)i).type == "MissingScript"),
                missingReferences = issues.Count(i => ((dynamic)i).type == "MissingReference"),
                issues
            };
        }

        // Store pending delete operations for confirmation
        private static Dictionary<string, PendingDeleteOperation> _pendingDeletes = new Dictionary<string, PendingDeleteOperation>();

        private class PendingDeleteOperation
        {
            public string[] Paths;
            public System.DateTime CreatedAt;
            public long TotalBytes;
        }

        [UnitySkill("cleaner_delete_assets", "Delete specified assets. Step 1: Call without confirmToken to preview. Step 2: Call with confirmToken to execute.", TracksWorkflow = true)]
        public static object CleanerDeleteAssets(
            string[] paths = null,
            string confirmToken = null)
        {
            // Step 2: Execute deletion with confirmation token
            if (!string.IsNullOrEmpty(confirmToken))
            {
                if (!_pendingDeletes.TryGetValue(confirmToken, out var pending))
                {
                    return new { success = false, error = "Invalid or expired confirmToken. Please call again without confirmToken to get a new preview." };
                }

                // Check if token is expired (5 minutes)
                if ((System.DateTime.Now - pending.CreatedAt).TotalMinutes > 5)
                {
                    _pendingDeletes.Remove(confirmToken);
                    return new { success = false, error = "confirmToken expired. Please call again without confirmToken to get a new preview." };
                }

                var deletedResults = new List<object>();
                int deletedCount = 0;
                foreach (var path in pending.Paths)
                {
                    if (Validate.SafePath(path, "path", isDelete: true) is object pathErr)
                    {
                        deletedResults.Add(new { path, deleted = false });
                        continue;
                    }

                    var existed = File.Exists(path) || Directory.Exists(path);
                    if (existed)
                    {
                        // Workflow snapshot comment normalized.
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                        if (asset != null) WorkflowManager.SnapshotObject(asset);
                        AssetDatabase.DeleteAsset(path);
                        deletedCount++;
                    }
                    deletedResults.Add(new { path, deleted = existed });
                }

                _pendingDeletes.Remove(confirmToken);
                AssetDatabase.Refresh();

                return new
                {
                    success = true,
                    action = "deleted",
                    deletedCount,
                    totalMB = pending.TotalBytes / (1024.0 * 1024.0),
                    message = $"Successfully deleted {deletedCount} assets",
                    results = deletedResults
                };
            }

            // Step 1: Preview and generate confirmation token
            if (paths == null || paths.Length == 0)
                return new { success = false, error = "No paths provided. Provide paths array to preview deletion." };

            var previewResults = new List<object>();
            long totalBytes = 0;

            foreach (var path in paths)
            {
                if (Validate.SafePath(path, "path", isDelete: true) is object pathErr)
                {
                    previewResults.Add(new
                    {
                        path,
                        exists = false,
                        sizeBytes = 0L,
                        sizeMB = 0d,
                        error = ((dynamic)pathErr).error
                    });
                    continue;
                }

                var fileInfo = new FileInfo(path);
                var exists = File.Exists(path) || Directory.Exists(path);
                var size = fileInfo.Exists ? fileInfo.Length : 0;

                if (exists) totalBytes += size;

                previewResults.Add(new
                {
                    path,
                    exists,
                    sizeBytes = size,
                    sizeMB = size / (1024.0 * 1024.0)
                });
            }

            // Generate confirmation token
            var token = System.Guid.NewGuid().ToString("N").Substring(0, 8);
            _pendingDeletes[token] = new PendingDeleteOperation
            {
                Paths = paths,
                CreatedAt = System.DateTime.Now,
                TotalBytes = totalBytes
            };

            // Clean up old tokens
            var expiredTokens = _pendingDeletes.Where(kv => (System.DateTime.Now - kv.Value.CreatedAt).TotalMinutes > 5).Select(kv => kv.Key).ToList();
            foreach (var expired in expiredTokens) _pendingDeletes.Remove(expired);

            return new
            {
                success = true,
                action = "preview",
                totalAssets = paths.Length,
                existingAssets = previewResults.Count(r => ((dynamic)r).exists),
                totalBytes,
                totalMB = totalBytes / (1024.0 * 1024.0),
                confirmToken = token,
                message = $"⚠️ PREVIEW ONLY - {previewResults.Count(r => ((dynamic)r).exists)} assets will be deleted ({totalBytes / (1024.0 * 1024.0):F2} MB). To confirm, call again with confirmToken='{token}'",
                expiresIn = "5 minutes",
                assetsToDelete = previewResults
            };
        }

        [UnitySkill("cleaner_get_asset_usage", "Find what objects reference a specific asset")]
        public static object CleanerGetAssetUsage(string assetPath, int limit = 50)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            if (!File.Exists(assetPath))
                return new { success = false, error = $"Asset not found: {assetPath}" };

            var usedBy = new List<object>();
            var allAssetGuids = AssetDatabase.FindAssets("t:Object");

            foreach (var guid in allAssetGuids)
            {
                if (usedBy.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == assetPath) continue;

                var deps = AssetDatabase.GetDependencies(path, false);
                if (deps.Contains(assetPath))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    usedBy.Add(new
                    {
                        path,
                        name = asset?.name,
                        type = asset?.GetType().Name
                    });
                }
            }

            var targetAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);

            return new
            {
                success = true,
                asset = new
                {
                    path = assetPath,
                    name = targetAsset?.name,
                    type = targetAsset?.GetType().Name
                },
                usedByCount = usedBy.Count,
                usedBy
            };
        }

        [UnitySkill("cleaner_find_empty_folders", "Find empty folders in the project")]
        public static object CleanerFindEmptyFolders(string searchPath = "Assets")
        {
            var empty = new List<string>();
            FindEmptyFoldersRecursive(searchPath, empty);
            return new { success = true, count = empty.Count, folders = empty };
        }

        private static bool FindEmptyFoldersRecursive(string path, List<string> results)
        {
            var dirs = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path).Where(f => !f.EndsWith(".meta")).ToArray();
            bool allSubEmpty = true;
            foreach (var dir in dirs)
                if (!FindEmptyFoldersRecursive(dir, results)) allSubEmpty = false;
            if (files.Length == 0 && (dirs.Length == 0 || allSubEmpty))
            { results.Add(path.Replace("\\", "/")); return true; }
            return false;
        }

        [UnitySkill("cleaner_find_large_assets", "Find largest assets by file size")]
        public static object CleanerFindLargeAssets(string searchPath = "Assets", int limit = 20, long minSizeBytes = 0)
        {
            var files = Directory.GetFiles(searchPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta"))
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Length > minSizeBytes)
                .OrderByDescending(fi => fi.Length)
                .Take(limit)
                .Select(fi =>
                {
                    var relativePath = fi.FullName.Replace("\\", "/");
                    var assetsIndex = relativePath.IndexOf("Assets/");
                    if (assetsIndex >= 0) relativePath = relativePath.Substring(assetsIndex);
                    return new { path = relativePath, sizeBytes = fi.Length, sizeMB = fi.Length / (1024.0 * 1024.0) };
                })
                .ToArray();
            return new { success = true, count = files.Length, assets = files };
        }

        [UnitySkill("cleaner_delete_empty_folders", "Delete all empty folders", TracksWorkflow = true)]
        public static object CleanerDeleteEmptyFolders(string searchPath = "Assets")
        {
            var empty = new List<string>();
            FindEmptyFoldersRecursive(searchPath, empty);
            int deleted = 0;
            foreach (var folder in empty.OrderByDescending(f => f.Length))
            {
                if (AssetDatabase.DeleteAsset(folder)) deleted++;
            }
            AssetDatabase.Refresh();
            return new { success = true, deleted, total = empty.Count };
        }

        [UnitySkill("cleaner_fix_missing_scripts", "Remove missing script components from GameObjects", TracksWorkflow = true)]
        public static object CleanerFixMissingScripts(bool includeInactive = true)
        {
            var allObjects = includeInactive
                ? Resources.FindObjectsOfTypeAll<GameObject>().Where(go => !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None).ToArray()
                : FindHelper.FindAll<GameObject>();
            int totalRemoved = 0;
            foreach (var go in allObjects)
            {
                int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (count > 0)
                {
                    Undo.RegisterCompleteObjectUndo(go, "Fix Missing Scripts");
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    totalRemoved += count;
                }
            }
            return new { success = true, removedComponents = totalRemoved };
        }

        [UnitySkill("cleaner_get_dependency_tree", "Get dependency tree for an asset")]
        public static object CleanerGetDependencyTree(string assetPath, bool recursive = true)
        {
            if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                return new { error = $"Asset not found: {assetPath}" };
            var deps = AssetDatabase.GetDependencies(assetPath, recursive)
                .Where(d => d != assetPath)
                .Select(d => new { path = d, type = AssetDatabase.LoadMainAssetAtPath(d)?.GetType().Name })
                .ToArray();
            return new { success = true, assetPath, dependencyCount = deps.Length, dependencies = deps };
        }

    }
}
