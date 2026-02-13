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
            WorkflowManager.SnapshotObject(terrainGO, SnapshotType.Created);
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
            WorkflowManager.SnapshotObject(data);
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
            WorkflowManager.SnapshotObject(data);
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

        [UnitySkill("terrain_add_hill", "Add a smooth hill to the terrain at normalized position with radius and height")]
        public static object TerrainAddHill(
            float normalizedX, float normalizedZ,
            float radius = 0.2f,
            float height = 0.5f,
            float smoothness = 1f,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            WorkflowManager.SnapshotObject(data);
            Undo.RegisterCompleteObjectUndo(data, "Add Hill to Terrain");

            int resolution = data.heightmapResolution;
            int centerX = Mathf.RoundToInt(normalizedX * (resolution - 1));
            int centerZ = Mathf.RoundToInt(normalizedZ * (resolution - 1));
            int radiusPixels = Mathf.Max(1, Mathf.RoundToInt(radius * resolution));

            // Calculate affected area
            int startX = Mathf.Max(0, centerX - radiusPixels);
            int startZ = Mathf.Max(0, centerZ - radiusPixels);
            int endX = Mathf.Min(resolution - 1, centerX + radiusPixels);
            int endZ = Mathf.Min(resolution - 1, centerZ + radiusPixels);

            int width = endX - startX + 1;
            int length = endZ - startZ + 1;

            // Get current heights
            float[,] heights = data.GetHeights(startX, startZ, width, length);

            // Add hill with smooth falloff
            for (int z = 0; z < length; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int worldX = startX + x;
                    int worldZ = startZ + z;

                    // Calculate distance from center
                    float dx = (worldX - centerX) / (float)radiusPixels;
                    float dz = (worldZ - centerZ) / (float)radiusPixels;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);

                    if (distance <= 1f)
                    {
                        // Smooth falloff using cosine interpolation
                        float falloff = Mathf.Pow(Mathf.Cos(distance * Mathf.PI * 0.5f), smoothness);
                        float addHeight = height * falloff;
                        heights[z, x] = Mathf.Clamp01(heights[z, x] + addHeight);
                    }
                }
            }

            data.SetHeights(startX, startZ, heights);

            return new
            {
                success = true,
                centerX = normalizedX,
                centerZ = normalizedZ,
                radius,
                height,
                affectedArea = new { startX, startZ, width, length }
            };
        }

        [UnitySkill("terrain_generate_perlin", "Generate terrain using Perlin noise for natural-looking landscapes")]
        public static object TerrainGeneratePerlin(
            float scale = 20f,
            float heightMultiplier = 0.3f,
            int octaves = 4,
            float persistence = 0.5f,
            float lacunarity = 2f,
            int seed = 0,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            WorkflowManager.SnapshotObject(data);
            Undo.RegisterCompleteObjectUndo(data, "Generate Perlin Terrain");

            int resolution = data.heightmapResolution;
            float[,] heights = new float[resolution, resolution];

            // Use seed for reproducible results
            System.Random random = seed != 0 ? new System.Random(seed) : new System.Random();
            float offsetX = random.Next(-10000, 10000);
            float offsetZ = random.Next(-10000, 10000);

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float amplitude = 1f;
                    float frequency = 1f;
                    float noiseHeight = 0f;

                    // Generate multiple octaves of Perlin noise
                    for (int i = 0; i < octaves; i++)
                    {
                        float sampleX = (x / (float)resolution * scale + offsetX) * frequency;
                        float sampleZ = (z / (float)resolution * scale + offsetZ) * frequency;

                        float perlinValue = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f;
                        noiseHeight += perlinValue * amplitude;

                        amplitude *= persistence;
                        frequency *= lacunarity;
                    }

                    heights[z, x] = Mathf.Clamp01(noiseHeight * heightMultiplier + 0.5f);
                }
            }

            data.SetHeights(0, 0, heights);

            return new
            {
                success = true,
                resolution,
                scale,
                heightMultiplier,
                octaves,
                persistence,
                lacunarity,
                seed = seed != 0 ? seed : (int?)null
            };
        }

        [UnitySkill("terrain_smooth", "Smooth terrain heights in a region to reduce sharp edges")]
        public static object TerrainSmooth(
            float normalizedX, float normalizedZ,
            float radius = 0.1f,
            int iterations = 1,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            WorkflowManager.SnapshotObject(data);
            Undo.RegisterCompleteObjectUndo(data, "Smooth Terrain");

            int resolution = data.heightmapResolution;
            int centerX = Mathf.RoundToInt(normalizedX * (resolution - 1));
            int centerZ = Mathf.RoundToInt(normalizedZ * (resolution - 1));
            int radiusPixels = Mathf.RoundToInt(radius * resolution);

            int startX = Mathf.Max(1, centerX - radiusPixels);
            int startZ = Mathf.Max(1, centerZ - radiusPixels);
            int endX = Mathf.Min(resolution - 2, centerX + radiusPixels);
            int endZ = Mathf.Min(resolution - 2, centerZ + radiusPixels);

            int width = endX - startX + 1;
            int length = endZ - startZ + 1;

            for (int iter = 0; iter < iterations; iter++)
            {
                float[,] heights = data.GetHeights(startX - 1, startZ - 1, width + 2, length + 2);
                float[,] smoothed = new float[length, width];

                for (int z = 0; z < length; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        // Average with 8 neighbors
                        float sum = 0f;
                        for (int dz = 0; dz <= 2; dz++)
                        {
                            for (int dx = 0; dx <= 2; dx++)
                            {
                                sum += heights[z + dz, x + dx];
                            }
                        }
                        smoothed[z, x] = sum / 9f;
                    }
                }

                data.SetHeights(startX, startZ, smoothed);
            }

            return new
            {
                success = true,
                centerX = normalizedX,
                centerZ = normalizedZ,
                radius,
                iterations,
                affectedArea = new { startX, startZ, width, length }
            };
        }

        [UnitySkill("terrain_flatten", "Flatten terrain to a specific height in a region")]
        public static object TerrainFlatten(
            float normalizedX, float normalizedZ,
            float targetHeight = 0.5f,
            float radius = 0.1f,
            float strength = 1f,
            string name = null, int instanceId = 0)
        {
            var terrain = FindTerrain(name, instanceId);
            if (terrain == null)
                return new { success = false, error = "Terrain not found" };

            var data = terrain.terrainData;
            WorkflowManager.SnapshotObject(data);
            Undo.RegisterCompleteObjectUndo(data, "Flatten Terrain");

            int resolution = data.heightmapResolution;
            int centerX = Mathf.RoundToInt(normalizedX * (resolution - 1));
            int centerZ = Mathf.RoundToInt(normalizedZ * (resolution - 1));
            int radiusPixels = Mathf.RoundToInt(radius * resolution);

            int startX = Mathf.Max(0, centerX - radiusPixels);
            int startZ = Mathf.Max(0, centerZ - radiusPixels);
            int endX = Mathf.Min(resolution - 1, centerX + radiusPixels);
            int endZ = Mathf.Min(resolution - 1, centerZ + radiusPixels);

            int width = endX - startX + 1;
            int length = endZ - startZ + 1;

            float[,] heights = data.GetHeights(startX, startZ, width, length);

            for (int z = 0; z < length; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int worldX = startX + x;
                    int worldZ = startZ + z;

                    float dx = (worldX - centerX) / (float)radiusPixels;
                    float dz = (worldZ - centerZ) / (float)radiusPixels;
                    float distance = Mathf.Sqrt(dx * dx + dz * dz);

                    if (distance <= 1f)
                    {
                        float falloff = Mathf.Cos(distance * Mathf.PI * 0.5f);
                        heights[z, x] = Mathf.Lerp(heights[z, x], targetHeight, strength * falloff);
                    }
                }
            }

            data.SetHeights(startX, startZ, heights);

            return new
            {
                success = true,
                centerX = normalizedX,
                centerZ = normalizedZ,
                targetHeight,
                radius,
                strength
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
