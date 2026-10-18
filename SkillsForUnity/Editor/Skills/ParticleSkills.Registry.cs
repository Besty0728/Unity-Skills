#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        private static ParticleRegistry _registry;

        /// <summary>The module/field table, built once per domain on first use (main thread only, like every skill).</summary>
        internal static ParticleRegistry Registry => _registry ?? (_registry = ParticleRegistry.Build());

        /// <summary>
        /// Every module the API knows, in Inspector order. Each field is a strongly typed adapter over Unity's module
        /// struct (`var main = ps.main; main.duration = v;`), never reflection or SerializedObject paths. To extend:
        /// add fields to an existing module with the <see cref="ModuleBuilder"/> helpers, or flip a P1 placeholder to
        /// configurable by giving it fields; capabilities, parsing, validation, readback and dryRun follow automatically.
        /// </summary>
        internal sealed class ParticleRegistry
        {
            public readonly List<ModuleAdapter> Modules = new List<ModuleAdapter>();

            public ModuleAdapter System => Find(SectionSystem, SectionSystem);
            public ModuleAdapter Renderer => Find(SectionRenderer, SectionRenderer);
            public IEnumerable<ModuleAdapter> ParticleModules => Modules.Where(module => module.Section == SectionModules);

            public ModuleAdapter Find(string section, string id) =>
                Modules.FirstOrDefault(module => module.Section == section && string.Equals(module.Id, id, StringComparison.Ordinal));

            /// <summary>A module of the "modules" section by id.</summary>
            public ModuleAdapter Module(string id) => Find(SectionModules, id);

            internal static ParticleRegistry Build()
            {
                var registry = new ParticleRegistry();
                registry.Modules.Add(BuildSystem());
                registry.Modules.Add(BuildMain());
                registry.Modules.Add(BuildEmission());
                registry.Modules.Add(BuildShape());
                registry.Modules.Add(BuildVelocityOverLifetime());
                registry.Modules.Add(BuildLimitVelocityOverLifetime());
                registry.Modules.Add(BuildInheritVelocity());
                registry.Modules.Add(BuildLifetimeByEmitterSpeed());
                registry.Modules.Add(BuildForceOverLifetime());
                registry.Modules.Add(BuildColorOverLifetime());
                registry.Modules.Add(BuildColorBySpeed());
                registry.Modules.Add(BuildSizeOverLifetime());
                registry.Modules.Add(BuildSizeBySpeed());
                registry.Modules.Add(BuildRotationOverLifetime());
                registry.Modules.Add(BuildRotationBySpeed());
                registry.Modules.Add(BuildExternalForces());
                registry.Modules.Add(BuildNoise());
                registry.Modules.Add(Planned("collision", "CollisionModule", b => b.System.collision.enabled, "Collision: world/planes, bounce, dampen, lifetime loss."));
                registry.Modules.Add(Planned("trigger", "TriggerModule", b => b.System.trigger.enabled, "Triggers: collider list and enter/exit behaviour."));
                registry.Modules.Add(BuildSubEmitters());
                registry.Modules.Add(Planned("textureSheetAnimation", "TextureSheetAnimationModule", b => b.System.textureSheetAnimation.enabled, "Texture Sheet Animation: grid/sprites and frame curves."));
                registry.Modules.Add(Planned("lights", "LightsModule", b => b.System.lights.enabled, "Lights: light reference, ratio, range and intensity."));
                registry.Modules.Add(Planned("trails", "TrailModule", b => b.System.trails.enabled, "Trails: mode, ratio, lifetime, width and colour."));
                registry.Modules.Add(BuildCustomData());
                registry.Modules.Add(BuildRenderer());

                foreach (var module in registry.Modules)
                {
                    foreach (var field in module.Fields)
                        field.Module = module;
                }
                return registry;
            }

            // ===================== Builder =====================

            /// <summary>Fluent helpers that keep one field to one line of registry code, typed end to end.</summary>
            internal sealed class ModuleBuilder
            {
                public readonly ModuleAdapter Module;

                public ModuleBuilder(string section, string id, string unityName, string description)
                {
                    Module = new ModuleAdapter { Section = section, Id = id, UnityName = unityName, Description = description };
                }

                private ModuleBuilder Add(FieldSpec field)
                {
                    Module.Fields.Add(field);
                    return this;
                }

                public ModuleBuilder Bool(string name, Func<ParticleBinding, bool> read, Action<ParticleBinding, bool> write, string description) =>
                    Add(new FieldSpec { Name = name, Kind = FieldKind.Bool, Description = description, Read = b => read(b), Write = (b, v) => write(b, (bool)v) });

                public ModuleBuilder Float(string name, Func<ParticleBinding, float> read, Action<ParticleBinding, float> write, string unit,
                    double? min, double? max, string description, bool minExclusive = false) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Float, Unit = unit, Min = min, Max = max, MinExclusive = minExclusive,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (float)v)
                    });

                public ModuleBuilder Int(string name, Func<ParticleBinding, int> read, Action<ParticleBinding, int> write, string unit,
                    double? min, double? max, string description) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Int, Unit = unit, Min = min, Max = max,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (int)v)
                    });

                public ModuleBuilder UInt(string name, Func<ParticleBinding, uint> read, Action<ParticleBinding, uint> write, string description) =>
                    Add(new FieldSpec { Name = name, Kind = FieldKind.UInt, Min = 0, Max = uint.MaxValue, Description = description, Read = b => read(b), Write = (b, v) => write(b, (uint)v) });

                public ModuleBuilder Enum<T>(string name, Func<ParticleBinding, T> read, Action<ParticleBinding, T> write, string description,
                    params T[] writable) where T : struct =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Enum, EnumType = typeof(T),
                        WritableEnumValues = writable != null && writable.Length > 0 ? writable.Select(value => value.ToString()).ToArray() : null,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (T)v)
                    });

                public ModuleBuilder Vector3(string name, Func<ParticleBinding, UnityEngine.Vector3> read, Action<ParticleBinding, UnityEngine.Vector3> write,
                    string unit, double? min, double? max, string description) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Vector3, Unit = unit, Min = min, Max = max,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (UnityEngine.Vector3)v)
                    });

                public ModuleBuilder Vector2(string name, Func<ParticleBinding, UnityEngine.Vector2> read, Action<ParticleBinding, UnityEngine.Vector2> write,
                    string unit, double? min, double? max, string description) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Vector2, Unit = unit, Min = min, Max = max,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (UnityEngine.Vector2)v)
                    });

                public ModuleBuilder Curve(string name, Func<ParticleBinding, ParticleSystem.MinMaxCurve> read, Action<ParticleBinding, ParticleSystem.MinMaxCurve> write,
                    string unit, string keyTime, string description, double? min = null, bool minExclusive = false, double? max = null,
                    params ParticleSystemCurveMode[] modes) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Curve, Unit = unit, Min = min, MinExclusive = minExclusive, Max = max,
                        CurveModes = modes != null && modes.Length > 0 ? modes : null, KeyTimeMeaning = keyTime,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (ParticleSystem.MinMaxCurve)v)
                    });

                public ModuleBuilder Gradient(string name, Func<ParticleBinding, ParticleSystem.MinMaxGradient> read, Action<ParticleBinding, ParticleSystem.MinMaxGradient> write,
                    string keyTime, string description, params ParticleSystemGradientMode[] modes) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Gradient, Unit = "linear RGBA (HDR allowed)", KeyTimeMeaning = keyTime,
                        GradientModes = modes != null && modes.Length > 0 ? modes : null,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, (ParticleSystem.MinMaxGradient)v)
                    });

                public ModuleBuilder Asset<T>(string name, Func<ParticleBinding, T> read, Action<ParticleBinding, T> write, string description)
                    where T : UnityEngine.Object =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Asset, AssetType = typeof(T), Nullable = true,
                        Description = description, Read = b => read(b), Write = (b, v) => write(b, v as T)
                    });

                public ModuleBuilder SortingLayerName(string name, Func<ParticleBinding, string> read, Action<ParticleBinding, string> write, string description) =>
                    Add(new FieldSpec { Name = name, Kind = FieldKind.SortingLayer, Description = description, Read = b => read(b), Write = (b, v) => write(b, (string)v) });

                public ModuleBuilder ReadOnlyInt(string name, Func<ParticleBinding, int> read, string reason, string description) =>
                    Add(new FieldSpec
                    {
                        Name = name, Kind = FieldKind.Int, Writable = false, ReadOnlyReason = reason,
                        Description = description, Read = b => read(b)
                    });

                /// <summary>Declares the fresh-system value of the field added last (see <see cref="FieldSpec.Default"/>).</summary>
                public ModuleBuilder Default(object value)
                {
                    var field = Module.Fields[Module.Fields.Count - 1];
                    field.Default = value;
                    field.HasDefault = true;
                    return this;
                }

                public ModuleBuilder Axis(string flag, string[] uniform, string[] axes)
                {
                    Module.AxisGroups.Add(new AxisGroup { Flag = flag, Uniform = uniform, Axes = axes });
                    return this;
                }

                public ModuleBuilder SameMode(string whenFlag, params string[] fields)
                {
                    Module.SameModeGroups.Add(new SameModeGroup { Fields = fields, WhenFlag = whenFlag });
                    return this;
                }

                public ModuleBuilder Collection(string name, string readSkill, string writeSkill, Func<ParticleBinding, int> count)
                {
                    Module.Collections.Add(new CollectionInfo { Name = name, ReadSkill = readSkill, WriteSkill = writeSkill, Count = count });
                    return this;
                }

                public ModuleBuilder Rule(Action<ModuleCheck> check)
                {
                    var previous = Module.Check;
                    Module.Check = previous == null ? check : context => { previous(context); check(context); };
                    return this;
                }
            }

            private static ModuleBuilder ParticleModule(string id, string unityName, string description) =>
                new ModuleBuilder(SectionModules, id, unityName, description);

            /// <summary>A module whose fields arrive in P1: its enabled flag is readable, writes are refused.</summary>
            private static ModuleAdapter Planned(string id, string unityName, Func<ParticleBinding, bool> enabled, string description)
            {
                var module = new ModuleAdapter
                {
                    Section = SectionModules,
                    Id = id,
                    UnityName = unityName,
                    Phase = "P1",
                    Configurable = false,
                    UnavailableReason = "Planned for P1: readable (enabled) only in this version.",
                    Description = description
                };
                module.Fields.Add(new FieldSpec
                {
                    Name = "enabled",
                    Kind = FieldKind.Bool,
                    Writable = false,
                    ReadOnlyReason = "Module configuration is planned for P1.",
                    Description = "Whether the module is enabled.",
                    Read = b => enabled(b)
                });
                return module;
            }

            /// <summary>A module delivered in P1: configurable like the P0 ones, listed with Phase "P1".</summary>
            private static ModuleBuilder P1Module(string id, string unityName, string description)
            {
                var builder = ParticleModule(id, unityName, description);
                builder.Module.Phase = "P1";
                return builder;
            }

            /// <summary>
            /// The by-speed modules (and Lifetime by Emitter Speed) map a speed range onto their curve or gradient, so
            /// the range has to run upwards. Unity itself accepts a reversed range; the mapping is then meaningless.
            /// </summary>
            private static void CheckSpeedRange(ModuleCheck check)
            {
                if (check.Requested("range", out var value) && value is Vector2 range && range.x > range.y)
                    check.Error("range", $"range.x ({range.x}) must be <= range.y ({range.y}).");
            }

            private static bool IsConstantCurve(object value) =>
                value is ParticleSystem.MinMaxCurve curve && (curve.mode == ParticleSystemCurveMode.Constant || curve.mode == ParticleSystemCurveMode.TwoConstants);

            /// <summary>A Curve-mode MinMaxCurve from (0, start) to (1, end) with multiplier 1, the shape of Unity's default lifetime curves.</summary>
            private static ParticleSystem.MinMaxCurve LinearCurve(float start, float end) =>
                new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, start, 1f, end));

            private const string SystemTime = "normalized system time over the duration (0-1)";
            private const string Lifetime = "normalized particle lifetime (0-1)";
            private const string NormalizedTime = "normalized time (0-1)";
            private const string SpeedRange = "speed mapped over range (range.x -> 0, range.y -> 1)";

            // ===================== system =====================

            private static ModuleAdapter BuildSystem()
            {
                var b = new ModuleBuilder(SectionSystem, SectionSystem, "ParticleSystem", "Properties of the ParticleSystem itself (not of a module).")
                    .Bool("useAutoRandomSeed", p => p.System.useAutoRandomSeed, (p, v) => p.System.useAutoRandomSeed = v,
                        "Pick a new random seed every time the system plays.")
                    .Default(true)
                    .UInt("randomSeed", p => p.System.randomSeed, (p, v) => p.System.randomSeed = v,
                        "Fixed seed used while useAutoRandomSeed is false; send useAutoRandomSeed:false with it.")
                    .Rule(check =>
                    {
                        if (check.Has("randomSeed") && check.Effective("useAutoRandomSeed") is bool auto && auto)
                            check.Error("randomSeed", "randomSeed applies only when useAutoRandomSeed is false, and writing it would switch useAutoRandomSeed off as a side effect. Send \"useAutoRandomSeed\": false in the same patch.");
                    });
                return b.Module;
            }

            // ===================== main =====================

            private static ModuleAdapter BuildMain()
            {
                var b = ParticleModule("main", "MainModule", "Main module: duration, looping, start values, simulation space, limits.")
                    .Float("duration", p => p.System.main.duration, (p, v) => { var m = p.System.main; m.duration = v; },
                        "seconds", 0.05, 100000, "Length of one system cycle.")
                    .Bool("loop", p => p.System.main.loop, (p, v) => { var m = p.System.main; m.loop = v; }, "Restart the cycle when duration ends.")
                    .Default(true)
                    .Bool("prewarm", p => p.System.main.prewarm, (p, v) => { var m = p.System.main; m.prewarm = v; },
                        "Start as if one full cycle already ran (looping systems only).")
                    .Default(false)
                    .Curve("startDelay", p => p.System.main.startDelay, (p, v) => { var m = p.System.main; m.startDelay = v; },
                        "seconds", SystemTime, "Delay before emission starts.", min: 0,
                        modes: new[] { ParticleSystemCurveMode.Constant, ParticleSystemCurveMode.TwoConstants })
                    .Curve("startLifetime", p => p.System.main.startLifetime, (p, v) => { var m = p.System.main; m.startLifetime = v; },
                        "seconds", SystemTime, "Initial lifetime of each particle.", min: 0, minExclusive: true)
                    .Curve("startSpeed", p => p.System.main.startSpeed, (p, v) => { var m = p.System.main; m.startSpeed = v; },
                        "units/second", SystemTime, "Initial speed along the emission direction (negative moves backwards).")
                    .Bool("startSize3D", p => p.System.main.startSize3D, (p, v) => { var m = p.System.main; m.startSize3D = v; },
                        "Use startSizeX/Y/Z instead of startSize.")
                    .Default(false)
                    .Curve("startSize", p => p.System.main.startSize, (p, v) => { var m = p.System.main; m.startSize = v; },
                        "units", SystemTime, "Initial size (applies while startSize3D is false; Unity stores it as startSizeX).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("startSizeX", p => p.System.main.startSizeX, (p, v) => { var m = p.System.main; m.startSizeX = v; },
                        "units", SystemTime, "Initial size along X (startSize3D only).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("startSizeY", p => p.System.main.startSizeY, (p, v) => { var m = p.System.main; m.startSizeY = v; },
                        "units", SystemTime, "Initial size along Y (startSize3D only).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("startSizeZ", p => p.System.main.startSizeZ, (p, v) => { var m = p.System.main; m.startSizeZ = v; },
                        "units", SystemTime, "Initial size along Z (startSize3D only).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Bool("startRotation3D", p => p.System.main.startRotation3D, (p, v) => { var m = p.System.main; m.startRotation3D = v; },
                        "Use startRotationX/Y/Z instead of startRotation.")
                    .Default(false)
                    .Curve("startRotation", p => p.System.main.startRotation, (p, v) => { var m = p.System.main; m.startRotation = v; },
                        "radians", SystemTime, "Initial rotation in RADIANS (the Inspector shows degrees); applies while startRotation3D is false, stored as startRotationZ.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("startRotationX", p => p.System.main.startRotationX, (p, v) => { var m = p.System.main; m.startRotationX = v; },
                        "radians", SystemTime, "Initial rotation around X in radians (startRotation3D only).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("startRotationY", p => p.System.main.startRotationY, (p, v) => { var m = p.System.main; m.startRotationY = v; },
                        "radians", SystemTime, "Initial rotation around Y in radians (startRotation3D only).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("startRotationZ", p => p.System.main.startRotationZ, (p, v) => { var m = p.System.main; m.startRotationZ = v; },
                        "radians", SystemTime, "Initial rotation around Z in radians (startRotation3D only).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Float("flipRotation", p => p.System.main.flipRotation, (p, v) => { var m = p.System.main; m.flipRotation = v; },
                        "ratio", 0, 1, "Fraction of particles that spin the opposite way.")
                    .Gradient("startColor", p => p.System.main.startColor, (p, v) => { var m = p.System.main; m.startColor = v; },
                        SystemTime, "Initial colour of each particle.")
                    .Enum<ParticleSystemGravitySource>("gravitySource", p => p.System.main.gravitySource, (p, v) => { var m = p.System.main; m.gravitySource = v; },
                        "Which physics gravity gravityModifier scales.")
                    .Curve("gravityModifier", p => p.System.main.gravityModifier, (p, v) => { var m = p.System.main; m.gravityModifier = v; },
                        "multiplier of the project gravity", SystemTime, "Scale of Physics.gravity (Physics2D.gravity with gravitySource Physics2D); not a gravity vector.")
                    .Enum<ParticleSystemSimulationSpace>("simulationSpace", p => p.System.main.simulationSpace, (p, v) => { var m = p.System.main; m.simulationSpace = v; },
                        "Space particles live in after emission. Custom (needs a Transform reference) is readable but not writable in this version.",
                        ParticleSystemSimulationSpace.Local, ParticleSystemSimulationSpace.World)
                    .Default(ParticleSystemSimulationSpace.Local)
                    .Float("simulationSpeed", p => p.System.main.simulationSpeed, (p, v) => { var m = p.System.main; m.simulationSpeed = v; },
                        "multiplier", 0, 100, "Playback speed of the whole system.")
                    .Bool("useUnscaledTime", p => p.System.main.useUnscaledTime, (p, v) => { var m = p.System.main; m.useUnscaledTime = v; },
                        "Ignore Time.timeScale.")
                    .Enum<ParticleSystemScalingMode>("scalingMode", p => p.System.main.scalingMode, (p, v) => { var m = p.System.main; m.scalingMode = v; },
                        "How Transform scale affects the system.")
                    .Bool("playOnAwake", p => p.System.main.playOnAwake, (p, v) => { var m = p.System.main; m.playOnAwake = v; },
                        "Start playing when the object is created in Play Mode.")
                    .Enum<ParticleSystemEmitterVelocityMode>("emitterVelocityMode", p => p.System.main.emitterVelocityMode, (p, v) => { var m = p.System.main; m.emitterVelocityMode = v; },
                        "Where the emitter's velocity (for inherit velocity and emission over distance) comes from.")
                    .Default(ParticleSystemEmitterVelocityMode.Rigidbody)
                    .Vector3("emitterVelocity", p => p.System.main.emitterVelocity, (p, v) => { var m = p.System.main; m.emitterVelocity = v; },
                        "units/second", null, null, "Emitter velocity used when emitterVelocityMode is Custom.")
                    .Int("maxParticles", p => p.System.main.maxParticles, (p, v) => { var m = p.System.main; m.maxParticles = v; },
                        "particles", 0, int.MaxValue, "Most particles alive at once; emission stops at the limit.")
                    .Enum<ParticleSystemStopAction>("stopAction", p => p.System.main.stopAction, (p, v) => { var m = p.System.main; m.stopAction = v; },
                        "What happens when the system stops. Only None is writable: Disable/Destroy/Callback deactivate or destroy objects or run scripts.",
                        ParticleSystemStopAction.None)
                    .Enum<ParticleSystemCullingMode>("cullingMode", p => p.System.main.cullingMode, (p, v) => { var m = p.System.main; m.cullingMode = v; },
                        "Behaviour while off screen.")
                    .Enum<ParticleSystemRingBufferMode>("ringBufferMode", p => p.System.main.ringBufferMode, (p, v) => { var m = p.System.main; m.ringBufferMode = v; },
                        "Keep particles alive until replaced when maxParticles is reached.")
                    .Vector2("ringBufferLoopRange", p => p.System.main.ringBufferLoopRange, (p, v) => { var m = p.System.main; m.ringBufferLoopRange = v; },
                        "normalized lifetime", 0, 1, "Lifetime range looped by ringBufferMode LoopUntilReplaced (x <= y).")
                    .Axis("startSize3D", new[] { "startSize" }, new[] { "startSizeX", "startSizeY", "startSizeZ" })
                    .Axis("startRotation3D", new[] { "startRotation" }, new[] { "startRotationX", "startRotationY", "startRotationZ" })
                    .SameMode("startSize3D", "startSizeX", "startSizeY", "startSizeZ")
                    .SameMode("startRotation3D", "startRotationX", "startRotationY", "startRotationZ")
                    .Rule(check =>
                    {
                        if (check.Has("prewarm") && check.Effective("prewarm") is bool prewarm && prewarm && check.Effective("loop") is bool loop && !loop)
                            check.Warn($"{check.PathOf("prewarm")}: prewarm only has an effect while loop is true.");
                        if (check.Has("emitterVelocity") && check.Effective("emitterVelocityMode") is ParticleSystemEmitterVelocityMode mode && mode != ParticleSystemEmitterVelocityMode.Custom)
                            check.Warn($"{check.PathOf("emitterVelocity")}: emitterVelocity only has an effect while emitterVelocityMode is Custom (it is {mode}).");
                        if (check.Requested("ringBufferLoopRange", out var range) && range is Vector2 loopRange && loopRange.x > loopRange.y)
                            check.Error("ringBufferLoopRange", $"ringBufferLoopRange.x ({loopRange.x}) must be <= ringBufferLoopRange.y ({loopRange.y}).");
                    });
                return b.Module;
            }

            // ===================== emission =====================

            private static ModuleAdapter BuildEmission()
            {
                var b = ParticleModule("emission", "EmissionModule", "Emission: rate over time / distance. Bursts are a list with their own skills.")
                    .Bool("enabled", p => p.System.emission.enabled, (p, v) => { var e = p.System.emission; e.enabled = v; }, "Whether the module is enabled.")
                    .Default(true)
                    .Curve("rateOverTime", p => p.System.emission.rateOverTime, (p, v) => { var e = p.System.emission; e.rateOverTime = v; },
                        "particles/second", SystemTime, "Particles emitted per second.", min: 0)
                    .Curve("rateOverDistance", p => p.System.emission.rateOverDistance, (p, v) => { var e = p.System.emission; e.rateOverDistance = v; },
                        "particles/unit", SystemTime, "Particles emitted per unit the emitter moves.", min: 0)
                    .ReadOnlyInt("burstCount", p => p.System.emission.burstCount, "Bursts are written with particle_set_bursts.", "Number of bursts.")
                    .Collection("bursts", "particle_get_bursts", "particle_set_bursts", p => p.System.emission.burstCount);
                return b.Module;
            }

            // ===================== shape =====================

            private static readonly ParticleSystemShapeType[] WritableShapeTypes =
            {
                ParticleSystemShapeType.Sphere, ParticleSystemShapeType.Hemisphere, ParticleSystemShapeType.Cone,
                ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Box, ParticleSystemShapeType.BoxShell,
                ParticleSystemShapeType.BoxEdge, ParticleSystemShapeType.Circle, ParticleSystemShapeType.SingleSidedEdge,
                ParticleSystemShapeType.Donut, ParticleSystemShapeType.Rectangle
            };

            /// <summary>The shapes whose Inspector section has the Arc group (arc, mode, spread, speed).</summary>
            private static readonly ParticleSystemShapeType[] ArcShapeTypes =
            {
                ParticleSystemShapeType.Sphere, ParticleSystemShapeType.Hemisphere, ParticleSystemShapeType.Cone,
                ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut
            };

            /// <summary>The shape types each type-specific field affects (the Inspector shows the field only for these).</summary>
            private static readonly Dictionary<string, ParticleSystemShapeType[]> ShapeFieldTypes = new Dictionary<string, ParticleSystemShapeType[]>
            {
                ["angle"] = new[] { ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume },
                ["length"] = new[] { ParticleSystemShapeType.ConeVolume },
                ["radius"] = new[]
                {
                    ParticleSystemShapeType.Sphere, ParticleSystemShapeType.Hemisphere, ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume,
                    ParticleSystemShapeType.Circle, ParticleSystemShapeType.SingleSidedEdge, ParticleSystemShapeType.Donut
                },
                ["radiusThickness"] = new[]
                {
                    ParticleSystemShapeType.Sphere, ParticleSystemShapeType.Hemisphere, ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume,
                    ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut
                },
                ["radiusMode"] = new[] { ParticleSystemShapeType.SingleSidedEdge },
                ["radiusSpread"] = new[] { ParticleSystemShapeType.SingleSidedEdge },
                ["radiusSpeed"] = new[] { ParticleSystemShapeType.SingleSidedEdge },
                ["arc"] = ArcShapeTypes,
                ["arcMode"] = ArcShapeTypes,
                ["arcSpread"] = ArcShapeTypes,
                ["arcSpeed"] = ArcShapeTypes,
                ["boxThickness"] = new[] { ParticleSystemShapeType.BoxShell, ParticleSystemShapeType.BoxEdge },
                ["donutRadius"] = new[] { ParticleSystemShapeType.Donut },
            };

            private static ModuleAdapter BuildShape()
            {
                var b = ParticleModule("shape", "ShapeModule", "Shape: emission volume or surface and its local transform. Mesh/sprite/texture shapes arrive in P1.")
                    .Bool("enabled", p => p.System.shape.enabled, (p, v) => { var s = p.System.shape; s.enabled = v; }, "Whether the module is enabled.")
                    .Default(true)
                    .Enum("shapeType", p => p.System.shape.shapeType, (p, v) => { var s = p.System.shape; s.shapeType = v; },
                        "Emitter shape. Mesh, MeshRenderer, SkinnedMeshRenderer, Sprite and SpriteRenderer need object references (P1) and are readable only.",
                        WritableShapeTypes)
                    .Default(ParticleSystemShapeType.Cone)
                    .Float("angle", p => p.System.shape.angle, (p, v) => { var s = p.System.shape; s.angle = v; },
                        "degrees", 0, 90, "Cone opening angle (Cone, ConeVolume).")
                    .Float("radius", p => p.System.shape.radius, (p, v) => { var s = p.System.shape; s.radius = v; },
                        "units", 0.0001, null, "Shape radius (Sphere, Hemisphere, Cone, ConeVolume, Circle, SingleSidedEdge, Donut).")
                    .Enum<ParticleSystemShapeMultiModeValue>("radiusMode", p => p.System.shape.radiusMode, (p, v) => { var s = p.System.shape; s.radiusMode = v; },
                        "How emission positions move along the radius (SingleSidedEdge): Random, Loop, PingPong or BurstSpread.")
                    .Default(ParticleSystemShapeMultiModeValue.Random)
                    .Float("radiusSpread", p => p.System.shape.radiusSpread, (p, v) => { var s = p.System.shape; s.radiusSpread = v; },
                        "ratio", 0, 1, "Discrete radius intervals emission is snapped to (SingleSidedEdge; 0 = continuous).")
                    .Curve("radiusSpeed", p => p.System.shape.radiusSpeed, (p, v) => { var s = p.System.shape; s.radiusSpeed = v; },
                        "2 x units/second", SystemTime, "How fast the emission position moves along the edge in radiusMode Loop or PingPong: 2 x this value units per second, whatever the radius (SingleSidedEdge).",
                        modes: new[] { ParticleSystemCurveMode.Constant, ParticleSystemCurveMode.Curve })
                    .Float("radiusThickness", p => p.System.shape.radiusThickness, (p, v) => { var s = p.System.shape; s.radiusThickness = v; },
                        "ratio", 0, 1, "0 emits from the surface, 1 from the whole volume.")
                    .Float("arc", p => p.System.shape.arc, (p, v) => { var s = p.System.shape; s.arc = v; },
                        "degrees", 0, 360, "Portion of the circle that emits (Cone, ConeVolume, Circle, Donut).")
                    .Enum<ParticleSystemShapeMultiModeValue>("arcMode", p => p.System.shape.arcMode, (p, v) => { var s = p.System.shape; s.arcMode = v; },
                        "How emission positions move around the arc.")
                    .Default(ParticleSystemShapeMultiModeValue.Random)
                    .Float("arcSpread", p => p.System.shape.arcSpread, (p, v) => { var s = p.System.shape; s.arcSpread = v; },
                        "ratio", 0, 1, "Discrete arc intervals emission is snapped to (0 = continuous).")
                    .Curve("arcSpeed", p => p.System.shape.arcSpeed, (p, v) => { var s = p.System.shape; s.arcSpeed = v; },
                        "x 360 degrees/second", SystemTime, "How fast the emission position moves around the arc in arcMode Loop or PingPong: 360 degrees per second per unit, wrapping (or bouncing) at the ends of the arc whatever its size.",
                        modes: new[] { ParticleSystemCurveMode.Constant, ParticleSystemCurveMode.Curve })
                    .Float("length", p => p.System.shape.length, (p, v) => { var s = p.System.shape; s.length = v; },
                        "units", 0, null, "Cone length (ConeVolume).")
                    .Vector3("boxThickness", p => p.System.shape.boxThickness, (p, v) => { var s = p.System.shape; s.boxThickness = v; },
                        "ratio", 0, 1, "Thickness of the emitting shell or edges per axis (BoxShell, BoxEdge): 0 emits from the surface, 1 from the whole box.")
                    .Float("donutRadius", p => p.System.shape.donutRadius, (p, v) => { var s = p.System.shape; s.donutRadius = v; },
                        "units", 0.0001, null, "Thickness of the ring (Donut).")
                    .Vector3("position", p => p.System.shape.position, (p, v) => { var s = p.System.shape; s.position = v; },
                        "units", null, null, "Shape offset in the system's local space.")
                    .Vector3("rotation", p => p.System.shape.rotation, (p, v) => { var s = p.System.shape; s.rotation = v; },
                        "degrees", null, null, "Shape rotation as Euler angles in DEGREES (unlike main.startRotation).")
                    .Vector3("scale", p => p.System.shape.scale, (p, v) => { var s = p.System.shape; s.scale = v; },
                        "multiplier", null, null, "Shape scale.")
                    .Bool("alignToDirection", p => p.System.shape.alignToDirection, (p, v) => { var s = p.System.shape; s.alignToDirection = v; },
                        "Orient particles along their emission direction.")
                    .Float("randomDirectionAmount", p => p.System.shape.randomDirectionAmount, (p, v) => { var s = p.System.shape; s.randomDirectionAmount = v; },
                        "ratio", 0, 1, "Blend towards a random direction.")
                    .Float("sphericalDirectionAmount", p => p.System.shape.sphericalDirectionAmount, (p, v) => { var s = p.System.shape; s.sphericalDirectionAmount = v; },
                        "ratio", 0, 1, "Blend towards a direction away from the shape centre.")
                    .Float("randomPositionAmount", p => p.System.shape.randomPositionAmount, (p, v) => { var s = p.System.shape; s.randomPositionAmount = v; },
                        "units", 0, null, "Random offset added to the emission position.")
                    .Rule(check =>
                    {
                        if (!(check.Effective("shapeType") is ParticleSystemShapeType type))
                            return;
                        foreach (var pair in ShapeFieldTypes)
                        {
                            if (check.Has(pair.Key) && !pair.Value.Contains(type))
                                check.Warn($"{check.PathOf(pair.Key)}: has no effect for shapeType {type} (it applies to {string.Join(", ", pair.Value)}).");
                        }
                    })
                    .Rule(check =>
                    {
                        foreach (var (speed, mode) in new[] { ("arcSpeed", "arcMode"), ("radiusSpeed", "radiusMode") })
                        {
                            if (check.Has(speed) && check.Effective(mode) is ParticleSystemShapeMultiModeValue current &&
                                current != ParticleSystemShapeMultiModeValue.Loop && current != ParticleSystemShapeMultiModeValue.PingPong)
                                check.Warn($"{check.PathOf(speed)}: {speed} only has an effect while {mode} is Loop or PingPong (it is {current}).");
                        }
                    });
                return b.Module;
            }

            // ===================== velocityOverLifetime =====================

            private static ModuleAdapter BuildVelocityOverLifetime()
            {
                var b = ParticleModule("velocityOverLifetime", "VelocityOverLifetimeModule", "Velocity over Lifetime: linear velocity curves, space, orbital velocity (with its centre offset), radial velocity and speed modifier.")
                    .Bool("enabled", p => p.System.velocityOverLifetime.enabled, (p, v) => { var m = p.System.velocityOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Curve("x", p => p.System.velocityOverLifetime.x, (p, v) => { var m = p.System.velocityOverLifetime; m.x = v; },
                        "units/second", Lifetime, "Velocity along X (x, y and z must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("y", p => p.System.velocityOverLifetime.y, (p, v) => { var m = p.System.velocityOverLifetime; m.y = v; },
                        "units/second", Lifetime, "Velocity along Y.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("z", p => p.System.velocityOverLifetime.z, (p, v) => { var m = p.System.velocityOverLifetime; m.z = v; },
                        "units/second", Lifetime, "Velocity along Z.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Enum("space", p => p.System.velocityOverLifetime.space, (p, v) => { var m = p.System.velocityOverLifetime; m.space = v; },
                        "Space of x/y/z.", ParticleSystemSimulationSpace.Local, ParticleSystemSimulationSpace.World)
                    .Curve("orbitalX", p => p.System.velocityOverLifetime.orbitalX, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalX = v; },
                        "radians/second", Lifetime, "Angular velocity around the system centre about the X axis (orbitalX, orbitalY and orbitalZ must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("orbitalY", p => p.System.velocityOverLifetime.orbitalY, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalY = v; },
                        "radians/second", Lifetime, "Angular velocity around the system centre about the Y axis.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("orbitalZ", p => p.System.velocityOverLifetime.orbitalZ, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalZ = v; },
                        "radians/second", Lifetime, "Angular velocity around the system centre about the Z axis.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("orbitalOffsetX", p => p.System.velocityOverLifetime.orbitalOffsetX, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalOffsetX = v; },
                        "units", Lifetime, "Offset of the orbit centre along X (orbitalOffsetX, orbitalOffsetY and orbitalOffsetZ must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("orbitalOffsetY", p => p.System.velocityOverLifetime.orbitalOffsetY, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalOffsetY = v; },
                        "units", Lifetime, "Offset of the orbit centre along Y.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("orbitalOffsetZ", p => p.System.velocityOverLifetime.orbitalOffsetZ, (p, v) => { var m = p.System.velocityOverLifetime; m.orbitalOffsetZ = v; },
                        "units", Lifetime, "Offset of the orbit centre along Z.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("radial", p => p.System.velocityOverLifetime.radial, (p, v) => { var m = p.System.velocityOverLifetime; m.radial = v; },
                        "units/second", Lifetime, "Velocity away from the system centre.")
                    .Curve("speedModifier", p => p.System.velocityOverLifetime.speedModifier, (p, v) => { var m = p.System.velocityOverLifetime; m.speedModifier = v; },
                        "multiplier", Lifetime, "Speed multiplier that keeps the direction.")
                    .SameMode(null, "x", "y", "z")
                    .SameMode(null, "orbitalX", "orbitalY", "orbitalZ")
                    .SameMode(null, "orbitalOffsetX", "orbitalOffsetY", "orbitalOffsetZ")
                    .Rule(check =>
                    {
                        var offsets = new[] { "orbitalOffsetX", "orbitalOffsetY", "orbitalOffsetZ" }.Where(check.Has).ToList();
                        bool noOrbit = new[] { "orbitalX", "orbitalY", "orbitalZ" }.All(name =>
                            check.Effective(name) is ParticleSystem.MinMaxCurve orbital && orbital.mode == ParticleSystemCurveMode.Constant && orbital.constant == 0f);
                        if (offsets.Count > 0 && noOrbit)
                            check.Warn($"{check.PathOf(offsets[0])}: the orbit centre offset has no effect while orbitalX, orbitalY and orbitalZ are all 0.");
                    });
                return b.Module;
            }

            // ===================== limitVelocityOverLifetime =====================

            private static ModuleAdapter BuildLimitVelocityOverLifetime()
            {
                var b = P1Module("limitVelocityOverLifetime", "LimitVelocityOverLifetimeModule", "Limit Velocity over Lifetime: a speed limit (one for all axes or one per axis), dampen and drag.")
                    .Bool("enabled", p => p.System.limitVelocityOverLifetime.enabled, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.limitVelocityOverLifetime.separateAxes, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.separateAxes = v; },
                        "Limit each axis on its own (limitX/Y/Z and space) instead of the overall speed (limit).")
                    .Default(false)
                    .Curve("limit", p => p.System.limitVelocityOverLifetime.limit, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.limit = v; },
                        "units/second", Lifetime, "Speed limit while separateAxes is false (stored apart from limitX).", min: 0)
                    .Curve("limitX", p => p.System.limitVelocityOverLifetime.limitX, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.limitX = v; },
                        "units/second", Lifetime, "Speed limit along X (separateAxes only; limitX, limitY and limitZ must share one mode).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("limitY", p => p.System.limitVelocityOverLifetime.limitY, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.limitY = v; },
                        "units/second", Lifetime, "Speed limit along Y (separateAxes only).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("limitZ", p => p.System.limitVelocityOverLifetime.limitZ, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.limitZ = v; },
                        "units/second", Lifetime, "Speed limit along Z (separateAxes only).", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Enum("space", p => p.System.limitVelocityOverLifetime.space, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.space = v; },
                        "Space of limitX/Y/Z (separateAxes only).", ParticleSystemSimulationSpace.Local, ParticleSystemSimulationSpace.World)
                    .Float("dampen", p => p.System.limitVelocityOverLifetime.dampen, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.dampen = v; },
                        "ratio", 0, 1, "How much of the velocity above the limit is removed on each step (0.5 dampens the excess by 50%, 1 clamps hard); 0, the default of a new system, disables the speed limit.")
                    .Default(0f)
                    .Curve("drag", p => p.System.limitVelocityOverLifetime.drag, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.drag = v; },
                        "drag coefficient", Lifetime, "Drag applied to each particle over its lifetime; multiplyDragByParticleSize and multiplyDragByParticleVelocity (both on in a new system) also scale it by the particle's size and speed.", min: 0)
                    .Bool("multiplyDragByParticleSize", p => p.System.limitVelocityOverLifetime.multiplyDragByParticleSize, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.multiplyDragByParticleSize = v; },
                        "Scale the drag by the particle size.")
                    .Bool("multiplyDragByParticleVelocity", p => p.System.limitVelocityOverLifetime.multiplyDragByParticleVelocity, (p, v) => { var m = p.System.limitVelocityOverLifetime; m.multiplyDragByParticleVelocity = v; },
                        "Scale the drag by the particle speed.")
                    .Axis("separateAxes", new[] { "limit" }, new[] { "limitX", "limitY", "limitZ" })
                    .SameMode("separateAxes", "limitX", "limitY", "limitZ")
                    .Rule(check =>
                    {
                        if (check.Has("space") && check.Effective("separateAxes") is bool separate && !separate)
                            check.Warn($"{check.PathOf("space")}: space only applies while separateAxes is true.");
                        var limits = new[] { "limit", "limitX", "limitY", "limitZ" }.Where(check.Has).ToList();
                        if (limits.Count > 0 && check.Effective("dampen") is float dampen && dampen <= 0f)
                            check.Warn($"{check.PathOf(limits[0])}: dampen is 0, so the speed limit removes nothing; raise dampen (1 clamps hard). Drag works regardless.");
                    });
                return b.Module;
            }

            // ===================== inheritVelocity =====================

            private static ModuleAdapter BuildInheritVelocity()
            {
                var b = P1Module("inheritVelocity", "InheritVelocityModule", "Inherit Velocity: the part of the emitter's velocity that particles inherit (see main.emitterVelocityMode for where that velocity comes from).")
                    .Bool("enabled", p => p.System.inheritVelocity.enabled, (p, v) => { var m = p.System.inheritVelocity; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Enum<ParticleSystemInheritVelocityMode>("mode", p => p.System.inheritVelocity.mode, (p, v) => { var m = p.System.inheritVelocity; m.mode = v; },
                        "Initial applies the emitter velocity once when a particle is born; Current applies it on every frame.")
                    .Curve("curve", p => p.System.inheritVelocity.curve, (p, v) => { var m = p.System.inheritVelocity; m.curve = v; },
                        "multiplier of the emitter velocity", Lifetime, "Proportion of the emitter velocity a particle inherits (may be negative).")
                    .Rule(check =>
                    {
                        if (!check.Has("mode") && !check.Has("curve"))
                            return;
                        if (check.Effective(ParticleSkills.Registry.Module("main"), "simulationSpace") is ParticleSystemSimulationSpace space && space == ParticleSystemSimulationSpace.Local)
                            check.Warn($"{check.PathOf(check.Has("curve") ? "curve" : "mode")}: Inherit Velocity has no effect while main.simulationSpace is Local; use World.");
                    });
                return b.Module;
            }

            // ===================== lifetimeByEmitterSpeed =====================

            private static ModuleAdapter BuildLifetimeByEmitterSpeed()
            {
                var b = P1Module("lifetimeByEmitterSpeed", "LifetimeByEmitterSpeedModule", "Lifetime by Emitter Speed: scales the start lifetime of a particle by the emitter's speed when it is born.")
                    .Bool("enabled", p => p.System.lifetimeByEmitterSpeed.enabled, (p, v) => { var m = p.System.lifetimeByEmitterSpeed; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Curve("curve", p => p.System.lifetimeByEmitterSpeed.curve, (p, v) => { var m = p.System.lifetimeByEmitterSpeed; m.curve = v; },
                        "multiplier of the start lifetime", SpeedRange, "Factor applied to the start lifetime; a Curve or TwoCurves reads the emitter speed mapped over range.", min: 0)
                    .Default(new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f, 0f, -0.8f), new Keyframe(1f, 0.2f, -0.8f, 0f))))
                    .Vector2("range", p => p.System.lifetimeByEmitterSpeed.range, (p, v) => { var m = p.System.lifetimeByEmitterSpeed; m.range = v; },
                        "units/second", 0, null, "Emitter speed range mapped onto the curve (x -> 0, y -> 1; x <= y); ignored by a constant multiplier.")
                    .Rule(check =>
                    {
                        CheckSpeedRange(check);
                        if (check.Has("range") && IsConstantCurve(check.Effective("curve")))
                            check.Warn($"{check.PathOf("range")}: range only maps the emitter speed onto a Curve or TwoCurves multiplier; a constant multiplier ignores it.");
                    });
                return b.Module;
            }

            // ===================== forceOverLifetime =====================

            private static ModuleAdapter BuildForceOverLifetime()
            {
                var b = P1Module("forceOverLifetime", "ForceOverLifetimeModule", "Force over Lifetime: a force applied to each particle, per axis, in local or world space.")
                    .Bool("enabled", p => p.System.forceOverLifetime.enabled, (p, v) => { var m = p.System.forceOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Curve("x", p => p.System.forceOverLifetime.x, (p, v) => { var m = p.System.forceOverLifetime; m.x = v; },
                        "units/second squared", Lifetime, "Force along X (x, y and z must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("y", p => p.System.forceOverLifetime.y, (p, v) => { var m = p.System.forceOverLifetime; m.y = v; },
                        "units/second squared", Lifetime, "Force along Y.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("z", p => p.System.forceOverLifetime.z, (p, v) => { var m = p.System.forceOverLifetime; m.z = v; },
                        "units/second squared", Lifetime, "Force along Z.")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Enum("space", p => p.System.forceOverLifetime.space, (p, v) => { var m = p.System.forceOverLifetime; m.space = v; },
                        "Space of x/y/z.", ParticleSystemSimulationSpace.Local, ParticleSystemSimulationSpace.World)
                    .Bool("randomized", p => p.System.forceOverLifetime.randomized, (p, v) => { var m = p.System.forceOverLifetime; m.randomized = v; },
                        "Pick a new random force between the two values on every frame (TwoConstants or TwoCurves only).")
                    .Default(false)
                    .SameMode(null, "x", "y", "z")
                    .Rule(check =>
                    {
                        if ((check.Has("randomized") || check.Has("x") || check.Has("y") || check.Has("z")) &&
                            check.Effective("randomized") is bool randomized && randomized &&
                            check.Effective("x") is ParticleSystem.MinMaxCurve force &&
                            (force.mode == ParticleSystemCurveMode.Constant || force.mode == ParticleSystemCurveMode.Curve))
                            check.Warn($"{check.PathOf("randomized")}: randomized only has an effect with TwoConstants or TwoCurves force (it is {force.mode}).");
                    });
                return b.Module;
            }

            // ===================== colorOverLifetime =====================

            private static ModuleAdapter BuildColorOverLifetime()
            {
                var b = ParticleModule("colorOverLifetime", "ColorOverLifetimeModule", "Color over Lifetime: a gradient multiplied with the start colour.")
                    .Bool("enabled", p => p.System.colorOverLifetime.enabled, (p, v) => { var m = p.System.colorOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Gradient("color", p => p.System.colorOverLifetime.color, (p, v) => { var m = p.System.colorOverLifetime; m.color = v; },
                        Lifetime, "Colour and alpha over the particle lifetime (Gradient or TwoGradients, as in the Inspector).",
                        ParticleSystemGradientMode.Gradient, ParticleSystemGradientMode.TwoGradients);
                return b.Module;
            }

            // ===================== colorBySpeed =====================

            private static ModuleAdapter BuildColorBySpeed()
            {
                var b = P1Module("colorBySpeed", "ColorBySpeedModule", "Color by Speed: a gradient multiplied with the particle colour, read at the particle's speed mapped over range.")
                    .Bool("enabled", p => p.System.colorBySpeed.enabled, (p, v) => { var m = p.System.colorBySpeed; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Gradient("color", p => p.System.colorBySpeed.color, (p, v) => { var m = p.System.colorBySpeed; m.color = v; },
                        SpeedRange, "Colour and alpha over the speed range (Gradient or TwoGradients, as in the Inspector).",
                        ParticleSystemGradientMode.Gradient, ParticleSystemGradientMode.TwoGradients)
                    .Vector2("range", p => p.System.colorBySpeed.range, (p, v) => { var m = p.System.colorBySpeed; m.range = v; },
                        "units/second", 0, null, "Speed range mapped onto the gradient (x -> 0, y -> 1; x <= y).")
                    .Rule(CheckSpeedRange);
                return b.Module;
            }

            // ===================== sizeOverLifetime =====================

            private static ModuleAdapter BuildSizeOverLifetime()
            {
                var curves = new[] { ParticleSystemCurveMode.Curve, ParticleSystemCurveMode.TwoCurves };
                var b = ParticleModule("sizeOverLifetime", "SizeOverLifetimeModule", "Size over Lifetime: size multiplier curves.")
                    .Bool("enabled", p => p.System.sizeOverLifetime.enabled, (p, v) => { var m = p.System.sizeOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.sizeOverLifetime.separateAxes, (p, v) => { var m = p.System.sizeOverLifetime; m.separateAxes = v; },
                        "Use x/y/z instead of size.")
                    .Default(false)
                    .Curve("size", p => p.System.sizeOverLifetime.size, (p, v) => { var m = p.System.sizeOverLifetime; m.size = v; },
                        "multiplier of the start size", Lifetime, "Size multiplier (while separateAxes is false; stored as x).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("x", p => p.System.sizeOverLifetime.x, (p, v) => { var m = p.System.sizeOverLifetime; m.x = v; },
                        "multiplier of the start size", Lifetime, "Size multiplier along X (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("y", p => p.System.sizeOverLifetime.y, (p, v) => { var m = p.System.sizeOverLifetime; m.y = v; },
                        "multiplier of the start size", Lifetime, "Size multiplier along Y (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("z", p => p.System.sizeOverLifetime.z, (p, v) => { var m = p.System.sizeOverLifetime; m.z = v; },
                        "multiplier of the start size", Lifetime, "Size multiplier along Z (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Axis("separateAxes", new[] { "size" }, new[] { "x", "y", "z" })
                    .SameMode("separateAxes", "x", "y", "z");
                return b.Module;
            }

            // ===================== sizeBySpeed =====================

            private static ModuleAdapter BuildSizeBySpeed()
            {
                var curves = new[] { ParticleSystemCurveMode.Curve, ParticleSystemCurveMode.TwoCurves };
                var b = P1Module("sizeBySpeed", "SizeBySpeedModule", "Size by Speed: a size multiplier curve read at the particle's speed mapped over range.")
                    .Bool("enabled", p => p.System.sizeBySpeed.enabled, (p, v) => { var m = p.System.sizeBySpeed; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.sizeBySpeed.separateAxes, (p, v) => { var m = p.System.sizeBySpeed; m.separateAxes = v; },
                        "Use x/y/z instead of size.")
                    .Default(false)
                    .Curve("size", p => p.System.sizeBySpeed.size, (p, v) => { var m = p.System.sizeBySpeed; m.size = v; },
                        "multiplier of the start size", SpeedRange, "Size multiplier (while separateAxes is false; stored as x).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("x", p => p.System.sizeBySpeed.x, (p, v) => { var m = p.System.sizeBySpeed; m.x = v; },
                        "multiplier of the start size", SpeedRange, "Size multiplier along X (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("y", p => p.System.sizeBySpeed.y, (p, v) => { var m = p.System.sizeBySpeed; m.y = v; },
                        "multiplier of the start size", SpeedRange, "Size multiplier along Y (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Curve("z", p => p.System.sizeBySpeed.z, (p, v) => { var m = p.System.sizeBySpeed; m.z = v; },
                        "multiplier of the start size", SpeedRange, "Size multiplier along Z (separateAxes only).", modes: curves)
                    .Default(LinearCurve(0f, 1f))
                    .Vector2("range", p => p.System.sizeBySpeed.range, (p, v) => { var m = p.System.sizeBySpeed; m.range = v; },
                        "units/second", 0, null, "Speed range mapped onto the curves (x -> 0, y -> 1; x <= y).")
                    .Axis("separateAxes", new[] { "size" }, new[] { "x", "y", "z" })
                    .SameMode("separateAxes", "x", "y", "z")
                    .Rule(CheckSpeedRange);
                return b.Module;
            }

            // ===================== rotationOverLifetime =====================

            private static ModuleAdapter BuildRotationOverLifetime()
            {
                var b = P1Module("rotationOverLifetime", "RotationOverLifetimeModule", "Rotation over Lifetime: angular velocity of each particle in RADIANS per second (the Inspector shows degrees).")
                    .Bool("enabled", p => p.System.rotationOverLifetime.enabled, (p, v) => { var m = p.System.rotationOverLifetime; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.rotationOverLifetime.separateAxes, (p, v) => { var m = p.System.rotationOverLifetime; m.separateAxes = v; },
                        "Rotate around X and Y as well; while false only z (the Inspector's \"Angular Velocity\") applies.")
                    .Default(false)
                    .Curve("x", p => p.System.rotationOverLifetime.x, (p, v) => { var m = p.System.rotationOverLifetime; m.x = v; },
                        "radians/second", Lifetime, "Angular velocity around X (separateAxes only; x, y and z must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("y", p => p.System.rotationOverLifetime.y, (p, v) => { var m = p.System.rotationOverLifetime; m.y = v; },
                        "radians/second", Lifetime, "Angular velocity around Y (separateAxes only).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("z", p => p.System.rotationOverLifetime.z, (p, v) => { var m = p.System.rotationOverLifetime; m.z = v; },
                        "radians/second", Lifetime, "Angular velocity around Z; the only axis while separateAxes is false (the Inspector's \"Angular Velocity\").")
                    .Default(new ParticleSystem.MinMaxCurve(Mathf.PI / 4f))
                    .Axis("separateAxes", new string[0], new[] { "x", "y" })
                    .SameMode("separateAxes", "x", "y", "z");
                return b.Module;
            }

            // ===================== rotationBySpeed =====================

            private static ModuleAdapter BuildRotationBySpeed()
            {
                var b = P1Module("rotationBySpeed", "RotationBySpeedModule", "Rotation by Speed: angular velocity in RADIANS per second (the Inspector shows degrees), read at the particle's speed mapped over range.")
                    .Bool("enabled", p => p.System.rotationBySpeed.enabled, (p, v) => { var m = p.System.rotationBySpeed; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.rotationBySpeed.separateAxes, (p, v) => { var m = p.System.rotationBySpeed; m.separateAxes = v; },
                        "Rotate around X and Y as well; while false only z (the Inspector's \"Angular Velocity\") applies.")
                    .Default(false)
                    .Curve("x", p => p.System.rotationBySpeed.x, (p, v) => { var m = p.System.rotationBySpeed; m.x = v; },
                        "radians/second", SpeedRange, "Angular velocity around X (separateAxes only; x, y and z must share one mode).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("y", p => p.System.rotationBySpeed.y, (p, v) => { var m = p.System.rotationBySpeed; m.y = v; },
                        "radians/second", SpeedRange, "Angular velocity around Y (separateAxes only).")
                    .Default(new ParticleSystem.MinMaxCurve(0f))
                    .Curve("z", p => p.System.rotationBySpeed.z, (p, v) => { var m = p.System.rotationBySpeed; m.z = v; },
                        "radians/second", SpeedRange, "Angular velocity around Z; the only axis while separateAxes is false (the Inspector's \"Angular Velocity\").")
                    .Default(new ParticleSystem.MinMaxCurve(Mathf.PI / 4f))
                    .Vector2("range", p => p.System.rotationBySpeed.range, (p, v) => { var m = p.System.rotationBySpeed; m.range = v; },
                        "units/second", 0, null, "Speed range mapped onto the curves (x -> 0, y -> 1; x <= y); ignored by constant angular velocity.")
                    .Axis("separateAxes", new string[0], new[] { "x", "y" })
                    .SameMode("separateAxes", "x", "y", "z")
                    .Rule(check =>
                    {
                        CheckSpeedRange(check);
                        if (check.Has("range") && IsConstantCurve(check.Effective("z")))
                            check.Warn($"{check.PathOf("range")}: range only maps the speed onto Curve or TwoCurves angular velocity; a constant angular velocity ignores it.");
                    });
                return b.Module;
            }

            // ===================== externalForces =====================

            private static ModuleAdapter BuildExternalForces()
            {
                var b = P1Module("externalForces", "ExternalForcesModule", "External Forces: how strongly force fields and wind zones act on the system. The force-field list and influence mask are not writable in this version.")
                    .Bool("enabled", p => p.System.externalForces.enabled, (p, v) => { var m = p.System.externalForces; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Curve("multiplierCurve", p => p.System.externalForces.multiplierCurve, (p, v) => { var m = p.System.externalForces; m.multiplierCurve = v; },
                        "multiplier of the external force", Lifetime, "Scales the external forces; a curve is applied over the lifetime of each particle (its multiplier is Unity's \"multiplier\").", min: 0)
                    .Enum("influenceFilter", p => p.System.externalForces.influenceFilter, (p, v) => { var m = p.System.externalForces; m.influenceFilter = v; },
                        "Which force fields affect the system. Only LayerMask is writable here: List and LayerMaskAndList need the force-field list, which this version cannot write.",
                        ParticleSystemGameObjectFilter.LayerMask)
                    .ReadOnlyInt("influenceCount", p => p.System.externalForces.influenceCount,
                        "The force-field list is not writable in this version.", "Number of force fields in the influence list.");
                return b.Module;
            }

            // ===================== noise =====================

            private static ModuleAdapter BuildNoise()
            {
                var b = ParticleModule("noise", "NoiseModule", "Noise: turbulent movement, with optional effect on rotation and size.")
                    .Bool("enabled", p => p.System.noise.enabled, (p, v) => { var m = p.System.noise; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .Bool("separateAxes", p => p.System.noise.separateAxes, (p, v) => { var m = p.System.noise; m.separateAxes = v; },
                        "Use strengthX/Y/Z and remapX/Y/Z instead of strength and remap.")
                    .Default(false)
                    .Curve("strength", p => p.System.noise.strength, (p, v) => { var m = p.System.noise; m.strength = v; },
                        "units", Lifetime, "Noise strength (while separateAxes is false; stored as strengthX).")
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("strengthX", p => p.System.noise.strengthX, (p, v) => { var m = p.System.noise; m.strengthX = v; },
                        "units", Lifetime, "Noise strength on X (separateAxes only).")
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("strengthY", p => p.System.noise.strengthY, (p, v) => { var m = p.System.noise; m.strengthY = v; },
                        "units", Lifetime, "Noise strength on Y (separateAxes only).")
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Curve("strengthZ", p => p.System.noise.strengthZ, (p, v) => { var m = p.System.noise; m.strengthZ = v; },
                        "units", Lifetime, "Noise strength on Z (separateAxes only).")
                    .Default(new ParticleSystem.MinMaxCurve(1f))
                    .Float("frequency", p => p.System.noise.frequency, (p, v) => { var m = p.System.noise; m.frequency = v; },
                        "1/units", 0.0001, null, "Noise frequency: low is smooth, high is erratic.")
                    .Curve("scrollSpeed", p => p.System.noise.scrollSpeed, (p, v) => { var m = p.System.noise; m.scrollSpeed = v; },
                        "noise field scroll per second", NormalizedTime, "Moves the noise field over time.")
                    .Bool("damping", p => p.System.noise.damping, (p, v) => { var m = p.System.noise; m.damping = v; },
                        "Scale strength with frequency.")
                    .Int("octaveCount", p => p.System.noise.octaveCount, (p, v) => { var m = p.System.noise; m.octaveCount = v; },
                        "octaves", 1, 4, "Layers of noise combined.")
                    .Float("octaveMultiplier", p => p.System.noise.octaveMultiplier, (p, v) => { var m = p.System.noise; m.octaveMultiplier = v; },
                        "ratio", 0, 1, "Strength factor of each further octave.")
                    .Float("octaveScale", p => p.System.noise.octaveScale, (p, v) => { var m = p.System.noise; m.octaveScale = v; },
                        "multiplier", 1, 4, "Frequency factor of each further octave.")
                    .Enum<ParticleSystemNoiseQuality>("quality", p => p.System.noise.quality, (p, v) => { var m = p.System.noise; m.quality = v; },
                        "Noise quality (1D/2D/3D).")
                    .Bool("remapEnabled", p => p.System.noise.remapEnabled, (p, v) => { var m = p.System.noise; m.remapEnabled = v; },
                        "Remap the final noise values with remap / remapX/Y/Z.")
                    .Curve("remap", p => p.System.noise.remap, (p, v) => { var m = p.System.noise; m.remap = v; },
                        "noise value", "normalized noise input (0-1)", "Remap curve (while separateAxes is false; stored as remapX).")
                    .Default(LinearCurve(-1f, 1f))
                    .Curve("remapX", p => p.System.noise.remapX, (p, v) => { var m = p.System.noise; m.remapX = v; },
                        "noise value", "normalized noise input (0-1)", "Remap curve on X (separateAxes only).")
                    .Default(LinearCurve(-1f, 1f))
                    .Curve("remapY", p => p.System.noise.remapY, (p, v) => { var m = p.System.noise; m.remapY = v; },
                        "noise value", "normalized noise input (0-1)", "Remap curve on Y (separateAxes only).")
                    .Default(LinearCurve(-1f, 1f))
                    .Curve("remapZ", p => p.System.noise.remapZ, (p, v) => { var m = p.System.noise; m.remapZ = v; },
                        "noise value", "normalized noise input (0-1)", "Remap curve on Z (separateAxes only).")
                    .Default(LinearCurve(-1f, 1f))
                    .Curve("positionAmount", p => p.System.noise.positionAmount, (p, v) => { var m = p.System.noise; m.positionAmount = v; },
                        "multiplier", Lifetime, "How much the noise moves particles.")
                    .Curve("rotationAmount", p => p.System.noise.rotationAmount, (p, v) => { var m = p.System.noise; m.rotationAmount = v; },
                        "degrees/second", Lifetime, "How much the noise rotates particles.")
                    .Curve("sizeAmount", p => p.System.noise.sizeAmount, (p, v) => { var m = p.System.noise; m.sizeAmount = v; },
                        "multiplier", Lifetime, "How much the noise scales particles.")
                    .Axis("separateAxes", new[] { "strength", "remap" }, new[] { "strengthX", "strengthY", "strengthZ", "remapX", "remapY", "remapZ" })
                    .SameMode("separateAxes", "strengthX", "strengthY", "strengthZ")
                    .SameMode("separateAxes", "remapX", "remapY", "remapZ");
                return b.Module;
            }

            // ===================== customData =====================

            private static readonly string[] CustomDataComponents = { "X", "Y", "Z", "W" };

            private static ModuleAdapter BuildCustomData()
            {
                var b = P1Module("customData", "CustomDataModule", "Custom Data: two per-particle streams, each Disabled, a vector of up to four curves or a colour, read by scripts (GetCustomParticleData) and shaders (custom vertex streams).")
                    .Bool("enabled", p => p.System.customData.enabled, (p, v) => { var m = p.System.customData; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false);
                AddCustomDataStream(b, ParticleSystemCustomData.Custom1, "custom1");
                AddCustomDataStream(b, ParticleSystemCustomData.Custom2, "custom2");
                b.Rule(check =>
                {
                    foreach (var stream in new[] { "custom1", "custom2" })
                    {
                        if (!(check.Effective(stream + "Mode") is ParticleSystemCustomDataMode mode))
                            continue;
                        var inactive = new List<string>();
                        var beyond = new List<string>();
                        if (check.Has(stream + "ComponentCount") && mode != ParticleSystemCustomDataMode.Vector)
                            inactive.Add(stream + "ComponentCount");
                        for (int i = 0; i < CustomDataComponents.Length; i++)
                        {
                            var field = stream + CustomDataComponents[i];
                            if (!check.Has(field))
                                continue;
                            if (mode != ParticleSystemCustomDataMode.Vector)
                                inactive.Add(field);
                            else if (check.Effective(stream + "ComponentCount") is int count && i >= count)
                                beyond.Add(field);
                        }
                        if (check.Has(stream + "Color") && mode != ParticleSystemCustomDataMode.Color)
                            inactive.Add(stream + "Color");

                        if (inactive.Count > 0)
                            check.Warn($"{check.PathOf(inactive[0])}: {string.Join(", ", inactive)} {(inactive.Count > 1 ? "are" : "is")} stored but not used while {stream}Mode is {mode} " +
                                $"(Vector uses {stream}ComponentCount and {stream}X/Y/Z/W, Color uses {stream}Color).");
                        if (beyond.Count > 0)
                            check.Warn($"{check.PathOf(beyond[0])}: {string.Join(", ", beyond)} {(beyond.Count > 1 ? "lie" : "lies")} beyond {stream}ComponentCount ({check.Effective(stream + "ComponentCount")}) and {(beyond.Count > 1 ? "are" : "is")} not used.");
                    }
                });
                return b.Module;
            }

            /// <summary>The flattened fields of one custom data stream: Unity exposes them only as Get/Set methods taking the stream.</summary>
            private static void AddCustomDataStream(ModuleBuilder b, ParticleSystemCustomData stream, string prefix)
            {
                var label = stream.ToString();
                b.Enum<ParticleSystemCustomDataMode>(prefix + "Mode", p => p.System.customData.GetMode(stream), (p, v) => { var m = p.System.customData; m.SetMode(stream, v); },
                    $"What {label} carries: Disabled, a Vector of up to four curves or a Color.")
                    .Default(ParticleSystemCustomDataMode.Disabled);
                b.Int(prefix + "ComponentCount", p => p.System.customData.GetVectorComponentCount(stream), (p, v) => { var m = p.System.customData; m.SetVectorComponentCount(stream, v); },
                    "components", 1, 4, $"How many of {prefix}X/Y/Z/W {label} fills in mode Vector.")
                    .Default(4);
                for (int i = 0; i < CustomDataComponents.Length; i++)
                {
                    int component = i;
                    var name = prefix + CustomDataComponents[i];
                    b.Curve(name, p => p.System.customData.GetVector(stream, component), (p, v) => { var m = p.System.customData; m.SetVector(stream, component, v); },
                        "unitless (script / shader defined)", Lifetime, $"Component {CustomDataComponents[i]} of {label} in mode Vector (used while {prefix}ComponentCount > {i}).");
                }
                b.Gradient(prefix + "Color", p => p.System.customData.GetColor(stream), (p, v) => { var m = p.System.customData; m.SetColor(stream, v); },
                    Lifetime, $"Colour of {label} in mode Color.",
                    ParticleSystemGradientMode.Color, ParticleSystemGradientMode.TwoColors, ParticleSystemGradientMode.Gradient, ParticleSystemGradientMode.TwoGradients);
            }

            // ===================== renderer =====================

            private static readonly ParticleSystemRenderMode[] StretchModes = { ParticleSystemRenderMode.Stretch };

            private static ModuleAdapter BuildRenderer()
            {
                var b = new ModuleBuilder(SectionRenderer, SectionRenderer, "ParticleSystemRenderer", "The ParticleSystemRenderer component: mode, material, sorting, alignment and display parameters.")
                    .Bool("enabled", p => p.Renderer.enabled, (p, v) => p.Renderer.enabled = v, "Whether the renderer draws.")
                    .Default(true)
                    .Enum<ParticleSystemRenderMode>("renderMode", p => p.Renderer.renderMode, (p, v) => p.Renderer.renderMode = v,
                        "How particles are drawn. Mesh needs a mesh.")
                    .Default(ParticleSystemRenderMode.Billboard)
                    .Asset<Material>("material", p => p.Renderer.sharedMaterial, (p, v) => p.Renderer.sharedMaterial = v,
                        "Shared material asset (never an implicit instance); the material asset itself is not modified. null clears it.")
                    .Asset<Mesh>("mesh", p => p.Renderer.mesh, (p, v) => p.Renderer.mesh = v,
                        "Mesh drawn per particle in renderMode Mesh (a mesh list arrives in P1). null clears it.")
                    .Default(null)
                    .Enum<ParticleSystemSortMode>("sortMode", p => p.Renderer.sortMode, (p, v) => p.Renderer.sortMode = v,
                        "Particle draw order within the system.")
                    .Float("sortingFudge", p => p.Renderer.sortingFudge, (p, v) => p.Renderer.sortingFudge = v,
                        "units", null, null, "Bias of the system in transparent sorting; lower draws later (on top).")
                    .SortingLayerName("sortingLayerName", p => p.Renderer.sortingLayerName, (p, v) => p.Renderer.sortingLayerName = v,
                        "Sorting layer (must exist in Tags and Layers).")
                    .Int("sortingOrder", p => p.Renderer.sortingOrder, (p, v) => p.Renderer.sortingOrder = v,
                        "order", short.MinValue, short.MaxValue, "Order within the sorting layer.")
                    .Enum<ParticleSystemRenderSpace>("alignment", p => p.Renderer.alignment, (p, v) => p.Renderer.alignment = v,
                        "Billboard facing: View, World, Local, Facing or Velocity.")
                    .Vector3("pivot", p => p.Renderer.pivot, (p, v) => p.Renderer.pivot = v,
                        "particle sizes", null, null, "Pivot offset in multiples of the particle size.")
                    .Vector3("flip", p => p.Renderer.flip, (p, v) => p.Renderer.flip = v,
                        "ratio", 0, 1, "Fraction of particles flipped on each axis.")
                    .Bool("allowRoll", p => p.Renderer.allowRoll, (p, v) => p.Renderer.allowRoll = v,
                        "Let camera-facing particles roll with the camera.")
                    .Float("minParticleSize", p => p.Renderer.minParticleSize, (p, v) => p.Renderer.minParticleSize = v,
                        "fraction of the viewport", 0, null, "Smallest on-screen size.")
                    .Default(0f)
                    .Float("maxParticleSize", p => p.Renderer.maxParticleSize, (p, v) => p.Renderer.maxParticleSize = v,
                        "fraction of the viewport", 0, null, "Largest on-screen size.")
                    .Default(0.5f)
                    .Float("normalDirection", p => p.Renderer.normalDirection, (p, v) => p.Renderer.normalDirection = v,
                        "ratio", 0, 1, "Billboard lighting normal: 1 faces the camera, 0 points away from the centre.")
                    .Float("cameraVelocityScale", p => p.Renderer.cameraVelocityScale, (p, v) => p.Renderer.cameraVelocityScale = v,
                        "multiplier", null, null, "Stretch from camera movement (renderMode Stretch).")
                    .Float("velocityScale", p => p.Renderer.velocityScale, (p, v) => p.Renderer.velocityScale = v,
                        "multiplier", null, null, "Stretch proportional to speed (renderMode Stretch).")
                    .Float("lengthScale", p => p.Renderer.lengthScale, (p, v) => p.Renderer.lengthScale = v,
                        "multiplier", null, null, "Stretch proportional to size (renderMode Stretch).")
                    .Bool("freeformStretching", p => p.Renderer.freeformStretching, (p, v) => p.Renderer.freeformStretching = v,
                        "Stretch without keeping the camera-facing orientation (renderMode Stretch).")
                    .Bool("rotateWithStretchDirection", p => p.Renderer.rotateWithStretchDirection, (p, v) => p.Renderer.rotateWithStretchDirection = v,
                        "Rotate stretched particles with their direction (renderMode Stretch).")
                    .Enum<ShadowCastingMode>("shadowCastingMode", p => p.Renderer.shadowCastingMode, (p, v) => p.Renderer.shadowCastingMode = v,
                        "Whether particles cast shadows.")
                    .Bool("receiveShadows", p => p.Renderer.receiveShadows, (p, v) => p.Renderer.receiveShadows = v,
                        "Whether particles receive shadows.")
                    .Float("shadowBias", p => p.Renderer.shadowBias, (p, v) => p.Renderer.shadowBias = v,
                        "particle sizes", 0, null, "Shadow offset along the light direction.")
                    .Enum<SpriteMaskInteraction>("maskInteraction", p => p.Renderer.maskInteraction, (p, v) => p.Renderer.maskInteraction = v,
                        "Interaction with Sprite Masks.")
                    .Rule(check =>
                    {
                        var mode = check.Effective("renderMode") as ParticleSystemRenderMode?;
                        bool meshKnown = check.Binding != null || check.Has("mesh");
                        if (mode == ParticleSystemRenderMode.Mesh && meshKnown && check.Effective("mesh") == null &&
                            (check.Has("renderMode") || check.Has("mesh")))
                            check.Error(check.Has("renderMode") ? "renderMode" : "mesh",
                                "renderMode Mesh needs a mesh: send \"mesh\" (e.g. {\"assetPath\": \"Library/unity default resources\", \"name\": \"Cube\"}) in the same patch.");
                        if (check.Has("material") && check.Effective("material") == null)
                            check.Warn($"{check.PathOf("material")}: with no material Unity has nothing to draw the particles with.");
                        foreach (var stretchOnly in new[] { "cameraVelocityScale", "velocityScale", "lengthScale", "freeformStretching", "rotateWithStretchDirection" })
                        {
                            if (check.Has(stretchOnly) && mode.HasValue && !StretchModes.Contains(mode.Value))
                                check.Warn($"{check.PathOf(stretchOnly)}: only has an effect in renderMode Stretch (it is {mode.Value}).");
                        }
                        if ((check.Has("minParticleSize") || check.Has("maxParticleSize")) &&
                            check.Effective("minParticleSize") is float min && check.Effective("maxParticleSize") is float max && min > max)
                            check.Error(check.Has("minParticleSize") ? "minParticleSize" : "maxParticleSize",
                                $"minParticleSize ({min}) must be <= maxParticleSize ({max}).");
                    });
                return b.Module;
            }

            // ===================== subEmitters (P1) =====================

            /// <summary>The module's own toggle; the list is a collection with its own skills, like the bursts of emission.</summary>
            private static ModuleAdapter BuildSubEmitters()
            {
                var b = ParticleModule("subEmitters", "SubEmittersModule", "Sub Emitters: other systems spawned at each particle's birth, collision, death, trigger or manually. The list is read and replaced with its own skills.")
                    .Bool("enabled", p => p.System.subEmitters.enabled, (p, v) => { var m = p.System.subEmitters; m.enabled = v; }, "Whether the module is enabled.")
                    .Default(false)
                    .ReadOnlyInt("subEmittersCount", p => StoredSubEmitterCount(p.System), "The list is written with particle_set_sub_emitters.", "Number of sub-emitter entries, broken ones included (as the Inspector lists them).")
                    .Collection("subEmitters", "particle_get_sub_emitters", "particle_set_sub_emitters", p => StoredSubEmitterCount(p.System))
                    .Rule(check =>
                    {
                        if (check.Requested("enabled", out var enabled) && enabled is bool on && on && check.Binding != null && StoredSubEmitterCount(check.Binding.System) == 0)
                            check.Warn($"{check.PathOf("enabled")}: the module is enabled with an empty list; set the list with particle_set_sub_emitters.");
                    });
                b.Module.Phase = "P1";
                return b.Module;
            }
        }
    }
}
#endif

// Producer:Betsy
