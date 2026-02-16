using UnityEngine;
using UnityEditor;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Object = UnityEngine.Object;

#if CINEMACHINE_3
using Unity.Cinemachine;
#elif CINEMACHINE_2
using Cinemachine;
#endif

#if HAS_SPLINES
using UnityEngine.Splines;
#endif

namespace UnitySkills
{
    /// <summary>
    /// Cinemachine skills - 支持 Cinemachine 2.x 和 3.x
    /// </summary>
    public static class CinemachineSkills
    {
#if !CINEMACHINE_2 && !CINEMACHINE_3
        private static object NoCinemachine() => new { error = "Cinemachine 未安装。请通过 Package Manager 安装 Cinemachine 2.x 或 3.x" };
#endif
        [UnitySkill("cinemachine_create_vcam", "Create a new Virtual Camera")]
        public static object CinemachineCreateVCam(string name, string folder = "Assets/Settings")
        {
#if CINEMACHINE_3
            var go = new GameObject(name);
            var vcam = go.AddComponent<CinemachineCamera>();
            vcam.Priority = 10;
#elif CINEMACHINE_2
            var go = new GameObject(name);
            var vcam = go.AddComponent<CinemachineVirtualCamera>();
            vcam.m_Priority = 10;
#else
            return NoCinemachine();
#endif

#if CINEMACHINE_2 || CINEMACHINE_3
            Undo.RegisterCreatedObjectUndo(go, "Create Virtual Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            // 确保 CinemachineBrain 存在
            var mainCamera = Camera.main;
            if (mainCamera != null)
            {
                var brain = mainCamera.GetComponent<CinemachineBrain>();
                if (brain == null)
                {
                    var brainComp = Undo.AddComponent<CinemachineBrain>(mainCamera.gameObject);
                    WorkflowManager.SnapshotCreatedComponent(brainComp);
                }
            }

            return new { success = true, gameObjectName = go.name, instanceId = go.GetInstanceID() };
#endif
        }

        [UnitySkill("cinemachine_inspect_vcam", "Deeply inspect a VCam, returning fields and tooltips.")]
        public static object CinemachineInspectVCam(string objectName = null, int instanceId = 0, string path = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(objectName, instanceId, path);
            if (err != null) return err;

#if CINEMACHINE_3
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            var followName = vcam.Follow ? vcam.Follow.name : "None";
            var lookAtName = vcam.LookAt ? vcam.LookAt.name : "None";
            var priority = vcam.Priority;
            var lens = InspectComponent(vcam.Lens);
#elif CINEMACHINE_2
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };

            var followName = vcam.m_Follow ? vcam.m_Follow.name : "None";
            var lookAtName = vcam.m_LookAt ? vcam.m_LookAt.name : "None";
            var priority = vcam.m_Priority;
            var lens = InspectComponent(vcam.m_Lens);
#endif

            // Helper to scrape a component
            object InspectComponent(object component)
            {
                if (component == null) return null;
                return Sanitize(component);
            }

            var components = go.GetComponents<MonoBehaviour>()
                               .Where(mb => mb != null && mb.GetType().Namespace != null && mb.GetType().Namespace.Contains("Cinemachine"))
                               .Select(mb => InspectComponent(mb))
                               .ToList();

            return new
            {
                name = vcam.name,
                priority = priority,
                follow = followName,
                lookAt = lookAtName,
                lens = lens,
                components = components
            };
#endif
        }

        // --- Custom Sanitizer to break Loops ---
#if CINEMACHINE_2 || CINEMACHINE_3
        private static object Sanitize(object obj, int depth = 0, HashSet<int> visited = null)
        {
            if (obj == null) return null;
            if (depth > 5) return obj.ToString();

            var t = obj.GetType();
            if (t.IsPrimitive || t == typeof(string) || t == typeof(bool) || t.IsEnum) return obj;

            // Handle Unity Structs manually
            if (obj is Vector2 v2) return new { v2.x, v2.y };
            if (obj is Vector3 v3) return new { v3.x, v3.y, v3.z };
            if (obj is Vector4 v4) return new { v4.x, v4.y, v4.z, v4.w };
            if (obj is Quaternion q) return new { q.x, q.y, q.z, q.w };
            if (obj is Color c) return new { c.r, c.g, c.b, c.a };
            if (obj is Rect r) return new { r.x, r.y, r.width, r.height };

            // Cycle detection for reference types
            if (!t.IsValueType)
            {
                if (visited == null) visited = new HashSet<int>();
                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
                if (!visited.Add(id)) return $"[Circular: {t.Name}]";
            }

