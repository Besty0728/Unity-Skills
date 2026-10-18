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
                registry.Modules.Add(Planned("limitVelocityOverLifetime", "LimitVelocityOverLifetimeModule", b => b.System.limitVelocityOverLifetime.enabled, "Limit Velocity over Lifetime: per-axis speed limit, dampen, drag."));
                registry.Modules.Add(Planned("inheritVelocity", "InheritVelocityModule", b => b.System.inheritVelocity.enabled, "Inherit Velocity: how much emitter velocity particles inherit."));
                registry.Modules.Add(Planned("lifetimeByEmitterSpeed", "LifetimeByEmitterSpeedModule", b => b.System.lifetimeByEmitterSpeed.enabled, "Lifetime by Emitter Speed: start lifetime mapped from emitter speed."));
                registry.Modules.Add(Planned("forceOverLifetime", "ForceOverLifetimeModule", b => b.System.forceOverLifetime.enabled, "Force over Lifetime: force curves, space, randomize."));
                registry.Modules.Add(BuildColorOverLifetime());
                registry.Modules.Add(Planned("colorBySpeed", "ColorBySpeedModule", b => b.System.colorBySpeed.enabled, "Color by Speed: speed range and gradient."));
                registry.Modules.Add(BuildSizeOverLifetime());
                registry.Modules.Add(Planned("sizeBySpeed", "SizeBySpeedModule", b => b.System.sizeBySpeed.enabled, "Size by Speed: speed range and size curves."));
                registry.Modules.Add(Planned("rotationOverLifetime", "RotationOverLifetimeModule", b => b.System.rotationOverLifetime.enabled, "Rotation over Lifetime: per-axis angular velocity."));
                registry.Modules.Add(Planned("rotationBySpeed", "RotationBySpeedModule", b => b.System.rotationBySpeed.enabled, "Rotation by Speed: speed range and angular velocity."));
                registry.Modules.Add(Planned("externalForces", "ExternalForcesModule", b => b.System.externalForces.enabled, "External Forces: multiplier and force fields."));
                registry.Modules.Add(BuildNoise());
                registry.Modules.Add(Planned("collision", "CollisionModule", b => b.System.collision.enabled, "Collision: world/planes, bounce, dampen, lifetime loss."));
                registry.Modules.Add(Planned("trigger", "TriggerModule", b => b.System.trigger.enabled, "Triggers: collider list and enter/exit behaviour."));
                registry.Modules.Add(Planned("subEmitters", "SubEmittersModule", b => b.System.subEmitters.enabled, "Sub Emitters: child systems spawned on events (particle_get_sub_emitters in P1)."));
                registry.Modules.Add(Planned("textureSheetAnimation", "TextureSheetAnimationModule", b => b.System.textureSheetAnimation.enabled, "Texture Sheet Animation: grid/sprites and frame curves."));
                registry.Modules.Add(Planned("lights", "LightsModule", b => b.System.lights.enabled, "Lights: light reference, ratio, range and intensity."));
                registry.Modules.Add(Planned("trails", "TrailModule", b => b.System.trails.enabled, "Trails: mode, ratio, lifetime, width and colour."));
                registry.Modules.Add(Planned("customData", "CustomDataModule", b => b.System.customData.enabled, "Custom Data: Custom1/Custom2 vector or colour streams."));
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

            /// <summary>A Curve-mode MinMaxCurve from (0, start) to (1, end) with multiplier 1, the shape of Unity's default lifetime curves.</summary>
            private static ParticleSystem.MinMaxCurve LinearCurve(float start, float end) =>
                new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, start, 1f, end));

            private const string SystemTime = "normalized system time over the duration (0-1)";
            private const string Lifetime = "normalized particle lifetime (0-1)";
            private const string NormalizedTime = "normalized time (0-1)";

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
                ["arc"] = new[] { ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut },
                ["arcMode"] = new[] { ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut },
                ["arcSpread"] = new[] { ParticleSystemShapeType.Cone, ParticleSystemShapeType.ConeVolume, ParticleSystemShapeType.Circle, ParticleSystemShapeType.Donut },
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
                    .Float("radiusThickness", p => p.System.shape.radiusThickness, (p, v) => { var s = p.System.shape; s.radiusThickness = v; },
                        "ratio", 0, 1, "0 emits from the surface, 1 from the whole volume.")
                    .Float("arc", p => p.System.shape.arc, (p, v) => { var s = p.System.shape; s.arc = v; },
                        "degrees", 0, 360, "Portion of the circle that emits (Cone, ConeVolume, Circle, Donut).")
                    .Enum<ParticleSystemShapeMultiModeValue>("arcMode", p => p.System.shape.arcMode, (p, v) => { var s = p.System.shape; s.arcMode = v; },
                        "How emission positions move around the arc.")
                    .Float("arcSpread", p => p.System.shape.arcSpread, (p, v) => { var s = p.System.shape; s.arcSpread = v; },
                        "ratio", 0, 1, "Discrete arc intervals emission is snapped to (0 = continuous).")
                    .Float("length", p => p.System.shape.length, (p, v) => { var s = p.System.shape; s.length = v; },
                        "units", 0, null, "Cone length (ConeVolume).")
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
                    });
                return b.Module;
            }

            // ===================== velocityOverLifetime =====================

            private static ModuleAdapter BuildVelocityOverLifetime()
            {
                var b = ParticleModule("velocityOverLifetime", "VelocityOverLifetimeModule", "Velocity over Lifetime: linear velocity curves, space, radial velocity and speed modifier. Orbital velocity arrives in P1.")
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
                    .Curve("radial", p => p.System.velocityOverLifetime.radial, (p, v) => { var m = p.System.velocityOverLifetime; m.radial = v; },
                        "units/second", Lifetime, "Velocity away from the system centre.")
                    .Curve("speedModifier", p => p.System.velocityOverLifetime.speedModifier, (p, v) => { var m = p.System.velocityOverLifetime; m.speedModifier = v; },
                        "multiplier", Lifetime, "Speed multiplier that keeps the direction.")
                    .SameMode(null, "x", "y", "z");
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
        }
    }
}
#endif

// Producer:Betsy
