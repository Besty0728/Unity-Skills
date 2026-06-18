using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Text;

namespace UnitySkills
{
    /// <summary>
    /// Shader management skills.
    /// </summary>
    public static class ShaderSkills
    {

        [UnitySkill("shader_create", "Create a new shader file",
            Category = SkillCategory.Shader, Operation = SkillOperation.Create,
            Tags = new[] { "shader", "create", "hlsl", "asset" },
            Outputs = new[] { "shaderName", "path" },
            TracksWorkflow = true)]
        public static object ShaderCreate(string shaderName, string savePath, string template = null)
        {
            if (Validate.Required(shaderName, "shaderName") is object err) return err;
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            if (!string.IsNullOrEmpty(savePath) && File.Exists(savePath))
                return new { error = $"File already exists: {savePath}" };

            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var content = template ?? $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _MainTex (""Texture"", 2D) = ""white"" {{}}
        _Color (""Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 100

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata
            {{
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            }};

            struct v2f
            {{
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            }};

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }}

            fixed4 frag (v2f i) : SV_Target
            {{
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                return col;
            }}
            ENDCG
        }}
            }}
}}
";
            File.WriteAllText(savePath, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);

            return new { success = true, shaderName, path = savePath };
        }

        [UnitySkill("shader_read", "Read shader source code",
            Category = SkillCategory.Shader, Operation = SkillOperation.Query,
            Tags = new[] { "shader", "read", "source", "code" },
            Outputs = new[] { "path", "lines", "content" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderRead(string shaderPath)
        {
            if (Validate.SafePath(shaderPath, "shaderPath") is object pathErr) return pathErr;
            if (!File.Exists(shaderPath))
                return new { error = $"Shader not found: {shaderPath}" };

            var content = File.ReadAllText(shaderPath, System.Text.Encoding.UTF8);
            var lines = content.Split('\n').Length;

            return new { path = shaderPath, lines, content };
        }

        [UnitySkill("shader_list", "List all shaders in project",
            Category = SkillCategory.Shader, Operation = SkillOperation.Query,
            Tags = new[] { "shader", "list", "search", "asset" },
            Outputs = new[] { "count", "shaders" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderList(string filter = null, int limit = 100)
        {
            var guids = AssetDatabase.FindAssets("t:Shader");
            var shaders = guids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => string.IsNullOrEmpty(filter) || p.Contains(filter))
                .Take(limit)
                .Select(p =>
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(p);
                    return new
                    {
                        path = p,
                        name = shader?.name,
                        propertyCount = shader != null ? shader.GetPropertyCount() : 0
                    };
                })
                .ToArray();

            return new { count = shaders.Length, shaders };
        }

        [UnitySkill("shader_get_properties", "Get properties of a shader",
            Category = SkillCategory.Shader, Operation = SkillOperation.Query,
            Tags = new[] { "shader", "property", "inspect" },
            Outputs = new[] { "shaderName", "propertyCount", "properties" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGetProperties(string shaderNameOrPath)
        {
            var shader = FindShaderByNameOrPath(shaderNameOrPath);
            if (shader == null)
                return new { error = $"Shader not found: {shaderNameOrPath}" };

            var propCount = shader.GetPropertyCount();
            var properties = Enumerable.Range(0, propCount)
                .Select(i => new
                {
                    name = shader.GetPropertyName(i),
                    type = shader.GetPropertyType(i).ToString(),
                    description = shader.GetPropertyDescription(i)
                })
                .ToArray();

            return new
            {
                shaderName = shader.name,
                propertyCount = propCount,
                properties
            };
        }

        [UnitySkill("shader_find", "Find shaders by name",
            Category = SkillCategory.Shader, Operation = SkillOperation.Query,
            Tags = new[] { "shader", "find", "search" },
            Outputs = new[] { "found", "name", "path" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderFind(string searchName)
        {
            var shader = Shader.Find(searchName);
            if (shader == null)
                return new { error = $"Shader not found: {searchName}" };

            var path = AssetDatabase.GetAssetPath(shader);
            return new
            {
                found = true,
                name = shader.name,
                path = string.IsNullOrEmpty(path) ? "(built-in)" : path
            };
        }

        [UnitySkill("shader_delete", "Delete a shader file",
            Category = SkillCategory.Shader, Operation = SkillOperation.Delete,
            Tags = new[] { "shader", "delete", "asset" },
            Outputs = new[] { "deleted" },
            RequiresInput = new[] { "assetPath" },
            TracksWorkflow = true)]
        public static object ShaderDelete(string shaderPath)
        {
            if (Validate.SafePath(shaderPath, "shaderPath", isDelete: true) is object pathErr) return pathErr;
            if (!File.Exists(shaderPath))
                return new { error = $"Shader not found: {shaderPath}" };

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(shaderPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            AssetDatabase.DeleteAsset(shaderPath);
            return new { success = true, deleted = shaderPath };
        }

        [UnitySkill("shader_check_errors", "Check shader for compilation errors",
            Category = SkillCategory.Shader, Operation = SkillOperation.Analyze,
            Tags = new[] { "shader", "error", "compile", "diagnostic" },
            Outputs = new[] { "shaderName", "hasErrors", "messageCount" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderCheckErrors(string shaderNameOrPath)
        {
            var shader = FindShaderByNameOrPath(shaderNameOrPath);
            if (shader == null) return new { error = $"Shader not found: {shaderNameOrPath}" };
            int msgCount = ShaderUtil.GetShaderMessageCount(shader);
            return new { shaderName = shader.name, hasErrors = msgCount > 0, messageCount = msgCount };
        }

        [UnitySkill("shader_get_keywords", "Get shader keyword list",
            Category = SkillCategory.Shader, Operation = SkillOperation.Query,
            Tags = new[] { "shader", "keyword", "inspect" },
            Outputs = new[] { "shaderName", "keywordCount", "keywords" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGetKeywords(string shaderNameOrPath)
        {
            var shader = FindShaderByNameOrPath(shaderNameOrPath);
            if (shader == null) return new { error = $"Shader not found: {shaderNameOrPath}" };
            var keywords = shader.keywordSpace.keywords.Select(k => new { name = k.name, type = k.type.ToString() }).ToArray();
            return new { shaderName = shader.name, keywordCount = keywords.Length, keywords };
        }

        [UnitySkill("shader_get_variant_count", "Get shader variant count for performance analysis",
            Category = SkillCategory.Shader, Operation = SkillOperation.Analyze,
            Tags = new[] { "shader", "variant", "performance", "optimization" },
            Outputs = new[] { "shaderName", "subshaderCount", "totalPasses" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ShaderGetVariantCount(string shaderNameOrPath)
        {
            var shader = FindShaderByNameOrPath(shaderNameOrPath);
            if (shader == null) return new { error = $"Shader not found: {shaderNameOrPath}" };
            var data = ShaderUtil.GetShaderData(shader);
            int totalVariants = 0;
            int subshaderCount = data.SubshaderCount;
            for (int s = 0; s < subshaderCount; s++)
            {
                var sub = data.GetSubshader(s);
                totalVariants += sub.PassCount;
            }
            return new { shaderName = shader.name, subshaderCount, totalPasses = totalVariants };
        }

        [UnitySkill("shader_create_urp", "Create a URP shader from template (type: Unlit or Lit)",
            Category = SkillCategory.Shader, Operation = SkillOperation.Create,
            Tags = new[] { "shader", "urp", "create", "template" },
            Outputs = new[] { "shaderName", "path", "type" },
            TracksWorkflow = true)]
        public static object ShaderCreateUrp(string shaderName, string savePath, string type = "Unlit")
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr2) return pathErr2;
            if (File.Exists(savePath)) return new { error = $"File already exists: {savePath}" };
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string content = type.ToLower() == "lit"
                ? $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _BaseMap (""Base Map"", 2D) = ""white"" {{}}
        _BaseColor (""Base Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" ""RenderPipeline""=""UniversalPipeline"" }}
        Pass
        {{
            Name ""ForwardLit""
            Tags {{ ""LightMode""=""UniversalForward"" }}
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl""
            struct Attributes {{ float4 positionOS : POSITION; float2 uv : TEXCOORD0; float3 normalOS : NORMAL; }};
            struct Varyings {{ float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; }};
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial) float4 _BaseMap_ST; half4 _BaseColor; CBUFFER_END
            Varyings vert(Attributes IN) {{ Varyings OUT; OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz); OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap); OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS); return OUT; }}
            half4 frag(Varyings IN) : SV_Target {{ half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor; return col; }}
            ENDHLSL
        }}
    }}
}}"
                : $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _BaseMap (""Base Map"", 2D) = ""white"" {{}}
        _BaseColor (""Base Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" ""RenderPipeline""=""UniversalPipeline"" }}
        Pass
        {{
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""
            struct Attributes {{ float4 positionOS : POSITION; float2 uv : TEXCOORD0; }};
            struct Varyings {{ float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; }};
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial) float4 _BaseMap_ST; half4 _BaseColor; CBUFFER_END
            Varyings vert(Attributes IN) {{ Varyings OUT; OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz); OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap); return OUT; }}
            half4 frag(Varyings IN) : SV_Target {{ return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor; }}
            ENDHLSL
        }}
    }}
}}";
            File.WriteAllText(savePath, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(savePath);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(savePath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            return new { success = true, shaderName, path = savePath, type };
        }

        /// <summary>
        /// Find a shader by name or asset path. Tries asset path first if it ends with .shader, then falls back to Shader.Find.
        /// </summary>
        private static Shader FindShaderByNameOrPath(string shaderNameOrPath)
        {
            if (string.IsNullOrEmpty(shaderNameOrPath)) return null;
            Shader shader = null;
            if (shaderNameOrPath.EndsWith(".shader"))
                shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderNameOrPath);
            if (shader == null)
                shader = Shader.Find(shaderNameOrPath);
            return shader;
        }

        [UnitySkill("shader_set_global_keyword", "Enable or disable a global shader keyword",
            Category = SkillCategory.Shader, Operation = SkillOperation.Modify,
            Tags = new[] { "shader", "keyword", "global", "rendering" },
            Outputs = new[] { "keyword", "enabled" },
            TracksWorkflow = true)]
        public static object ShaderSetGlobalKeyword(string keyword, bool enabled)
        {
            if (enabled) Shader.EnableKeyword(keyword);
            else Shader.DisableKeyword(keyword);
            return new { success = true, keyword, enabled };
        }
    }
}