            // Handle Dictionaries (before IEnumerable to preserve key-value structure)
            if (obj is System.Collections.IDictionary dict)
            {
                var dictResult = new Dictionary<string, object>();
                foreach (System.Collections.DictionaryEntry entry in dict)
                    dictResult[entry.Key.ToString()] = Sanitize(entry.Value, depth + 1, visited);
                return dictResult;
            }

            // Handle Arrays/Lists
            if (obj is System.Collections.IEnumerable list)
            {
                var result = new List<object>();
                foreach(var item in list) result.Add(Sanitize(item, depth + 1, visited));
                return result;
            }

            // Deep Sanitization for complex Structs/Classes
            var memberDict = new Dictionary<string, object>();
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

            foreach (var member in members)
            {
                 if (member.GetCustomAttribute<System.ObsoleteAttribute>() != null) continue;
                 if (member.Name == "normalized" || member.Name == "magnitude" || member.Name == "sqrMagnitude") continue;

                 try
                 {
                     object val = null;
                     if (member is FieldInfo f) val = f.GetValue(obj);
                     else if (member is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0) val = p.GetValue(obj);

                     if (val != null)
                     {
                         memberDict[member.Name] = Sanitize(val, depth + 1, visited);
                     }
                 }
                 catch { /* Reflection read failed — skip member */ }
            }
            if (depth == 0) memberDict["_type"] = t.Name;

            return memberDict;
        }
#endif

        [UnitySkill("cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components.")]
        public static object CinemachineSetVCamProperty(string vcamName = null, int instanceId = 0, string path = null, string componentType = null, string propertyName = null, object value = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

#if CINEMACHINE_3
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };
#elif CINEMACHINE_2
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };
#endif
            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            object target = null;
            bool isLens = false;

#if CINEMACHINE_3
            if (string.IsNullOrEmpty(componentType) ||
                componentType.Equals("Main", System.StringComparison.OrdinalIgnoreCase) ||
                componentType.Equals("CinemachineCamera", System.StringComparison.OrdinalIgnoreCase))
#elif CINEMACHINE_2
            if (string.IsNullOrEmpty(componentType) ||
                componentType.Equals("Main", System.StringComparison.OrdinalIgnoreCase) ||
                componentType.Equals("CinemachineVirtualCamera", System.StringComparison.OrdinalIgnoreCase))
#endif
            {
                target = vcam;
            }
            else if (componentType.Equals("Lens", System.StringComparison.OrdinalIgnoreCase))
            {
#if CINEMACHINE_3
                target = vcam.Lens;
#elif CINEMACHINE_2
                target = vcam.m_Lens;
#endif
                isLens = true;
            }
            else
            {
                var comps = go.GetComponents<MonoBehaviour>();
                target = comps.FirstOrDefault(c => c.GetType().Name.Equals(componentType, System.StringComparison.OrdinalIgnoreCase));

                if (target == null && !componentType.StartsWith("Cinemachine"))
                {
                    target = comps.FirstOrDefault(c => c.GetType().Name.Equals("Cinemachine" + componentType, System.StringComparison.OrdinalIgnoreCase));
                }
            }

            if (target == null) return new { error = "Component " + componentType + " not found on Object." };

            if (isLens)
            {
#if CINEMACHINE_3
                object boxedLens = vcam.Lens;
                if (SetFieldOrProperty(boxedLens, propertyName, value))
                {
                   vcam.Lens = (LensSettings)boxedLens;
                   return new { success = true, message = "Set Lens." + propertyName + " to " + value };
                }
#elif CINEMACHINE_2
                object boxedLens = vcam.m_Lens;
                if (SetFieldOrProperty(boxedLens, propertyName, value))
                {
                   vcam.m_Lens = (LensSettings)boxedLens;
                   return new { success = true, message = "Set Lens." + propertyName + " to " + value };
                }
#endif
                return new { error = "Property " + propertyName + " not found on Lens" };
            }

            if (SetFieldOrProperty(target, propertyName, value))
            {
                if (target is Object unityObj) EditorUtility.SetDirty(unityObj);
                return new { success = true, message = "Set " + target.GetType().Name + "." + propertyName + " to " + value };
            }

