using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Light management skills: create, configure, and query lights.
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
            string shadows = "Soft")
        {
            // Both enums are validated before the GameObject is created, so an invalid value never
            // leaves behind a half-configured light (a switch fallback would let an invalid shadows
            // value silently fall through to None).
            if (!SkillParamUtil.TryParseRequiredEnum<LightType>(lightType, "lightType", out var lt, out var typeError))
                return typeError;
            if (!SkillParamUtil.TryParseRequiredEnum<LightShadows>(shadows, "shadows", out var shadowMode, out var shadowsError))
                return shadowsError;

            var go = new GameObject(name);
            var light = go.AddComponent<Light>();
            light.type = lt;

            go.transform.position = new Vector3(x, y, z);

            light.color = new Color(r, g, b);
            light.intensity = intensity;

            if (lt == LightType.Point || lt == LightType.Spot)
                light.range = range;

            if (lt == LightType.Spot)
                light.spotAngle = spotAngle;

            light.shadows = shadowMode;

            Undo.RegisterCreatedObjectUndo(go, "Create Light");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                lightType = light.type.ToString(),
                position = new { x, y, z },
                color = new { r, g, b },
                intensity,
                shadows = light.shadows.ToString()
            };
        }

        [UnitySkill("light_set_properties", "Set light properties (supports name/instanceId/path). Colour takes r/g/b/a, each defaulting to the light's current channel. Rejects the whole call if shadows is not a valid value; the response lists which parameters were 'applied' and which were 'skipped' because the light type does not carry them (range needs Point/Spot, spotAngle needs Spot).",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "color", "intensity", "shadow" },
            Outputs = new[] { "lightType", "color", "intensity", "range", "spotAngle", "shadows", "applied", "skipped" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightSetProperties(
            string name = null, int instanceId = 0, string path = null,
            float? r = null, float? g = null, float? b = null, float? a = null,
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

            // All validation must complete before the first write. Writing color/intensity/range
            // and then checking shadows would mean an invalid value returns a { warning } object —
            // the router treats that as neither success nor failure, so the caller gets no payload
            // at all even though those earlier fields have already been written.
            if (!SkillParamUtil.TryParseOptionalEnum<LightShadows>(shadows, "shadows", out var shadowMode, out var shadowsError))
                return shadowsError;

            WorkflowManager.SnapshotObject(light);
            Undo.RecordObject(light, "Set Light Properties");

            var applied = new List<string>();
            var skipped = new List<string>();

            if (r.HasValue || g.HasValue || b.HasValue || a.HasValue)
            {
                var currentColor = light.color;
                light.color = new Color(
                    r ?? currentColor.r,
                    g ?? currentColor.g,
                    b ?? currentColor.b,
                    a ?? currentColor.a
                );
                // applied's contract is "the parameter names that actually took effect", so list per-channel.
                // "color" is not a parameter of this skill; reporting it would mismatch what the caller sent.
                if (r.HasValue) applied.Add("r");
                if (g.HasValue) applied.Add("g");
                if (b.HasValue) applied.Add("b");
                if (a.HasValue) applied.Add("a");
            }

            if (intensity.HasValue)
            {
                light.intensity = intensity.Value;
                applied.Add("intensity");
            }

            if (range.HasValue)
            {
                if (light.type == LightType.Point || light.type == LightType.Spot)
                {
                    light.range = range.Value;
                    applied.Add("range");
                }
                else
                {
                    skipped.Add($"range (ignored: {light.type} lights have no range)");
                }
            }

            if (spotAngle.HasValue)
            {
                if (light.type == LightType.Spot)
                {
                    light.spotAngle = spotAngle.Value;
                    applied.Add("spotAngle");
                }
                else
                {
                    skipped.Add($"spotAngle (ignored: only Spot lights have a cone angle, this is {light.type})");
                }
            }

            if (shadowMode.HasValue)
            {
                light.shadows = shadowMode.Value;
                applied.Add("shadows");
            }

            return new
            {
                success = true,
                name = go.name,
                applied = applied.ToArray(),
                skipped = skipped.ToArray(),
                lightType = light.type.ToString(),
                color = new { r = light.color.r, g = light.color.g, b = light.color.b, a = light.color.a },
                intensity = light.intensity,
                range = light.range,
                spotAngle = light.spotAngle,
                shadows = light.shadows.ToString()
            };
        }

        [UnitySkill("light_get_info", "Get information about a light (supports name/instanceId/path)",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "light", "info", "inspect" },
            // Must list every key ReadLight actually returns: omitting one from Outputs means
            // the caller makes an extra round trip for a value that's already in the response.
            Outputs = new[] { "name", "entityId", "instanceId", "path", "lightType", "color", "intensity",
                "range", "spotAngle", "shadows", "enabled", "cullingMask", "bounceIntensity" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object LightGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            return ReadLight(name, instanceId, path);
        }

        [UnitySkill("light_get_properties", "Alias of light_get_info — get information about a light (supports name/instanceId/path). Same parameters, same response; exists because the setter is light_set_properties and callers reach for the matching getter name.",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "light", "info", "inspect" },
            // Must list every key ReadLight actually returns: omitting one from Outputs means
            // the caller makes an extra round trip for a value that's already in the response.
            Outputs = new[] { "name", "entityId", "instanceId", "path", "lightType", "color", "intensity",
                "range", "spotAngle", "shadows", "enabled", "cullingMask", "bounceIntensity" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object LightGetProperties(string name = null, int instanceId = 0, string path = null)
        {
            return ReadLight(name, instanceId, path);
        }

        private static object ReadLight(string name, int instanceId, string path)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var light = go.GetComponent<Light>();
            if (light == null)
                return new { error = $"No Light component on {go.name}" };

            return new
            {
                name = go.name,
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                path = GameObjectFinder.GetPath(go),
                lightType = light.type.ToString(),
                color = new { r = light.color.r, g = light.color.g, b = light.color.b, a = light.color.a },
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
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object LightFindAll(string lightType = null, int limit = 50)
        {
            // A misspelled filter value must be an error: letting it through would return every
            // light in the scene, and the caller would interpret that as "they're all Directional."
            if (!SkillParamUtil.TryParseOptionalEnum<LightType>(lightType, "lightType", out var lt, out var typeError))
                return typeError;

            var lights = FindHelper.FindAll<Light>();

            if (lt.HasValue)
                lights = lights.Where(l => l.type == lt.Value).ToArray();

            var results = lights.Take(limit).Select(l => new
            {
                name = l.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(l.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(l.gameObject),
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
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightSetEnabledBatch(string items)
        {
            return BatchExecutor.Execute<BatchLightEnabledItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path };

                var light = go.GetComponent<Light>();
                if (light == null) return new { error = "No Light component", target = go.name };

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

        [UnitySkill("light_set_properties_batch", "Set properties for multiple lights in one call (Efficient). items: JSON array of {name, instanceId, r, g, b, a, intensity, range, shadows}",
            Category = SkillCategory.Light, Operation = SkillOperation.Modify,
            Tags = new[] { "light", "batch", "properties", "color" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object LightSetPropertiesBatch(string items)
        {
            return BatchExecutor.Execute<BatchLightPropsItem>(items, item =>
            {
                var (go, error) = GameObjectFinder.FindOrError(item.name, item.instanceId, item.path);
                if (error != null) return new { error = "Object not found", target = item.name ?? item.path };

                var light = go.GetComponent<Light>();
                if (light == null) return new { error = "No Light component", target = go.name };

                // Same "parse first, then write" rule as the single-object setter, scoped to this item.
                if (!SkillParamUtil.TryParseOptionalEnum<LightShadows>(item.shadows, "shadows", out var shadowMode, out _))
                    return SkillParamUtil.InvalidEnumError<LightShadows>(item.shadows, "shadows", go.name);

                WorkflowManager.SnapshotObject(light);
                Undo.RecordObject(light, "Batch Set Light Properties");

                if (item.r.HasValue || item.g.HasValue || item.b.HasValue || item.a.HasValue)
                {
                    var c = light.color;
                    light.color = new Color(item.r ?? c.r, item.g ?? c.g, item.b ?? c.b, item.a ?? c.a);
                }
                if (item.intensity.HasValue) light.intensity = item.intensity.Value;
                if (item.range.HasValue && (light.type == LightType.Point || light.type == LightType.Spot))
                    light.range = item.range.Value;
                if (shadowMode.HasValue) light.shadows = shadowMode.Value;

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
            public float? a { get; set; }
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

            return new { success = true, name = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go), resolution, size = new { x = sizeX, y = sizeY, z = sizeZ } };
        }

        [UnitySkill("light_get_lightmap_settings", "Get Lightmap baking settings",
            Category = SkillCategory.Light, Operation = SkillOperation.Query,
            Tags = new[] { "lightmap", "baking", "gi", "settings" },
            Outputs = new[] { "bakedGI", "realtimeGI", "lightmapSize", "lightmapPadding", "isRunning", "lightmapCount", "hasLightingSettings" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object LightGetLightmapSettings()
        {
            // A default project (or a scene that has never created/assigned a Lighting Settings asset)
            // has no active LightingSettings. bakedGI/realtimeGI are underneath just properties on that
            // asset, so accessing them when none is assigned throws
            // "Lightmapping.lightingSettings is null..." instead of returning a usable response.
            //
            // The key thing is that the getter itself throws, rather than returning null and letting
            // the caller check — so a plain settings == null check never gets a chance to run, and this
            // read-only query would count as a smoke-test failure on any project without a Lighting
            // Settings asset. Both outcomes (null or throw) are treated identically: both are handled
            // as "no asset assigned" and report Unity's built-in defaults.
            try
            {
                var settings = Lightmapping.lightingSettings;
                if (settings != null)
                {
                    return new
                    {
                        success = true,
                        hasLightingSettings = true,
                        bakedGI = Lightmapping.bakedGI,
                        realtimeGI = Lightmapping.realtimeGI,
                        lightmapSize = settings.lightmapMaxSize,
                        lightmapPadding = settings.lightmapPadding,
                        isRunning = Lightmapping.isRunning,
                        lightmapCount = LightmapSettings.lightmaps.Length
                    };
                }
            }
            catch (System.Exception)
            {
                // Falls through to the "no settings" branch below; deliberately not an error: "this
                // scene has no Lighting Settings asset" is a normal project state, and the defaults
                // reported below are exactly what Unity itself uses when baking.
            }

            return new
            {
                success = true,
                hasLightingSettings = false,
                note = "No Lighting Settings asset is assigned to this scene; reporting Unity's baked-in defaults.",
                bakedGI = false,
                realtimeGI = false,
                lightmapSize = 1024,
                lightmapPadding = 2,
                isRunning = Lightmapping.isRunning,
                lightmapCount = LightmapSettings.lightmaps.Length
            };
        }
    }
}

// Producer:Betsy
