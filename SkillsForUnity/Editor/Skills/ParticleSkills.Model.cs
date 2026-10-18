#if COM_UNITY_MODULES_PARTICLESYSTEM
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnitySkills
{
    public static partial class ParticleSkills
    {
        /// <summary>The JSON value shape of one registered field; the codec reads and writes exactly these.</summary>
        internal enum FieldKind
        {
            Bool,
            Int,
            UInt,
            Float,
            Enum,
            Vector2,
            Vector3,
            Color,
            Curve,
            Gradient,
            Asset,
            SortingLayer
        }

        /// <summary>The live objects one target exposes to field readers and writers.</summary>
        internal sealed class ParticleBinding
        {
            public GameObject GameObject;
            public ParticleSystem System;
            public ParticleSystemRenderer Renderer;
        }

        /// <summary>
        /// One field of one module: its JSON name, value shape, bounds, unit and a strongly typed reader/writer pair.
        /// The same description drives capabilities, parsing, validation, readback and the dryRun preview, so a field
        /// exists everywhere or nowhere.
        /// </summary>
        internal sealed class FieldSpec
        {
            public string Name;
            public FieldKind Kind;
            public Type EnumType;
            /// <summary>Enum members accepted on write; null means every non-obsolete member.</summary>
            public string[] WritableEnumValues;
            public Type AssetType;
            /// <summary>Only a nullable field accepts JSON null, and null clears it.</summary>
            public bool Nullable;
            public string Unit;
            /// <summary>Bounds of a number, of every component of a vector, and of the constants of a Constant/TwoConstants curve.</summary>
            public double? Min;
            public bool MinExclusive;
            public double? Max;
            /// <summary>Curve modes accepted on write; null means all four.</summary>
            public ParticleSystemCurveMode[] CurveModes;
            /// <summary>Gradient modes accepted on write; null means all five.</summary>
            public ParticleSystemGradientMode[] GradientModes;
            /// <summary>Allowed key time range of the curves of this property (normalized lifetime or duration for every P0 field).</summary>
            public float KeyTimeMin;
            public float KeyTimeMax = 1f;
            public string KeyTimeMeaning;
            public string Description;
            public bool Writable = true;
            public string ReadOnlyReason;
            public Func<ParticleBinding, object> Read;
            public Action<ParticleBinding, object> Write;
            public ModuleAdapter Module;
            /// <summary>
            /// The value a freshly added ParticleSystem has, for the fields cross-field rules read. Validation of
            /// particle_create / particle_add_system uses it because no system exists yet (a dryRun must not make one);
            /// a test compares every declared default with a real new system.
            /// </summary>
            public object Default;
            public bool HasDefault;

            /// <summary>The field's patch path below configJson, e.g. "modules.main.startLifetime" or "renderer.renderMode".</summary>
            public string Path => Module.Path + "." + Name;
        }

        /// <summary>
        /// A flag that switches a module between one uniform field and per-axis fields (startSize3D, separateAxes).
        /// With the flag off only <see cref="Uniform"/> fields apply; with it on only <see cref="Axes"/> do. The uniform
        /// field and the X axis share storage in Unity, which is why one of the two sets is always hidden.
        /// </summary>
        internal sealed class AxisGroup
        {
            public string Flag;
            public string[] Uniform;
            public string[] Axes;
        }

        /// <summary>
        /// Curve fields Unity evaluates as one triplet: the Inspector offers a single mode for them and Velocity over
        /// Lifetime logs "curves must all be in the same mode" otherwise. <see cref="WhenFlag"/>, when set, limits the
        /// rule to the per-axis state of an <see cref="AxisGroup"/>.
        /// </summary>
        internal sealed class SameModeGroup
        {
            public string[] Fields;
            public string WhenFlag;
        }

        /// <summary>A list owned by a module that has its own read/write skills (bursts).</summary>
        internal sealed class CollectionInfo
        {
            public string Name;
            public string ReadSkill;
            public string WriteSkill;
            public Func<ParticleBinding, int> Count;
        }

        /// <summary>
        /// One Unity module (or the system / renderer sections) as the API sees it. P1 engineers add fields here; a
        /// module with <see cref="Configurable"/> false is readable (enabled flag) but rejects writes.
        /// </summary>
        internal sealed class ModuleAdapter
        {
            public string Id;
            /// <summary>"system", "modules" or "renderer": where the module sits in configJson.</summary>
            public string Section;
            public string UnityName;
            public string Phase = "P0";
            public string Description;
            public bool Configurable = true;
            public string UnavailableReason;
            public readonly List<FieldSpec> Fields = new List<FieldSpec>();
            public readonly List<AxisGroup> AxisGroups = new List<AxisGroup>();
            public readonly List<SameModeGroup> SameModeGroups = new List<SameModeGroup>();
            public readonly List<CollectionInfo> Collections = new List<CollectionInfo>();
            /// <summary>Module-specific cross-field rules, run after every field of the patch parsed.</summary>
            public Action<ModuleCheck> Check;

            public string Path => Section == SectionModules ? SectionModules + "." + Id : Id;
            public bool TargetsRenderer => Section == SectionRenderer;

            public FieldSpec Field(string name)
            {
                for (int i = 0; i < Fields.Count; i++)
                {
                    if (string.Equals(Fields[i].Name, name, StringComparison.Ordinal))
                        return Fields[i];
                }
                return null;
            }

            public FieldSpec EnabledField => Field("enabled");
        }

        internal const string SectionSystem = "system";
        internal const string SectionModules = "modules";
        internal const string SectionRenderer = "renderer";

        /// <summary>One rejected input, located by its JSON path (e.g. "items[2].configJson.modules.main.startLifetime.min").</summary>
        internal sealed class Problem
        {
            public string Path;
            public string Message;
            public string Code;
        }

        /// <summary>
        /// Every problem of one request, so a single response lists all of them instead of the first. Capped, because
        /// an invalid 100-item batch would otherwise answer with thousands of lines.
        /// </summary>
        internal sealed class Problems
        {
            public const int MaxReported = 40;
            public readonly List<Problem> Items = new List<Problem>();
            public int Omitted;

            public bool Any => Items.Count > 0 || Omitted > 0;
            public int Count => Items.Count + Omitted;

            public void Add(string path, string message, string code = CodeSemantic)
            {
                if (Items.Count >= MaxReported)
                {
                    Omitted++;
                    return;
                }
                Items.Add(new Problem { Path = path, Message = message, Code = code });
            }

            public void AddRange(Problems other)
            {
                if (other == null)
                    return;
                foreach (var problem in other.Items)
                    Add(problem.Path, problem.Message, problem.Code);
                Omitted += other.Omitted;
            }

            /// <summary>The error code of the whole call: the first problem decides, as the router does for its own buckets.</summary>
            public string LeadingCode => Items.Count > 0 ? Items[0].Code : CodeSemantic;

            public object[] ToJson()
            {
                var list = Items.Select(problem => (object)new Dictionary<string, object>
                {
                    ["path"] = problem.Path,
                    ["error"] = problem.Message,
                    ["errorCode"] = problem.Code
                }).ToList();
                if (Omitted > 0)
                    list.Add(new Dictionary<string, object> { ["path"] = "", ["error"] = $"(+{Omitted} more problem(s) omitted)" });
                return list.ToArray();
            }
        }

        internal const string CodeSemantic = "SEMANTIC_INVALID";
        internal const string CodeMissingParam = "MISSING_PARAM";
        internal const string CodeTypeMismatch = "TYPE_MISMATCH";
        internal const string CodeInvalidJson = "INVALID_JSON";
        internal const string CodeTargetNotFound = "TARGET_NOT_FOUND";
        internal const string CodeMissingPackage = "MISSING_PACKAGE";

        /// <summary>One parsed field value of a patch, ready to be written.</summary>
        internal sealed class FieldWrite
        {
            public FieldSpec Field;
            public object Value;
            public string Path;
        }

        /// <summary>
        /// A fully parsed and validated set of field writes for one target. Nothing in it has touched Unity state yet;
        /// <see cref="Writes"/> keeps registry order, which is also the order the fields are applied in.
        /// </summary>
        internal sealed class Patch
        {
            public readonly List<FieldWrite> Writes = new List<FieldWrite>();

            public FieldWrite Find(FieldSpec field)
            {
                for (int i = 0; i < Writes.Count; i++)
                {
                    if (ReferenceEquals(Writes[i].Field, field))
                        return Writes[i];
                }
                return null;
            }

            public bool TouchesSystem => Writes.Any(write => !write.Field.Module.TargetsRenderer);
            public bool TouchesRenderer => Writes.Any(write => write.Field.Module.TargetsRenderer);
            public bool IsEmpty => Writes.Count == 0;

            public IEnumerable<ModuleAdapter> Modules => Writes.Select(write => write.Field.Module).Distinct();
        }

        /// <summary>
        /// What a module's cross-field rule sees: the patch, the live state when the target exists (null for a target an
        /// earlier step of a dry-run batch will create), and where to report.
        /// </summary>
        internal sealed class ModuleCheck
        {
            public ModuleAdapter Module;
            public Patch Patch;
            public ParticleBinding Binding;
            public Problems Problems;
            public List<string> Warnings;
            public string BasePath;
            /// <summary>True when the target is a system about to be created: unknown live values read as registry defaults.</summary>
            public bool UseDefaults;

            public bool Has(string field) => Requested(field, out _);

            public bool Requested(string field, out object value)
            {
                value = null;
                var spec = Module.Field(field);
                var write = spec != null ? Patch.Find(spec) : null;
                if (write == null)
                    return false;
                value = write.Value;
                return true;
            }

            /// <summary>The value the field will hold after the patch: the requested one, else the live one, else null when unknown.</summary>
            public object Effective(string field) => Effective(Module, field);

            public object Effective(ModuleAdapter module, string field)
            {
                var spec = module.Field(field);
                if (spec == null)
                    return null;
                var write = Patch.Find(spec);
                if (write != null)
                    return write.Value;
                if (Binding != null)
                    return spec.Read(Binding);
                return UseDefaults && spec.HasDefault ? spec.Default : null;
            }

            public string PathOf(string field) => BasePath + "." + Module.Path + "." + field;

            public void Error(string field, string message) => Problems.Add(PathOf(field), message);

            public void Warn(string message)
            {
                if (!Warnings.Contains(message))
                    Warnings.Add(message);
            }
        }
    }
}
#endif

// Producer:Betsy
