using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// 可选包 Unity Behavior（com.unity.behavior）的反射桥。
    ///
    /// 该包不是 com.besty.unity-skills 的声明依赖，所以这里所有类型、成员、方法都在运行时
    /// 按名解析，对任何 Unity.Behavior 类型都没有编译期引用。类型查找有缓存；每次成员查找
    /// 都做 null 检查，缺失时给出结构化的 API 不匹配错误而非抛异常。
    ///
    /// 下列成员名取自 com.unity.behavior 1.0.16 源码（needle-mirror/com.unity.behavior），
    /// 公开部分与已发布的 Scripting API 交叉核对过：
    /// https://docs.unity3d.com/Packages/com.unity.behavior@1.0/api/Unity.Behavior.html
    /// 属于 internal 的成员（BehaviorAuthoringGraph、GraphAsset、BlackboardAsset）
    /// 在候选列表处的注释里标注了来源源码文件。
    /// </summary>
    internal static class BehaviorReflectionHelper
    {
        public const string PackageId = "com.unity.behavior";
        public const string DocsUrl = "https://docs.unity3d.com/Packages/com.unity.behavior@1.0/manual/index.html";

        private const BindingFlags InstanceFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ==================================================================================
        // 类型解析（带缓存）
        // ==================================================================================

        /// <summary>
        /// 短名 -> 候选全限定名，按顺序逐个探测。列多个候选是为了 1.x 线内发生命名空间
        /// 迁移时仍能解析到。
        /// </summary>
        private static readonly Dictionary<string, string[]> TypeCandidates =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // 运行时部分 —— public，Scripting API 有文档。
                ["BehaviorGraphAgent"] = new[] { "Unity.Behavior.BehaviorGraphAgent" },
                ["BehaviorGraph"] = new[] { "Unity.Behavior.BehaviorGraph" },
                ["BlackboardReference"] = new[] { "Unity.Behavior.BlackboardReference" },
                ["Blackboard"] = new[] { "Unity.Behavior.Blackboard" },
                ["BlackboardVariable"] = new[] { "Unity.Behavior.BlackboardVariable" },
                ["RuntimeBlackboardAsset"] = new[] { "Unity.Behavior.RuntimeBlackboardAsset" },

                // 编辑（authoring）部分 —— internal 类型，源码位于 Authoring/Asset/*.cs 与
                // Tools/Graph/Asset/*.cs；Assembly.GetType 能正常解析 internal 类型。
                ["BehaviorAuthoringGraph"] = new[] { "Unity.Behavior.BehaviorAuthoringGraph" },
                ["BehaviorBlackboardAuthoringAsset"] = new[] { "Unity.Behavior.BehaviorBlackboardAuthoringAsset" },
                ["GraphAsset"] = new[] { "Unity.Behavior.GraphFramework.GraphAsset" },
                ["BlackboardAsset"] = new[] { "Unity.Behavior.GraphFramework.BlackboardAsset" },

                // authoring 资源用到的 public 模型类型。
                ["VariableModel"] = new[] { "Unity.Behavior.GraphFramework.VariableModel" },
                ["NodeModel"] = new[] { "Unity.Behavior.GraphFramework.NodeModel" }
            };

        private static readonly Dictionary<string, Type> ResolvedTypes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        public static Type Resolve(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return null;
            if (ResolvedTypes.TryGetValue(shortName, out var cached)) return cached;

            Type found = null;
            if (TypeCandidates.TryGetValue(shortName, out var candidates))
            {
                foreach (var fullName in candidates)
                {
                    found = SkillsCommon.FindTypeByName(fullName);
                    if (found != null) break;
                }
            }

            ResolvedTypes[shortName] = found;
            return found;
        }

        public static Type AgentType => Resolve("BehaviorGraphAgent");
        public static Type GraphType => Resolve("BehaviorGraph");
        public static Type AuthoringGraphType => Resolve("BehaviorAuthoringGraph");
        public static Type BlackboardAssetType => Resolve("BlackboardAsset");
        public static Type BlackboardVariableType => Resolve("BlackboardVariable");
        public static Type VariableModelType => Resolve("VariableModel");

        /// <summary>agent 组件与运行时图类型都能解析出来，即视为该包可用。</summary>
        public static bool IsInstalled => AgentType != null && GraphType != null;

        /// <summary>已安装的包版本；包管理器没见过该包时返回 null。</summary>
        public static string InstalledVersion
        {
            get
            {
                try { return PackageManagerHelper.GetInstalledVersion(PackageId); }
                catch { return null; }
            }
        }

        /// <summary>缺包时所有技能统一返回的结构化响应。</summary>
        public static object NotInstalled() => new
        {
            error = "Unity Behavior package (com.unity.behavior) is not installed. " +
                    "All behavior_* skills require it.",
            errorCode = "PACKAGE_NOT_INSTALLED",
            package = PackageId,
            suggestedFixes = new[]
            {
                "Install it with the package_install skill: packageName=\"com.unity.behavior\"",
                "Or install manually: Window > Package Manager > Unity Registry > Behavior",
                "After installing, wait for the Domain Reload to finish, then call behavior_status to confirm"
            },
            retryStrategy = SkillErrorResponse.RetryInstallAndRetry,
            docs = DocsUrl
        };

        /// <summary>包能解析到、但成员布局与本集成不再匹配时的结构化响应。</summary>
        public static object ApiMismatch(string detail) => new
        {
            error = $"Unity Behavior API mismatch: {detail}",
            errorCode = "API_MISMATCH",
            package = PackageId,
            installedVersion = InstalledVersion,
            validatedVersion = "1.0.16",
            suggestedFixes = new[]
            {
                "The installed com.unity.behavior version exposes a different member layout than this integration expects.",
                "Edit the graph in the Behavior editor window instead, or edit the .asset text directly (see the yaml-editing advisory).",
                "Do not retry with different argument shapes — this is a version mismatch, not a bad argument."
            },
            retryStrategy = SkillErrorResponse.Abort,
            docs = DocsUrl
        };

        public static void ClearCache() => ResolvedTypes.Clear();

        // ==================================================================================
        // 成员访问 —— 每次查找失败都返回结构化错误，绝不抛异常
        // ==================================================================================

        /// <summary>
        /// 按候选名依次查找并读取第一个匹配的成员（先属性、后字段）。
        /// 全部候选都不存在时返回 false 并给出描述性错误。
        /// </summary>
        public static bool TryGetMember(object target, out object value, out string error, params string[] names)
        {
            value = null;
            error = null;

            if (target == null)
            {
                error = "target object is null";
                return false;
            }

            var type = target.GetType();
            foreach (var name in names)
            {
                var property = FindProperty(type, name);
                if (property != null && property.CanRead)
                {
                    try
                    {
                        value = property.GetValue(target);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = $"{type.Name}.{name} getter threw: {ex.InnerException?.Message ?? ex.Message}";
                        return false;
                    }
                }

                var field = FindField(type, name);
                if (field != null)
                {
                    try
                    {
                        value = field.GetValue(target);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = $"{type.Name}.{name} field read threw: {ex.InnerException?.Message ?? ex.Message}";
                        return false;
                    }
                }
            }

            error = $"{type.Name} exposes none of [{string.Join(", ", names)}]";
            return false;
        }

        /// <summary>读取成员，不存在时返回 null。仅用于描述性输出。</summary>
        public static object GetMemberOrNull(object target, params string[] names)
        {
            return TryGetMember(target, out var value, out _, names) ? value : null;
        }

        public static bool TrySetMember(object target, object value, out string error, params string[] names)
        {
            error = null;

            if (target == null)
            {
                error = "target object is null";
                return false;
            }

            var type = target.GetType();
            foreach (var name in names)
            {
                var property = FindProperty(type, name);
                if (property != null && property.CanWrite)
                {
                    try
                    {
                        property.SetValue(target, value);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = $"{type.Name}.{name} setter threw: {ex.InnerException?.Message ?? ex.Message}";
                        return false;
                    }
                }

                var field = FindField(type, name);
                if (field != null)
                {
                    try
                    {
                        field.SetValue(target, value);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = $"{type.Name}.{name} field write threw: {ex.InnerException?.Message ?? ex.Message}";
                        return false;
                    }
                }
            }

            error = $"{type.Name} exposes no writable member among [{string.Join(", ", names)}]";
            return false;
        }

        /// <summary>按名调用无参或简单参数的实例方法，方法不存在时不报错。</summary>
        public static bool TryInvoke(object target, string methodName, object[] args, out object result, out string error)
        {
            result = null;
            error = null;

            if (target == null)
            {
                error = "target object is null";
                return false;
            }

            var argCount = args?.Length ?? 0;
            var method = target.GetType()
                .GetMethods(InstanceFlags)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, methodName, StringComparison.Ordinal) &&
                    !candidate.IsGenericMethodDefinition &&
                    candidate.GetParameters().Length == argCount);

            if (method == null)
            {
                error = $"{target.GetType().Name}.{methodName}({argCount} args) was not found";
                return false;
            }

            try
            {
                result = method.Invoke(target, args ?? Array.Empty<object>());
                return true;
            }
            catch (Exception ex)
            {
                error = $"{target.GetType().Name}.{methodName} threw: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
        }

        private static PropertyInfo FindProperty(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(name, InstanceFlags | BindingFlags.DeclaredOnly);
                if (property != null) return property;
            }
            return null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField(name, InstanceFlags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        // ==================================================================================
        // 资源查找
        // ==================================================================================

        /// <summary>
        /// 定位 behavior graph 资源。主过滤器用 authoring 资源类型（"Behavior Graph" .asset
        /// 文件的主资源）；再以运行时类型兜底，应对类型索引只认烘焙出的子资源的情况。
        /// </summary>
        public static string[] FindGraphAssetPaths(string folder)
        {
            var roots = string.IsNullOrWhiteSpace(folder)
                ? new[] { "Assets" }
                : new[] { folder.Replace('\\', '/').TrimEnd('/') };

            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var filter in new[] { "t:BehaviorAuthoringGraph", "t:BehaviorGraph" })
            {
                string[] guids;
                try { guids = AssetDatabase.FindAssets(filter, roots); }
                catch { continue; }

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                    paths.Add(path);
                }

                // authoring 过滤器一旦有命中就以它为准。
                if (paths.Count > 0) break;
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths.ToArray();
        }

        /// <summary>加载指定路径上的 authoring 图（主资源）；不是的话说明原因。</summary>
        public static bool TryLoadAuthoringGraph(string assetPath, out UnityEngine.Object graph, out object error)
        {
            graph = null;
            error = null;

            var authoringType = AuthoringGraphType;
            if (authoringType == null)
            {
                error = ApiMismatch("type Unity.Behavior.BehaviorAuthoringGraph was not found");
                return false;
            }

            if (!SkillsCommon.PathExists(assetPath))
            {
                error = new { error = $"Asset not found: {assetPath}" };
                return false;
            }

            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (main != null && authoringType.IsInstanceOfType(main))
            {
                graph = main;
                return true;
            }

            var nested = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .FirstOrDefault(asset => asset != null && authoringType.IsInstanceOfType(asset));
            if (nested != null)
            {
                graph = nested;
                return true;
            }

            error = new
            {
                error = $"Not a Behavior Graph asset: {assetPath}",
                suggestedFixes = new[] { "Call behavior_graph_list to see the behavior graph assets in this project." }
            };
            return false;
        }

        /// <summary>
        /// 取 behavior graph 文件中作为子资源存放的、已烘焙的运行时 BehaviorGraph。
        /// BehaviorGraphAgent.Graph 要的是这个类型，不是 authoring 资源。
        /// </summary>
        public static bool TryLoadRuntimeGraph(string assetPath, out UnityEngine.Object runtimeGraph, out object error)
        {
            runtimeGraph = null;
            error = null;

            var graphType = GraphType;
            if (graphType == null)
            {
                error = ApiMismatch("type Unity.Behavior.BehaviorGraph was not found");
                return false;
            }

            var candidate = AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .FirstOrDefault(asset => asset != null && graphType.IsInstanceOfType(asset));

            if (candidate == null)
            {
                error = new
                {
                    error = $"No baked BehaviorGraph sub-asset inside {assetPath}. " +
                            "The authoring graph has never been compiled.",
                    errorCode = "RUNTIME_GRAPH_MISSING",
                    suggestedFixes = new[]
                    {
                        "Open the graph once in the Behavior editor window so Unity bakes the runtime graph.",
                        "Or reimport the asset (asset_reimport) and retry."
                    }
                };
                return false;
            }

            runtimeGraph = candidate;
            return true;
        }

        /// <summary>取存放设计期变量列表的 authoring blackboard 资源。</summary>
        public static bool TryGetAuthoringBlackboard(UnityEngine.Object authoringGraph, out object blackboard, out object error)
        {
            blackboard = null;
            error = null;

            // GraphAsset.Blackboard 是 public 字段（Tools/Graph/Asset/GraphAsset.cs）；
            // MainBlackboardAuthoringAsset 是 BehaviorAuthoringGraph 层的访问器。
            if (!TryGetMember(authoringGraph, out var value, out var memberError, "Blackboard", "MainBlackboardAuthoringAsset"))
            {
                error = ApiMismatch(memberError);
                return false;
            }

            // Unity 重载的 == 还能识别已销毁 / 引用丢失的资源。
            var isMissing = value == null || (value is UnityEngine.Object unityObject && unityObject == null);
            if (isMissing)
            {
                error = new
                {
                    error = $"Behavior graph '{authoringGraph.name}' has no blackboard asset yet.",
                    suggestedFixes = new[] { "Open the graph once in the Behavior editor window, then retry." }
                };
                return false;
            }

            blackboard = value;
            return true;
        }

        /// <summary>枚举 BlackboardAsset 中的 authoring VariableModel 条目。</summary>
        public static bool TryGetAuthoringVariables(object blackboardAsset, out IList variables, out object error)
        {
            variables = null;
            error = null;

            // BlackboardAsset.Variables 的类型是 List<VariableModel>（Tools/Graph/Asset/BlackboardAsset.cs）。
            if (!TryGetMember(blackboardAsset, out var value, out var memberError, "Variables"))
            {
                error = ApiMismatch(memberError);
                return false;
            }

            variables = value as IList;
            if (variables == null)
            {
                error = ApiMismatch("BlackboardAsset.Variables is not an indexable list");
                return false;
            }

            return true;
        }

        /// <summary>枚举从 BehaviorGraph 可达的运行时 BlackboardVariable 条目。</summary>
        public static IList GetRuntimeVariables(object behaviorGraph)
        {
            if (behaviorGraph == null) return null;

            var reference = GetMemberOrNull(behaviorGraph, "BlackboardReference");
            if (reference == null) return null;

            var blackboard = GetMemberOrNull(reference, "Blackboard");
            if (blackboard == null) return null;

            return GetMemberOrNull(blackboard, "Variables") as IList;
        }

        // ==================================================================================
        // 描述辅助
        // ==================================================================================

        /// <summary>描述一个 authoring VariableModel（名称 / 类型 / 默认值 / 标记）。</summary>
        public static object DescribeAuthoringVariable(object variableModel)
        {
            if (variableModel == null) return null;

            var type = GetMemberOrNull(variableModel, "Type") as Type;
            return new
            {
                name = GetMemberOrNull(variableModel, "Name")?.ToString(),
                id = GetMemberOrNull(variableModel, "ID")?.ToString(),
                type = type?.Name,
                typeFullName = type?.FullName,
                value = RenderPipelineSkillsCommon.ToSerializableValue(GetMemberOrNull(variableModel, "ObjectValue")),
                isShared = GetMemberOrNull(variableModel, "IsShared") as bool?,
                isExposed = GetMemberOrNull(variableModel, "IsExposed") as bool?,
                source = "authoring"
            };
        }

        /// <summary>描述一个运行时 BlackboardVariable（名称 / 类型 / 当前值）。</summary>
        public static object DescribeRuntimeVariable(object blackboardVariable, string source)
        {
            if (blackboardVariable == null) return null;

            var type = GetMemberOrNull(blackboardVariable, "Type") as Type;
            return new
            {
                name = GetMemberOrNull(blackboardVariable, "Name")?.ToString(),
                id = GetMemberOrNull(blackboardVariable, "GUID")?.ToString(),
                type = type?.Name,
                typeFullName = type?.FullName,
                value = RenderPipelineSkillsCommon.ToSerializableValue(GetMemberOrNull(blackboardVariable, "ObjectValue")),
                source
            };
        }

        /// <summary>解析指定变量名声明的 CLR 类型：先查 override，再查图的 blackboard。</summary>
        public static Type FindVariableType(IEnumerable candidates, string variableName)
        {
            if (candidates == null || string.IsNullOrEmpty(variableName)) return null;

            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                var name = GetMemberOrNull(candidate, "Name")?.ToString();
                if (!string.Equals(name, variableName, StringComparison.Ordinal)) continue;
                return GetMemberOrNull(candidate, "Type") as Type;
            }

            return null;
        }

        public static string[] ListVariableNames(IEnumerable candidates)
        {
            if (candidates == null) return Array.Empty<string>();

            return candidates
                .Cast<object>()
                .Where(candidate => candidate != null)
                .Select(candidate => GetMemberOrNull(candidate, "Name")?.ToString())
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        // ==================================================================================
        // 值转换 —— 覆盖 agent 实际会驱动的那些 blackboard 类型
        // ==================================================================================

        public static bool TryConvertValue(object raw, Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            if (targetType == null)
            {
                error = "target variable type is unknown";
                return false;
            }

            if (raw is JToken nullToken && nullToken.Type == JTokenType.Null) raw = null;

            if (raw == null)
            {
                if (targetType.IsValueType)
                {
                    error = $"null cannot be assigned to value type {targetType.Name}";
                    return false;
                }
                converted = null;
                return true;
            }

            if (targetType.IsInstanceOfType(raw))
            {
                converted = raw;
                return true;
            }

            try
            {
                if (targetType == typeof(string))
                {
                    converted = raw is JToken stringToken ? stringToken.ToString() : raw.ToString();
                    return true;
                }

                if (targetType.IsEnum)
                    return TryConvertEnum(raw, targetType, out converted, out error);

                if (targetType == typeof(Vector2) || targetType == typeof(Vector3) ||
                    targetType == typeof(Vector4) || targetType == typeof(Color) ||
                    targetType == typeof(Quaternion) ||
                    targetType == typeof(Vector2Int) || targetType == typeof(Vector3Int))
                    return TryConvertVectorLike(raw, targetType, out converted, out error);

                if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
                    return TryConvertUnityObject(raw, targetType, out converted, out error);

                var plain = raw is JValue jsonValue ? jsonValue.Value : raw;
                if (plain == null)
                {
                    converted = null;
                    return !targetType.IsValueType;
                }

                if (targetType == typeof(bool) && plain is string boolText)
                {
                    if (!bool.TryParse(boolText.Trim(), out var parsedBool))
                    {
                        error = $"'{boolText}' is not a boolean";
                        return false;
                    }
                    converted = parsedBool;
                    return true;
                }

                converted = Convert.ChangeType(plain, targetType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex)
            {
                error = $"cannot convert value to {targetType.Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryConvertEnum(object raw, Type enumType, out object converted, out string error)
        {
            converted = null;
            error = null;

            var text = raw is JToken token ? token.ToString() : raw.ToString();
            try
            {
                converted = Enum.Parse(enumType, text, ignoreCase: true);
                return true;
            }
            catch
            {
                error = $"'{text}' is not a valid {enumType.Name}. Valid values: {string.Join(", ", Enum.GetNames(enumType))}";
                return false;
            }
        }

        private static bool TryConvertVectorLike(object raw, Type targetType, out object converted, out string error)
        {
            converted = null;

            if (!TryReadComponents(raw, out var parts, out error))
                return false;

            float At(int index, float fallback = 0f) => index < parts.Length ? parts[index] : fallback;

            if (targetType == typeof(Vector2)) { converted = new Vector2(At(0), At(1)); return true; }
            if (targetType == typeof(Vector3)) { converted = new Vector3(At(0), At(1), At(2)); return true; }
            if (targetType == typeof(Vector4)) { converted = new Vector4(At(0), At(1), At(2), At(3)); return true; }
            if (targetType == typeof(Quaternion)) { converted = new Quaternion(At(0), At(1), At(2), At(3, 1f)); return true; }
            if (targetType == typeof(Color)) { converted = new Color(At(0), At(1), At(2), At(3, 1f)); return true; }
            if (targetType == typeof(Vector2Int)) { converted = new Vector2Int((int)At(0), (int)At(1)); return true; }
            if (targetType == typeof(Vector3Int)) { converted = new Vector3Int((int)At(0), (int)At(1), (int)At(2)); return true; }

            error = $"unsupported vector-like target type {targetType.Name}";
            return false;
        }

        /// <summary>接受 [1,2,3]、{"x":1,"y":2}、{"r":1,"g":0}、"1,2,3" 或裸数字。</summary>
        private static bool TryReadComponents(object raw, out float[] parts, out string error)
        {
            parts = Array.Empty<float>();
            error = null;

            if (raw is JArray array)
            {
                parts = array.Select(item => item.ToObject<float>()).ToArray();
                return true;
            }

            if (raw is JObject obj)
            {
                var keySets = new[] { new[] { "x", "y", "z", "w" }, new[] { "r", "g", "b", "a" } };
                foreach (var keys in keySets)
                {
                    var values = new List<float>();
                    foreach (var key in keys)
                    {
                        var token = obj[key];
                        if (token == null) break;
                        values.Add(token.ToObject<float>());
                    }
                    if (values.Count > 0)
                    {
                        parts = values.ToArray();
                        return true;
                    }
                }

                error = "object value needs x/y/z/w or r/g/b/a members";
                return false;
            }

            var text = raw is JValue jsonValue ? jsonValue.Value?.ToString() : raw?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "value is empty";
                return false;
            }

            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                if (!ColorUtility.TryParseHtmlString(text, out var htmlColor))
                {
                    error = $"'{text}' is not a valid html color";
                    return false;
                }
                parts = new[] { htmlColor.r, htmlColor.g, htmlColor.b, htmlColor.a };
                return true;
            }

            var tokens = text.Trim('(', ')', '[', ']')
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var numbers = new List<float>();
            foreach (var candidate in tokens)
            {
                if (!float.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    error = $"'{candidate}' is not a number";
                    return false;
                }
                numbers.Add(number);
            }

            if (numbers.Count == 0)
            {
                error = $"'{text}' contains no numbers";
                return false;
            }

            parts = numbers.ToArray();
            return true;
        }

        /// <summary>按资源路径、Hierarchy 路径或场景对象名解析 UnityEngine.Object 类型的变量。</summary>
        private static bool TryConvertUnityObject(object raw, Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            var text = raw is JToken token ? token.ToString() : raw.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                converted = null;
                return true;
            }

            if (text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath(text, targetType);
                if (asset != null)
                {
                    converted = asset;
                    return true;
                }

                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(text);
                    var component = prefab != null ? prefab.GetComponent(targetType) : null;
                    if (component != null)
                    {
                        converted = component;
                        return true;
                    }
                }

                error = $"no {targetType.Name} asset at path '{text}'";
                return false;
            }

            if (typeof(GameObject) == targetType || typeof(Component).IsAssignableFrom(targetType))
            {
                var go = GameObjectFinder.Find(text, 0, text);
                if (go == null)
                {
                    error = $"scene GameObject '{text}' was not found";
                    return false;
                }

                if (typeof(GameObject) == targetType)
                {
                    converted = go;
                    return true;
                }

                var component = go.GetComponent(targetType);
                if (component == null)
                {
                    error = $"GameObject '{go.name}' has no {targetType.Name} component";
                    return false;
                }

                converted = component;
                return true;
            }

            error = $"cannot resolve a {targetType.Name} from '{text}'. Pass an Assets/ path.";
            return false;
        }
    }

    /// <summary>
    /// Unity Behavior（com.unity.behavior）技能：behavior graph 资源的发现与检视、
    /// BehaviorGraphAgent 挂接、blackboard 变量读写。
    ///
    /// 该包是可选的，全程通过反射访问（见 <see cref="BehaviorReflectionHelper"/>）；
    /// 缺包时每个技能都返回同一份结构化安装提示，既不会编译失败也不会抛异常。
    ///
    /// 节点级的图拓扑编辑刻意不在范围内，详见模块 SKILL.md 的 Limitations 一节。
    /// </summary>
    public static class BehaviorSkills
    {
        private const string PackageId = BehaviorReflectionHelper.PackageId;

        // ==================================================================================
        // 状态（1 个技能）
        // ==================================================================================

        [UnitySkill("behavior_status", "Report Unity Behavior (com.unity.behavior) availability: whether the package is installed, its version, which core types resolved, and how many behavior graph assets and scene agents exist",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query | SkillOperation.Analyze,
            Tags = new[] { "behavior", "status", "package", "ai", "behavior-tree", "diagnostic" },
            Outputs = new[] { "installed", "version", "types", "graphAssetCount", "agentCount" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorStatus()
        {
            var installed = BehaviorReflectionHelper.IsInstalled;
            var types = new Dictionary<string, bool>
            {
                ["BehaviorGraphAgent"] = BehaviorReflectionHelper.AgentType != null,
                ["BehaviorGraph"] = BehaviorReflectionHelper.GraphType != null,
                ["BehaviorAuthoringGraph"] = BehaviorReflectionHelper.AuthoringGraphType != null,
                ["BlackboardAsset"] = BehaviorReflectionHelper.BlackboardAssetType != null,
                ["BlackboardVariable"] = BehaviorReflectionHelper.BlackboardVariableType != null,
                ["VariableModel"] = BehaviorReflectionHelper.VariableModelType != null
            };

            if (!installed)
            {
                return new
                {
                    success = true,
                    installed = false,
                    package = PackageId,
                    version = (string)null,
                    types,
                    graphAssetCount = 0,
                    agentCount = 0,
                    hint = "Install com.unity.behavior with the package_install skill to enable the behavior_* module.",
                    docs = BehaviorReflectionHelper.DocsUrl
                };
            }

            var graphPaths = BehaviorReflectionHelper.FindGraphAssetPaths(null);
            var agentType = BehaviorReflectionHelper.AgentType;
            var agentCount = agentType != null ? FindHelper.FindAll(agentType, includeInactive: true).Length : 0;

            return new
            {
                success = true,
                installed = true,
                package = PackageId,
                version = BehaviorReflectionHelper.InstalledVersion,
                validatedVersion = "1.0.16",
                types,
                graphAssetCount = graphPaths.Length,
                agentCount,
                isPlaying = Application.isPlaying,
                docs = BehaviorReflectionHelper.DocsUrl
            };
        }

        // ==================================================================================
        // 图资源（3 个技能）
        // ==================================================================================

        [UnitySkill("behavior_graph_list", "List behavior graph assets in the project with node count, blackboard variable count, and whether the runtime graph has been baked",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query,
            Tags = new[] { "behavior", "graph", "list", "assets", "ai", "behavior-tree" },
            Outputs = new[] { "count", "graphs" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorGraphList(string filter = null, string folder = null, int limit = 100)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();

            var paths = BehaviorReflectionHelper.FindGraphAssetPaths(folder)
                .Where(path => string.IsNullOrWhiteSpace(filter) ||
                               path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(Math.Max(1, limit))
                .ToArray();

            var graphs = paths.Select(path =>
            {
                BehaviorReflectionHelper.TryLoadAuthoringGraph(path, out var authoring, out _);
                var nodes = authoring != null
                    ? BehaviorReflectionHelper.GetMemberOrNull(authoring, "Nodes") as IList
                    : null;

                int? variableCount = null;
                if (authoring != null &&
                    BehaviorReflectionHelper.TryGetAuthoringBlackboard(authoring, out var blackboard, out _) &&
                    BehaviorReflectionHelper.TryGetAuthoringVariables(blackboard, out var variables, out _))
                {
                    variableCount = variables.Count;
                }

                var hasRuntimeGraph = BehaviorReflectionHelper
                    .TryLoadRuntimeGraph(path, out _, out _);

                return new
                {
                    path,
                    name = Path.GetFileNameWithoutExtension(path),
                    guid = AssetDatabase.AssetPathToGUID(path),
                    nodeCount = nodes?.Count,
                    variableCount,
                    hasRuntimeGraph,
                    readable = authoring != null
                };
            }).ToArray();

            return new
            {
                success = true,
                count = graphs.Length,
                graphs
            };
        }

        [UnitySkill("behavior_graph_info", "Read the structure summary of a behavior graph asset: node count and per-type breakdown, root nodes, blackboard variables with types and default values, subgraph dependencies, and runtime graph state",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query | SkillOperation.Analyze,
            Tags = new[] { "behavior", "graph", "inspect", "info", "blackboard", "ai", "behavior-tree" },
            Outputs = new[] { "assetPath", "nodeCount", "nodeTypeCounts", "rootCount", "blackboard", "hasRuntimeGraph" },
            RequiresInput = new[] { "assetPath" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorGraphInfo(string assetPath, int maxNodes = 100)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();
            if (Validate.Required(assetPath, "assetPath") is object requiredError) return requiredError;
            if (Validate.SafePath(assetPath, "assetPath") is object pathError) return pathError;

            if (!BehaviorReflectionHelper.TryLoadAuthoringGraph(assetPath, out var authoring, out var loadError))
                return loadError;

            // GraphAsset.Nodes 的类型是 List<NodeModel>（Tools/Graph/Asset/GraphAsset.cs）。
            if (!BehaviorReflectionHelper.TryGetMember(authoring, out var nodesValue, out var nodesError, "Nodes"))
                return BehaviorReflectionHelper.ApiMismatch(nodesError);

            var nodes = nodesValue as IList;
            if (nodes == null)
                return BehaviorReflectionHelper.ApiMismatch("BehaviorAuthoringGraph.Nodes is not an indexable list");

            var nodeTypeCounts = nodes.Cast<object>()
                .Where(node => node != null)
                .GroupBy(node => node.GetType().Name, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new { type = group.Key, count = group.Count() })
                .ToArray();

            var describedNodes = nodes.Cast<object>()
                .Where(node => node != null)
                .Take(Math.Max(1, maxNodes))
                .Select(DescribeNodeModel)
                .ToArray();

            object blackboardInfo = null;
            string blackboardWarning = null;
            if (BehaviorReflectionHelper.TryGetAuthoringBlackboard(authoring, out var blackboard, out _) &&
                BehaviorReflectionHelper.TryGetAuthoringVariables(blackboard, out var variables, out _))
            {
                blackboardInfo = new
                {
                    name = (blackboard as UnityEngine.Object)?.name,
                    variableCount = variables.Count,
                    variables = variables.Cast<object>()
                        .Select(BehaviorReflectionHelper.DescribeAuthoringVariable)
                        .Where(item => item != null)
                        .ToArray()
                };
            }
            else
            {
                blackboardWarning = "Blackboard could not be read — open the graph once in the Behavior editor window.";
            }

            var roots = BehaviorReflectionHelper.GetMemberOrNull(authoring, "Roots") as IList;
            var subgraphs = BehaviorReflectionHelper.GetMemberOrNull(authoring, "SubgraphsInfo") as IEnumerable;
            var subgraphPaths = subgraphs?.Cast<object>()
                .Select(info => BehaviorReflectionHelper.GetMemberOrNull(info, "Asset") as UnityEngine.Object)
                .Where(asset => asset != null)
                .Select(AssetDatabase.GetAssetPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? Array.Empty<string>();

            var hasRuntimeGraph = BehaviorReflectionHelper.TryLoadRuntimeGraph(assetPath, out var runtimeGraph, out _);

            return new
            {
                success = true,
                assetPath,
                name = authoring.name,
                description = BehaviorReflectionHelper.GetMemberOrNull(authoring, "Description")?.ToString(),
                nodeCount = nodes.Count,
                nodeTypeCounts,
                nodes = describedNodes,
                nodesTruncated = nodes.Count > describedNodes.Length,
                rootCount = roots?.Count,
                blackboard = blackboardInfo,
                subgraphDependencies = subgraphPaths,
                hasRuntimeGraph,
                runtimeGraphName = (runtimeGraph as UnityEngine.Object)?.name,
                warning = blackboardWarning
            };
        }

        [UnitySkill("behavior_graph_create", "Create an empty behavior graph asset (BehaviorAuthoringGraph) at savePath and bake its runtime graph, blackboard, and Start root through a forced reimport",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Create,
            Tags = new[] { "behavior", "graph", "create", "asset", "ai", "behavior-tree" },
            Outputs = new[] { "path", "name", "hasRuntimeGraph", "nodeCount" },
            RequiresInput = new[] { "savePath" },
            TracksWorkflow = true,
            MutatesAssets = true,
            RequiresPackages = new[] { PackageId },
            RiskLevel = "medium")]
        public static object BehaviorGraphCreate(string savePath)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();
            if (Validate.Required(savePath, "savePath") is object requiredError) return requiredError;

            var authoringType = BehaviorReflectionHelper.AuthoringGraphType;
            if (authoringType == null)
                return BehaviorReflectionHelper.ApiMismatch("type Unity.Behavior.BehaviorAuthoringGraph was not found");

            var fileName = Path.GetFileNameWithoutExtension(savePath);
            var resolvedPath = RenderPipelineSkillsCommon.ResolveAssetSavePath(
                savePath,
                string.IsNullOrWhiteSpace(fileName) ? "New Behavior Graph" : fileName,
                ".asset");

            if (Validate.SafePath(resolvedPath, "savePath") is object pathError) return pathError;
            if (File.Exists(resolvedPath))
                return new { error = $"Asset already exists: {resolvedPath}" };

            RenderPipelineSkillsCommon.EnsureAssetFolderExists(resolvedPath);

            ScriptableObject instance;
            try
            {
                instance = ScriptableObject.CreateInstance(authoringType);
            }
            catch (Exception ex)
            {
                return BehaviorReflectionHelper.ApiMismatch(
                    $"ScriptableObject.CreateInstance(BehaviorAuthoringGraph) threw: {ex.InnerException?.Message ?? ex.Message}");
            }

            if (instance == null)
                return BehaviorReflectionHelper.ApiMismatch("ScriptableObject.CreateInstance(BehaviorAuthoringGraph) returned null");

            try
            {
                AssetDatabase.CreateAsset(instance, resolvedPath);
            }
            catch (Exception ex)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return new { error = $"Failed to create asset at {resolvedPath}: {ex.Message}" };
            }

            // Behavior 的资源后处理器会在导入时调用 BehaviorAuthoringGraph.ValidateAsset()，
            // 由它创建 blackboard、烘焙出的 BehaviorGraph 子资源、debug info 子资源
            // 以及必需的 Start 根节点。
            AssetDatabase.ImportAsset(resolvedPath, ImportAssetOptions.ForceUpdate);

            var created = AssetDatabase.LoadMainAssetAtPath(resolvedPath);
            if (created == null)
                return new { error = $"Asset was created but could not be reloaded: {resolvedPath}" };

            string bakeWarning = null;
            if (!BehaviorReflectionHelper.TryLoadRuntimeGraph(resolvedPath, out _, out _))
            {
                // 兜底：某些导入顺序下后处理器还没完成烘焙。
                // 这两个成员在 BehaviorAuthoringGraph 上都是 public
                // （Authoring/Asset/BehaviorAuthoringGraph.cs）。
                BehaviorReflectionHelper.TryInvoke(created, "EnsureAuthoringDataIsUpToDate", null, out _, out _);
                BehaviorReflectionHelper.TryInvoke(created, "BuildRuntimeGraph", new object[] { true }, out _, out _);
                BehaviorReflectionHelper.TryInvoke(created, "SaveAsset", null, out _, out _);
                AssetDatabase.SaveAssets();
            }

            var hasRuntimeGraph = BehaviorReflectionHelper.TryLoadRuntimeGraph(resolvedPath, out var runtimeGraph, out _);
            if (!hasRuntimeGraph)
            {
                bakeWarning = "The runtime BehaviorGraph sub-asset was not baked. " +
                              "Open the graph once in the Behavior editor window before assigning it to an agent.";
            }

            WorkflowManager.SnapshotCreatedAsset(created);

            var nodes = BehaviorReflectionHelper.GetMemberOrNull(created, "Nodes") as IList;

            return new
            {
                success = true,
                path = resolvedPath,
                name = created.name,
                hasRuntimeGraph,
                runtimeGraphName = (runtimeGraph as UnityEngine.Object)?.name,
                nodeCount = nodes?.Count,
                warning = bakeWarning
            };
        }

        // ==================================================================================
        // Agent（4 个技能）
        // ==================================================================================

        [UnitySkill("behavior_agent_add", "Add a BehaviorGraphAgent component to a GameObject, optionally binding a behavior graph asset in the same call",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "behavior", "agent", "component", "add", "ai", "behavior-tree" },
            Outputs = new[] { "gameObject", "instanceId", "graphAssetPath", "graphName" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { PackageId })]
        public static object BehaviorAgentAdd(string name = null, int instanceId = 0, string path = null,
            string graphAssetPath = null)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();

            var agentType = BehaviorReflectionHelper.AgentType;
            if (agentType == null)
                return BehaviorReflectionHelper.ApiMismatch("type Unity.Behavior.BehaviorGraphAgent was not found");

            var (go, findError) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findError != null) return findError;

            // 先解析图再动场景，避免路径有误时留下一个配置到一半的 agent。
            UnityEngine.Object runtimeGraph = null;
            if (!string.IsNullOrWhiteSpace(graphAssetPath))
            {
                var resolveError = ResolveRuntimeGraph(graphAssetPath, out runtimeGraph);
                if (resolveError != null) return resolveError;
            }

            var existing = go.GetComponent(agentType);
            var created = existing == null;
            var agent = existing;

            if (created)
            {
                try
                {
                    agent = Undo.AddComponent(go, agentType);
                }
                catch (Exception ex)
                {
                    return new { error = $"Failed to add BehaviorGraphAgent to '{go.name}': {ex.Message}" };
                }

                if (agent == null)
                    return new { error = $"Failed to add BehaviorGraphAgent to '{go.name}'." };
            }

            if (runtimeGraph != null)
            {
                var assignError = AssignResolvedGraph(agent, runtimeGraph);
                if (assignError != null) return assignError;
            }

            EditorUtility.SetDirty(go);
            WorkflowManager.SnapshotObject(go, created ? SnapshotType.Created : SnapshotType.Modified);

            return new
            {
                success = true,
                gameObject = go.name,
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                entityId = UnityObjectIdUtility.GetEntityId(go),
                hierarchyPath = GameObjectFinder.GetPath(go),
                componentAdded = created,
                graphAssetPath = string.IsNullOrWhiteSpace(graphAssetPath) ? null : graphAssetPath,
                graphName = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") is UnityEngine.Object boundGraph
                    ? boundGraph.name
                    : null
            };
        }

        [UnitySkill("behavior_agent_get", "Read a BehaviorGraphAgent on a GameObject: bound graph asset, initialization/run state, and its blackboard variables plus agent-level overrides",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query,
            Tags = new[] { "behavior", "agent", "inspect", "blackboard", "ai", "behavior-tree" },
            Outputs = new[] { "gameObject", "graphAssetPath", "isInitialised", "isStarted", "variables", "overrides" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorAgentGet(string name = null, int instanceId = 0, string path = null)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();

            var (agent, agentError) = FindAgentOrError(name, instanceId, path);
            if (agentError != null) return agentError;

            var graph = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") as UnityEngine.Object;
            var variables = BehaviorReflectionHelper.GetRuntimeVariables(graph);
            var overrides = BehaviorReflectionHelper
                .GetMemberOrNull(agent, "m_BlackboardVariableOverridesList") as IList;

            return new
            {
                success = true,
                gameObject = agent.gameObject.name,
                instanceId = UnityObjectIdUtility.GetObjectId(agent.gameObject),
                entityId = UnityObjectIdUtility.GetEntityId(agent.gameObject),
                hierarchyPath = GameObjectFinder.GetPath(agent.gameObject),
                enabled = agent is Behaviour behaviour && behaviour.enabled,
                graphName = graph != null ? graph.name : null,
                graphAssetPath = graph != null ? AssetDatabase.GetAssetPath(graph) : null,
                isInitialised = BehaviorReflectionHelper.GetMemberOrNull(agent, "m_IsInitialised") as bool?,
                isStarted = BehaviorReflectionHelper.GetMemberOrNull(agent, "m_IsStarted") as bool?,
                isRunning = graph != null ? BehaviorReflectionHelper.GetMemberOrNull(graph, "IsRunning") as bool? : null,
                variableCount = variables?.Count,
                variables = variables?.Cast<object>()
                    .Select(variable => BehaviorReflectionHelper.DescribeRuntimeVariable(variable, "graph"))
                    .Where(item => item != null)
                    .ToArray(),
                overrides = overrides?.Cast<object>()
                    .Select(variable => BehaviorReflectionHelper.DescribeRuntimeVariable(variable, "agentOverride"))
                    .Where(item => item != null)
                    .ToArray()
            };
        }

        [UnitySkill("behavior_agent_set_graph", "Bind a behavior graph asset to an existing BehaviorGraphAgent by resolving the baked BehaviorGraph sub-asset at graphAssetPath",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Modify,
            Tags = new[] { "behavior", "agent", "graph", "bind", "assign", "ai", "behavior-tree" },
            Outputs = new[] { "gameObject", "graphAssetPath", "graphName" },
            RequiresInput = new[] { "gameObject", "graphAssetPath" },
            TracksWorkflow = true,
            MutatesScene = true,
            RequiresPackages = new[] { PackageId },
            RiskLevel = "medium")]
        public static object BehaviorAgentSetGraph(string graphAssetPath, string name = null, int instanceId = 0,
            string path = null)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();
            if (Validate.Required(graphAssetPath, "graphAssetPath") is object requiredError) return requiredError;

            var (agent, agentError) = FindAgentOrError(name, instanceId, path);
            if (agentError != null) return agentError;

            var resolveError = ResolveRuntimeGraph(graphAssetPath, out var runtimeGraph);
            if (resolveError != null) return resolveError;

            var assignError = AssignResolvedGraph(agent, runtimeGraph);
            if (assignError != null) return assignError;

            WorkflowManager.SnapshotObject(agent);

            var graph = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") as UnityEngine.Object;
            return new
            {
                success = true,
                gameObject = agent.gameObject.name,
                instanceId = UnityObjectIdUtility.GetObjectId(agent.gameObject),
                graphAssetPath,
                graphName = graph != null ? graph.name : null
            };
        }

        [UnitySkill("behavior_agent_list", "List every BehaviorGraphAgent in the loaded scenes with its bound graph, hierarchy path, and initialization/run state",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query,
            Tags = new[] { "behavior", "agent", "list", "scene", "ai", "behavior-tree" },
            Outputs = new[] { "count", "agents" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorAgentList(bool includeInactive = true, string graphFilter = null, int limit = 200)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();

            var agentType = BehaviorReflectionHelper.AgentType;
            if (agentType == null)
                return BehaviorReflectionHelper.ApiMismatch("type Unity.Behavior.BehaviorGraphAgent was not found");

            var agents = FindHelper.FindAll(agentType, includeInactive)
                .OfType<Component>()
                .Select(agent =>
                {
                    var graph = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") as UnityEngine.Object;
                    return new
                    {
                        gameObject = agent.gameObject.name,
                        instanceId = UnityObjectIdUtility.GetObjectId(agent.gameObject),
                        entityId = UnityObjectIdUtility.GetEntityId(agent.gameObject),
                        hierarchyPath = GameObjectFinder.GetPath(agent.gameObject),
                        activeInHierarchy = agent.gameObject.activeInHierarchy,
                        enabled = agent is Behaviour behaviour && behaviour.enabled,
                        graphName = graph != null ? graph.name : null,
                        graphAssetPath = graph != null ? AssetDatabase.GetAssetPath(graph) : null,
                        isInitialised = BehaviorReflectionHelper.GetMemberOrNull(agent, "m_IsInitialised") as bool?,
                        isStarted = BehaviorReflectionHelper.GetMemberOrNull(agent, "m_IsStarted") as bool?,
                        isRunning = graph != null
                            ? BehaviorReflectionHelper.GetMemberOrNull(graph, "IsRunning") as bool?
                            : null
                    };
                })
                .Where(agent => string.IsNullOrWhiteSpace(graphFilter) ||
                                (agent.graphName ?? string.Empty).IndexOf(graphFilter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                (agent.graphAssetPath ?? string.Empty).IndexOf(graphFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(agent => agent.hierarchyPath, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, limit))
                .ToArray();

            return new
            {
                success = true,
                count = agents.Length,
                isPlaying = Application.isPlaying,
                agents
            };
        }

        // ==================================================================================
        // Blackboard（2 个技能）
        // ==================================================================================

        [UnitySkill("behavior_blackboard_list", "List blackboard variables with name, type, and current value. Pass graphAssetPath to read the graph asset's authoring defaults, or a GameObject to read the agent's graph variables plus its agent-level overrides",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Query,
            Tags = new[] { "behavior", "blackboard", "variable", "list", "ai", "behavior-tree" },
            Outputs = new[] { "source", "count", "variables" },
            // 真正的二选一：给图资源路径读 authoring 默认值，给 GameObject 定位符读某个 agent
            // 的运行时变量。只声明 "gameObject" 会误拒合法的 {graphAssetPath: …} 请求体，
            // 所以这个 token 同时点出两侧，由 SkillPlanningService._requiredInputGroups 映射为
            // {name, path, instanceId, graphAssetPath}——空请求体在入口就被拒，
            // 而不是执行到手写的 "Provide either …" 错误。
            RequiresInput = new[] { "gameObject|graphAssetPath" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object BehaviorBlackboardList(string graphAssetPath = null, string name = null,
            int instanceId = 0, string path = null)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();

            var hasGameObject = !string.IsNullOrWhiteSpace(name) || instanceId != 0 || !string.IsNullOrWhiteSpace(path);
            if (!hasGameObject && string.IsNullOrWhiteSpace(graphAssetPath))
            {
                return new
                {
                    error = "Provide either graphAssetPath (asset defaults) or a GameObject locator (name/instanceId/path) for an agent.",
                    suggestedFixes = new[]
                    {
                        "behavior_graph_list returns the graphAssetPath values in this project.",
                        "behavior_agent_list returns the GameObjects carrying a BehaviorGraphAgent."
                    }
                };
            }

            if (hasGameObject)
            {
                var (agent, agentError) = FindAgentOrError(name, instanceId, path);
                if (agentError != null) return agentError;

                var graph = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") as UnityEngine.Object;
                if (graph == null)
                {
                    return new
                    {
                        error = $"BehaviorGraphAgent on '{agent.gameObject.name}' has no graph assigned.",
                        suggestedFixes = new[] { "Bind one with behavior_agent_set_graph first." }
                    };
                }

                var runtimeVariables = BehaviorReflectionHelper.GetRuntimeVariables(graph);
                var overrides = BehaviorReflectionHelper
                    .GetMemberOrNull(agent, "m_BlackboardVariableOverridesList") as IList;

                return new
                {
                    success = true,
                    source = "agent",
                    gameObject = agent.gameObject.name,
                    graphName = graph.name,
                    graphAssetPath = AssetDatabase.GetAssetPath(graph),
                    count = runtimeVariables?.Count ?? 0,
                    variables = runtimeVariables?.Cast<object>()
                        .Select(variable => BehaviorReflectionHelper.DescribeRuntimeVariable(variable, "graph"))
                        .Where(item => item != null)
                        .ToArray() ?? Array.Empty<object>(),
                    overrides = overrides?.Cast<object>()
                        .Select(variable => BehaviorReflectionHelper.DescribeRuntimeVariable(variable, "agentOverride"))
                        .Where(item => item != null)
                        .ToArray() ?? Array.Empty<object>(),
                    note = "In edit mode the graph values are the shared asset defaults; per-agent values live in overrides."
                };
            }

            if (Validate.SafePath(graphAssetPath, "graphAssetPath") is object pathError) return pathError;
            if (!BehaviorReflectionHelper.TryLoadAuthoringGraph(graphAssetPath, out var authoring, out var loadError))
                return loadError;
            if (!BehaviorReflectionHelper.TryGetAuthoringBlackboard(authoring, out var blackboard, out var blackboardError))
                return blackboardError;
            if (!BehaviorReflectionHelper.TryGetAuthoringVariables(blackboard, out var variables, out var variablesError))
                return variablesError;

            return new
            {
                success = true,
                source = "asset",
                graphAssetPath,
                graphName = authoring.name,
                blackboardName = (blackboard as UnityEngine.Object)?.name,
                count = variables.Count,
                variables = variables.Cast<object>()
                    .Select(BehaviorReflectionHelper.DescribeAuthoringVariable)
                    .Where(item => item != null)
                    .ToArray()
            };
        }

        [UnitySkill("behavior_blackboard_set", "Set a blackboard variable value. With a GameObject locator this writes the agent-level override on that BehaviorGraphAgent; with graphAssetPath it writes the graph asset's authoring default and rebuilds the baked blackboard. Supports int/float/bool/string/enum/Vector2-3-4/Color/Quaternion and UnityEngine.Object references by asset path or scene name",
            Category = SkillCategory.Behavior, Operation = SkillOperation.Modify,
            Tags = new[] { "behavior", "blackboard", "variable", "set", "override", "ai", "behavior-tree" },
            Outputs = new[] { "target", "variable", "type", "value" },
            RequiresInput = new[] { "variable" },
            TracksWorkflow = true,
            MutatesScene = true,
            MutatesAssets = true,
            RequiresPackages = new[] { PackageId },
            RiskLevel = "medium")]
        public static object BehaviorBlackboardSet(string variable, object value = null, string graphAssetPath = null,
            string name = null, int instanceId = 0, string path = null)
        {
            if (!BehaviorReflectionHelper.IsInstalled) return BehaviorReflectionHelper.NotInstalled();
            if (Validate.Required(variable, "variable") is object requiredError) return requiredError;

            var hasGameObject = !string.IsNullOrWhiteSpace(name) || instanceId != 0 || !string.IsNullOrWhiteSpace(path);
            if (!hasGameObject && string.IsNullOrWhiteSpace(graphAssetPath))
            {
                return new
                {
                    error = "Provide either a GameObject locator (name/instanceId/path) to set an agent override, " +
                            "or graphAssetPath to set the graph asset default.",
                    suggestedFixes = new[]
                    {
                        "behavior_agent_list returns the GameObjects carrying a BehaviorGraphAgent.",
                        "behavior_blackboard_list shows the available variable names and their declared types."
                    }
                };
            }

            return hasGameObject
                ? SetAgentBlackboardVariable(variable, value, name, instanceId, path)
                : SetAssetBlackboardVariable(variable, value, graphAssetPath);
        }

        // ==================================================================================
        // 内部实现
        // ==================================================================================

        private static object SetAgentBlackboardVariable(string variableName, object value, string name,
            int instanceId, string path)
        {
            var (agent, agentError) = FindAgentOrError(name, instanceId, path);
            if (agentError != null) return agentError;

            var graph = BehaviorReflectionHelper.GetMemberOrNull(agent, "Graph") as UnityEngine.Object;
            if (graph == null)
            {
                return new
                {
                    error = $"BehaviorGraphAgent on '{agent.gameObject.name}' has no graph assigned.",
                    suggestedFixes = new[] { "Bind one with behavior_agent_set_graph first." }
                };
            }

            var graphVariables = BehaviorReflectionHelper.GetRuntimeVariables(graph);
            var overrides = BehaviorReflectionHelper
                .GetMemberOrNull(agent, "m_BlackboardVariableOverridesList") as IList;

            var declaredType = BehaviorReflectionHelper.FindVariableType(overrides, variableName)
                               ?? BehaviorReflectionHelper.FindVariableType(graphVariables, variableName);

            if (declaredType == null)
            {
                return new
                {
                    error = $"Blackboard variable '{variableName}' was not found on graph '{graph.name}'.",
                    availableVariables = BehaviorReflectionHelper.ListVariableNames(graphVariables),
                    suggestedFixes = new[] { "Call behavior_blackboard_list to see the exact variable names." }
                };
            }

            if (!BehaviorReflectionHelper.TryConvertValue(value, declaredType, out var converted, out var convertError))
            {
                return new
                {
                    error = $"Cannot set '{variableName}' ({declaredType.Name}): {convertError}",
                    variable = variableName,
                    expectedType = declaredType.FullName
                };
            }

            // BehaviorGraphAgent.SetVariableValue<TValue>(string, TValue) 在 agent 尚未初始化时
            // （编辑模式下始终如此）写的是 agent 级 override，播放模式下则写到运行中的图实例。
            var setter = BehaviorReflectionHelper.AgentType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, "SetVariableValue", StringComparison.Ordinal) &&
                    candidate.IsGenericMethodDefinition &&
                    candidate.GetGenericArguments().Length == 1 &&
                    candidate.GetParameters().Length == 2 &&
                    candidate.GetParameters()[0].ParameterType == typeof(string));

            if (setter == null)
                return BehaviorReflectionHelper.ApiMismatch("BehaviorGraphAgent.SetVariableValue<T>(string, T) was not found");

            Undo.RegisterCompleteObjectUndo(agent, $"Set Behavior Variable {variableName}");

            bool applied;
            try
            {
                var result = setter.MakeGenericMethod(declaredType).Invoke(agent, new[] { variableName, converted });
                applied = result is bool boolResult && boolResult;
            }
            catch (Exception ex)
            {
                return new
                {
                    error = $"BehaviorGraphAgent.SetVariableValue threw: {ex.InnerException?.Message ?? ex.Message}",
                    variable = variableName
                };
            }

            if (!applied)
            {
                return new
                {
                    error = $"The agent rejected the write to '{variableName}'. The variable may not exist on the bound graph.",
                    variable = variableName,
                    availableVariables = BehaviorReflectionHelper.ListVariableNames(graphVariables)
                };
            }

            EditorUtility.SetDirty(agent);
            WorkflowManager.SnapshotObject(agent);

            return new
            {
                success = true,
                target = "agent",
                gameObject = agent.gameObject.name,
                instanceId = UnityObjectIdUtility.GetObjectId(agent.gameObject),
                graphName = graph.name,
                variable = variableName,
                type = declaredType.Name,
                value = RenderPipelineSkillsCommon.ToSerializableValue(converted),
                note = Application.isPlaying
                    ? "Play mode: written to the running graph instance."
                    : "Edit mode: written as an agent-level override, visible in the Inspector."
            };
        }

        private static object SetAssetBlackboardVariable(string variableName, object value, string graphAssetPath)
        {
            if (Validate.SafePath(graphAssetPath, "graphAssetPath") is object pathError) return pathError;
            if (!BehaviorReflectionHelper.TryLoadAuthoringGraph(graphAssetPath, out var authoring, out var loadError))
                return loadError;
            if (!BehaviorReflectionHelper.TryGetAuthoringBlackboard(authoring, out var blackboard, out var blackboardError))
                return blackboardError;
            if (!BehaviorReflectionHelper.TryGetAuthoringVariables(blackboard, out var variables, out var variablesError))
                return variablesError;

            object target = null;
            foreach (var candidate in variables)
            {
                if (candidate == null) continue;
                if (string.Equals(BehaviorReflectionHelper.GetMemberOrNull(candidate, "Name")?.ToString(),
                        variableName, StringComparison.Ordinal))
                {
                    target = candidate;
                    break;
                }
            }

            if (target == null)
            {
                return new
                {
                    error = $"Blackboard variable '{variableName}' was not found on graph asset '{graphAssetPath}'.",
                    availableVariables = BehaviorReflectionHelper.ListVariableNames(variables),
                    suggestedFixes = new[] { "Call behavior_blackboard_list with graphAssetPath to see the exact names." }
                };
            }

            var declaredType = BehaviorReflectionHelper.GetMemberOrNull(target, "Type") as Type;
            if (declaredType == null)
                return BehaviorReflectionHelper.ApiMismatch("VariableModel.Type was not readable");

            if (!BehaviorReflectionHelper.TryConvertValue(value, declaredType, out var converted, out var convertError))
            {
                return new
                {
                    error = $"Cannot set '{variableName}' ({declaredType.Name}): {convertError}",
                    variable = variableName,
                    expectedType = declaredType.FullName
                };
            }

            var blackboardObject = blackboard as UnityEngine.Object;

            WorkflowManager.SnapshotObject(authoring);
            if (blackboardObject != null)
                Undo.RegisterCompleteObjectUndo(blackboardObject, $"Set Behavior Variable {variableName}");
            Undo.RegisterCompleteObjectUndo(authoring, $"Set Behavior Variable {variableName}");

            if (!BehaviorReflectionHelper.TrySetMember(target, converted, out var setError, "ObjectValue"))
                return BehaviorReflectionHelper.ApiMismatch(setError);

            // 复刻 BehaviorAuthoringGraph.RebuildGraphAndBlackboardRuntimeData() 的顺序：
            // 先标脏并重烘 blackboard，再重烘并保存图，使运行时子资源与之一致。
            BehaviorReflectionHelper.TryInvoke(blackboard, "SetAssetDirty", null, out _, out _);
            BehaviorReflectionHelper.TryInvoke(blackboard, "BuildRuntimeBlackboard", null, out _, out _);
            BehaviorReflectionHelper.TryInvoke(authoring, "SetAssetDirty", new object[] { true }, out _, out _);
            BehaviorReflectionHelper.TryInvoke(authoring, "BuildRuntimeGraph", new object[] { true }, out _, out _);
            BehaviorReflectionHelper.TryInvoke(authoring, "SaveAsset", null, out _, out _);

            if (blackboardObject != null) EditorUtility.SetDirty(blackboardObject);
            EditorUtility.SetDirty(authoring);
            AssetDatabase.SaveAssets();

            return new
            {
                success = true,
                target = "asset",
                graphAssetPath,
                graphName = authoring.name,
                variable = variableName,
                type = declaredType.Name,
                value = RenderPipelineSkillsCommon.ToSerializableValue(converted),
                note = "Written as the graph asset default. Existing agents keep their own overrides."
            };
        }

        /// <summary>定位 GameObject，并要求其上挂有 BehaviorGraphAgent。</summary>
        private static (Component agent, object error) FindAgentOrError(string name, int instanceId, string path)
        {
            var agentType = BehaviorReflectionHelper.AgentType;
            if (agentType == null)
                return (null, BehaviorReflectionHelper.ApiMismatch("type Unity.Behavior.BehaviorGraphAgent was not found"));

            var (go, findError) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findError != null) return (null, findError);

            var agent = go.GetComponent(agentType);
            if (agent == null)
            {
                return (null, new
                {
                    error = $"GameObject '{go.name}' has no BehaviorGraphAgent component.",
                    suggestedFixes = new[] { "Add one with behavior_agent_add." }
                });
            }

            return (agent, null);
        }

        /// <summary>
        /// 把调用方给的 authoring 资源路径解析为已烘焙的运行时 BehaviorGraph。
        /// 成功返回 null，失败返回结构化错误对象。
        /// </summary>
        private static object ResolveRuntimeGraph(string graphAssetPath, out UnityEngine.Object runtimeGraph)
        {
            runtimeGraph = null;

            if (Validate.SafePath(graphAssetPath, "graphAssetPath") is object pathError) return pathError;
            if (!BehaviorReflectionHelper.TryLoadAuthoringGraph(graphAssetPath, out _, out var loadError))
                return loadError;
            if (!BehaviorReflectionHelper.TryLoadRuntimeGraph(graphAssetPath, out runtimeGraph, out var runtimeError))
                return runtimeError;

            // BehaviorGraphAgent.Graph 的 setter 会遍历 RootGraph.BlackboardGroupReferences，
            // 未编译的图会在包内部抛异常，所以提前拒绝。注意：RootGraph 成员本身缺失说明是
            // 布局变更而非未编译，那种情况放过，让它以 set 错误的形式暴露。
            if (BehaviorReflectionHelper.TryGetMember(runtimeGraph, out var rootGraph, out _, "RootGraph") &&
                rootGraph == null)
            {
                runtimeGraph = null;
                return new
                {
                    error = $"The behavior graph at {graphAssetPath} has no compiled root graph.",
                    errorCode = "RUNTIME_GRAPH_MISSING",
                    suggestedFixes = new[]
                    {
                        "Open the graph once in the Behavior editor window so Unity compiles it.",
                        "Or reimport the asset with asset_reimport and retry."
                    }
                };
            }

            return null;
        }

        /// <summary>把已解析好的运行时图写到 agent 上。成功返回 null。</summary>
        private static object AssignResolvedGraph(Component agent, UnityEngine.Object runtimeGraph)
        {
            Undo.RegisterCompleteObjectUndo(agent, "Set Behavior Graph");

            if (!BehaviorReflectionHelper.TrySetMember(agent, runtimeGraph, out var setError, "Graph", "m_Graph"))
                return BehaviorReflectionHelper.ApiMismatch(setError);

            EditorUtility.SetDirty(agent);
            return null;
        }

        /// <summary>为图信息输出描述一个 authoring NodeModel；成员缺失时降级为 null。</summary>
        private static object DescribeNodeModel(object node)
        {
            var position = BehaviorReflectionHelper.GetMemberOrNull(node, "Position");
            return new
            {
                id = BehaviorReflectionHelper.GetMemberOrNull(node, "ID")?.ToString(),
                type = node.GetType().Name,
                isRoot = BehaviorReflectionHelper.GetMemberOrNull(node, "IsRoot") as bool?,
                position = position is Vector2 vector ? new { x = vector.x, y = vector.y } : null
            };
        }
    }
}

// Producer:Betsy