            return new { error = "Property " + propertyName + " not found on " + target.GetType().Name };
#endif
        }

        [UnitySkill("cinemachine_set_targets", "Set Follow and LookAt targets.")]
        public static object CinemachineSetTargets(string vcamName = null, int instanceId = 0, string path = null, string followName = null, string lookAtName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

#if CINEMACHINE_3
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            Undo.RecordObject(vcam, "Set Targets");
            if (followName != null)
                vcam.Follow = GameObjectFinder.Find(followName)?.transform;
            if (lookAtName != null)
                vcam.LookAt = GameObjectFinder.Find(lookAtName)?.transform;
#elif CINEMACHINE_2
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };

            Undo.RecordObject(vcam, "Set Targets");
            if (followName != null)
                vcam.m_Follow = GameObjectFinder.Find(followName)?.transform;
            if (lookAtName != null)
                vcam.m_LookAt = GameObjectFinder.Find(lookAtName)?.transform;
#endif

            EditorUtility.SetDirty(go);
            return new { success = true };
#endif
        }

        [UnitySkill("cinemachine_add_component", "Add a Cinemachine component (e.g., OrbitalFollow).")]
        public static object CinemachineAddComponent(string vcamName = null, int instanceId = 0, string path = null, string componentType = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            var type = FindCinemachineType(componentType);
            if (type == null) return new { error = "Could not find Cinemachine component type: " + componentType };

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            var comp = Undo.AddComponent(go, type);
            if (comp != null)
            {
                WorkflowManager.SnapshotCreatedComponent(comp);
                return new { success = true, message = "Added " + type.Name + " to " + go.name };
            }
            return new { error = "Failed to add component." };
#endif
        }

        // --- NEW SKILLS (v1.5/CM3) ---

        [UnitySkill("cinemachine_set_lens", "Quickly configure Lens settings (FOV, Near, Far, OrthoSize).")]
        public static object CinemachineSetLens(string vcamName = null, int instanceId = 0, string path = null, float? fov = null, float? nearClip = null, float? farClip = null, float? orthoSize = null, string mode = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

#if CINEMACHINE_3
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            var lens = vcam.Lens;
            bool changed = false;

            if (fov.HasValue) { lens.FieldOfView = fov.Value; changed = true; }
            if (nearClip.HasValue) { lens.NearClipPlane = nearClip.Value; changed = true; }
            if (farClip.HasValue) { lens.FarClipPlane = farClip.Value; changed = true; }
            if (orthoSize.HasValue) { lens.OrthographicSize = orthoSize.Value; changed = true; }

            if (changed)
            {
                vcam.Lens = lens;
                EditorUtility.SetDirty(go);
                return new { success = true, message = "Updated Lens settings" };
            }
#elif CINEMACHINE_2
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };

            var lens = vcam.m_Lens;
            bool changed = false;

            if (fov.HasValue) { lens.FieldOfView = fov.Value; changed = true; }
            if (nearClip.HasValue) { lens.NearClipPlane = nearClip.Value; changed = true; }
            if (farClip.HasValue) { lens.FarClipPlane = farClip.Value; changed = true; }
            if (orthoSize.HasValue) { lens.OrthographicSize = orthoSize.Value; changed = true; }

            if (changed)
            {
                vcam.m_Lens = lens;
                EditorUtility.SetDirty(go);
                return new { success = true, message = "Updated Lens settings" };
            }
#endif

            return new { error = "No values provided to update." };
#endif
        }

        [UnitySkill("cinemachine_list_components", "List all available Cinemachine component names.")]
        public static object CinemachineListComponents()
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
#if CINEMACHINE_3
            var cmAssembly = typeof(CinemachineCamera).Assembly;
#elif CINEMACHINE_2
            var cmAssembly = typeof(CinemachineVirtualCamera).Assembly;
#endif
            System.Type[] assemblyTypes;
            try { assemblyTypes = cmAssembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { assemblyTypes = ex.Types.Where(t => t != null).ToArray(); }

            var componentTypes = assemblyTypes
                .Where(t => t.IsSubclassOf(typeof(MonoBehaviour)) && !t.IsAbstract && t.IsPublic)
                .Select(t => t.Name)
                .Where(n => n.StartsWith("Cinemachine"))
                .OrderBy(n => n)
                .ToList();

            return new { success = true, count = componentTypes.Count, components = componentTypes };
#endif
        }

        [UnitySkill("cinemachine_set_component", "Switch VCam pipeline component (Body/Aim/Noise). CM3 only.")]
        public static object CinemachineSetComponent(string vcamName = null, int instanceId = 0, string path = null, string stage = null, string componentType = null)
        {
#if CINEMACHINE_3
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            if (!System.Enum.TryParse<CinemachineCore.Stage>(stage, true, out var stageEnum))
            {
                return new { error = "Invalid stage. Use Body, Aim, or Noise." };
            }

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            // 1. Remove existing component at this stage
            var existing = vcam.GetCinemachineComponent(stageEnum);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }

            // 2. Add new component if not "None"
            if (!string.IsNullOrEmpty(componentType) && !componentType.Equals("None", System.StringComparison.OrdinalIgnoreCase))
            {
                var type = FindCinemachineType(componentType);
                if (type == null) return new { error = "Could not find Cinemachine component type: " + componentType };

                var comp = Undo.AddComponent(go, type);
                if (comp == null) return new { error = "Failed to add component " + type.Name };
                WorkflowManager.SnapshotCreatedComponent(comp);
            }

            EditorUtility.SetDirty(go);
            return new { success = true, message = "Set " + stage + " to " + (componentType ?? "None") };
