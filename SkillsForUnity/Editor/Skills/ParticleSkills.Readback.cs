#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        /// <summary>
        /// The fields an axis flag currently hides: with startSize3D false, startSizeX/Y/Z; with it true, startSize (which
        /// is the same storage as startSizeX). Readback shows only the set that applies, as the Inspector does.
        /// </summary>
        internal static HashSet<string> HiddenFields(ModuleAdapter module, ParticleBinding binding)
        {
            var hidden = new HashSet<string>();
            foreach (var group in module.AxisGroups)
            {
                var flag = module.Field(group.Flag);
                bool on = flag != null && flag.Read(binding) is bool value && value;
                foreach (var name in on ? group.Uniform : group.Axes)
                    hidden.Add(name);
            }
            return hidden;
        }

        /// <summary>Every field of a module that applies in its current state, as Unity reports it.</summary>
        internal static JObject ReadModule(ModuleAdapter module, ParticleBinding binding)
        {
            var hidden = HiddenFields(module, binding);
            var result = new JObject();
            foreach (var field in module.Fields)
            {
                if (!hidden.Contains(field.Name))
                    result[field.Name] = Codec.WriteField(field, field.Read(binding));
            }
            return result;
        }

        /// <summary>
        /// The state block of a write response: each written field read back from Unity (never echoed from the input),
        /// plus the enabled flag of every touched module, so writing a disabled module's values says so.
        /// </summary>
        internal static JObject ReadState(Patch patch, ParticleBinding binding)
        {
            var state = new JObject();
            foreach (var module in Registry.Modules)
            {
                var written = patch.Writes.Where(write => ReferenceEquals(write.Field.Module, module)).ToList();
                if (written.Count == 0)
                    continue;

                var fields = new JObject();
                var enabled = module.EnabledField;
                if (enabled != null && module.Section == SectionModules)
                    fields["enabled"] = Codec.WriteField(enabled, enabled.Read(binding));
                foreach (var write in written)
                    fields[write.Field.Name] = Codec.WriteField(write.Field, write.Field.Read(binding));

                if (module.Section == SectionModules)
                {
                    if (!(state[SectionModules] is JObject modules))
                    {
                        modules = new JObject();
                        state[SectionModules] = modules;
                    }
                    modules[module.Id] = fields;
                }
                else
                {
                    state[module.Section] = fields;
                }
            }
            return state;
        }

        internal static JObject PlaybackState(ParticleSystem system) => new JObject
        {
            ["isPlaying"] = system.isPlaying,
            ["isPaused"] = system.isPaused,
            ["isStopped"] = system.isStopped,
            ["isEmitting"] = system.isEmitting,
            ["particleCount"] = system.particleCount,
            ["time"] = system.time
        };

        /// <summary>The handful of Main values that identify what a system does; the full module comes from particle_get_module.</summary>
        internal static JObject MainSummary(ParticleBinding binding)
        {
            var summaryFields = new[]
            {
                "duration", "loop", "prewarm", "startLifetime", "startSpeed", "startSize3D", "startSize", "startSizeX", "startSizeY", "startSizeZ",
                "startColor", "simulationSpace", "maxParticles", "playOnAwake", "stopAction"
            };
            var main = Registry.Module("main");
            var hidden = HiddenFields(main, binding);
            var result = new JObject();
            foreach (var name in summaryFields)
            {
                var field = main.Field(name);
                if (field != null && !hidden.Contains(name))
                    result[name] = Codec.WriteField(field, field.Read(binding));
            }
            return result;
        }

        /// <summary>The enabled flag of every module, Inspector order (P1 modules included: knowing Collision is on matters).</summary>
        internal static JObject ModulesEnabled(ParticleBinding binding)
        {
            var result = new JObject();
            foreach (var module in Registry.ParticleModules)
            {
                var enabled = module.EnabledField;
                if (enabled != null)
                    result[module.Id] = Codec.WriteField(enabled, enabled.Read(binding));
            }
            return result;
        }

        internal static JToken RendererSummary(ParticleBinding binding)
        {
            if (binding.Renderer == null)
                return JValue.CreateNull();
            var renderer = Registry.Renderer;
            var result = new JObject();
            foreach (var name in new[] { "enabled", "renderMode", "material", "mesh", "sortingLayerName", "sortingOrder" })
            {
                var field = renderer.Field(name);
                if (name == "mesh" && !(binding.Renderer.renderMode == ParticleSystemRenderMode.Mesh))
                    continue;
                result[name] = Codec.WriteField(field, field.Read(binding));
            }
            return result;
        }

        internal static JArray ReadBursts(ParticleSystem system)
        {
            var emission = system.emission;
            var bursts = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(bursts);
            var result = new JArray();
            foreach (var burst in bursts)
                result.Add(Codec.BurstToJson(burst));
            return result;
        }
    }
}
#endif

// Producer:Betsy
