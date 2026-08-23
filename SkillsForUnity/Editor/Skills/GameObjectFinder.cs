using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;

namespace UnitySkills.Internal
{
    /// <summary>
    /// 兼容层：Unity 6+ 用 FindObjectsByType，旧版本回落到 FindObjectsOfType。
    /// </summary>
    internal static class FindHelper
    {
        internal static T[] FindAll<T>(bool includeInactive = false) where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType<T>(FindObjectsInactive.Include)
                : Object.FindObjectsByType<T>(FindObjectsInactive.Exclude);
#elif UNITY_6000_0_OR_NEWER || UNITY_2022_2_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                : Object.FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return includeInactive
                ? Resources.FindObjectsOfTypeAll<T>()
                : Object.FindObjectsOfType<T>();
#endif
        }

        internal static Object[] FindAll(System.Type type, bool includeInactive = false)
        {
            if (type == null)
                return System.Array.Empty<Object>();

#if UNITY_6000_4_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType(type, FindObjectsInactive.Include)
                : Object.FindObjectsByType(type, FindObjectsInactive.Exclude);
#elif UNITY_6000_0_OR_NEWER || UNITY_2022_2_OR_NEWER
            return includeInactive
                ? Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None)
                : Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            return includeInactive
                ? Resources.FindObjectsOfTypeAll(type)
                : Object.FindObjectsOfType(type);
#endif
        }
    }
}

namespace UnitySkills
{
    /// <summary>
    /// 参数校验助手：出错返回错误对象，合法返回 null。
    /// </summary>
    public static class Validate
    {
        // 全包所有技能的参数错误都汇聚到这几个辅助方法，因此这里补的结构化字段
        // 就是几百个技能不用改自身代码也能拥有精确 errorCode 与可用 retryStrategy 的原因。
        // SkillRouter 的 TryGetErrorContext 原样读取它们；SkillErrorClassifier 只补缺失项。

        private static object MissingParam(string message, string paramName) => new
        {
            error = message,
            errorCode = SkillErrorCode.MissingParam.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            suggestedFixes = new[]
            {
                new
                {
                    action = "fix_param",
                    skill = "POST /skill/<name>?mode=dryRun",
                    reason = $"Pass '{paramName}'. dryRun returns the full parameter schema without executing."
                }
            }
        };

        private static object InvalidParam(string message, string reason) => new
        {
            error = message,
            errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
            retryStrategy = SkillErrorResponse.RetryFixAndRetry,
            suggestedFixes = new[]
            {
                new { action = "fix_param", reason }
            }
        };

        /// <summary>
        /// 检查字符串参数是否提供。为空返回错误对象，合法返回 null。
        /// 用法：if (Validate.Required(x, "x") is object err) return err;
        /// </summary>
        public static object Required(string value, string paramName) =>
            string.IsNullOrEmpty(value) ? MissingParam($"{paramName} is required", paramName) : null;

        /// <summary>
        /// <see cref="Required(string,string)"/> 的可空值类型版本，供载荷是数字的 setter 使用。
        ///
        /// <para>声明成 <c>float x = 1f</c> 的参数无法区分"调用方传了 1"和"调用方什么都没传"，
        /// 于是省略时会用 CLR 默认值静默覆盖对象，响应还报成功。改成 <c>float? x = null</c>
        /// 并配上一条 <c>RequiresInput</c>，schema 才会标为必填、dryRun 才会拒绝空请求体，
        /// 而这个守卫负责拦住进程内直接调用的那一类。</para>
        /// </summary>
        public static object Required<T>(T? value, string paramName) where T : struct =>
            value.HasValue ? null : MissingParam($"{paramName} is required", paramName);

