using UnityEngine;
using UnityEditor;
using UnityEditor.PackageManager;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using PkgInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnitySkills
{
    /// <summary>
    /// Material skills: create, modify, assign. Supports lookup by name, instanceId, or path,
    /// auto-detects the render pipeline to pick the correct shader, and covers HDR, keyword, and GI flag operations.
    /// </summary>
    public static class MaterialSkills
    {
        private const string MaterialPathNote = "'Assets/...' or '*.mat' = that material asset; any other value is a hierarchy path, targeting its Renderer's sharedMaterial (the shared asset).";
        private const string ShaderPropertyNote = "Shader property reference name (e.g. _Metallic, _Cutoff), not the Inspector label; material_get_properties lists them.";
        private const string MainTextureNote = "Shader texture property; default _BaseMap (URP), _BaseColorMap (HDRP) or _MainTex (Built-in).";

        #region Helper Methods
        
        /// <summary>
        /// Finds a material by asset path, or by a GameObject's name/instanceId/path.
        ///
        /// <para>Both branches return the material on disk: either a directly loaded .mat, or <c>renderer.sharedMaterial</c> --
        /// the latter IS that same .mat, not a per-renderer copy. So every setter that goes through here, regardless of how the caller addresses it,
        /// is writing to the asset, and must declare <c>MutatesAssets = true</c>: the surface profile relies on that flag to withdraw asset-write operations,
        /// and a setter missing this tag remains callable even under a profile that explicitly forbids such operations. Addressing by GameObject name doesn't make the write
        /// scene-local -- it modifies the material shared by every object that uses it.</para>
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
        /// Returns a path the caller can feed back as <c>materialPath</c> that actually resolves back to <em>this</em> material;
        /// returns "" when no such path exists.
        ///
        /// <para>Trap: AssetDatabase.GetAssetPath gives the <em>container</em> file -- a model's embedded material gets
        /// "Assets/Models/Robot.fbx". But LoadAssetAtPath&lt;Material&gt; resolves sub-assets (verified on 6000.3),
        /// and always returns the <em>first</em> material at that path. So every material of a multi-material model echoes back the same path,
        /// and feeding that path back always resolves to material #1 -- an agent that takes material #2's materialPath and calls material_set_color
        /// ends up modifying #1 while being told it succeeded. Silently writing to the wrong target is worse than having no target.</para>
        ///
        /// <para>So this verifies the claim directly rather than trusting a proxy for it: it only echoes the path back when loading that path
        /// actually yields this exact material. A .mat passes (it's its own main asset), the first material in a container passes (the round trip is genuinely faithful), everything else returns "";
        /// built-in materials also return "" -- GetAssetPath gives them "Resources/unity_builtin_extra",
        /// a path that can't load a Material at all.</para>
        /// </summary>
        private static string ResolveFeedableMaterialPath(Material material)
        {
            var path = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return AssetDatabase.LoadAssetAtPath<Material>(path) == material ? path : string.Empty;
        }

        private static string NormalizeSlashes(string path)
        {
            var normalized = path.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            if (normalized.StartsWith("./")) normalized = normalized.Substring(2);
            return normalized.TrimEnd('/');
        }

        /// <summary>
        /// Pure normalization + filename derivation for a material save path (bugs.md B3).
        /// <see cref="Validate.SafePath"/> validates the raw string but never returns its
        /// normalized form, so a malformed-but-valid input like "Assets\Mats" or "Assets//Mats"
        /// used to reach AssetDatabase unnormalized and land in the wrong folder.
        /// </summary>
        private static string NormalizeMaterialSavePath(string savePath, string materialName)
        {
            if (string.IsNullOrEmpty(savePath))
                return null;

            var normalized = NormalizeSlashes(savePath);

            // Append a filename when it looks like a folder (no extension, or the directory already exists)
            if (Directory.Exists(normalized) || !Path.HasExtension(normalized))
            {
                string fileName = string.IsNullOrEmpty(materialName) ? "NewMaterial" : materialName;
                normalized = Path.Combine(normalized, fileName + ".mat").Replace("\\", "/");
            }
            else if (!normalized.EndsWith(".mat"))
            {
                normalized += ".mat";
            }

            return normalized;
        }

        /// <summary>Which package (if any) owns a resolved Packages/ asset path, and whether it's writable.</summary>
        internal readonly struct PackageWriteCheck
        {
            internal readonly bool Found;
            internal readonly bool Writable;
            internal readonly string PackageName;
            internal readonly string PackageSource;

            internal PackageWriteCheck(bool found, bool writable, string packageName, string packageSource)
            {
                Found = found;
                Writable = writable;
                PackageName = packageName;
                PackageSource = packageSource;
            }
        }

        private static PackageWriteCheck CheckPackageWritability(string assetPath)
        {
            var info = PkgInfo.FindForAssetPath(assetPath);
            if (info == null)
                return new PackageWriteCheck(found: false, writable: false, packageName: null, packageSource: null);

            bool writable = info.source == PackageSource.Embedded || info.source == PackageSource.Local;
            return new PackageWriteCheck(found: true, writable, info.name, info.source.ToString());
        }

        private static object BuildPackagePathError(string savePath, string reason, string packageName, string packageSource)
        {
            return new
            {
                error = $"Invalid value '{savePath}' for parameter 'savePath': {reason}. Materials can be saved under Assets/ or inside an embedded/local package.",
                errorCode = SkillParamUtil.SemanticInvalidCode,
                retryStrategy = SkillErrorResponse.RetryFixAndRetry,
                parameter = "savePath",
                packageName,
                packageSource,
                suggestedFixes = new object[]
                {
                    new { action = "fix_param", args = new { savePath = "Assets/Materials" }, reason = "Save the material under Assets/ instead." }
                },
            };
        }

        /// <summary>
        /// Resolves a material save path and rejects it before any write happens (bugs.md B3):
        /// normalizes like <see cref="Validate.SafePath"/> and appends "&lt;materialName&gt;.mat"
        /// when the path looks like a folder. A Packages/ destination is accepted only inside an
        /// embedded or local package -- registry/git/built-in packages are read-only on disk, so
        /// writing there would fail deep inside AssetDatabase instead of with a structured error.
        /// </summary>
        internal static bool TryResolveMaterialSavePath(string savePath, string materialName, out string resolved, out object error) =>
            TryResolveMaterialSavePath(savePath, materialName, CheckPackageWritability, out resolved, out error);

        /// <summary>
        /// The <paramref name="packageWritabilityCheck"/> seam lets tests stub package ownership
        /// without depending on which packages happen to be installed in the test project.
        /// </summary>
        internal static bool TryResolveMaterialSavePath(string savePath, string materialName,
            System.Func<string, PackageWriteCheck> packageWritabilityCheck, out string resolved, out object error)
        {
            error = null;
            resolved = null;

            if (string.IsNullOrEmpty(savePath))
                return true;

            if (string.Equals(NormalizeSlashes(savePath), "Packages", System.StringComparison.Ordinal))
            {
                error = BuildPackagePathError(savePath, "the Packages root is not a folder", null, null);
                return false;
            }

            var candidate = NormalizeMaterialSavePath(savePath, materialName);
            if (candidate.StartsWith("Packages/", System.StringComparison.Ordinal))
            {
                var check = packageWritabilityCheck(candidate);
                if (!check.Writable)
                {
                    var reason = check.Found
                        ? $"package '{check.PackageName}' is a {check.PackageSource} package (read-only)"
                        : $"no installed package owns '{candidate}'";
                    error = BuildPackagePathError(savePath, reason, check.PackageName, check.PackageSource);
                    return false;
                }
            }

            resolved = candidate;
            return true;
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

        /// <summary>Forwards a structured error object as-is, plus a batch item's <c>target</c> (bugs.md B1).</summary>
        private static JObject WithTarget(object error, string target)
        {
            var json = JObject.FromObject(error);
            json["target"] = target;
            return json;
        }

        /// <summary>All colour-type properties this shader declares, Color-typed ones first, then Vector-typed (bugs.md B1's <c>validValues</c> ordering).</summary>
        private static string[] ShaderColourProperties(Shader shader)
        {
            var colors = new List<string>();
            var vectors = new List<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var type = shader.GetPropertyType(i);
                if (type == UnityEngine.Rendering.ShaderPropertyType.Color) colors.Add(shader.GetPropertyName(i));
                else if (type == UnityEngine.Rendering.ShaderPropertyType.Vector) vectors.Add(shader.GetPropertyName(i));
            }
            colors.AddRange(vectors);
            return colors.ToArray();
        }

        private static object ColourPropertyError(Material material, string requested, string[] colourProps, string reason)
        {
            var closest = SkillsCommon.ClosestMatch(requested, colourProps);
            var fixes = new List<object>();
            if (closest != null)
                fixes.Add(new { action = "fix_param", args = new { propertyName = closest }, reason = "Closest colour property on this shader." });
            fixes.Add(new { action = "fix_param", skill = "material_get_properties", reason = "List the shader's properties and their types." });
            fixes.Add(new { action = "fix_param", reason = "Omit propertyName to auto-detect the main colour; propertyUsed reports it." });

            return new
            {
                error = $"Invalid value '{requested}' for parameter 'propertyName': {reason}. Valid values: {string.Join(", ", colourProps)}.",
                errorCode = SkillParamUtil.SemanticInvalidCode,
                retryStrategy = SkillErrorResponse.RetryFixAndRetry,
                parameter = "propertyName",
                validValues = colourProps,
                shaderName = material.shader.name,
                suggestedFixes = fixes.ToArray(),
            };
        }

        /// <summary>
        /// Resolves which shader colour property material_set_color / material_set_colors_batch actually
        /// writes (bugs.md B1, H8). "Colour type" is <c>HasColor || HasVector</c>, never plain HasProperty --
        /// that also matches Float/Texture/Int properties, so a caller's typo used to silently write a colour
        /// into a slot the shader never reads as one.
        ///
        /// <para>An omitted <paramref name="requested"/> keeps today's auto-detect chain (pipeline default,
        /// then _BaseColor/_Color/_TintColor/_EmissionColor). A given name is honoured exactly, then
        /// case-insensitively if that uniquely matches one colour property (with a warning), then --
        /// only for the main-colour family _BaseColor/_Color/_MainColor/_TintColor -- falls back to
        /// whichever of that family the shader actually has (with a warning): a habit formed on one
        /// pipeline's default keeps working on another. It never substitutes _EmissionColor for an
        /// explicit request; that would turn "set the main colour" into "make it glow".</para>
        /// </summary>
        internal static bool TryResolveColorProperty(Material material, string requested, out string used, out string warning, out object error)
        {
            used = null;
            warning = null;
            error = null;

            bool IsColourProperty(string n) => material.HasColor(n) || material.HasVector(n);

            if (string.IsNullOrEmpty(requested))
            {
                var autoChain = new[] { ProjectSkills.GetColorPropertyName(), "_BaseColor", "_Color", "_TintColor", "_EmissionColor" };
                foreach (var candidate in autoChain)
                {
                    if (IsColourProperty(candidate)) { used = candidate; return true; }
                }
                error = new
                {
                    error = $"Material does not have a color property. Tried: {string.Join(", ", autoChain)}",
                    shaderName = material.shader.name,
                    suggestion = "Use material_get_properties to see available properties"
                };
                return false;
            }

            var colourProps = ShaderColourProperties(material.shader);

            if (material.HasProperty(requested))
            {
                if (IsColourProperty(requested)) { used = requested; return true; }
                var typeName = material.shader.GetPropertyType(material.shader.FindPropertyIndex(requested)).ToString();
                error = ColourPropertyError(material, requested, colourProps, $"'{requested}' is a {typeName} property, not a colour");
                return false;
            }

            var caseInsensitiveMatches = colourProps.Where(p => string.Equals(p, requested, System.StringComparison.OrdinalIgnoreCase)).ToArray();
            if (caseInsensitiveMatches.Length == 1)
            {
                used = caseInsensitiveMatches[0];
                warning = $"'{requested}' matched shader property '{used}' case-insensitively.";
                return true;
            }

            var mainColourFamily = new[] { "_BaseColor", "_Color", "_MainColor", "_TintColor" };
            if (mainColourFamily.Any(n => string.Equals(n, requested, System.StringComparison.OrdinalIgnoreCase)))
            {
                var fallback = mainColourFamily.FirstOrDefault(IsColourProperty);
                if (fallback != null)
                {
                    used = fallback;
                    warning = $"'{requested}' is not on shader '{material.shader.name}'; wrote its main-colour equivalent '{fallback}'.";
                    return true;
                }
            }

            error = ColourPropertyError(material, requested, colourProps, $"shader '{material.shader.name}' has no colour property named '{requested}'");
            return false;
        }

        /// <summary>Writes a colour property, replicating material_set_color's "landing on _EmissionColor turns on emission" side effect (bugs.md B1).</summary>
        private static void WriteColorProperty(Material material, string property, Color color, bool enableEmissionIfLanded)
        {
            material.SetColor(property, color);
            if (property == "_EmissionColor" && enableEmissionIfLanded)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }

        #endregion
        
        #region Material Creation & Assignment

        [UnitySkill("material_create", "Create a new material (auto-detects render pipeline if shader not specified). savePath can be a folder or full path.",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "shader", "pipeline", "asset" },
            // Only the keys present in both success shapes are declared here: the agent has to plan against this before knowing which branch it will hit,
            // which is the only honest reading of Outputs. With savePath, the material is written to disk; without it, it only exists in memory,
            // and the response additionally carries instanceId + warning -- instanceId is precisely the key the on-disk branch doesn't carry, so it can't be declared.
            Outputs = new[] { "name", "shader", "path", "entityId", "renderPipeline", "colorProperty", "textureProperty" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object MaterialCreate(string name,
            [SkillParam("Full shader name, e.g. 'Universal Render Pipeline/Lit'; omitted = the pipeline's default Lit shader (URP Lit, HDRP/Lit or Standard). An unresolved name falls back to the pipeline default and the response adds shaderRequested + warnings.")]
            string shaderName = null,
            [SkillParam("Assets/... or a folder inside an embedded/local package (Packages/<id>/...); read-only packages are rejected. A folder (existing, or no extension) gets '<name>.mat'; '.mat' is appended if missing. Omit for an unsaved in-memory material.")]
            string savePath = null)
        {
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            // Resolved before the Material exists (bugs.md B3): a rejected savePath must leave no
            // in-memory Material behind for the caller to have to notice and clean up.
            string resolvedSavePath = null;
            if (!string.IsNullOrEmpty(savePath))
            {
                if (!TryResolveMaterialSavePath(savePath, name, out resolvedSavePath, out var saveErr))
                    return saveErr;
            }

            var requestedShaderName = string.IsNullOrEmpty(shaderName) ? ProjectSkills.GetDefaultShaderName() : shaderName;
            shaderName = requestedShaderName;

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
                        error = $"Shader not found: {requestedShaderName}. Detected pipeline: {pipelineInfo}. Try using project_get_render_pipeline to see available shaders.",
                        detectedPipeline = pipelineInfo.ToString(),
                        recommendedShader = ProjectSkills.GetDefaultShaderName()
                    };
                }
            }

            // bugs.md B2: the fallback chain above is a documented capability and stays; only its
            // visibility was missing. shaderFellBack is true only when the caller's own name (or the
            // pipeline default, when omitted) didn't resolve directly.
            bool shaderFellBack = shaderName != requestedShaderName;
            var material = new Material(shader) { name = name };

            if (resolvedSavePath != null)
            {
                EnsureDirectoryExists(resolvedSavePath);

                AssetDatabase.CreateAsset(material, resolvedSavePath);
                WorkflowManager.SnapshotObject(material, SnapshotType.Created);
                AssetDatabase.SaveAssets();
            }
            else
            {
                // Not written to disk: additionally return instanceId, for the caller to reference or destroy later
                var pipelineType2 = ProjectSkills.DetectRenderPipeline();
                var inMemoryResult = new Dictionary<string, object> {
                    ["success"] = true,
                    ["name"] = name,
                    ["shader"] = shaderName,
                    ["path"] = (string)null,
                    ["entityId"] = UnityObjectIdUtility.GetEntityId(material),
                    ["instanceId"] = UnityObjectIdUtility.GetObjectId(material),
                    ["renderPipeline"] = pipelineType2.ToString(),
                    ["colorProperty"] = ProjectSkills.GetColorPropertyName(),
                    ["textureProperty"] = ProjectSkills.GetMainTexturePropertyName(),
                    ["warning"] = "Material created in memory only (no savePath). It will be lost on editor restart. Use asset_save or specify savePath to persist."
                };
                if (shaderFellBack)
                {
                    inMemoryResult["shaderRequested"] = requestedShaderName;
                    inMemoryResult["warnings"] = new[] { $"Shader '{requestedShaderName}' not found; used '{shaderName}' ({pipelineType2} fallback). Pass an exact name (project_get_render_pipeline lists them) or a shader asset path." };
                }
                return inMemoryResult;
            }

            var pipelineType = ProjectSkills.DetectRenderPipeline();
            var onDiskResult = new Dictionary<string, object> {
                ["success"] = true,
                ["name"] = name,
                ["shader"] = shaderName,
                ["path"] = resolvedSavePath,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(material),
                ["renderPipeline"] = pipelineType.ToString(),
                ["colorProperty"] = ProjectSkills.GetColorPropertyName(),
                ["textureProperty"] = ProjectSkills.GetMainTexturePropertyName()
            };
            if (shaderFellBack)
            {
                onDiskResult["shaderRequested"] = requestedShaderName;
                onDiskResult["warnings"] = new[] { $"Shader '{requestedShaderName}' not found; used '{shaderName}' ({pipelineType} fallback). Pass an exact name (project_get_render_pipeline lists them) or a shader asset path." };
            }
            return onDiskResult;
        }

        [UnitySkill("material_assign", "Assign a material asset to a renderer's first slot (supports name/instanceId/path). material/materialName are read back from renderer.sharedMaterial after the assignment.",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "material", "assign", "renderer" },
            Outputs = new[] { "gameObject", "material", "materialName" },
            RequiresInput = new[] { "gameObject", "materialPath" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object MaterialAssign(string name = null, int instanceId = 0, string path = null,
            [SkillParam("Project path of a material asset, e.g. Assets/Materials/Red.mat; a model file path yields its first embedded material.")]
            string materialPath = null)
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

            var assigned = renderer.sharedMaterial;
            return new
            {
                success = true,
                gameObject = go.name,
                material = assigned != null ? AssetDatabase.GetAssetPath(assigned) : null,
                materialName = assigned != null ? assigned.name : null,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go)
            };
        }

        [UnitySkill("material_create_batch", "Create multiple materials (Efficient). items: JSON array of {name, shaderName?, savePath?}",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "batch", "shader", "pipeline" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialCreateBatch(
            [SkillParam("JSON array of {name, shaderName?, savePath?}, each as in material_create (no savePath = unsaved in-memory material).")]
            string items)
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
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object MaterialAssignBatch(
            [SkillParam("JSON array of {name|path|instanceId, materialPath}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchMaterialAssignItem>(items, item =>
            {
                var result = MaterialAssign(name: item.name, instanceId: item.instanceId, path: item.path, materialPath: item.materialPath);
                if (SkillResultHelper.TryGetError(result, out string errorText))
                    return new { error = errorText, target = item.name ?? item.path };
                return result;
            }, item => item.name ?? item.path, atomic: true);
        }

        private class BatchMaterialAssignItem { public string name { get; set; } public int instanceId { get; set; } public string path { get; set; } public string materialPath { get; set; } }
        
        [UnitySkill("material_duplicate", "Duplicate an existing material",
            Category = SkillCategory.Material, Operation = SkillOperation.Create,
            Tags = new[] { "material", "duplicate", "copy", "asset" },
            Outputs = new[] { "name", "path", "sourcePath", "shader" },
            // Real accepted params are sourcePath/newName - "materialPath" (the token) doesn't exist on this
            // skill at all. Both sourcePath and newName have no CLR default and the body already Validate.Requires
            // both, so declaring them here just brings the schema in line with what execution already enforces.
            RequiresInput = new[] { "sourcePath", "newName" },
            // CreateAsset + SaveAssets: creates a new .mat on disk, same as material_create
            MutatesAssets = true)]
        public static object MaterialDuplicate(string sourcePath, string newName,
            [SkillParam("Assets/... or a folder inside an embedded/local package (Packages/<id>/...); read-only packages are rejected. A folder (existing, or no extension) gets '<newName>.mat'; '.mat' is appended if missing. Omitted: saved next to the source material.")]
            string savePath = null)
        {
            if (Validate.Required(sourcePath, "sourcePath") is object err) return err;
            if (Validate.Required(newName, "newName") is object err2) return err2;
            if (Validate.SafePath(sourcePath, "sourcePath") is object srcErr) return srcErr;
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object saveErr) return saveErr;

            var sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
            if (sourceMaterial == null)
                return new { error = $"Source material not found: {sourcePath}" };

            // Resolved before the copy is made (bugs.md B3): a rejected path must leave no orphaned
            // in-memory Material behind.
            bool savePathOmitted = string.IsNullOrEmpty(savePath);
            var effectiveSavePath = savePath;
            if (savePathOmitted)
            {
                var sourceDir = Path.GetDirectoryName(sourcePath);
                effectiveSavePath = Path.Combine(sourceDir, newName + ".mat").Replace("\\", "/");
            }

            if (!TryResolveMaterialSavePath(effectiveSavePath, newName, out var resolvedSavePath, out var resolveErr))
            {
                // The default path (derived from the source's own folder) landing in a read-only
                // package is a different situation for the caller than an explicit bad savePath:
                // the fix is "pass savePath", not "pass a different savePath".
                if (savePathOmitted)
                {
                    var json = JObject.FromObject(resolveErr);
                    var packageName = json.Value<string>("packageName");
                    json["error"] = packageName != null
                        ? $"Invalid value '{effectiveSavePath}' for parameter 'savePath': the source material lives in read-only package '{packageName}'; pass savePath."
                        : $"Invalid value '{effectiveSavePath}' for parameter 'savePath': the source material's folder has no writable location for a duplicate; pass savePath.";
                    return json;
                }
                return resolveErr;
            }

            var newMaterial = new Material(sourceMaterial) { name = newName };

            EnsureDirectoryExists(resolvedSavePath);
            AssetDatabase.CreateAsset(newMaterial, resolvedSavePath);
            WorkflowManager.SnapshotObject(newMaterial, SnapshotType.Created);
            AssetDatabase.SaveAssets();

            return new {
                success = true,
                name = newName,
                path = resolvedSavePath,
                sourcePath,
                shader = newMaterial.shader.name
            };
        }
        
        #endregion
        
        #region Color & Emission

        [UnitySkill("material_set_color", "Set a color property on a material with optional HDR intensity for emission",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "color", "hdr", "emission", "rendering" },
            Outputs = new[] { "color", "propertyUsed", "intensity", "hdrEnabled", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true,
            MutatesAssets = true)]
        public static object MaterialSetColor(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam("r/g/b/a are 0-1 floats; intensity multiplies r/g/b but not a.")]
            float r = 1, float g = 1, float b = 1, float a = 1,
            [SkillParam("Shader colour property, exact then case-insensitive. Omitted: auto-detect (pipeline default, _BaseColor, _Color, _TintColor, _EmissionColor). A missing _BaseColor/_Color/_MainColor/_TintColor falls back to the main colour present (propertyRequested + warnings); any other missing or non-colour name is rejected with validValues. propertyUsed names the property written.")]
            string propertyName = null,
            [SkillParam("HDR multiplier on r/g/b (alpha unchanged); writing _EmissionColor with intensity > 0 also enables _EMISSION.")]
            float intensity = 1.0f)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (!TryResolveColorProperty(material, propertyName, out var resolvedProperty, out var warning, out var resolveError))
                return resolveError;

            // HDR intensity: for emission, only values greater than 1 produce bloom
            var color = new Color(r, g, b, a);
            if (intensity != 1.0f)
            {
                color = new Color(r * intensity, g * intensity, b * intensity, a);
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Material Color");

            WriteColorProperty(material, resolvedProperty, color, intensity > 0);

            if (go == null) EditorUtility.SetDirty(material);

            var result = new Dictionary<string, object> {
                ["success"] = true,
                ["target"] = go != null ? go.name : path,
                ["color"] = ColorObject(material.GetColor(resolvedProperty)),
                ["intensity"] = intensity,
                ["propertyUsed"] = resolvedProperty,
                ["hdrEnabled"] = resolvedProperty == "_EmissionColor" && material.IsKeywordEnabled("_EMISSION"),
                ["materialPath"] = ResolveFeedableMaterialPath(material),
            };
            if (warning != null)
            {
                result["propertyRequested"] = propertyName;
                result["warnings"] = new[] { warning };
            }
            return result;
        }

        private static object ColorObject(Color c) => new { r = c.r, g = c.g, b = c.b, a = c.a };

        [UnitySkill("material_set_colors_batch", "Set colors on multiple GameObjects in a single call. items: JSON array of {name, instanceId, path, r, g, b, a}, e.g. [{name:'Obj1',r:1,g:0,b:0},{name:'Obj2',r:0,g:1,b:0}]. Much more efficient than calling material_set_color multiple times.",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "color", "batch", "rendering" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetColorsBatch(
            [SkillParam("JSON array of {name|path|instanceId, r?, g?, b?, a?, propertyName?}: 0-1 floats, each omitted channel = 1; path may be a material asset path; propertyName overrides the batch-level one.")]
            string items = null,
            [SkillParam("Default colour property for every item, same rules as material_set_color; omitted = auto-detect.")]
            string propertyName = null)
        {
            return BatchExecutor.Execute<BatchColorItem>(items, item =>
            {
                var (material, go, error) = FindMaterial(item.name, item.instanceId, item.path);
                if (error != null) return WithTarget(error, item.name ?? item.path);

                var effectiveProperty = item.propertyName ?? propertyName;
                if (!TryResolveColorProperty(material, effectiveProperty, out var resolvedProperty, out var warning, out var resolveError))
                    return WithTarget(resolveError, item.name ?? item.path);

                var color = new Color(item.r, item.g, item.b, item.a);

                WorkflowManager.SnapshotObject(material);
                Undo.RecordObject(material, "Batch Set Color");
                WriteColorProperty(material, resolvedProperty, color, enableEmissionIfLanded: true);

                if (go == null) EditorUtility.SetDirty(material);

                var result = new Dictionary<string, object> {
                    ["target"] = go?.name ?? item.path,
                    ["success"] = true,
                    ["propertyUsed"] = resolvedProperty,
                    ["color"] = ColorObject(material.GetColor(resolvedProperty)),
                };
                if (warning != null)
                {
                    result["propertyRequested"] = effectiveProperty;
                    result["warnings"] = new[] { warning };
                }
                return result;
            }, item => item.name ?? item.path, atomic: true);
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
            public string propertyName { get; set; }
        }

        [UnitySkill("material_set_emission", "Set emission color with HDR intensity and auto-enable emission",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "emission", "hdr", "glow", "lighting" },
            Outputs = new[] { "emissionColor", "intensity", "hdrColor", "emissionEnabled", "giFlags", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            // FindMaterial resolves to renderer.sharedMaterial, i.e. the .mat on disk,
            // the same kind of write already declared by material_set_color
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetEmission(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam("r/g/b: base emission colour as 0-1 floats; intensity multiplies them.")]
            float r = 1, float g = 1, float b = 1,
            [SkillParam("HDR multiplier on r/g/b; intensity <= 0 or enableEmission=false disables _EMISSION.")]
            float intensity = 1.0f, bool enableEmission = true)
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

            var readHdrColor = material.GetColor(emissionProperty);
            return new {
                success = true,
                target = go != null ? go.name : path,
                emissionColor = new { r, g, b },
                intensity,
                hdrColor = new { r = readHdrColor.r, g = readHdrColor.g, b = readHdrColor.b },
                emissionEnabled = material.IsKeywordEnabled("_EMISSION"),
                giFlags = material.globalIlluminationFlags.ToString(),
                materialPath = ResolveFeedableMaterialPath(material)
            };
        }

        [UnitySkill("material_set_emission_batch", "Set emission on multiple objects (Efficient). items: JSON array of {name, r, g, b, intensity?, enableEmission?}",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "emission", "hdr", "batch", "lighting" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetEmissionBatch(
            [SkillParam("JSON array of {name|path|instanceId, r, g, b (0-1, omitted = 0; material_set_emission defaults to 1), intensity? (default 1; <= 0 disables emission, as in material_set_emission), enableEmission? (default true)}.")]
            string items)
        {
            return BatchExecutor.Execute<BatchEmissionItem>(items, item =>
            {
                var result = MaterialSetEmission(name: item.name, instanceId: item.instanceId, path: item.path,
                    r: item.r ?? 0f, g: item.g ?? 0f, b: item.b ?? 0f, intensity: item.intensity, enableEmission: item.enableEmission);
                if (SkillResultHelper.TryGetError(result, out string errorText))
                    return new { error = errorText, target = item.name ?? item.path };

                var json = JObject.FromObject(result);
                bool allChannelsOmitted = item.r == null && item.g == null && item.b == null;
                if (allChannelsOmitted && json.Value<bool>("emissionEnabled"))
                {
                    json["warnings"] = JArray.FromObject(new[] {
                        "r/g/b omitted: batch channels default to 0, so the emission colour is black. Pass r/g/b (material_set_emission defaults them to 1)."
                    });
                }
                return json;
            }, item => item.name ?? item.path, atomic: true);
        }

        private class BatchEmissionItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public float? r { get; set; }
            public float? g { get; set; }
            public float? b { get; set; }
            public float intensity { get; set; } = 1f;
            public bool enableEmission { get; set; } = true;
        }
        
        #endregion
        
        #region Property Setters

        [UnitySkill("material_set_texture", "Set a texture on a material (auto-detects property name for render pipeline)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "material", "rendering" },
            Outputs = new[] { "texture", "propertyUsed", "materialPath" },
            RequiresInput = new[] { "gameObject|path", "texturePath" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetTexture(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            string texturePath = null,
            [SkillParam(MainTextureNote)] string propertyName = null)
        {
            if (Validate.Required(texturePath, "texturePath") is object err) return err;

            if (string.IsNullOrEmpty(propertyName))
            {
                propertyName = ProjectSkills.GetMainTexturePropertyName();
            }

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

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
                return new { error = $"Texture not found: {texturePath}" };

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture");
            material.SetTexture(propertyName, texture);

            if (go == null) EditorUtility.SetDirty(material);

            var assignedTexture = material.GetTexture(propertyName);
            return new {
                success = true,
                target = go != null ? go.name : path,
                texture = assignedTexture != null ? AssetDatabase.GetAssetPath(assignedTexture) : null,
                propertyUsed = propertyName,
                materialPath = ResolveFeedableMaterialPath(material)
            };
        }

        [UnitySkill("material_set_float", "Set a float property on a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "property", "float", "material" },
            Outputs = new[] { "property", "value" },
            RequiresInput = new[] { "gameObject|path" },
            RequiredParams = new[] { "propertyName" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetFloat(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam(ShaderPropertyNote)] string propertyName = null,
            float value = 0)
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
            RequiredParams = new[] { "propertyName" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetInt(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam(ShaderPropertyNote)] string propertyName = null,
            int value = 0)
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
            RequiredParams = new[] { "propertyName" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetVector(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam(ShaderPropertyNote)] string propertyName = null,
            float x = 0, float y = 0, float z = 0, float w = 0)
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
            Outputs = new[] { "property", "offset", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetTextureOffset(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam(MainTextureNote)] string propertyName = null,
            float x = 0, float y = 0)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetMainTexturePropertyName();

            if (!material.HasProperty(propertyName))
            {
                return new {
                    error = $"Property not found: {propertyName}",
                    shaderName = material.shader.name,
                    suggestion = "Use material_get_properties to see available properties"
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture Offset");
            material.SetTextureOffset(propertyName, new Vector2(x, y));

            if (go == null) EditorUtility.SetDirty(material);

            var readOffset = material.GetTextureOffset(propertyName);
            return new { success = true, target = go != null ? go.name : path, property = propertyName, offset = new { x = readOffset.x, y = readOffset.y }, materialPath = ResolveFeedableMaterialPath(material) };
        }

        [UnitySkill("material_set_texture_scale", "Set texture scale (tiling)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "texture", "scale", "tiling", "uv" },
            Outputs = new[] { "property", "scale", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetTextureScale(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam(MainTextureNote)] string propertyName = null,
            float x = 1, float y = 1)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            if (string.IsNullOrEmpty(propertyName))
                propertyName = ProjectSkills.GetMainTexturePropertyName();

            if (!material.HasProperty(propertyName))
            {
                return new {
                    error = $"Property not found: {propertyName}",
                    shaderName = material.shader.name,
                    suggestion = "Use material_get_properties to see available properties"
                };
            }

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Texture Scale");
            material.SetTextureScale(propertyName, new Vector2(x, y));

            if (go == null) EditorUtility.SetDirty(material);

            var readScale = material.GetTextureScale(propertyName);
            return new { success = true, target = go != null ? go.name : path, property = propertyName, scale = new { x = readScale.x, y = readScale.y }, materialPath = ResolveFeedableMaterialPath(material) };
        }
        
        #endregion
        
        #region Keywords & Render State

        [UnitySkill("material_set_keyword", "Enable or disable a shader keyword (e.g., _EMISSION, _NORMALMAP, _METALLICGLOSSMAP)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "keyword", "shader", "rendering" },
            Outputs = new[] { "keyword", "enabled", "allKeywords", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            RequiredParams = new[] { "keyword" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetKeyword(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
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

            // Unlike ordinary property values, enabling a keyword isn't reliably recognized by Unity's own dirty flag as "changed" --
            // without an explicit SetDirty + SaveAssets it never gets written into the on-disk .mat's m_ValidKeywords --
            // this relies on the same convention that PrefabSetProperty's writes depend on. This doesn't branch on go == null:
            // addressing via the GameObject's renderer still resolves to that same shared .mat asset, not a scene-local copy.
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            return new {
                success = true,
                target = go != null ? go.name : path,
                keyword,
                enabled = material.IsKeywordEnabled(keyword),
                allKeywords = material.shaderKeywords,
                materialPath = ResolveFeedableMaterialPath(material)
            };
        }

        [UnitySkill("material_set_render_queue", "Set material render queue (-1 for shader default, 2000=Geometry, 2450=AlphaTest, 3000=Transparent)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "renderQueue", "sorting", "transparency" },
            Outputs = new[] { "renderQueue", "queueCategory", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetRenderQueue(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            int renderQueue = -1)
        {
            var (material, go, error) = FindMaterial(name, instanceId, path);
            if (error != null) return error;

            WorkflowManager.SnapshotObject(material);
            Undo.RecordObject(material, "Set Render Queue");
            material.renderQueue = renderQueue;

            // Same not-written-to-disk problem as material_set_keyword: without an explicit SetDirty + SaveAssets,
            // the on-disk m_CustomRenderQueue stays at -1, and the in-memory value can also get recalculated back to the shader default on the next reimport.
            // Likewise no branching: no matter how the caller addresses it, renderer.sharedMaterial is always the disk asset, never a per-object copy.
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            // -1 (or any other value the shader doesn't declare) resolves immediately: Unity's own
            // getter returns the shader's actual queue, not the sentinel just assigned.
            var actualQueue = material.renderQueue;
            string queueName = actualQueue switch
            {
                -1 => "ShaderDefault",
                < 2000 => "Background",
                < 2450 => "Geometry",
                < 2500 => "AlphaTest",
                < 3000 => "GeometryLast",
                < 4000 => "Transparent",
                _ => "Overlay"
            };

            var result = new Dictionary<string, object> {
                ["success"] = true,
                ["target"] = go != null ? go.name : path,
                ["renderQueue"] = actualQueue,
                ["queueCategory"] = queueName,
                ["materialPath"] = ResolveFeedableMaterialPath(material),
            };
            if (actualQueue != renderQueue)
                result["valueRequested"] = renderQueue;
            return result;
        }

        [UnitySkill("material_set_shader", "Change the shader of a material",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "shader", "material", "pipeline" },
            Outputs = new[] { "shader", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            RequiredParams = new[] { "shaderName" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetShader(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            [SkillParam("Full shader name, e.g. 'Universal Render Pipeline/Lit' or 'Standard'.")]
            string shaderName = null)
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
                shader = material.shader.name,
                materialPath = ResolveFeedableMaterialPath(material)
            };
        }

        [UnitySkill("material_set_gi_flags", "Set global illumination flags (None, RealtimeEmissive, BakedEmissive, EmissiveIsBlack)",
            Category = SkillCategory.Material, Operation = SkillOperation.Modify,
            Tags = new[] { "gi", "globalIllumination", "emission", "lighting" },
            Outputs = new[] { "giFlags", "materialPath" },
            RequiresInput = new[] { "gameObject|path" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MaterialSetGIFlags(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null,
            string flags = "RealtimeEmissive")
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
                giFlags = material.globalIlluminationFlags.ToString(),
                materialPath = ResolveFeedableMaterialPath(material)
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
        public static object MaterialGetProperties(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null)
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
        public static object MaterialGetKeywords(string name = null, int instanceId = 0,
            [SkillParam(MaterialPathNote)] string path = null)
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