#elif CINEMACHINE_2
            return new { error = "cinemachine_set_component 仅支持 Cinemachine 3.x。CM2 请使用 cinemachine_add_component 添加组件。" };
#else
            return NoCinemachine();
#endif
        }

        [UnitySkill("cinemachine_impulse_generate", "Trigger an Impulse. Params: {velocity: {x,y,z}} or empty.")]
        public static object CinemachineImpulseGenerate(string sourceParams)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
             var sources = FindAllObjects<CinemachineImpulseSource>();
             if (sources.Length == 0) return new { success = false, error = "No CinemachineImpulseSource found in scene." };

             var source = sources[0];
             Vector3 velocity = Vector3.down;

             if (!string.IsNullOrEmpty(sourceParams))
             {
                 try
                 {
                     var json = JObject.Parse(sourceParams);
                     if (json["velocity"] != null)
                     {
                         var v = json["velocity"];
                         velocity = new Vector3((float)v["x"], (float)v["y"], (float)v["z"]);
                     }
                 }
                 catch (System.Exception ex) { UnityEngine.Debug.LogWarning($"[UnitySkills] Failed to parse impulse params: {ex.Message}"); }
             }

             source.GenerateImpulse(velocity);
             return new { success = true, message = "Generated Impulse from " + source.name + " with velocity " + velocity };
#endif
        }
        
        [UnitySkill("cinemachine_get_brain_info", "Get info about the Active Camera and Blend.")]
        public static object CinemachineGetBrainInfo()
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var mainCamera = Camera.main;
            if (mainCamera == null) return new { error = "No Main Camera" };
            var brain = mainCamera.GetComponent<CinemachineBrain>();
            if (brain == null) return new { error = "No CinemachineBrain on Main Camera" };

            var activeCam = brain.ActiveVirtualCamera as Component;
#if CINEMACHINE_3
            var updateMethod = brain.UpdateMethod.ToString();
#else
            var updateMethod = brain.m_UpdateMethod.ToString();
#endif
            return new {
                success = true,
                activeCamera = activeCam ? activeCam.name : "None",
                isBlending = brain.IsBlending,
                activeBlend = brain.ActiveBlend?.Description ?? "None",
                updateMethod
            };
#endif
        }

        [UnitySkill("cinemachine_set_active", "Force activation of a VCam (SOLO) by setting highest priority.")]
        public static object CinemachineSetActive(string vcamName = null, int instanceId = 0, string path = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

#if CINEMACHINE_3
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            var allCams = FindAllObjects<CinemachineCamera>();
            int maxPrio = 0;
            foreach (var c in allCams) { int p = (int)c.Priority; if (p > maxPrio) maxPrio = p; }

            vcam.Priority = maxPrio + 1;
            EditorUtility.SetDirty(vcam);

            return new { success = true, message = "Set Priority to " + vcam.Priority + " (Highest)" };
#elif CINEMACHINE_2
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };

            var allCams = FindAllObjects<CinemachineVirtualCamera>();
            int maxPrio = 0;
            if (allCams.Length > 0) maxPrio = allCams.Max(c => c.m_Priority);

            vcam.m_Priority = maxPrio + 1;
            EditorUtility.SetDirty(vcam);

            return new { success = true, message = "Set Priority to " + vcam.m_Priority + " (Highest)" };
#endif
#endif
        }

        [UnitySkill("cinemachine_set_noise", "Configure Noise settings (Basic Multi Channel Perlin).")]
        public static object CinemachineSetNoise(string vcamName = null, int instanceId = 0, string path = null, float amplitudeGain = 1f, float frequencyGain = 1f)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            var perlin = go.GetComponent<CinemachineBasicMultiChannelPerlin>();
            if (perlin == null)
            {
                perlin = Undo.AddComponent<CinemachineBasicMultiChannelPerlin>(go);
                WorkflowManager.SnapshotCreatedComponent(perlin);
            }

#if CINEMACHINE_3
            perlin.AmplitudeGain = amplitudeGain;
            perlin.FrequencyGain = frequencyGain;
