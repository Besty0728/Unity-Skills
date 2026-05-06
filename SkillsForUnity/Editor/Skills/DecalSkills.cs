using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;
#if URP
using UnityEngine.Rendering.Universal;
#endif

namespace UnitySkills
{
    /// <summary>
    /// URP decal projector skills.
    /// </summary>
    public static class DecalSkills
    {
#if !URP
        [UnitySkill("decal_create", "Create a URP Decal Projector",
            Category = SkillCategory.Decal, Operation = SkillOperation.Create,
            Tags = new[] { "decal", "projector", "create", "urp" },
            Outputs = new[] { "name", "instanceId" })]
        public static object DecalCreate(string name = "Decal Projector", string materialPath = null, float x = 0, float y = 0, float z = 0) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_get_info", "Get information about a Decal Projector",
            Category = SkillCategory.Decal, Operation = SkillOperation.Query,
            Tags = new[] { "decal", "projector", "info" },
            Outputs = new[] { "name", "material", "size" },
            ReadOnly = true)]
        public static object DecalGetInfo(string name = null, int instanceId = 0, string path = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_set_properties", "Modify Decal Projector properties",
            Category = SkillCategory.Decal, Operation = SkillOperation.Modify,
            Tags = new[] { "decal", "projector", "modify" },
            Outputs = new[] { "name", "material", "size" })]
        public static object DecalSetProperties(string name = null, int instanceId = 0, string path = null, string materialPath = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_find_all", "Find all Decal Projectors in the scene",
            Category = SkillCategory.Decal, Operation = SkillOperation.Query,
            Tags = new[] { "decal", "projector", "list" },
            Outputs = new[] { "count", "decals" },
            ReadOnly = true)]
        public static object DecalFindAll(int limit = 50) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_delete", "Delete a Decal Projector GameObject",
            Category = SkillCategory.Decal, Operation = SkillOperation.Delete,
            Tags = new[] { "decal", "projector", "delete" },
            Outputs = new[] { "deleted" })]
        public static object DecalDelete(string name = null, int instanceId = 0, string path = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_set_properties_batch", "Modify multiple Decal Projectors in one request",
            Category = SkillCategory.Decal, Operation = SkillOperation.Modify,
            Tags = new[] { "decal", "projector", "batch" },
            Outputs = new[] { "successCount", "failCount", "results" })]
        public static object DecalSetPropertiesBatch(string items) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("decal_ensure_renderer_feature", "Ensure the current URP renderer has a DecalRendererFeature",
            Category = SkillCategory.Decal, Operation = SkillOperation.Create | SkillOperation.Query,
            Tags = new[] { "decal", "renderer feature", "urp" },
            Outputs = new[] { "renderer", "feature" })]
        public static object DecalEnsureRendererFeature(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null) => RenderPipelineSkillsCommon.NoURP();
