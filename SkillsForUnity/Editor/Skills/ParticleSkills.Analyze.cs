#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        internal const int AnalyzeMaxLimit = 500;

        internal const string SeverityError = "error";
        internal const string SeverityWarning = "warning";
        internal const string SeverityInfo = "info";

        /// <summary>
        /// The configurable limits of particle_analyze. They are judgement calls, not Unity limits: a high cap or many
        /// systems is a hint to measure, never a frame-time prediction.
        /// </summary>
        internal sealed class AnalyzeThresholds
        {
            public int MaxParticlesPerSystem = 10000;
            public int TotalMaxParticles = 100000;
            public int MaxSystems = 200;
            public double MinStartSize = 0.0001;
            public double MaxStartSize = 1000;
            public double MinStartLifetime = 0.01;
            public double MaxStartLifetime = 600;
            public double MaxEmissionRate = 5000;
            public int MaxLights = 20;

            public JObject ToJson() => new JObject
            {
                ["maxParticlesPerSystem"] = MaxParticlesPerSystem,
                ["totalMaxParticles"] = TotalMaxParticles,
                ["maxSystems"] = MaxSystems,
                ["minStartSize"] = MinStartSize,
                ["maxStartSize"] = MaxStartSize,
                ["minStartLifetime"] = MinStartLifetime,
                ["maxStartLifetime"] = MaxStartLifetime,
                ["maxEmissionRate"] = MaxEmissionRate,
                ["maxLights"] = MaxLights
            };

            public static readonly string[] Keys =
            {
                "maxParticlesPerSystem", "totalMaxParticles", "maxSystems", "minStartSize", "maxStartSize",
                "minStartLifetime", "maxStartLifetime", "maxEmissionRate", "maxLights"
            };
        }

        /// <summary>One diagnosis: what, how bad, on which system and field, the evidence, and a skill that addresses it.</summary>
        internal sealed class Finding
        {
            public string Code;
            public string Severity;
            public ParticleSystem System;
            public int Order;
            public string Field;
            public string Message;
            public JObject Evidence;
            public Dictionary<string, object> Fix;

            public Dictionary<string, object> ToJson()
            {
                var result = new Dictionary<string, object>
                {
                    ["code"] = Code,
                    ["severity"] = Severity,
                    ["system"] = System != null ? (object)ReferenceToJson(System.gameObject) : null,
                    ["field"] = Field,
                    ["message"] = Message
                };
                if (Evidence != null)
                    result["evidence"] = Evidence;
                if (Fix != null)
                    result["fix"] = Fix;
                return result;
            }
        }

        private static int SeverityRank(string severity) => severity == SeverityError ? 0 : severity == SeverityWarning ? 1 : 2;

        // ===================== Entry =====================

        internal static object AnalyzeCore(Locator target, Locator root, bool includeInactive, int limit, string thresholdsJson)
        {
            if (limit < 1 || limit > AnalyzeMaxLimit)
                return Error(CodeSemantic, $"limit must be between 1 and {AnalyzeMaxLimit}, got {limit}.", parameter: "limit");
            if (!target.IsEmpty && !root.IsEmpty)
            {
                return Error(CodeSemantic, "Give either a single system (name/entityId/instanceId/path) or a root (rootName/rootEntityId/rootInstanceId/rootPath), not both.",
                    parameter: "rootPath",
                    fixes: new object[] { Fix("fix_param", "particle_analyze", "Drop the root to analyze one system, or the target to analyze everything under the root.") });
            }

            var problems = new Problems();
            var thresholds = ReadThresholds(thresholdsJson, problems);
            if (problems.Any)
                return ProblemsError("particle_analyze", problems);

            List<ParticleSystem> systems;
            Dictionary<string, object> scope;
            if (!target.IsEmpty)
            {
                var error = ResolveSystem(target, out var binding);
                if (error != null)
                    return error;
                systems = new List<ParticleSystem> { binding.System };
                scope = new Dictionary<string, object> { ["kind"] = "system", ["path"] = GameObjectFinder.GetPath(binding.GameObject) };
            }
            else if (!root.IsEmpty)
            {
                var (rootGo, error) = GameObjectFinder.FindOrError(root.Name, root.InstanceId, root.Path, entityId: root.EntityId);
                if (error != null)
                    return error;
                systems = rootGo.GetComponentsInChildren<ParticleSystem>(true)
                    .Where(system => includeInactive || system.gameObject.activeInHierarchy)
                    .ToList();
                scope = new Dictionary<string, object> { ["kind"] = "root", ["path"] = GameObjectFinder.GetPath(rootGo) };
            }
            else
            {
                systems = GameObjectFinder.GetSceneObjects()
                    .Where(go => go != null && (includeInactive || go.activeInHierarchy))
                    .Select(go => go.GetComponent<ParticleSystem>())
                    .Where(system => system != null)
                    .ToList();
                scope = new Dictionary<string, object> { ["kind"] = "openScenes" };
            }
            systems = systems.Where(system => IsInOpenScene(system.gameObject)).ToList();

            var findings = new List<Finding>();
            var referenced = new HashSet<ParticleSystem>(systems.SelectMany(LiveSubEmitterSystems));
            for (int i = 0; i < systems.Count; i++)
                AnalyzeSystem(systems[i], i, thresholds, referenced, findings);

            long totalMaxParticles = systems.Sum(system => (long)system.main.maxParticles);
            if (systems.Count > 1 && totalMaxParticles > thresholds.TotalMaxParticles)
            {
                findings.Add(new Finding
                {
                    Code = "TOTAL_MAX_PARTICLES_HIGH", Severity = SeverityWarning, Order = int.MaxValue, Field = "modules.main.maxParticles",
                    Message = $"The {systems.Count} analyzed systems allow {totalMaxParticles} particles alive at once (threshold {thresholds.TotalMaxParticles}). A cap is an upper bound, not a cost; profile the scene before lowering caps.",
                    Evidence = new JObject { ["totalMaxParticles"] = totalMaxParticles, ["threshold"] = thresholds.TotalMaxParticles, ["systems"] = systems.Count },
                    Fix = Fix("fix_param", "particle_configure_batch", "Lower modules.main.maxParticles of the systems that never reach their cap.")
                });
            }
            if (systems.Count > thresholds.MaxSystems)
            {
                findings.Add(new Finding
                {
                    Code = "SYSTEM_COUNT_HIGH", Severity = SeverityInfo, Order = int.MaxValue, Field = null,
                    Message = $"{systems.Count} particle systems in scope (threshold {thresholds.MaxSystems}); every active system costs a little CPU even while idle.",
                    Evidence = new JObject { ["systems"] = systems.Count, ["threshold"] = thresholds.MaxSystems }
                });
            }

            var ordered = findings
                .OrderBy(finding => SeverityRank(finding.Severity))
                .ThenBy(finding => finding.Order)
                .ThenBy(finding => finding.Code, StringComparer.Ordinal)
                .ToList();
            var page = ordered.Take(limit).Select(finding => finding.ToJson()).ToArray();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["scope"] = scope,
                ["systemsAnalyzed"] = systems.Count,
                ["totalMaxParticles"] = totalMaxParticles,
                ["findingCount"] = ordered.Count,
                ["counts"] = new Dictionary<string, object>
                {
                    [SeverityError] = ordered.Count(finding => finding.Severity == SeverityError),
                    [SeverityWarning] = ordered.Count(finding => finding.Severity == SeverityWarning),
                    [SeverityInfo] = ordered.Count(finding => finding.Severity == SeverityInfo)
                },
                ["findings"] = page,
                ["truncated"] = ordered.Count > page.Length,
                ["thresholds"] = thresholds.ToJson(),
                ["note"] = "Static checks of the configuration only: no frame times, GPU cost or overdraw, and nothing was changed. Findings are suggestions; check them in the scene."
            };
        }

        private static AnalyzeThresholds ReadThresholds(string json, Problems problems)
        {
            var thresholds = new AnalyzeThresholds();
            if (string.IsNullOrWhiteSpace(json))
                return thresholds;
            var obj = Codec.ExpectObject(Codec.ParseText(json, "thresholdsJson", problems), "thresholdsJson", problems, "an object of threshold name -> number");
            if (obj == null || !Codec.CheckKeys(obj, "thresholdsJson", problems, AnalyzeThresholds.Keys, null))
                return thresholds;

            int ReadCount(string key, int current)
            {
                if (obj.Property(key) == null)
                    return current;
                return Codec.ReadInt(obj[key], "thresholdsJson." + key, problems, out var value, 1, null) ? value : current;
            }
            double ReadPositive(string key, double current)
            {
                if (obj.Property(key) == null)
                    return current;
                return Codec.ReadDouble(obj[key], "thresholdsJson." + key, problems, out var value) && Codec.CheckBounds(value, "thresholdsJson." + key, problems, 0, null, true)
                    ? value
                    : current;
            }

            thresholds.MaxParticlesPerSystem = ReadCount("maxParticlesPerSystem", thresholds.MaxParticlesPerSystem);
            thresholds.TotalMaxParticles = ReadCount("totalMaxParticles", thresholds.TotalMaxParticles);
            thresholds.MaxSystems = ReadCount("maxSystems", thresholds.MaxSystems);
            thresholds.MaxLights = ReadCount("maxLights", thresholds.MaxLights);
            thresholds.MinStartSize = ReadPositive("minStartSize", thresholds.MinStartSize);
            thresholds.MaxStartSize = ReadPositive("maxStartSize", thresholds.MaxStartSize);
            thresholds.MinStartLifetime = ReadPositive("minStartLifetime", thresholds.MinStartLifetime);
            thresholds.MaxStartLifetime = ReadPositive("maxStartLifetime", thresholds.MaxStartLifetime);
            thresholds.MaxEmissionRate = ReadPositive("maxEmissionRate", thresholds.MaxEmissionRate);
            if (!problems.Any && thresholds.MinStartSize >= thresholds.MaxStartSize)
                problems.Add("thresholdsJson.minStartSize", $"minStartSize ({thresholds.MinStartSize}) must be below maxStartSize ({thresholds.MaxStartSize}).");
            if (!problems.Any && thresholds.MinStartLifetime >= thresholds.MaxStartLifetime)
                problems.Add("thresholdsJson.minStartLifetime", $"minStartLifetime ({thresholds.MinStartLifetime}) must be below maxStartLifetime ({thresholds.MaxStartLifetime}).");
            return thresholds;
        }

        // ===================== Checks =====================

        private static void AnalyzeSystem(ParticleSystem system, int order, AnalyzeThresholds thresholds, HashSet<ParticleSystem> referencedAsSubEmitter, List<Finding> findings)
        {
            void Add(string code, string severity, string field, string message, JObject evidence = null, Dictionary<string, object> fix = null) =>
                findings.Add(new Finding { Code = code, Severity = severity, System = system, Order = order, Field = field, Message = message, Evidence = evidence, Fix = fix });

            var go = system.gameObject;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var main = system.main;
            var emission = system.emission;

            // Drawing
            if (renderer == null)
            {
                Add("NO_RENDERER", SeverityWarning, "renderer", "There is no ParticleSystemRenderer: the particles simulate but are never drawn.",
                    fix: Fix("fix_param", "component_add", "Add a ParticleSystemRenderer (componentType 'ParticleSystemRenderer')."));
            }
            else
            {
                if (!renderer.enabled)
                {
                    Add("RENDERER_DISABLED", SeverityWarning, "renderer.enabled", "The renderer is disabled: nothing is drawn.",
                        fix: SetRendererFix(new JObject { ["enabled"] = true }, "Enable the renderer."));
                }
                var material = renderer.sharedMaterial;
                if (material == null)
                {
                    Add("MATERIAL_MISSING", SeverityError, "renderer.material", "The renderer has no material: Unity draws nothing.",
                        fix: DefaultMaterialFix("Assign the render pipeline's default particle material (or a material of your own)."));
                }
                else
                {
                    CheckShader(material, "renderer.material", Add);
                }
                if (renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.meshCount == 0)
                {
                    Add("MESH_MISSING", SeverityError, "renderer.mesh", "renderMode is Mesh but no mesh is assigned: nothing is drawn.",
                        fix: SetRendererFix(new JObject { ["mesh"] = new JObject { ["assetPath"] = "Library/unity default resources", ["name"] = "Cube" } }, "Assign a mesh, or switch renderMode to Billboard."));
                }
                if (system.trails.enabled && renderer.trailMaterial == null)
                {
                    Add("TRAIL_MATERIAL_MISSING", SeverityError, "renderer.trailMaterial", "Trails are enabled but the renderer has no trail material: the trails are not drawn.",
                        new JObject { ["trailsEnabled"] = true });
                }
            }

            // Emission
            if (!emission.enabled)
            {
                var asSub = referencedAsSubEmitter.Contains(system);
                Add("EMISSION_DISABLED", asSub ? SeverityInfo : SeverityWarning, "modules.emission.enabled",
                    asSub
                        ? "The Emission module is disabled; this system is another system's sub emitter, which spawns it, but it still emits nothing itself."
                        : "The Emission module is disabled: the system never emits particles.",
                    fix: ConfigureFix(new JObject { ["emission"] = new JObject { ["enabled"] = true } }, "Enable emission."));
            }
            else if (!EmitsAnything(system))
            {
                Add("NO_EMISSION", SeverityWarning, "modules.emission",
                    "rateOverTime and rateOverDistance are 0 and no burst fires within the duration: the system never emits.",
                    new JObject
                    {
                        ["rateOverTimeMax"] = CurveMax(emission.rateOverTime),
                        ["rateOverDistanceMax"] = CurveMax(emission.rateOverDistance),
                        ["burstCount"] = emission.burstCount
                    },
                    ConfigureFix(new JObject { ["emission"] = new JObject { ["rateOverTime"] = 10 } }, "Give it a rate, or add bursts with particle_set_bursts."));
            }
            if (emission.enabled && CurveMax(emission.rateOverTime) > thresholds.MaxEmissionRate)
            {
                Add("EMISSION_RATE_HIGH", SeverityInfo, "modules.emission.rateOverTime",
                    $"Up to {CurveMax(emission.rateOverTime)} particles per second (threshold {thresholds.MaxEmissionRate}).",
                    new JObject { ["rateOverTimeMax"] = CurveMax(emission.rateOverTime), ["threshold"] = thresholds.MaxEmissionRate });
            }
            if (emission.burstCount > 0)
            {
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                for (int i = 0; i < bursts.Length; i++)
                {
                    if (bursts[i].time > main.duration)
                    {
                        Add("BURST_AFTER_DURATION", SeverityInfo, $"modules.emission.bursts[{i}].time",
                            $"Burst {i} is at {bursts[i].time} s, after the duration ({main.duration} s): it never fires.",
                            new JObject { ["time"] = bursts[i].time, ["duration"] = main.duration });
                    }
                }
            }

            // Visibility
            if (MaxAlpha(main.startColor) <= 0f)
            {
                Add("INVISIBLE_START_COLOR", SeverityWarning, "modules.main.startColor", "Every possible start colour has alpha 0: particles are invisible.",
                    fix: ConfigureFix(new JObject { ["main"] = new JObject { ["startColor"] = new JObject { ["r"] = 1, ["g"] = 1, ["b"] = 1, ["a"] = 1 } } }, "Give the start colour an alpha above 0."));
            }
            var colorOverLifetime = system.colorOverLifetime;
            if (colorOverLifetime.enabled && MaxAlpha(colorOverLifetime.color) <= 0f)
            {
                Add("INVISIBLE_COLOR_OVER_LIFETIME", SeverityWarning, "modules.colorOverLifetime.color", "Color over Lifetime keeps alpha at 0 for the whole lifetime: particles are invisible.",
                    fix: ConfigureFix(new JObject { ["colorOverLifetime"] = new JObject { ["enabled"] = false } }, "Fix the gradient's alpha keys, or disable the module."));
            }

            // Size and lifetime
            double sizeMax = main.startSize3D ? new[] { main.startSizeX, main.startSizeY, main.startSizeZ }.Max(curve => CurveMax(curve)) : CurveMax(main.startSize);
            double sizeMin = main.startSize3D ? new[] { main.startSizeX, main.startSizeY, main.startSizeZ }.Min(curve => CurveMin(curve)) : CurveMin(main.startSize);
            if (sizeMax < thresholds.MinStartSize)
            {
                Add("START_SIZE_TINY", SeverityWarning, "modules.main.startSize", $"The start size never exceeds {sizeMax} (threshold {thresholds.MinStartSize}): particles are too small to see.",
                    new JObject { ["startSizeMax"] = sizeMax, ["threshold"] = thresholds.MinStartSize },
                    ConfigureFix(new JObject { ["main"] = new JObject { ["startSize"] = 0.5 } }, "Raise the start size."));
            }
            if (sizeMin > thresholds.MaxStartSize)
            {
                Add("START_SIZE_HUGE", SeverityInfo, "modules.main.startSize", $"The start size is at least {sizeMin} units (threshold {thresholds.MaxStartSize}).",
                    new JObject { ["startSizeMin"] = sizeMin, ["threshold"] = thresholds.MaxStartSize });
            }
            double lifetimeMax = CurveMax(main.startLifetime);
            double lifetimeMin = CurveMin(main.startLifetime);
            if (lifetimeMax < thresholds.MinStartLifetime)
            {
                Add("START_LIFETIME_SHORT", SeverityWarning, "modules.main.startLifetime", $"Particles live at most {lifetimeMax} s (threshold {thresholds.MinStartLifetime}): they vanish before they are seen.",
                    new JObject { ["startLifetimeMax"] = lifetimeMax, ["threshold"] = thresholds.MinStartLifetime },
                    ConfigureFix(new JObject { ["main"] = new JObject { ["startLifetime"] = 1 } }, "Raise the start lifetime."));
            }
            if (lifetimeMin > thresholds.MaxStartLifetime)
            {
                Add("START_LIFETIME_LONG", SeverityInfo, "modules.main.startLifetime", $"Particles live at least {lifetimeMin} s (threshold {thresholds.MaxStartLifetime}); long-lived particles pile up to maxParticles.",
                    new JObject { ["startLifetimeMin"] = lifetimeMin, ["threshold"] = thresholds.MaxStartLifetime });
            }

            // Caps and side effects
            if (main.maxParticles > thresholds.MaxParticlesPerSystem)
            {
                Add("MAX_PARTICLES_HIGH", SeverityInfo, "modules.main.maxParticles", $"maxParticles is {main.maxParticles} (threshold {thresholds.MaxParticlesPerSystem}). It is a cap, not a cost: check what the system actually reaches.",
                    new JObject { ["maxParticles"] = main.maxParticles, ["threshold"] = thresholds.MaxParticlesPerSystem },
                    ConfigureFix(new JObject { ["main"] = new JObject { ["maxParticles"] = thresholds.MaxParticlesPerSystem } }, "Lower the cap if the system never needs it."));
            }
            if (main.stopAction != ParticleSystemStopAction.None)
            {
                var effect = main.stopAction == ParticleSystemStopAction.Destroy ? "destroys the GameObject"
                    : main.stopAction == ParticleSystemStopAction.Disable ? "deactivates the GameObject"
                    : "calls OnParticleSystemStopped on the object's scripts";
                Add("STOP_ACTION_SIDE_EFFECT", SeverityWarning, "modules.main.stopAction", $"stopAction is {main.stopAction}: when the system stops, Unity {effect}.",
                    new JObject { ["stopAction"] = main.stopAction.ToString() },
                    ConfigureFix(new JObject { ["main"] = new JObject { ["stopAction"] = "None" } }, "Set stopAction None unless the side effect is intended."));
            }
            if (main.prewarm && !main.loop)
            {
                Add("PREWARM_WITHOUT_LOOP", SeverityInfo, "modules.main.prewarm", "prewarm is on but loop is off: prewarm only affects looping systems.",
                    fix: ConfigureFix(new JObject { ["main"] = new JObject { ["prewarm"] = false } }, "Turn prewarm off, or loop on."));
            }

            // References
            if (main.simulationSpace == ParticleSystemSimulationSpace.Custom && main.customSimulationSpace == null)
            {
                Add("CUSTOM_SPACE_MISSING", SeverityError, "modules.main.customSimulationSpace", "simulationSpace is Custom but no Transform is assigned: Unity falls back to local space.",
                    fix: ConfigureFix(new JObject { ["main"] = new JObject { ["simulationSpace"] = "World" } }, "Use World or Local space, or assign the Transform in the Inspector."));
            }
            var shape = system.shape;
            if (shape.enabled)
            {
                string missing = null;
                switch (shape.shapeType)
                {
                    case ParticleSystemShapeType.Mesh: missing = shape.mesh == null ? "mesh" : null; break;
                    case ParticleSystemShapeType.MeshRenderer: missing = shape.meshRenderer == null ? "meshRenderer" : null; break;
                    case ParticleSystemShapeType.SkinnedMeshRenderer: missing = shape.skinnedMeshRenderer == null ? "skinnedMeshRenderer" : null; break;
                    case ParticleSystemShapeType.Sprite: missing = shape.sprite == null ? "sprite" : null; break;
                    case ParticleSystemShapeType.SpriteRenderer: missing = shape.spriteRenderer == null ? "spriteRenderer" : null; break;
                }
                if (missing != null)
                {
                    Add("SHAPE_REFERENCE_MISSING", SeverityError, "modules.shape." + missing, $"shapeType {shape.shapeType} has no {missing}: Unity emits from nothing.",
                        new JObject { ["shapeType"] = shape.shapeType.ToString() },
                        ConfigureFix(new JObject { ["shape"] = new JObject { ["shapeType"] = "Cone" } }, "Assign the reference, or use a primitive shape."));
                }
            }

            if (system.subEmitters.enabled)
            {
                var stored = ReadStoredSubEmitters(system);
                if (stored.Count == 0)
                    Add("SUB_EMITTERS_EMPTY", SeverityInfo, "modules.subEmitters", "The Sub Emitters module is enabled with an empty list.");
                for (int i = 0; i < stored.Count; i++)
                {
                    var child = stored[i].System;
                    if (child == null)
                    {
                        Add("SUB_EMITTER_MISSING", SeverityError, $"{SubEmittersPath}[{i}].system", $"Sub emitter {i} is a missing reference: it spawns nothing.",
                            fix: Fix("fix_param", "particle_set_sub_emitters", "Replace the list without the broken entry, or point it at an existing system."));
                    }
                    else if (!child.transform.IsChildOf(system.transform))
                    {
                        Add("SUB_EMITTER_NOT_CHILD", SeverityInfo, $"{SubEmittersPath}[{i}].system", $"Sub emitter '{GameObjectFinder.GetPath(child.gameObject)}' is not a child of this system; Unity's Inspector expects it below the system that triggers it.",
                            fix: Fix("fix_param", "gameobject_set_parent", "Parent the sub emitter under this system."));
                    }
                }
            }

            // Cost hints of modules that are expensive or run scripts
            var collision = system.collision;
            if (collision.enabled)
            {
                if (collision.type == ParticleSystemCollisionType.World)
                {
                    Add("COLLISION_WORLD_COST", SeverityInfo, "modules.collision", $"World collision queries the physics scene for particles (quality {collision.quality}); it is one of the most expensive modules.",
                        new JObject { ["quality"] = collision.quality.ToString(), ["maxCollisionShapes"] = collision.maxCollisionShapes });
                }
                else if (collision.planeCount == 0)
                {
                    Add("COLLISION_NO_PLANES", SeverityWarning, "modules.collision", "Collision type Planes has no planes: nothing collides.");
                }
                if (collision.sendCollisionMessages)
                    Add("SCRIPT_CALLBACKS", SeverityInfo, "modules.collision.sendCollisionMessages", "Collision messages are on: OnParticleCollision runs on scripts of the colliding objects.");
            }
            var trigger = system.trigger;
            if (trigger.enabled)
            {
                if (trigger.colliderCount == 0)
                    Add("TRIGGER_NO_COLLIDERS", SeverityWarning, "modules.trigger", "The Triggers module is enabled with no colliders: it never fires.");
                for (int i = 0; i < trigger.colliderCount; i++)
                {
                    if (trigger.GetCollider(i) == null)
                        Add("TRIGGER_COLLIDER_MISSING", SeverityError, $"modules.trigger.colliders[{i}]", $"Trigger collider {i} is a missing reference.");
                }
                if (trigger.enter == ParticleSystemOverlapAction.Callback || trigger.exit == ParticleSystemOverlapAction.Callback ||
                    trigger.inside == ParticleSystemOverlapAction.Callback || trigger.outside == ParticleSystemOverlapAction.Callback)
                    Add("SCRIPT_CALLBACKS", SeverityInfo, "modules.trigger", "A trigger action is Callback: OnParticleTrigger runs on scripts of this object.");
            }
            var lights = system.lights;
            if (lights.enabled)
            {
                if (lights.light == null)
                    Add("LIGHT_MISSING", SeverityError, "modules.lights.light", "The Lights module is enabled with no Light template: no lights are created.");
                else if (lights.maxLights > thresholds.MaxLights)
                    Add("LIGHTS_COST", SeverityInfo, "modules.lights.maxLights", $"Up to {lights.maxLights} real-time lights (threshold {thresholds.MaxLights}); each one costs lighting work per frame.",
                        new JObject { ["maxLights"] = lights.maxLights, ["ratio"] = lights.ratio, ["threshold"] = thresholds.MaxLights });
            }
            var trails = system.trails;
            if (trails.enabled)
            {
                Add("TRAILS_COST", SeverityInfo, "modules.trails", $"Trails build a ribbon mesh every frame (mode {trails.mode}, ratio {trails.ratio}); it scales with the particle count.",
                    new JObject { ["mode"] = trails.mode.ToString(), ["ratio"] = trails.ratio });
            }
        }

        private delegate void AddFinding(string code, string severity, string field, string message, JObject evidence = null, Dictionary<string, object> fix = null);

        private static void CheckShader(Material material, string field, AddFinding add)
        {
            var shader = material.shader;
            var evidence = new JObject { ["material"] = material.name, ["shader"] = shader != null ? shader.name : null };
            if (shader == null || shader.name == "Hidden/InternalErrorShader" || ShaderUtil.ShaderHasError(shader))
            {
                add("SHADER_ERROR", SeverityError, field, $"Material '{material.name}' uses a broken shader ({evidence["shader"]}): Unity draws it magenta.", evidence,
                    DefaultMaterialFix("Use a material whose shader compiles for this project."));
                return;
            }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && !shader.isSupported)
            {
                add("SHADER_UNSUPPORTED", SeverityWarning, field, $"Shader '{shader.name}' is not supported on this graphics device.", evidence);
                return;
            }

            var pipeline = ActivePipelineKind();
            var shaderPipeline = ShaderPipelineKind(shader.name);
            if (shaderPipeline != null && shaderPipeline != pipeline)
            {
                evidence["shaderPipeline"] = shaderPipeline;
                evidence["activePipeline"] = pipeline;
                add("SHADER_PIPELINE_MISMATCH", SeverityWarning, field,
                    $"Shader '{shader.name}' is a {shaderPipeline} shader but the project renders with {pipeline}: particles usually draw magenta.", evidence,
                    DefaultMaterialFix($"Use a {pipeline} particle material (the pipeline's default one is suggested)."));
            }
        }

        /// <summary>The active render pipeline as Built-in, URP, HDRP or SRP (another scriptable pipeline).</summary>
        internal static string ActivePipelineKind()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null)
                return "Built-in";
            var typeName = pipeline.GetType().FullName ?? "";
            if (typeName.IndexOf("Universal", StringComparison.Ordinal) >= 0)
                return "URP";
            if (typeName.IndexOf("HDRenderPipeline", StringComparison.Ordinal) >= 0 || typeName.IndexOf("HighDefinition", StringComparison.Ordinal) >= 0)
                return "HDRP";
            return "SRP";
        }

        /// <summary>The pipeline a shader is known to belong to by its name, or null when the name says nothing.</summary>
        internal static string ShaderPipelineKind(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
                return null;
            if (shaderName.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                return "URP";
            if (shaderName.StartsWith("HDRP/", StringComparison.Ordinal))
                return "HDRP";
            if (shaderName.StartsWith("Particles/Standard", StringComparison.Ordinal) || shaderName.StartsWith("Legacy Shaders/Particles/", StringComparison.Ordinal) ||
                shaderName.StartsWith("Mobile/Particles/", StringComparison.Ordinal))
                return "Built-in";
            return null;
        }

        private static bool EmitsAnything(ParticleSystem system)
        {
            var emission = system.emission;
            if (CurveMax(emission.rateOverTime) > 0 || CurveMax(emission.rateOverDistance) > 0)
                return true;
            var bursts = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(bursts);
            var duration = system.main.duration;
            return bursts.Any(burst => CurveMax(burst.count) > 0 && burst.probability > 0 && burst.time <= duration);
        }

        /// <summary>The largest value a MinMaxCurve can give (curve keys times the multiplier; tangent overshoot is ignored).</summary>
        internal static double CurveMax(ParticleSystem.MinMaxCurve curve) => CurveExtreme(curve, true);

        internal static double CurveMin(ParticleSystem.MinMaxCurve curve) => CurveExtreme(curve, false);

        private static double CurveExtreme(ParticleSystem.MinMaxCurve curve, bool max)
        {
            IEnumerable<double> values;
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    values = new double[] { curve.constant };
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    values = new double[] { curve.constantMin, curve.constantMax };
                    break;
                case ParticleSystemCurveMode.Curve:
                    values = KeyValues(curve.curve, curve.curveMultiplier);
                    break;
                default:
                    values = KeyValues(curve.curveMin, curve.curveMultiplier).Concat(KeyValues(curve.curveMax, curve.curveMultiplier));
                    break;
            }
            var list = values.ToList();
            if (list.Count == 0)
                return 0;
            return max ? list.Max() : list.Min();
        }

        private static IEnumerable<double> KeyValues(AnimationCurve curve, float multiplier) =>
            curve == null ? Enumerable.Empty<double>() : curve.keys.Select(key => (double)key.value * multiplier);

        /// <summary>The largest alpha a MinMaxGradient can give.</summary>
        internal static float MaxAlpha(ParticleSystem.MinMaxGradient gradient)
        {
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return gradient.color.a;
                case ParticleSystemGradientMode.TwoColors:
                    return Mathf.Max(gradient.colorMin.a, gradient.colorMax.a);
                case ParticleSystemGradientMode.TwoGradients:
                    return Mathf.Max(GradientMaxAlpha(gradient.gradientMin), GradientMaxAlpha(gradient.gradientMax));
                default:
                    return GradientMaxAlpha(gradient.gradient);
            }
        }

        private static float GradientMaxAlpha(Gradient gradient) =>
            gradient == null || gradient.alphaKeys.Length == 0 ? 1f : gradient.alphaKeys.Max(key => key.alpha);

        private static Dictionary<string, object> ConfigureFix(JObject modules, string reason)
        {
            var fix = Fix("fix_param", "particle_configure", reason);
            fix["args"] = new JObject { ["configJson"] = new JObject { ["schemaVersion"] = SchemaVersion, ["modules"] = modules } };
            return fix;
        }

        private static Dictionary<string, object> SetRendererFix(JObject renderer, string reason)
        {
            var fix = Fix("fix_param", "particle_set_renderer", reason);
            fix["args"] = new JObject { ["configJson"] = new JObject { ["schemaVersion"] = SchemaVersion, ["renderer"] = renderer } };
            return fix;
        }

        private static Dictionary<string, object> DefaultMaterialFix(string reason)
        {
            var material = DefaultParticleMaterial();
            return material != null
                ? SetRendererFix(new JObject { ["material"] = Codec.AssetToJson(material) }, reason)
                : Fix("fix_param", "particle_set_renderer", reason);
        }
    }
}
#endif

// Producer:Betsy
