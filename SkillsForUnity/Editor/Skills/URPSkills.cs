using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

#if URP
using UnityEngine.Rendering.Universal;
#endif

namespace UnitySkills
{
    /// <summary>
    /// URP-specific asset and renderer feature skills.
    /// </summary>
    public static class URPSkills
    {
#if !URP
        [UnitySkill("urp_get_info", "Get information about the active URP asset and renderer setup",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "asset", "renderer", "info" },
            Outputs = new[] { "asset", "renderers" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object URPGetInfo(string assetPath = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_set_asset_settings", "Modify key settings on a URP asset",
            Category = SkillCategory.URP, Operation = SkillOperation.Modify,
            Tags = new[] { "urp", "asset", "settings", "modify" },
            Outputs = new[] { "asset", "settings" })]
        public static object URPSetAssetSettings(string assetPath = null, bool? supportsHDR = null, int? msaaSampleCount = null,
            float? renderScale = null, bool? supportsMainLightShadows = null, bool? supportsAdditionalLightShadows = null,
            bool? supportsCameraDepthTexture = null, bool? supportsCameraOpaqueTexture = null, float? shadowDistance = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_list_renderers", "List renderer data assets on a URP asset",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "renderers", "list" },
            Outputs = new[] { "count", "renderers" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object URPListRenderers(string assetPath = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_list_renderer_features", "List renderer features on a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "renderer feature", "list" },
            Outputs = new[] { "count", "features" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object URPListRendererFeatures(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_add_renderer_feature", "Add a safe built-in renderer feature to a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Create,
            Tags = new[] { "urp", "renderer feature", "add" },
            Outputs = new[] { "feature", "renderer" })]
        public static object URPAddRendererFeature(string featureType, string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, string featureName = null, bool active = true) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_remove_renderer_feature", "Remove a renderer feature from a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Delete,
            Tags = new[] { "urp", "renderer feature", "remove" },
            Outputs = new[] { "removedFeature", "renderer" })]
        public static object URPRemoveRendererFeature(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, int featureIndex = -1, string featureName = null, string featureType = null) => RenderPipelineSkillsCommon.NoURP();

        [UnitySkill("urp_set_renderer_feature_active", "Enable or disable a renderer feature on a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Modify,
            Tags = new[] { "urp", "renderer feature", "active" },
            Outputs = new[] { "feature", "active" })]
        public static object URPSetRendererFeatureActive(bool active, string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, int featureIndex = -1, string featureName = null, string featureType = null) => RenderPipelineSkillsCommon.NoURP();
#else
        [UnitySkill("urp_get_info", "Get information about the active URP asset and renderer setup",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "asset", "renderer", "info" },
            Outputs = new[] { "asset", "renderers" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            Mode = SkillMode.SemiAuto)]
        public static object URPGetInfo(string assetPath = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            return new
            {
                success = true,
                asset = DescribeURPAsset(asset),
                renderers = GetRendererInfos(asset),
                creatableRendererFeatures = URPRendererFeatureHelper.GetCreatableFeatures()
                    .Select(x => new { name = x.Name, notes = x.Notes })
                    .ToArray()
            };
        }

        [UnitySkill("urp_set_asset_settings", "Modify key settings on a URP asset",
            Category = SkillCategory.URP, Operation = SkillOperation.Modify,
            Tags = new[] { "urp", "asset", "settings", "modify" },
            Outputs = new[] { "asset", "settings" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object URPSetAssetSettings(
            string assetPath = null,
            bool? supportsHDR = null,
            int? msaaSampleCount = null,
            float? renderScale = null,
            bool? supportsMainLightShadows = null,
            bool? supportsAdditionalLightShadows = null,
            bool? supportsCameraDepthTexture = null,
            bool? supportsCameraOpaqueTexture = null,
            float? shadowDistance = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(asset);
            Undo.RegisterCompleteObjectUndo(asset, "Modify URP Asset Settings");

            var serializedObject = new SerializedObject(asset);
            SetIfProvided(serializedObject, "m_SupportsHDR", supportsHDR);
            SetIfProvided(serializedObject, "m_MSAA", msaaSampleCount);
            SetIfProvided(serializedObject, "m_RenderScale", renderScale);
            SetIfProvided(serializedObject, "m_MainLightShadowsSupported", supportsMainLightShadows);
            SetIfProvided(serializedObject, "m_AdditionalLightShadowsSupported", supportsAdditionalLightShadows);
            SetIfProvided(serializedObject, "m_RequireDepthTexture", supportsCameraDepthTexture);
            SetIfProvided(serializedObject, "m_RequireOpaqueTexture", supportsCameraOpaqueTexture);
            SetIfProvided(serializedObject, "m_ShadowDistance", shadowDistance);
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new
            {
                success = true,
                asset = DescribeURPAsset(asset)
            };
        }

        [UnitySkill("urp_list_renderers", "List renderer data assets on a URP asset",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "renderers", "list" },
            Outputs = new[] { "count", "renderers" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            Mode = SkillMode.SemiAuto)]
        public static object URPListRenderers(string assetPath = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            var renderers = GetRendererInfos(asset);
            return new
            {
                success = true,
                count = renderers.Length,
                defaultRendererIndex = URPRendererFeatureHelper.GetDefaultRendererIndex(asset),
                renderers
            };
        }

        [UnitySkill("urp_list_renderer_features", "List renderer features on a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Query,
            Tags = new[] { "urp", "renderer feature", "list" },
            Outputs = new[] { "count", "features" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" },
            Mode = SkillMode.SemiAuto)]
        public static object URPListRendererFeatures(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            if (!URPRendererFeatureHelper.TryGetRendererData(asset, rendererIndex, rendererDataPath, out var rendererData, out var resolvedIndex, out var resolveError))
                return new { error = resolveError };

            var features = rendererData.rendererFeatures
                .Select((feature, index) => new
                {
                    index,
                    name = feature?.name,
                    type = feature?.GetType().Name,
                    active = feature?.isActive ?? false
                })
                .ToArray();

            return new
            {
                success = true,
                rendererIndex = resolvedIndex,
                renderer = new
                {
                    name = rendererData.name,
                    path = AssetDatabase.GetAssetPath(rendererData)
                },
                count = features.Length,
                features
            };
        }

        [UnitySkill("urp_add_renderer_feature", "Add a safe built-in renderer feature to a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Create,
            Tags = new[] { "urp", "renderer feature", "add" },
            Outputs = new[] { "feature", "renderer" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "featureType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object URPAddRendererFeature(string featureType, string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, string featureName = null, bool active = true)
        {
            if (Validate.Required(featureType, "featureType") is object err) return err;

            var descriptor = URPRendererFeatureHelper.FindCreatableFeature(featureType);
            if (descriptor == null)
                return new { error = $"Unsupported featureType '{featureType}'. Use urp_get_info to inspect supported values." };

            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            if (!URPRendererFeatureHelper.TryGetRendererData(asset, rendererIndex, rendererDataPath, out var rendererData, out var resolvedIndex, out var resolveError))
                return new { error = resolveError };

            if (HasDisallowMultipleFeature(rendererData, descriptor.Type))
                return new { error = $"{descriptor.Name} already exists on renderer '{rendererData.name}'" };

            WorkflowManager.SnapshotObject(rendererData);
            Undo.RegisterCompleteObjectUndo(rendererData, "Add Renderer Feature");

            var feature = ScriptableObject.CreateInstance(descriptor.Type) as ScriptableRendererFeature;
            feature.name = string.IsNullOrWhiteSpace(featureName) ? descriptor.Name : featureName;
            feature.SetActive(active);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
            URPRendererFeatureHelper.SyncRendererFeatureMap(rendererData);
            URPRendererFeatureHelper.MarkRendererDataDirty(rendererData);

            return new
            {
                success = true,
                rendererIndex = resolvedIndex,
                renderer = rendererData.name,
                feature = new
                {
                    name = feature.name,
                    type = feature.GetType().Name,
                    active = feature.isActive
                }
            };
        }

        [UnitySkill("urp_remove_renderer_feature", "Remove a renderer feature from a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Delete,
            Tags = new[] { "urp", "renderer feature", "remove" },
            Outputs = new[] { "removedFeature", "renderer" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object URPRemoveRendererFeature(string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, int featureIndex = -1, string featureName = null, string featureType = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            if (!URPRendererFeatureHelper.TryGetRendererData(asset, rendererIndex, rendererDataPath, out var rendererData, out var resolvedIndex, out var resolveError))
                return new { error = resolveError };

            if (!TryResolveFeature(rendererData, featureIndex, featureName, featureType, out var feature, out featureIndex, out var featureError))
                return new { error = featureError };

            var removedFeatureName = feature != null ? feature.name : null;
            var removedFeatureType = feature != null ? feature.GetType().Name : null;
            WorkflowManager.SnapshotObject(rendererData);
            Undo.RegisterCompleteObjectUndo(rendererData, "Remove Renderer Feature");
            rendererData.rendererFeatures.RemoveAt(featureIndex);
            if (feature != null)
                Undo.DestroyObjectImmediate(feature);
            URPRendererFeatureHelper.SyncRendererFeatureMap(rendererData);
            URPRendererFeatureHelper.MarkRendererDataDirty(rendererData);

            return new
            {
                success = true,
                rendererIndex = resolvedIndex,
                renderer = rendererData.name,
                removedFeature = removedFeatureName,
                featureType = removedFeatureType
            };
        }

        [UnitySkill("urp_set_renderer_feature_active", "Enable or disable a renderer feature on a URP renderer",
            Category = SkillCategory.URP, Operation = SkillOperation.Modify,
            Tags = new[] { "urp", "renderer feature", "active" },
            Outputs = new[] { "feature", "active" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.universal" })]
        public static object URPSetRendererFeatureActive(bool active, string assetPath = null, int rendererIndex = -1, string rendererDataPath = null, int featureIndex = -1, string featureName = null, string featureType = null)
        {
            var asset = LoadAssetOrError(assetPath, out var error);
            if (error != null) return error;

            if (!URPRendererFeatureHelper.TryGetRendererData(asset, rendererIndex, rendererDataPath, out var rendererData, out var resolvedIndex, out var resolveError))
                return new { error = resolveError };

            if (!TryResolveFeature(rendererData, featureIndex, featureName, featureType, out var feature, out _, out var featureError))
                return new { error = featureError };

            WorkflowManager.SnapshotObject(feature);
            Undo.RegisterCompleteObjectUndo(feature, "Toggle Renderer Feature");
            feature.SetActive(active);
            EditorUtility.SetDirty(feature);
            URPRendererFeatureHelper.MarkRendererDataDirty(rendererData);

            return new
            {
                success = true,
                rendererIndex = resolvedIndex,
                renderer = rendererData.name,
                feature = feature.name,
                featureType = feature.GetType().Name,
                active = feature.isActive
            };
        }

        internal static UniversalRenderPipelineAsset LoadAssetOrError(string assetPath, out object error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(assetPath) && Validate.SafePath(assetPath, "assetPath") is object pathErr)
            {
                error = pathErr;
                return null;
            }

            var asset = URPRendererFeatureHelper.GetCurrentAsset(assetPath);
            if (asset == null)
            {
                error = new { error = "No active URP asset found" };
                return null;
            }

            return asset;
        }

        private static object DescribeURPAsset(UniversalRenderPipelineAsset asset)
        {
            return new
            {
                name = asset.name,
                path = AssetDatabase.GetAssetPath(asset),
                supportsHDR = asset.supportsHDR,
                msaaSampleCount = asset.msaaSampleCount,
                renderScale = asset.renderScale,
                supportsMainLightShadows = asset.supportsMainLightShadows,
                supportsAdditionalLightShadows = asset.supportsAdditionalLightShadows,
                supportsCameraDepthTexture = asset.supportsCameraDepthTexture,
                supportsCameraOpaqueTexture = asset.supportsCameraOpaqueTexture,
                shadowDistance = asset.shadowDistance,
                defaultRendererIndex = URPRendererFeatureHelper.GetDefaultRendererIndex(asset)
            };
        }

        private static object[] GetRendererInfos(UniversalRenderPipelineAsset asset)
        {
            var defaultIndex = URPRendererFeatureHelper.GetDefaultRendererIndex(asset);
            var rendererDataList = URPRendererFeatureHelper.GetRendererDataList(asset);
            var renderers = new object[rendererDataList.Length];
            for (var i = 0; i < rendererDataList.Length; i++)
            {
                var rendererData = rendererDataList[i];
                renderers[i] = new
                {
                    index = i,
                    isDefault = i == defaultIndex,
                    name = rendererData?.name,
                    path = rendererData != null ? AssetDatabase.GetAssetPath(rendererData) : null,
                    rendererType = rendererData?.GetType().Name,
                    featureCount = rendererData?.rendererFeatures.Count ?? 0
                };
            }

            return renderers;
        }

        private static bool HasDisallowMultipleFeature(UniversalRendererData rendererData, Type featureType)
        {
            var hasDisallowAttr = featureType.GetCustomAttributes(false)
                .Any(attr => attr.GetType().Name.IndexOf("DisallowMultipleRendererFeature", StringComparison.OrdinalIgnoreCase) >= 0);
            return hasDisallowAttr && rendererData.rendererFeatures.Any(x => x != null && x.GetType() == featureType);
        }

        private static bool TryResolveFeature(UniversalRendererData rendererData, int featureIndex, string featureName, string featureType, out ScriptableRendererFeature feature, out int resolvedIndex, out string error)
        {
            feature = null;
            resolvedIndex = -1;
            error = null;

            if (featureIndex >= 0)
            {
                if (featureIndex >= rendererData.rendererFeatures.Count)
                {
                    error = $"featureIndex out of range: {featureIndex}";
                    return false;
                }

                feature = rendererData.rendererFeatures[featureIndex];
                resolvedIndex = featureIndex;
                if (feature == null)
                {
                    error = $"Renderer feature at index {featureIndex} is null";
                    return false;
                }

                return true;
            }

            for (var i = 0; i < rendererData.rendererFeatures.Count; i++)
            {
                var candidate = rendererData.rendererFeatures[i];
                if (candidate == null)
                    continue;

                var nameMatches = !string.IsNullOrWhiteSpace(featureName) &&
                                  string.Equals(candidate.name, featureName, StringComparison.OrdinalIgnoreCase);
                var typeMatches = !string.IsNullOrWhiteSpace(featureType) &&
                                  string.Equals(candidate.GetType().Name, featureType, StringComparison.OrdinalIgnoreCase);

                if (nameMatches || typeMatches)
                {
                    feature = candidate;
                    resolvedIndex = i;
                    return true;
                }
            }

            error = "Renderer feature not found. Provide featureIndex, featureName, or featureType.";
            return false;
        }

        private static void SetIfProvided(SerializedObject serializedObject, string propertyName, bool? value)
        {
            if (!value.HasValue)
                return;

            var property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.boolValue = value.Value;
        }

        private static void SetIfProvided(SerializedObject serializedObject, string propertyName, int? value)
        {
            if (!value.HasValue)
                return;

            var property = serializedObject.FindProperty(propertyName);
            if (property == null)
                return;
            property.intValue = value.Value;
        }

        private static void SetIfProvided(SerializedObject serializedObject, string propertyName, float? value)
        {
            if (!value.HasValue)
                return;

            var property = serializedObject.FindProperty(propertyName);
            if (property != null)
                property.floatValue = value.Value;
        }
#endif
    }
}

// Producer:Betsy