        /// <summary>
        /// 检查 JSON 数组参数已提供且非空。
        /// 用法：if (Validate.RequiredJsonArray(items, "items") is object err) return err;
        /// </summary>
        public static object RequiredJsonArray(string jsonArray, string paramName)
        {
            if (string.IsNullOrEmpty(jsonArray))
                return MissingParam($"{paramName} is required", paramName);
            var trimmed = jsonArray.Trim();
            if (trimmed == "[]" || trimmed == "null")
                return InvalidParam($"{paramName} must be a non-empty array",
                    $"'{paramName}' is a JSON array string — send at least one element, e.g. [\"first\"].");
            return null;
        }

        /// <summary>
        /// 校验数值落在闭区间内。
        /// 用法：if (Validate.InRange(count, 1, 100, "count") is object err) return err;
        /// </summary>
        public static object InRange(float value, float min, float max, string paramName)
        {
            if (value < min || value > max)
                return InvalidParam($"{paramName} must be between {min} and {max}, got {value}",
                    $"Clamp '{paramName}' into [{min}, {max}] and retry.");
            return null;
        }

        /// <summary>
        /// 校验整数落在闭区间内。
        /// </summary>
        public static object InRange(int value, int min, int max, string paramName)
        {
            if (value < min || value > max)
                return InvalidParam($"{paramName} must be between {min} and {max}, got {value}",
                    $"Clamp '{paramName}' into [{min}, {max}] and retry.");
            return null;
        }

        /// <summary>
        /// 校验资源路径安全性：阻止路径穿越，并限制在 Assets/ 或 Packages/ 之下。
        /// 用法：if (Validate.SafePath(path, "path") is object err) return err;
        /// </summary>
        public static object SafePath(string path, string paramName, bool isDelete = false)
        {
            if (string.IsNullOrEmpty(path))
                return MissingParam($"{paramName} is required", paramName);

            var normalized = path.Replace('\\', '/');
            while (normalized.Contains("//")) normalized = normalized.Replace("//", "/");
            if (normalized.StartsWith("./")) normalized = normalized.Substring(2);

            // 阻止路径穿越。
            if (normalized.Contains(".."))
                return InvalidParam($"Path traversal not allowed: {path}",
                    "Send a normalized project-relative path with no '..' segments.");

            // 限制在 Assets/ 或 Packages/ 之下。
            if (!normalized.StartsWith("Assets/") && !normalized.StartsWith("Packages/") &&
                normalized != "Assets" && normalized != "Packages")
                return InvalidParam($"Path must start with Assets/ or Packages/: {path}",
                    "Paths are project-relative: prefix with 'Assets/' (or 'Packages/'), not an absolute disk path.");

            // 禁止删除根目录。
            if (isDelete && (normalized == "Assets" || normalized == "Assets/" ||
                            normalized == "Packages" || normalized == "Packages/"))
                return InvalidParam("Cannot delete root Assets or Packages folder",
                    "Target a specific asset or subfolder instead of the project root.");

            return null;
        }

        /// <summary>
        /// 同时校验资源路径的安全性与存在性。
        /// 用法：if (Validate.SafePathExists(path, "path") is object err) return err;
        /// </summary>
        public static object SafePathExists(string path, string paramName)
        {
            var safeErr = SafePath(path, paramName);
            if (safeErr != null) return safeErr;
            if (!SkillsCommon.PathExists(path))
                return new
                {
                    error = $"Path does not exist: {path}",
                    errorCode = SkillErrorCode.TargetNotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFindAndRetry,
                    relatedSkills = new[] { "asset_find", "asset_get_info" },
                    suggestedFixes = new[]
                    {
                        new
                        {
                            action = "find_target",
                            skill = "asset_find",
                            reason = "Resolve the real project path first — asset paths are case-sensitive and must start with Assets/ or Packages/."
                        }
                    }
                };
            return null;
        }