#elif CINEMACHINE_2
            perlin.m_AmplitudeGain = amplitudeGain;
            perlin.m_FrequencyGain = frequencyGain;
#endif
            EditorUtility.SetDirty(perlin);

            return new { success = true, message = "Set Noise profile." };
#endif
        }

        // --- Helpers ---

        private static void RecordAndSetDirty(Object target, string name)
        {
            Undo.RecordObject(target, name);
            EditorUtility.SetDirty(target);
        }

        private static T[] FindAllObjects<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<T>();
#endif
        }

#if CINEMACHINE_2 || CINEMACHINE_3
        private static bool SetFieldOrProperty(object target, string name, object value)
        {
            if (target == null) return false;

            if (name.Contains("."))
            {
                var parts = name.Split(new[] { '.' }, 2);
                string currentName = parts[0];
                string remainingName = parts[1];

                var type = target.GetType();
                var flags = BindingFlags.Public | BindingFlags.Instance;
                object nestedTarget = null;

                var field = type.GetField(currentName, flags);
                if (field != null) nestedTarget = field.GetValue(target);
                else
                {
                    var prop = type.GetProperty(currentName, flags);
                    if (prop != null && prop.CanRead) nestedTarget = prop.GetValue(target);
                }

                if (nestedTarget == null) return false;

                bool isStruct = nestedTarget.GetType().IsValueType;
                bool success = SetFieldOrProperty(nestedTarget, remainingName, value);

                if (success && (isStruct || field != null))
                {
                    if (field != null) field.SetValue(target, nestedTarget);
                    else if (type.GetProperty(currentName, flags) is PropertyInfo p && p.CanWrite) p.SetValue(target, nestedTarget);
                }
                return success;
            }

            return SetFieldOrPropertySimple(target, name, value);
        }

        private static bool SetFieldOrPropertySimple(object target, string name, object value)
        {
            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            object SafeConvert(object val, System.Type destType)
            {
                if (val == null) return null;
                if (destType.IsAssignableFrom(val.GetType())) return val;

                if ((typeof(Component).IsAssignableFrom(destType) || destType == typeof(GameObject)) && val is string nameStr)
                {
                    var foundGo = GameObject.Find(nameStr);
                    if (foundGo != null)
                    {
                        if (destType == typeof(GameObject)) return foundGo;
                        if (destType == typeof(Transform)) return foundGo.transform;
                        return foundGo.GetComponent(destType);
                    }
                }
                if (destType.IsEnum) { try { return System.Enum.Parse(destType, val.ToString(), true); } catch { /* Enum parse fallthrough */ } }
                try { return JToken.FromObject(val).ToObject(destType); } catch { /* JSON conversion fallthrough */ }
                try { return System.Convert.ChangeType(val, destType); } catch { return null; }
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                try {
                    object safeValue = SafeConvert(value, field.FieldType);
                    if (safeValue != null) { field.SetValue(target, safeValue); return true; }
                } catch (System.Exception ex) { UnityEngine.Debug.LogWarning($"[UnitySkills] Failed to set field '{name}': {ex.Message}"); }
            }

            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try {
                    object safeValue = SafeConvert(value, prop.PropertyType);
                    if (safeValue != null) { prop.SetValue(target, safeValue); return true; }
                } catch (System.Exception ex) { UnityEngine.Debug.LogWarning($"[UnitySkills] Failed to set property '{name}': {ex.Message}"); }
            }
            return false;
        }
#endif

        private static System.Type FindCinemachineType(string name)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return null;
#else
             if (string.IsNullOrEmpty(name)) return null;

#if CINEMACHINE_3
             var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
             {
                 { "OrbitalFollow", "CinemachineOrbitalFollow" },
                 { "Transposer", "CinemachineTransposer" },
                 { "Composer", "CinemachineComposer" },
                 { "PanTilt", "CinemachinePanTilt" },
                 { "SameAsFollow", "CinemachineSameAsFollowTarget" },
                 { "HardLockToTarget", "CinemachineHardLockToTarget" },
                 { "Perlin", "CinemachineBasicMultiChannelPerlin" },
                 { "Impulse", "CinemachineImpulseSource" }
             };
             var cmAssembly = typeof(CinemachineCamera).Assembly;
             string ns = "Unity.Cinemachine.";
