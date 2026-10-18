using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Built-in Particle System (Shuriken) skills: discovery, typed module configuration, renderer and bursts, with the
    /// same patch contract for every write. This file holds only the skill entries and their metadata, and compiles
    /// without the Particle System module (each body then answers MISSING_PACKAGE). The work lives in the partial
    /// files: Registry (modules/fields), Codec (JSON value shapes), Prepare (validation shared with the dryRun
    /// planners), Apply (Undo/workflow transaction), Query, Authoring and Planning. Visual Effect Graph is out of scope.
    /// Calls into other partial files are written ParticleSkills.X(...): OutputsReturnContractTests resolves a bare call
    /// only within its own file, a qualified one by owner across files.
    /// </summary>
    public static partial class ParticleSkills
    {
        internal const string DefaultSystemName = "Particle System";
        /// <summary>Unity's GameObject > Effects > Particle System turns the object -90 degrees on X so the default cone emits upwards.</summary>
        internal const float DefaultRotX = -90f;

        private const string LocatorNote = "Target ParticleSystem object: entityId (preferred), name, path or instanceId.";
        private const string ConfigNote = "configJson: {\"schemaVersion\": 1, \"system\"?: {useAutoRandomSeed, randomSeed}, \"modules\"?: {main|emission|shape|velocityOverLifetime|colorOverLifetime|sizeOverLifetime|noise: {field: value}}}; field list, value shapes and ranges: particle_get_capabilities.";

        // ===================== Discovery and reads =====================

        [UnitySkill("particle_get_capabilities", "Describe the built-in ParticleSystem (Shuriken, not VFX Graph) contract of this Editor: module availability, every module and field with type, unit, range, enum values and curve/gradient modes, the configJson/burstsJson shapes and limits. module: one id (main, emission, shape, renderer, system, ...) or omit for all. Call this before writing configJson.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "shuriken", "capabilities", "schema", "fields", "modules", "contract" },
            Outputs = new[] { "available", "schemaVersion", "unityVersion", "modules", "valueTypes", "limits" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetCapabilities(
            [SkillParam("One module id (system, main, emission, shape, velocityOverLifetime, colorOverLifetime, sizeOverLifetime, noise, renderer, or a P1 module); omit for every module.")]
            string module = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return SimulateMissingModuleForTests ? (object)ParticleSkills.UnavailableCapabilities() : ParticleSkills.CapabilitiesCore(module);
#else
            return ParticleSkills.UnavailableCapabilities();
#endif
        }

        [UnitySkill("particle_find_all", "List the ParticleSystems in the open scenes in Hierarchy order (no project assets or Prefab Mode objects), optionally under a root object (rootName/rootEntityId/rootInstanceId/rootPath). Returns name, entityId, path, playback flags, duration, loop, maxParticles, emission and renderer summary, paged by limit/offset with total and truncated.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "find", "list", "scene", "effect", "fx", "shuriken" },
            Outputs = new[] { "total", "count", "offset", "limit", "truncated", "systems" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleFindAll(string rootName = null, int rootInstanceId = 0,
            [SkillParam("Only search under this hierarchy path (the root itself included); omit all root* parameters to search every open scene.")]
            string rootPath = null,
            [SkillParam("Include systems on inactive objects.")]
            bool includeInactive = true,
            [SkillParam("Page size, 1-500.")]
            int limit = 100,
            [SkillParam("Systems to skip before the page, in Hierarchy order.")]
            int offset = 0,
            string rootEntityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_find_all") ?? ParticleSkills.FindAllCore(new Locator(rootName, rootInstanceId, rootPath, rootEntityId), includeInactive, limit, offset);
#else
            return ParticleSkills.MissingModuleError("particle_find_all");
#endif
        }

        [UnitySkill("particle_get_info", "Summarize one ParticleSystem (name/entityId/instanceId/path): playback state, system seed settings, a Main summary, every module's enabled flag, a renderer summary and the burst count. includeModules=true returns every supported field of every module instead.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "info", "inspect", "modules", "shuriken" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "playback", "system", "main", "modules", "renderer", "burstCount" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetInfo(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("false (default): summary; true: the full readback of every module.")]
            bool includeModules = false,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_info") ?? ParticleSkills.GetInfoCore(new Locator(name, instanceId, path, entityId), includeModules);
#else
            return ParticleSkills.MissingModuleError("particle_get_info");
#endif
        }

        [UnitySkill("particle_get_info_batch", "particle_get_info for several systems in one call. items: JSON array of {name|entityId|instanceId|path, includeModules?}.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "info", "batch", "inspect" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetInfoBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, includeModules?}; at most 100 items, unknown keys rejected.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_info_batch") ?? ParticleSkills.GetInfoBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_get_info_batch");
#endif
        }

        [UnitySkill("particle_get_module", "Read every supported field of one module of a ParticleSystem (name/entityId/instanceId/path) in the same shape configJson accepts: module = main, emission, shape, velocityOverLifetime, colorOverLifetime, sizeOverLifetime, noise, system, renderer, or a P1 module (enabled flag only). Lists such as bursts point to their own skills.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "module", "read", "inspect", "main", "emission", "shape", "noise" },
            Outputs = new[] { "module", "fields" },
            RequiresInput = new[] { "gameObject" },
            RequiredParams = new[] { "module" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetModule(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("Module id, e.g. main, emission, shape, noise, renderer (see particle_get_capabilities).")]
            string module = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_module") ?? ParticleSkills.GetModuleCore(new Locator(name, instanceId, path, entityId), module);
#else
            return ParticleSkills.MissingModuleError("particle_get_module");
#endif
        }

        [UnitySkill("particle_get_renderer", "Read the ParticleSystemRenderer of a ParticleSystem (name/entityId/instanceId/path): enabled, renderMode, material and mesh references, sorting, alignment and display fields, in the shape particle_set_renderer accepts.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "renderer", "material", "sorting", "read" },
            Outputs = new[] { "renderer" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetRenderer(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null, string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_renderer") ?? ParticleSkills.GetRendererCore(new Locator(name, instanceId, path, entityId));
#else
            return ParticleSkills.MissingModuleError("particle_get_renderer");
#endif
        }

        [UnitySkill("particle_get_bursts", "Read the complete burst list of a ParticleSystem (name/entityId/instanceId/path): time, count (MinMaxCurve), cycleCount, repeatInterval and probability of each burst, with the system's duration and loop.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "burst", "bursts", "emission", "read" },
            Outputs = new[] { "count", "bursts", "duration" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetBursts(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null, string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_bursts") ?? ParticleSkills.GetBurstsCore(new Locator(name, instanceId, path, entityId));
#else
            return ParticleSkills.MissingModuleError("particle_get_bursts");
#endif
        }

        // ===================== Creation =====================

        [UnitySkill("particle_create", "Create a GameObject with a built-in ParticleSystem and its ParticleSystemRenderer (not VFX Graph), optionally configured by configJson in one Undo step. The system is stopped with playOnAwake=false (unless configJson sets it) and gets the render pipeline's default particle material. x/y/z and rotX/rotY/rotZ (degrees; rotX defaults to -90 so the default cone emits upwards, as Unity's menu does) are local to the parent unless space='world'.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Create,
            Tags = new[] { "particle", "particles", "create", "effect", "fx", "shuriken", "smoke", "fire", "rain", "snow", "sparks" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "parentPath", "position", "localPosition", "rotation", "particleSystem", "renderer", "material", "defaultsApplied", "applied", "state", "playback", "warnings" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleCreate(
            [SkillParam("Name of the new GameObject.")]
            string name = DefaultSystemName,
            [SkillParam("x/y/z: position, local to the parent unless space='world'.")]
            float x = 0, float y = 0, float z = 0,
            string parentName = null, int parentInstanceId = 0,
            [SkillParam("Parent: parentEntityId > parentInstanceId > parentPath > parentName; omit all for a scene-root object. The parent must already exist.")]
            string parentPath = null, string parentEntityId = null,
            [SkillParam("rotX/rotY/rotZ: Euler angles in degrees; the default -90/0/0 points the default cone up.")]
            float rotX = DefaultRotX, float rotY = 0, float rotZ = 0,
            [SkillParam("'local' (default) or 'world' for x/y/z and rotX/rotY/rotZ.")]
            string space = "local",
            [SkillParam("Optional initial configuration, same shape as particle_configure's configJson (may be omitted).")]
            string configJson = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_create") ?? ParticleSkills.CreateCore(name, x, y, z, new Locator(parentName, parentInstanceId, parentPath, parentEntityId), rotX, rotY, rotZ, space, configJson);
#else
            return ParticleSkills.MissingModuleError("particle_create");
#endif
        }

        [UnitySkill("particle_create_batch", "Create several ParticleSystem objects in one all-or-nothing call: every item is validated first and nothing is created if any item is invalid. items: JSON array of particle_create parameters {name, x, y, z, rotX, rotY, rotZ, space, parentName|parentEntityId|parentInstanceId|parentPath, configJson}.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Create,
            Tags = new[] { "particle", "particles", "create", "batch", "effect" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleCreateBatch(
            [SkillParam("JSON array of {name?, x?, y?, z?, rotX?, rotY?, rotZ?, space?, parentName|parentEntityId|parentInstanceId|parentPath?, configJson?}; at most 100 items, unknown keys rejected; parents must already exist.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_create_batch") ?? ParticleSkills.CreateBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_create_batch");
#endif
        }

        [UnitySkill("particle_add_system", "Add a ParticleSystem (Unity adds its ParticleSystemRenderer) to an existing object (name/entityId/instanceId/path) that has none, optionally configured by configJson; an object that already has a system is refused, never reset. Like particle_create, playOnAwake becomes false and the default particle material is assigned.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "add", "component", "effect", "shuriken" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "particleSystem", "renderer", "applied", "state", "warnings" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleAddSystem(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("Optional initial configuration, same shape as particle_configure's configJson.")]
            string configJson = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_add_system") ?? ParticleSkills.AddSystemCore(new Locator(name, instanceId, path, entityId), configJson);
#else
            return ParticleSkills.MissingModuleError("particle_add_system");
#endif
        }

        [UnitySkill("particle_add_system_batch", "particle_add_system for several objects in one all-or-nothing call. items: JSON array of {name|entityId|instanceId|path, configJson?}.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "add", "component", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleAddSystemBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, configJson?}; at most 100 items, one item per object.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_add_system_batch") ?? ParticleSkills.AddSystemBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_add_system_batch");
#endif
        }

        // ===================== Configuration =====================

        [UnitySkill("particle_configure", "Configure a built-in ParticleSystem (name/entityId/instanceId/path; Shuriken, not VFX Graph) with a partial configJson patch over system and modules (main, emission, shape, velocityOverLifetime, colorOverLifetime, sizeOverLifetime, noise), applied atomically: omitted fields keep their value, false and 0 are applied, curves and gradients replace the property, any invalid field rejects the whole call. Needs Edit Mode and a stopped system; returns applied paths and the state read back from Unity.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "configure", "modules", "main", "emission", "shape", "noise", "curve", "gradient", "shuriken" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "applied", "state", "warnings" },
            RequiresInput = new[] { "gameObject" },
            RequiredParams = new[] { "configJson" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleConfigure(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam(ConfigNote)] string configJson = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_configure") ?? ParticleSkills.ConfigureCore(new Locator(name, instanceId, path, entityId), configJson);
#else
            return ParticleSkills.MissingModuleError("particle_configure");
#endif
        }

        [UnitySkill("particle_configure_batch", "particle_configure for several systems in one all-or-nothing call: every item is validated first, nothing is written if any item is invalid, and a failure while applying rolls every item back. items: JSON array of {name|entityId|instanceId|path, configJson}, one item per system.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "configure", "batch", "modules" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleConfigureBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, configJson}; at most 100 items, one item per system, unknown keys rejected.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_configure_batch") ?? ParticleSkills.ConfigureBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_configure_batch");
#endif
        }

        [UnitySkill("particle_set_renderer", "Configure the ParticleSystemRenderer of a ParticleSystem (name/entityId/instanceId/path) with a partial configJson {\"schemaVersion\": 1, \"renderer\": {enabled, renderMode, material, mesh, sortMode, sortingFudge, sortingLayerName, sortingOrder, alignment, pivot, flip, ...}}. material/mesh are asset references (shared material, never an instance; the asset itself is not modified). Atomic, stopped system and Edit Mode only; returns the read-back state.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "renderer", "material", "sorting", "billboard", "mesh" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "applied", "state", "warnings" },
            RequiresInput = new[] { "gameObject" },
            RequiredParams = new[] { "configJson" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetRenderer(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("configJson: {\"schemaVersion\": 1, \"renderer\": {field: value}}; fields and ranges: particle_get_capabilities with module=renderer.")]
            string configJson = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_renderer") ?? ParticleSkills.SetRendererCore(new Locator(name, instanceId, path, entityId), configJson);
#else
            return ParticleSkills.MissingModuleError("particle_set_renderer");
#endif
        }

        [UnitySkill("particle_set_renderer_batch", "particle_set_renderer for several systems in one all-or-nothing call. items: JSON array of {name|entityId|instanceId|path, configJson}, one item per system.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "renderer", "material", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetRendererBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, configJson}; at most 100 items, one item per system.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_renderer_batch") ?? ParticleSkills.SetRendererBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_set_renderer_batch");
#endif
        }

        [UnitySkill("particle_set_bursts", "Replace the whole burst list of a ParticleSystem (name/entityId/instanceId/path). burstsJson: JSON array of at most 8 {time (s), count (MinMaxCurve or number), cycleCount? (1; 0 repeats forever), repeatInterval? (0.01 s), probability? (1)}; [] clears the list. Atomic, stopped system and Edit Mode only; returns the list read back from Unity.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "burst", "bursts", "emission", "explosion" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "count", "bursts", "warnings" },
            RequiresInput = new[] { "gameObject" },
            RequiredParams = new[] { "burstsJson" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetBursts(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("The complete burst list as a JSON array (not a patch); [] clears it. Omitting it is an error.")]
            string burstsJson = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_bursts") ?? ParticleSkills.SetBurstsCore(new Locator(name, instanceId, path, entityId), burstsJson);
#else
            return ParticleSkills.MissingModuleError("particle_set_bursts");
#endif
        }

        [UnitySkill("particle_set_bursts_batch", "particle_set_bursts for several systems in one all-or-nothing call. items: JSON array of {name|entityId|instanceId|path, burstsJson}, one item per system.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "burst", "bursts", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetBurstsBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, burstsJson}; at most 100 items, one item per system.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_bursts_batch") ?? ParticleSkills.SetBurstsBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_set_bursts_batch");
#endif
        }

        // ===================== Batch items =====================

        private sealed class BatchParticleInfoItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public bool? includeModules { get; set; }

            [JsonIgnore] internal Locator Locator => new Locator(name, instanceId, path, entityId);
        }

        private sealed class BatchParticleCreateItem
        {
            public string name { get; set; }
            public float? x { get; set; }
            public float? y { get; set; }
            public float? z { get; set; }
            public float? rotX { get; set; }
            public float? rotY { get; set; }
            public float? rotZ { get; set; }
            public string space { get; set; }
            public string parentName { get; set; }
            public int parentInstanceId { get; set; }
            public string parentPath { get; set; }
            public string parentEntityId { get; set; }
            public JToken configJson { get; set; }

            [JsonIgnore] internal Locator ParentLocator => new Locator(parentName, parentInstanceId, parentPath, parentEntityId);
        }

        private sealed class BatchParticleConfigItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public JToken configJson { get; set; }

            [JsonIgnore] internal Locator Locator => new Locator(name, instanceId, path, entityId);
        }

        private sealed class BatchParticleBurstsItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public JToken burstsJson { get; set; }

            [JsonIgnore] internal Locator Locator => new Locator(name, instanceId, path, entityId);
        }

        // ===================== P1: sub emitters and static analysis =====================

        [UnitySkill("particle_get_sub_emitters", "Read the sub-emitter list of a ParticleSystem (name/entityId/instanceId/path): each entry's system (name/entityId/path, or missing:true for a broken reference), type (Birth/Collision/Death/Trigger/Manual), inherited properties and emitProbability, and whether the Sub Emitters module is enabled.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Query,
            Tags = new[] { "particle", "particles", "subemitter", "sub-emitter", "subemitters", "read", "firework" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "enabled", "count", "subEmitters", "warnings" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleGetSubEmitters(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null, string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_get_sub_emitters") ?? ParticleSkills.GetSubEmittersCore(new Locator(name, instanceId, path, entityId));
#else
            return ParticleSkills.MissingModuleError("particle_get_sub_emitters");
#endif
        }

        [UnitySkill("particle_set_sub_emitters", "Replace the whole sub-emitter list of a ParticleSystem (name/entityId/instanceId/path). subEmittersJson: JSON array of at most 16 {system (entityId/name/path of an existing ParticleSystem in the same scene), type (Birth|Collision|Death|Trigger|Manual), properties? (InheritColor/InheritSize/InheritRotation/InheritLifetime/InheritDuration; [] inherits nothing), emitProbability? (0-1, default 1)}; [] clears it. Self references and cycles are refused; nothing is created, reparented or enabled. Atomic, stopped system and Edit Mode only; returns the list read back from Unity.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "subemitter", "sub-emitter", "subemitters", "firework", "explosion", "chain" },
            Outputs = new[] { "name", "entityId", "instanceId", "path", "enabled", "count", "subEmitters", "warnings" },
            RequiresInput = new[] { "gameObject" },
            RequiredParams = new[] { "subEmittersJson" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetSubEmitters(string name = null, int instanceId = 0,
            [SkillParam(LocatorNote)] string path = null,
            [SkillParam("The complete sub-emitter list as a JSON array (not a patch); [] clears it. Omitting it is an error.")]
            string subEmittersJson = null,
            string entityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_sub_emitters") ?? ParticleSkills.SetSubEmittersCore(new Locator(name, instanceId, path, entityId), subEmittersJson);
#else
            return ParticleSkills.MissingModuleError("particle_set_sub_emitters");
#endif
        }

        [UnitySkill("particle_set_sub_emitters_batch", "particle_set_sub_emitters for several systems in one all-or-nothing call; cycles are checked across the whole batch, so a rocket -> explosion -> sparks chain can be set in one call. items: JSON array of {name|entityId|instanceId|path, subEmittersJson}, one item per system.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Modify,
            Tags = new[] { "particle", "particles", "subemitter", "subemitters", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesScene = true, RiskLevel = "medium")]
        public static object ParticleSetSubEmittersBatch(
            [SkillParam("JSON array of {name|entityId|instanceId|path, subEmittersJson}; at most 100 items, one item per system.")]
            string items)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_set_sub_emitters_batch") ?? ParticleSkills.SetSubEmittersBatchCore(items);
#else
            return ParticleSkills.MissingModuleError("particle_set_sub_emitters_batch");
#endif
        }

        [UnitySkill("particle_analyze", "Read-only static diagnosis of built-in ParticleSystems: one system (name/entityId/instanceId/path), every system under a root (rootName/rootEntityId/rootInstanceId/rootPath), or every system in the open scenes. Reports missing or pipeline-mismatched materials and shaders, disabled renderer or emission, systems that never emit, invisible colours, odd start size or lifetime, high particle caps, collision/light/trail cost, missing references and side-effect stop actions; each finding has code, severity, evidence and a suggested skill. thresholdsJson overrides the limits. No frame times or GPU cost; nothing is changed.",
            Category = SkillCategory.Particle, Operation = SkillOperation.Analyze,
            Tags = new[] { "particle", "particles", "analyze", "diagnose", "lint", "check", "performance", "invisible", "pink" },
            Outputs = new[] { "scope", "systemsAnalyzed", "totalMaxParticles", "findingCount", "counts", "findings", "truncated", "thresholds" },
            ReadOnly = true, Mode = SkillMode.SemiAuto)]
        public static object ParticleAnalyze(string name = null, int instanceId = 0,
            [SkillParam("One system to analyze: entityId (preferred), name, path or instanceId. Omit it and every root* parameter to analyze the open scenes.")]
            string path = null,
            string rootName = null, int rootInstanceId = 0,
            [SkillParam("Analyze every system under this object (itself included); not combinable with a single system.")]
            string rootPath = null,
            [SkillParam("Include systems on inactive objects.")]
            bool includeInactive = true,
            [SkillParam("Most findings returned, 1-500, most severe first.")]
            int limit = 100,
            [SkillParam("Optional JSON object overriding thresholds: maxParticlesPerSystem, totalMaxParticles, maxSystems, maxLights (integers), minStartSize, maxStartSize, minStartLifetime, maxStartLifetime, maxEmissionRate (numbers > 0).")]
            string thresholdsJson = null,
            string entityId = null, string rootEntityId = null)
        {
#if COM_UNITY_MODULES_PARTICLESYSTEM
            return ParticleSkills.Gate("particle_analyze") ?? ParticleSkills.AnalyzeCore(new Locator(name, instanceId, path, entityId),
                new Locator(rootName, rootInstanceId, rootPath, rootEntityId), includeInactive, limit, thresholdsJson);
#else
            return ParticleSkills.MissingModuleError("particle_analyze");
#endif
        }

        private sealed class BatchParticleSubEmittersItem
        {
            public string name { get; set; }
            public string entityId { get; set; }
            public int instanceId { get; set; }
            public string path { get; set; }
            public JToken subEmittersJson { get; set; }

            [JsonIgnore] internal Locator Locator => new Locator(name, instanceId, path, entityId);
        }
    }
}

// Producer:Betsy