        /// <summary>
        /// 确保文件路径的父目录存在。
        /// </summary>
        public static void EnsureDirectoryExists(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// 统一的 GameObject 查找工具，支持按 name、entityId、旧版 instanceId、
    /// Hierarchy 路径、tag、组件类型定位，并带有逐级回退的查找策略。
    /// </summary>
    public static class GameObjectFinder
    {
        private sealed class SceneObjectCache
        {
            public readonly List<GameObject> Objects = new List<GameObject>();
            public readonly Dictionary<string, string> PathsByEntityId =
                new Dictionary<string, string>(System.StringComparer.Ordinal);
            public readonly Dictionary<string, int> DepthsByEntityId =
                new Dictionary<string, int>(System.StringComparer.Ordinal);
            public readonly Dictionary<string, GameObject> PathLookup =
                new Dictionary<string, GameObject>(System.StringComparer.OrdinalIgnoreCase);
        }

        // 场景遍历元数据的请求级缓存，每个请求结束后由 InvalidateCache() 失效。
        private static SceneObjectCache _cachedSceneData;
        private static bool _cacheValid = false;

        /// <summary>
        /// 使场景对象缓存失效，应在每个请求周期结束后调用。
        /// </summary>
        public static void InvalidateCache()
        {
            _cachedSceneData = null;
            _cacheValid = false;
        }

        /// <summary>
        /// 每个请求构建并缓存一次场景遍历元数据。
        /// </summary>
        private static SceneObjectCache GetOrBuildSceneCache()
        {
            if (_cachedSceneData != null && _cacheValid)
            {
                // DestroyImmediate 之后 Unity 的托管包装对象仍留在列表里，但与 null 判等成立。
                // 检测到就重建缓存，让后续查找看到替换后的对象，而不是解引用已销毁的包装
                // （undo/redo 与测试夹具拆解时很常见）。
                if (_cachedSceneData.Objects.All(gameObject => gameObject != null))
                    return _cachedSceneData;

                InvalidateCache();
            }

            var cache = new SceneObjectCache();
            var roots = GetLoadedSceneRoots();
            var stack = new Stack<(Transform transform, string path, string sceneName, int depth)>();
            foreach (var root in roots)
                stack.Push((root.transform, root.name, root.scene.name, 0));

            while (stack.Count > 0)
            {
                var (transform, path, sceneName, depth) = stack.Pop();
                var gameObject = transform.gameObject;
                var entityId = UnityObjectIdUtility.GetEntityId(gameObject);

                cache.Objects.Add(gameObject);
                if (!string.IsNullOrEmpty(entityId))
                {
                    cache.PathsByEntityId[entityId] = path;
                    cache.DepthsByEntityId[entityId] = depth;
                }
                AddPathLookup(cache.PathLookup, path, gameObject);

                if (!string.IsNullOrEmpty(sceneName))
                    AddPathLookup(cache.PathLookup, sceneName + "/" + path, gameObject);

                foreach (Transform child in transform)
                    stack.Push((child, path + "/" + child.name, sceneName, depth + 1));
            }

            _cachedSceneData = cache;
            _cacheValid = true;
            return cache;
        }

        /// <summary>
        /// 从根节点遍历高效枚举场景中所有 GameObject（比 FindObjectsOfType 快）。
        /// 结果按请求缓存，避免同一次技能执行内重复遍历。
        /// </summary>
        private static IEnumerable<GameObject> GetAllSceneObjects()
        {
            return GetOrBuildSceneCache().Objects;
        }

        /// <summary>
        /// 取当前请求已缓存的场景对象列表。
        /// </summary>
        public static IReadOnlyList<GameObject> GetSceneObjects()
        {
            return GetOrBuildSceneCache().Objects;
        }

        /// <summary>
        /// 取场景对象在层级中的深度（走缓存）。非场景对象回落到逐级向上遍历父节点。
        /// </summary>
        public static int GetDepth(GameObject go)
        {
            if (go == null)
                return 0;

            var entityId = UnityObjectIdUtility.GetEntityId(go);
            if (_cachedSceneData != null && _cacheValid &&
                !string.IsNullOrEmpty(entityId) &&
                _cachedSceneData.DepthsByEntityId.TryGetValue(entityId, out var depth))
                return depth;

            depth = 0;
            var parent = go.transform.parent;
            while (parent != null)
            {
                depth++;
                parent = parent.parent;
            }

            if (_cachedSceneData != null && _cacheValid && !string.IsNullOrEmpty(entityId))
                _cachedSceneData.DepthsByEntityId[entityId] = depth;

            return depth;
        }

        private static void AddPathLookup(Dictionary<string, GameObject> lookup, string path, GameObject go)
        {
            if (string.IsNullOrEmpty(path) || lookup.ContainsKey(path))
                return;

            lookup[path] = go;
        }

        private static string NormalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var parts = path
                .Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            return parts.Length == 0 ? null : string.Join("/", parts);
        }

        private static IEnumerable<GameObject> GetLoadedSceneRoots()
        {
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                    yield return root;
            }
        }

