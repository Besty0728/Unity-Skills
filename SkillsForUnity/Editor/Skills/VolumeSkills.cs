using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

#if SRP_CORE
using UnityEngine.Rendering;
#endif

namespace UnitySkills
{
    /// <summary>
    /// Shared SRP volume framework skills.
    /// </summary>
    public static class VolumeSkills
    {
#if !SRP_CORE
        [UnitySkill("volume_profile_create", "Create a VolumeProfile asset",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "profile", "create", "asset" },
            Outputs = new[] { "path" })]
        public static object VolumeProfileCreate(string name, string savePath = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_create", "Create a global or local Volume GameObject",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "create", "gameobject", "profile" },
            Outputs = new[] { "name", "instanceId" })]
        public static object VolumeCreate(string name = "Global Volume", bool isGlobal = true, string profilePath = null,
            float x = 0, float y = 0, float z = 0, float priority = 0, float blendDistance = 5f) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_set_profile", "Assign or replace a Volume profile on a Volume component",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "profile", "assign" },
            Outputs = new[] { "profilePath" })]
        public static object VolumeSetProfile(string name = null, int instanceId = 0, string path = null, string profilePath = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_list_component_types", "List explicit VolumeComponent types supported by the active pipeline",
            Category = SkillCategory.Volume, Operation = SkillOperation.Query,
            Tags = new[] { "volume", "component", "list", "pipeline" },
            Outputs = new[] { "count", "components" },
            ReadOnly = true)]
        public static object VolumeListComponentTypes(bool includePostProcess = true) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_add_component", "Add a VolumeComponent override to a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "component", "add", "profile" },
            Outputs = new[] { "componentType", "profilePath" })]
        public static object VolumeAddComponent(string profilePath, string componentType, bool overrides = true) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_remove_component", "Remove a VolumeComponent override from a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Delete,
            Tags = new[] { "volume", "component", "remove", "profile" },
            Outputs = new[] { "componentType", "profilePath" })]
        public static object VolumeRemoveComponent(string profilePath, string componentType) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_get_component", "Inspect a VolumeComponent override on a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Query,
            Tags = new[] { "volume", "component", "inspect", "profile" },
            Outputs = new[] { "componentType", "parameters" },
            ReadOnly = true)]
        public static object VolumeGetComponent(string profilePath, string componentType) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_set_parameter", "Set a parameter on a VolumeComponent override",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "component", "parameter", "set" },
            Outputs = new[] { "componentType", "parameterName", "value" })]
        public static object VolumeSetParameter(string profilePath, string componentType, string parameterName, object value, bool? overrideState = true) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("volume_set_parameter_batch", "Set multiple parameters on a single VolumeComponent override",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "component", "parameter", "batch" },
            Outputs = new[] { "successCount", "failCount", "results" })]
        public static object VolumeSetParameterBatch(string profilePath, string componentType, string items) => RenderPipelineSkillsCommon.NoSRP();
