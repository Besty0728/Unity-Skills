using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Scene Understanding Skills - Help AI quickly perceive project state.
    /// </summary>
    public static class PerceptionSkills
    {
        [UnitySkill("scene_summarize", "Get a structured summary of the current scene (object counts, component stats, hierarchy depth)")]
        public static object SceneSummarize(bool includeComponentStats = true, int topComponentsLimit = 10)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var rootObjects = scene.GetRootGameObjects();
            
            // Basic stats
            int totalObjects = allObjects.Length;
            int activeObjects = allObjects.Count(g => g.activeInHierarchy);
            int maxDepth = 0;
            
            // Component stats
            var componentCounts = new Dictionary<string, int>();
            
            foreach (var go in allObjects)
            {
                // Calculate depth
                int depth = 0;
                var t = go.transform;
                while (t.parent != null) { depth++; t = t.parent; }
                if (depth > maxDepth) maxDepth = depth;
                
                // Count components
                if (includeComponentStats)
                {
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var typeName = comp.GetType().Name;
                        if (!componentCounts.ContainsKey(typeName))
                            componentCounts[typeName] = 0;
                        componentCounts[typeName]++;
                    }
                }
            }
            
            // Top components (excluding Transform which is on everything)
            componentCounts.Remove("Transform");
            var topComponents = componentCounts
                .OrderByDescending(kv => kv.Value)
                .Take(topComponentsLimit)
                .Select(kv => new { component = kv.Key, count = kv.Value })
                .ToList();
            
            // Lights, Cameras, Canvas counts
            int lightCount = UnityEngine.Object.FindObjectsOfType<Light>().Length;
            int cameraCount = UnityEngine.Object.FindObjectsOfType<Camera>().Length;
            int canvasCount = UnityEngine.Object.FindObjectsOfType<Canvas>().Length;
            
            return new
            {
                success = true,
                sceneName = scene.name,
                scenePath = scene.path,
                isDirty = scene.isDirty,
                stats = new
                {
                    totalObjects,
                    activeObjects,
                    inactiveObjects = totalObjects - activeObjects,
                    rootObjects = rootObjects.Length,
                    maxHierarchyDepth = maxDepth,
                    lights = lightCount,
                    cameras = cameraCount,
                    canvases = canvasCount
                },
                topComponents
            };
        }

        [UnitySkill("hierarchy_describe", "Get a text tree of the scene hierarchy (like 'tree' command)")]
        public static object HierarchyDescribe(int maxDepth = 5, bool includeInactive = false, int maxItemsPerLevel = 20)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects()
                .Where(g => includeInactive || g.activeInHierarchy)
                .OrderBy(g => g.transform.GetSiblingIndex())
                .Take(maxItemsPerLevel)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"Scene: {scene.name}");
            sb.AppendLine("─".PadRight(40, '─'));

            int totalShown = 0;
            foreach (var root in rootObjects)
            {
                BuildHierarchyTree(sb, root.transform, 0, maxDepth, includeInactive, maxItemsPerLevel, ref totalShown);
            }

            if (scene.GetRootGameObjects().Length > maxItemsPerLevel)
            {
                sb.AppendLine($"... and {scene.GetRootGameObjects().Length - maxItemsPerLevel} more root objects");
            }

            return new
            {
                success = true,
                sceneName = scene.name,
                hierarchy = sb.ToString(),
                totalObjectsShown = totalShown
            };
        }

        private static void BuildHierarchyTree(StringBuilder sb, Transform t, int depth, int maxDepth, bool includeInactive, int maxItems, ref int total)
        {
            if (depth > maxDepth) return;
            if (!includeInactive && !t.gameObject.activeInHierarchy) return;

            total++;
            string indent = new string(' ', depth * 2);
            string prefix = depth == 0 ? "► " : "├─";
            string activeMarker = t.gameObject.activeSelf ? "" : " [inactive]";
            
            // Get main component hint
            string componentHint = "";
            if (t.GetComponent<Camera>()) componentHint = " 📷";
            else if (t.GetComponent<Light>()) componentHint = " 💡";
            else if (t.GetComponent<Canvas>()) componentHint = " 🖼";
            else if (t.GetComponent<UnityEngine.UI.Button>()) componentHint = " 🔘";
            else if (t.GetComponent<MeshRenderer>()) componentHint = " ▣";

            sb.AppendLine($"{indent}{prefix} {t.name}{componentHint}{activeMarker}");

            int childrenShown = 0;
            foreach (Transform child in t)
            {
                if (childrenShown >= maxItems)
                {
                    sb.AppendLine($"{indent}  ... and {t.childCount - childrenShown} more children");
                    break;
                }
                BuildHierarchyTree(sb, child, depth + 1, maxDepth, includeInactive, maxItems, ref total);
                childrenShown++;
            }
        }

        [UnitySkill("script_analyze", "Analyze a MonoBehaviour script's public API (methods, fields, properties)")]
        public static object ScriptAnalyze(string scriptName, bool includePrivate = false)
        {
            // Find the type
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase) && 
                                     typeof(MonoBehaviour).IsAssignableFrom(t));

            if (type == null)
            {
                return new { success = false, error = $"MonoBehaviour '{scriptName}' not found" };
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate) flags |= BindingFlags.NonPublic;

            // Fields
            var fields = type.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<")) // Skip backing fields
                .Select(f => new
                {
                    name = f.Name,
                    type = GetFriendlyTypeName(f.FieldType),
                    isSerializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                })
                .ToList();

            // Properties
            var properties = type.GetProperties(flags)
                .Where(p => p.CanRead)
                .Select(p => new
                {
                    name = p.Name,
                    type = GetFriendlyTypeName(p.PropertyType),
                    canWrite = p.CanWrite
                })
                .ToList();

            // Methods
            var methods = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName) // Skip property getters/setters
                .Select(m => new
                {
                    name = m.Name,
                    returnType = GetFriendlyTypeName(m.ReturnType),
                    parameters = string.Join(", ", m.GetParameters().Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"))
                })
                .ToList();

            // Unity Events (like OnCollisionEnter, Update, etc.)
            var unityEvents = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => IsUnityCallback(m.Name))
                .Select(m => m.Name)
                .ToList();

            return new
            {
                success = true,
                script = scriptName,
                fullName = type.FullName,
                baseClass = type.BaseType?.Name,
                fields,
                properties,
                methods,
                unityCallbacks = unityEvents
            };
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type.IsGenericType)
            {
                var baseName = type.Name.Split('`')[0];
                var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
                return $"{baseName}<{args}>";
            }
            if (type.IsArray)
            {
                return GetFriendlyTypeName(type.GetElementType()) + "[]";
            }
            return type.Name;
        }

        private static bool IsUnityCallback(string name)
        {
            var callbacks = new HashSet<string>
            {
                "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
                "OnEnable", "OnDisable", "OnDestroy",
                "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
                "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
                "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
                "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
                "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit",
                "OnGUI", "OnDrawGizmos", "OnValidate"
            };
            return callbacks.Contains(name);
        }
    }
}
