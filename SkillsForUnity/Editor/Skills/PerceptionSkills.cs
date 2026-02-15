using UnityEngine;
using UnityEditor;
using System;
using System.IO;
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
        private static readonly HashSet<string> UnityCallbacks = new HashSet<string>
        {
            "Awake", "Start", "Update", "FixedUpdate", "LateUpdate",
            "OnEnable", "OnDisable", "OnDestroy",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay",
            "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
            "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit",
            "OnGUI", "OnDrawGizmos", "OnValidate",
            "OnBecameVisible", "OnBecameInvisible",
            "OnApplicationPause", "OnApplicationQuit", "OnApplicationFocus",
            "OnAnimatorIK", "OnAnimatorMove",
            "OnParticleCollision", "OnParticleTrigger",
            "OnRenderObject", "OnPreRender", "OnPostRender",
            "OnWillRenderObject", "OnRenderImage"
        };

        [UnitySkill("scene_summarize", "Get a structured summary of the current scene (object counts, component stats, hierarchy depth)")]
        public static object SceneSummarize(bool includeComponentStats = true, int topComponentsLimit = 10)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var rootObjects = scene.GetRootGameObjects();

            int totalObjects = allObjects.Length;
            int activeObjects = 0;
            int maxDepth = 0;
            int lightCount = 0, cameraCount = 0, canvasCount = 0;
            var componentCounts = new Dictionary<string, int>();

            foreach (var go in allObjects)
            {
                if (go.activeInHierarchy) activeObjects++;

                // Calculate depth
                int depth = 0;
                var t = go.transform;
                while (t.parent != null) { depth++; t = t.parent; }
                if (depth > maxDepth) maxDepth = depth;

                // Count components in single pass
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var typeName = comp.GetType().Name;

                    // Count key types inline
                    if (comp is Light) lightCount++;
                    else if (comp is Camera) cameraCount++;
                    else if (comp is Canvas) canvasCount++;

                    if (includeComponentStats)
                    {
                        if (!componentCounts.ContainsKey(typeName))
                            componentCounts[typeName] = 0;
                        componentCounts[typeName]++;
                    }
                }
            }

            componentCounts.Remove("Transform");
            var topComponents = componentCounts
                .OrderByDescending(kv => kv.Value)
                .Take(topComponentsLimit)
                .Select(kv => new { component = kv.Key, count = kv.Value })
                .ToList();

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

            var allRoots = scene.GetRootGameObjects();
            if (allRoots.Length > maxItemsPerLevel)
            {
                sb.AppendLine($"... and {allRoots.Length - maxItemsPerLevel} more root objects");
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
            string componentHint = GetComponentHint(t);

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

        private static string GetComponentHint(Transform t)
        {
            if (t.GetComponent<Camera>()) return " 📷";
            if (t.GetComponent<Light>()) return " 💡";
            if (t.GetComponent<Canvas>()) return " 🖼";
            if (t.GetComponent<UnityEngine.UI.Button>()) return " 🔘";
            if (t.GetComponent<Animator>()) return " 🎬";
            if (t.GetComponent<AudioSource>()) return " 🔊";
            if (t.GetComponent<ParticleSystem>()) return " ✨";
            if (t.GetComponent<Collider>() || t.GetComponent<Collider2D>()) return " 🧱";
            if (t.GetComponent<Rigidbody>() || t.GetComponent<Rigidbody2D>()) return " ⚙";
            if (t.GetComponent<SkinnedMeshRenderer>()) return " 🦴";
            if (t.GetComponent<MeshRenderer>()) return " ▣";
            if (t.GetComponent<SpriteRenderer>()) return " 🖾";
            if (t.GetComponent<UnityEngine.UI.Text>() || t.GetComponent<UnityEngine.UI.Image>()) return " 🎨";
            return "";
        }

        [UnitySkill("script_analyze", "Analyze a script's public API (MonoBehaviour, ScriptableObject, or plain class)")]
        public static object ScriptAnalyze(string scriptName, bool includePrivate = false)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                .FirstOrDefault(t => t.Name.Equals(scriptName, StringComparison.OrdinalIgnoreCase) &&
                                     (typeof(MonoBehaviour).IsAssignableFrom(t) ||
                                      typeof(ScriptableObject).IsAssignableFrom(t) ||
                                      (t.IsClass && !t.IsAbstract && t.Namespace != null &&
                                       !t.Namespace.StartsWith("Unity") && !t.Namespace.StartsWith("System"))));

            if (type == null)
            {
                return new { success = false, error = $"Script '{scriptName}' not found (searched MonoBehaviour, ScriptableObject, and user classes)" };
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate) flags |= BindingFlags.NonPublic;

            var fields = type.GetFields(flags)
                .Where(f => !f.Name.StartsWith("<"))
                .Select(f => new
                {
                    name = f.Name,
                    type = GetFriendlyTypeName(f.FieldType),
                    isSerializable = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null
                })
                .ToList();

            var properties = type.GetProperties(flags)
                .Where(p => p.CanRead)
                .Select(p => new
                {
                    name = p.Name,
                    type = GetFriendlyTypeName(p.PropertyType),
                    canWrite = p.CanWrite
                })
                .ToList();

            var methods = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName)
                .Select(m => new
                {
                    name = m.Name,
                    returnType = GetFriendlyTypeName(m.ReturnType),
                    parameters = string.Join(", ", m.GetParameters().Select(p => $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"))
                })
                .ToList();

            // Unity callbacks only for MonoBehaviour
            List<string> unityEvents = null;
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                unityEvents = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => UnityCallbacks.Contains(m.Name))
                    .Select(m => m.Name)
                    .ToList();
            }

            string scriptKind = typeof(MonoBehaviour).IsAssignableFrom(type) ? "MonoBehaviour"
                : typeof(ScriptableObject).IsAssignableFrom(type) ? "ScriptableObject"
                : "Class";

            return new
            {
                success = true,
                script = scriptName,
                fullName = type.FullName,
                kind = scriptKind,
                baseClass = type.BaseType?.Name,
                fields,
                properties,
                methods,
                unityCallbacks = unityEvents
            };
        }

        [UnitySkill("scene_spatial_query", "Find objects within a radius of a point, or near another object")]
        public static object SceneSpatialQuery(
            float x = 0, float y = 0, float z = 0,
            float radius = 10f,
            string nearObject = null,
            string componentFilter = null,
            int maxResults = 50)
        {
            Vector3 center;

            if (!string.IsNullOrEmpty(nearObject))
            {
                var go = GameObjectFinder.Find(nearObject);
                if (go == null)
                    return new { success = false, error = $"Object '{nearObject}' not found" };
                center = go.transform.position;
            }
            else
            {
                center = new Vector3(x, y, z);
            }

            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            float radiusSq = radius * radius;

            Type filterType = null;
            if (!string.IsNullOrEmpty(componentFilter))
            {
                filterType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                    .FirstOrDefault(t => t.Name.Equals(componentFilter, StringComparison.OrdinalIgnoreCase) &&
                                         typeof(Component).IsAssignableFrom(t));
            }

            var found = new List<(float dist, object info)>();
            foreach (var go in allObjects)
            {
                if (filterType != null && go.GetComponent(filterType) == null) continue;

                var pos = go.transform.position;
                float distSq = (pos - center).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    found.Add((dist, new
                    {
                        name = go.name,
                        path = GameObjectFinder.GetPath(go),
                        distance = dist,
                        position = new { x = pos.x, y = pos.y, z = pos.z }
                    }));
                }
            }

            var results = found.Count <= maxResults
                ? found.Select(f => f.info).ToList()
                : found.OrderBy(f => f.dist).Take(maxResults).Select(f => f.info).ToList();

            return new
            {
                success = true,
                center = new { x = center.x, y = center.y, z = center.z },
                radius,
                totalFound = found.Count,
                results
            };
        }

        [UnitySkill("scene_materials", "Get an overview of all materials and shaders used in the current scene")]
        public static object SceneMaterials(bool includeProperties = false)
        {
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            var materialMap = new Dictionary<string, MaterialInfo>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    var key = mat.GetInstanceID().ToString();
                    if (!materialMap.ContainsKey(key))
                    {
                        materialMap[key] = new MaterialInfo
                        {
                            name = mat.name,
                            shader = mat.shader != null ? mat.shader.name : "null",
                            renderQueue = mat.renderQueue,
                            path = AssetDatabase.GetAssetPath(mat),
                            users = new List<string>()
                        };
                        if (includeProperties && mat.shader != null)
                        {
                            var props = new List<object>();
                            int count = ShaderUtil.GetPropertyCount(mat.shader);
                            for (int i = 0; i < count; i++)
                            {
                                props.Add(new
                                {
                                    name = ShaderUtil.GetPropertyName(mat.shader, i),
                                    type = ShaderUtil.GetPropertyType(mat.shader, i).ToString()
                                });
                            }
                            materialMap[key].properties = props;
                        }
                    }
                    materialMap[key].users.Add(renderer.gameObject.name);
                }
            }

            // Group by shader
            var shaderGroups = materialMap.Values
                .GroupBy(m => m.shader)
                .Select(g => new
                {
                    shader = g.Key,
                    materialCount = g.Count(),
                    materials = g.Select(m => new
                    {
                        m.name, m.path, m.renderQueue,
                        userCount = m.users.Count,
                        users = m.users.Take(5).ToList(),
                        properties = includeProperties ? m.properties : null
                    }).ToList()
                })
                .OrderByDescending(g => g.materialCount)
                .ToList();

            return new
            {
                success = true,
                totalMaterials = materialMap.Count,
                totalShaders = shaderGroups.Count,
                shaders = shaderGroups
            };
        }

        private class MaterialInfo
        {
            public string name, shader, path;
            public int renderQueue;
            public List<string> users;
            public List<object> properties;
        }

        [UnitySkill("scene_context", "Generate a comprehensive scene snapshot for AI coding assistance (hierarchy, components, script fields, references, UI layout)")]
        public static object SceneContext(
            int maxDepth = 10,
            int maxObjects = 200,
            string rootPath = null,
            bool includeValues = false,
            bool includeReferences = true)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var totalObjects = UnityEngine.Object.FindObjectsOfType<GameObject>().Length;

            // Determine roots
            Transform[] roots;
            if (!string.IsNullOrEmpty(rootPath))
            {
                var rootGo = GameObjectFinder.FindByPath(rootPath);
                if (rootGo == null)
                    return new { success = false, error = $"Root path '{rootPath}' not found" };
                roots = new[] { rootGo.transform };
            }
            else
            {
                roots = scene.GetRootGameObjects().Select(g => g.transform).ToArray();
            }

            // BFS traversal
            var objects = new List<object>();
            var references = new List<object>();
            var queue = new Queue<(Transform t, int depth)>();
            foreach (var r in roots) queue.Enqueue((r, 0));

            while (queue.Count > 0 && objects.Count < maxObjects)
            {
                var (t, depth) = queue.Dequeue();
                objects.Add(BuildObjectInfo(t.gameObject, includeValues, includeReferences, references));

                if (depth + 1 <= maxDepth)
                {
                    foreach (Transform child in t)
                        queue.Enqueue((child, depth + 1));
                }
            }

            var result = new
            {
                success = true,
                sceneName = scene.name,
                totalObjects,
                exportedObjects = objects.Count,
                truncated = objects.Count < totalObjects || queue.Count > 0,
                objects,
                references = includeReferences ? references : null
            };
            return result;
        }

        private static object BuildObjectInfo(GameObject go, bool includeValues, bool includeReferences, List<object> refs)
        {
            var path = GameObjectFinder.GetPath(go);
            var components = new List<object>();

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                components.Add(BuildComponentInfo(comp, path, includeValues, includeReferences, refs));
            }

            var children = new List<string>();
            foreach (Transform child in go.transform)
                children.Add(GameObjectFinder.GetPath(child.gameObject));

            return new
            {
                path,
                name = go.name,
                active = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                components,
                children
            };
        }

        private static object BuildComponentInfo(Component comp, string objPath, bool includeValues, bool includeReferences, List<object> refs)
        {
            var type = comp.GetType();
            var typeName = type.Name;

            // MonoBehaviour → serialized fields
            if (comp is MonoBehaviour)
            {
                var fields = ExtractSerializedFields(comp, objPath, includeValues, includeReferences, refs);
                return new { type = typeName, kind = "MonoBehaviour", fields };
            }

            // Built-in components: only output props when includeValues is true
            if (includeValues)
            {
                var props = GetBuiltinComponentProps(comp);
                return new { type = typeName, props };
            }
            return new { type = typeName };
        }

        private static readonly HashSet<string> SkipFields = new HashSet<string>
        {
            "m_Script", "m_ObjectHideFlags", "m_CorrespondingSourceObject",
            "m_PrefabInstance", "m_PrefabAsset", "m_GameObject", "m_Enabled"
        };

        private static Dictionary<string, object> ExtractSerializedFields(Component comp, string objPath, bool includeValues, bool includeReferences, List<object> refs)
        {
            var fields = new Dictionary<string, object>();
            var so = new SerializedObject(comp);
            var prop = so.GetIterator();
            bool enterChildren = true;

            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (SkipFields.Contains(prop.name)) continue;
                if (prop.propertyType == SerializedPropertyType.ArraySize) continue;

                var fieldType = prop.propertyType.ToString();

                // Always extract ObjectReference refs (independent of includeValues)
                if (prop.propertyType == SerializedPropertyType.ObjectReference && prop.objectReferenceValue != null)
                {
                    var refObj = prop.objectReferenceValue;
                    fieldType = refObj.GetType().Name;
                    string refPath = null;
                    if (refObj is GameObject refGo)
                        refPath = GameObjectFinder.GetPath(refGo);
                    else if (refObj is Component refComp)
                        refPath = GameObjectFinder.GetPath(refComp.gameObject);

                    if (includeReferences && refPath != null)
                        refs.Add(new { from = $"{objPath}:{comp.GetType().Name}.{prop.name}", to = refPath });

                    if (includeValues)
                        fields[prop.name] = new { type = fieldType, value = (object)(refPath ?? refObj.name) };
                    else
                        fields[prop.name] = fieldType;
                    continue;
                }

                if (!includeValues)
                {
                    fields[prop.name] = fieldType;
                    continue;
                }

                // includeValues=true: extract actual values
                object value;
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: value = prop.intValue; break;
                    case SerializedPropertyType.Float: value = prop.floatValue; break;
                    case SerializedPropertyType.Boolean: value = prop.boolValue; break;
                    case SerializedPropertyType.String:
                        var sv = prop.stringValue;
                        value = sv != null && sv.Length > 100 ? sv.Substring(0, 100) + "..." : sv;
                        break;
                    case SerializedPropertyType.Enum: value = prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex; break;
                    case SerializedPropertyType.Vector2: value = FormatVec(prop.vector2Value); break;
                    case SerializedPropertyType.Vector3: value = FormatVec(prop.vector3Value); break;
                    case SerializedPropertyType.Vector4: value = FormatVec(prop.vector4Value); break;
                    case SerializedPropertyType.Color: var c = prop.colorValue; value = $"({c.r:F2}, {c.g:F2}, {c.b:F2}, {c.a:F2})"; break;
                    case SerializedPropertyType.ObjectReference: value = "null"; break; // ref is null (non-null handled above)
                    default:
                        value = prop.isArray ? $"{prop.arrayElementType}[{prop.arraySize}]" : fieldType;
                        break;
                }
                fields[prop.name] = new { type = fieldType, value };
            }
            return fields;
        }

        private static string FormatVec(Vector2 v) => $"({v.x}, {v.y})";
        private static string FormatVec(Vector3 v) => $"({v.x}, {v.y}, {v.z})";
        private static string FormatVec(Vector4 v) => $"({v.x}, {v.y}, {v.z}, {v.w})";

        private static Dictionary<string, object> GetBuiltinComponentProps(Component comp)
        {
            var props = new Dictionary<string, object>();

            switch (comp)
            {
                case RectTransform rt:
                    props["anchoredPosition"] = FormatVec(rt.anchoredPosition);
                    props["sizeDelta"] = FormatVec(rt.sizeDelta);
                    props["anchorMin"] = FormatVec(rt.anchorMin);
                    props["anchorMax"] = FormatVec(rt.anchorMax);
                    props["pivot"] = FormatVec(rt.pivot);
                    break;
                case Transform t:
                    props["position"] = FormatVec(t.position);
                    props["rotation"] = FormatVec(t.eulerAngles);
                    props["scale"] = FormatVec(t.localScale);
                    break;
                case Camera cam:
                    props["fieldOfView"] = cam.fieldOfView;
                    props["orthographic"] = cam.orthographic;
                    props["clearFlags"] = cam.clearFlags.ToString();
                    props["cullingMask"] = cam.cullingMask;
                    break;
                case Light light:
                    props["type"] = light.type.ToString();
                    props["color"] = $"({light.color.r:F2}, {light.color.g:F2}, {light.color.b:F2})";
                    props["intensity"] = light.intensity;
                    props["range"] = light.range;
                    break;
                case Renderer rend:
                    props["material"] = rend.sharedMaterial != null ? rend.sharedMaterial.name : "null";
                    props["enabled"] = rend.enabled;
                    break;
                case Canvas canvas:
                    props["renderMode"] = canvas.renderMode.ToString();
                    props["sortingOrder"] = canvas.sortingOrder;
                    break;
                case CanvasGroup cg:
                    props["alpha"] = cg.alpha;
                    props["interactable"] = cg.interactable;
                    props["blocksRaycasts"] = cg.blocksRaycasts;
                    break;
                case UnityEngine.UI.Button btn:
                    props["interactable"] = btn.interactable;
                    props["transition"] = btn.transition.ToString();
                    break;
                case UnityEngine.UI.Text txt:
                    var textVal = txt.text;
                    props["text"] = textVal != null && textVal.Length > 50 ? textVal.Substring(0, 50) + "..." : textVal;
                    props["fontSize"] = txt.fontSize;
                    props["color"] = $"({txt.color.r:F2}, {txt.color.g:F2}, {txt.color.b:F2}, {txt.color.a:F2})";
                    break;
                case UnityEngine.UI.Image img:
                    props["sprite"] = img.sprite != null ? img.sprite.name : "null";
                    props["color"] = $"({img.color.r:F2}, {img.color.g:F2}, {img.color.b:F2}, {img.color.a:F2})";
                    props["raycastTarget"] = img.raycastTarget;
                    break;
                case Animator anim:
                    props["controller"] = anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "null";
                    props["enabled"] = anim.enabled;
                    break;
                case AudioSource audio:
                    props["clip"] = audio.clip != null ? audio.clip.name : "null";
                    props["playOnAwake"] = audio.playOnAwake;
                    props["loop"] = audio.loop;
                    props["volume"] = audio.volume;
                    break;
                case Collider col:
                    props["isTrigger"] = col.isTrigger;
                    props["enabled"] = col.enabled;
                    break;
                case Collider2D col2d:
                    props["isTrigger"] = col2d.isTrigger;
                    props["enabled"] = col2d.enabled;
                    break;
                case Rigidbody rb:
                    props["mass"] = rb.mass;
                    props["useGravity"] = rb.useGravity;
                    props["isKinematic"] = rb.isKinematic;
                    break;
                default:
                    props["enabled"] = IsComponentEnabled(comp);
                    break;
            }
            return props;
        }

        private static object IsComponentEnabled(Component comp)
        {
            if (comp is Behaviour b) return b.enabled;
            if (comp is Renderer r) return r.enabled;
            if (comp is Collider c) return c.enabled;
            return null;
        }

        [UnitySkill("scene_export_report", "Export complete scene structure and script dependency report as markdown file. Use when user asks to: export scene report, generate scene document, save scene overview, create scene context file")]
        public static object SceneExportReport(
            string savePath = "Assets/Docs/SceneReport.md",
            int maxDepth = 10,
            int maxObjects = 500)
        {
            if (Validate.SafePath(savePath, "savePath") is object pathErr0) return pathErr0;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().Select(g => g.transform).ToArray();

            // BFS collect objects
            var objList = new List<(GameObject go, int depth)>();
            var queue = new Queue<(Transform t, int depth)>();
            foreach (var r in roots) queue.Enqueue((r, 0));
            while (queue.Count > 0 && objList.Count < maxObjects)
            {
                var (t, depth) = queue.Dequeue();
                objList.Add((t.gameObject, depth));
                if (depth + 1 <= maxDepth)
                    foreach (Transform child in t) queue.Enqueue((child, depth + 1));
            }

            // Collect serialized reference edges
            var allObjects = objList.Select(o => o.go).ToArray();
            var edges = CollectDependencyEdges(allObjects);

            // Collect C# code-level dependencies
            var codeEdges = CollectCodeDependencies();

            // Merge all edges
            var allEdges = new List<DependencyEdge>(edges);
            allEdges.AddRange(codeEdges);
            var reverseIndex = allEdges.GroupBy(e => e.toObject).ToDictionary(g => g.Key, g => g.ToList());

            // Build markdown
            var sb = new StringBuilder();
            sb.AppendLine($"# Scene Report: {scene.name}");
            int userScriptCount = 0;
            foreach (var (go, _) in objList)
                foreach (var c in go.GetComponents<MonoBehaviour>())
                    if (c != null && IsUserScript(c.GetType())) userScriptCount++;
            sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm} | Objects: {objList.Count} | User Scripts: {userScriptCount} | References: {allEdges.Count}");
            sb.AppendLine();

            // Hierarchy section — built-in components: name only; user scripts: name*
            sb.AppendLine("## Hierarchy");
            sb.AppendLine();
            foreach (var (go, depth) in objList)
            {
                var indent = new string(' ', depth * 2);
                var comps = go.GetComponents<Component>()
                    .Where(c => c != null && !(c is Transform))
                    .Select(c => IsUserScript(c.GetType()) ? c.GetType().Name + "*" : c.GetType().Name);
                var compStr = string.Join(", ", comps);
                sb.AppendLine($"{indent}{go.name}{(compStr.Length > 0 ? $" [{compStr}]" : "")}");
            }
            sb.AppendLine();

            // Script Fields section — only user scripts, with values
            var userMonos = new List<(string objPath, MonoBehaviour mb)>();
            foreach (var (go, _) in objList)
                foreach (var c in go.GetComponents<MonoBehaviour>())
                    if (c != null && IsUserScript(c.GetType()))
                        userMonos.Add((GameObjectFinder.GetPath(go), c));

            if (userMonos.Count > 0)
            {
                sb.AppendLine("## Script Fields");
                sb.AppendLine();
                foreach (var (objPath, mb) in userMonos)
                {
                    sb.AppendLine($"### {mb.GetType().Name} (on: {objPath})");
                    sb.AppendLine();
                    sb.AppendLine("| Field | Type | Value |");
                    sb.AppendLine("|-------|------|-------|");
                    var so = new SerializedObject(mb);
                    var prop = so.GetIterator();
                    bool enter = true;
                    while (prop.NextVisible(enter))
                    {
                        enter = false;
                        if (SkipFields.Contains(prop.name) || prop.propertyType == SerializedPropertyType.ArraySize) continue;
                        string ft = prop.propertyType.ToString();
                        string val = "";
                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (prop.objectReferenceValue != null)
                            {
                                var refObj = prop.objectReferenceValue;
                                ft = refObj.GetType().Name;
                                if (refObj is GameObject rg) val = GameObjectFinder.GetPath(rg);
                                else if (refObj is Component rc) val = GameObjectFinder.GetPath(rc.gameObject);
                                else val = refObj.name;
                            }
                            else val = "null";
                        }
                        else val = GetSerializedValue(prop);
                        sb.AppendLine($"| {prop.name} | {ft} | {val} |");
                    }
                    sb.AppendLine();
                }
            }

            // Code Dependencies section
            if (codeEdges.Count > 0)
            {
                sb.AppendLine("## Code Dependencies (C# source analysis)");
                sb.AppendLine();
                var byFrom = codeEdges.GroupBy(e => e.fromScript);
                foreach (var g in byFrom.OrderBy(g => g.Key))
                {
                    sb.AppendLine($"### {g.Key}");
                    foreach (var e in g)
                        sb.AppendLine($"- → **{e.toObject}** via `{e.fieldName}` ({e.fieldType})");
                    sb.AppendLine();
                }
            }

            // Dependency Graph section (merged: serialized + code)
            if (allEdges.Count > 0)
            {
                sb.AppendLine("## Dependency Graph");
                sb.AppendLine();
                sb.AppendLine("| From | To | Type | Source | Detail |");
                sb.AppendLine("|------|----|------|--------|--------|");
                foreach (var e in allEdges.OrderBy(e => e.fromObject).ThenBy(e => e.toObject))
                    sb.AppendLine($"| {e.fromScript} | {e.toObject} | {e.fieldType} | {e.source} | {e.fieldName} |");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm}*");

            // Save
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(savePath, sb.ToString());
            AssetDatabase.ImportAsset(savePath);

            return new
            {
                success = true,
                savedTo = savePath,
                objectCount = objList.Count,
                userScriptCount,
                referenceCount = allEdges.Count,
                codeReferenceCount = codeEdges.Count
            };
        }

        private static bool IsUserScript(Type type)
        {
            if (type == null) return false;
            var ns = type.Namespace;
            if (string.IsNullOrEmpty(ns)) return true; // no namespace = user script
            return !ns.StartsWith("UnityEngine") && !ns.StartsWith("Unity.") &&
                   !ns.StartsWith("TMPro") && !ns.StartsWith("UnityEditor");
        }

        private static string GetSerializedValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue.ToString();
                case SerializedPropertyType.Float: return prop.floatValue.ToString("G4");
                case SerializedPropertyType.Boolean: return prop.boolValue.ToString();
                case SerializedPropertyType.String:
                    var s = prop.stringValue;
                    return s != null && s.Length > 60 ? s.Substring(0, 57) + "..." : (s ?? "");
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2: return FormatVec(prop.vector2Value);
                case SerializedPropertyType.Vector3: return FormatVec(prop.vector3Value);
                case SerializedPropertyType.Vector4: return FormatVec(prop.vector4Value);
                case SerializedPropertyType.Color: var c = prop.colorValue; return $"({c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2})";
                case SerializedPropertyType.LayerMask: return prop.intValue.ToString();
                default:
                    return prop.isArray ? $"{prop.arrayElementType}[{prop.arraySize}]" : prop.propertyType.ToString();
            }
        }

        // Regex patterns for C# code-level dependency detection
        private static readonly System.Text.RegularExpressions.Regex RxGetComponent =
            new System.Text.RegularExpressions.Regex(@"GetComponent(?:InChildren|InParent|s)?<(\w+)>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxFindObject =
            new System.Text.RegularExpressions.Regex(@"FindObject(?:OfType|sOfType|sByType)?<(\w+)>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxSendMessage =
            new System.Text.RegularExpressions.Regex(@"(?:SendMessage|BroadcastMessage)\s*\(\s*""(\w+)""", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxFieldRef =
            new System.Text.RegularExpressions.Regex(@"(?:public|private|protected|\[SerializeField\])\s+(\w+)\s+\w+\s*[;=]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxSingleton =
            new System.Text.RegularExpressions.Regex(@"([A-Z]\w+)\s*\.\s*[Ii]nstance\b", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxStaticAccess =
            new System.Text.RegularExpressions.Regex(@"([A-Z]\w+)\s*\.\s*[A-Z]\w*\s*[\(;,\)]", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxNewInstance =
            new System.Text.RegularExpressions.Regex(@"new\s+(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxGenericArg =
            new System.Text.RegularExpressions.Regex(@"<(\w+)>\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxInheritance =
            new System.Text.RegularExpressions.Regex(@"class\s+(\w+)\s*:\s*([\w\s,]+?)\s*\{", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex RxTypeCheck =
            new System.Text.RegularExpressions.Regex(@"(?:typeof|is|as)\s*[\(<]\s*(\w+)\s*[\)>]?", System.Text.RegularExpressions.RegexOptions.Compiled);
        // Matches strings (group 1) OR comments (group 2/3). Strings are kept, comments replaced.
        private static readonly System.Text.RegularExpressions.Regex RxComment =
            new System.Text.RegularExpressions.Regex(@"(""(?:[^""\\]|\\.)*"")|(/\*[\s\S]*?\*/)|(//.*?$)",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
        private static readonly System.Text.RegularExpressions.Regex RxMethodDecl =
            new System.Text.RegularExpressions.Regex(@"(?:(?:public|private|protected|internal|static|virtual|override|abstract|async)\s+)*(?:void|bool|int|float|string|IEnumerator|object|[A-Z]\w*)\s+([A-Z]\w*)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static List<DependencyEdge> CollectCodeDependencies()
        {
            var edges = new List<DependencyEdge>();
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });

            // Collect all user class names (MonoBehaviour, ScriptableObject, plain classes, etc.)
            var userClassNames = new HashSet<string>();
            var userScriptPaths = new List<(string path, string className)>();
            foreach (var guid in scriptGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null) continue;
                var scriptClass = script.GetClass();
                if (scriptClass != null && IsUserScript(scriptClass))
                {
                    userClassNames.Add(scriptClass.Name);
                    userScriptPaths.Add((path, scriptClass.Name));
                }
            }
            if (userClassNames.Count == 0) return edges;

            foreach (var (path, className) in userScriptPaths)
            {
                string rawSource;
                try { rawSource = File.ReadAllText(path); } catch { continue; }

                // Strip comments to avoid false positives (preserve string literals, keep char offsets)
                var source = RxComment.Replace(rawSource, m => m.Groups[1].Success ? m.Value : new string(' ', m.Length));

                // Build line→method index for method-level granularity
                var methodAtLine = BuildMethodIndex(source);

                void AddEdge(string target, string pattern, string edgeType, int charIndex)
                {
                    if (target != className && userClassNames.Contains(target))
                    {
                        var method = GetMethodAtPos(source, charIndex, methodAtLine);
                        var location = method != null ? $"{className}.{method}" : className;
                        edges.Add(new DependencyEdge { fromObject = className, fromScript = location, fieldName = pattern, fieldType = edgeType, toObject = target, source = "code" });
                    }
                }

                // GetComponent<T> / GetComponentInChildren<T>
                foreach (System.Text.RegularExpressions.Match m in RxGetComponent.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "GetComponent", m.Index);

                // FindObjectOfType<T>
                foreach (System.Text.RegularExpressions.Match m in RxFindObject.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "FindObject", m.Index);

                // SendMessage / BroadcastMessage
                foreach (System.Text.RegularExpressions.Match m in RxSendMessage.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value, "Message", m.Index);

                // Field referencing other user classes
                foreach (System.Text.RegularExpressions.Match m in RxFieldRef.Matches(source))
                    AddEdge(m.Groups[1].Value, $"field:{m.Groups[1].Value}", "FieldReference", m.Index);

                // Singleton access: ClassName.Instance (PascalCase only)
                foreach (System.Text.RegularExpressions.Match m in RxSingleton.Matches(source))
                    AddEdge(m.Groups[1].Value, $"{m.Groups[1].Value}.Instance", "Singleton", m.Index);

                // Static member access: PascalCase.PascalCase (both sides must start uppercase)
                foreach (System.Text.RegularExpressions.Match m in RxStaticAccess.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('(', ';', ',', ')').Trim(), "StaticAccess", m.Index);

                // new ClassName()
                foreach (System.Text.RegularExpressions.Match m in RxNewInstance.Matches(source))
                    AddEdge(m.Groups[1].Value, $"new {m.Groups[1].Value}()", "Instantiation", m.Index);

                // Generic type argument: SomeMethod<ClassName>()
                foreach (System.Text.RegularExpressions.Match m in RxGenericArg.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.TrimEnd('('), "GenericArg", m.Index);

                // Inheritance: class X : BaseClass, IInterface (Matches for multi-class files)
                foreach (System.Text.RegularExpressions.Match inhMatch in RxInheritance.Matches(source))
                {
                    var declaredClass = inhMatch.Groups[1].Value;
                    foreach (var baseType in inhMatch.Groups[2].Value.Split(','))
                    {
                        var trimmed = baseType.Trim();
                        if (trimmed != declaredClass && userClassNames.Contains(trimmed))
                            edges.Add(new DependencyEdge { fromObject = declaredClass, fromScript = declaredClass, fieldName = $"extends:{trimmed}", fieldType = "Inheritance", toObject = trimmed, source = "code" });
                    }
                }

                // typeof(T) / is T / as T
                foreach (System.Text.RegularExpressions.Match m in RxTypeCheck.Matches(source))
                    AddEdge(m.Groups[1].Value, m.Value.Trim(), "TypeCheck", m.Index);
            }

            // Deduplicate
            return edges.GroupBy(e => $"{e.fromScript}→{e.toObject}:{e.fieldName}")
                .Select(g => g.First()).ToList();
        }

        private static List<(int lineStart, string methodName)> BuildMethodIndex(string source)
        {
            var result = new List<(int lineStart, string methodName)>();
            foreach (System.Text.RegularExpressions.Match m in RxMethodDecl.Matches(source))
                result.Add((m.Index, m.Groups[1].Value));
            result.Sort((a, b) => a.lineStart.CompareTo(b.lineStart));
            return result;
        }

        private static string GetMethodAtPos(string source, int charIndex, List<(int lineStart, string methodName)> methods)
        {
            string best = null;
            foreach (var (pos, name) in methods)
            {
                if (pos <= charIndex) best = name;
                else break;
            }
            return best;
        }

        private static List<DependencyEdge> CollectDependencyEdges(GameObject[] allObjects)
        {
            var edges = new List<DependencyEdge>();
            foreach (var go in allObjects)
            {
                var objPath = GameObjectFinder.GetPath(go);
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    bool enter = true;
                    while (prop.NextVisible(enter))
                    {
                        enter = false;
                        if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (prop.objectReferenceValue == null) continue;

                        string refTarget = null;
                        var refObj = prop.objectReferenceValue;
                        if (refObj is GameObject refGo)
                            refTarget = GameObjectFinder.GetPath(refGo);
                        else if (refObj is Component refComp)
                            refTarget = GameObjectFinder.GetPath(refComp.gameObject);
                        if (refTarget == null || refTarget == objPath) continue;

                        edges.Add(new DependencyEdge
                        {
                            fromObject = objPath,
                            fromScript = comp.GetType().Name,
                            fieldName = prop.name,
                            fieldType = refObj.GetType().Name,
                            toObject = refTarget,
                            source = "scene"
                        });
                    }
                }
            }
            return edges;
        }

        [UnitySkill("scene_dependency_analyze", "Analyze object dependency graph and impact of changes. Use ONLY when user explicitly asks about: dependency analysis, impact analysis, what depends on, what references, safe to delete/disable/remove, refactoring impact, reference check")]
        public static object SceneDependencyAnalyze(
            string targetPath = null,
            string savePath = null)
        {
            if (!string.IsNullOrEmpty(savePath) && Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();

            var edges = CollectDependencyEdges(allObjects);

            // Build reverse index: who depends on each object
            var reverseIndex = edges.GroupBy(e => e.toObject)
                .ToDictionary(g => g.Key, g => g.ToList());

            // If targetPath specified, only analyze that subtree
            List<object> analysis;
            if (!string.IsNullOrEmpty(targetPath))
            {
                var targetGo = GameObjectFinder.FindByPath(targetPath);
                if (targetGo == null)
                    return new { success = false, error = $"Target '{targetPath}' not found" };

                // Collect target + all descendants
                var targetPaths = new HashSet<string>();
                var stack = new Stack<Transform>();
                stack.Push(targetGo.transform);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    targetPaths.Add(GameObjectFinder.GetPath(t.gameObject));
                    foreach (Transform child in t) stack.Push(child);
                }

                analysis = BuildAnalysis(targetPaths, reverseIndex, edges);
            }
            else
            {
                // All objects that are depended upon
                var allTargets = new HashSet<string>(reverseIndex.Keys);
                analysis = BuildAnalysis(allTargets, reverseIndex, edges);
            }

            // Build markdown
            var md = BuildDependencyMarkdown(scene.name, targetPath, analysis, edges);

            // Save if requested
            string savedPath = null;
            if (!string.IsNullOrEmpty(savePath))
            {
                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(savePath, md);
                AssetDatabase.ImportAsset(savePath);
                savedPath = savePath;
            }

            return new
            {
                success = true,
                sceneName = scene.name,
                target = targetPath,
                totalReferences = edges.Count,
                objectsAnalyzed = analysis.Count,
                analysis,
                savedTo = savedPath,
                markdown = savedPath == null ? md : null
            };
        }

        private class DependencyEdge
        {
            public string fromObject, fromScript, fieldName, fieldType, toObject, source;
        }

        private static List<object> BuildAnalysis(HashSet<string> targets, Dictionary<string, List<DependencyEdge>> reverseIndex, List<DependencyEdge> allEdges)
        {
            var result = new List<object>();
            foreach (var path in targets.OrderBy(p => p))
            {
                var dependedBy = reverseIndex.ContainsKey(path)
                    ? reverseIndex[path].Select(e => new { source = e.fromObject, script = e.fromScript, field = e.fieldName, fieldType = e.fieldType }).ToList()
                    : null;
                var dependsOn = allEdges.Where(e => e.fromObject == path)
                    .Select(e => new { target = e.toObject, script = e.fromScript, field = e.fieldName, fieldType = e.fieldType }).ToList();

                int incomingCount = dependedBy?.Count ?? 0;
                string risk = incomingCount == 0 ? "safe" : incomingCount <= 2 ? "low" : incomingCount <= 5 ? "medium" : "high";

                result.Add(new
                {
                    path,
                    risk,
                    dependedByCount = incomingCount,
                    dependedBy,
                    dependsOnCount = dependsOn.Count,
                    dependsOn = dependsOn.Count > 0 ? dependsOn : null
                });
            }
            return result;
        }

        private static string BuildDependencyMarkdown(string sceneName, string targetPath, List<object> analysis, List<DependencyEdge> edges)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Dependency Analysis: {sceneName}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(targetPath))
                sb.AppendLine($"> Target: `{targetPath}`");
            sb.AppendLine($"> Total references: {edges.Count} | Objects analyzed: {analysis.Count}");
            sb.AppendLine();

            // High risk objects first
            sb.AppendLine("## Risk Summary");
            sb.AppendLine();
            sb.AppendLine("| Object | Risk | Depended By | Depends On |");
            sb.AppendLine("|--------|------|-------------|------------|");
            foreach (dynamic item in analysis)
            {
                sb.AppendLine($"| `{item.path}` | {item.risk} | {item.dependedByCount} | {item.dependsOnCount} |");
            }
            sb.AppendLine();

            // Detail for objects with incoming dependencies
            var withDeps = analysis.Where(a => ((dynamic)a).dependedByCount > 0).ToList();
            if (withDeps.Count > 0)
            {
                sb.AppendLine("## Dependency Details");
                sb.AppendLine();
                sb.AppendLine("Objects below are referenced by other scripts. **Disabling/deleting them may cause errors.**");
                sb.AppendLine();
                foreach (dynamic item in withDeps)
                {
                    sb.AppendLine($"### `{item.path}` (risk: {item.risk})");
                    sb.AppendLine();
                    sb.AppendLine("**Referenced by:**");
                    if (item.dependedBy != null)
                    {
                        foreach (dynamic dep in item.dependedBy)
                            sb.AppendLine($"- `{dep.source}` → `{dep.script}.{dep.field}` ({dep.fieldType})");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine($"*Generated: {DateTime.Now:yyyy-MM-dd HH:mm}*");
            return sb.ToString();
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

        [UnitySkill("scene_tag_layer_stats", "Get Tag/Layer usage stats and find potential issues (untagged objects, unused layers)")]
        public static object SceneTagLayerStats()
        {
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var tagCounts = new Dictionary<string, int>();
            var layerCounts = new Dictionary<string, int>();
            int untaggedCount = 0;

            foreach (var go in allObjects)
            {
                var tag = go.tag ?? "Untagged";
                if (tag == "Untagged") untaggedCount++;
                tagCounts[tag] = tagCounts.TryGetValue(tag, out var tc) ? tc + 1 : 1;
                var layerName = LayerMask.LayerToName(go.layer);
                if (string.IsNullOrEmpty(layerName)) layerName = $"Layer {go.layer}";
                layerCounts[layerName] = layerCounts.TryGetValue(layerName, out var lc) ? lc + 1 : 1;
            }

            // Find layers with physics interactions that have no objects
            var usedLayers = allObjects.Select(go => go.layer).Distinct().ToArray();
            var emptyLayers = Enumerable.Range(0, 32)
                .Where(i => !string.IsNullOrEmpty(LayerMask.LayerToName(i)) && !usedLayers.Contains(i))
                .Select(i => LayerMask.LayerToName(i)).ToArray();

            return new { success = true, totalObjects = allObjects.Length, untaggedCount,
                tags = tagCounts.OrderByDescending(kv => kv.Value).Select(kv => new { tag = kv.Key, count = kv.Value }).ToArray(),
                layers = layerCounts.OrderByDescending(kv => kv.Value).Select(kv => new { layer = kv.Key, count = kv.Value }).ToArray(),
                emptyDefinedLayers = emptyLayers };
        }

        [UnitySkill("scene_performance_hints", "Diagnose scene performance issues with prioritized actionable suggestions")]
        public static object ScenePerformanceHints()
        {
            var hints = new List<object>();
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();

            // 1. Realtime shadow lights
            var lights = UnityEngine.Object.FindObjectsOfType<Light>();
            var shadowLights = lights.Where(l => l.shadows != LightShadows.None).ToArray();
            if (shadowLights.Length > 4)
                hints.Add(new { priority = 1, category = "Lighting", issue = $"{shadowLights.Length} shadow-casting lights",
                    suggestion = "Reduce to ≤4 or use baked lighting", fixSkill = "light_set_properties" });

            // 2. Non-static renderers
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>();
            int nonStaticCount = renderers.Count(r => !r.gameObject.isStatic);
            if (nonStaticCount > 100)
                hints.Add(new { priority = 2, category = "Batching", issue = $"{nonStaticCount} non-static renderers",
                    suggestion = "Mark static objects with optimize_set_static_flags", fixSkill = "optimize_set_static_flags" });

            // 3. High-poly meshes without LOD
            var meshFilters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
            var highPoly = meshFilters.Where(mf => mf.sharedMesh != null && mf.sharedMesh.triangles.Length / 3 > 10000
                && mf.GetComponent<LODGroup>() == null).ToArray();
            if (highPoly.Length > 0)
                hints.Add(new { priority = 2, category = "Geometry", issue = $"{highPoly.Length} high-poly meshes (>10k tris) without LOD",
                    suggestion = "Add LOD groups", fixSkill = "optimize_set_lod_group" });

            // 4. Duplicate materials
            var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
            var uniqueShaders = mats.Select(m => m.shader?.name).Distinct().Count();
            var duplicateCount = mats.Length - mats.Select(m => m.GetInstanceID()).Distinct().Count();
            if (duplicateCount > 10)
                hints.Add(new { priority = 3, category = "Materials", issue = $"{duplicateCount} duplicate material references",
                    suggestion = "Consolidate materials", fixSkill = "optimize_find_duplicate_materials" });

            // 5. Particle systems
            var particles = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            if (particles.Length > 20)
                hints.Add(new { priority = 3, category = "Particles", issue = $"{particles.Length} particle systems",
                    suggestion = "Consider reducing or pooling particle systems", fixSkill = (string)null });

            if (hints.Count == 0)
                hints.Add(new { priority = 0, category = "OK", issue = "No obvious performance issues",
                    suggestion = "Scene looks good", fixSkill = (string)null });

            return new { success = true, hintCount = hints.Count, hints };
        }
    }
}
