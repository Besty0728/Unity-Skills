using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// 材质技能：创建、修改、赋予。支持按名称、instanceId 或路径查找，
    /// 自动检测渲染管线以选择正确的 shader，并覆盖 HDR、keyword 与 GI flag 操作。
    /// </summary>
    public static class MaterialSkills
    {
        #region Helper Methods
        
        /// <summary>
        /// 按资产路径或 GameObject 的 name/instanceId/path 查找材质。
        ///
        /// <para>两个分支返回的都是磁盘上的材质：直接加载的 .mat，或 <c>renderer.sharedMaterial</c>——
        /// 后者就是同一个 .mat，不是每个 renderer 的副本。因此经由此处的所有 setter 无论调用方怎么寻址
        /// 都在写资产，必须声明 <c>MutatesAssets = true</c>：surface profile 靠该 flag 撤下资产写操作，
        /// 漏标的 setter 会在明令禁止此类操作的 profile 下仍然可调用。用 GameObject 名寻址并不会让写入
        /// 变成场景局部的——它改的是所有使用该材质的对象共享的材质。</para>
        /// </summary>
        private static (Material material, GameObject go, object error) FindMaterial(string name = null, int instanceId = 0, string path = null)
        {
            if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/") || path.EndsWith(".mat")))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return (null, null, new { error = $"Material asset not found: {path}" });
                return (material, null, null);
            }
            
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
        /// 返回调用方可以作为 <c>materialPath</c> 回传、并确实能取回<em>本</em>材质的路径；
        /// 不存在这样的路径时返回 ""。
        ///
        /// <para>陷阱：AssetDatabase.GetAssetPath 给的是<em>容器</em>文件，模型内嵌材质会得到
        /// "Assets/Models/Robot.fbx"。而 LoadAssetAtPath&lt;Material&gt; 会解析子资产（6000.3 上验证），
        /// 并且永远返回该路径下的<em>第一个</em>材质。于是多材质模型的每个材质都回显同一个路径，
        /// 回传后一律解析到 1 号材质——agent 拿 2 号材质的 materialPath 去调 material_set_color，
        /// 改的是 1 号却被告知成功。悄悄写错目标比没有目标更糟。</para>
        ///
        /// <para>因此直接检验主张本身而非其代理：只有把路径载回来恰好得到本材质时才回显它。
        /// .mat 通过（它是自己的主资产），容器里的第一个材质通过（往返确实忠实），其余返回 ""；
        /// 内置材质也返回 ""——GetAssetPath 给它们的是 "Resources/unity_builtin_extra"，
        /// 该路径根本加载不出 Material。</para>
        /// </summary>
        private static string ResolveFeedableMaterialPath(Material material)
        {
            var path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return AssetDatabase.LoadAssetAtPath<Material>(path) == material ? path : string.Empty;
        }

        private static string ResolveSavePath(string savePath, string materialName)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;
                
            if (!savePath.StartsWith("Assets/"))
            {
                savePath = "Assets/" + savePath;
            }
            
            // 看起来像文件夹（无扩展名，或目录已存在）时补上文件名
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

        [UnitySkill("material_create", "Create a new material (auto-detects render pipeline if shader not specified). savePath can be a folder or full path.",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "shader", "pipeline", "asset" },
            // 此处只声明两种成功形态都带的键：agent 在不知道会走哪个分支之前就要据此规划，
            // 这是对 Outputs 唯一诚实的读法。带 savePath 时材质落盘；不带时只存在于内存，
            // 响应额外附带 instanceId + warning——instanceId 恰恰是落盘分支不带的键，不能声明。
            Outputs = new[] { "name", "shader", "path", "entityId", "renderPipeline", "colorProperty", "textureProperty" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object MaterialCreate(string name, string shaderName = null, string savePath = null)
        {
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            if (string.IsNullOrEmpty(shaderName))
            {
                shaderName = ProjectSkills.GetDefaultShaderName();
            }
            
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
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
                savePath = ResolveSavePath(savePath, name);
                EnsureDirectoryExists(savePath);

                AssetDatabase.CreateAsset(material, savePath);
                WorkflowManager.SnapshotObject(material, SnapshotType.Created);
                AssetDatabase.SaveAssets();
            }
            else
            {
                // 未落盘：额外返回 instanceId，供调用方后续引用或销毁
                var pipelineType2 = ProjectSkills.DetectRenderPipeline();
                return new {
                    success = true,
                    name,
                    shader = shaderName,
                    path = (string)null,
                    entityId = UnityObjectIdUtility.GetEntityId(material),
                    instanceId = UnityObjectIdUtility.GetObjectId(material),
                    renderPipeline = pipelineType2.ToString(),
                    colorProperty = ProjectSkills.GetColorPropertyName(),
                    textureProperty = ProjectSkills.GetMainTexturePropertyName(),
                    warning = "Material created in memory only (no savePath). It will be lost on editor restart. Use asset_save or specify savePath to persist."
                };
            }

            var pipelineType = ProjectSkills.DetectRenderPipeline();
            return new {
                success = true,
                name,
                shader = shaderName,
                path = savePath,
                entityId = UnityObjectIdUtility.GetEntityId(material),
                renderPipeline = pipelineType.ToString(),
                colorProperty = ProjectSkills.GetColorPropertyName(),
                textureProperty = ProjectSkills.GetMainTexturePropertyName()
            };
        }

        [UnitySkill("material_assign", "Assign a material asset to a renderer (supports name/instanceId/path)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "material", "assign", "renderer" },
            Outputs = new[] { "gameObject", "material" },
            RequiresInput = new[] { "gameObject", "materialPath" },
            TracksWorkflow = true, MutatesScene = true)]
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

        [UnitySkill("material_create_batch", "Create multiple materials (Efficient). items: JSON array of {name, shaderName?, savePath?}",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "batch", "shader", "pipeline" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialCreateBatch(string items)
        {
            return BatchExecutor.Execute<BatchMaterialCreateItem>(items, item =>
            {
                var result = MaterialCreate(item.name, item.shaderName, item.savePath);
                if (SkillResultHelper.TryGetError(result, out string errorText))
                    return new { error = errorText, target = item.name };
                return result;
            }, item => item.name);
        }

        private class BatchMaterialCreateItem { public string name { get; set; } public string shaderName { get; set; } public string savePath { get; set; } }

        [UnitySkill("material_assign_batch", "Assign materials to multiple objects (Efficient). items: JSON array of {name, materialPath}",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "material", "assign", "batch", "renderer" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject", "materialPath" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object MaterialAssignBatch(string items)
        {
            return BatchExecutor.Execute<BatchMaterialAssignItem>(items, item =>
            {
                var result = MaterialAssign(name: item.name, instanceId: item.instanceId, path: item.path, materialPath: item.materialPath);
                if (SkillResultHelper.TryGetError(result, out string errorText))
                    return new { error = errorText, target = item.name ?? item.path };
                return result;
            }, item => item.name ?? item.path);
        }

        private class BatchMaterialAssignItem { public string name { get; set; } public int instanceId { get; set; } public string path { get; set; } public string materialPath { get; set; } }
        
        [UnitySkill("material_duplicate", "Duplicate an existing material",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "duplicate", "copy", "asset" },
            Outputs = new[] { "name", "path", "sourcePath", "shader" },
            RequiresInput = new[] { "materialPath" },
            // CreateAsset + SaveAssets：与 material_create 一样在磁盘上新建 .mat
            MutatesAssets = true)]
        public static object MaterialDuplicate(string sourcePath, string newName, string savePath = null)
        {
            if (Validate.Required(sourcePath, "sourcePath") is object err) return err;
            if (Validate.Required(newName, "newName") is object err2) return err2;
            if (Validate.SafePath(sourcePath, "sourcePath") is object srcErr) return srcErr;
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object saveErr) return saveErr;

            var sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (sourceMaterial == null)
                return new { error = $"Source material not found: {sourcePath}" };
            
            var newMaterial = new Material(sourceMaterial) { name = newName };
            
            if (string.IsNullOrEmpty(savePath))
            {
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

        [UnitySkill("material_set_color", "Set a color property on a material with optional HDR intensity for emission",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "color", "hdr", "emission", "rendering" },
            Outputs = new[] { "color", "propertyUsed", "intensity", "hdrEnabled" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object MaterialSetColor(string name = null, int instanceId = 0, string path = null, 
            float r = 1, float g = 1, float b = 1, float a = 1, 
            string propertyName = null, float intensity = 1.0f)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (string.IsNullOrEmpty(propertyName))
            {
                propertyName = ProjectSkills.GetColorPropertyName();
            }

            // HDR 强度：自发光场景下大于 1 才会产生 bloom
            var color = new Color(r, g, b, a);
            if (intensity != 1.0f)
            {
                color = new Color(r * intensity, g * intensity, b * intensity, a);
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Color");
            
            bool colorSet = false;
            var propertiesToTry = new[] { propertyName, "_BaseColor", "_Color", "_TintColor", "_EmissionColor" };
            
            foreach (var prop in propertiesToTry)
            {
                if (material.HasProperty(prop))
                {
                    material.SetColor(prop, color);
                    propertyName = prop;
                    colorSet = true;
                    
                    // 设置自发光颜色时自动打开 emission，否则改了颜色也不发光
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

        [UnitySkill("material_set_colors_batch", "Set colors on multiple GameObjects in a single call. items: JSON array of {name, instanceId, path, r, g, b, a}, e.g. [{name:'Obj1',r:1,g:0,b:0},{name:'Obj2',r:0,g:1,b:0}]. Much more efficient than calling material_set_color multiple times.",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "color", "batch", "rendering" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetColorsBatch(string items = null, string propertyName = null)
        {
            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetColorPropertyName();

            return BatchExecutor.Execute<BatchColorItem>(items, item =>
            {
                var (material, go, error) = FindMaterial(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Material not found", target = item.name ?? item.path };

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

                if (!colorSet)
                    return new { error = "No color property found on material", target = material.name };

                if (go == null) EditorUtility.SetDirty(material);
                return new { target = go?.name ?? item.path, success = true };
            }, item => item.name ?? item.path);
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

        [UnitySkill("material_set_emission", "Set emission color with HDR intensity and auto-enable emission",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "emission", "hdr", "glow", "lighting" },
            Outputs = new[] { "emissionColor", "intensity", "hdrColor", "emissionEnabled" },
            RequiresInput = new[] { "gameObject|path" },
            // FindMaterial 解析到 renderer.sharedMaterial，即磁盘上的 .mat，
            // 与 material_set_color 已声明的是同一种写入
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetEmission(string name = null, int instanceId = 0, string path = null,
            float r = 1, float g = 1, float b = 1, float intensity = 1.0f, bool enableEmission = true)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Emission");
            
            var hdrColor = new Color(r * intensity, g * intensity, b * intensity, 1f);

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

        [UnitySkill("material_set_emission_batch", "Set emission on multiple objects (Efficient). items: JSON array of {name, r, g, b, intensity?, enableEmission?}",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "emission", "hdr", "batch", "lighting" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetEmissionBatch(string items)
        {
            return BatchExecutor.Execute<BatchEmissionItem>(items, item =>
            {
                var result = MaterialSetEmission(name: item.name, instanceId: item.instanceId, path: item.path,
                    r: item.r, g: item.g, b: item.b, intensity: item.intensity > 0 ? item.intensity : 1f, enableEmission: item.enableEmission);
                if (SkillResultHelper.TryGetError(result, out string errorText))
                    return new { error = errorText, target = item.name ?? item.path };
                return result;
            }, item => item.name ?? item.path);
        }

        private class BatchEmissionItem { public string name { get; set; } public int instanceId { get; set; } public string path { get; set; } public float r { get; set; } public float g { get; set; } public float b { get; set; } public float intensity { get; set; } = 1f; public bool enableEmission { get; set; } = true; }
        
        #endregion
        
        #region Property Setters

        [UnitySkill("material_set_texture", "Set a texture on a material (auto-detects property name for render pipeline)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "material", "rendering" },
            Outputs = new[] { "texture", "propertyUsed" },
            RequiresInput = new[] { "gameObject|path", "texturePath" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetTexture(string name = null, int instanceId = 0, string path = null, string texturePath = null, string propertyName = null)
        {
            if (Validate.Required(texturePath, "texturePath") is object err) return err;

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

        [UnitySkill("material_set_float", "Set a float property on a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "float", "material" },
            Outputs = new[] { "property", "value" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
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
        
        [UnitySkill("material_set_int", "Set an integer property on a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "integer", "material" },
            Outputs = new[] { "property", "value" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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
        
        [UnitySkill("material_set_vector", "Set a vector4 property on a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "vector", "material" },
            Outputs = new[] { "property", "value" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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
        
        [UnitySkill("material_set_texture_offset", "Set texture offset (tiling position)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "offset", "tiling", "uv" },
            Outputs = new[] { "property", "offset" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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
        
        [UnitySkill("material_set_texture_scale", "Set texture scale (tiling)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "scale", "tiling", "uv" },
            Outputs = new[] { "property", "scale" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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

        [UnitySkill("material_set_keyword", "Enable or disable a shader keyword (e.g., _EMISSION, _NORMALMAP, _METALLICGLOSSMAP)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "keyword", "shader", "rendering" },
            Outputs = new[] { "keyword", "enabled", "allKeywords" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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

            // 与普通属性值不同，keyword 的启用不会被 Unity 自身的脏标记可靠地识别为"已变更"，
            // 没有显式 SetDirty + SaveAssets 就写不进磁盘 .mat 的 m_ValidKeywords——
            // 与 PrefabSetProperty 写入时依赖的是同一套约定。此处不按 go == null 分支：
            // 经由 GameObject 的 renderer 寻址到的仍是同一个共享 .mat 资产，不是场景局部副本。
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return new {
                success = true,
                target = go != null ? go.name : path,
                keyword,
                enabled = enable,
                allKeywords = material.shaderKeywords
            };
        }
        
        [UnitySkill("material_set_render_queue", "Set material render queue (-1 for shader default, 2000=Geometry, 2450=AlphaTest, 3000=Transparent)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "renderQueue", "sorting", "transparency" },
            Outputs = new[] { "renderQueue", "queueCategory" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
        public static object MaterialSetRenderQueue(string name = null, int instanceId = 0, string path = null, int renderQueue = -1)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Render Queue");
            material.renderQueue = renderQueue;

            // 与 material_set_keyword 同样的不落盘问题：缺少显式 SetDirty + SaveAssets，
            // 磁盘上的 m_CustomRenderQueue 会保持 -1，内存值也可能在下次重导入时被算回 shader 默认值。
            // 同样不分支：无论调用方怎么寻址，renderer.sharedMaterial 都是磁盘资产而非逐对象副本。
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

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
        
        [UnitySkill("material_set_shader", "Change the shader of a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "shader", "material", "pipeline" },
            Outputs = new[] { "shader" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
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
        
        [UnitySkill("material_set_gi_flags", "Set global illumination flags (None, RealtimeEmissive, BakedEmissive, EmissiveIsBlack)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "gi", "globalIllumination", "emission", "lighting" },
            Outputs = new[] { "giFlags" },
            RequiresInput = new[] { "gameObject|path" },
            MutatesAssets = true)]
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

        [UnitySkill("material_get_properties", "Get all properties of a material (colors, floats, textures, keywords). Responds with materialPath = a path that loads back to exactly the material inspected, so a lookup by GameObject name can be traced to a concrete asset and reused. Empty when no such path exists: built-in materials, and materials embedded in a model file past the first one (a .fbx shares one path across all its materials, and that path always loads the first, so echoing it for the others would send a follow-up write to the wrong material).",
            Category = SkillCategory.Material, Operation = SkillOperation.Query,
            Tags = new[] { "property", "inspect", "shader", "material" },
            Outputs = new[] { "materialPath", "shader", "renderQueue", "keywords", "giFlags", "properties" },
            RequiresInput = new[] { "gameObject|path" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                        floats.Add(new { name = propName, description = propDesc, value = material.GetFloat(propName), min = 0f, max = 0f });
                        break;
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
                materialPath = ResolveFeedableMaterialPath(material),
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
        
        [UnitySkill("material_get_keywords", "Get all enabled shader keywords on a material. Responds with materialPath = a path that loads back to exactly the material inspected, empty when no such path exists (built-in materials, and materials embedded in a model file past the first one — see material_get_properties).",
            Category = SkillCategory.Material, Operation = SkillOperation.Query,
            Tags = new[] { "keyword", "shader", "inspect" },
            Outputs = new[] { "materialPath", "shader", "enabledKeywords", "commonKeywordStatus" },
            RequiresInput = new[] { "gameObject|path" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object MaterialGetKeywords(string name = null, int instanceId = 0, string path = null)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

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
                materialPath = ResolveFeedableMaterialPath(material),
                shader = material.shader.name,
                enabledKeywords,
                commonKeywordStatus = keywordStatus
            };
        }
        
        #endregion
    }
}

// Producer:Betsy