#else
        [UnitySkill("volume_profile_create", "Create a VolumeProfile asset",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "profile", "create", "asset" },
            Outputs = new[] { "name", "path", "instanceId" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeProfileCreate(string name, string savePath = null)
        {
            if (Validate.Required(name, "name") is object err) return err;

            var resolvedPath = RenderPipelineSkillsCommon.ResolveAssetSavePath(savePath, name);
            if (Validate.SafePath(resolvedPath, "savePath") is object pathErr) return pathErr;
            if (File.Exists(resolvedPath))
                return new { error = $"Asset already exists: {resolvedPath}" };

            RenderPipelineSkillsCommon.EnsureAssetFolderExists(resolvedPath);

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = name;
            AssetDatabase.CreateAsset(profile, resolvedPath);
            AssetDatabase.SaveAssets();
            WorkflowManager.SnapshotCreatedAsset(profile);

            return new
            {
                success = true,
                name,
                path = resolvedPath,
                instanceId = profile.GetInstanceID(),
                componentCount = profile.components.Count
            };
        }

        [UnitySkill("volume_create", "Create a global or local Volume GameObject",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "create", "gameobject", "profile" },
            Outputs = new[] { "name", "instanceId", "isGlobal", "profilePath" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeCreate(
            string name = "Global Volume",
            bool isGlobal = true,
            string profilePath = null,
            float x = 0,
            float y = 0,
            float z = 0,
            float priority = 0,
            float blendDistance = 5f)
        {
            VolumeProfile profile = null;
            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                if (Validate.SafePath(profilePath, "profilePath") is object pathErr) return pathErr;
                profile = RenderPipelineSkillsCommon.LoadVolumeProfile(profilePath);
                if (profile == null)
                    return new { error = $"VolumeProfile not found: {profilePath}" };
            }

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Volume");
            var volume = go.AddComponent<Volume>();
            go.transform.position = new Vector3(x, y, z);
            volume.isGlobal = isGlobal;
            volume.priority = priority;
            volume.blendDistance = isGlobal ? 0f : blendDistance;
            volume.weight = 1f;
            volume.sharedProfile = profile;

            if (!isGlobal && go.GetComponent<Collider>() == null)
            {
                var collider = go.AddComponent<BoxCollider>();
                collider.size = Vector3.one * 10f;
                collider.isTrigger = true;
            }

            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                isGlobal,
                priority = volume.priority,
                blendDistance = volume.blendDistance,
                profilePath = profile != null ? AssetDatabase.GetAssetPath(profile) : null
            };
        }

        [UnitySkill("volume_set_profile", "Assign or replace a Volume profile on a Volume component",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "profile", "assign" },
            Outputs = new[] { "gameObject", "profilePath" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresInput = new[] { "gameObject", "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeSetProfile(string name = null, int instanceId = 0, string path = null, string profilePath = null)
        {
            if (Validate.Required(profilePath, "profilePath") is object err) return err;
            if (Validate.SafePath(profilePath, "profilePath") is object pathErr) return pathErr;

            var profile = RenderPipelineSkillsCommon.LoadVolumeProfile(profilePath);
            if (profile == null)
                return new { error = $"VolumeProfile not found: {profilePath}" };

            var (go, goErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (goErr != null) return goErr;

            var volume = go.GetComponent<Volume>();
            if (volume == null)
                return new { error = $"No Volume component on {go.name}" };

            WorkflowManager.SnapshotObject(volume);
            Undo.RecordObject(volume, "Assign Volume Profile");
            volume.sharedProfile = profile;
            EditorUtility.SetDirty(volume);

            return new
            {
                success = true,
                gameObject = go.name,
                profilePath
            };
        }

        [UnitySkill("volume_list_component_types", "List explicit VolumeComponent types supported by the active pipeline",
            Category = SkillCategory.Volume, Operation = SkillOperation.Query,
            Tags = new[] { "volume", "component", "list", "pipeline" },
            Outputs = new[] { "count", "components" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeListComponentTypes(bool includePostProcess = true)
        {
            var pipeline = RenderPipelineSkillsCommon.DetectPipeline();
            if (pipeline == RenderPipelineSupport.BuiltIn)
                return new { error = "Built-in Render Pipeline does not support the SRP Volume workflow used by these skills." };

            var components = RenderPipelineSkillsCommon.GetVolumeComponentRegistry()
                .Where(x => includePostProcess || !x.IsPostProcess)
                .Select(x => new
                {
                    name = x.Name,
                    pipeline = x.Pipeline.ToString(),
                    group = x.Group,
                    isPostProcess = x.IsPostProcess
                })
                .ToArray();

            return new
            {
                success = true,
                pipeline = pipeline.ToString(),
                count = components.Length,
                components
            };
        }

        [UnitySkill("volume_add_component", "Add a VolumeComponent override to a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Create,
            Tags = new[] { "volume", "component", "add", "profile" },
            Outputs = new[] { "componentType", "profilePath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "componentType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeAddComponent(string profilePath, string componentType, bool overrides = true)
        {
            var descriptorResult = ResolveDescriptor(componentType);
            if (descriptorResult.error != null) return descriptorResult.error;

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return profileResult.error;

            var profile = profileResult.profile;
            if (profile.Has(descriptorResult.type))
                return new { success = true, alreadyExists = true, componentType = descriptorResult.name, profilePath };

            WorkflowManager.SnapshotObject(profile);
            Undo.RegisterCompleteObjectUndo(profile, "Add Volume Component");
            var component = RenderPipelineSkillsCommon.GetOrAddVolumeComponent(profile, descriptorResult.type, overrides);
            RenderPipelineSkillsCommon.MarkDirty(profile);

            return new
            {
                success = true,
                componentType = descriptorResult.name,
                profilePath,
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(component)
            };
        }

        [UnitySkill("volume_remove_component", "Remove a VolumeComponent override from a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Delete,
            Tags = new[] { "volume", "component", "remove", "profile" },
            Outputs = new[] { "componentType", "profilePath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "componentType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeRemoveComponent(string profilePath, string componentType)
        {
            var descriptorResult = ResolveDescriptor(componentType);
            if (descriptorResult.error != null) return descriptorResult.error;

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return profileResult.error;

            var profile = profileResult.profile;
            if (!profile.TryGet(descriptorResult.type, out VolumeComponent component))
                return new { error = $"Component '{descriptorResult.name}' not found in profile {profilePath}" };

            WorkflowManager.SnapshotObject(profile);
            Undo.RegisterCompleteObjectUndo(profile, "Remove Volume Component");
            profile.Remove(descriptorResult.type);
            if (component != null)
                Undo.DestroyObjectImmediate(component);
            RenderPipelineSkillsCommon.MarkDirty(profile);

            return new
            {
                success = true,
                componentType = descriptorResult.name,
                profilePath
            };
        }

        [UnitySkill("volume_get_component", "Inspect a VolumeComponent override on a VolumeProfile",
            Category = SkillCategory.Volume, Operation = SkillOperation.Query,
            Tags = new[] { "volume", "component", "inspect", "profile" },
            Outputs = new[] { "componentType", "parameters" },
            ReadOnly = true,
            RequiresInput = new[] { "profilePath", "componentType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeGetComponent(string profilePath, string componentType)
        {
            var descriptorResult = ResolveDescriptor(componentType);
            if (descriptorResult.error != null) return descriptorResult.error;

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return profileResult.error;

            var profile = profileResult.profile;
            if (!profile.TryGet(descriptorResult.type, out VolumeComponent component))
                return new { error = $"Component '{descriptorResult.name}' not found in profile {profilePath}" };

            return new
            {
                success = true,
                profilePath,
                componentType = descriptorResult.name,
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(component)
            };
        }

        [UnitySkill("volume_set_parameter", "Set a parameter on a VolumeComponent override",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "component", "parameter", "set" },
            Outputs = new[] { "componentType", "parameterName", "value" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "componentType", "parameterName" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeSetParameter(string profilePath, string componentType, string parameterName, object value, bool? overrideState = true)
        {
            var componentResult = GetProfileAndComponent(profilePath, componentType);
            if (componentResult.error != null) return componentResult.error;

            WorkflowManager.SnapshotObject(componentResult.component);
            Undo.RegisterCompleteObjectUndo(componentResult.component, "Set Volume Parameter");

            if (!RenderPipelineSkillsCommon.TrySetVolumeParameter(componentResult.component, parameterName, value, overrideState, out var error))
                return new { error };

            RenderPipelineSkillsCommon.MarkDirty(componentResult.profile);
            return new
            {
                success = true,
                profilePath,
                componentType = componentResult.componentType,
                parameterName,
                value = RenderPipelineSkillsCommon.GetVolumeParameterValue(componentResult.component, parameterName),
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(componentResult.component)
            };
        }

        [UnitySkill("volume_set_parameter_batch", "Set multiple parameters on a single VolumeComponent override",
            Category = SkillCategory.Volume, Operation = SkillOperation.Modify,
            Tags = new[] { "volume", "component", "parameter", "batch" },
            Outputs = new[] { "successCount", "failCount", "results" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "componentType", "items" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object VolumeSetParameterBatch(string profilePath, string componentType, string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            var componentResult = GetProfileAndComponent(profilePath, componentType);
            if (componentResult.error != null) return componentResult.error;

            WorkflowManager.SnapshotObject(componentResult.component);
            Undo.RegisterCompleteObjectUndo(componentResult.component, "Set Volume Parameters");

            var result = BatchExecutor.Execute<VolumeParameterBatchItem>(items, item =>
            {
                if (Validate.Required(item.parameterName, "parameterName") is object itemErr)
                    throw new ArgumentException(SkillResultHelper.TryGetError(itemErr, out var message) ? message : "parameterName is required");

                if (!RenderPipelineSkillsCommon.TrySetVolumeParameter(componentResult.component, item.parameterName, item.value, item.overrideState, out var conversionError))
                    throw new ArgumentException(conversionError);

                return new
                {
                    success = true,
                    parameterName = item.parameterName,
                    value = RenderPipelineSkillsCommon.GetVolumeParameterValue(componentResult.component, item.parameterName)
                };
            }, item => item.parameterName);

            RenderPipelineSkillsCommon.MarkDirty(componentResult.profile);
            return result;
        }

        private static (VolumeProfile profile, object error) LoadProfileOrError(string profilePath) =>
            RenderPipelineSkillsCommon.LoadProfileOrError(profilePath);

        private static (string name, Type type, object error) ResolveDescriptor(string componentType)
        {
            if (Validate.Required(componentType, "componentType") is object err) return (null, null, err);

            var descriptor = RenderPipelineSkillsCommon.FindVolumeComponent(componentType);
            if (descriptor == null)
            {
                return (null, null, new
                {
                    error = $"Unsupported VolumeComponent '{componentType}'. Use volume_list_component_types to inspect supported values."
                });
            }

            return (descriptor.Name, descriptor.Type, null);
        }

        private static (VolumeProfile profile, VolumeComponent component, string componentType, object error) GetProfileAndComponent(string profilePath, string componentType)
        {
            var descriptorResult = ResolveDescriptor(componentType);
            if (descriptorResult.error != null) return (null, null, null, descriptorResult.error);

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return (null, null, null, profileResult.error);

            if (!profileResult.profile.TryGet(descriptorResult.type, out VolumeComponent component))
            {
                return (null, null, null, new
                {
                    error = $"Component '{descriptorResult.name}' not found in profile {profilePath}"
                });
            }

            return (profileResult.profile, component, descriptorResult.name, null);
        }

        private sealed class VolumeParameterBatchItem
        {
            public string parameterName { get; set; }
            public object value { get; set; }
            public bool? overrideState { get; set; }
        }
#endif
    }
}
