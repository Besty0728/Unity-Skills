using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Asset management skills - import, create, delete, search.
    /// </summary>
    public static class AssetSkills
    {
        [UnitySkill("asset_import", "Import an asset from external path",
            Category = SkillCategory.Asset, Operation = SkillOperation.Create,
            Tags = new[] { "import", "copy", "external" },
            Outputs = new[] { "imported", "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true, RiskLevel = "high")]
        public static object AssetImport(string sourcePath, string destinationPath)
        {
            bool isDir = Directory.Exists(sourcePath);
            if (!File.Exists(sourcePath) && !isDir)
                return new { error = $"Source not found: {sourcePath}" };
            if (isDir)
                return new { error = $"Source path must be a file, not a directory: {sourcePath}" };
            if (Validate.SafePath(destinationPath, "destinationPath") is object err) return err;

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Back up any existing asset before it is overwritten, so undo can restore the old contents.
            bool overwriting = File.Exists(destinationPath);
            if (overwriting)
            {
                var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath);
                if (existing != null) WorkflowManager.SnapshotObject(existing);
            }

            File.Copy(sourcePath, destinationPath, true);
            AssetDatabase.ImportAsset(destinationPath);

            if (!overwriting)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(destinationPath);
                if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            }

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["imported"] = destinationPath
            };

            if (ServerAvailabilityHelper.AffectsScriptDomain(destinationPath))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Imported script-domain asset: {destinationPath}. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Asset import completed: {destinationPath}. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("asset_delete", "Delete an asset",
            Category = SkillCategory.Asset, Operation = SkillOperation.Delete,
            Tags = new[] { "delete", "remove", "cleanup" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, RiskLevel = "medium")]
        public static object AssetDelete(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath", isDelete: true) is object err) return err;
            if (!SkillsCommon.PathExists(assetPath))
                return new { error = $"Asset not found: {assetPath}" };

            // DeleteAssetToTrash backs up the file (+ .meta) to the content-addressed store
            // and records a Deleted snapshot, so no explicit pre-snapshot is needed.
            if (!WorkflowManager.DeleteAssetToTrash(assetPath))
                return new { error = $"Failed to delete asset: {assetPath}" };

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["deleted"] = assetPath
            };

            if (ServerAvailabilityHelper.AffectsScriptDomain(assetPath))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Deleted script-domain asset: {assetPath}. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Asset deletion completed: {assetPath}. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("asset_move", "Move or rename an asset",
            Category = SkillCategory.Asset, Operation = SkillOperation.Modify,
            Tags = new[] { "move", "rename", "reorganize" },
            Outputs = new[] { "from", "to" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, RiskLevel = "medium")]
        public static object AssetMove(string sourcePath, string destinationPath)
        {
            if (Validate.SafePath(sourcePath, "sourcePath") is object err1) return err1;
            if (Validate.SafePath(destinationPath, "destinationPath") is object err2) return err2;

            // Lightweight Moved snapshot (stores both paths only); undo moves the asset back.
            WorkflowManager.SnapshotAssetMove(sourcePath, destinationPath);

            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["from"] = sourcePath,
                ["to"] = destinationPath
            };

            if (ServerAvailabilityHelper.AffectsScriptDomain(sourcePath) || ServerAvailabilityHelper.AffectsScriptDomain(destinationPath))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Moved script-domain asset: {sourcePath} -> {destinationPath}. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Asset move completed: {destinationPath}. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("asset_import_batch", "Import multiple assets. items: JSON array of {sourcePath, destinationPath}",
            Category = SkillCategory.Asset, Operation = SkillOperation.Create,
            Tags = new[] { "import", "copy", "external", "batch" },
            Outputs = new[] { "imported", "assetPath" },
            TracksWorkflow = true)]
        public static object AssetImportBatch(string items)
        {
            return BatchExecutor.Execute<BatchImportItem>(items, item =>
            {
                if (Validate.SafePath(item.destinationPath, "destinationPath") is object dstErr)
                    throw new System.Exception(((dynamic)dstErr).error);
                if (!File.Exists(item.sourcePath))
                    throw new System.Exception("File not found");

                var dir = Path.GetDirectoryName(item.destinationPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Back up any existing asset before overwrite so undo can restore old contents.
                bool overwriting = File.Exists(item.destinationPath);
                if (overwriting)
                {
                    var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.destinationPath);
                    if (existing != null) WorkflowManager.SnapshotObject(existing);
                }

                File.Copy(item.sourcePath, item.destinationPath, true);
                AssetDatabase.ImportAsset(item.destinationPath);

                if (!overwriting)
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.destinationPath);
                    if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
                }

                return new
                {
                    target = item.destinationPath,
                    success = true,
                    serverAvailability = ServerAvailabilityHelper.AffectsScriptDomain(item.destinationPath)
                        ? ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            $"Imported script-domain asset: {item.destinationPath}. Unity may briefly reload the script domain.",
                            alwaysInclude: true)
                        : ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            $"Asset import completed: {item.destinationPath}. Unity may still be refreshing assets.",
                            alwaysInclude: false)
                };
            }, item => item.destinationPath ?? item.sourcePath,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchImportItem { public string sourcePath; public string destinationPath; }

        [UnitySkill("asset_delete_batch", "Delete multiple assets. items: JSON array of {path}",
            Category = SkillCategory.Asset, Operation = SkillOperation.Delete,
            Tags = new[] { "delete", "remove", "cleanup", "batch" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true)]
        public static object AssetDeleteBatch(string items)
        {
            return BatchExecutor.Execute<BatchDeleteItem>(items, item =>
            {
                if (Validate.SafePath(item.path, "path", isDelete: true) is object pathErr)
                    throw new System.Exception(((dynamic)pathErr).error);

                // DeleteAssetToTrash self-manages backup + Deleted snapshot; no pre-snapshot needed.
                if (!WorkflowManager.DeleteAssetToTrash(item.path))
                    throw new System.Exception("Delete failed");

                return new
                {
                    target = item.path,
                    success = true,
                    serverAvailability = ServerAvailabilityHelper.AffectsScriptDomain(item.path)
                        ? ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            $"Deleted script-domain asset: {item.path}. Unity may briefly reload the script domain.",
                            alwaysInclude: true)
                        : ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                            $"Asset deletion completed: {item.path}. Unity may still be refreshing assets.",
                            alwaysInclude: false)
                };
            }, item => item.path,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchDeleteItem { public string path; }

        [UnitySkill("asset_move_batch", "Move multiple assets. items: JSON array of {sourcePath, destinationPath}",
            Category = SkillCategory.Asset, Operation = SkillOperation.Modify,
            Tags = new[] { "move", "rename", "reorganize", "batch" },
            Outputs = new[] { "from", "to" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true)]
        public static object AssetMoveBatch(string items)
        {
            return BatchExecutor.Execute<BatchMoveItem>(items, item =>
            {
                if (Validate.SafePath(item.sourcePath, "sourcePath") is object srcErr)
                    throw new System.Exception(((dynamic)srcErr).error);
                if (Validate.SafePath(item.destinationPath, "destinationPath") is object dstErr)
                    throw new System.Exception(((dynamic)dstErr).error);

                // Lightweight Moved snapshot (both paths only); undo moves the asset back.
                WorkflowManager.SnapshotAssetMove(item.sourcePath, item.destinationPath);

                string error = AssetDatabase.MoveAsset(item.sourcePath, item.destinationPath);
                if (!string.IsNullOrEmpty(error))
                    throw new System.Exception(error);

                return new
                {
                    target = item.sourcePath,
                    success = true,
                    from = item.sourcePath,
                    to = item.destinationPath,
                    serverAvailability =
                        (ServerAvailabilityHelper.AffectsScriptDomain(item.sourcePath) || ServerAvailabilityHelper.AffectsScriptDomain(item.destinationPath))
                            ? ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                                $"Moved script-domain asset: {item.sourcePath} -> {item.destinationPath}. Unity may briefly reload the script domain.",
                                alwaysInclude: true)
                            : ServerAvailabilityHelper.CreateTransientUnavailableNotice(
                                $"Asset move completed: {item.destinationPath}. Unity may still be refreshing assets.",
                                alwaysInclude: false)
                };
            }, item => item.sourcePath ?? item.destinationPath,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchMoveItem { public string sourcePath; public string destinationPath; }

        [UnitySkill("asset_duplicate", "Duplicate an asset",
            Category = SkillCategory.Asset, Operation = SkillOperation.Create,
            Tags = new[] { "duplicate", "copy", "clone" },
            Outputs = new[] { "original", "copy" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true)]
        public static object AssetDuplicate(string assetPath)
        {
            if (Validate.SafePath(assetPath, "assetPath") is object err) return err;

            var newPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CopyAsset(assetPath, newPath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(newPath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["original"] = assetPath,
                ["copy"] = newPath
            };

            if (ServerAvailabilityHelper.AffectsScriptDomain(assetPath) || ServerAvailabilityHelper.AffectsScriptDomain(newPath))
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Duplicated script-domain asset: {assetPath} -> {newPath}. Unity may briefly reload the script domain.",
                    alwaysInclude: true);
            }
            else
            {
                ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                    result,
                    $"Asset duplication completed: {newPath}. Unity may still be refreshing assets.",
                    alwaysInclude: false);
            }

            return result;
        }

        [UnitySkill("asset_find", "Find assets by name, type, or label",
            Category = SkillCategory.Asset, Operation = SkillOperation.Query,
            Tags = new[] { "search", "filter", "database" },
            Outputs = new[] { "count", "assets", "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("asset_create_folder", "Create a new folder in Assets",
            Category = SkillCategory.Asset, Operation = SkillOperation.Create,
            Tags = new[] { "folder", "directory", "organize" },
            Outputs = new[] { "path", "guid" },
            TracksWorkflow = true, SkipAutoPresnapshot = true)]
        public static object AssetCreateFolder(string folderPath)
        {
            if (Validate.SafePath(folderPath, "folderPath") is object pathErr) return pathErr;
            if (Directory.Exists(folderPath))
                return new { error = "Folder already exists" };

            var parent = Path.GetDirectoryName(folderPath);
            var name = Path.GetFileName(folderPath);
            var guid = AssetDatabase.CreateFolder(parent, name);

            // AssetDatabase.CreateFolder returns an empty guid (and logs its own error) when it
            // fails, e.g. the parent folder does not exist. Don't report success or record a
            // workflow snapshot for a folder that was never created.
            if (string.IsNullOrEmpty(guid))
                return new { error = $"Failed to create folder '{folderPath}'. The parent folder may not exist." };

            WorkflowManager.SnapshotCreatedFolder(folderPath);

            return new { success = true, path = folderPath, guid };
        }

        [UnitySkill("asset_create_folder_batch", "Create multiple folders. items: JSON array of {folderPath}",
            Category = SkillCategory.Asset, Operation = SkillOperation.Create,
            Tags = new[] { "folder", "directory", "organize", "batch" },
            Outputs = new[] { "path", "guid" },
            TracksWorkflow = true, SkipAutoPresnapshot = true)]
        public static object AssetCreateFolderBatch(string items)
        {
            return BatchExecutor.Execute<BatchFolderItem>(items, item =>
            {
                if (Validate.SafePath(item.folderPath, "folderPath") is object pathErr)
                    throw new System.Exception(((dynamic)pathErr).error);
                if (Directory.Exists(item.folderPath))
                    throw new System.Exception("Folder already exists");

                var parent = Path.GetDirectoryName(item.folderPath);
                var name = Path.GetFileName(item.folderPath);
                var guid = AssetDatabase.CreateFolder(parent, name);
                if (string.IsNullOrEmpty(guid))
                    throw new System.Exception("Create folder failed (parent path may not exist)");

                WorkflowManager.SnapshotCreatedFolder(item.folderPath);

                return new { target = item.folderPath, success = true, path = item.folderPath, guid };
            }, item => item.folderPath,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchFolderItem { public string folderPath; }

        [UnitySkill("asset_refresh", "Refresh the Asset Database",
            Category = SkillCategory.Asset, Operation = SkillOperation.Execute,
            Tags = new[] { "refresh", "reimport", "database" },
            Outputs = new[] { "message" })]
        public static object AssetRefresh()
        {
            AssetDatabase.Refresh();

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "Asset database refreshed"
            };

            ServerAvailabilityHelper.AttachTransientUnavailableNotice(
                result,
                "AssetDatabase.Refresh may trigger a short asset refresh window. The REST server can be briefly unavailable if Unity starts recompiling scripts.",
                alwaysInclude: false);

            return result;
        }

        [UnitySkill("asset_get_info", "Get information about an asset",
            Category = SkillCategory.Asset, Operation = SkillOperation.Query,
            Tags = new[] { "info", "metadata", "inspect" },
            Outputs = new[] { "assetPath", "name", "type", "guid", "labels" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

// Producer:Betsy
