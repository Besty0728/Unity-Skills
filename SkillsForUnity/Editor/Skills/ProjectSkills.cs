using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Project information and configuration skills - detect render pipeline, project settings, etc.
    /// </summary>
    public static class ProjectSkills
    {
        /// <summary>
        /// Enum representing different render pipelines
        /// </summary>
        public enum RenderPipelineType
        {
            BuiltIn,
            URP,
            HDRP,
            Custom
        }

        /// <summary>
        /// Detects the current render pipeline used in the project
        /// </summary>
        public static RenderPipelineType DetectRenderPipeline()
        {
            var currentRP = GraphicsSettings.currentRenderPipeline;
            
            if (currentRP == null)
                return RenderPipelineType.BuiltIn;
            
            var rpTypeName = currentRP.GetType().Name;
            
            if (rpTypeName.Contains("Universal") || rpTypeName.Contains("URP"))
                return RenderPipelineType.URP;
            
            if (rpTypeName.Contains("HDRender") || rpTypeName.Contains("HDRP"))
                return RenderPipelineType.HDRP;
            
            return RenderPipelineType.Custom;
        }

        /// <summary>
        /// Gets the recommended default shader for the current render pipeline
        /// </summary>
        public static string GetDefaultShaderName()
        {
            var pipeline = DetectRenderPipeline();
            
            return pipeline switch
            {
                RenderPipelineType.URP => "Universal Render Pipeline/Lit",
                RenderPipelineType.HDRP => "HDRP/Lit",
                RenderPipelineType.BuiltIn => "Standard",
                _ => "Standard"
            };
        }

        /// <summary>
        /// Gets the recommended unlit shader for the current render pipeline
        /// </summary>
        public static string GetUnlitShaderName()
        {
            var pipeline = DetectRenderPipeline();
            
            return pipeline switch
            {
                RenderPipelineType.URP => "Universal Render Pipeline/Unlit",
                RenderPipelineType.HDRP => "HDRP/Unlit",
                RenderPipelineType.BuiltIn => "Unlit/Color",
                _ => "Unlit/Color"
            };
        }

        /// <summary>
        /// Gets the correct color property name for the current render pipeline
        /// </summary>
        public static string GetColorPropertyName()
        {
            var pipeline = DetectRenderPipeline();
            
            return pipeline switch
            {
                RenderPipelineType.URP => "_BaseColor",
                RenderPipelineType.HDRP => "_BaseColor",
                RenderPipelineType.BuiltIn => "_Color",
                _ => "_Color"
            };
        }

        /// <summary>
        /// Gets the correct main texture property name for the current render pipeline
        /// </summary>
        public static string GetMainTexturePropertyName()
        {
            var pipeline = DetectRenderPipeline();
            
            return pipeline switch
            {
                RenderPipelineType.URP => "_BaseMap",
                RenderPipelineType.HDRP => "_BaseColorMap",
                RenderPipelineType.BuiltIn => "_MainTex",
                _ => "_MainTex"
            };
        }

        [UnitySkill("project_get_info", "Get project information including render pipeline, Unity version, and settings")]
        public static object ProjectGetInfo()
        {
            var pipeline = DetectRenderPipeline();
            var currentRP = GraphicsSettings.currentRenderPipeline;
            
            return new
            {
                success = true,
                unityVersion = Application.unityVersion,
                productName = Application.productName,
                companyName = Application.companyName,
                platform = Application.platform.ToString(),
                renderPipeline = new
                {
                    type = pipeline.ToString(),
                    name = currentRP != null ? currentRP.name : "Built-in Render Pipeline",
                    assetType = currentRP != null ? currentRP.GetType().Name : null
                },
                recommendedShaders = new
                {
                    defaultLit = GetDefaultShaderName(),
                    unlit = GetUnlitShaderName(),
                    colorProperty = GetColorPropertyName(),
                    mainTextureProperty = GetMainTexturePropertyName()
                },
                projectPath = Application.dataPath,
                isPlaying = Application.isPlaying
            };
        }

        [UnitySkill("project_get_render_pipeline", "Get current render pipeline type and recommended shaders")]
        public static object ProjectGetRenderPipeline()
        {
            var pipeline = DetectRenderPipeline();
            var currentRP = GraphicsSettings.currentRenderPipeline;
            
            var availableShaders = new List<object>();
            
            // List common shaders for the detected pipeline
            string[] shadersToCheck = pipeline switch
            {
                RenderPipelineType.URP => new[] 
                {
                    "Universal Render Pipeline/Lit",
                    "Universal Render Pipeline/Simple Lit",
                    "Universal Render Pipeline/Unlit",
                    "Universal Render Pipeline/Particles/Lit",
                    "Universal Render Pipeline/Particles/Unlit"
                },
                RenderPipelineType.HDRP => new[]
                {
                    "HDRP/Lit",
                    "HDRP/Unlit",
                    "HDRP/LitTessellation"
                },
                _ => new[]
                {
                    "Standard",
                    "Standard (Specular setup)",
                    "Unlit/Color",
                    "Unlit/Texture",
                    "Mobile/Diffuse"
                }
            };
            
            foreach (var shaderName in shadersToCheck)
            {
                var shader = Shader.Find(shaderName);
                availableShaders.Add(new
                {
                    name = shaderName,
                    available = shader != null
                });
            }
            
            return new
            {
                success = true,
                pipelineType = pipeline.ToString(),
                pipelineName = currentRP != null ? currentRP.name : "Built-in Render Pipeline",
                defaultShader = GetDefaultShaderName(),
                unlitShader = GetUnlitShaderName(),
                colorProperty = GetColorPropertyName(),
                textureProperty = GetMainTexturePropertyName(),
                availableShaders = availableShaders
            };
        }

        [UnitySkill("project_list_shaders", "List all available shaders in the project")]
        public static object ProjectListShaders(string filter = null, int limit = 50)
        {
            var shaderNames = new List<string>();
            
            // Get all shader assets
            var guids = AssetDatabase.FindAssets("t:Shader");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader != null)
                {
                    if (string.IsNullOrEmpty(filter) || shader.name.ToLower().Contains(filter.ToLower()))
                    {
                        shaderNames.Add(shader.name);
                    }
                }
            }
            
            // Also try to find common built-in shaders
            var builtInShaders = new[]
            {
                "Standard", "Standard (Specular setup)",
                "Unlit/Color", "Unlit/Texture", "Unlit/Transparent",
                "Mobile/Diffuse", "Mobile/Bumped Diffuse",
                "Particles/Standard Unlit", "Particles/Standard Surface",
                "Skybox/6 Sided", "Skybox/Procedural",
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit",
                "HDRP/Lit", "HDRP/Unlit"
            };
            
            foreach (var shaderName in builtInShaders)
            {
                var shader = Shader.Find(shaderName);
                if (shader != null && !shaderNames.Contains(shaderName))
                {
                    if (string.IsNullOrEmpty(filter) || shaderName.ToLower().Contains(filter.ToLower()))
                    {
                        shaderNames.Add(shaderName);
                    }
                }
            }
            
            var sortedShaders = shaderNames.Distinct().OrderBy(s => s).Take(limit).ToList();
            
            return new
            {
                success = true,
                count = sortedShaders.Count,
                filter = filter,
                shaders = sortedShaders
            };
        }

        [UnitySkill("project_get_quality_settings", "Get current quality settings")]
        public static object ProjectGetQualitySettings()
        {
            var qualityNames = QualitySettings.names;
            var currentLevel = QualitySettings.GetQualityLevel();

            return new
            {
                success = true,
                currentLevel = currentLevel,
                currentName = qualityNames[currentLevel],
                allLevels = qualityNames.Select((name, index) => new { index, name }).ToList(),
                shadows = QualitySettings.shadows.ToString(),
                shadowResolution = QualitySettings.shadowResolution.ToString(),
                antiAliasing = QualitySettings.antiAliasing,
                vSyncCount = QualitySettings.vSyncCount,
                lodBias = QualitySettings.lodBias,
                maximumLODLevel = QualitySettings.maximumLODLevel
            };
        }

        [UnitySkill("project_get_build_settings", "Get build settings (platform, scenes)")]
        public static object ProjectGetBuildSettings()
        {
            var scenes = EditorBuildSettings.scenes.Select((s, i) => new { index = i, path = s.path, enabled = s.enabled }).ToArray();
            return new
            {
                success = true,
                activeBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString(),
                sceneCount = scenes.Length,
                scenes
            };
        }

        [UnitySkill("project_get_packages", "List installed UPM packages")]
        public static object ProjectGetPackages()
        {
            var manifestPath = "Packages/manifest.json";
            if (!File.Exists(manifestPath)) return new { error = "manifest.json not found" };
            var json = File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
            var manifest = Newtonsoft.Json.Linq.JObject.Parse(json);
            return new { success = true, manifest };
        }

        [UnitySkill("project_get_layers", "Get all Layer definitions")]
        public static object ProjectGetLayers()
        {
            var layers = UnityEditorInternal.InternalEditorUtility.layers;
            return new { success = true, count = layers.Length, layers };
        }

        [UnitySkill("project_get_tags", "Get all Tag definitions")]
        public static object ProjectGetTags()
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return new { success = true, count = tags.Length, tags };
        }

        [UnitySkill("project_add_tag", "Add a custom Tag", TracksWorkflow = true)]
        public static object ProjectAddTag(string tagName)
        {
            if (Validate.Required(tagName, "tagName") is object err) return err;

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tagsProp = tagManager.FindProperty("tags");
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == tagName)
                    return new { error = $"Tag '{tagName}' already exists" };
            }
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tagName;
            tagManager.ApplyModifiedProperties();
            return new { success = true, tag = tagName };
        }

        [UnitySkill("project_get_player_settings", "Get Player Settings")]
        public static object ProjectGetPlayerSettings()
        {
            return new
            {
                success = true,
                productName = PlayerSettings.productName,
                companyName = PlayerSettings.companyName,
                bundleVersion = PlayerSettings.bundleVersion,
                defaultScreenWidth = PlayerSettings.defaultScreenWidth,
                defaultScreenHeight = PlayerSettings.defaultScreenHeight,
                fullscreen = PlayerSettings.fullScreenMode.ToString(),
                apiCompatibility = PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                scriptingBackend = PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString()
            };
        }

        [UnitySkill("project_set_quality_level", "Switch quality level by index or name", TracksWorkflow = true)]
        public static object ProjectSetQualityLevel(int level = -1, string levelName = null)
        {
            if (!string.IsNullOrEmpty(levelName))
            {
                var names = QualitySettings.names;
                for (int i = 0; i < names.Length; i++)
                    if (names[i] == levelName) { level = i; break; }
                if (level < 0) return new { error = $"Quality level '{levelName}' not found" };
            }
            if (level < 0 || level >= QualitySettings.names.Length)
                return new { error = $"Invalid quality level: {level}" };
            QualitySettings.SetQualityLevel(level, true);
            return new { success = true, level, name = QualitySettings.names[level] };
        }
    }
}
