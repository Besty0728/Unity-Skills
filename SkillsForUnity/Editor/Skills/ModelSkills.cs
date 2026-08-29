using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Model import settings skills: read/write ModelImporter properties (FBX, OBJ, etc.).
    /// </summary>
    public static class ModelSkills
    {
        /// <summary>
        /// Rejects model-import writes that could never actually land on disk before attempting them: paths
        /// under Packages/ (registry or otherwise immutable packages), and paths where
        /// AssetDatabase.MakeEditable refuses to unlock them (read-only on disk, or checkout rejected by version control).
        ///
        /// <para>Without this gate, the following ModelImporter.SaveAndReimport() would run to completion
        /// without throwing, the importer's in-memory property getters would echo back the new values as-is, and
        /// the skill would return success + changesApplied with a full change list — but the asset's .meta was
        /// never writable to begin with, and Unity's immediately following reimport would wipe all of it out.
        /// The caller is told a change took effect that never actually happened.</para>
        /// </summary>
        private static object CheckModelAssetWritable(string assetPath, string target = null)
        {
            var normalized = assetPath.Replace('\\', '/');
            bool underPackages = normalized.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase);

            if (underPackages || !AssetDatabase.MakeEditable(assetPath))
            {
                return new
                {
                    error = $"Model import settings cannot be persisted for a read-only asset: {assetPath}",
                    errorCode = SkillParamUtil.SemanticInvalidCode,
                    parameter = "assetPath",
                    reason = underPackages
                        ? "Path is under Packages/ (registry or immutable package); its .meta file cannot be written."
                        : "AssetDatabase.MakeEditable(assetPath) failed - read-only on disk or rejected by version control.",
                    suggestion = "Copy the model under Assets/ first if its import settings need to change.",
                    target = target ?? assetPath
                };
            }
            return null;
        }


        [UnitySkill("model_get_settings", "Get model import settings for a 3D model asset (FBX, OBJ, etc)",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "model", "import", "settings", "fbx" },
            Outputs = new[] { "globalScale", "meshCompression", "animationType", "materialImportMode" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                // Mesh
                meshCompression = importer.meshCompression.ToString(),
                isReadable = importer.isReadable,
                optimizeMeshPolygons = importer.optimizeMeshPolygons,
                optimizeMeshVertices = importer.optimizeMeshVertices,
                generateSecondaryUV = importer.generateSecondaryUV,
                // Geometry
                keepQuads = importer.keepQuads,
                weldVertices = importer.weldVertices,
                // Normals and tangents
                importNormals = importer.importNormals.ToString(),
                importTangents = importer.importTangents.ToString(),
                // Animation
                animationType = importer.animationType.ToString(),
                importAnimation = importer.importAnimation,
                // Material
                materialImportMode = importer.materialImportMode.ToString()
            };
        }

        [UnitySkill("model_set_settings", "Set model import settings. meshCompression: Off/Low/Medium/High. animationType: None/Legacy/Generic/Human (Inspector alias: Humanoid = Human). materialImportMode: None/ImportViaMaterialDescription/ImportStandard",
            Category = SkillCategory.Model, Operation = SkillOperation.Modify,
            Tags = new[] { "model", "import", "settings", "mesh" },
            Outputs = new[] { "changesApplied", "changes" },
            RequiresInput = new[] { "assetPath" },
            MutatesAssets = true)]
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

            // All five enums are parsed before anything is written: previously, a parse failure on
            // importNormals/importTangents/materialImportMode would be silently skipped while the rest of the
            // fields were written anyway, and the response would still report success + changesApplied>0.
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterMeshCompression>(meshCompression, "meshCompression", out var mc, out var mcError))
                return mcError;
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterNormals>(importNormals, "importNormals", out var normals, out var normalsError))
                return normalsError;
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterTangents>(importTangents, "importTangents", out var tangents, out var tangentsError))
                return tangentsError;
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterAnimationType>(
                    animationType, "animationType", SkillParamUtil.ModelAnimationTypeAliases, out var at, out var atError))
                return atError;
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterMaterialImportMode>(materialImportMode, "materialImportMode", out var mim, out var mimError))
                return mimError;

            // Enum validation must happen before the writability check (consistent with ModelSetRig):
            // otherwise an invalid enum value would still trigger AssetDatabase.MakeEditable's real
            // version-control checkout side effect.
            if (CheckModelAssetWritable(assetPath) is object writableErr) return writableErr;

            // Record asset state before modifying it
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var changes = new List<string>();

            // Scene
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

            // Mesh
            if (mc.HasValue)
            {
                importer.meshCompression = mc.Value;
                changes.Add($"meshCompression={mc.Value}");
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

            // Normals and tangents
            if (normals.HasValue)
            {
                importer.importNormals = normals.Value;
                changes.Add($"importNormals={normals.Value}");
            }

            if (tangents.HasValue)
            {
                importer.importTangents = tangents.Value;
                changes.Add($"importTangents={tangents.Value}");
            }

            // Animation
            if (at.HasValue)
            {
                importer.animationType = at.Value;
                changes.Add($"animationType={at.Value}");
            }

            if (importAnimation.HasValue)
            {
                importer.importAnimation = importAnimation.Value;
                changes.Add($"importAnimation={importAnimation.Value}");
            }

            // Material
            if (mim.HasValue)
            {
                importer.materialImportMode = mim.Value;
                changes.Add($"materialImportMode={mim.Value}");
            }

            importer.SaveAndReimport();

            return new
            {
                success = true,
                path = assetPath,
                changesApplied = changes.Count,
                changes
            };
        }

        [UnitySkill("model_set_settings_batch", "Set model import settings for multiple 3D models. items: JSON array of {assetPath, meshCompression, animationType, ...}",
            Category = SkillCategory.Model, Operation = SkillOperation.Modify,
            Tags = new[] { "model", "import", "batch", "settings" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            MutatesAssets = true)]
        public static object ModelSetSettingsBatch(string items)
        {
            return BatchExecutor.Execute<BatchModelItem>(items, item =>
            {
                var importer = AssetImporter.GetAtPath(item.assetPath) as ModelImporter;
                if (importer == null)
                    throw new System.Exception("Not a model file");

                // Same rule as the single-item setter; the error is pinned to this item's assetPath.
                if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterMeshCompression>(item.meshCompression, "meshCompression", out var mc, out _))
                    return SkillParamUtil.InvalidEnumError<ModelImporterMeshCompression>(item.meshCompression, "meshCompression", item.assetPath);
                if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterAnimationType>(
                        item.animationType, "animationType", SkillParamUtil.ModelAnimationTypeAliases, out var at, out _))
                    return SkillParamUtil.InvalidEnumError<ModelImporterAnimationType>(
                        item.animationType, "animationType", SkillParamUtil.ModelAnimationTypeAliases, item.assetPath);
                if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterMaterialImportMode>(item.materialImportMode, "materialImportMode", out var mim, out _))
                    return SkillParamUtil.InvalidEnumError<ModelImporterMaterialImportMode>(item.materialImportMode, "materialImportMode", item.assetPath);

                // Enum validation must happen before the writability check (consistent with ModelSetRig):
                // otherwise an invalid enum value would still trigger AssetDatabase.MakeEditable's real
                // version-control checkout side effect.
                if (CheckModelAssetWritable(item.assetPath, item.assetPath) is object writableErr) return writableErr;

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.assetPath);
                if (asset != null) WorkflowManager.SnapshotObject(asset);

                if (item.globalScale.HasValue) importer.globalScale = item.globalScale.Value;
                if (item.importBlendShapes.HasValue) importer.importBlendShapes = item.importBlendShapes.Value;
                if (item.importCameras.HasValue) importer.importCameras = item.importCameras.Value;
                if (item.importLights.HasValue) importer.importLights = item.importLights.Value;
                if (item.isReadable.HasValue) importer.isReadable = item.isReadable.Value;
                if (item.generateSecondaryUV.HasValue) importer.generateSecondaryUV = item.generateSecondaryUV.Value;
                if (item.importAnimation.HasValue) importer.importAnimation = item.importAnimation.Value;

                if (mc.HasValue) importer.meshCompression = mc.Value;
                if (at.HasValue) importer.animationType = at.Value;
                if (mim.HasValue) importer.materialImportMode = mim.Value;

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

        [UnitySkill("model_find_assets", "Search for model assets in the project",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "model", "search", "find", "asset" },
            Outputs = new[] { "totalFound", "models" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("model_get_mesh_info", "Get detailed Mesh information (vertices, triangles, submeshes)",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "mesh", "vertices", "triangles", "geometry" },
            Outputs = new[] { "vertexCount", "triangles", "subMeshCount", "bounds", "blendShapeCount" },
            RequiresInput = new[] { "gameObject|assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

            return new { success = true, name = mesh.name, vertexCount = mesh.vertexCount, triangles = SkillsCommon.GetTriangleCount(mesh),
                subMeshCount = mesh.subMeshCount, bounds = new { center = $"{mesh.bounds.center}", size = $"{mesh.bounds.size}" },
                hasNormals = mesh.normals.Length > 0, hasTangents = mesh.tangents.Length > 0, hasUV = mesh.uv.Length > 0, hasUV2 = mesh.uv2.Length > 0,
                hasColors = mesh.colors.Length > 0, blendShapeCount = mesh.blendShapeCount, isReadable = mesh.isReadable };
        }

        [UnitySkill("model_get_materials_info", "Get material mapping for a model asset",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "model", "material", "mapping", "inspect" },
            Outputs = new[] { "materialCount", "materials", "meshCount", "meshes" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ModelGetMaterialsInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var materials = allAssets.OfType<Material>().Select(m => new { name = m.name, shader = m.shader != null ? m.shader.name : "null" }).ToArray();
            var meshes = allAssets.OfType<Mesh>().Select(m => new { name = m.name, vertices = m.vertexCount, triangles = SkillsCommon.GetTriangleCount(m) }).ToArray();

            return new { success = true, path = assetPath, materialCount = materials.Length, materials, meshCount = meshes.Length, meshes };
        }

        [UnitySkill("model_get_animations_info", "Get animation clip information from a model asset",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "model", "animation", "clip", "inspect" },
            Outputs = new[] { "importAnimation", "clipCount", "clips", "clipDefinitions" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

        [UnitySkill("model_set_animation_clips", "Configure animation clip splitting. clips: JSON array of {name, firstFrame, lastFrame, loop}",
            Category = SkillCategory.Model, Operation = SkillOperation.Modify,
            Tags = new[] { "model", "animation", "clip", "splitting" },
            Outputs = new[] { "clipCount" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object ModelSetAnimationClips(string assetPath, string clips)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            if (Validate.Required(clips, "clips") is object err2) return err2;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            if (CheckModelAssetWritable(assetPath) is object writableErr) return writableErr;

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

        [UnitySkill("model_get_rig_info", "Get rig/skeleton binding information",
            Category = SkillCategory.Model, Operation = SkillOperation.Query,
            Tags = new[] { "model", "rig", "skeleton", "avatar" },
            Outputs = new[] { "animationType", "avatarSetup", "sourceAvatar", "isHuman" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ModelGetRigInfo(string assetPath)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            return new { success = true, path = assetPath, animationType = importer.animationType.ToString(),
                avatarSetup = importer.avatarSetup.ToString(), sourceAvatar = importer.sourceAvatar != null ? importer.sourceAvatar.name : "null",
                optimizeGameObjects = importer.optimizeGameObjects, isHuman = importer.animationType == ModelImporterAnimationType.Human };
        }

        [UnitySkill("model_set_rig", "Set rig/skeleton binding type. animationType: None/Legacy/Generic/Human (Inspector alias: Humanoid = Human)",
            Category = SkillCategory.Model, Operation = SkillOperation.Modify,
            Tags = new[] { "model", "rig", "skeleton", "animation" },
            Outputs = new[] { "animationType" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object ModelSetRig(string assetPath, string animationType, string avatarSetup = null)
        {
            if (Validate.Required(assetPath, "assetPath") is object err) return err;
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return new { error = $"Not a model: {assetPath}" };

            // The Rig dropdown (and this skill's own description) writes "Humanoid", while the enum member is
            // called Human, so it must go through the alias table — otherwise the word given in the docs would be rejected outright.
            if (!SkillParamUtil.TryParseRequiredEnum<ModelImporterAnimationType>(
                    animationType, "animationType", SkillParamUtil.ModelAnimationTypeAliases, out var at, out var atError))
                return atError;
            // Must validate before animationType is written: otherwise an invalid avatarSetup would be
            // discarded while the rig type still got rewritten and reimported.
            if (!SkillParamUtil.TryParseOptionalEnum<ModelImporterAvatarSetup>(avatarSetup, "avatarSetup", out var avs, out var avsError))
                return avsError;

            if (CheckModelAssetWritable(assetPath) is object writableErr) return writableErr;

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            importer.animationType = at;
            if (avs.HasValue) importer.avatarSetup = avs.Value;
            importer.SaveAndReimport();

            return new { success = true, path = assetPath, animationType = at.ToString() };
        }
    }
}

// Producer:Betsy