        /// <summary>
        /// 用一组灵活参数查找 GameObject，逐级回退。
        /// 优先级：entityId &gt; instanceId &gt; path &gt; name（精确）&gt; name（包含）&gt; tag &gt; 组件类型
        /// </summary>
        /// <param name="name">简单名（先精确匹配，再回退到包含匹配）</param>
        /// <param name="instanceId">旧版 Unity instance ID</param>
        /// <param name="path">Hierarchy 路径，如 "Parent/Child/Target"</param>
        /// <param name="tag">按标签查找，如 "MainCamera"、"Player"</param>
        /// <param name="componentType">查找挂有该组件的第一个对象，如 "Camera"</param>
        /// <param name="entityId">Unity EntityId，以十进制 ulong 字符串表示</param>
        /// <returns>找到的 GameObject，未找到返回 null</returns>
        public static GameObject Find(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null, string entityId = null)
        {
            // 优先级 1：EntityId（最精确，且兼容 Unity 6000.5）。
            if (!string.IsNullOrWhiteSpace(entityId))
            {
                var obj = UnityObjectIdUtility.EntityIdToObject(entityId);
                if (obj is GameObject go)
                    return go;
                if (obj is Component component)
                    return component.gameObject;
            }

            // 优先级 2：旧版 instance ID。
            if (instanceId != 0)
            {
                var obj = UnityObjectIdUtility.ObjectIdToObject(instanceId);
                if (obj is GameObject go)
                    return go;
                if (obj is Component component)
                    return component.gameObject;
            }

            // 优先级 3：Hierarchy 路径（可定位嵌套对象）。
            if (!string.IsNullOrEmpty(path))
            {
                var go = FindByPath(path);
                if (go != null)
                    return go;
            }

            // 优先级 4：按简单名查找，先精确匹配。
            if (!string.IsNullOrEmpty(name))
            {
                var go = FindByNameCaseInsensitive(name);
                if (go != null)
                    return go;

                // 精确匹配不中再退到包含匹配。
                go = FindByNameContains(name);
                if (go != null)
                    return go;
            }

            // 优先级 5：按标签查找。
            if (!string.IsNullOrEmpty(tag))
            {
                var go = GetAllSceneObjects().FirstOrDefault(candidate =>
                {
                    try { return candidate.CompareTag(tag); }
                    catch { return false; }
                });
                if (go != null)
                    return go;
            }

            // 优先级 6：按组件类型查找。
            if (!string.IsNullOrEmpty(componentType))
            {
                var go = FindByComponent(componentType);
                if (go != null)
                    return go;
            }

            return null;
        }

