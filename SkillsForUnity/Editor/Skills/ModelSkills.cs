using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Model import settings skills - get/set model importer properties (FBX, OBJ, etc).
    /// </summary>
    public static class ModelSkills
    {
        [UnitySkill("model_get_settings", "Get model import settings for a 3D model asset (FBX, OBJ, etc)")]
        public static object ModelGetSettings(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                return new { error = $"Not a model file or asset not found: {assetPath}" };

            return new
            {
                success = true,
                path = assetPath,
                // Scene
                globalScale = importer.globalScale,
                useFileScale = importer.useFileScale,
                importBlendShapes = importer.importBlendShapes,
                importVisibility = importer.importVisibility,
                importCameras = importer.importCameras,
                importLights = importer.importLights,
                // Meshes
                meshCompression = importer.meshCompression.ToString(),
                isReadable = importer.isReadable,
                optimizeMeshPolygons = importer.optimizeMeshPolygons,
                optimizeMeshVertices = importer.optimizeMeshVertices,
                generateSecondaryUV = importer.generateSecondaryUV,
                // Geometry
                keepQuads = importer.keepQuads,
                weldVertices = importer.weldVertices,
                // Normals & Tangents
                importNormals = importer.importNormals.ToString(),
                importTangents = importer.importTangents.ToString(),
                // Animation
                animationType = importer.animationType.ToString(),
                importAnimation = importer.importAnimation,
                // Materials
                materialImportMode = importer.materialImportMode.ToString()
            };
        }

        [UnitySkill("model_set_settings", "Set model import settings. meshCompression: Off/Low/Medium/High. animationType: None/Legacy/Generic/Humanoid. materialImportMode: None/ImportViaMaterialDescription/ImportStandard")]
        public static object ModelSetSettings(
            string assetPath,
            float? globalScale = null,
            bool? useFileScale = null,
            bool? importBlendShapes = null,
            bool? importVisibility = null,
            bool? importCameras = null,
            bool? importLights = null,
            string meshCompression = null,
            bool? isReadable = null,
            bool? optimizeMeshPolygons = null,
            bool? optimizeMeshVertices = null,
            bool? generateSecondaryUV = null,
            bool? keepQuads = null,
            bool? weldVertices = null,
            string importNormals = null,
            string importTangents = null,
            string animationType = null,
            bool? importAnimation = null,
            string materialImportMode = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                return new { error = $"Not a model file or asset not found: {assetPath}" };

            // 修改前记录资产状态
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var changes = new List<string>();

            // Scene settings
            if (globalScale.HasValue)
            {
                importer.globalScale = globalScale.Value;
                changes.Add($"globalScale={globalScale.Value}");
            }

            if (useFileScale.HasValue)
            {
                importer.useFileScale = useFileScale.Value;
                changes.Add($"useFileScale={useFileScale.Value}");
            }

            if (importBlendShapes.HasValue)
            {
                importer.importBlendShapes = importBlendShapes.Value;
                changes.Add($"importBlendShapes={importBlendShapes.Value}");
            }

            if (importVisibility.HasValue)
            {
                importer.importVisibility = importVisibility.Value;
                changes.Add($"importVisibility={importVisibility.Value}");
            }

            if (importCameras.HasValue)
            {
                importer.importCameras = importCameras.Value;
                changes.Add($"importCameras={importCameras.Value}");
            }

            if (importLights.HasValue)
            {
                importer.importLights = importLights.Value;
                changes.Add($"importLights={importLights.Value}");
            }

            // Mesh settings
            if (!string.IsNullOrEmpty(meshCompression))
            {
                if (System.Enum.TryParse<ModelImporterMeshCompression>(meshCompression, true, out var mc))
                {
                    importer.meshCompression = mc;
                    changes.Add($"meshCompression={mc}");
                }
                else
                {
                    return new { error = $"Invalid meshCompression: {meshCompression}. Valid: Off, Low, Medium, High" };
                }
            }

            if (isReadable.HasValue)
            {
                importer.isReadable = isReadable.Value;
                changes.Add($"isReadable={isReadable.Value}");
            }

            if (optimizeMeshPolygons.HasValue)
            {
                importer.optimizeMeshPolygons = optimizeMeshPolygons.Value;
                changes.Add($"optimizeMeshPolygons={optimizeMeshPolygons.Value}");
            }

            if (optimizeMeshVertices.HasValue)
            {
                importer.optimizeMeshVertices = optimizeMeshVertices.Value;
                changes.Add($"optimizeMeshVertices={optimizeMeshVertices.Value}");
            }

            if (generateSecondaryUV.HasValue)
            {
                importer.generateSecondaryUV = generateSecondaryUV.Value;
                changes.Add($"generateSecondaryUV={generateSecondaryUV.Value}");
            }

            // Geometry
            if (keepQuads.HasValue)
            {
                importer.keepQuads = keepQuads.Value;
                changes.Add($"keepQuads={keepQuads.Value}");
            }

            if (weldVertices.HasValue)
            {
                importer.weldVertices = weldVertices.Value;
                changes.Add($"weldVertices={weldVertices.Value}");
            }

            // Normals & Tangents
            if (!string.IsNullOrEmpty(importNormals))
            {
                if (System.Enum.TryParse<ModelImporterNormals>(importNormals, true, out var normals))
                {
                    importer.importNormals = normals;
                    changes.Add($"importNormals={normals}");
                }
            }

            if (!string.IsNullOrEmpty(importTangents))
            {
                if (System.Enum.TryParse<ModelImporterTangents>(importTangents, true, out var tangents))
                {
                    importer.importTangents = tangents;
                    changes.Add($"importTangents={tangents}");
                }
            }

            // Animation
            if (!string.IsNullOrEmpty(animationType))
            {
                if (System.Enum.TryParse<ModelImporterAnimationType>(animationType, true, out var at))
                {
                    importer.animationType = at;
                    changes.Add($"animationType={at}");
                }
                else
                {
                    return new { error = $"Invalid animationType: {animationType}. Valid: None, Legacy, Generic, Humanoid" };
                }
            }

            if (importAnimation.HasValue)
            {
                importer.importAnimation = importAnimation.Value;
                changes.Add($"importAnimation={importAnimation.Value}");
            }

            // Materials
            if (!string.IsNullOrEmpty(materialImportMode))
            {
                if (System.Enum.TryParse<ModelImporterMaterialImportMode>(materialImportMode, true, out var mim))
                {
                    importer.materialImportMode = mim;
                    changes.Add($"materialImportMode={mim}");
                }
            }

            // Apply changes
            importer.SaveAndReimport();

            return new
            {
                success = true,
                path = assetPath,
                changesApplied = changes.Count,
                changes
            };
        }

        [UnitySkill("model_set_settings_batch", "Set model import settings for multiple 3D models. items: JSON array of {assetPath, meshCompression, animationType, ...}")]
        public static object ModelSetSettingsBatch(string items)
        {
            return BatchExecutor.Execute<BatchModelItem>(items, item =>
            {
                var importer = AssetImporter.GetAtPath(item.assetPath) as ModelImporter;
                if (importer == null)
                    throw new System.Exception("Not a model file");

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                if (asset != null) WorkflowManager.SnapshotObject(asset);

                if (item.globalScale.HasValue) importer.globalScale = item.globalScale.Value;
                if (item.importBlendShapes.HasValue) importer.importBlendShapes = item.importBlendShapes.Value;
                if (item.importCameras.HasValue) importer.importCameras = item.importCameras.Value;
                if (item.importLights.HasValue) importer.importLights = item.importLights.Value;
                if (item.isReadable.HasValue) importer.isReadable = item.isReadable.Value;
                if (item.generateSecondaryUV.HasValue) importer.generateSecondaryUV = item.generateSecondaryUV.Value;
                if (item.importAnimation.HasValue) importer.importAnimation = item.importAnimation.Value;

                if (!string.IsNullOrEmpty(item.meshCompression) &&
                    System.Enum.TryParse<ModelImporterMeshCompression>(item.meshCompression, true, out var mc))
                    importer.meshCompression = mc;

                if (!string.IsNullOrEmpty(item.animationType) &&
                    System.Enum.TryParse<ModelImporterAnimationType>(item.animationType, true, out var at))
                    importer.animationType = at;

                if (!string.IsNullOrEmpty(item.materialImportMode) &&
                    System.Enum.TryParse<ModelImporterMaterialImportMode>(item.materialImportMode, true, out var mim))
                    importer.materialImportMode = mim;

                importer.SaveAndReimport();
                return new { path = item.assetPath, success = true };
            }, item => item.assetPath,
            setup: () => AssetDatabase.StartAssetEditing(),
            teardown: () => { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); });
        }

        private class BatchModelItem
        {
            public string assetPath { get; set; }
            public float? globalScale { get; set; }
            public bool? importBlendShapes { get; set; }
            public bool? importCameras { get; set; }
            public bool? importLights { get; set; }
            public string meshCompression { get; set; }
            public bool? isReadable { get; set; }
            public bool? generateSecondaryUV { get; set; }
            public string animationType { get; set; }
            public bool? importAnimation { get; set; }
            public string materialImportMode { get; set; }
        }

        [UnitySkill("model_find_assets", "Search for model assets in the project")]
        public static object ModelFindAssets(string filter = "", int limit = 50)
        {
            var guids = AssetDatabase.FindAssets("t:Model " + filter);
            var models = guids.Take(limit).Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                return new { path, name = System.IO.Path.GetFileNameWithoutExtension(path) };
            }).ToArray();
            return new { success = true, totalFound = guids.Length, showing = models.Length, models };
        }

        [UnitySkill("model_get_mesh_info", "Get detailed Mesh information (vertices, triangles, submeshes)")]
        public static object ModelGetMeshInfo(string name = null, int instanceId = 0, string path = null, string assetPath = null)
        {
            Mesh mesh = null;
            if (!string.IsNullOrEmpty(assetPath))
            {
                mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (mesh == null)
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (go != null) { var mf = go.GetComponentInChildren<MeshFilter>(); if (mf != null) mesh = mf.sharedMesh; }
                }
            }
            else
            {
                var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
                if (error != null) return error;
                var mf = go.GetComponent<MeshFilter>();
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                mesh = mf != null ? mf.sharedMesh : smr != null ? smr.sharedMesh : null;
            }
            if (mesh == null) return new { error = "No mesh found" };

            return new { success = true, name = mesh.name, vertexCount = mesh.vertexCount, triangles = mesh.triangles.Length / 3,
                subMeshCount = mesh.subMeshCount, bounds = new { center = $"{mesh.bounds.center}", size = $"{mesh.bounds.size}" },
                hasNormals = mesh.normals.Length > 0, hasTangents = mesh.tangents.Length > 0, hasUV = mesh.uv.Length > 0, hasUV2 = mesh.uv2.Length > 0,
                hasColors = mesh.colors.Length > 0, blendShapeCount = mesh.blendShapeCount, isReadable = mesh.isReadable };
        }

        [UnitySkill("model_get_materials_info", "Get material mapping for a model asset")]
        public static object ModelGetMaterialsInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var materials = allAssets.OfType<Material>().Select(m => new { name = m.name, shader = m.shader != null ? m.shader.name : "null" }).ToArray();
            var meshes = allAssets.OfType<Mesh>().Select(m => new { name = m.name, vertices = m.vertexCount, triangles = m.triangles.Length / 3 }).ToArray();

            return new { success = true, path = assetPath, materialCount = materials.Length, materials, meshCount = meshes.Length, meshes };
        }

        [UnitySkill("model_get_animations_info", "Get animation clip information from a model asset")]
        public static object ModelGetAnimationsInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var clips = allAssets.OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__"))
                .Select(c => new { name = c.name, length = c.length, frameRate = c.frameRate, wrapMode = c.wrapMode.ToString(), isLooping = c.isLooping }).ToArray();

            var importedClips = importer.clipAnimations;
            var clipDefs = importedClips != null ? importedClips.Select(c => new { name = c.name, firstFrame = c.firstFrame, lastFrame = c.lastFrame, loop = c.loopTime }).ToArray() : null;

            return new { success = true, path = assetPath, importAnimation = importer.importAnimation, clipCount = clips.Length, clips, clipDefinitions = clipDefs };
        }

        [UnitySkill("model_set_animation_clips", "Configure animation clip splitting. clips: JSON array of {name, firstFrame, lastFrame, loop}")]
        public static object ModelSetAnimationClips(string assetPath, string clips)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(clips, "clips") is object err2) return err2;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            var clipList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ClipDef>>(clips);
            if (clipList == null || clipList.Count == 0) return new { error = "No clips provided" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            importer.clipAnimations = clipList.Select(c => new ModelImporterClipAnimation
            {
                name = c.name, takeName = c.takeName ?? "Take 001",
                firstFrame = c.firstFrame, lastFrame = c.lastFrame, loopTime = c.loop
            }).ToArray();
            importer.SaveAndReimport();

            return new { success = true, path = assetPath, clipCount = clipList.Count };
        }

        private class ClipDef
        {
            public string name { get; set; }
            public string takeName { get; set; }
            public float firstFrame { get; set; }
            public float lastFrame { get; set; }
            public bool loop { get; set; }
        }

        [UnitySkill("model_get_rig_info", "Get rig/skeleton binding information")]
        public static object ModelGetRigInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            return new { success = true, path = assetPath, animationType = importer.animationType.ToString(),
                avatarSetup = importer.avatarSetup.ToString(), sourceAvatar = importer.sourceAvatar != null ? importer.sourceAvatar.name : "null",
                optimizeGameObjects = importer.optimizeGameObjects, isHuman = importer.animationType == ModelImporterAnimationType.Human };
        }

        [UnitySkill("model_set_rig", "Set rig/skeleton binding type. animationType: None/Legacy/Generic/Humanoid")]
        public static object ModelSetRig(string assetPath, string animationType, string avatarSetup = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            if (!System.Enum.TryParse<ModelImporterAnimationType>(animationType, true, out var at))
                return new { error = $"Invalid animationType: {animationType}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            importer.animationType = at;
            if (!string.IsNullOrEmpty(avatarSetup) && System.Enum.TryParse<ModelImporterAvatarSetup>(avatarSetup, true, out var avs))
                importer.avatarSetup = avs;
            importer.SaveAndReimport();

            return new { success = true, path = assetPath, animationType = at.ToString() };
        }
    }
}
