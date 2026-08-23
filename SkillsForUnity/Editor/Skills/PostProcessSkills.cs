using System;
using System.Linq;
using System.Reflection;
using UnityEditor;

#if SRP_CORE
using UnityEngine.Rendering;
#endif

namespace UnitySkills
{
    /// <summary>
    /// 基于 Volume 框架的现代 SRP 后处理技能。
    /// </summary>
    public static class PostProcessSkills
    {
#if !SRP_CORE
        [UnitySkill("postprocess_list_effects", "List post-processing effects supported by the active SRP pipeline",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Query,
            Tags = new[] { "postprocess", "effects", "list", "pipeline" },
            Outputs = new[] { "count", "effects" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PostProcessListEffects() => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_add_effect", "Add a post-processing effect override to a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Create,
            Tags = new[] { "postprocess", "effect", "add", "profile" },
            Outputs = new[] { "effectType", "profilePath" })]
        public static object PostProcessAddEffect(string profilePath, string effectType, bool overrides = true) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_remove_effect", "Remove a post-processing effect override from a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Delete,
            Tags = new[] { "postprocess", "effect", "remove", "profile" },
            Outputs = new[] { "effectType", "profilePath" },
            RiskLevel = "medium")]
        public static object PostProcessRemoveEffect(string profilePath, string effectType) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_get_effect", "Inspect a post-processing effect override on a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Query,
            Tags = new[] { "postprocess", "effect", "inspect", "profile" },
            Outputs = new[] { "effectType", "parameters" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PostProcessGetEffect(string profilePath, string effectType) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_parameter", "Set a parameter on a post-processing effect override",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "effect", "parameter", "set" },
            Outputs = new[] { "effectType", "parameterName", "value" })]
        public static object PostProcessSetParameter(string profilePath, string effectType, string parameterName, object value, bool? overrideState = true) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_bloom", "Configure the Bloom post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "bloom", "configure" },
            Outputs = new[] { "effectType", "parameters" })]
        public static object PostProcessSetBloom(string profilePath, float? intensity = null, float? threshold = null, float? scatter = null, string tint = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_depth_of_field", "Configure the Depth Of Field post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "depth of field", "configure" },
            Outputs = new[] { "effectType", "parameters" })]
        public static object PostProcessSetDepthOfField(string profilePath, string mode = null, float? focusDistance = null, float? gaussianStart = null, float? gaussianEnd = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_tonemapping", "Configure the Tonemapping post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "tonemapping", "configure" },
            Outputs = new[] { "effectType", "parameters" })]
        public static object PostProcessSetTonemapping(string profilePath, string mode = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_vignette", "Configure the Vignette post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "vignette", "configure" },
            Outputs = new[] { "effectType", "parameters" })]
        public static object PostProcessSetVignette(string profilePath, float? intensity = null, float? smoothness = null, string color = null, string center = null, bool? rounded = null) => RenderPipelineSkillsCommon.NoSRP();

        [UnitySkill("postprocess_set_color_adjustments", "Configure the Color Adjustments post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "color adjustments", "configure" },
            Outputs = new[] { "effectType", "parameters" })]
        public static object PostProcessSetColorAdjustments(string profilePath, float? postExposure = null, float? contrast = null, string colorFilter = null, float? hueShift = null, float? saturation = null) => RenderPipelineSkillsCommon.NoSRP();
