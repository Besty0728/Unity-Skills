using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Validation and cleanup skills - find issues, optimize assets, clean project.
    /// </summary>
    public static class ValidationSkills
    {
        private class ValidationIssue
        {
            public string type;
            public string severity;
            public string gameObject;
            public string path;
            public string message;
            public string prefabPath;
            public int count;
        }

        [UnitySkill("validate_scene", "Validate current scene for common issues")]
        public static object ValidateScene(bool checkMissingScripts = true, bool checkMissingPrefabs = true, bool checkDuplicateNames = true, bool checkEmptyGameObjects = false)
        {
            var issues = new List<ValidationIssue>();
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();
            var allObjects = Object.FindObjectsOfType<GameObject>();

            // Check for missing scripts
            if (checkMissingScripts)
            {
                foreach (var go in allObjects)
                {
                    var components = go.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        if (components[i] == null)
                        {
                            issues.Add(new ValidationIssue
                            {
                                type = "MissingScript",
                                severity = "Error",
                                gameObject = go.name,
                                path = GameObjectFinder.GetPath(go),
                                message = $"Missing script at component index {i}"
                            });
                        }
                    }
                }
            }

            // Check for missing prefab references
            if (checkMissingPrefabs)
            {
                foreach (var go in allObjects)
                {
                    if (PrefabUtility.IsPrefabAssetMissing(go))
                    {
                        issues.Add(new ValidationIssue
                        {
                            type = "MissingPrefab",
                            severity = "Warning",
                            gameObject = go.name,
                            path = GameObjectFinder.GetPath(go),
                            message = "Prefab asset is missing"
                        });
                    }
                }
            }

            // Check for duplicate names
            if (checkDuplicateNames)
            {
                var nameGroups = allObjects.GroupBy(go => go.name).Where(g => g.Count() > 1);
                foreach (var group in nameGroups)
                {
                    issues.Add(new ValidationIssue
                    {
                        type = "DuplicateName",
                        severity = "Info",
                        gameObject = group.Key,
                        count = group.Count(),
                        message = $"{group.Count()} objects share the name '{group.Key}'"
                    });
                }
            }

            // Check for empty GameObjects
            if (checkEmptyGameObjects)
            {
                foreach (var go in allObjects)
                {
                    var components = go.GetComponents<Component>();
                    if (components.Length == 1 && go.transform.childCount == 0) // Only Transform
                    {
                        issues.Add(new ValidationIssue
                        {
                            type = "EmptyGameObject",
                            severity = "Info",
                            gameObject = go.name,
                            path = GameObjectFinder.GetPath(go),
                            message = "GameObject has no components (except Transform) and no children"
                        });
                    }
                }
            }

            var summary = new
            {
                errors = issues.Count(i => i.severity == "Error"),
                warnings = issues.Count(i => i.severity == "Warning"),
                info = issues.Count(i => i.severity == "Info")
            };

            return new
            {
                scene = scene.name,
                totalIssues = issues.Count,
                summary,
                issues = issues.Select(i => new { i.type, i.severity, i.gameObject, i.path, i.message, i.count }).ToArray()
            };
        }

        [UnitySkill("validate_find_missing_scripts", "Find all GameObjects with missing scripts")]
        public static object ValidateFindMissingScripts(bool searchInPrefabs = true)
        {
            var results = new List<object>();

            // Search in scene
            var sceneObjects = Object.FindObjectsOfType<GameObject>();
            foreach (var go in sceneObjects)
            {
                var components = go.GetComponents<Component>();
                var missingCount = components.Count(c => c == null);
                if (missingCount > 0)
                {
                    results.Add(new
                    {
                        source = "Scene",
                        gameObject = go.name,
                        path = GameObjectFinder.GetPath(go),
                        missingCount
                    });
                }
            }

            // Search in prefabs
            if (searchInPrefabs)
            {
                var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
                foreach (var guid in prefabGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;

                    var allChildren = prefab.GetComponentsInChildren<Transform>(true);
                    foreach (var t in allChildren)
                    {
                        var components = t.gameObject.GetComponents<Component>();
                        var missingCount = components.Count(c => c == null);
                        if (missingCount > 0)
                        {
                            results.Add(new
                            {
                                source = "Prefab",
                                prefabPath = path,
                                gameObject = t.name,
                                missingCount
                            });
                        }
                    }
                }
            }

            return new { totalFound = results.Count, objects = results };
        }

        [UnitySkill("validate_cleanup_empty_folders", "Find and optionally delete empty folders")]
        public static object ValidateCleanupEmptyFolders(string rootPath = "Assets", bool dryRun = true)
        {
            if (Validate.SafePath(rootPath, "rootPath") is object pathErr) return pathErr;

            var emptyFolders = new List<string>();
            FindEmptyFolders(rootPath, emptyFolders);

            if (!dryRun && emptyFolders.Count > 0)
            {
                // Delete in reverse order (deepest first) to handle nested empty folders
                var sorted = emptyFolders.OrderByDescending(f => f.Length).ToList();
                foreach (var folder in sorted)
                {
                    if (Directory.Exists(folder))
                    {
                        AssetDatabase.DeleteAsset(folder);
                    }
                }
                AssetDatabase.Refresh();
            }

            return new
            {
                success = true,
                dryRun,
                emptyFolderCount = emptyFolders.Count,
                folders = emptyFolders,
                message = dryRun ? "Dry run - no folders deleted" : $"Deleted {emptyFolders.Count} empty folders"
            };
        }

        private static void FindEmptyFolders(string path, List<string> emptyFolders)
        {
            if (!Directory.Exists(path)) return;

            var subDirectories = Directory.GetDirectories(path);
            foreach (var subDir in subDirectories)
            {
                FindEmptyFolders(subDir, emptyFolders);
            }

            var files = Directory.GetFiles(path);
            var directories = Directory.GetDirectories(path);

            // Check if folder is empty (only .meta files don't count)
            var hasRealFiles = files.Any(f => !f.EndsWith(".meta"));
            var hasSubDirs = directories.Length > 0;

            if (!hasRealFiles && !hasSubDirs)
            {
                emptyFolders.Add(path.Replace("\\", "/"));
            }
        }

        [UnitySkill("validate_find_unused_assets", "Find potentially unused assets")]
        public static object ValidateFindUnusedAssets(string assetType = "Material", int limit = 100)
        {
            var filter = $"t:{assetType}";
            var guids = AssetDatabase.FindAssets(filter);
            var potentiallyUnused = new List<object>();

            foreach (var guid in guids.Take(limit * 2)) // Check more than limit
            {
                if (potentiallyUnused.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var dependencies = AssetDatabase.GetDependencies(path, false);
                
                // Simple heuristic: if nothing depends on it except itself
                var usedBy = guids
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .Where(p => p != path && AssetDatabase.GetDependencies(p, true).Contains(path))
                    .Take(1)
                    .ToArray();

                if (usedBy.Length == 0)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    potentiallyUnused.Add(new
                    {
                        path,
                        name = asset?.name,
                        type = asset?.GetType().Name
                    });
                }
            }

            return new
            {
                assetType,
                potentiallyUnusedCount = potentiallyUnused.Count,
                note = "These assets may still be used via scripts or Resources.Load",
                assets = potentiallyUnused
            };
        }

        [UnitySkill("validate_texture_sizes", "Find textures that may need optimization")]
        public static object ValidateTextureSizes(int maxRecommendedSize = 2048, int limit = 50)
        {
            var largeTextures = new List<object>();
            var guids = AssetDatabase.FindAssets("t:Texture2D");

            foreach (var guid in guids)
            {
                if (largeTextures.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null) continue;

                if (texture.width > maxRecommendedSize || texture.height > maxRecommendedSize)
                {
                    largeTextures.Add(new
                    {
                        path,
                        name = texture.name,
                        width = texture.width,
                        height = texture.height,
                        maxTextureSize = importer.maxTextureSize,
                        format = texture.format.ToString(),
                        recommendation = $"Consider reducing to {maxRecommendedSize}x{maxRecommendedSize} or smaller"
                    });
                }
            }

            return new
            {
                maxRecommendedSize,
                largeTextureCount = largeTextures.Count,
                textures = largeTextures
            };
        }

        [UnitySkill("validate_project_structure", "Get overview of project structure")]
        public static object ValidateProjectStructure(string rootPath = "Assets", int maxDepth = 2)
        {
            var structure = GetFolderStructure(rootPath, 0, maxDepth);
            
            // Count assets by type
            var assetCounts = new Dictionary<string, int>();
            var commonTypes = new[] { "Material", "Prefab", "Script", "Texture2D", "AudioClip", "Scene", "Shader" };
            
            foreach (var type in commonTypes)
            {
                var count = AssetDatabase.FindAssets($"t:{type}", new[] { rootPath }).Length;
                assetCounts[type] = count;
            }

            return new
            {
                rootPath,
                assetCounts,
                structure
            };
        }

        private static object GetFolderStructure(string path, int depth, int maxDepth)
        {
            if (!Directory.Exists(path) || depth >= maxDepth)
                return null;

            var subDirs = Directory.GetDirectories(path)
                .Select(d => new DirectoryInfo(d))
                .Select(d => new
                {
                    name = d.Name,
                    fileCount = Directory.GetFiles(d.FullName).Count(f => !f.EndsWith(".meta")),
                    children = depth < maxDepth - 1 ? GetFolderStructure(d.FullName, depth + 1, maxDepth) : null
                })
                .ToArray();

            return subDirs;
        }

        [UnitySkill("validate_fix_missing_scripts", "Remove missing script components from GameObjects")]
        public static object ValidateFixMissingScripts(bool dryRun = true)
        {
            var fixedObjects = new List<object>();
            var sceneObjects = Object.FindObjectsOfType<GameObject>();

            foreach (var go in sceneObjects)
            {
                var missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missingCount > 0)
                {
                    fixedObjects.Add(new
                    {
                        gameObject = go.name,
                        path = GameObjectFinder.GetPath(go),
                        missingCount
                    });

                    if (!dryRun)
                    {
                        WorkflowManager.SnapshotObject(go);
                        Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    }
                }
            }

            return new
            {
                success = true,
                dryRun,
                fixedCount = fixedObjects.Count,
                message = dryRun ? "Dry run - no scripts removed" : $"Removed missing scripts from {fixedObjects.Count} objects",
                objects = fixedObjects
            };
        }
        [UnitySkill("validate_missing_references", "Find null/missing object references on components in the scene")]
        public static object ValidateMissingReferences(int limit = 50)
        {
            var results = new List<object>();
            foreach (var go in Object.FindObjectsOfType<GameObject>())
            {
                if (results.Count >= limit) break;
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    while (prop.NextVisible(true))
                    {
                        if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                            prop.objectReferenceValue == null && prop.objectReferenceInstanceIDValue != 0)
                        {
                            results.Add(new { gameObject = go.name, path = GameObjectFinder.GetPath(go),
                                component = comp.GetType().Name, property = prop.propertyPath });
                            break;
                        }
                    }
                }
            }
            return new { success = true, count = results.Count, issues = results };
        }

        [UnitySkill("validate_mesh_collider_convex", "Find non-convex MeshColliders (potential performance issue)")]
        public static object ValidateMeshColliderConvex(int limit = 50)
        {
            var colliders = Object.FindObjectsOfType<MeshCollider>()
                .Where(mc => !mc.convex)
                .Take(limit)
                .Select(mc => new { gameObject = mc.gameObject.name, path = GameObjectFinder.GetPath(mc.gameObject),
                    vertexCount = mc.sharedMesh != null ? mc.sharedMesh.vertexCount : 0 })
                .ToArray();
            return new { success = true, count = colliders.Length, nonConvexColliders = colliders };
        }

        [UnitySkill("validate_shader_errors", "Find shaders with compilation errors")]
        public static object ValidateShaderErrors(int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:Shader");
            var errors = new List<object>();
            foreach (var guid in guids)
            {
                if (errors.Count >= limit) break;
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) continue;
                int msgCount = UnityEditor.ShaderUtil.GetShaderMessageCount(shader);
                if (msgCount > 0)
                    errors.Add(new { name = shader.name, path, errorCount = msgCount });
            }
            return new { success = true, count = errors.Count, shaders = errors };
        }
    }
}
