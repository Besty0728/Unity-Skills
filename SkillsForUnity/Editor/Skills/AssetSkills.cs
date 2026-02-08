using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Asset management skills - import, create, delete, search.
    /// </summary>
    public static class AssetSkills
    {
        [UnitySkill("asset_import", "Import an asset from external path")]
        public static object AssetImport(string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                return new { error = $"Source not found: {sourcePath}" };

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.Copy(sourcePath, destinationPath, true);
            AssetDatabase.ImportAsset(destinationPath);

            // 记录新创建的资产
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);

            return new { success = true, imported = destinationPath };
        }

        [UnitySkill("asset_delete", "Delete an asset")]
        public static object AssetDelete(string assetPath)
        {
            if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                return new { error = $"Asset not found: {assetPath}" };

            // 删除前记录资产状态（用于恢复）
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            AssetDatabase.DeleteAsset(assetPath);
            return new { success = true, deleted = assetPath };
        }

        [UnitySkill("asset_move", "Move or rename an asset")]
        public static object AssetMove(string sourcePath, string destinationPath)
        {
            // 移动前记录资产状态
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(sourcePath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            return new { success = true, from = sourcePath, to = destinationPath };
        }

        [UnitySkill("asset_import_batch", "Import multiple assets. items: JSON array of {sourcePath, destinationPath}")]
        public static object AssetImportBatch(string items)
        {
            if (string.IsNullOrEmpty(items)) return new { error = "items parameter is required." };
            try {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<BatchImportItem>>(items);
                if (itemList == null || itemList.Count == 0) return new { error = "items empty" };
                
                var results = new System.Collections.Generic.List<object>();
                int successCount = 0;
                
                AssetDatabase.StartAssetEditing();
                try {
                    foreach (var item in itemList) {
                        try {
                            if (!File.Exists(item.sourcePath)) {
                                results.Add(new { target = item.sourcePath, success = false, error = "File not found" });
                                continue;
                            }
                            var dir = Path.GetDirectoryName(item.destinationPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                            File.Copy(item.sourcePath, item.destinationPath, true);
                            AssetDatabase.ImportAsset(item.destinationPath);
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.destinationPath);
                            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
                            results.Add(new { target = item.destinationPath, success = true });
                            successCount++;
                        } catch (System.Exception ex) {
                            results.Add(new { target = item.sourcePath, success = false, error = ex.Message });
                        }
                    }
                } finally {
                    AssetDatabase.StopAssetEditing();
                }
                AssetDatabase.Refresh();
                
                return new { success = true, total = itemList.Count, successCount, results };
            } catch (System.Exception ex) { return new { error = ex.Message }; }
        }

        private class BatchImportItem { public string sourcePath; public string destinationPath; }

        [UnitySkill("asset_delete_batch", "Delete multiple assets. items: JSON array of {path}")]
        public static object AssetDeleteBatch(string items)
        {
            if (string.IsNullOrEmpty(items)) return new { error = "items parameter is required." };
            try {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<BatchDeleteItem>>(items);
                if (itemList == null || itemList.Count == 0) return new { error = "items empty" };
                
                var results = new System.Collections.Generic.List<object>();
                int successCount = 0;
                
                AssetDatabase.StartAssetEditing();
                try {
                    foreach (var item in itemList) {
                        try {
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.path);
                            if (asset != null) WorkflowManager.SnapshotObject(asset);
                            if (AssetDatabase.DeleteAsset(item.path)) {
                                results.Add(new { target = item.path, success = true });
                                successCount++;
                            } else {
                                results.Add(new { target = item.path, success = false, error = "Delete failed" });
                            }
                        } catch (System.Exception ex) {
                            results.Add(new { target = item.path, success = false, error = ex.Message });
                        }
                    }
                } finally {
                    AssetDatabase.StopAssetEditing();
                }
                AssetDatabase.Refresh();
                
                return new { success = true, total = itemList.Count, successCount, results };
            } catch (System.Exception ex) { return new { error = ex.Message }; }
        }

        private class BatchDeleteItem { public string path; }

        [UnitySkill("asset_move_batch", "Move multiple assets. items: JSON array of {sourcePath, destinationPath}")]
        public static object AssetMoveBatch(string items)
        {
            if (string.IsNullOrEmpty(items)) return new { error = "items parameter is required." };
            try {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<BatchMoveItem>>(items);
                if (itemList == null || itemList.Count == 0) return new { error = "items empty" };
                
                var results = new System.Collections.Generic.List<object>();
                int successCount = 0;
                
                AssetDatabase.StartAssetEditing();
                try {
                    foreach (var item in itemList) {
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.sourcePath);
                        if (asset != null) WorkflowManager.SnapshotObject(asset);
                        string error = AssetDatabase.MoveAsset(item.sourcePath, item.destinationPath);
                        if (string.IsNullOrEmpty(error)) {
                             results.Add(new { target = item.sourcePath, success = true, from = item.sourcePath, to = item.destinationPath });
                             successCount++;
                        } else {
                             results.Add(new { target = item.sourcePath, success = false, error });
                        }
                    }
                } finally {
                    AssetDatabase.StopAssetEditing();
                }
                AssetDatabase.Refresh();
                
                return new { success = true, total = itemList.Count, successCount, results };
            } catch (System.Exception ex) { return new { error = ex.Message }; }
        }

        private class BatchMoveItem { public string sourcePath; public string destinationPath; }

        [UnitySkill("asset_duplicate", "Duplicate an asset")]
        public static object AssetDuplicate(string assetPath)
        {
            var newPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CopyAsset(assetPath, newPath);

            // 记录新创建的资产
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(newPath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);

            return new { success = true, original = assetPath, copy = newPath };
        }

        [UnitySkill("asset_find", "Find assets by name, type, or label")]
        public static object AssetFind(string searchFilter, int limit = 50)
        {
            var guids = AssetDatabase.FindAssets(searchFilter);
            var results = guids.Take(limit).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                return new
                {
                    path,
                    name = asset?.name,
                    type = asset?.GetType().Name
                };
            }).ToArray();

            return new { count = results.Length, totalFound = guids.Length, assets = results };
        }

        [UnitySkill("asset_create_folder", "Create a new folder in Assets")]
        public static object AssetCreateFolder(string folderPath)
        {
            if (Directory.Exists(folderPath))
                return new { error = "Folder already exists" };

            var parent = Path.GetDirectoryName(folderPath);
            var name = Path.GetFileName(folderPath);

            var guid = AssetDatabase.CreateFolder(parent, name);
            return new { success = true, path = folderPath, guid };
        }

        [UnitySkill("asset_refresh", "Refresh the Asset Database")]
        public static object AssetRefresh()
        {
            AssetDatabase.Refresh();
            return new { success = true, message = "Asset database refreshed" };
        }

        [UnitySkill("asset_get_info", "Get information about an asset")]
        public static object AssetGetInfo(string assetPath)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return new { error = $"Asset not found: {assetPath}" };

            return new
            {
                path = assetPath,
                name = asset.name,
                type = asset.GetType().Name,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                labels = AssetDatabase.GetLabels(asset)
            };
        }
    }
}
