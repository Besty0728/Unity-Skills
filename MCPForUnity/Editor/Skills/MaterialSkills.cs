using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Material management skills - create, modify, assign.
    /// Now supports finding by name, instanceId, or path.
    /// </summary>
    public static class MaterialSkills
    {
        [UnitySkill("material_create", "Create a new material")]
        public static object MaterialCreate(string name, string shaderName = "Universal Render Pipeline/Lit", string savePath = null)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
                return new { error = $"Shader not found: {shaderName}" };

            var material = new Material(shader) { name = name };

            if (!string.IsNullOrEmpty(savePath))
            {
                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                AssetDatabase.CreateAsset(material, savePath);
                AssetDatabase.SaveAssets();
            }

            return new { success = true, name, shader = shaderName, path = savePath };
        }

        [UnitySkill("material_set_color", "Set a color property on a material (supports name/instanceId/path)")]
        public static object MaterialSetColor(string name = null, int instanceId = 0, string path = null, float r = 1, float g = 1, float b = 1, float a = 1, string propertyName = "_Color")
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = "No Renderer component found" };

            if (renderer.sharedMaterial == null)
                return new { error = "No material assigned to renderer" };

            var color = new Color(r, g, b, a);
            
            Undo.RecordObject(renderer.sharedMaterial, "Set Material Color");
            renderer.sharedMaterial.SetColor(propertyName, color);

            return new { success = true, gameObject = go.name, color = new { r, g, b, a } };
        }

        [UnitySkill("material_set_texture", "Set a texture on a material (supports name/instanceId/path)")]
        public static object MaterialSetTexture(string name = null, int instanceId = 0, string path = null, string texturePath = null, string propertyName = "_MainTex")
        {
            if (string.IsNullOrEmpty(texturePath))
                return new { error = "texturePath is required" };

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = "No Renderer component found" };

            if (renderer.sharedMaterial == null)
                return new { error = "No material assigned to renderer" };

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
                return new { error = $"Texture not found: {texturePath}" };

            Undo.RecordObject(renderer.sharedMaterial, "Set Texture");
            renderer.sharedMaterial.SetTexture(propertyName, texture);

            return new { success = true, gameObject = go.name, texture = texturePath };
        }

        [UnitySkill("material_assign", "Assign a material asset to a renderer (supports name/instanceId/path)")]
        public static object MaterialAssign(string name = null, int instanceId = 0, string path = null, string materialPath = null)
        {
            if (string.IsNullOrEmpty(materialPath))
                return new { error = "materialPath is required" };

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = "No Renderer component found" };

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                return new { error = $"Material not found: {materialPath}" };

            Undo.RecordObject(renderer, "Assign Material");
            renderer.sharedMaterial = material;

            return new { success = true, gameObject = go.name, material = materialPath };
        }

        [UnitySkill("material_set_float", "Set a float property on a material (supports name/instanceId/path)")]
        public static object MaterialSetFloat(string name = null, int instanceId = 0, string path = null, string propertyName = null, float value = 0)
        {
            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = "No Renderer component found" };

            if (renderer.sharedMaterial == null)
                return new { error = "No material assigned to renderer" };

            Undo.RecordObject(renderer.sharedMaterial, "Set Material Float");
            renderer.sharedMaterial.SetFloat(propertyName, value);

            return new { success = true, gameObject = go.name, property = propertyName, value };
        }
    }
}
