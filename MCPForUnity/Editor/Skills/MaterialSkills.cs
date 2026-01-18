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
            Material material = null;
            GameObject go = null;

            // Check if finding by Asset Path
            if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/") || path.EndsWith(".mat")))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) return new { error = $"Material asset not found: {path}" };
            }
            else
            {
                var result = GameObjectFinder.FindOrError(name, instanceId, path);
                if (result.error != null) return result.error;
                go = result.go;

                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return new { error = "No Renderer component found" };
                if (renderer.sharedMaterial == null) return new { error = "No material assigned to renderer" };
                
                material = renderer.sharedMaterial;
            }

            var color = new Color(r, g, b, a);
            
            Undo.RecordObject(material, "Set Material Color");
            material.SetColor(propertyName, color);
            
            // If modified asset directly, strict save is sometimes safer but RecordObject usually enough for dirty
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, color = new { r, g, b, a } };
        }

        [UnitySkill("material_set_texture", "Set a texture on a material (supports name/instanceId/path)")]
        public static object MaterialSetTexture(string name = null, int instanceId = 0, string path = null, string texturePath = null, string propertyName = "_MainTex")
        {
            if (string.IsNullOrEmpty(texturePath))
                return new { error = "texturePath is required" };

            Material material = null;
            GameObject go = null;

            // Check if finding by Asset Path
            if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/") || path.EndsWith(".mat")))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) return new { error = $"Material asset not found: {path}" };
            }
            else
            {
                var result = GameObjectFinder.FindOrError(name, instanceId, path);
                if (result.error != null) return result.error;
                go = result.go;

                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return new { error = "No Renderer component found" };
                if (renderer.sharedMaterial == null) return new { error = "No material assigned to renderer" };
                
                material = renderer.sharedMaterial;
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
                return new { error = $"Texture not found: {texturePath}" };

            Undo.RecordObject(material, "Set Texture");
            material.SetTexture(propertyName, texture);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, texture = texturePath };
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

            Material material = null;
            GameObject go = null;

            // Check if finding by Asset Path
            if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/") || path.EndsWith(".mat")))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) return new { error = $"Material asset not found: {path}" };
            }
            else
            {
                var result = GameObjectFinder.FindOrError(name, instanceId, path);
                if (result.error != null) return result.error;
                go = result.go;

                var renderer = go.GetComponent<Renderer>();
                if (renderer == null) return new { error = "No Renderer component found" };
                if (renderer.sharedMaterial == null) return new { error = "No material assigned to renderer" };
                
                material = renderer.sharedMaterial;
            }

            Undo.RecordObject(material, "Set Material Float");
            material.SetFloat(propertyName, value);
            
            if (go == null) EditorUtility.SetDirty(material);

            return new { success = true, target = go != null ? go.name : path, property = propertyName, value };
        }
    }
}