#elif CINEMACHINE_2
             var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
             {
                 { "Transposer", "CinemachineTransposer" },
                 { "Composer", "CinemachineComposer" },
                 { "FramingTransposer", "CinemachineFramingTransposer" },
                 { "HardLockToTarget", "CinemachineHardLockToTarget" },
                 { "Perlin", "CinemachineBasicMultiChannelPerlin" },
                 { "Impulse", "CinemachineImpulseSource" },
                 { "POV", "CinemachinePOV" },
                 { "OrbitalTransposer", "CinemachineOrbitalTransposer" }
             };
             var cmAssembly = typeof(CinemachineVirtualCamera).Assembly;
             string ns = "Cinemachine.";
#endif

             if (map.TryGetValue(name, out var fullName)) name = fullName;
             if (!name.StartsWith("Cinemachine")) name = "Cinemachine" + name;

             var type = cmAssembly.GetType(ns + name, false, true);
             if (type == null) type = cmAssembly.GetType(name, false, true);
             return type;
#endif
        }
        [UnitySkill("cinemachine_create_target_group", "Create a CinemachineTargetGroup. Returns name.")]
        public static object CinemachineCreateTargetGroup(string name)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var go = new GameObject(name);
             Undo.RegisterCreatedObjectUndo(go, "Create TargetGroup");
             WorkflowManager.SnapshotObject(go, SnapshotType.Created);
             var group = Undo.AddComponent<CinemachineTargetGroup>(go);
             if (group == null) return new { error = "Failed to add CinemachineTargetGroup component" };
             return new { success = true, name = go.name };
#endif
        }

        [UnitySkill("cinemachine_target_group_add_member", "Add/Update member in TargetGroup. Inputs: groupName, targetName, weight, radius.")]
        public static object CinemachineTargetGroupAddMember(string groupName = null, int groupInstanceId = 0, string groupPath = null, string targetName = null, int targetInstanceId = 0, string targetPath = null, float weight = 1f, float radius = 1f)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var (groupGo, groupErr) = GameObjectFinder.FindOrError(groupName, groupInstanceId, groupPath);
             if (groupErr != null) return groupErr;
             var group = groupGo.GetComponent<CinemachineTargetGroup>();
             if (group == null) return new { error = "GameObject is not a CinemachineTargetGroup" };

             var (targetGo, targetErr) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath);
             if (targetErr != null) return targetErr;

             WorkflowManager.SnapshotObject(groupGo);
             Undo.RecordObject(group, "Add TargetGroup Member");
             group.RemoveMember(targetGo.transform);
             group.AddMember(targetGo.transform, weight, radius);

             return new { success = true, message = $"Added {targetGo.name} to {groupGo.name} (W:{weight}, R:{radius})" };
