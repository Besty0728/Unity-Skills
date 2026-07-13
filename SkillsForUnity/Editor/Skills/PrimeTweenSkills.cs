using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// PrimeTween Free diagnostics, API discovery, and runtime-script generation.
    /// All PrimeTween access is reflective so UnitySkills continues to compile
    /// when the optional package is not installed.
    /// </summary>
    public static class PrimeTweenSkills
    {
        private const string PackageName = "com.kyrylokuzyk.primetween";
        private const string TweenTypeName = "PrimeTween.Tween";
        private const string SequenceTypeName = "PrimeTween.Sequence";
        private const string ConfigTypeName = "PrimeTween.PrimeTweenConfig";
        private const string ShakeTypeName = "PrimeTween.Shake";

        [UnitySkill("primetween_get_status",
            "Get PrimeTween Free installation status, package version, and the available core API types.",
            Category = SkillCategory.PrimeTween, Operation = SkillOperation.Query,
            Tags = new[] { "primetween", "free", "status", "installed", "package" },
            Outputs = new[] { "isInstalled", "packageVersion", "types" },
            RequiresPackages = new[] { PackageName },
            Mode = SkillMode.SemiAuto)]
        public static object PrimeTweenGetStatus()
        {
            var tweenType = FindType(TweenTypeName);
            if (tweenType == null)
            {
                return new
                {
                    isInstalled = false,
                    error = $"PrimeTween Free is not installed. Install the '{PackageName}' package before using PrimeTween skills."
                };
            }

            var packageInfo = PackageInfo.FindForAssembly(tweenType.Assembly);
            var typeNames = new[]
            {
                TweenTypeName,
                SequenceTypeName,
                ConfigTypeName,
                ShakeTypeName
            };

            return new
            {
                isInstalled = true,
                packageName = packageInfo?.name,
                packageVersion = packageInfo?.version,
                assembly = tweenType.Assembly.GetName().Name,
                types = typeNames
                    .Select(FindType)
                    .Where(type => type != null)
                    .Select(type => type.FullName)
                    .OrderBy(name => name)
                    .ToArray()
            };
        }

        [UnitySkill("primetween_get_config",
            "Read PrimeTween's current global runtime configuration. These values are static runtime settings, not a project asset.",
            Category = SkillCategory.PrimeTween, Operation = SkillOperation.Query,
            Tags = new[] { "primetween", "free", "config", "runtime", "query" },
            Outputs = new[] { "success", "properties" },
            RequiresPackages = new[] { PackageName },
            Mode = SkillMode.SemiAuto)]
        public static object PrimeTweenGetConfig()
        {
            var configType = FindType(ConfigTypeName);
            if (configType == null)
            {
                return NoPrimeTween();
            }

            var propertyNames = new[]
            {
                "defaultEase",
                "defaultUpdateType"
            };
            var properties = new Dictionary<string, object>();
            var unavailable = new List<string>();

            foreach (var propertyName in propertyNames)
            {
                var property = configType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property == null || !property.CanRead || property.GetMethod == null || !property.GetMethod.IsPublic)
                {
                    unavailable.Add(propertyName);
                    continue;
                }

                try
                {
                    properties[propertyName] = Stringify(property.GetValue(null));
                }
                catch (Exception exception)
                {
                    unavailable.Add($"{propertyName}: {exception.Message}");
                }
            }

            return new
            {
                success = true,
                properties,
                unavailable,
                note = "PrimeTweenConfig is process-wide runtime state. This skill intentionally does not modify it."
            };
        }

        [UnitySkill("primetween_list_factories",
            "List PrimeTween Free public factory methods on Tween, Sequence, Shake, or PrimeTweenConfig.",
            Category = SkillCategory.PrimeTween, Operation = SkillOperation.Query,
            Tags = new[] { "primetween", "free", "api", "factory", "reflection" },
            Outputs = new[] { "count", "methods" },
            RequiresPackages = new[] { PackageName },
            Mode = SkillMode.SemiAuto)]
        public static object PrimeTweenListFactories(string typeName = "Tween", string methodPrefix = null, int limit = 100)
        {
            var type = ResolveApiType(typeName);
            if (type == null)
            {
                return new
                {
                    error = "PrimeTween is not installed, or typeName must be Tween, Sequence, Shake, or PrimeTweenConfig."
                };
            }

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => !method.IsSpecialName)
                .Where(method => string.IsNullOrEmpty(methodPrefix) ||
                                 method.Name.StartsWith(methodPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(method => method.Name)
                .ThenBy(method => method.GetParameters().Length)
                .Take(Mathf.Max(limit, 1))
                .Select(ToMethodInfo)
                .ToArray();

            return new
            {
                type = type.FullName,
                count = methods.Length,
                methods
            };
        }

        [UnitySkill("primetween_generate_tween_script",
            "Generate a PrimeTween Free MonoBehaviour for a supported Transform animation. The generated script owns and stops its tween; it does not create DOTween-style links.",
            Category = SkillCategory.PrimeTween, Operation = SkillOperation.Create,
            Tags = new[] { "primetween", "free", "generate", "script", "runtime", "tween" },
            Outputs = new[] { "success", "path", "className" },
            RequiresInput = new[] { "className" },
            RequiresPackages = new[] { PackageName },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object PrimeTweenGenerateTweenScript(
            string className,
            string folder = "Assets/Scripts/PrimeTween",
            string namespaceName = null,
            string tweenKind = "LocalPosition",
            float duration = 1f,
            string ease = "OutQuad",
            int cycles = 1,
            string cycleMode = "Restart",
            bool autoPlay = true)
        {
            if (FindType(TweenTypeName) == null)
            {
                return NoPrimeTween();
            }

            var spec = ResolveTransformTween(tweenKind);
            if (spec == null)
            {
                return UnsupportedTween(tweenKind);
            }

            if (TryValidateGeneratorInputs(ease, cycleMode, cycles) is object error)
            {
                return error;
            }

            return WriteGeneratedScript(
                className,
                folder,
                BuildTweenScript(className, namespaceName, spec, duration, ease, cycles, cycleMode, autoPlay));
        }

        [UnitySkill("primetween_generate_sequence_script",
            "Generate a PrimeTween Free MonoBehaviour that composes supported Transform tweens with Sequence.Chain and Sequence.Group.",
            Category = SkillCategory.PrimeTween, Operation = SkillOperation.Create,
            Tags = new[] { "primetween", "free", "generate", "script", "runtime", "sequence" },
            Outputs = new[] { "success", "path", "className" },
            RequiresInput = new[] { "className" },
            RequiresPackages = new[] { PackageName },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "high")]
        public static object PrimeTweenGenerateSequenceScript(
            string className,
            string folder = "Assets/Scripts/PrimeTween",
            string namespaceName = null,
            string tweenKind = "Scale",
            float duration = 0.2f,
            string ease = "OutBack",
            int cycles = 1,
            string sequenceCycleMode = "Restart",
            bool autoPlay = true,
            string stepsJson = null)
        {
            if (FindType(SequenceTypeName) == null)
            {
                return NoPrimeTween();
            }

            var steps = ParseSequenceSteps(stepsJson, tweenKind, duration);
            if (steps == null || steps.Count == 0)
            {
                return new
                {
                    error = "stepsJson must be a JSON array of { op: Chain|Group, tweenKind, duration }."
                };
            }

            var specs = new List<SequenceStep>();
            foreach (var step in steps)
            {
                var spec = ResolveTransformTween(step.tweenKind ?? tweenKind);
                if (spec == null)
                {
                    return UnsupportedTween(step.tweenKind ?? tweenKind);
                }

                if (!string.Equals(step.op, "Chain", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(step.op, "Group", StringComparison.OrdinalIgnoreCase))
                {
                    return new { error = $"Unsupported sequence operation '{step.op}'. Use Chain or Group." };
                }

                specs.Add(new SequenceStep
                {
                    Operation = string.Equals(step.op, "Group", StringComparison.OrdinalIgnoreCase) ? "Group" : "Chain",
                    Tween = spec,
                    Duration = step.duration > 0f ? step.duration : duration
                });
            }

            if (TryValidateGeneratorInputs(ease, sequenceCycleMode, cycles, SequenceTypeName + "+SequenceCycleMode") is object error)
            {
                return error;
            }

            return WriteGeneratedScript(
                className,
                folder,
                BuildSequenceScript(className, namespaceName, specs, ease, cycles, sequenceCycleMode, autoPlay));
        }

        private sealed class TransformTweenSpec
        {
            public string Method;
            public string FieldName;
            public string DefaultValue;
            public string StartValueExpression;
        }

        private sealed class SequenceStepInput
        {
            public string op { get; set; }
            public string tweenKind { get; set; }
            public float duration { get; set; }
        }

        private sealed class SequenceStep
        {
            public string Operation;
            public TransformTweenSpec Tween;
            public float Duration;
        }

        private static object NoPrimeTween()
        {
            return new
            {
                error = $"PrimeTween Free is not installed. Install the '{PackageName}' package before using PrimeTween skills."
            };
        }

        private static Type ResolveApiType(string typeName)
        {
            if (string.Equals(typeName, "Tween", StringComparison.OrdinalIgnoreCase))
            {
                return FindType(TweenTypeName);
            }

            if (string.Equals(typeName, "Sequence", StringComparison.OrdinalIgnoreCase))
            {
                return FindType(SequenceTypeName);
            }

            if (string.Equals(typeName, "Shake", StringComparison.OrdinalIgnoreCase))
            {
                return FindType(ShakeTypeName);
            }

            if (string.Equals(typeName, "PrimeTweenConfig", StringComparison.OrdinalIgnoreCase))
            {
                return FindType(ConfigTypeName);
            }

            return null;
        }

        private static Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static object ToMethodInfo(MethodInfo method)
        {
            var parameters = method
                .GetParameters()
                .Select(parameter => new
                {
                    name = parameter.Name,
                    type = FriendlyTypeName(parameter.ParameterType),
                    optional = parameter.IsOptional,
                    defaultValue = parameter.IsOptional ? parameter.DefaultValue?.ToString() : null
                })
                .ToArray();

            return new
            {
                name = method.Name,
                returnType = FriendlyTypeName(method.ReturnType),
                parameters,
                signature = $"{FriendlyTypeName(method.ReturnType)} {method.Name}({string.Join(", ", parameters.Select(parameter => $"{parameter.type} {parameter.name}"))})"
            };
        }

        private static string FriendlyTypeName(Type type)
        {
            if (type == null)
            {
                return null;
            }

            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            var name = type.Name;
            var genericMarker = name.IndexOf('`');
            if (genericMarker >= 0)
            {
                name = name.Substring(0, genericMarker);
            }

            return $"{type.Namespace}.{name}<{string.Join(",", type.GetGenericArguments().Select(FriendlyTypeName))}>";
        }

        private static object Stringify(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is Enum enumValue)
            {
                return enumValue.ToString();
            }

            return value;
        }

        private static TransformTweenSpec ResolveTransformTween(string tweenKind)
        {
            switch ((tweenKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "position":
                    return new TransformTweenSpec { Method = "Position", FieldName = "endPosition", DefaultValue = "new Vector3(0f, 1f, 0f)" };
                case "localposition":
                    return new TransformTweenSpec { Method = "LocalPosition", FieldName = "endLocalPosition", DefaultValue = "new Vector3(0f, 1f, 0f)" };
                case "eulerangles":
                    return new TransformTweenSpec { Method = "EulerAngles", FieldName = "endEulerAngles", DefaultValue = "new Vector3(0f, 180f, 0f)", StartValueExpression = "_target.eulerAngles" };
                case "localeulerangles":
                    return new TransformTweenSpec { Method = "LocalEulerAngles", FieldName = "endLocalEulerAngles", DefaultValue = "new Vector3(0f, 180f, 0f)", StartValueExpression = "_target.localEulerAngles" };
                case "scale":
                    return new TransformTweenSpec { Method = "Scale", FieldName = "endScale", DefaultValue = "Vector3.one * 1.2f" };
                default:
                    return null;
            }
        }

        private static object UnsupportedTween(string tweenKind)
        {
            return new
            {
                error = $"Unsupported PrimeTween generator tweenKind '{tweenKind}'. Supported values: Position, LocalPosition, EulerAngles, LocalEulerAngles, Scale. Use primetween_list_factories to inspect other PrimeTween APIs."
            };
        }

        private static object TryValidateGeneratorInputs(
            string ease,
            string cycleMode,
            int cycles,
            string cycleModeTypeName = "PrimeTween.CycleMode")
        {
            if (cycles < -1)
            {
                return new { error = "cycles must be -1 (infinite) or a positive integer." };
            }

            if (!IsEnumValue("PrimeTween.Ease", ease))
            {
                return new { error = $"Invalid PrimeTween Ease value '{ease}'. Use primetween_list_factories typeName=Tween to inspect available APIs." };
            }

            if (!IsEnumValue(cycleModeTypeName, cycleMode))
            {
                return new { error = $"Invalid PrimeTween cycle mode '{cycleMode}'." };
            }

            return null;
        }

        private static bool IsEnumValue(string typeName, string value)
        {
            var enumType = FindType(typeName);
            return enumType != null &&
                   enumType.IsEnum &&
                   !string.IsNullOrWhiteSpace(value) &&
                   Enum.GetNames(enumType).Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
        }

        private static List<SequenceStepInput> ParseSequenceSteps(string stepsJson, string tweenKind, float duration)
        {
            if (string.IsNullOrWhiteSpace(stepsJson))
            {
                return new List<SequenceStepInput>
                {
                    new SequenceStepInput { op = "Chain", tweenKind = tweenKind, duration = duration },
                    new SequenceStepInput { op = "Group", tweenKind = "Scale", duration = duration },
                    new SequenceStepInput { op = "Chain", tweenKind = tweenKind, duration = duration }
                };
            }

            try
            {
                return JsonConvert.DeserializeObject<List<SequenceStepInput>>(stepsJson);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static object WriteGeneratedScript(string className, string folder, string content)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                return new { error = "className is required" };
            }

            if (!IsValidClassName(className))
            {
                return new { error = "className must be a valid C# identifier and must not contain path separators." };
            }

            if (Validate.SafePath(folder, "folder") is object folderError)
            {
                return folderError;
            }

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            var path = Path.Combine(folder, className + ".cs").Replace("\\", "/");
            if (File.Exists(path))
            {
                return new { error = $"Script already exists: {path}" };
            }

            File.WriteAllText(path, content, SkillsCommon.Utf8NoBom);
            AssetDatabase.ImportAsset(path);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null)
            {
                WorkflowManager.SnapshotCreatedAsset(asset);
            }

            return new
            {
                success = true,
                path,
                className,
                nextAction = "Unity may start compiling. After compilation finishes, call script_get_compile_feedback if needed."
            };
        }

        private static bool IsValidClassName(string className)
        {
            if (string.IsNullOrWhiteSpace(className) ||
                className.Contains("/") ||
                className.Contains("\\") ||
                className.Contains(".."))
            {
                return false;
            }

            if (!char.IsLetter(className[0]) && className[0] != '_')
            {
                return false;
            }

            return className.All(character => char.IsLetterOrDigit(character) || character == '_');
        }

        private static string BuildTweenScript(
            string className,
            string namespaceName,
            TransformTweenSpec spec,
            float duration,
            string ease,
            int cycles,
            string cycleMode,
            bool autoPlay)
        {
            var builder = CreateScriptHeader(className, namespaceName);
            builder.AppendLine("    [SerializeField] private Transform _target;");
            builder.AppendLine($"    [SerializeField] private Vector3 _{spec.FieldName} = {spec.DefaultValue};");
            builder.AppendLine($"    [SerializeField] private float _duration = {FloatLiteral(duration)};");
            builder.AppendLine($"    [SerializeField] private Ease _ease = Ease.{SanitizeEnumName(ease, "OutQuad")};");
            builder.AppendLine($"    [SerializeField] private int _cycles = {NormalizeCycles(cycles)};");
            builder.AppendLine($"    [SerializeField] private CycleMode _cycleMode = CycleMode.{SanitizeEnumName(cycleMode, "Restart")};");
            builder.AppendLine($"    [SerializeField] private bool _autoPlay = {BoolLiteral(autoPlay)};");
            builder.AppendLine();
            builder.AppendLine("    private Tween _tween;");
            builder.AppendLine();
            builder.AppendLine("    private void Awake()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_target == null)");
            builder.AppendLine("        {");
            builder.AppendLine("            _target = transform;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private void OnEnable()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_autoPlay)");
            builder.AppendLine("        {");
            builder.AppendLine("            Play();");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private void OnDisable()");
            builder.AppendLine("    {");
            builder.AppendLine("        Stop();");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public void Play()");
            builder.AppendLine("    {");
            builder.AppendLine("        Stop();");
            builder.AppendLine("        if (_target == null)");
            builder.AppendLine("        {");
            builder.AppendLine("            return;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine($"        _tween = {BuildTweenCall(spec, "_duration", "_ease, _cycles, _cycleMode")};");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public void Stop()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_tween.isAlive)");
            builder.AppendLine("        {");
            builder.AppendLine("            _tween.Stop();");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            CloseScript(builder, namespaceName);
            return builder.ToString();
        }

        private static string BuildSequenceScript(
            string className,
            string namespaceName,
            List<SequenceStep> steps,
            string ease,
            int cycles,
            string sequenceCycleMode,
            bool autoPlay)
        {
            var uniqueSpecs = steps
                .Select(step => step.Tween)
                .GroupBy(spec => spec.FieldName)
                .Select(group => group.First())
                .ToArray();
            var builder = CreateScriptHeader(className, namespaceName);
            builder.AppendLine("    [SerializeField] private Transform _target;");
            foreach (var spec in uniqueSpecs)
            {
                builder.AppendLine($"    [SerializeField] private Vector3 _{spec.FieldName} = {spec.DefaultValue};");
            }
            builder.AppendLine($"    [SerializeField] private Ease _ease = Ease.{SanitizeEnumName(ease, "OutBack")};");
            builder.AppendLine($"    [SerializeField] private int _cycles = {NormalizeCycles(cycles)};");
            builder.AppendLine($"    [SerializeField] private Sequence.SequenceCycleMode _cycleMode = Sequence.SequenceCycleMode.{SanitizeEnumName(sequenceCycleMode, "Restart")};");
            builder.AppendLine($"    [SerializeField] private bool _autoPlay = {BoolLiteral(autoPlay)};");
            builder.AppendLine();
            builder.AppendLine("    private Sequence _sequence;");
            builder.AppendLine();
            builder.AppendLine("    private void Awake()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_target == null)");
            builder.AppendLine("        {");
            builder.AppendLine("            _target = transform;");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private void OnEnable()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_autoPlay)");
            builder.AppendLine("        {");
            builder.AppendLine("            Play();");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private void OnDisable()");
            builder.AppendLine("    {");
            builder.AppendLine("        Stop();");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public void Play()");
            builder.AppendLine("    {");
            builder.AppendLine("        Stop();");
            builder.AppendLine("        if (_target == null)");
            builder.AppendLine("        {");
            builder.AppendLine("            return;");
            builder.AppendLine("        }");
            builder.AppendLine();
            builder.AppendLine("        _sequence = Sequence.Create(_cycles, _cycleMode, _ease);");
            foreach (var step in steps)
            {
                builder.AppendLine($"        _sequence.{step.Operation}({BuildTweenCall(step.Tween, FloatLiteral(step.Duration), "_ease")});");
            }
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    public void Stop()");
            builder.AppendLine("    {");
            builder.AppendLine("        if (_sequence.isAlive)");
            builder.AppendLine("        {");
            builder.AppendLine("            _sequence.Stop();");
            builder.AppendLine("        }");
            builder.AppendLine("    }");
            CloseScript(builder, namespaceName);
            return builder.ToString();
        }

        private static StringBuilder CreateScriptHeader(string className, string namespaceName)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using PrimeTween;");
            builder.AppendLine("using UnityEngine;");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine($"namespace {namespaceName}");
                builder.AppendLine("{");
            }
            builder.AppendLine($"public sealed class {className} : MonoBehaviour");
            builder.AppendLine("{");
            return builder;
        }

        private static void CloseScript(StringBuilder builder, string namespaceName)
        {
            builder.AppendLine("}");
            if (!string.IsNullOrWhiteSpace(namespaceName))
            {
                builder.AppendLine("}");
            }
        }

        private static string FloatLiteral(float value)
        {
            return value.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture) + "f";
        }

        private static string BoolLiteral(bool value)
        {
            return value ? "true" : "false";
        }

        private static string BuildTweenCall(TransformTweenSpec spec, string duration, string settings)
        {
            var arguments = spec.StartValueExpression == null
                ? $"_target, _{spec.FieldName}, {duration}, {settings}"
                : $"_target, {spec.StartValueExpression}, _{spec.FieldName}, {duration}, {settings}";
            return $"Tween.{spec.Method}({arguments})";
        }

        private static int NormalizeCycles(int cycles)
        {
            return cycles == -1 ? -1 : Mathf.Max(cycles, 1);
        }

        private static string SanitizeEnumName(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.All(character => char.IsLetterOrDigit(character) || character == '_'))
            {
                return fallback;
            }

            return value;
        }
    }
}