        /// <summary>
        /// 按 Hierarchy 路径查找 GameObject，如 "Canvas/Panel/Button"。
        /// </summary>
        public static GameObject FindByPath(string path)
        {
            var normalizedPath = NormalizePathKey(path);
            if (string.IsNullOrEmpty(normalizedPath))
                return null;

            var cache = GetOrBuildSceneCache();
            if (cache.PathLookup.TryGetValue(normalizedPath, out var cachedGo))
                return cachedGo;

            var parts = normalizedPath.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return null;

            foreach (var scene in Enumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt)
                .Where(scene => scene.IsValid() && scene.isLoaded))
            {
                var rootObjects = scene.GetRootGameObjects();
                int partIndex = 0;

                if (parts.Length > 1 && scene.name.Equals(parts[0], System.StringComparison.OrdinalIgnoreCase))
                    partIndex = 1;

                if (partIndex >= parts.Length)
                    continue;

                var current = rootObjects.FirstOrDefault(go =>
                    go.name.Equals(parts[partIndex], System.StringComparison.OrdinalIgnoreCase));
                if (current == null)
                    continue;

                partIndex++;
                while (partIndex < parts.Length && current != null)
                {
                    current = FindDirectChild(current, parts[partIndex]);
                    partIndex++;
                }

                if (current != null)
                    return current;
            }

            return null;
        }

