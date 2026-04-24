using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

#if URP
using UnityEngine.Rendering.Universal;
#endif

namespace UnitySkills
{
    internal enum RenderPipelineSupport
    {
        BuiltIn,
        URP,
        HDRP,
        Custom
    }

    internal sealed class VolumeComponentDescriptor
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public RenderPipelineSupport Pipeline { get; set; }
        public string Group { get; set; }
        public bool IsPostProcess { get; set; }
    }

#if URP
    internal sealed class URPRendererFeatureDescriptor
    {
        public string Name { get; set; }
        public Type Type { get; set; }
        public string Notes { get; set; }
    }
#endif

    internal static class RenderPipelineSkillsCommon
    {
        public static object NoSRP() => new
        {
            error = "Scriptable Render Pipeline Core package (com.unity.render-pipelines.core) is not installed. Install URP or HDRP via Package Manager."
        };

        public static object NoURP() => new
        {
            error = "Universal Render Pipeline package (com.unity.render-pipelines.universal) is not installed. Install URP via Package Manager."
        };

        public static object NoHDRP() => new
        {
            error = "High Definition Render Pipeline package (com.unity.render-pipelines.high-definition) is not installed. Install HDRP via Package Manager."
        };

        public static RenderPipelineSupport DetectPipeline()
        {
            return ProjectSkills.DetectRenderPipeline() switch
            {
                ProjectSkills.RenderPipelineType.URP => RenderPipelineSupport.URP,
                ProjectSkills.RenderPipelineType.HDRP => RenderPipelineSupport.HDRP,
                ProjectSkills.RenderPipelineType.Custom => RenderPipelineSupport.Custom,
                _ => RenderPipelineSupport.BuiltIn
            };
        }

        public static string ResolveAssetSavePath(string savePath, string defaultName, string extension = ".asset")
        {
            if (string.IsNullOrWhiteSpace(savePath))
                savePath = $"Assets/{defaultName}{extension}";

            savePath = savePath.Replace('\\', '/');
            if (!savePath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !savePath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                savePath = "Assets/" + savePath.TrimStart('/');
            }

            if (savePath.EndsWith("/", StringComparison.Ordinal) || !Path.HasExtension(savePath))
                savePath = Path.Combine(savePath.TrimEnd('/'), defaultName + extension).Replace("\\", "/");
            else if (!savePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                savePath += extension;

            return savePath;
        }

        public static void EnsureAssetFolderExists(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory))
                return;

            var parts = directory.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        public static SerializedObject GetGraphicsSettingsObject()
        {
            var graphicsSettings = GraphicsSettings.GetGraphicsSettings();
            return graphicsSettings == null ? null : new SerializedObject(graphicsSettings);
        }

        public static object GetEnumSerializedPropertyValue(SerializedProperty property)
        {
            if (property == null)
                return null;

            return property.propertyType == SerializedPropertyType.Enum
                ? property.enumNames[Mathf.Clamp(property.enumValueIndex, 0, property.enumNames.Length - 1)]
                : property.intValue;
        }

        public static bool TrySetEnumSerializedProperty(SerializedProperty property, string enumName, int? rawValue, out string error)
        {
            error = null;
            if (property == null)
            {
                error = "Serialized property not found";
                return false;
            }

            if (rawValue.HasValue)
            {
                if (property.propertyType == SerializedPropertyType.Enum && rawValue.Value >= 0 && rawValue.Value < property.enumNames.Length)
                    property.enumValueIndex = rawValue.Value;
                else
                    property.intValue = rawValue.Value;
                return true;
            }

            if (string.IsNullOrWhiteSpace(enumName))
            {
                error = "enumName or rawValue is required";
                return false;
            }

            if (property.propertyType != SerializedPropertyType.Enum)
            {
                if (!int.TryParse(enumName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    error = $"Property {property.propertyPath} is not an enum";
                    return false;
                }

                property.intValue = intValue;
                return true;
            }

            for (var i = 0; i < property.enumNames.Length; i++)
            {
                if (string.Equals(property.enumNames[i], enumName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.enumDisplayNames[i], enumName, StringComparison.OrdinalIgnoreCase))
                {
                    property.enumValueIndex = i;
                    return true;
                }
            }

            error = $"Invalid enum value '{enumName}'. Valid values: {string.Join(", ", property.enumNames)}";
            return false;
        }

        public static object DescribePipelineAsset(RenderPipelineAsset asset)
        {
            if (asset == null)
                return null;

            var path = AssetDatabase.GetAssetPath(asset);
            return new
            {
                name = asset.name,
                assetType = asset.GetType().Name,
                path = string.IsNullOrEmpty(path) ? null : path
            };
        }

        public static object DescribeUnityObject(UnityEngine.Object asset)
        {
            if (asset == null)
                return null;

            var path = AssetDatabase.GetAssetPath(asset);
            return new
            {
                name = asset.name,
                path = string.IsNullOrEmpty(path) ? null : path,
                instanceId = asset.GetInstanceID(),
                type = asset.GetType().Name
            };
        }

        // Cached registries: postProcessOnly=true and postProcessOnly=false
        private static IReadOnlyList<VolumeComponentDescriptor> _registryAll;
        private static IReadOnlyList<VolumeComponentDescriptor> _registryPostProcess;

        public static IReadOnlyList<VolumeComponentDescriptor> GetVolumeComponentRegistry(bool postProcessOnly = false)
        {
            var cached = postProcessOnly ? _registryPostProcess : _registryAll;
            if (cached != null) return cached;

            var descriptors = new List<VolumeComponentDescriptor>();

#if URP
            descriptors.AddRange(new[]
            {
                CreateDescriptor("Bloom", typeof(UnityEngine.Rendering.Universal.Bloom), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ChromaticAberration", typeof(UnityEngine.Rendering.Universal.ChromaticAberration), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ColorAdjustments", typeof(UnityEngine.Rendering.Universal.ColorAdjustments), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ColorCurves", typeof(UnityEngine.Rendering.Universal.ColorCurves), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ColorLookup", typeof(UnityEngine.Rendering.Universal.ColorLookup), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ChannelMixer", typeof(UnityEngine.Rendering.Universal.ChannelMixer), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("DepthOfField", typeof(UnityEngine.Rendering.Universal.DepthOfField), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("FilmGrain", typeof(UnityEngine.Rendering.Universal.FilmGrain), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("LensDistortion", typeof(UnityEngine.Rendering.Universal.LensDistortion), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("LiftGammaGain", typeof(UnityEngine.Rendering.Universal.LiftGammaGain), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("MotionBlur", typeof(UnityEngine.Rendering.Universal.MotionBlur), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("PaniniProjection", typeof(UnityEngine.Rendering.Universal.PaniniProjection), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("ShadowsMidtonesHighlights", typeof(UnityEngine.Rendering.Universal.ShadowsMidtonesHighlights), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("SplitToning", typeof(UnityEngine.Rendering.Universal.SplitToning), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("Tonemapping", typeof(UnityEngine.Rendering.Universal.Tonemapping), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("Vignette", typeof(UnityEngine.Rendering.Universal.Vignette), RenderPipelineSupport.URP, "Post-processing", true),
                CreateDescriptor("WhiteBalance", typeof(UnityEngine.Rendering.Universal.WhiteBalance), RenderPipelineSupport.URP, "Post-processing", true)
            });
#endif

#if HDRP
            descriptors.AddRange(new[]
            {
                CreateDescriptor("Bloom", typeof(UnityEngine.Rendering.HighDefinition.Bloom), RenderPipelineSupport.HDRP, "Post-processing", true),
                CreateDescriptor("ColorAdjustments", typeof(UnityEngine.Rendering.HighDefinition.ColorAdjustments), RenderPipelineSupport.HDRP, "Post-processing", true),
                CreateDescriptor("DepthOfField", typeof(UnityEngine.Rendering.HighDefinition.DepthOfField), RenderPipelineSupport.HDRP, "Post-processing", true),
                CreateDescriptor("Tonemapping", typeof(UnityEngine.Rendering.HighDefinition.Tonemapping), RenderPipelineSupport.HDRP, "Post-processing", true),
                CreateDescriptor("Vignette", typeof(UnityEngine.Rendering.HighDefinition.Vignette), RenderPipelineSupport.HDRP, "Post-processing", true)
            });
#endif

            var pipeline = DetectPipeline();
            IEnumerable<VolumeComponentDescriptor> filtered = descriptors;
            if (pipeline == RenderPipelineSupport.URP)
                filtered = filtered.Where(x => x.Pipeline == RenderPipelineSupport.URP);
            else if (pipeline == RenderPipelineSupport.HDRP)
                filtered = filtered.Where(x => x.Pipeline == RenderPipelineSupport.HDRP);

            if (postProcessOnly)
                filtered = filtered.Where(x => x.IsPostProcess);

            var result = filtered
                .OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (postProcessOnly) _registryPostProcess = result;
            else _registryAll = result;
            return result;
        }

        public static VolumeComponentDescriptor FindVolumeComponent(string componentType, bool postProcessOnly = false)
        {
            return GetVolumeComponentRegistry(postProcessOnly)
                .FirstOrDefault(x => string.Equals(x.Name, componentType, StringComparison.OrdinalIgnoreCase));
        }

#if SRP_CORE
        public static VolumeProfile LoadVolumeProfile(string profilePath)
        {
            return string.IsNullOrWhiteSpace(profilePath)
                ? null
                : AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
        }

        public static (VolumeProfile profile, object error) LoadProfileOrError(string profilePath)
        {
            if (Validate.Required(profilePath, "profilePath") is object err) return (null, err);
            if (Validate.SafePath(profilePath, "profilePath") is object pathErr) return (null, pathErr);

            var profile = LoadVolumeProfile(profilePath);
            if (profile == null)
                return (null, new { error = $"VolumeProfile not found: {profilePath}" });

            return (profile, null);
        }

        public static VolumeComponent GetOrAddVolumeComponent(VolumeProfile profile, Type componentType, bool overrides = true)
        {
            if (profile.TryGet(componentType, out VolumeComponent existing))
                return existing;

            var component = profile.Add(componentType, overrides);
            AttachComponentToProfileAsset(profile, component);
            return component;
        }

        public static void AttachComponentToProfileAsset(VolumeProfile profile, VolumeComponent component)
        {
            var profilePath = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(profilePath) || component == null)
                return;

            var componentPath = AssetDatabase.GetAssetPath(component);
            if (!string.IsNullOrEmpty(componentPath))
                return;

            AssetDatabase.AddObjectToAsset(component, profile);
        }

        public static object DescribeVolumeComponent(VolumeComponent component)
        {
            if (component == null)
                return null;

            var parameters = EnumerateVolumeParameters(component)
                .Select(x => new
                {
                    name = x.Name,
                    type = GetFriendlyTypeName(GetVolumeParameterValueType(x.Parameter)),
                    overrideState = x.Parameter.overrideState,
                    value = ToSerializableValue(GetRawVolumeParameterValue(x.Parameter))
                })
                .OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new
            {
                componentType = component.GetType().Name,
                active = component.active,
                parameterCount = parameters.Length,
                parameters
            };
        }

        public static bool TrySetVolumeParameter(VolumeComponent component, string parameterName, object value, bool? overrideState, out string error)
        {
            error = null;
            if (component == null)
            {
                error = "Volume component is null";
                return false;
            }

            if (string.IsNullOrWhiteSpace(parameterName))
            {
                error = "parameterName is required";
                return false;
            }

            var binding = EnumerateVolumeParameters(component)
                .FirstOrDefault(x => string.Equals(x.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            if (binding == null)
            {
                error = $"Parameter '{parameterName}' not found on {component.GetType().Name}";
                return false;
            }

            var targetType = GetVolumeParameterValueType(binding.Parameter);
            if (!TryConvertRawValue(value, targetType, out var converted, out error))
                return false;

            var property = binding.Parameter.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                error = $"Parameter '{parameterName}' does not expose a writable value";
                return false;
            }

            property.SetValue(binding.Parameter, converted);
            binding.Parameter.overrideState = overrideState ?? true;
            return true;
        }

        public static object GetVolumeParameterValue(VolumeComponent component, string parameterName)
        {
            if (component == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            var binding = EnumerateVolumeParameters(component)
                .FirstOrDefault(x => string.Equals(x.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            return binding == null ? null : ToSerializableValue(GetRawVolumeParameterValue(binding.Parameter));
        }

        public static Type GetVolumeParameterValueType(VolumeParameter parameter)
        {
            var type = parameter?.GetType();
            while (type != null)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(VolumeParameter<>))
                    return type.GetGenericArguments()[0];
                type = type.BaseType;
            }

            return typeof(object);
        }
#endif

        public static void MarkDirty(UnityEngine.Object target)
        {
            if (target == null)
                return;

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
        }

        private static VolumeComponentDescriptor CreateDescriptor(string name, Type type, RenderPipelineSupport pipeline, string group, bool isPostProcess)
        {
            return new VolumeComponentDescriptor
            {
                Name = name,
                Type = type,
                Pipeline = pipeline,
                Group = group,
                IsPostProcess = isPostProcess
            };
        }

#if SRP_CORE
        private sealed class VolumeParameterBinding
        {
            public string Name { get; set; }
            public VolumeParameter Parameter { get; set; }
        }

        private static IEnumerable<VolumeParameterBinding> EnumerateVolumeParameters(object source, string prefix = null)
        {
            if (source == null)
                yield break;

            var fields = source.GetType()
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderBy(x => x.MetadataToken);

            foreach (var field in fields)
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(field.FieldType))
                    continue;

                var parameter = field.GetValue(source) as VolumeParameter;
                if (parameter == null)
                    continue;

                yield return new VolumeParameterBinding
                {
                    Name = string.IsNullOrEmpty(prefix) ? field.Name : $"{prefix}.{field.Name}",
                    Parameter = parameter
                };
            }
        }
#endif

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == null)
                return "object";
            if (type.IsEnum)
                return type.Name;
            if (!type.IsGenericType)
                return type.Name;

            var typeName = type.Name;
            var tickIndex = typeName.IndexOf('`');
            if (tickIndex >= 0)
                typeName = typeName.Substring(0, tickIndex);
            return $"{typeName}<{string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName))}>";
        }