#else
        [UnitySkill("postprocess_list_effects", "List post-processing effects supported by the active SRP pipeline",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Query,
            Tags = new[] { "postprocess", "effects", "list", "pipeline" },
            Outputs = new[] { "count", "effects" },
            ReadOnly = true,
            RequiresPackages = new[] { "com.unity.render-pipelines.core" },
            Mode = SkillMode.SemiAuto)]
        public static object PostProcessListEffects()
        {
            var pipeline = RenderPipelineSkillsCommon.DetectPipeline();
            if (pipeline == RenderPipelineSupport.BuiltIn)
                return new { error = "Built-in Render Pipeline does not support these post-processing skills." };

            var effects = RenderPipelineSkillsCommon.GetVolumeComponentRegistry(postProcessOnly: true);
            return new
            {
                success = true,
                pipeline = pipeline.ToString(),
                count = effects.Count,
                effects = effects.Select(x => new
                {
                    name = x.Name,
                    pipeline = x.Pipeline.ToString(),
                    group = x.Group
                }).ToArray()
            };
        }

        [UnitySkill("postprocess_add_effect", "Add a post-processing effect override to a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Create,
            Tags = new[] { "postprocess", "effect", "add", "profile" },
            Outputs = new[] { "effectType", "profilePath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "effectType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessAddEffect(string profilePath, string effectType, bool overrides = true)
        {
            var result = GetOrAddEffect(profilePath, effectType, overrides);
            if (result.error != null) return result.error;

            return new
            {
                success = true,
                profilePath,
                effectType = result.descriptor.Name,
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(result.component)
            };
        }

        [UnitySkill("postprocess_remove_effect", "Remove a post-processing effect override from a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Delete,
            Tags = new[] { "postprocess", "effect", "remove", "profile" },
            Outputs = new[] { "effectType", "profilePath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "effectType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" },
            RiskLevel = "medium")]
        public static object PostProcessRemoveEffect(string profilePath, string effectType)
        {
            var descriptorResult = ResolveEffect(effectType);
            if (descriptorResult.error != null) return descriptorResult.error;

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return profileResult.error;

            if (!profileResult.profile.TryGet(descriptorResult.descriptor.Type, out VolumeComponent component))
                return new { error = $"Effect '{descriptorResult.descriptor.Name}' not found in profile {profilePath}" };

            WorkflowManager.SnapshotObject(profileResult.profile);
            Undo.RegisterCompleteObjectUndo(profileResult.profile, "Remove Post Process Effect");
            profileResult.profile.Remove(descriptorResult.descriptor.Type);
            if (component != null)
                Undo.DestroyObjectImmediate(component);
            RenderPipelineSkillsCommon.MarkDirty(profileResult.profile);

            return new
            {
                success = true,
                profilePath,
                effectType = descriptorResult.descriptor.Name
            };
        }

        [UnitySkill("postprocess_get_effect", "Inspect a post-processing effect override on a VolumeProfile",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Query,
            Tags = new[] { "postprocess", "effect", "inspect", "profile" },
            Outputs = new[] { "effectType", "parameters" },
            ReadOnly = true,
            RequiresInput = new[] { "profilePath", "effectType" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" },
            Mode = SkillMode.SemiAuto)]
        public static object PostProcessGetEffect(string profilePath, string effectType)
        {
            var effectResult = GetEffect(profilePath, effectType);
            if (effectResult.error != null) return effectResult.error;

            return new
            {
                success = true,
                profilePath,
                effectType = effectResult.descriptor.Name,
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(effectResult.component)
            };
        }

        [UnitySkill("postprocess_set_parameter", "Set a parameter on a post-processing effect override",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "effect", "parameter", "set" },
            Outputs = new[] { "effectType", "parameterName", "value" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath", "effectType", "parameterName" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetParameter(string profilePath, string effectType, string parameterName, object value, bool? overrideState = true)
        {
            var effectResult = GetOrAddEffect(profilePath, effectType, overrides: true);
            if (effectResult.error != null) return effectResult.error;

            WorkflowManager.SnapshotObject(effectResult.component);
            Undo.RegisterCompleteObjectUndo(effectResult.component, "Set Post Process Parameter");

            if (!RenderPipelineSkillsCommon.TrySetVolumeParameter(effectResult.component, parameterName, value, overrideState, out var error))
                return new { error };

            RenderPipelineSkillsCommon.MarkDirty(effectResult.profile);
            return new
            {
                success = true,
                profilePath,
                effectType = effectResult.descriptor.Name,
                parameterName,
                value = RenderPipelineSkillsCommon.GetVolumeParameterValue(effectResult.component, parameterName),
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(effectResult.component)
            };
        }

        [UnitySkill("postprocess_set_bloom", "Configure the Bloom post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "bloom", "configure" },
            Outputs = new[] { "effectType", "parameters" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetBloom(string profilePath, float? intensity = null, float? threshold = null, float? scatter = null, string tint = null)
        {
            var result = GetOrAddEffect(profilePath, "Bloom", overrides: true);
            if (result.error != null) return result.error;

            if (intensity.HasValue && !SetParameter(result.component, result.profile, intensity.Value, out var e1, "intensity")) return new { error = e1 };
            if (threshold.HasValue && !SetParameter(result.component, result.profile, threshold.Value, out var e2, "threshold")) return new { error = e2 };
            if (scatter.HasValue && !SetParameter(result.component, result.profile, scatter.Value, out var e3, "scatter")) return new { error = e3 };
            if (!string.IsNullOrWhiteSpace(tint) && !SetParameter(result.component, result.profile, tint, out var e4, "tint")) return new { error = e4 };

            return EffectResponse(profilePath, result.descriptor.Name, result.component);
        }

        [UnitySkill("postprocess_set_depth_of_field", "Configure the Depth Of Field post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "depth of field", "configure" },
            Outputs = new[] { "effectType", "parameters" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetDepthOfField(string profilePath, string mode = null, float? focusDistance = null, float? gaussianStart = null, float? gaussianEnd = null)
        {
            var result = GetOrAddEffect(profilePath, "DepthOfField", overrides: true);
            if (result.error != null) return result.error;

            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (ValidateModeParameter(result.component, mode, "mode", "focusMode") is object modeErr) return modeErr;
                if (!SetParameter(result.component, result.profile, mode, out var e1, "mode", "focusMode")) return new { error = e1 };
            }
            if (focusDistance.HasValue && !SetParameter(result.component, result.profile, focusDistance.Value, out var e2, "focusDistance")) return new { error = e2 };
            if (gaussianStart.HasValue && !SetParameter(result.component, result.profile, gaussianStart.Value, out var e3, "gaussianStart", "nearFocusStart")) return new { error = e3 };
            if (gaussianEnd.HasValue && !SetParameter(result.component, result.profile, gaussianEnd.Value, out var e4, "gaussianEnd", "farFocusStart")) return new { error = e4 };

            return EffectResponse(profilePath, result.descriptor.Name, result.component);
        }

        [UnitySkill("postprocess_set_tonemapping", "Configure the Tonemapping post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "tonemapping", "configure" },
            Outputs = new[] { "effectType", "parameters" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetTonemapping(string profilePath, string mode = null)
        {
            var result = GetOrAddEffect(profilePath, "Tonemapping", overrides: true);
            if (result.error != null) return result.error;

            if (!string.IsNullOrWhiteSpace(mode))
            {
                if (ValidateModeParameter(result.component, mode, "mode") is object modeErr) return modeErr;
                if (!SetParameter(result.component, result.profile, mode, out var e1, "mode")) return new { error = e1 };
            }
            return EffectResponse(profilePath, result.descriptor.Name, result.component);
        }

        [UnitySkill("postprocess_set_vignette", "Configure the Vignette post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "vignette", "configure" },
            Outputs = new[] { "effectType", "parameters" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetVignette(string profilePath, float? intensity = null, float? smoothness = null, string color = null, string center = null, bool? rounded = null)
        {
            var result = GetOrAddEffect(profilePath, "Vignette", overrides: true);
            if (result.error != null) return result.error;

            if (intensity.HasValue && !SetParameter(result.component, result.profile, intensity.Value, out var e1, "intensity")) return new { error = e1 };
            if (smoothness.HasValue && !SetParameter(result.component, result.profile, smoothness.Value, out var e2, "smoothness")) return new { error = e2 };
            if (!string.IsNullOrWhiteSpace(color) && !SetParameter(result.component, result.profile, color, out var e3, "color")) return new { error = e3 };
            if (!string.IsNullOrWhiteSpace(center) && !SetParameter(result.component, result.profile, center, out var e4, "center")) return new { error = e4 };
            if (rounded.HasValue && !SetParameter(result.component, result.profile, rounded.Value, out var e5, "rounded")) return new { error = e5 };

            return EffectResponse(profilePath, result.descriptor.Name, result.component);
        }

        [UnitySkill("postprocess_set_color_adjustments", "Configure the Color Adjustments post-processing effect",
            Category = SkillCategory.PostProcess, Operation = SkillOperation.Modify,
            Tags = new[] { "postprocess", "color adjustments", "configure" },
            Outputs = new[] { "effectType", "parameters" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresInput = new[] { "profilePath" },
            RequiresPackages = new[] { "com.unity.render-pipelines.core" })]
        public static object PostProcessSetColorAdjustments(string profilePath, float? postExposure = null, float? contrast = null, string colorFilter = null, float? hueShift = null, float? saturation = null)
        {
            var result = GetOrAddEffect(profilePath, "ColorAdjustments", overrides: true);
            if (result.error != null) return result.error;

            if (postExposure.HasValue && !SetParameter(result.component, result.profile, postExposure.Value, out var e1, "postExposure")) return new { error = e1 };
            if (contrast.HasValue && !SetParameter(result.component, result.profile, contrast.Value, out var e2, "contrast")) return new { error = e2 };
            if (!string.IsNullOrWhiteSpace(colorFilter) && !SetParameter(result.component, result.profile, colorFilter, out var e3, "colorFilter")) return new { error = e3 };
            if (hueShift.HasValue && !SetParameter(result.component, result.profile, hueShift.Value, out var e4, "hueShift")) return new { error = e4 };
            if (saturation.HasValue && !SetParameter(result.component, result.profile, saturation.Value, out var e5, "saturation")) return new { error = e5 };

            return EffectResponse(profilePath, result.descriptor.Name, result.component);
        }

        /// <summary>
        /// 在 <see cref="SetParameter"/> 调入 RenderPipelineSkillsCommon 之前，先校验枚举形态的
        /// "mode" 类参数。没有这一步，非法字符串（mode="NotARealMode"）会在该 helper 深处的
        /// Enum.Parse 失败，而 SetParameter 的别名循环把每次尝试的错误丢弃（<c>out _</c>），
        /// 调用方最终只看到 "None of the parameters matched: mode, focusMode"（SKILL_ERROR/abort）——
        /// 值非法与组件压根没这个参数，两种情况读起来一模一样。此处反射组件自身的字段拿到真实枚举
        /// 类型（正是 RenderPipelineSkillsCommon.TrySetVolumeParameter 将要写入的那个字段），
        /// 非法值于是能拿到模块统一的 SEMANTIC_INVALID + validValues 形态。
        /// <paramref name="parameterNames"/> 是按顺序尝试的别名（URP/HDRP 字段名不同），
        /// 该组件上第一个存在的字段决定取值词表。
        /// </summary>
        private static object ValidateModeParameter(VolumeComponent component, string value, params string[] parameterNames)
        {
            if (component == null || string.IsNullOrWhiteSpace(value))
                return null;

            foreach (var parameterName in parameterNames)
            {
                var field = component.GetType()
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(f => string.Equals(f.Name, parameterName, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    continue;

                if (!(field.GetValue(component) is VolumeParameter parameter))
                    return null;

                var valueType = RenderPipelineSkillsCommon.GetVolumeParameterValueType(parameter);
                if (valueType == null || !valueType.IsEnum)
                    return null;

                var validValues = Enum.GetNames(valueType);
                if (validValues.Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)))
                    return null;

                return SkillParamUtil.InvalidValueError(value, "mode", validValues);
            }

            return null;
        }

        private static bool SetParameter(VolumeComponent component, VolumeProfile profile, object value, out string error, params string[] parameterNames)
        {
            WorkflowManager.SnapshotObject(component);
            Undo.RegisterCompleteObjectUndo(component, "Set Post Process Parameter");

            foreach (var parameterName in parameterNames)
            {
                if (RenderPipelineSkillsCommon.TrySetVolumeParameter(component, parameterName, value, true, out _))
                {
                    RenderPipelineSkillsCommon.MarkDirty(profile);
                    error = null;
                    return true;
                }
            }

            error = $"None of the parameters matched: {string.Join(", ", parameterNames)}";
            return false;
        }

        private static object EffectResponse(string profilePath, string effectType, VolumeComponent component)
        {
            return new
            {
                success = true,
                profilePath,
                effectType,
                component = RenderPipelineSkillsCommon.DescribeVolumeComponent(component)
            };
        }

        private static (VolumeProfile profile, object error) LoadProfileOrError(string profilePath) =>
            RenderPipelineSkillsCommon.LoadProfileOrError(profilePath);

        private static (VolumeComponentDescriptor descriptor, object error) ResolveEffect(string effectType)
        {
            if (Validate.Required(effectType, "effectType") is object err) return (null, err);

            var descriptor = RenderPipelineSkillsCommon.FindVolumeComponent(effectType, postProcessOnly: true);
            if (descriptor == null)
            {
                return (null, new
                {
                    error = $"Unsupported post-process effect '{effectType}'. Use postprocess_list_effects to inspect supported values."
                });
            }

            return (descriptor, null);
        }

        private static (VolumeProfile profile, VolumeComponent component, VolumeComponentDescriptor descriptor, object error) GetEffect(string profilePath, string effectType)
        {
            var descriptorResult = ResolveEffect(effectType);
            if (descriptorResult.error != null) return (null, null, null, descriptorResult.error);

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return (null, null, null, profileResult.error);

            if (!profileResult.profile.TryGet(descriptorResult.descriptor.Type, out VolumeComponent component))
            {
                return (null, null, null, new
                {
                    error = $"Effect '{descriptorResult.descriptor.Name}' not found in profile {profilePath}"
                });
            }

            return (profileResult.profile, component, descriptorResult.descriptor, null);
        }

        private static (VolumeProfile profile, VolumeComponent component, VolumeComponentDescriptor descriptor, object error) GetOrAddEffect(string profilePath, string effectType, bool overrides)
        {
            var descriptorResult = ResolveEffect(effectType);
            if (descriptorResult.error != null) return (null, null, null, descriptorResult.error);

            var profileResult = LoadProfileOrError(profilePath);
            if (profileResult.error != null) return (null, null, null, profileResult.error);

            var component = RenderPipelineSkillsCommon.GetOrAddVolumeComponent(profileResult.profile, descriptorResult.descriptor.Type, overrides);
            RenderPipelineSkillsCommon.MarkDirty(profileResult.profile);
            return (profileResult.profile, component, descriptorResult.descriptor, null);
        }
#endif
    }
}

// Producer:Betsy
