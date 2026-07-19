using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// Graphics and quality settings skills for SRP-aware projects.
    /// </summary>
    public static class GraphicsSkills
    {
        // --- Workflow setting restorers (real, reversible undo/redo) ---

        private sealed class ShaderStrippingValue
        {
            public int lightmap;
            public int fog;
            public int instancing;
        }

        /// <summary>
        /// Registers getters/setters for graphics settings so their skill changes are truly
        /// reversible via workflow undo/redo. Stateless keys are registered here on domain load;
        /// the per-quality-level render pipeline key is registered on demand (its getter needs
        /// the level) in <see cref="GraphicsSetQualityRenderPipeline"/>.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RegisterSettingRestorers()
        {
            WorkflowSettingRestorerRegistry.Register("graphics.qualityLevel",
                () => JsonConvert.SerializeObject(QualitySettings.GetQualityLevel()),
                json =>
                {
                    int level = JsonConvert.DeserializeObject<int>(json);
                    if (level < 0 || level >= QualitySettings.names.Length) return false;
                    QualitySettings.SetQualityLevel(level, true);
                    return true;
                });

            WorkflowSettingRestorerRegistry.Register("graphics.defaultRenderPipeline",
                () => JsonConvert.SerializeObject(AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline) ?? string.Empty),
                json =>
                {
                    string path = JsonConvert.DeserializeObject<string>(json) ?? string.Empty;
                    GraphicsSettings.defaultRenderPipeline = string.IsNullOrEmpty(path)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                    return true;
                });

            WorkflowSettingRestorerRegistry.Register("graphics.alwaysIncludedShaders",
                CaptureAlwaysIncludedShaders,
                ApplyAlwaysIncludedShaders);

            WorkflowSettingRestorerRegistry.Register("graphics.shaderStripping",
                CaptureShaderStripping,
                ApplyShaderStripping);

            // Register a per-level render pipeline restorer for every existing quality level so
            // undo/redo works even after a domain reload (which clears the in-memory registry).
            for (var level = 0; level < QualitySettings.names.Length; level++)
                EnsureQualityRenderPipelineRestorer(level);
        }

        private static string QualityRenderPipelineKey(int level) => "graphics.qualityRenderPipeline:" + level;

        /// <summary>
        /// Registers (idempotently) a getter/setter for a specific quality level's render pipeline.
        /// The level is captured in the closures because the registry passes no key to handlers.
        /// </summary>
        private static void EnsureQualityRenderPipelineRestorer(int level)
        {
            WorkflowSettingRestorerRegistry.Register(QualityRenderPipelineKey(level),
                () => JsonConvert.SerializeObject(AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(level)) ?? string.Empty),
                json =>
                {
                    if (level < 0 || level >= QualitySettings.names.Length) return false;
                    string path = JsonConvert.DeserializeObject<string>(json) ?? string.Empty;
                    var asset = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                    int previousLevel = QualitySettings.GetQualityLevel();
                    QualitySettings.SetQualityLevel(level, false);
                    QualitySettings.renderPipeline = asset;
                    if (previousLevel != level)
                        QualitySettings.SetQualityLevel(previousLevel, false);
                    return true;
                });
        }

        private static string CaptureAlwaysIncludedShaders()
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var property = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            var paths = new List<string>();
            if (property != null)
            {
                for (var i = 0; i < property.arraySize; i++)
                {
                    var shader = property.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                    paths.Add(shader != null ? AssetDatabase.GetAssetPath(shader) : null);
                }
            }
            return JsonConvert.SerializeObject(paths);
        }

        private static bool ApplyAlwaysIncludedShaders(string json)
        {
            var paths = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            var property = graphicsSettings?.FindProperty("m_AlwaysIncludedShaders");
            if (property == null)
                return false;

            property.ClearArray();
            for (var i = 0; i < paths.Count; i++)
            {
                property.InsertArrayElementAtIndex(i);
                var shader = string.IsNullOrEmpty(paths[i]) ? null : AssetDatabase.LoadAssetAtPath<Shader>(paths[i]);
                property.GetArrayElementAtIndex(i).objectReferenceValue = shader;
            }
            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        private static string CaptureShaderStripping()
        {
            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            return JsonConvert.SerializeObject(new ShaderStrippingValue
            {
                lightmap = graphicsSettings?.FindProperty("m_LightmapStripping")?.enumValueIndex ?? 0,
                fog = graphicsSettings?.FindProperty("m_FogStripping")?.enumValueIndex ?? 0,
                instancing = graphicsSettings?.FindProperty("m_InstancingStripping")?.enumValueIndex ?? 0
            });
        }

        private static bool ApplyShaderStripping(string json)
        {
            var value = JsonConvert.DeserializeObject<ShaderStrippingValue>(json);
            if (value == null)
                return false;

            var graphicsSettings = RenderPipelineSkillsCommon.GetGraphicsSettingsObject();
            if (graphicsSettings == null)
                return false;

            var lightmap = graphicsSettings.FindProperty("m_LightmapStripping");
            var fog = graphicsSettings.FindProperty("m_FogStripping");
            var instancing = graphicsSettings.FindProperty("m_InstancingStripping");
            if (lightmap != null) lightmap.enumValueIndex = value.lightmap;
            if (fog != null) fog.enumValueIndex = value.fog;
            if (instancing != null) instancing.enumValueIndex = value.instancing;
            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

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

            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("graphics.qualityLevel",
                    JsonConvert.SerializeObject(QualitySettings.GetQualityLevel()), "Graphics: Quality Level");

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

            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("graphics.defaultRenderPipeline",
                    JsonConvert.SerializeObject(AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline) ?? string.Empty),
                    "Graphics: Default Render Pipeline");

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

            if (WorkflowManager.IsRecording)
            {
                EnsureQualityRenderPipelineRestorer(level);
                WorkflowManager.SnapshotSetting(QualityRenderPipelineKey(level),
                    JsonConvert.SerializeObject(AssetDatabase.GetAssetPath(QualitySettings.GetRenderPipelineAssetAt(level)) ?? string.Empty),
                    $"Graphics: Quality Render Pipeline (level {level})");
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

            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("graphics.alwaysIncludedShaders",
                    CaptureAlwaysIncludedShaders(), "Graphics: Always Included Shaders");

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
                    if (WorkflowManager.IsRecording)
                        WorkflowManager.SnapshotSetting("graphics.alwaysIncludedShaders",
                            CaptureAlwaysIncludedShaders(), "Graphics: Always Included Shaders");

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

            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting("graphics.shaderStripping",
                    CaptureShaderStripping(), "Graphics: Shader Stripping");

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
