using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Material management skills - create, modify, assign.
    /// Now supports finding by name, instanceId, or path.
    /// Automatically detects render pipeline for correct shader selection.
    /// Enhanced with HDR, Keyword, GI support and comprehensive material operations.
    /// </summary>
    public static class MaterialSkills
    {
        #region Helper Methods
        
        /// <summary>
        /// Find material by various methods: asset path, GameObject name/instanceId/path
        /// </summary>
        private static (Material material, GameObject go, object error) FindMaterial(string name = null, int instanceId = 0, string path = null)
        {
            // Check if finding by Asset Path (material file)
            if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/") || path.EndsWith(".mat")))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return (null, null, new { error = $"Material asset not found: {path}" });
                return (material, null, null);
            }
            
            // Find by GameObject
            var result = GameObjectFinder.FindOrError(name, instanceId, path);
            if (result.error != null)
                return (null, null, result.error);
            
            var go = result.go;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return (null, null, new { error = "No Renderer component found" });
            if (renderer.sharedMaterial == null)
                return (null, null, new { error = "No material assigned to renderer" });
            
            return (renderer.sharedMaterial, go, null);
        }
        
        /// <summary>
        /// Smart path resolution for saving materials
        /// </summary>
        private static string ResolveSavePath(string savePath, string materialName)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;
                
            // Ensure path starts with Assets/
            if (!savePath.StartsWith("Assets/"))
            {
                savePath = "Assets/" + savePath;
            }
            
            // Smart Path Handling: If it looks like a folder (no extension or directory exists), append name
            if (Directory.Exists(savePath) || !Path.HasExtension(savePath))
            {
                string fileName = string.IsNullOrEmpty(materialName) ? "NewMaterial" : materialName;
                savePath = Path.Combine(savePath, fileName + ".mat").Replace("\\", "/");
            }
            else if (!savePath.EndsWith(".mat"))
            {
                savePath = savePath + ".mat";
            }
            
            return savePath;
        }
        
        /// <summary>
        /// Ensure directory exists for the given path
        /// </summary>
        private static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                var folders = dir.Split('/');
                var currentPath = folders[0];
                for (int i = 1; i < folders.Length; i++)
                {
                    var newPath = currentPath + "/" + folders[i];
                    if (!AssetDatabase.IsValidFolder(newPath))
                    {
                        AssetDatabase.CreateFolder(currentPath, folders[i]);
                    }
                    currentPath = newPath;
                }
            }
        }
        
        #endregion
        
        #region Material Creation & Assignment

        [UnitySkill("material_create", "Create a new material (auto-detects render pipeline if shader not specified). savePath can be a folder or full path.")]
        public static object MaterialCreate(string name, string shaderName = null, string savePath = null)
        {
            // Auto-detect shader based on render pipeline if not specified
            if (string.IsNullOrEmpty(shaderName))
            {
                shaderName = ProjectSkills.GetDefaultShaderName();
            }
            
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                // Try fallback shaders
                var pipeline = ProjectSkills.DetectRenderPipeline();
                var fallbackShaders = pipeline switch
                {
                    ProjectSkills.RenderPipelineType.URP => new[] { "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard" },
                    ProjectSkills.RenderPipelineType.HDRP => new[] { "HDRP/Lit", "Standard" },
                    _ => new[] { "Standard", "Mobile/Diffuse", "Unlit/Color" }
                };
                
                foreach (var fallback in fallbackShaders)
                {
                    shader = Shader.Find(fallback);
                    if (shader != null)
                    {
                        shaderName = fallback;
                        break;
                    }
                }
                
                if (shader == null)
                {
                    var pipelineInfo = ProjectSkills.DetectRenderPipeline();
                    return new { 
                        error = $"Shader not found: {shaderName}. Detected pipeline: {pipelineInfo}. Try using project_get_render_pipeline to see available shaders.",
                        detectedPipeline = pipelineInfo.ToString(),
                        recommendedShader = ProjectSkills.GetDefaultShaderName()
                    };
                }
            }

            var material = new Material(shader) { name = name };

            if (!string.IsNullOrEmpty(savePath))
            {
                // Smart path resolution
                savePath = ResolveSavePath(savePath, name);
                EnsureDirectoryExists(savePath);

                AssetDatabase.CreateAsset(material, savePath);
                WorkflowManager.SnapshotObject(material, SnapshotType.Created);
                AssetDatabase.SaveAssets();
            }

            var pipelineType = ProjectSkills.DetectRenderPipeline();
            return new { 
                success = true, 
                name, 
                shader = shaderName, 
                path = savePath,
                renderPipeline = pipelineType.ToString(),
                colorProperty = ProjectSkills.GetColorPropertyName(),
                textureProperty = ProjectSkills.GetMainTexturePropertyName()
            };
        }

        [UnitySkill("material_assign", "Assign a material asset to a renderer (supports name/instanceId/path)")]
        public static object MaterialAssign(string name = null, int instanceId = 0, string path = null, string materialPath = null)
        {
            if (Validate.Required(materialPath, "materialPath") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = "No Renderer component found" };

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                return new { error = $"Material not found: {materialPath}" };

            WorkflowManager.SnapshotObject(renderer);
            Undo.RecordObject(renderer, "Assign Material");
            renderer.sharedMaterial = material;

            return new { success = true, gameObject = go.name, material = materialPath };
        }

        [UnitySkill("material_create_batch", "Create multiple materials (Efficient). items: JSON array of {name, shaderName?, savePath?}")]
        public static object MaterialCreateBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchMaterialCreateItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0, failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var result = MaterialCreate(item.name, item.shaderName, item.savePath);
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                        if (json.Contains("\"error\"")) { failCount++; results.Add(new { target = item.name, success = false, result }); }
                        else { successCount++; results.Add(result); }
                    }
                    catch (System.Exception ex) { results.Add(new { target = item.name, success = false, error = ex.Message }); failCount++; }
                }
                return new { success = failCount == 0, totalItems = itemList.Count, successCount, failCount, results };
            }
            catch (System.Exception ex) { return new { error = $"JSON parse failed: {ex.Message}" }; }
        }

        private class BatchMaterialCreateItem { public string name { get; set; } public string shaderName { get; set; } public string savePath { get; set; } }

        [UnitySkill("material_assign_batch", "Assign materials to multiple objects (Efficient). items: JSON array of {name, materialPath}")]
        public static object MaterialAssignBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchMaterialAssignItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0, failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var result = MaterialAssign(name: item.name, instanceId: item.instanceId, path: item.path, materialPath: item.materialPath);
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                        if (json.Contains("\"error\"")) { failCount++; results.Add(new { target = item.name ?? item.path, success = false, result }); }
                        else { successCount++; results.Add(result); }
                    }
                    catch (System.Exception ex) { results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message }); failCount++; }
                }
                return new { success = failCount == 0, totalItems = itemList.Count, successCount, failCount, results };
            }
            catch (System.Exception ex) { return new { error = $"JSON parse failed: {ex.Message}" }; }
        }

        private class BatchMaterialAssignItem { public string name { get; set; } public int instanceId { get; set; } public string path { get; set; } public string materialPath { get; set; } }
        
        [UnitySkill("material_duplicate", "Duplicate an existing material")]
        public static object MaterialDuplicate(string sourcePath, string newName, string savePath = null)
        {
            if (Validate.Required(sourcePath, "sourcePath") is object err) return err;
            if (Validate.Required(newName, "newName") is object err2) return err2;
                
            var sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (sourceMaterial == null)
                return new { error = $"Source material not found: {sourcePath}" };
            
            var newMaterial = new Material(sourceMaterial) { name = newName };
            
            if (string.IsNullOrEmpty(savePath))
            {
                // Save in same folder as source
                var sourceDir = Path.GetDirectoryName(sourcePath);
                savePath = Path.Combine(sourceDir, newName + ".mat").Replace("\\", "/");
            }
            else
            {
                savePath = ResolveSavePath(savePath, newName);
            }
            
            EnsureDirectoryExists(savePath);
            AssetDatabase.CreateAsset(newMaterial, savePath);
            WorkflowManager.SnapshotObject(newMaterial, SnapshotType.Created);
            AssetDatabase.SaveAssets();
            
            return new { 
                success = true, 
                name = newName, 
                path = savePath,
                sourcePath,
                shader = newMaterial.shader.name
            };
        }
        
        #endregion
        
        #region Color & Emission

        [UnitySkill("material_set_color", "Set a color property on a material with optional HDR intensity for emission")]
        public static object MaterialSetColor(string name = null, int instanceId = 0, string path = null, 
            float r = 1, float g = 1, float b = 1, float a = 1, 
            string propertyName = null, float intensity = 1.0f)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            // Auto-detect color property name if not specified
            if (string.IsNullOrEmpty(propertyName))
            {
                propertyName = ProjectSkills.GetColorPropertyName();
            }

            // Apply HDR intensity (for emission, values > 1 create bloom effect)
            var color = new Color(r, g, b, a);
            if (intensity != 1.0f)
            {
                color = new Color(r * intensity, g * intensity, b * intensity, a);
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Color");
            
            // Try setting color with detected property, fallback to common names
            bool colorSet = false;
            var propertiesToTry = new[] { propertyName, "_BaseColor", "_Color", "_TintColor", "_EmissionColor" };
            
            foreach (var prop in propertiesToTry)
            {
                if (material.HasProperty(prop))
                {
                    material.SetColor(prop, color);
                    propertyName = prop;
                    colorSet = true;
                    
                    // Smart Emission Handling: Auto-enable emission when setting emission color
                    if (prop == "_EmissionColor" && intensity > 0)
                    {
                        material.EnableKeyword("_EMISSION");
                        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    
                    break;
                }
            }
            
            if (!colorSet)
            {
                return new { 
                    error = $"Material does not have a color property. Tried: {string.Join(", ", propertiesToTry)}",
                    shaderName = material.shader.name,
                    suggestion = "Use material_get_properties to see available properties"
                };
            }
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                color = new { r, g, b, a },
                intensity,
                propertyUsed = propertyName,
                hdrEnabled = (propertyName == "_EmissionColor" && intensity > 0)
            };
        }

        [UnitySkill("material_set_colors_batch", "Set colors on multiple GameObjects in a single call. items is a JSON array like [{name:'Obj1',r:1,g:0,b:0},{name:'Obj2',r:0,g:1,b:0}]. Much more efficient than calling material_set_color multiple times.")]
        public static object MaterialSetColorsBatch(string items = null, string propertyName = null)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required. Example: [{\"name\":\"Cube1\",\"r\":1,\"g\":0,\"b\":0},{\"name\":\"Cube2\",\"r\":0,\"g\":1,\"b\":0}]" };

            // Auto-detect color property if not specified
            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetColorPropertyName();

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchColorItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var (material, go, error) = FindMaterial(item.name, item.instanceId, item.path);
                        if (error != null)
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "Material not found" });
                            failCount++;
                            continue;
                        }

                        var color = new Color(item.r, item.g, item.b, item.a);

                        WorkflowManager.SnapshotObject(material);
                        Undo.RecordObject(material, "Batch Set Color");

                        bool colorSet = false;
                        var propertiesToTry = new[] { propertyName, "_BaseColor", "_Color" };
                        foreach (var prop in propertiesToTry)
                        {
                            if (material.HasProperty(prop))
                            {
                                material.SetColor(prop, color);
                                colorSet = true;
                                break;
                            }
                        }

                        if (colorSet)
                        {
                            if (go == null) EditorUtility.SetDirty(material);
                            results.Add(new { target = go?.name ?? item.path, success = true });
                            successCount++;
                        }
                        else
                        {
                            results.Add(new { target = item.name ?? item.path, success = false, error = "No color property found" });
                            failCount++;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message });
                        failCount++;
                    }
                }

                return new
                {
                    success = failCount == 0,
                    totalItems = itemList.Count,
                    successCount,
                    failCount,
                    propertyUsed = propertyName,
                    results
                };
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}", example = "[{\"name\":\"Cube\",\"r\":1,\"g\":0,\"b\":0}]" };
            }
        }

        private class BatchColorItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public float r { get; set; } = 1f;
            public float g { get; set; } = 1f;
            public float b { get; set; } = 1f;
            public float a { get; set; } = 1f;
        }

        [UnitySkill("material_set_emission", "Set emission color with HDR intensity and auto-enable emission")]
        public static object MaterialSetEmission(string name = null, int instanceId = 0, string path = null,
            float r = 1, float g = 1, float b = 1, float intensity = 1.0f, bool enableEmission = true)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Emission");
            
            // Calculate HDR color
            var hdrColor = new Color(r * intensity, g * intensity, b * intensity, 1f);
            
            // Try emission property names
            string emissionProperty = null;
            var emissionProps = new[] { "_EmissionColor", "_Emission" };
            foreach (var prop in emissionProps)
            {
                if (material.HasProperty(prop))
                {
                    material.SetColor(prop, hdrColor);
                    emissionProperty = prop;
                    break;
                }
            }
            
            if (emissionProperty == null)
            {
                return new { 
                    error = "Material does not support emission",
                    shaderName = material.shader.name,
                    suggestion = "Use a shader that supports emission like Standard, URP/Lit, or HDRP/Lit"
                };
            }
            
            // Enable emission
            if (enableEmission && intensity > 0)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else if (!enableEmission || intensity <= 0)
            {
                material.DisableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            
            if (go == null) EditorUtility.SetDirty(material);
            
            return new {
                success = true,
                target = go != null ? go.name : path,
                emissionColor = new { r, g, b },
                intensity,
                hdrColor = new { r = hdrColor.r, g = hdrColor.g, b = hdrColor.b },
                emissionEnabled = enableEmission && intensity > 0
            };
        }

        [UnitySkill("material_set_emission_batch", "Set emission on multiple objects (Efficient). items: JSON array of {name, r, g, b, intensity?, enableEmission?}")]
        public static object MaterialSetEmissionBatch(string items)
        {
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchEmissionItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items is empty or invalid JSON" };

                var results = new List<object>();
                int successCount = 0, failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var result = MaterialSetEmission(name: item.name, instanceId: item.instanceId, path: item.path,
                            r: item.r, g: item.g, b: item.b, intensity: item.intensity > 0 ? item.intensity : 1f, enableEmission: item.enableEmission);
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                        if (json.Contains("\"error\"")) { failCount++; results.Add(new { target = item.name ?? item.path, success = false, result }); }
                        else { successCount++; results.Add(result); }
                    }
                    catch (System.Exception ex) { results.Add(new { target = item.name ?? item.path, success = false, error = ex.Message }); failCount++; }
                }
                return new { success = failCount == 0, totalItems = itemList.Count, successCount, failCount, results };
            }
            catch (System.Exception ex) { return new { error = $"JSON parse failed: {ex.Message}" }; }
        }

        private class BatchEmissionItem { public string name { get; set; } public int instanceId { get; set; } public string path { get; set; } public float r { get; set; } public float g { get; set; } public float b { get; set; } public float intensity { get; set; } = 1f; public bool enableEmission { get; set; } = true; }
        
        #endregion
        
        #region Property Setters

        [UnitySkill("material_set_texture", "Set a texture on a material (auto-detects property name for render pipeline)")]
        public static object MaterialSetTexture(string name = null, int instanceId = 0, string path = null, string texturePath = null, string propertyName = null)
        {
            if (Validate.Required(texturePath, "texturePath") is object err) return err;
            
            // Auto-detect texture property name if not specified
            if (string.IsNullOrEmpty(propertyName))
            {
                propertyName = ProjectSkills.GetMainTexturePropertyName();
            }

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
                return new { error = $"Texture not found: {texturePath}" };

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture");
            material.SetTexture(propertyName, texture);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                texture = texturePath,
                propertyUsed = propertyName
            };
        }

        [UnitySkill("material_set_float", "Set a float property on a material")]
        public static object MaterialSetFloat(string name = null, int instanceId = 0, string path = null, string propertyName = null, float value = 0)
        {
            if (Validate.Required(propertyName, "propertyName") is object err) return err;

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (!material.HasProperty(propertyName))
            {
                return new { 
                    error = $"Property not found: {propertyName}",
                    shaderName = material.shader.name,
                    suggestion = "Use material_get_properties to see available properties"
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Float");
            material.SetFloat(propertyName, value);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, value };
        }
        
        [UnitySkill("material_set_int", "Set an integer property on a material")]
        public static object MaterialSetInt(string name = null, int instanceId = 0, string path = null, string propertyName = null, int value = 0)
        {
            if (Validate.Required(propertyName, "propertyName") is object err) return err;

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (!material.HasProperty(propertyName))
            {
                return new {
                    error = $"Property not found: {propertyName}",
                    shaderName = material.shader.name
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Int");
            material.SetInt(propertyName, value);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, value };
        }
        
        [UnitySkill("material_set_vector", "Set a vector4 property on a material")]
        public static object MaterialSetVector(string name = null, int instanceId = 0, string path = null, 
            string propertyName = null, float x = 0, float y = 0, float z = 0, float w = 0)
        {
            if (Validate.Required(propertyName, "propertyName") is object err) return err;

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (!material.HasProperty(propertyName))
            {
                return new {
                    error = $"Property not found: {propertyName}",
                    shaderName = material.shader.name
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Vector");
            material.SetVector(propertyName, new Vector4(x, y, z, w));
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, value = new { x, y, z, w } };
        }
        
        [UnitySkill("material_set_texture_offset", "Set texture offset (tiling position)")]
        public static object MaterialSetTextureOffset(string name = null, int instanceId = 0, string path = null,
            string propertyName = null, float x = 0, float y = 0)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;
            
            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetMainTexturePropertyName();

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture Offset");
            material.SetTextureOffset(propertyName, new Vector2(x, y));
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, offset = new { x, y } };
        }
        
        [UnitySkill("material_set_texture_scale", "Set texture scale (tiling)")]
        public static object MaterialSetTextureScale(string name = null, int instanceId = 0, string path = null,
            string propertyName = null, float x = 1, float y = 1)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;
            
            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetMainTexturePropertyName();

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture Scale");
            material.SetTextureScale(propertyName, new Vector2(x, y));
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, scale = new { x, y } };
        }
        
        #endregion
        
        #region Keywords & Render State

        [UnitySkill("material_set_keyword", "Enable or disable a shader keyword (e.g., _EMISSION, _NORMALMAP, _METALLICGLOSSMAP)")]
        public static object MaterialSetKeyword(string name = null, int instanceId = 0, string path = null, 
            string keyword = null, bool enable = true)
        {
            if (Validate.Required(keyword, "keyword") is object err) return err;

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Keyword");
            
            if (enable)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                keyword, 
                enabled = enable,
                allKeywords = material.shaderKeywords
            };
        }
        
        [UnitySkill("material_set_render_queue", "Set material render queue (-1 for shader default, 2000=Geometry, 2450=AlphaTest, 3000=Transparent)")]
        public static object MaterialSetRenderQueue(string name = null, int instanceId = 0, string path = null, int renderQueue = -1)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Render Queue");
            material.renderQueue = renderQueue;
            
            if (go == null) EditorUtility.SetDirty(material);

            string queueName = renderQueue switch
            {
                -1 => "ShaderDefault",
                < 2000 => "Background",
                < 2450 => "Geometry",
                < 2500 => "AlphaTest",
                < 3000 => "GeometryLast",
                < 4000 => "Transparent",
                _ => "Overlay"
            };

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                renderQueue,
                queueCategory = queueName
            };
        }
        
        [UnitySkill("material_set_shader", "Change the shader of a material")]
        public static object MaterialSetShader(string name = null, int instanceId = 0, string path = null, string shaderName = null)
        {
            if (Validate.Required(shaderName, "shaderName") is object err) return err;

            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;
            
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return new {
                    error = $"Shader not found: {shaderName}",
                    suggestion = "Use project_get_render_pipeline to see recommended shaders"
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Shader");
            material.shader = shader;
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                shader = shaderName
            };
        }
        
        [UnitySkill("material_set_gi_flags", "Set global illumination flags (None, RealtimeEmissive, BakedEmissive, EmissiveIsBlack)")]
        public static object MaterialSetGIFlags(string name = null, int instanceId = 0, string path = null, string flags = "RealtimeEmissive")
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            MaterialGlobalIlluminationFlags giFlags;
            if (!System.Enum.TryParse(flags, true, out giFlags))
            {
                return new { 
                    error = $"Invalid GI flags: {flags}",
                    validOptions = new[] { "None", "RealtimeEmissive", "BakedEmissive", "EmissiveIsBlack", "AnyEmissive" }
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set GI Flags");
            material.globalIlluminationFlags = giFlags;
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { 
                success = true, 
                target = go != null ? go.name : path, 
                giFlags = flags
            };
        }
        
        #endregion
        
        #region Property Query

        [UnitySkill("material_get_properties", "Get all properties of a material (colors, floats, textures, keywords)")]
        public static object MaterialGetProperties(string name = null, int instanceId = 0, string path = null)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            var shader = material.shader;
            int propertyCount = shader.GetPropertyCount();
            
            var colors = new List<object>();
            var floats = new List<object>();
            var vectors = new List<object>();
            var textures = new List<object>();
            var integers = new List<object>();
            
            for (int i = 0; i < propertyCount; i++)
            {
                var propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                var propDesc = shader.GetPropertyDescription(i);
                
                switch (propType)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        var color = material.GetColor(propName);
                        colors.Add(new { name = propName, description = propDesc, value = new { r = color.r, g = color.g, b = color.b, a = color.a } });
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        var range = shader.GetPropertyRangeLimits(i);
                        floats.Add(new { name = propName, description = propDesc, value = material.GetFloat(propName), min = range.x, max = range.y });
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        var vec = material.GetVector(propName);
                        vectors.Add(new { name = propName, description = propDesc, value = new { x = vec.x, y = vec.y, z = vec.z, w = vec.w } });
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var tex = material.GetTexture(propName);
                        textures.Add(new { name = propName, description = propDesc, value = tex != null ? tex.name : null });
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                        integers.Add(new { name = propName, description = propDesc, value = material.GetInt(propName) });
                        break;
                }
            }

            return new {
                success = true,
                target = go != null ? go.name : path,
                shader = shader.name,
                renderQueue = material.renderQueue,
                keywords = material.shaderKeywords,
                giFlags = material.globalIlluminationFlags.ToString(),
                properties = new {
                    colors,
                    floats,
                    vectors,
                    textures,
                    integers
                }
            };
        }
        
        [UnitySkill("material_get_keywords", "Get all enabled shader keywords on a material")]
        public static object MaterialGetKeywords(string name = null, int instanceId = 0, string path = null)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            // Get common keywords that might be available
            var commonKeywords = new[] {
                "_EMISSION", "_NORMALMAP", "_METALLICGLOSSMAP", "_SPECGLOSSMAP",
                "_ALPHATEST_ON", "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON",
                "_DETAIL_MULX2", "_PARALLAXMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A",
                "_SPECULARHIGHLIGHTS_OFF", "_ENVIRONMENTREFLECTIONS_OFF",
                "_RECEIVE_SHADOWS_OFF", "_SURFACE_TYPE_TRANSPARENT"
            };
            
            var enabledKeywords = material.shaderKeywords;
            var keywordStatus = new List<object>();
            
            foreach (var kw in commonKeywords)
            {
                keywordStatus.Add(new { keyword = kw, enabled = material.IsKeywordEnabled(kw) });
            }

            return new {
                success = true,
                target = go != null ? go.name : path,
                shader = material.shader.name,
                enabledKeywords,
                commonKeywordStatus = keywordStatus
            };
        }
        
        #endregion
    }
}