        private static GameObject FindDirectChild(GameObject parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName))
                return null;

            var exact = parent.transform.Find(childName);
            if (exact != null)
                return exact.gameObject;

            foreach (Transform child in parent.transform)
            {
                if (child.name.Equals(childName, System.StringComparison.OrdinalIgnoreCase))
                    return child.gameObject;
            }

            return null;
        }

        /// <summary>
        /// 按名查找 GameObject，大小写不敏感。
        /// </summary>
        public static GameObject FindByNameCaseInsensitive(string name)
        {
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 按名字包含指定子串查找 GameObject。
        /// </summary>
        public static GameObject FindByNameContains(string name)
        {
            // 优先整词匹配。
            var exactWord = GetAllSceneObjects()
                .FirstOrDefault(go => go.name.Split(' ', '_', '-').Any(
                    word => word.Equals(name, System.StringComparison.OrdinalIgnoreCase)));
            if (exactWord != null)
                return exactWord;

            // 整词不中再退到子串包含。
            return GetAllSceneObjects()
                .FirstOrDefault(go => go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 查找挂有指定组件类型的第一个 GameObject。
        /// </summary>
        public static GameObject FindByComponent(string componentType)
        {
            var type = ComponentSkills.FindComponentType(componentType);
            if (type == null) return null;

            return GetAllSceneObjects().FirstOrDefault(go => go.GetComponent(type) != null);
        }

        /// <summary>
        /// 查找符合条件的全部 GameObject。
        /// </summary>
        public static List<GameObject> FindAll(string name = null, string tag = null, string componentType = null, bool includeInactive = false)
        {
            IEnumerable<GameObject> results;

            results = GetAllSceneObjects();

            if (!includeInactive)
                results = results.Where(go => go.activeInHierarchy);

            if (!string.IsNullOrEmpty(tag))
            {
                results = results.Where(go =>
                {
                    try { return go.CompareTag(tag); }
                    catch { return false; }
                });
            }

            if (!string.IsNullOrEmpty(name))
            {
                results = results.Where(go => 
                    go.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrEmpty(componentType))
            {
                var type = ComponentSkills.FindComponentType(componentType);
                if (type != null)
                    results = results.Where(go => go.GetComponent(type) != null);
            }

            return results.ToList();
        }

        /// <summary>
        /// 取 GameObject 的完整 Hierarchy 路径。
        /// </summary>
        public static string GetPath(GameObject go)
        {
            if (go == null)
                return null;

            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }

        /// <summary>
        /// 走请求级缓存取完整 Hierarchy 路径。大批量只读遍历应优先用这个。
        /// </summary>
        public static string GetCachedPath(GameObject go)
        {
            if (go == null)
                return null;

            var entityId = UnityObjectIdUtility.GetEntityId(go);
            var cache = GetOrBuildSceneCache();
            if (!string.IsNullOrEmpty(entityId) &&
                cache.PathsByEntityId.TryGetValue(entityId, out var cachedPath))
                return cachedPath;

            var path = GetPath(go);
            if (!string.IsNullOrEmpty(entityId))
                cache.PathsByEntityId[entityId] = path;
            return path;
        }

        /// <summary>
        /// 查找对象，找不到时返回带近似候选建议的错误。
        /// </summary>
        public static (GameObject go, object error) FindOrError(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null, string entityId = null)
        {
            var go = Find(name, instanceId, path, tag, componentType, entityId);
            if (go == null)
            {
                var identifier = !string.IsNullOrEmpty(entityId) ? $"entityId {entityId}" :
                    instanceId != 0 ? $"instanceId {instanceId}" :
                    !string.IsNullOrEmpty(path) ? $"path '{path}'" :
                    !string.IsNullOrEmpty(tag) ? $"tag '{tag}'" :
                    !string.IsNullOrEmpty(componentType) ? $"component '{componentType}'" :
                    $"name '{name}'";

                var suggestions = GetSuggestions(name, tag, componentType);

                return (null, new {
                    error = $"GameObject not found: {identifier}",
                    suggestions = suggestions.Any() ? suggestions : null,
                    errorCode = SkillErrorCode.TargetNotFound.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFindAndRetry,
                    relatedSkills = new[] { "gameobject_find", "scene_get_hierarchy" },
                    suggestedFixes = BuildNotFoundFixes(identifier, suggestions)
                });
            }
            return (go, null);
        }

        /// <summary>
        /// 把近似候选转成 suggestedFixes。不这么做的话候选算出来也会被路由丢弃——
        /// 路由只会读错误字符串。
        /// </summary>
        private static object[] BuildNotFoundFixes(string identifier, string[] suggestions)
        {
            var fixes = new List<object>();

            foreach (var candidate in suggestions.Take(3))
            {
                fixes.Add(new
                {
                    action = "find_target",
                    skill = "gameobject_find",
                    reason = $"Close match already in an open scene: {candidate}"
                });
            }

            fixes.Add(new
            {
                action = "find_target",
                skill = "scene_get_hierarchy",
                reason = $"Nothing matched {identifier}. List the open scenes' hierarchy, then retry with the exact path or the entityId it returns."
            });

            return fixes.ToArray();
        }

        /// <summary>
        /// 查找 GameObject 并取其上必需的组件，任一步失败都返回错误。
        /// </summary>
        public static (T component, object error) FindComponentOrError<T>(string name = null, int instanceId = 0, string path = null, string entityId = null) where T : Component
        {
            var (go, err) = FindOrError(name, instanceId, path, entityId: entityId);
            if (err != null) return (null, err);
            var comp = go.GetComponent<T>();
            if (comp == null) return (null, new { error = $"No {typeof(T).Name} component on {go.name}" });
            return (comp, null);
        }

        /// <summary>
        /// 查找失败时给出相近对象的候选建议。
        /// </summary>
        private static string[] GetSuggestions(string name, string tag, string componentType)
        {
            var suggestions = new List<string>();

            if (!string.IsNullOrEmpty(name))
            {
                // 用名字前 3 个字符做模糊匹配。
                var similar = GetAllSceneObjects()
                    .Where(go => go.name.IndexOf(name.Substring(0, System.Math.Min(3, name.Length)),
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(5)
                    .Select(go => $"'{go.name}' (path: {GetPath(go)})");
                suggestions.AddRange(similar);
            }

            if (!string.IsNullOrEmpty(componentType))
            {
                // 再补上确实挂有该组件的对象。
                var type = ComponentSkills.FindComponentType(componentType);
                if (type != null)
                {
                    var withComp = GetAllSceneObjects()
                        .Where(candidate => candidate.GetComponent(type) != null)
                        .Take(3)
                        .Select(candidate => $"'{candidate.name}' has {type.Name}");
                    suggestions.AddRange(withComp);
                }
            }

            return suggestions.Take(5).ToArray();
        }

        /// <summary>
        /// 依次尝试多种策略的智能查找，供不确定精确名字的 AI 调用方使用。
        /// </summary>
        public static GameObject SmartFind(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;

            // 当作精确名。
            var go = FindByNameCaseInsensitive(query);
            if (go != null) return go;

            // 当作路径。
            go = FindByPath(query);
            if (go != null) return go;

            // 当作标签。
            go = Find(tag: query);
            if (go != null) return go;

            // "Main Camera" 的各种叫法。
            if (query.Equals("camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("main camera", System.StringComparison.OrdinalIgnoreCase) ||
                query.Equals("maincamera", System.StringComparison.OrdinalIgnoreCase))
            {
                go = Camera.main?.gameObject;
                if (go != null) return go;
                
                // 没有 Camera.main 就退而取场景里任意一个相机。
                var cam = GetAllSceneObjects()
                    .Select(candidate => candidate.GetComponent<Camera>())
                    .FirstOrDefault(component => component != null);
                if (cam != null) return cam.gameObject;
            }

            // "Player" 的各种叫法。
            if (query.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                go = Find(tag: "Player");
                if (go != null) return go;
            }

            // 大小写不敏感的子串包含。
            go = FindByNameContains(query);
            if (go != null) return go;

            // 最后当作组件类型名。
            go = FindByComponent(query);
            return go;
        }
    }

    /// <summary>
    /// 各技能模块共用的工具方法。
    /// </summary>
    public static class SkillsCommon
    {
        /// <summary>不带 BOM 的 UTF-8 编码。</summary>
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>共享 JSON 设置：Unicode 直出可读，不做转义。</summary>
        public static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.Default
        };

        /// <summary>
        /// 与 <see cref="JsonSettings"/> 相同，但 null 成员会被丢弃而不是写成 <c>null</c>。
        /// 专供 <c>?wire=v2</c> 的 manifest 载荷使用——那里"字段缺席"意为"默认 / 不适用"。
        /// 其余响应必须继续输出显式 null，切勿把已有路径改走这个实例。
        /// </summary>
        public static readonly JsonSerializerSettings JsonSettingsOmitNull = new JsonSerializerSettings
        {
            StringEscapeHandling = Newtonsoft.Json.StringEscapeHandling.Default,
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
        };

        /// <summary>
        /// 取所有非动态程序集中已加载的全部类型。
        /// </summary>
        public static System.Collections.Generic.IEnumerable<System.Type> GetAllLoadedTypes()
        {
            return System.AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a => { try { return a.GetTypes(); } catch { return System.Type.EmptyTypes; } });
        }

        /// <summary>
        /// 统计网格三角面数，不分配完整的 triangles 数组。
        /// </summary>
        public static int GetTriangleCount(UnityEngine.Mesh mesh)
        {
            int count = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                count += (int)mesh.GetIndexCount(i);
            return count / 3;
        }

        /// <summary>路径存在（文件或目录皆可）时返回 true。</summary>
        public static bool PathExists(string path) =>
            !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));

        // -----------------------------------------------------------------
        // 统一类型查找（带缓存，各 ReflectionHelper 共用）
        // -----------------------------------------------------------------

        private static readonly Dictionary<string, System.Type> _findTypeCache =
            new Dictionary<string, System.Type>();

        /// <summary>
        /// 在所有已加载程序集中按全限定名查找类型。
        /// 结果会缓存（未命中的 null 也缓存），后续查找为 O(1)。
        /// </summary>
        public static System.Type FindTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_findTypeCache.TryGetValue(fullName, out var cached)) return cached;

            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, throwOnError: false);
                    if (t != null) { _findTypeCache[fullName] = t; return t; }
                }
                catch { /* skip assemblies that fail to enumerate */ }
            }

            _findTypeCache[fullName] = null;
            return null;
        }
    }
}

// Producer:Betsy