#endif
        }

        [UnitySkill("cinemachine_target_group_remove_member", "Remove member from TargetGroup. Inputs: groupName, targetName.")]
        public static object CinemachineTargetGroupRemoveMember(string groupName = null, int groupInstanceId = 0, string groupPath = null, string targetName = null, int targetInstanceId = 0, string targetPath = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var (groupGo, groupErr) = GameObjectFinder.FindOrError(groupName, groupInstanceId, groupPath);
             if (groupErr != null) return groupErr;
             var group = groupGo.GetComponent<CinemachineTargetGroup>();
             if (group == null) return new { error = "GameObject is not a CinemachineTargetGroup" };

             var (targetGo, targetErr) = GameObjectFinder.FindOrError(targetName, targetInstanceId, targetPath);
             if (targetErr != null) return targetErr;

             WorkflowManager.SnapshotObject(groupGo);
             Undo.RecordObject(group, "Remove TargetGroup Member");
             group.RemoveMember(targetGo.transform);

             return new { success = true, message = $"Removed {targetGo.name} from {groupGo.name}" };
#endif
        }

        [UnitySkill("cinemachine_set_spline", "Set Spline for VCam Body. CM3 + Splines only. Inputs: vcamName, splineName.")]
        public static object CinemachineSetSpline(string vcamName = null, int vcamInstanceId = 0, string vcamPath = null, string splineName = null, int splineInstanceId = 0, string splinePath = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#elif CINEMACHINE_2
            return new { error = "cinemachine_set_spline 仅支持 Cinemachine 3.x + Splines 包" };
#elif !HAS_SPLINES
            return new { error = "Splines 包未安装。请通过 Package Manager 安装 com.unity.splines" };
#else
            var (vcamGo, vcamErr) = GameObjectFinder.FindOrError(vcamName, vcamInstanceId, vcamPath);
            if (vcamErr != null) return vcamErr;
            var vcam = vcamGo.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            var dolly = vcam.GetCinemachineComponent(CinemachineCore.Stage.Body) as CinemachineSplineDolly;
            if (dolly == null)
            {
                return new { error = "VCam does not have a CinemachineSplineDolly component on Body stage. Use cinemachine_set_component first." };
            }

            var (splineGo, splineErr) = GameObjectFinder.FindOrError(splineName, splineInstanceId, splinePath);
            if (splineErr != null) return splineErr;
            var container = splineGo.GetComponent<SplineContainer>();
            if (container == null) return new { error = "GameObject does not have a SplineContainer" };

            WorkflowManager.SnapshotObject(vcamGo);
            Undo.RecordObject(dolly, "Set Spline");
            dolly.Spline = container;

            return new { success = true, message = $"Assigned Spline {splineGo.name} to VCam {vcamGo.name}" };
#endif
        }
        [UnitySkill("cinemachine_add_extension", "Add a CinemachineExtension. Inputs: vcamName, extensionName (e.g. CinemachineStoryboard).")]
        public static object CinemachineAddExtension(string vcamName = null, int instanceId = 0, string path = null, string extensionName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
             if (err != null) return err;

#if CINEMACHINE_3
             var vcam = go.GetComponent<CinemachineCamera>();
             if (vcam == null) return new { error = "Not a CinemachineCamera" };
#elif CINEMACHINE_2
             var vcam = go.GetComponent<CinemachineVirtualCamera>();
             if (vcam == null) return new { error = "Not a CinemachineVirtualCamera" };
#endif

             var type = FindCinemachineType(extensionName);
             if (type == null) return new { error = "Could not find Cinemachine extension type: " + extensionName };
             if (!typeof(CinemachineExtension).IsAssignableFrom(type)) return new { error = type.Name + " is not a CinemachineExtension" };

             if (go.GetComponent(type) != null) return new { success = true, message = "Extension " + type.Name + " already exists on " + go.name };

             WorkflowManager.SnapshotObject(go);
             var ext = Undo.AddComponent(go, type);
             if (ext == null) return new { error = "Failed to add extension " + type.Name };
             WorkflowManager.SnapshotCreatedComponent(ext);
             return new { success = true, message = "Added extension " + type.Name };
#endif
        }

        [UnitySkill("cinemachine_remove_extension", "Remove a CinemachineExtension. Inputs: vcamName, extensionName.")]
        public static object CinemachineRemoveExtension(string vcamName = null, int instanceId = 0, string path = null, string extensionName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
             if (err != null) return err;

             var type = FindCinemachineType(extensionName);
             if (type == null) return new { error = "Could not find Cinemachine extension type: " + extensionName };

             var ext = go.GetComponent(type);
             if (ext == null) return new { error = "Extension " + type.Name + " not found on " + go.name };

             WorkflowManager.SnapshotObject(go);
             Undo.DestroyObjectImmediate(ext);
             return new { success = true, message = "Removed extension " + type.Name };
#endif
        }

        [UnitySkill("cinemachine_create_mixing_camera", "Create a Cinemachine Mixing Camera.")]
        public static object CinemachineCreateMixingCamera(string name)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Mixing Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            var cam = Undo.AddComponent<CinemachineMixingCamera>(go);
            if (cam == null) return new { error = "Failed to add CinemachineMixingCamera component" };
            return new { success = true, name = name };
#endif
        }

        [UnitySkill("cinemachine_mixing_camera_set_weight", "Set weight of a child camera in a Mixing Camera. Inputs: mixerName, childName, weight.")]
        public static object CinemachineMixingCameraSetWeight(string mixerName = null, int mixerInstanceId = 0, string mixerPath = null, string childName = null, int childInstanceId = 0, string childPath = null, float weight = 1f)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (mixerGo, mixerErr) = GameObjectFinder.FindOrError(mixerName, mixerInstanceId, mixerPath);
            if (mixerErr != null) return mixerErr;
            var mixer = mixerGo.GetComponent<CinemachineMixingCamera>();
            if (mixer == null) return new { error = "Not a CinemachineMixingCamera" };

            var (childGo, childErr) = GameObjectFinder.FindOrError(childName, childInstanceId, childPath);
            if (childErr != null) return childErr;
            var childVcam = childGo.GetComponent<CinemachineVirtualCameraBase>();
            if (childVcam == null) return new { error = "Child is not a Cinemachine Virtual Camera" };

            WorkflowManager.SnapshotObject(mixerGo);
            Undo.RecordObject(mixer, "Set Mixing Weight");
            mixer.SetWeight(childVcam, weight);
            EditorUtility.SetDirty(mixer);

            return new { success = true, message = $"Set weight of {childGo.name} to {weight} in {mixerGo.name}" };
