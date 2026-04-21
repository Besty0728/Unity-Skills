using UnityEngine;
using UnityEditor;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Light management skills - create, configure, query lights.
    /// </summary>
    public static class LightSkills
    {
        [UnitySkill("light_create", "Create a new light (Directional, Point, Spot, Area)",
            Category = SkillCategory.Light, Operation = SkillOperation.Create,
            Tags = new[] { "light", "create", "illumination", "scene" },
            Outputs = new[] { "name", "instanceId", "lightType", "intensity", "shadows" },
            TracksWorkflow = true)]
        public static object LightCreate(
            string name = "New Light",
            string lightType = "Point",
            float x = 0, float y = 3, float z = 0,
            float r = 1, float g = 1, float b = 1,
            float intensity = 1,
            float range = 10,
            float spotAngle = 30,
            string shadows = "soft")
        {
            var go = new GameObject(name);
            var light = go.AddComponent<Light>();

            // Set light type
            if (System.Enum.TryParse<LightType>(lightType, true, out var lt))
                light.type = lt;
            else
            {
                Object.DestroyImmediate(go);
                return new { error = $"Unknown light type: {lightType}. Use: Directional, Point, Spot, Area" };
            }

            // Set position
            go.transform.position = new Vector3(x, y, z);

            // Set color
            light.color = new Color(r, g, b);
            light.intensity = intensity;

            // Type-specific settings
            if (lt == LightType.Point || lt == LightType.Spot)
                light.range = range;

            if (lt == LightType.Spot)
                light.spotAngle = spotAngle;

            // Set shadows
            switch (shadows.ToLower())
            {
                case "hard":
                    light.shadows = LightShadows.Hard;
                    break;
                case "soft":
                    light.shadows = LightShadows.Soft;
                    break;
                default:
                    light.shadows = LightShadows.None;
                    break;
            }

            Undo.RegisterCreatedObjectUndo(go, "Create Light");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                lightType = light.type.ToString(),
                position = new { x, y, z },
                color = new { r, g, b },
                intensity,
                shadows = light.shadows.ToString()
            };
        }

        [UnitySkill("light_set_properties", "Set light properties (supports name/instanceId/path)",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "color", "intensity", "shadow" },
            Outputs = new[] { "lightType", "color", "intensity", "range", "shadows" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightSetProperties(
            string name = null, int instanceId = 0, string path = null,
            float? r = null, float? g = null, float? b = null,
            float? intensity = null,
            float? range = null,
            float? spotAngle = null,
            string shadows = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var light = go.GetComponent<Light>();
            if (light == null)
                return new { error = $"No Light component on {go.name}" };

            WorkflowManager.SnapshotObject(light);
            Undo.RecordObject(light, "Set Light Properties");

            // Update color if any color component provided
            if (r.HasValue || g.HasValue || b.HasValue)
            {
                var currentColor = light.color;
                light.color = new Color(
                    r ?? currentColor.r,
                    g ?? currentColor.g,
                    b ?? currentColor.b
                );
            }

            if (intensity.HasValue)
                light.intensity = intensity.Value;

            if (range.HasValue && (light.type == LightType.Point || light.type == LightType.Spot))
                light.range = range.Value;

            if (spotAngle.HasValue && light.type == LightType.Spot)
                light.spotAngle = spotAngle.Value;

            if (!string.IsNullOrEmpty(shadows))
            {
                switch (shadows.ToLower())
                {
                    case "hard":
                        light.shadows = LightShadows.Hard;
                        break;
                    case "soft":
                        light.shadows = LightShadows.Soft;
                        break;
                    case "none":
                        light.shadows = LightShadows.None;
                        break;
                    default:
                        return new { warning = $"Unknown shadow type: '{shadows}'. Valid values: hard, soft, none" };
                }
            }

            return new
            {
                success = true,
                name = go.name,
                lightType = light.type.ToString(),
                color = new { r = light.color.r, g = light.color.g, b = light.color.b },
                intensity = light.intensity,
                range = light.range,
                spotAngle = light.spotAngle,
                shadows = light.shadows.ToString()
            };
        }

        [UnitySkill("light_get_info", "Get information about a light (supports name/instanceId/path)",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "light", "info", "inspect" },
            Outputs = new[] { "lightType", "color", "intensity", "range", "shadows", "enabled" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true)]
        public static object LightGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var light = go.GetComponent<Light>();
            if (light == null)
                return new { error = $"No Light component on {go.name}" };

            return new
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                path = GameObjectFinder.GetPath(go),
                lightType = light.type.ToString(),
                color = new { r = light.color.r, g = light.color.g, b = light.color.b },
                intensity = light.intensity,
                range = light.range,
                spotAngle = light.spotAngle,
                shadows = light.shadows.ToString(),
                enabled = light.enabled,
                cullingMask = light.cullingMask,
                bounceIntensity = light.bounceIntensity
            };
        }

        [UnitySkill("light_find_all", "Find all lights in the scene",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "light", "find", "search", "scene" },
            Outputs = new[] { "count", "lights" },
            ReadOnly = true)]
        public static object LightFindAll(string lightType = null, int limit = 50)
        {
            var lights = FindHelper.FindAll<Light>();

            if (!string.IsNullOrEmpty(lightType))
            {
                if (System.Enum.TryParse<LightType>(lightType, true, out var lt))
                    lights = lights.Where(l => l.type == lt).ToArray();
            }

            var results = lights.Take(limit).Select(l => new
            {
                name = l.gameObject.name,
                instanceId = l.gameObject.GetInstanceID(),
                path = GameObjectFinder.GetPath(l.gameObject),
                lightType = l.type.ToString(),
                intensity = l.intensity,
                enabled = l.enabled
            }).ToArray();

            return new { count = results.Length, lights = results };
        }

        [UnitySkill("light_set_enabled", "Enable or disable a light (supports name/instanceId/path). Returns: {success, name, enabled}",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "enable", "disable", "toggle" },
            Outputs = new[] { "name", "enabled" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightSetEnabled(string name = null, int instanceId = 0, string path = null, bool enabled = true)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var light = go.GetComponent<Light>();
            if (light == null)
                return new { error = $"No Light component on {go.name}" };

            WorkflowManager.SnapshotObject(light);
            Undo.RecordObject(light, "Set Light Enabled");
            light.enabled = enabled;

            return new { success = true, name = go.name, enabled };
        }

        [UnitySkill("light_set_enabled_batch", "Enable/disable multiple lights in one call (Efficient). items: JSON array of {name, instanceId, path, enabled}",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "enable", "batch", "toggle" },
            Outputs = new[] { "totalCount", "successCount", "results" },
            RequiresInput = new[] { "gameObject" })]
        public static object LightSetEnabledBatch(string items)
        {
            return BatchExecutor.Execute<BatchLightEnabledItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                var light = go.GetComponent<Light>();
                if (light == null) throw new System.Exception("No Light component");

                WorkflowManager.SnapshotObject(light);
                Undo.RecordObject(light, "Batch Set Light Enabled");
                light.enabled = item.enabled;
                return new { target = go.name, success = true, enabled = item.enabled };
            }, item => item.name ?? item.path ?? item.instanceId.ToString());
        }

        private class BatchLightEnabledItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public bool enabled { get; set; }
        }

        [UnitySkill("light_set_properties_batch", "Set properties for multiple lights in one call (Efficient). items: JSON array of {name, instanceId, r, g, b, intensity, range, shadows}",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "batch", "properties", "color" },
            Outputs = new[] { "totalCount", "successCount", "results" },
            RequiresInput = new[] { "gameObject" })]
        public static object LightSetPropertiesBatch(string items)
        {
            return BatchExecutor.Execute<BatchLightPropsItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) throw new System.Exception("Object not found");

                var light = go.GetComponent<Light>();
                if (light == null) throw new System.Exception("No Light component");

                WorkflowManager.SnapshotObject(light);
                Undo.RecordObject(light, "Batch Set Light Properties");

                if (item.r.HasValue || item.g.HasValue || item.b.HasValue)
                {
                    var c = light.color;
                    light.color = new Color(item.r ?? c.r, item.g ?? c.g, item.b ?? c.b);
                }
                if (item.intensity.HasValue) light.intensity = item.intensity.Value;
                if (item.range.HasValue && (light.type == LightType.Point || light.type == LightType.Spot))
                    light.range = item.range.Value;
                if (!string.IsNullOrEmpty(item.shadows))
                {
                    switch (item.shadows.ToLower())
                    {
                        case "hard": light.shadows = LightShadows.Hard; break;
                        case "soft": light.shadows = LightShadows.Soft; break;
                        case "none": light.shadows = LightShadows.None; break;
                        default: throw new System.Exception($"Unknown shadow type: '{item.shadows}'. Valid values: hard, soft, none");
                    }
                }

                return new { target = go.name, success = true };
            }, item => item.name ?? item.path ?? item.instanceId.ToString());
        }

        private class BatchLightPropsItem
        {
            public string name { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public float? r { get; set; }
            public float? g { get; set; }
            public float? b { get; set; }
            public float? intensity { get; set; }
            public float? range { get; set; }
            public string shadows { get; set; }
        }

        [UnitySkill("light_add_probe_group", "Add a Light Probe Group to a GameObject. Optional grid layout: gridX/gridY/gridZ (count per axis), spacingX/spacingY/spacingZ (meters between probes)",
            Category = SkillCategory.Light, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "lightProbe", "gi", "globalIllumination", "grid" },
            Outputs = new[] { "gameObject", "probeCount", "existed", "hasGrid" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightAddProbeGroup(string name = null, int instanceId = 0, string path = null,
            int gridX = 0, int gridY = 0, int gridZ = 0,
            float spacingX = 2f, float spacingY = 1.5f, float spacingZ = 2f)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var lpg = go.GetComponent<LightProbeGroup>();
            bool existed = lpg != null;
            if (!existed)
                lpg = Undo.AddComponent<LightProbeGroup>(go);

            // Set grid layout if any grid parameter provided
            if (gridX > 0 && gridY > 0 && gridZ > 0)
            {
                Undo.RecordObject(lpg, "Set Light Probe Positions");
                var probes = new Vector3[gridX * gridY * gridZ];
                int idx = 0;
                float offsetX = (gridX - 1) * spacingX * 0.5f;
                float offsetZ = (gridZ - 1) * spacingZ * 0.5f;
                for (int iy = 0; iy < gridY; iy++)
                    for (int ix = 0; ix < gridX; ix++)
                        for (int iz = 0; iz < gridZ; iz++)
                            probes[idx++] = new Vector3(ix * spacingX - offsetX, iy * spacingY, iz * spacingZ - offsetZ);
                lpg.probePositions = probes;
                EditorUtility.SetDirty(lpg);
            }

            return new { success = true, gameObject = go.name, probeCount = lpg.probePositions.Length,
                existed, hasGrid = gridX > 0 && gridY > 0 && gridZ > 0 };
        }

        [UnitySkill("light_add_reflection_probe", "Create a Reflection Probe at a position",
            Category = SkillCategory.Light, Operation = SkillOperation.Create,
            Tags = new[] { "reflectionProbe", "reflection", "environment", "gi" },
            Outputs = new[] { "name", "instanceId", "resolution", "size" },
            TracksWorkflow = true)]
        public static object LightAddReflectionProbe(string probeName = "ReflectionProbe", float x = 0, float y = 1, float z = 0,
            float sizeX = 10, float sizeY = 10, float sizeZ = 10, int resolution = 256)
        {
            var go = new GameObject(probeName);
            go.transform.position = new Vector3(x, y, z);
            var probe = go.AddComponent<ReflectionProbe>();
            probe.size = new Vector3(sizeX, sizeY, sizeZ);
            probe.resolution = resolution;

            Undo.RegisterCreatedObjectUndo(go, "Create Reflection Probe");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, name = go.name, instanceId = go.GetInstanceID(), resolution, size = new { x = sizeX, y = sizeY, z = sizeZ } };
        }

        [UnitySkill("light_get_lightmap_settings", "Get Lightmap baking settings",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "lightmap", "baking", "gi", "settings" },
            Outputs = new[] { "bakedGI", "realtimeGI", "lightmapSize", "isRunning", "lightmapCount" },
            ReadOnly = true)]
        public static object LightGetLightmapSettings()
        {
            var settings = Lightmapping.lightingSettings;
            return new
            {
                success = true,
                bakedGI = Lightmapping.bakedGI,
                realtimeGI = Lightmapping.realtimeGI,
                lightmapSize = settings != null ? settings.lightmapMaxSize : 1024,
                lightmapPadding = settings != null ? settings.lightmapPadding : 2,
                isRunning = Lightmapping.isRunning,
                lightmapCount = LightmapSettings.lightmaps.Length
            };
        }
    }
}
