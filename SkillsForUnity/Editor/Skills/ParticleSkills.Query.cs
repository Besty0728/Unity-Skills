#if COM_UNITY_MODULES_PARTICLESYSTEM
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        internal const int FindAllMaxLimit = 500;
        internal const int MaxBatchItems = 100;

        // ===================== particle_get_capabilities =====================

        internal static object CapabilitiesCore(string module)
        {
            var registry = Registry;
            IEnumerable<ModuleAdapter> modules = registry.Modules;
            if (!string.IsNullOrWhiteSpace(module))
            {
                var wanted = module.Trim();
                var match = registry.Modules.FirstOrDefault(candidate => string.Equals(candidate.Id, wanted, System.StringComparison.Ordinal));
                if (match == null)
                {
                    return Error(CodeSemantic, Codec.UnknownKeyMessage(wanted, "module", registry.Modules.Select(candidate => candidate.Id)),
                        parameter: "module",
                        fixes: new object[] { Fix("fix_param", "particle_get_capabilities", "Omit module to list every module, or pass one of the listed ids.") });
                }
                modules = new[] { match };
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["available"] = true,
                ["schemaVersion"] = SchemaVersion,
                ["contractVersion"] = ContractVersion,
                ["unityVersion"] = Application.unityVersion,
                ["package"] = ModulePackage,
                ["scope"] = "Built-in Particle System (Shuriken) components in open scenes and prefab instances. Not Visual Effect Graph.",
                ["modules"] = modules.Select(DescribeModule).ToArray(),
                ["payloads"] = DescribePayloads(),
                ["valueTypes"] = DescribeValueTypes(),
                ["rules"] = PatchRules,
                ["limits"] = new Dictionary<string, object>
                {
                    ["maxCurveKeys"] = MaxCurveKeys,
                    ["maxGradientColorKeys"] = MaxGradientKeys,
                    ["maxGradientAlphaKeys"] = MaxGradientKeys,
                    ["maxBursts"] = MaxBursts,
                    ["maxSubEmitters"] = MaxSubEmitters,
                    ["maxBatchItems"] = MaxBatchItems,
                    ["findAllMaxLimit"] = FindAllMaxLimit,
                    ["gradientKeyTimeStep"] = "1/65535 (Unity quantizes gradient key times)",
                    ["playback"] = PlaybackLimits()
                }
            };
        }

        /// <summary>The patch semantics every write follows (a property: static initializers stay out of partial files).</summary>
        private static string[] PatchRules => new[]
        {
            "configJson is a partial patch: omitted fields keep their value; explicit false and 0 are applied.",
            "A curve, gradient, vector, colour or asset value replaces that whole property; module objects patch field by field.",
            "null is accepted only by fields marked nullable (asset references), and clears them.",
            "Unknown modules, unknown fields, duplicate keys, wrong JSON types, non-finite numbers, out-of-range values and illegal enum names are rejected; nothing is skipped silently.",
            "The whole request is validated before anything is written; any problem rejects the call and changes nothing.",
            "Writes never enable a module implicitly: a disabled module's values are stored and reported with enabled:false.",
            "An axis flag (startSize3D, startRotation3D, separateAxes) decides which of its uniform / per-axis fields apply; sending the other set is rejected.",
            "Writes need Edit Mode and a stopped system; a playing or paused system is refused, never stopped implicitly.",
            "Responses read every written field back from Unity; adjustments lists values Unity changed (clamps).",
            "schemaVersion is required and must be " + SchemaVersion + "; an empty configure patch is rejected."
        };

        private static object DescribeModule(ModuleAdapter module)
        {
            var result = new Dictionary<string, object>
            {
                ["id"] = module.Id,
                ["section"] = module.Section,
                ["path"] = module.Path,
                ["unityType"] = module.UnityName,
                ["phase"] = module.Phase,
                ["configurable"] = module.Configurable,
                ["description"] = module.Description
            };
            if (!module.Configurable)
                result["unavailableReason"] = module.UnavailableReason;
            result["fields"] = module.Fields.Select(DescribeField).ToArray();
            if (module.AxisGroups.Count > 0)
            {
                result["axisGroups"] = module.AxisGroups.Select(group => new Dictionary<string, object>
                {
                    ["flag"] = group.Flag,
                    ["whenFalse"] = group.Uniform,
                    ["whenTrue"] = group.Axes
                }).ToArray();
            }
            if (module.SameModeGroups.Count > 0)
            {
                result["sameModeGroups"] = module.SameModeGroups.Select(group => new Dictionary<string, object>
                {
                    ["fields"] = group.Fields,
                    ["whenFlag"] = group.WhenFlag
                }).ToArray();
            }
            if (module.Collections.Count > 0)
            {
                result["collections"] = module.Collections.Select(collection => new Dictionary<string, object>
                {
                    ["name"] = collection.Name,
                    ["readWith"] = collection.ReadSkill,
                    ["writeWith"] = collection.WriteSkill
                }).ToArray();
            }
            return result;
        }

        private static object DescribeField(FieldSpec field)
        {
            var result = new Dictionary<string, object>
            {
                ["name"] = field.Name,
                ["type"] = TypeName(field),
                ["writable"] = field.Writable
            };
            if (!field.Writable)
                result["readOnlyReason"] = field.ReadOnlyReason;
            if (field.Unit != null)
                result["unit"] = field.Unit;
            if (field.Min.HasValue)
                result[field.MinExclusive ? "minExclusive" : "min"] = field.Min.Value;
            if (field.Max.HasValue)
                result["max"] = field.Max.Value;
            if (field.Kind == FieldKind.Enum)
            {
                result["values"] = Codec.LiveEnumNames(field.EnumType);
                if (field.WritableEnumValues != null)
                    result["writableValues"] = field.WritableEnumValues;
            }
            if (field.Kind == FieldKind.Curve)
            {
                result["curveModes"] = (field.CurveModes ?? Codec.AllCurveModes).Select(mode => mode.ToString()).ToArray();
                result["keyTimeRange"] = new[] { field.KeyTimeMin, field.KeyTimeMax };
                if (field.KeyTimeMeaning != null)
                    result["keyTime"] = field.KeyTimeMeaning;
            }
            if (field.Kind == FieldKind.Gradient)
            {
                result["gradientModes"] = (field.GradientModes ?? Codec.AllGradientModes).Select(mode => mode.ToString()).ToArray();
                if (field.KeyTimeMeaning != null)
                    result["keyTime"] = field.KeyTimeMeaning;
            }
            if (field.Kind == FieldKind.Asset)
            {
                result["assetType"] = field.AssetType.Name;
                result["nullable"] = field.Nullable;
            }
            result["description"] = field.Description;
            return result;
        }

        private static string TypeName(FieldSpec field)
        {
            switch (field.Kind)
            {
                case FieldKind.Bool: return "bool";
                case FieldKind.Int: return "int";
                case FieldKind.UInt: return "uint";
                case FieldKind.Float: return "float";
                case FieldKind.Enum: return "enum";
                case FieldKind.Vector2: return "Vector2";
                case FieldKind.Vector3: return "Vector3";
                case FieldKind.Color: return "Color";
                case FieldKind.Curve: return "MinMaxCurve";
                case FieldKind.Gradient: return "MinMaxGradient";
                case FieldKind.Asset: return "AssetReference";
                case FieldKind.SortingLayer: return "sortingLayerName";
                default: return field.Kind.ToString();
            }
        }

        private static object DescribePayloads() => new Dictionary<string, object>
        {
            ["particle_configure.configJson"] = "{\"schemaVersion\": 1, \"system\"?: {field: value}, \"modules\"?: {moduleId: {field: value}}} (at least one field)",
            ["particle_create.configJson"] = "same shape as particle_configure, optional; may be empty",
            ["particle_add_system.configJson"] = "same shape as particle_configure, optional; may be empty",
            ["particle_set_renderer.configJson"] = "{\"schemaVersion\": 1, \"renderer\": {field: value}} (at least one field)",
            ["particle_set_bursts.burstsJson"] = "[Burst, ...] the complete list, at most " + MaxBursts + "; [] clears it",
            ["particle_set_sub_emitters.subEmittersJson"] = "[SubEmitter, ...] the complete list, at most " + MaxSubEmitters + "; [] clears it",
            ["particle_analyze.thresholdsJson"] = "{maxParticlesPerSystem?, totalMaxParticles?, maxSystems?, maxLights? (integers >= 1), minStartSize?, maxStartSize?, minStartLifetime?, maxStartLifetime?, maxEmissionRate? (numbers > 0)}; omitted keys keep their defaults",
            ["batchItems"] = "items is a JSON array of objects carrying each single skill's parameters; unknown item keys are rejected"
        };

        private static object DescribeValueTypes() => new Dictionary<string, object>
        {
            ["MinMaxCurve"] = new Dictionary<string, object>
            {
                ["Constant"] = "{\"mode\": \"Constant\", \"constant\": 1.5} (a bare number is shorthand for this)",
                ["TwoConstants"] = "{\"mode\": \"TwoConstants\", \"min\": 0.5, \"max\": 2} (min <= max)",
                ["Curve"] = "{\"mode\": \"Curve\", \"multiplier\": 1, \"curve\": AnimationCurve}",
                ["TwoCurves"] = "{\"mode\": \"TwoCurves\", \"multiplier\": 1, \"minCurve\": AnimationCurve, \"maxCurve\": AnimationCurve}",
                ["bounds"] = "a field's min/max apply to the constants; curve key values are any finite number"
            },
            ["AnimationCurve"] = "{\"preWrapMode\"?: Default|Clamp|Loop|PingPong|ClampForever (default ClampForever), \"postWrapMode\"?: same, \"keys\": [Keyframe, ...] (1-" + MaxCurveKeys + ", sorted by time, no repeated time)}",
            ["Keyframe"] = new Dictionary<string, object>
            {
                ["shape"] = "{\"time\", \"value\", \"inTangent\"?: 0, \"outTangent\"?: 0, \"inWeight\"?: 1/3, \"outWeight\"?: 1/3, \"weightedMode\"?: None|In|Out|Both, \"leftTangentMode\"?: Free|Auto|Linear|Constant|ClampedAuto (default Free), \"rightTangentMode\"?: same, \"broken\"?: false}",
                ["tangents"] = "a Free side uses the given tangent; Auto/ClampedAuto/Linear sides are recomputed by Unity; a Constant (stepped) side has no tangent value (omit it)",
                ["broken"] = "Unity marks a key broken when either side is Linear or Constant"
            },
            ["MinMaxGradient"] = new Dictionary<string, object>
            {
                ["Color"] = "{\"mode\": \"Color\", \"color\": Color} (a bare Color object is shorthand)",
                ["TwoColors"] = "{\"mode\": \"TwoColors\", \"minColor\": Color, \"maxColor\": Color}",
                ["Gradient"] = "{\"mode\": \"Gradient\", \"gradient\": Gradient}",
                ["TwoGradients"] = "{\"mode\": \"TwoGradients\", \"minGradient\": Gradient, \"maxGradient\": Gradient}",
                ["RandomColor"] = "{\"mode\": \"RandomColor\", \"gradient\": Gradient}"
            },
            ["Gradient"] = "{\"mode\"?: Blend|Fixed|PerceptualBlend, \"colorSpace\"?: Uninitialized|Gamma|Linear, \"colorKeys\": [{\"time\", \"color\": {r, g, b}}] (1-8), \"alphaKeys\": [{\"time\", \"alpha\"}] (1-8)}; times 0-1, no repeats; colour keys carry no alpha",
            ["Color"] = "{\"r\", \"g\", \"b\", \"a\"?: 1}: linear floats as Unity stores them (no gamma conversion); r/g/b >= 0 (HDR above 1), a 0-1",
            ["Vector3"] = "{\"x\", \"y\", \"z\"} all required",
            ["Vector2"] = "{\"x\", \"y\"} all required",
            ["AssetReference"] = "\"Assets/.../X.mat\", {\"assetPath\"}, {\"guid\"}, plus \"name\" to pick a sub-asset; built-in assets: {\"assetPath\": \"Resources/unity_builtin_extra\" | \"Library/unity default resources\", \"name\": ...}; null clears a nullable field",
            ["Burst"] = "{\"time\" (s, >= 0), \"count\": MinMaxCurve (>= 0), \"cycleCount\"?: 1 (0 repeats forever), \"repeatInterval\"?: 0.01 (s, > 0), \"probability\"?: 1 (0-1)}",
            ["SubEmitter"] = "{\"system\": {entityId|name|path|instanceId} of a ParticleSystem in the parent's scene (or a path/name string), \"type\": Birth|Collision|Death|Trigger|Manual, \"properties\"?: [InheritColor|InheritSize|InheritRotation|InheritLifetime|InheritDuration] ([] inherits nothing), \"emitProbability\"?: 1 (0-1)}; readback adds missing:true for a broken reference"
        };

        // ===================== particle_find_all =====================

        internal static object FindAllCore(Locator root, bool includeInactive, int limit, int offset)
        {
            if (limit < 1 || limit > FindAllMaxLimit)
                return Error(CodeSemantic, $"limit must be between 1 and {FindAllMaxLimit}, got {limit}.", parameter: "limit");
            if (offset < 0)
                return Error(CodeSemantic, $"offset must be >= 0, got {offset}.", parameter: "offset");

            IEnumerable<GameObject> candidates;
            string rootPath = null;
            if (!root.IsEmpty)
            {
                var (rootGo, error) = GameObjectFinder.FindOrError(root.Name, root.InstanceId, root.Path, entityId: root.EntityId);
                if (error != null)
                    return error;
                rootPath = GameObjectFinder.GetPath(rootGo);
                candidates = rootGo.GetComponentsInChildren<Transform>(true).Select(transform => transform.gameObject);
            }
            else
            {
                // Hierarchy order of the open scenes only: no project assets, no Prefab Mode preview objects, and a stable order for paging.
                candidates = GameObjectFinder.GetSceneObjects();
            }

            var systems = candidates
                .Where(go => go != null && (includeInactive || go.activeInHierarchy))
                .Select(go => go.GetComponent<ParticleSystem>())
                .Where(system => system != null)
                .ToList();

            var page = systems.Skip(offset).Take(limit).Select(DescribeListed).ToArray();
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["root"] = rootPath,
                ["total"] = systems.Count,
                ["count"] = page.Length,
                ["offset"] = offset,
                ["limit"] = limit,
                ["truncated"] = offset + page.Length < systems.Count,
                ["systems"] = page
            };
        }

        private static object DescribeListed(ParticleSystem system)
        {
            var go = system.gameObject;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var main = system.main;
            return new Dictionary<string, object>
            {
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["scene"] = go.scene.name,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["isPlaying"] = system.isPlaying,
                ["isPaused"] = system.isPaused,
                ["duration"] = main.duration,
                ["loop"] = main.loop,
                ["maxParticles"] = main.maxParticles,
                ["emissionEnabled"] = system.emission.enabled,
                ["renderMode"] = renderer != null ? renderer.renderMode.ToString() : null,
                ["material"] = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : null,
                ["isPrefabInstance"] = PrefabUtility.IsPartOfPrefabInstance(go)
            };
        }

        // ===================== particle_get_info / _batch =====================

        internal static object GetInfoCore(Locator locator, bool includeModules)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var go = binding.GameObject;
            JObject modules;
            JToken main;
            JToken renderer;
            if (includeModules)
            {
                modules = new JObject();
                foreach (var module in Registry.ParticleModules)
                {
                    if (module.Id == "main")
                        continue;
                    modules[module.Id] = ReadModule(module, binding);
                }
                main = ReadModule(Registry.Module("main"), binding);
                renderer = binding.Renderer != null ? (JToken)ReadModule(Registry.Renderer, binding) : JValue.CreateNull();
            }
            else
            {
                modules = ModulesEnabled(binding);
                main = MainSummary(binding);
                renderer = RendererSummary(binding);
            }

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["isPrefabInstance"] = PrefabUtility.IsPartOfPrefabInstance(go),
                ["playback"] = PlaybackState(binding.System),
                ["system"] = ReadModule(Registry.System, binding),
                ["main"] = main,
                ["modules"] = modules,
                ["renderer"] = renderer,
                ["burstCount"] = binding.System.emission.burstCount,
                ["detail"] = includeModules ? "full" : "summary; pass includeModules=true or use particle_get_module for every field"
            };
        }

        internal static object GetInfoBatchCore(string items)
        {
            return ParticleSkills.RunBatch<BatchParticleInfoItem>(items, "particle_get_info_batch", BatchItemKeys.Info, atomic: false,
                prepare: (item, prep) =>
                {
                    prep.Target = item.Locator.Describe();
                    prep.Run = () => AsDictionary(GetInfoCore(item.Locator, item.includeModules ?? false));
                },
                describe: item => item.Locator.Describe());
        }

        // ===================== particle_get_module / renderer / bursts =====================

        internal static object GetModuleCore(Locator locator, string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                return Error(CodeMissingParam, "module is required: one of " + string.Join(", ", Registry.Modules.Select(candidate => candidate.Id)) + ".", parameter: "module");
            var module = Registry.Modules.FirstOrDefault(candidate => string.Equals(candidate.Id, moduleId.Trim(), System.StringComparison.Ordinal));
            if (module == null)
                return Error(CodeSemantic, Codec.UnknownKeyMessage(moduleId.Trim(), "module", Registry.Modules.Select(candidate => candidate.Id)), parameter: "module");

            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;
            if (module.TargetsRenderer && RequireRenderer(binding) is object rendererError)
                return rendererError;

            var go = binding.GameObject;
            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["module"] = module.Id,
                ["section"] = module.Section,
                ["phase"] = module.Phase,
                ["configurable"] = module.Configurable,
                ["fields"] = ReadModule(module, binding)
            };
            if (!module.Configurable)
                result["unavailableReason"] = module.UnavailableReason;
            if (module.Collections.Count > 0)
            {
                result["collections"] = module.Collections.Select(collection => new Dictionary<string, object>
                {
                    ["name"] = collection.Name,
                    ["count"] = collection.Count(binding),
                    ["readWith"] = collection.ReadSkill,
                    ["writeWith"] = collection.WriteSkill
                }).ToArray();
            }
            return result;
        }

        internal static object GetRendererCore(Locator locator)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;
            if (RequireRenderer(binding) is object rendererError)
                return rendererError;

            var go = binding.GameObject;
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["rendererEntityId"] = UnityObjectIdUtility.GetEntityId(binding.Renderer),
                ["renderer"] = ReadModule(Registry.Renderer, binding)
            };
        }

        internal static object GetBurstsCore(Locator locator)
        {
            var error = ResolveSystem(locator, out var binding);
            if (error != null)
                return error;

            var go = binding.GameObject;
            var bursts = ReadBursts(binding.System);
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["name"] = go.name,
                ["entityId"] = UnityObjectIdUtility.GetEntityId(go),
                ["instanceId"] = UnityObjectIdUtility.GetObjectId(go),
                ["path"] = GameObjectFinder.GetPath(go),
                ["count"] = bursts.Count,
                ["bursts"] = bursts,
                ["duration"] = binding.System.main.duration,
                ["loop"] = binding.System.main.loop,
                ["emissionEnabled"] = binding.System.emission.enabled
            };
        }

        /// <summary>A skill result as a dictionary, so batch helpers can stamp the item's target on it.</summary>
        internal static Dictionary<string, object> AsDictionary(object result)
        {
            if (result is Dictionary<string, object> dictionary)
                return dictionary;
            var json = JObject.FromObject(result, Newtonsoft.Json.JsonSerializer.Create(SkillsCommon.JsonSettings));
            return json.Properties().ToDictionary(property => property.Name, property => (object)property.Value);
        }
    }
}
#endif

// Producer:Betsy
