using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnitySkills
{
    /// <summary>
    /// Graphics and quality settings skills for SRP-aware projects.
    /// </summary>
    public static class GraphicsSkills
    {
        [UnitySkill("graphics_get_overview", "Get an overview of graphics, quality, and render pipeline settings",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Query,
            Tags = new[] { "graphics", "quality", "render pipeline", "settings", "overview" },
            Outputs = new[] { "currentQuality", "defaultRenderPipeline", "currentRenderPipeline", "alwaysIncludedShaderCount", "shaderStripping" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GraphicsGetOverview()
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var alwaysIncluded = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            var lightmap = graphicsSettings?.FindProperty("m_LightmapStripping");
            var fog = graphicsSettings?.FindProperty("m_FogStripping");
            var instancing = graphicsSettings?.FindProperty("m_InstancingStripping");
            var currentLevel = QualitySettings.GetQualityLevel();

            return new
            {
                success = true,
                currentQuality = new
                {
                    index = currentLevel,
                    name = QualitySettings.names[currentLevel],
                    antiAliasing = QualitySettings.antiAliasing,
                    shadows = QualitySettings.shadows.ToString(),
                    lodBias = QualitySettings.lodBias
                },
                defaultRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(GraphicsSettings.defaultRenderPipeline),
                currentRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(GraphicsSettings.currentRenderPipeline),
                qualityOverrideRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(QualitySettings.renderPipeline),
                alwaysIncludedShaderCount = alwaysIncluded?.arraySize ?? 0,
                shaderStripping = new
                {
                    lightmap = RenderPipelineSkillsCommon.GetEnumSerializedPropertyValue(lightmap),
                    fog = RenderPipelineSkillsCommon.GetEnumSerializedPropertyValue(fog),
                    instancing = RenderPipelineSkillsCommon.GetEnumSerializedPropertyValue(instancing)
                }
            };
        }

        [UnitySkill("graphics_get_quality_settings", "Get quality settings and per-level render pipeline assignments",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Query,
            Tags = new[] { "graphics", "quality", "settings", "levels" },
            Outputs = new[] { "currentLevel", "currentName", "levels" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GraphicsGetQualitySettings()
        {
            var currentLevel = QualitySettings.GetQualityLevel();
            var levels = QualitySettings.names
                .Select((name, index) => new
                {
                    index,
                    name,
                    renderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(QualitySettings.GetRenderPipelineAssetAt(index))
                })
                .ToArray();

            return new
            {
                success = true,
                currentLevel,
                currentName = QualitySettings.names[currentLevel],
                antiAliasing = QualitySettings.antiAliasing,
                vSyncCount = QualitySettings.vSyncCount,
                shadowResolution = QualitySettings.shadowResolution.ToString(),
                shadows = QualitySettings.shadows.ToString(),
                levels
            };
        }

        [UnitySkill("graphics_set_quality_level", "Switch the active quality level by index or name",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "quality", "level", "switch" },
            Outputs = new[] { "level", "name" },
            TracksWorkflow = true)]
        public static object GraphicsSetQualityLevel(int level = -1, string levelName = null)
        {
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                level = Array.FindIndex(QualitySettings.names, x => string.Equals(x, levelName, StringComparison.Ordinal));
                if (level < 0)
                    return new { error = $"Quality level '{levelName}' not found" };
            }

            if (level < 0 || level >= QualitySettings.names.Length)
                return new { error = $"Invalid quality level: {level}" };

            QualitySettings.SetQualityLevel(level, true);
            return new
            {
                success = true,
                level,
                name = QualitySettings.names[level],
                renderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(QualitySettings.renderPipeline)
            };
        }

        [UnitySkill("graphics_get_render_pipeline_assets", "List default, current, and per-quality render pipeline assets",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Query,
            Tags = new[] { "graphics", "render pipeline", "assets", "quality" },
            Outputs = new[] { "defaultRenderPipeline", "currentRenderPipeline", "qualityLevels" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GraphicsGetRenderPipelineAssets()
        {
            var qualityLevels = QualitySettings.names
                .Select((name, index) => new
                {
                    index,
                    name,
                    renderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(QualitySettings.GetRenderPipelineAssetAt(index))
                })
                .ToArray();

            return new
            {
                success = true,
                defaultRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(GraphicsSettings.defaultRenderPipeline),
                currentRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(GraphicsSettings.currentRenderPipeline),
                qualityLevels
            };
        }

        [UnitySkill("graphics_set_default_render_pipeline", "Set or clear the default render pipeline asset",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "render pipeline", "default", "asset" },
            Outputs = new[] { "defaultRenderPipeline" },
            TracksWorkflow = true)]
        public static object GraphicsSetDefaultRenderPipeline(string assetPath = null, bool clear = false)
        {
            if (!clear)
            {
                if (Validate.Required(assetPath, "assetPath") is object err) return err;
                if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            }

            RenderPipelineAsset asset = null;
            if (!clear)
            {
                asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(assetPath);
                if (asset == null)
                    return new { error = $"RenderPipelineAsset not found: {assetPath}" };
            }

            GraphicsSettings.defaultRenderPipeline = asset;
            return new
            {
                success = true,
                defaultRenderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(GraphicsSettings.defaultRenderPipeline)
            };
        }

        [UnitySkill("graphics_set_quality_render_pipeline", "Assign or clear the render pipeline asset for a specific quality level",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "quality", "render pipeline", "asset" },
            Outputs = new[] { "level", "name", "renderPipeline" },
            TracksWorkflow = true)]
        public static object GraphicsSetQualityRenderPipeline(int level = -1, string levelName = null, string assetPath = null, bool clear = false)
        {
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                level = Array.FindIndex(QualitySettings.names, x => string.Equals(x, levelName, StringComparison.Ordinal));
                if (level < 0)
                    return new { error = $"Quality level '{levelName}' not found" };
            }

            if (level < 0 || level >= QualitySettings.names.Length)
                return new { error = $"Invalid quality level: {level}" };

            if (!clear)
            {
                if (Validate.Required(assetPath, "assetPath") is object err) return err;
                if (Validate.SafePath(assetPath, "assetPath") is object pathErr) return pathErr;
            }

            RenderPipelineAsset asset = null;
            if (!clear)
            {
                asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(assetPath);
                if (asset == null)
                    return new { error = $"RenderPipelineAsset not found: {assetPath}" };
            }

            var previousLevel = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(level, false);
            QualitySettings.renderPipeline = asset;
            if (previousLevel != level)
                QualitySettings.SetQualityLevel(previousLevel, false);

            return new
            {
                success = true,
                level,
                name = QualitySettings.names[level],
                renderPipeline = RenderPipelineSkillsCommon.DescribePipelineAsset(QualitySettings.GetRenderPipelineAssetAt(level))
            };
        }

        [UnitySkill("graphics_list_always_included_shaders", "List shaders in GraphicsSettings > Always Included Shaders",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Query,
            Tags = new[] { "graphics", "shader", "always included", "list" },
            Outputs = new[] { "count", "shaders" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GraphicsListAlwaysIncludedShaders()
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var property = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            if (property == null)
                return new { error = "Always Included Shaders property not found in GraphicsSettings" };

            var shaders = new List<object>();
            for (var i = 0; i < property.arraySize; i++)
            {
                var shader = property.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                shaders.Add(new
                {
                    index = i,
                    shader = shader != null ? new
                    {
                        name = shader.name,
                        path = AssetDatabase.GetAssetPath(shader)
                    } : null
                });
            }

            return new
            {
                success = true,
                count = shaders.Count,
                shaders
            };
        }

        [UnitySkill("graphics_add_always_included_shader", "Add a shader to Always Included Shaders",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "shader", "always included", "add" },
            Outputs = new[] { "count", "shader" },
            TracksWorkflow = true)]
        public static object GraphicsAddAlwaysIncludedShader(string shaderNameOrPath)
        {
            if (Validate.Required(shaderNameOrPath, "shaderNameOrPath") is object err) return err;

            var shader = FindShaderByNameOrPath(shaderNameOrPath);
            if (shader == null)
                return new { error = $"Shader not found: {shaderNameOrPath}" };

            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var property = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            if (property == null)
                return new { error = "Always Included Shaders property not found in GraphicsSettings" };

            for (var i = 0; i < property.arraySize; i++)
            {
                if (property.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    return new { success = true, alreadyIncluded = true, shader = shader.name, count = property.arraySize };
            }

            property.InsertArrayElementAtIndex(property.arraySize);
            property.GetArrayElementAtIndex(property.arraySize - 1).objectReferenceValue = shader;
            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();

            return new
            {
                success = true,
                shader = shader.name,
                count = property.arraySize
            };
        }

        [UnitySkill("graphics_remove_always_included_shader", "Remove a shader from Always Included Shaders",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "shader", "always included", "remove" },
            Outputs = new[] { "count", "removedShader" },
            TracksWorkflow = true)]
        public static object GraphicsRemoveAlwaysIncludedShader(string shaderNameOrPath)
        {
            if (Validate.Required(shaderNameOrPath, "shaderNameOrPath") is object err) return err;

            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var property = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            if (property == null)
                return new { error = "Always Included Shaders property not found in GraphicsSettings" };

            for (var i = 0; i < property.arraySize; i++)
            {
                var shader = property.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader == null)
                    continue;

                if (string.Equals(shader.name, shaderNameOrPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(AssetDatabase.GetAssetPath(shader), shaderNameOrPath, StringComparison.OrdinalIgnoreCase))
                {
                    property.DeleteArrayElementAtIndex(i);
                    graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
                    return new
                    {
                        success = true,
                        removedShader = shader.name,
                        count = property.arraySize
                    };
                }
            }

            return new { error = $"Shader not present in Always Included Shaders: {shaderNameOrPath}" };
        }

        [UnitySkill("graphics_get_shader_stripping", "Get GraphicsSettings shader stripping configuration",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Query,
            Tags = new[] { "graphics", "shader", "stripping", "settings" },
            Outputs = new[] { "lightmap", "fog", "instancing" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GraphicsGetShaderStripping()
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            if (graphicsSettings == null)
                return new { error = "GraphicsSettings asset not found" };

            return new
            {
                success = true,
                lightmap = DescribeSerializedEnum(graphicsSettings.FindProperty("m_LightmapStripping")),
                fog = DescribeSerializedEnum(graphicsSettings.FindProperty("m_FogStripping")),
                instancing = DescribeSerializedEnum(graphicsSettings.FindProperty("m_InstancingStripping"))
            };
        }

        [UnitySkill("graphics_set_shader_stripping", "Configure GraphicsSettings shader stripping modes",
            Category = SkillCategory.Graphics, Operation = SkillOperation.Modify,
            Tags = new[] { "graphics", "shader", "stripping", "settings" },
            Outputs = new[] { "lightmap", "fog", "instancing" },
            TracksWorkflow = true)]
        public static object GraphicsSetShaderStripping(
            string lightmapMode = null,
            string fogMode = null,
            string instancingMode = null,
            int? lightmapValue = null,
            int? fogValue = null,
            int? instancingValue = null)
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            if (graphicsSettings == null)
                return new { error = "GraphicsSettings asset not found" };

            if (!ApplyEnumSetting(graphicsSettings.FindProperty("m_LightmapStripping"), lightmapMode, lightmapValue, out var lightmapError))
                return new { error = lightmapError };
            if (!ApplyEnumSetting(graphicsSettings.FindProperty("m_FogStripping"), fogMode, fogValue, out var fogError))
                return new { error = fogError };
            if (!ApplyEnumSetting(graphicsSettings.FindProperty("m_InstancingStripping"), instancingMode, instancingValue, out var instancingError))
                return new { error = instancingError };

            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            return GraphicsGetShaderStripping();
        }

        private static Shader FindShaderByNameOrPath(string shaderNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(shaderNameOrPath))
                return null;

            if (shaderNameOrPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                shaderNameOrPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<Shader>(shaderNameOrPath);
            }

            return Shader.Find(shaderNameOrPath);
        }

        private static object DescribeSerializedEnum(SerializedProperty property)
        {
            if (property == null)
                return null;

            return new
            {
                value = RenderPipelineSkillsCommon.GetEnumSerializedPropertyValue(property),
                options = property.propertyType == SerializedPropertyType.Enum ? property.enumNames : Array.Empty<string>()
            };
        }

        private static bool ApplyEnumSetting(SerializedProperty property, string enumName, int? rawValue, out string error)
        {
            if (property == null)
            {
                error = "GraphicsSettings serialized property not found";
                return false;
            }

            if (string.IsNullOrWhiteSpace(enumName) && !rawValue.HasValue)
            {
                error = null;
                return true;
            }

            return RenderPipelineSkillsCommon.TrySetEnumSerializedProperty(property, enumName, rawValue, out error);
        }
    }
}

// Producer:Betsy
