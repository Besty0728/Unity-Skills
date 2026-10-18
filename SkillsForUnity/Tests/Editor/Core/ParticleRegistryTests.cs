#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The registry is the single field table behind capabilities, parsing, validation, readback and dryRun, so it is
    /// checked against Unity itself: declared defaults equal a fresh system, every writable field of every configurable
    /// module writes and reads back a sample value (a reader/writer wired to the wrong property fails here), and the
    /// group declarations only name fields that exist.
    /// </summary>
    [TestFixture]
    public class ParticleRegistryTests : ParticleTestBase
    {
        private static ParticleSkills.ParticleBinding Bind(ParticleSystem system) => new ParticleSkills.ParticleBinding
        {
            GameObject = system.gameObject,
            System = system,
            Renderer = system.GetComponent<ParticleSystemRenderer>()
        };

        [Test]
        public void DeclaredDefaults_MatchAFreshSystem()
        {
            var binding = Bind(MakeSystem("DefaultsFx"));
            var mismatches = new List<string>();
            foreach (var module in ParticleSkills.Registry.Modules)
            {
                foreach (var field in module.Fields.Where(candidate => candidate.HasDefault))
                {
                    var actual = field.Read(binding);
                    bool same = field.Kind == ParticleSkills.FieldKind.Curve
                        ? ((ParticleSystem.MinMaxCurve)actual).mode == ((ParticleSystem.MinMaxCurve)field.Default).mode
                        : ParticleSkills.Codec.Equivalent(ParticleSkills.Codec.WriteField(field, field.Default), ParticleSkills.Codec.WriteField(field, actual));
                    if (!same)
                        mismatches.Add($"{field.Path}: declared {ParticleSkills.Codec.WriteField(field, field.Default)}, Unity has {ParticleSkills.Codec.WriteField(field, actual)}");
                }
            }
            Assert.That(mismatches, Is.Empty, string.Join("\n", mismatches));
        }

        [Test]
        public void EveryWritableField_WritesAndReadsBackASampleValue()
        {
            var failures = new List<string>();
            int checkedFields = 0;
            foreach (var module in ParticleSkills.Registry.Modules.Where(candidate => candidate.Configurable))
            {
                foreach (var field in module.Fields.Where(candidate => candidate.Writable))
                {
                    var system = MakeSystem("Sample_" + module.Id + "_" + field.Name);
                    var binding = Bind(system);
                    var sample = Sample(field, binding);
                    var fields = new JObject { [field.Name] = sample };
                    AddPrerequisites(module, field, fields);

                    object result;
                    if (module.Section == ParticleSkills.SectionRenderer)
                        result = ParticleSkills.ParticleSetRenderer(entityId: Id(system.gameObject), configJson: RendererConfig(fields));
                    else if (module.Section == ParticleSkills.SectionSystem)
                        result = ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: Config(null, fields));
                    else
                        result = ParticleSkills.ParticleConfigure(entityId: Id(system.gameObject), configJson: Config(new JObject { [module.Id] = fields }));

                    checkedFields++;
                    var json = Json(result);
                    if (json["error"] != null)
                    {
                        failures.Add($"{field.Path}: rejected {sample.ToString(Formatting.None)}: {json["error"]}");
                        continue;
                    }
                    var expected = JToken.Parse(sample.ToString(Formatting.None));
                    var actual = ParticleSkills.Codec.WriteField(field, field.Read(binding));
                    if (field.Kind == ParticleSkills.FieldKind.Asset)
                    {
                        if (actual["name"]?.ToString() != sample["name"]?.ToString())
                            failures.Add($"{field.Path}: wrote {sample.ToString(Formatting.None)}, read {actual.ToString(Formatting.None)}");
                    }
                    else if (!ParticleSkills.Codec.Equivalent(Canonical(field, expected), actual))
                    {
                        failures.Add($"{field.Path}: wrote {sample.ToString(Formatting.None)}, read {actual.ToString(Formatting.None)}");
                    }
                    UnityEngine.Object.DestroyImmediate(system.gameObject);
                }
            }
            Assert.That(checkedFields, Is.GreaterThan(170), "The sweep found too few writable fields; the registry walk is broken.");
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        /// <summary>A sample a field accepts, different from a fresh system's value where the field allows it.</summary>
        private static JToken Sample(ParticleSkills.FieldSpec field, ParticleSkills.ParticleBinding binding)
        {
            switch (field.Kind)
            {
                case ParticleSkills.FieldKind.Bool:
                    return !(bool)field.Read(binding);
                case ParticleSkills.FieldKind.Int:
                {
                    var current = (int)field.Read(binding);
                    var candidates = new List<int> { 3, 2, 1 };
                    if (field.Max.HasValue && field.Max.Value <= 4)
                        candidates.Insert(0, (int)field.Max.Value);
                    return candidates.First(value => value != current && (!field.Max.HasValue || value <= field.Max.Value) && (!field.Min.HasValue || value >= field.Min.Value));
                }
                case ParticleSkills.FieldKind.UInt:
                    return 12345;
                case ParticleSkills.FieldKind.Float:
                    return Number(field, 0.37);
                case ParticleSkills.FieldKind.Enum:
                {
                    var current = field.Read(binding).ToString();
                    var values = (field.WritableEnumValues ?? ParticleSkills.Codec.LiveEnumNames(field.EnumType))
                        .Where(value => value != "Mesh" || field.Name != "renderMode").ToArray();
                    return values.FirstOrDefault(value => value != current) ?? values[0];
                }
                case ParticleSkills.FieldKind.Vector2:
                    return new JObject { ["x"] = 0.2, ["y"] = 0.6 };
                case ParticleSkills.FieldKind.Vector3:
                    return field.Max.HasValue
                        ? new JObject { ["x"] = 0.25, ["y"] = 0.5, ["z"] = 0.75 }
                        : new JObject { ["x"] = 1.5, ["y"] = -2, ["z"] = 3 };
                case ParticleSkills.FieldKind.Curve:
                {
                    var modes = field.CurveModes ?? ParticleSkills.Codec.AllCurveModes;
                    if (modes.Contains(ParticleSystemCurveMode.TwoConstants))
                        return new JObject { ["mode"] = "TwoConstants", ["min"] = Number(field, 0.25), ["max"] = Number(field, 0.75) };
                    return JObject.Parse("{\"mode\":\"Curve\",\"multiplier\":1.5,\"curve\":{\"keys\":[{\"time\":0,\"value\":0.25},{\"time\":1,\"value\":0.75}]}}");
                }
                case ParticleSkills.FieldKind.Gradient:
                {
                    var modes = field.GradientModes ?? ParticleSkills.Codec.AllGradientModes;
                    if (modes.Contains(ParticleSystemGradientMode.TwoColors))
                        return JObject.Parse("{\"mode\":\"TwoColors\",\"minColor\":{\"r\":1,\"g\":0,\"b\":0,\"a\":1},\"maxColor\":{\"r\":0,\"g\":0,\"b\":1,\"a\":0.5}}");
                    return JObject.Parse("{\"mode\":\"Gradient\",\"gradient\":{\"mode\":\"Blend\",\"colorSpace\":\"Uninitialized\",\"colorKeys\":[{\"time\":0,\"color\":{\"r\":1,\"g\":0,\"b\":0}}],\"alphaKeys\":[{\"time\":0,\"alpha\":1},{\"time\":1,\"alpha\":0}]}}");
                }
                case ParticleSkills.FieldKind.Asset:
                    return field.AssetType == typeof(Mesh)
                        ? new JObject { ["assetPath"] = "Library/unity default resources", ["name"] = "Cube" }
                        : new JObject { ["assetPath"] = "Resources/unity_builtin_extra", ["name"] = "Default-ParticleSystem" };
                case ParticleSkills.FieldKind.SortingLayer:
                    return "Default";
                default:
                    throw new NotSupportedException(field.Kind.ToString());
            }
        }

        private static double Number(ParticleSkills.FieldSpec field, double fraction)
        {
            var min = field.Min ?? 0;
            var max = field.Max ?? min + 10;
            return Math.Round(min + (max - min) * fraction, 4) + (field.MinExclusive && fraction == 0 ? 0.01 : 0);
        }

        /// <summary>The flags a field needs in the same patch: per-axis fields need their flag on, uniform ones off, randomSeed manual seeding.</summary>
        private static void AddPrerequisites(ParticleSkills.ModuleAdapter module, ParticleSkills.FieldSpec field, JObject fields)
        {
            foreach (var group in module.AxisGroups)
            {
                if (group.Axes.Contains(field.Name))
                    fields[group.Flag] = true;
                if (group.Uniform.Contains(field.Name))
                    fields[group.Flag] = false;
            }
            // A triplet shares one mode: send the sample for every member. A member may sit outside the axis group (the
            // rotation modules' z has no uniform twin), so the flag a group depends on is turned on here as well.
            foreach (var same in module.SameModeGroups.Where(group => group.Fields.Contains(field.Name)))
            {
                if (same.WhenFlag != null)
                    fields[same.WhenFlag] = true;
                foreach (var other in same.Fields.Where(other => other != field.Name))
                    fields[other] = fields[field.Name].DeepClone();
            }
            if (field.Name == "minParticleSize")
                fields["maxParticleSize"] = 10;
            if (field.Name == "randomSeed")
                fields["useAutoRandomSeed"] = false;
            if (field.Name == "useAutoRandomSeed")
                fields["useAutoRandomSeed"] = false;
        }

        /// <summary>What readback returns for a sample: the codec's canonical form of the same value.</summary>
        private static JToken Canonical(ParticleSkills.FieldSpec field, JToken sample)
        {
            var problems = new ParticleSkills.Problems();
            Assert.That(ParticleSkills.Codec.ReadField(field, sample, "sample", problems, out var value), Is.True,
                string.Join("; ", problems.Items.Select(problem => problem.Message)));
            return ParticleSkills.Codec.WriteField(field, value);
        }

        [Test]
        public void RegistryDeclarations_AreConsistent()
        {
            var problems = new List<string>();
            foreach (var module in ParticleSkills.Registry.Modules)
            {
                var names = module.Fields.Select(field => field.Name).ToList();
                if (names.Count != names.Distinct().Count())
                    problems.Add($"{module.Id}: duplicate field names");
                foreach (var field in module.Fields)
                {
                    if (!ReferenceEquals(field.Module, module))
                        problems.Add($"{module.Id}.{field.Name}: Module back-reference not set");
                    if (field.Writable && field.Write == null)
                        problems.Add($"{field.Path}: writable without a writer");
                    if (field.Kind == ParticleSkills.FieldKind.Enum && field.WritableEnumValues != null)
                    {
                        var live = ParticleSkills.Codec.LiveEnumNames(field.EnumType);
                        problems.AddRange(field.WritableEnumValues.Where(value => !live.Contains(value)).Select(value => $"{field.Path}: writable value {value} is not a live enum name"));
                    }
                    if (string.IsNullOrEmpty(field.Description))
                        problems.Add($"{field.Path}: no description");
                }
                foreach (var group in module.AxisGroups)
                {
                    problems.AddRange(new[] { group.Flag }.Concat(group.Uniform).Concat(group.Axes)
                        .Where(name => !names.Contains(name)).Select(name => $"{module.Id}: axis group names unknown field {name}"));
                }
                foreach (var group in module.SameModeGroups)
                {
                    problems.AddRange(group.Fields.Concat(group.WhenFlag != null ? new[] { group.WhenFlag } : new string[0])
                        .Where(name => !names.Contains(name)).Select(name => $"{module.Id}: same-mode group names unknown field {name}"));
                }
                if (module.Section == ParticleSkills.SectionModules && module.Id != "main" && module.EnabledField == null)
                    problems.Add($"{module.Id}: no enabled field (only Main has no toggle)");
                if (!module.Configurable && module.Fields.Any(field => field.Writable))
                    problems.Add($"{module.Id}: a planned module has writable fields");
            }

            var configurable = ParticleSkills.Registry.ParticleModules.Where(module => module.Configurable).Select(module => module.Id).ToArray();
            Assert.That(configurable, Is.EquivalentTo(new[]
            {
                "main", "emission", "shape", "velocityOverLifetime", "limitVelocityOverLifetime", "inheritVelocity", "lifetimeByEmitterSpeed",
                "forceOverLifetime", "colorOverLifetime", "colorBySpeed", "sizeOverLifetime", "sizeBySpeed", "rotationOverLifetime",
                "rotationBySpeed", "externalForces", "noise", "customData"
            }));
            Assert.That(problems, Is.Empty, string.Join("\n", problems));
        }
    }
}
#endif

// Producer:Betsy