#else
        [UnitySkill("decal_create", "Create a URP Decal Projector",
            Category = SkillCategory.Decal, Operation = SkillOperation.Create,
            Tags = new[] { "decal", "projector", "create", "urp" },
            Outputs = new[] { "name", "instanceId", "material", "size" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalCreate(string name = "Decal Projector", string materialPath = null, float x = 0, float y = 0, float z = 0)
        {
            Material material = null;
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                if (Validate.SafePath(materialPath, "materialPath") is object pathErr) return pathErr;
                material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return new { error = $"Material not found: {materialPath}" };
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Decal Projector");
            go.transform.position = new Vector3(x, y, z);
            var projector = go.AddComponent<DecalProjector>();
            projector.material = material;
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return DescribeProjector(projector);
        }

        [UnitySkill("decal_get_info", "Get information about a Decal Projector",
            Category = SkillCategory.Decal, Operation = SkillOperation.Query,
            Tags = new[] { "decal", "projector", "info" },
            Outputs = new[] { "name", "material", "size" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            var projectorResult = GetProjector(name, instanceId, path);
            if (projectorResult.error != null) return projectorResult.error;
            return DescribeProjector(projectorResult.projector);
        }

        [UnitySkill("decal_set_properties", "Modify Decal Projector properties",
            Category = SkillCategory.Decal, Operation = SkillOperation.Modify,
            Tags = new[] { "decal", "projector", "modify" },
            Outputs = new[] { "name", "material", "size" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalSetProperties(
            string name = null,
            int instanceId = 0,
            string path = null,
            string materialPath = null,
            float? drawDistance = null,
            float? fadeScale = null,
            float? fadeFactor = null,
            float? startAngleFade = null,
            float? endAngleFade = null,
            string uvScale = null,
            string uvBias = null,
            string size = null,
            string pivot = null,
            uint? renderingLayerMask = null,
            string scaleMode = null)
        {
            var projectorResult = GetProjector(name, instanceId, path);
            if (projectorResult.error != null) return projectorResult.error;

            var projector = projectorResult.projector;
            WorkflowManager.SnapshotObject(projector);
            Undo.RegisterCompleteObjectUndo(projector, "Modify Decal Projector");

            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                if (Validate.SafePath(materialPath, "materialPath") is object pathErr) return pathErr;
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return new { error = $"Material not found: {materialPath}" };
                projector.material = material;
            }

            if (drawDistance.HasValue) projector.drawDistance = drawDistance.Value;
            if (fadeScale.HasValue) projector.fadeScale = fadeScale.Value;
            if (fadeFactor.HasValue) projector.fadeFactor = fadeFactor.Value;
            if (startAngleFade.HasValue) projector.startAngleFade = startAngleFade.Value;
            if (endAngleFade.HasValue) projector.endAngleFade = endAngleFade.Value;
            if (!string.IsNullOrWhiteSpace(uvScale))
            {
                if (!TryParseVector2(uvScale, out var parsedUvScale))
                    return new { error = $"Invalid uvScale '{uvScale}'. Use 'x,y'." };
                projector.uvScale = parsedUvScale;
            }

            if (!string.IsNullOrWhiteSpace(uvBias))
            {
                if (!TryParseVector2(uvBias, out var parsedUvBias))
                    return new { error = $"Invalid uvBias '{uvBias}'. Use 'x,y'." };
                projector.uvBias = parsedUvBias;
            }

            if (!string.IsNullOrWhiteSpace(size))
            {
                if (!TryParseVector3(size, out var parsedSize))
                    return new { error = $"Invalid size '{size}'. Use 'x,y,z'." };
                projector.size = parsedSize;
            }

            if (!string.IsNullOrWhiteSpace(pivot))
            {
                if (!TryParseVector3(pivot, out var parsedPivot))
                    return new { error = $"Invalid pivot '{pivot}'. Use 'x,y,z'." };
                projector.pivot = parsedPivot;
            }
            if (renderingLayerMask.HasValue)
            {
                var serializedObject = new SerializedObject(projector);
                var property = serializedObject.FindProperty("m_RenderingLayerMask");
                if (property != null)
                {
                    property.intValue = unchecked((int)renderingLayerMask.Value);
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            if (!string.IsNullOrWhiteSpace(scaleMode))
            {
                if (!Enum.TryParse(scaleMode, true, out DecalScaleMode parsedScaleMode))
                    return new { error = $"Invalid scaleMode '{scaleMode}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(DecalScaleMode)))}" };
                projector.scaleMode = parsedScaleMode;
            }

            EditorUtility.SetDirty(projector);
            return DescribeProjector(projector);
        }

        [UnitySkill("decal_find_all", "Find all Decal Projectors in the scene",
            Category = SkillCategory.Decal, Operation = SkillOperation.Query,
            Tags = new[] { "decal", "projector", "list" },
            Outputs = new[] { "count", "decals" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalFindAll(int limit = 50)
        {
            var decals = FindHelper.FindAll<DecalProjector>()
                .Take(limit)
                .Select(DescribeProjector)
                .ToArray();

            return new
            {
                success = true,
                count = decals.Length,
                decals
            };
        }

        [UnitySkill("decal_delete", "Delete a Decal Projector GameObject",
            Category = SkillCategory.Decal, Operation = SkillOperation.Delete,
            Tags = new[] { "decal", "projector", "delete" },
            Outputs = new[] { "deleted" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalDelete(string name = null, int instanceId = 0, string path = null)
        {
            var projectorResult = GetProjector(name, instanceId, path);
            if (projectorResult.error != null) return projectorResult.error;

            var go = projectorResult.projector.gameObject;
            var deletedName = go.name;
            WorkflowManager.SnapshotObject(go);
            Undo.DestroyObjectImmediate(go);
            return new
            {
                success = true,
                deleted = deletedName
            };
        }

        [UnitySkill("decal_set_properties_batch", "Modify multiple Decal Projectors in one request",
            Category = SkillCategory.Decal, Operation = SkillOperation.Modify,
            Tags = new[] { "decal", "projector", "batch" },
            Outputs = new[] { "successCount", "failCount", "results" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresInput = new[] { "items" },
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalSetPropertiesBatch(string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            return BatchExecutor.Execute<DecalBatchItem>(items, item =>
            {
                var result = DecalSetProperties(
                    name: item.name,
                    instanceId: item.instanceId,
                    path: item.path,
                    materialPath: item.materialPath,
                    drawDistance: item.drawDistance,
                    fadeScale: item.fadeScale,
                    fadeFactor: item.fadeFactor,
                    startAngleFade: item.startAngleFade,
                    endAngleFade: item.endAngleFade,
                    uvScale: item.uvScale,
                    uvBias: item.uvBias,
                    size: item.size,
                    pivot: item.pivot,
                    renderingLayerMask: item.renderingLayerMask,
                    scaleMode: item.scaleMode);

                if (SkillResultHelper.TryGetError(result, out var errorText))
                    throw new ArgumentException(errorText);

                return result;
            }, item => item.name ?? item.path);
        }

        [UnitySkill("decal_ensure_renderer_feature", "Ensure the current URP renderer has a DecalRendererFeature",
            Category = SkillCategory.Decal, Operation = SkillOperation.Create | SkillOperation.Query,
            Tags = new[] { "decal", "renderer feature", "urp" },
            Outputs = new[] { "renderer", "feature" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object DecalEnsureRendererFeature(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null)
        {
            var asset = URPSkills.LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            if (!URPRendererFeatureHelper.TryGetRendererData(asset, rendererIndex, rendererDataPath, out var rendererData, out var resolvedIndex, out var resolveError))
                return new { error = resolveError };

            var existing = rendererData.rendererFeatures.FirstOrDefault(x =>
                x != null && string.Equals(x.GetType().Name, "DecalRendererFeature", StringComparison.Ordinal));
            if (existing != null)
            {
                return new
                {
                    success = true,
                    alreadyExists = true,
                    rendererIndex = resolvedIndex,
                    renderer = rendererData.name,
                    feature = existing.name
                };
            }

            return URPSkills.URPAddRendererFeature("DecalRendererFeature", assetPath, rendererIndex, rendererDataPath);
        }

        private static (DecalProjector projector, object error) GetProjector(string name, int instanceId, string path)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (goErr != null) return (null, goErr);

            var projector = go.GetComponent<DecalProjector>();
            if (projector == null)
                return (null, new { error = $"No DecalProjector on {go.name}" });

            return (projector, null);
        }

        private static object DescribeProjector(DecalProjector projector)
        {
            var serializedObject = new SerializedObject(projector);
            var renderingLayerMaskProperty = serializedObject.FindProperty("m_RenderingLayerMask");

            return new
            {
                success = true,
                name = projector.gameObject.name,
                instanceId = projector.gameObject.GetInstanceID(),
                path = GameObjectFinder.GetPath(projector.gameObject),
                material = projector.material != null ? new
                {
                    name = projector.material.name,
                    path = AssetDatabase.GetAssetPath(projector.material)
                } : null,
                drawDistance = projector.drawDistance,
                fadeScale = projector.fadeScale,
                fadeFactor = projector.fadeFactor,
                startAngleFade = projector.startAngleFade,
                endAngleFade = projector.endAngleFade,
                uvScale = RenderPipelineSkillsCommon.ToSerializableValue(projector.uvScale),
                uvBias = RenderPipelineSkillsCommon.ToSerializableValue(projector.uvBias),
                size = RenderPipelineSkillsCommon.ToSerializableValue(projector.size),
                pivot = RenderPipelineSkillsCommon.ToSerializableValue(projector.pivot),
                renderingLayerMask = renderingLayerMaskProperty != null ? (uint)renderingLayerMaskProperty.intValue : 0u,
                scaleMode = projector.scaleMode.ToString()
            };
        }

        private static bool TryParseVector2(string value, out Vector2 result)
        {
            result = default;
            if (!RenderPipelineSkillsCommon.TryParseVector(value, 2, out var v)) return false;
            result = new Vector2(v[0], v[1]);
            return true;
        }

        private static bool TryParseVector3(string value, out Vector3 result)
        {
            result = default;
            if (!RenderPipelineSkillsCommon.TryParseVector(value, 3, out var v)) return false;
            result = new Vector3(v[0], v[1], v[2]);
            return true;
        }

        private sealed class DecalBatchItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public string materialPath { get; set; }
            public float? drawDistance { get; set; }
            public float? fadeScale { get; set; }
            public float? fadeFactor { get; set; }
            public float? startAngleFade { get; set; }
            public float? endAngleFade { get; set; }
            public string uvScale { get; set; }
            public string uvBias { get; set; }
            public string size { get; set; }
            public string pivot { get; set; }
            public uint? renderingLayerMask { get; set; }
            public string scaleMode { get; set; }
        }
#endif
    }
}