#if SRP_CORE
        private static object GetRawVolumeParameterValue(VolumeParameter parameter)
        {
            if (parameter == null)
                return null;

            var property = parameter.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(parameter);
        }
#endif

        public static object ToSerializableValue(object value)
        {
            return value switch
            {
                null => null,
                UnityEngine.Object unityObject => DescribeUnityObject(unityObject),
                Color color => new
                {
                    r = color.r,
                    g = color.g,
                    b = color.b,
                    a = color.a
                },
                Vector2 vector2 => new
                {
                    x = vector2.x,
                    y = vector2.y
                },
                Vector3 vector3 => new
                {
                    x = vector3.x,
                    y = vector3.y,
                    z = vector3.z
                },
                Vector4 vector4 => new
                {
                    x = vector4.x,
                    y = vector4.y,
                    z = vector4.z,
                    w = vector4.w
                },
                Vector2Int vector2Int => new
                {
                    x = vector2Int.x,
                    y = vector2Int.y
                },
                Vector3Int vector3Int => new
                {
                    x = vector3Int.x,
                    y = vector3Int.y,
                    z = vector3Int.z
                },
                Quaternion quaternion => new
                {
                    x = quaternion.x,
                    y = quaternion.y,
                    z = quaternion.z,
                    w = quaternion.w
                },
                Enum enumValue => enumValue.ToString(),
                _ => value
            };
        }

        private static bool TryConvertRawValue(object rawValue, Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            if (targetType == typeof(object))
            {
                converted = rawValue;
                return true;
            }

            if (rawValue == null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                {
                    converted = null;
                    return true;
                }

                error = $"Null cannot be assigned to {targetType.Name}";
                return false;
            }

            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            try
            {
                if (typeof(UnityEngine.Object).IsAssignableFrom(underlying))
                {
                    if (rawValue is string assetPath)
                    {
                        converted = AssetDatabase.LoadAssetAtPath(assetPath, underlying);
                        if (converted == null)
                        {
                            error = $"Asset not found or type mismatch: {assetPath}";
                            return false;
                        }

                        return true;
                    }

                    error = $"{underlying.Name} parameters require an asset path string";
                    return false;
                }

                if (underlying == typeof(Color) && rawValue is string colorString)
                {
                    if (!TryParseColor(colorString, out var color))
                    {
                        error = $"Invalid color string: {colorString}";
                        return false;
                    }

                    converted = color;
                    return true;
                }

                if (underlying == typeof(Vector2) && rawValue is string vector2String)
                {
                    if (!TryParseVector(vector2String, 2, out var vector2))
                    {
                        error = $"Invalid Vector2 string: {vector2String}";
                        return false;
                    }

                    converted = new Vector2(vector2[0], vector2[1]);
                    return true;
                }

                if (underlying == typeof(Vector3) && rawValue is string vector3String)
                {
                    if (!TryParseVector(vector3String, 3, out var vector3))
                    {
                        error = $"Invalid Vector3 string: {vector3String}";
                        return false;
                    }

                    converted = new Vector3(vector3[0], vector3[1], vector3[2]);
                    return true;
                }

                if (underlying == typeof(Vector4) && rawValue is string vector4String)
                {
                    if (!TryParseVector(vector4String, 4, out var vector4))
                    {
                        error = $"Invalid Vector4 string: {vector4String}";
                        return false;
                    }

                    converted = new Vector4(vector4[0], vector4[1], vector4[2], vector4[3]);
                    return true;
                }

                if (underlying.IsEnum && rawValue is string enumString)
                {
                    converted = Enum.Parse(underlying, enumString, true);
                    return true;
                }

                converted = JToken.FromObject(rawValue).ToObject(underlying);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to convert value to {underlying.Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryParseColor(string input, out Color color)
        {
            color = default;
            if (ColorUtility.TryParseHtmlString(input, out color))
                return true;

            if (!TryParseVector(input, 4, out var rgba))
                return false;

            color = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
            return true;
        }

        public static bool TryParseVector(string input, int expectedCount, out float[] values)
        {
            values = null;
            var parts = input.Split(',')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToArray();
            if (parts.Length != expectedCount)
                return false;

            var parsed = new float[expectedCount];
            for (var i = 0; i < expectedCount; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                    return false;
            }

            values = parsed;
            return true;
        }
    }

#if URP
    internal static class URPRendererFeatureHelper
    {
        private static readonly Dictionary<string, URPRendererFeatureDescriptor> CreatableFeatures =
            new Dictionary<string, URPRendererFeatureDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                ["DecalRendererFeature"] = new URPRendererFeatureDescriptor
                {
                    Name = "DecalRendererFeature",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.DecalRendererFeature"),
                    Notes = "URP decal projection support"
                },
                ["RenderObjects"] = new URPRendererFeatureDescriptor
                {
                    Name = "RenderObjects",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.RenderObjects"),
                    Notes = "Filter and draw renderers with custom overrides"
                },
                ["ScreenSpaceAmbientOcclusion"] = new URPRendererFeatureDescriptor
                {
                    Name = "ScreenSpaceAmbientOcclusion",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion"),
                    Notes = "Screen-space ambient occlusion"
                },
                ["FullScreenPassRendererFeature"] = new URPRendererFeatureDescriptor
                {
                    Name = "FullScreenPassRendererFeature",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.FullScreenPassRendererFeature"),
                    Notes = "Inject a fullscreen pass"
                },
                ["ScreenSpaceReflectionRendererFeature"] = new URPRendererFeatureDescriptor
                {
                    Name = "ScreenSpaceReflectionRendererFeature",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.ScreenSpaceReflectionRendererFeature"),
                    Notes = "Screen-space reflections"
                },
                ["SurfaceCacheGIRendererFeature"] = new URPRendererFeatureDescriptor
                {
                    Name = "SurfaceCacheGIRendererFeature",
                    Type = ResolveURPType(
                        "UnityEngine.Rendering.Universal.SurfaceCacheGIRendererFeature"),
                    Notes = "Surface cache GI support"
                }
            };

        public static IReadOnlyCollection<URPRendererFeatureDescriptor> GetCreatableFeatures()
        {
            return CreatableFeatures.Values
                .Where(x => x.Type != null && typeof(ScriptableRendererFeature).IsAssignableFrom(x.Type))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static URPRendererFeatureDescriptor FindCreatableFeature(string featureType)
        {
            if (string.IsNullOrWhiteSpace(featureType) || !CreatableFeatures.TryGetValue(featureType, out var descriptor))
                return null;

            return descriptor.Type != null && typeof(ScriptableRendererFeature).IsAssignableFrom(descriptor.Type)
                ? descriptor
                : null;
        }

        public static UniversalRenderPipelineAsset GetCurrentAsset(string assetPath = null)
        {
            if (!string.IsNullOrWhiteSpace(assetPath))
                return AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(assetPath);

            return GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset
                   ?? GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        }

        public static int GetDefaultRendererIndex(UniversalRenderPipelineAsset asset)
        {
            var serializedObject = new SerializedObject(asset);
            return serializedObject.FindProperty("m_DefaultRendererIndex")?.intValue ?? 0;
        }

        public static bool TryGetRendererData(
            UniversalRenderPipelineAsset asset,
            int rendererIndex,
            string rendererDataPath,
            out UniversalRendererData rendererData,
            out int resolvedIndex,
            out string error)
        {
            rendererData = null;
            resolvedIndex = -1;
            error = null;

            if (asset == null)
            {
                error = "URP asset not found";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rendererDataPath))
            {
                rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererDataPath);
                if (rendererData == null)
                {
                    error = $"UniversalRendererData not found: {rendererDataPath}";
                    return false;
                }

                var rendererDataList = GetRendererDataList(asset);
                for (var i = 0; i < rendererDataList.Length; i++)
                {
                    if (ReferenceEquals(rendererDataList[i], rendererData))
                    {
                        resolvedIndex = i;
                        return true;
                    }
                }

                error = $"RendererData '{rendererData.name}' does not belong to URP asset '{asset.name}'";
                return false;
            }

            if (rendererIndex < 0)
                rendererIndex = GetDefaultRendererIndex(asset);

            var list = GetRendererDataList(asset);
            if (rendererIndex >= list.Length)
            {
                error = $"Renderer index out of range: {rendererIndex}";
                return false;
            }

            rendererData = list[rendererIndex] as UniversalRendererData;
            if (rendererData == null)
            {
                error = $"Renderer at index {rendererIndex} is not a UniversalRendererData";
                return false;
            }

            resolvedIndex = rendererIndex;
            return true;
        }

        public static ScriptableRendererData[] GetRendererDataList(UniversalRenderPipelineAsset asset)
        {
            if (asset == null)
                return Array.Empty<ScriptableRendererData>();

            var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(asset) is ScriptableRendererData[] rendererDataList && rendererDataList != null)
                return rendererDataList;

            var serializedObject = new SerializedObject(asset);
            var property = serializedObject.FindProperty("m_RendererDataList");
            if (property == null || !property.isArray)
                return Array.Empty<ScriptableRendererData>();

            var results = new ScriptableRendererData[property.arraySize];
            for (var i = 0; i < property.arraySize; i++)
                results[i] = property.GetArrayElementAtIndex(i).objectReferenceValue as ScriptableRendererData;
            return results;
        }

        public static void SyncRendererFeatureMap(ScriptableRendererData rendererData)
        {
            var mapField = typeof(ScriptableRendererData).GetField("m_RendererFeatureMap", BindingFlags.Instance | BindingFlags.NonPublic);
            if (mapField?.GetValue(rendererData) is not List<long> map)
                return;

            map.Clear();
            foreach (var feature in rendererData.rendererFeatures)
            {
                if (feature != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId))
                    map.Add(localId);
                else
                    map.Add(0);
            }
        }

        public static void MarkRendererDataDirty(ScriptableRendererData rendererData)
        {
            rendererData.SetDirty();
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
        }

        private static Type ResolveURPType(string fullName) => SkillsCommon.FindTypeByName(fullName);
    }
#endif
}
