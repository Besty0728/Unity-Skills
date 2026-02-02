using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Terrain skills - create, modify, and query terrain data.
    /// </summary>
    public static class TerrainSkills
    {
        [UnitySkill("terrain_create", "Create a new Terrain with TerrainData asset")]
        public static object TerrainCreate(
            string name = "Terrain",
            int width = 500,
            int length = 500,
            int height = 100,
            int heightmapResolution = 513,
            float x = 0, float y = 0, float z = 0)
        {
            // Create TerrainData asset
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = heightmapResolution;
            terrainData.size = new Vector3(width, height, length);

            // Save TerrainData as asset
            var assetPath = $"Assets/{name}_Data.asset";
            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            AssetDatabase.CreateAsset(terrainData, assetPath);

            // Create Terrain GameObject
            var terrainGO = Terrain.CreateTerrainGameObject(terrainData);
            terrainGO.name = name;
            terrainGO.transform.position = new Vector3(x, y, z);

            Undo.RegisterCreatedObjectUndo(terrainGO, "Create Terrain");
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                name = terrainGO.name,
                instanceId = terrainGO.GetInstanceID(),
                terrainDataPath = assetPath,
                size = new { width, length, height },
                position = new { x, y, z }
            };
        }

        [UnitySkill("terrain_get_info", "Get terrain information including size, resolution, and layers")]
        public static object TerrainGetInfo(string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            var layers = new List<object>();
            
            if (data.terrainLayers != null)
            {
                foreach (var layer in data.terrainLayers)
                {
                    if (layer != null)
                    {
                        layers.Add(new
                        {
                            name = layer.name,
                            diffuseTexture = layer.diffuseTexture?.name,
                            tileSize = new { x = layer.tileSize.x, y = layer.tileSize.y }
                        });
                    }
                }
            }

            return new
            {
                success = true,
                name = terrain.name,
                instanceId = terrain.gameObject.GetInstanceID(),
                position = new { x = terrain.transform.position.x, y = terrain.transform.position.y, z = terrain.transform.position.z },
                size = new { width = data.size.x, height = data.size.y, length = data.size.z },
                heightmapResolution = data.heightmapResolution,
                alphamapResolution = data.alphamapResolution,
                detailResolution = data.detailResolution,
                terrainLayerCount = data.terrainLayers?.Length ?? 0,
                layers
            };
        }

        [UnitySkill("terrain_get_height", "Get terrain height at world position")]
        public static object TerrainGetHeight(float worldX, float worldZ, string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var height = terrain.SampleHeight(new Vector3(worldX, 0, worldZ));
            
            return new
            {
                success = true,
                worldX,
                worldZ,
                height,
                worldY = height + terrain.transform.position.y
            };
        }

        [UnitySkill("terrain_set_height", "Set terrain height at normalized coordinates (0-1)")]
        public static object TerrainSetHeight(
            float normalizedX, float normalizedZ, float height,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            Undo.RegisterCompleteObjectUndo(data, "Set Terrain Height");

            int resolution = data.heightmapResolution;
            int x = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (resolution - 1)), 0, resolution - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt(normalizedZ * (resolution - 1)), 0, resolution - 1);

            float[,] heights = data.GetHeights(x, z, 1, 1);
            heights[0, 0] = Mathf.Clamp01(height);
            data.SetHeights(x, z, heights);

            return new
            {
                success = true,
                normalizedX,
                normalizedZ,
                height = Mathf.Clamp01(height),
                pixelX = x,
                pixelZ = z
            };
        }

        [UnitySkill("terrain_set_heights_batch", "Set terrain heights in a rectangular region. Heights is a 2D array [z][x] with values 0-1.")]
        public static object TerrainSetHeightsBatch(
            int startX, int startZ,
            float[][] heights,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            if (heights == null || heights.Length == 0)
                return new { success = false, error = "Heights array is empty" };

            var data = terrain.terrainData;
            Undo.RegisterCompleteObjectUndo(data, "Set Terrain Heights Batch");

            int zSize = heights.Length;
            int xSize = heights[0].Length;
            int resolution = data.heightmapResolution;

            // Clamp start positions
            startX = Mathf.Clamp(startX, 0, resolution - 1);
            startZ = Mathf.Clamp(startZ, 0, resolution - 1);

            // Clamp sizes to fit within terrain
            xSize = Mathf.Min(xSize, resolution - startX);
            zSize = Mathf.Min(zSize, resolution - startZ);

            float[,] heightData = new float[zSize, xSize];
            for (int z = 0; z < zSize; z++)
            {
                for (int x = 0; x < xSize; x++)
                {
                    if (x < heights[z].Length)
                        heightData[z, x] = Mathf.Clamp01(heights[z][x]);
                }
            }

            data.SetHeights(startX, startZ, heightData);

            return new
            {
                success = true,
                startX,
                startZ,
                modifiedWidth = xSize,
                modifiedLength = zSize,
                totalPointsModified = xSize * zSize
            };
        }

        [UnitySkill("terrain_paint_texture", "Paint terrain texture layer at normalized position. Requires terrain layers to be set up.")]
        public static object TerrainPaintTexture(
            float normalizedX, float normalizedZ,
            int layerIndex,
            float strength = 1f,
            int brushSize = 10,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            
            if (data.terrainLayers == null || layerIndex >= data.terrainLayers.Length)
                return new { success = false, error = $"Layer index {layerIndex} out of range. Terrain has {data.terrainLayers?.Length ?? 0} layers." };

            Undo.RegisterCompleteObjectUndo(data, "Paint Terrain Texture");

            int alphamapRes = data.alphamapResolution;
            int centerX = Mathf.RoundToInt(normalizedX * (alphamapRes - 1));
            int centerZ = Mathf.RoundToInt(normalizedZ * (alphamapRes - 1));

            int halfBrush = brushSize / 2;
            int startX = Mathf.Clamp(centerX - halfBrush, 0, alphamapRes - 1);
            int startZ = Mathf.Clamp(centerZ - halfBrush, 0, alphamapRes - 1);
            int endX = Mathf.Clamp(centerX + halfBrush, 0, alphamapRes - 1);
            int endZ = Mathf.Clamp(centerZ + halfBrush, 0, alphamapRes - 1);

            int width = endX - startX + 1;
            int height = endZ - startZ + 1;

            float[,,] alphamaps = data.GetAlphamaps(startX, startZ, width, height);
            int layerCount = alphamaps.GetLength(2);

            for (int z = 0; z < height; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Apply brush with falloff
                    float dist = Vector2.Distance(new Vector2(x, z), new Vector2(width / 2f, height / 2f));
                    float falloff = Mathf.Clamp01(1f - dist / halfBrush);
                    float paintStrength = strength * falloff;

                    // Reduce other layers and increase target layer
                    for (int l = 0; l < layerCount; l++)
                    {
                        if (l == layerIndex)
                            alphamaps[z, x, l] = Mathf.Lerp(alphamaps[z, x, l], 1f, paintStrength);
                        else
                            alphamaps[z, x, l] = Mathf.Lerp(alphamaps[z, x, l], 0f, paintStrength);
                    }

                    // Normalize
                    float sum = 0;
                    for (int l = 0; l < layerCount; l++) sum += alphamaps[z, x, l];
                    if (sum > 0)
                    {
                        for (int l = 0; l < layerCount; l++) alphamaps[z, x, l] /= sum;
                    }
                }
            }

            data.SetAlphamaps(startX, startZ, alphamaps);

            return new
            {
                success = true,
                layerIndex,
                layerName = data.terrainLayers[layerIndex]?.name,
                centerX,
                centerZ,
                brushSize,
                strength
            };
        }

        private static Terrain FindTerrain(string name, int instanceId)
        {
            if (instanceId != 0)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                return obj?.GetComponent<Terrain>();
            }

            if (!string.IsNullOrEmpty(name))
            {
                var go = GameObject.Find(name);
                return go?.GetComponent<Terrain>();
            }

            // Return first terrain in scene
            return Object.FindObjectOfType<Terrain>();
        }
    }
}