#endif
        }

        [UnitySkill("cinemachine_create_clear_shot", "Create a Cinemachine Clear Shot Camera.")]
        public static object CinemachineCreateClearShot(string name)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Clear Shot");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            var cam = Undo.AddComponent<CinemachineClearShot>(go);
            if (cam == null) return new { error = "Failed to add CinemachineClearShot component" };
            return new { success = true, name = name };
#endif
        }

        [UnitySkill("cinemachine_create_state_driven_camera", "Create a Cinemachine State Driven Camera. Optional: targetAnimatorName.")]
        public static object CinemachineCreateStateDrivenCamera(string name, string targetAnimatorName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create State Driven Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            var cam = Undo.AddComponent<CinemachineStateDrivenCamera>(go);
            if (cam == null) return new { error = "Failed to add CinemachineStateDrivenCamera component" };

            if (!string.IsNullOrEmpty(targetAnimatorName))
            {
                var animatorGo = GameObjectFinder.Find(targetAnimatorName);
                if (animatorGo != null)
                {
                    var animator = animatorGo.GetComponent<Animator>();
                    if (animator != null)
                    {
                        Undo.RecordObject(cam, "Set Animated Target");
#if CINEMACHINE_3
                        cam.AnimatedTarget = animator;
#elif CINEMACHINE_2
                        cam.m_AnimatedTarget = animator;
#endif
                    }
                }
            }
            return new { success = true, name = name };
#endif
        }

        [UnitySkill("cinemachine_state_driven_camera_add_instruction", "Add instruction to State Driven Camera. Inputs: cameraName, stateName, childCameraName, minDuration, activateAfter.")]
        public static object CinemachineStateDrivenCameraAddInstruction(string cameraName = null, int cameraInstanceId = 0, string cameraPath = null, string stateName = null, string childCameraName = null, int childInstanceId = 0, string childPath = null, float minDuration = 0, float activateAfter = 0)
        {
#if CINEMACHINE_3
            var (go, err) = GameObjectFinder.FindOrError(cameraName, cameraInstanceId, cameraPath);
            if (err != null) return err;
            var stateCam = go.GetComponent<CinemachineStateDrivenCamera>();
            if (stateCam == null) return new { error = "Not a CinemachineStateDrivenCamera" };

            var (childGo, childErr) = GameObjectFinder.FindOrError(childCameraName, childInstanceId, childPath);
            if (childErr != null) return childErr;
            var childVcam = childGo.GetComponent<CinemachineVirtualCameraBase>();
            if (childVcam == null) return new { error = "Child is not a Cinemachine Virtual Camera" };

            int hash = Animator.StringToHash(stateName);

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(stateCam, "Add Instruction");

            var list = new List<CinemachineStateDrivenCamera.Instruction>();
            if (stateCam.Instructions != null) list.AddRange(stateCam.Instructions);

            var newInstr = new CinemachineStateDrivenCamera.Instruction
            {
                FullHash = hash,
                Camera = childVcam,
                MinDuration = minDuration,
                ActivateAfter = activateAfter
            };
            list.Add(newInstr);

            stateCam.Instructions = list.ToArray();
            EditorUtility.SetDirty(stateCam);

            return new { success = true, message = $"Added instruction: {stateName} -> {childGo.name}" };
#elif CINEMACHINE_2
            var (go, err) = GameObjectFinder.FindOrError(cameraName, cameraInstanceId, cameraPath);
            if (err != null) return err;
            var stateCam = go.GetComponent<CinemachineStateDrivenCamera>();
            if (stateCam == null) return new { error = "Not a CinemachineStateDrivenCamera" };

            var (childGo, childErr) = GameObjectFinder.FindOrError(childCameraName, childInstanceId, childPath);
            if (childErr != null) return childErr;
            var childVcam = childGo.GetComponent<CinemachineVirtualCameraBase>();
            if (childVcam == null) return new { error = "Child is not a Cinemachine Virtual Camera" };

            int hash = Animator.StringToHash(stateName);

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(stateCam, "Add Instruction");

            var list = new List<CinemachineStateDrivenCamera.Instruction>();
            if (stateCam.m_Instructions != null) list.AddRange(stateCam.m_Instructions);

            var newInstr = new CinemachineStateDrivenCamera.Instruction
            {
                m_FullHash = hash,
                m_VirtualCamera = childVcam,
                m_MinDuration = minDuration,
                m_ActivateAfter = activateAfter
            };
            list.Add(newInstr);

            stateCam.m_Instructions = list.ToArray();
            EditorUtility.SetDirty(stateCam);

            return new { success = true, message = $"Added instruction: {stateName} -> {childCameraName}" };
#else
            return NoCinemachine();
#endif
        }
    }
}
