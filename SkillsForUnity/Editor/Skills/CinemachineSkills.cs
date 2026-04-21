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
        [UnitySkill("cinemachine_create_vcam", "Create a new Virtual Camera",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "virtual", "cinemachine", "vcam" },
            Outputs = new[] { "gameObjectName", "instanceId" })]
        public static object CinemachineCreateVCam(string name, string folder = "Assets/Settings")
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = new GameObject(name);
            var vcam = go.AddComponent(CinemachineAdapter.FindCinemachineType(CinemachineAdapter.VCamTypeName)) as MonoBehaviour;
            CinemachineAdapter.SetPriority(vcam, 10);

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

        [UnitySkill("cinemachine_inspect_vcam", "Deeply inspect a VCam, returning fields and tooltips.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Query,
            Tags = new[] { "camera", "inspect", "vcam", "cinemachine" },
            Outputs = new[] { "name", "priority", "follow", "lookAt", "lens", "components" },
            RequiresInput = new[] { "vcam" },
            ReadOnly = true)]
        public static object CinemachineInspectVCam(string vcamName = null, int instanceId = 0, string path = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(name: vcamName, instanceId: instanceId, path: path);
            if (err != null) return err;

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

            var followName = CinemachineAdapter.GetFollow(vcam) ? CinemachineAdapter.GetFollow(vcam).name : "None";
            var lookAtName = CinemachineAdapter.GetLookAt(vcam) ? CinemachineAdapter.GetLookAt(vcam).name : "None";
            var priority = CinemachineAdapter.GetPriority(vcam);
            var lens = Sanitize(CinemachineAdapter.GetLens(vcam));

            var components = go.GetComponents<MonoBehaviour>()
                               .Where(mb => mb != null && mb.GetType().Namespace != null && mb.GetType().Namespace.Contains("Cinemachine"))
                               .Select(mb => InspectCmComponent(mb))
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

#if CINEMACHINE_2 || CINEMACHINE_3
        private static object InspectCmComponent(MonoBehaviour mb)
        {
            var t = mb.GetType();
            var result = new Dictionary<string, object>();
            result["_type"] = t.Name;

            // Collect serialized fields (what appears in Inspector)
            var serialized = new Dictionary<string, object>();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var field in t.GetFields(flags))
            {
                try { if (field.GetCustomAttribute<System.ObsoleteAttribute>() != null) continue; } catch { continue; }
                // Include: public fields with m_ prefix, [SerializeField], [Tooltip], or simple value types
                bool isInspector = field.Name.StartsWith("m_")
                    || field.GetCustomAttribute<SerializeField>() != null
                    || field.GetCustomAttribute<TooltipAttribute>() != null
                    || field.FieldType.IsValueType
                    || field.FieldType == typeof(string)
                    || typeof(Object).IsAssignableFrom(field.FieldType);

                if (!isInspector) continue;
                // Skip internal/runtime fields
                if (field.Name == "destroyCancellationToken" || field.Name == "useGUILayout"
                    || field.Name == "runInEditMode" || field.Name == "enabled") continue;

                try
                {
                    var val = field.GetValue(mb);
                    serialized[field.Name] = SanitizeShallow(val, field.FieldType);
                }
                catch { /* skip */ }
            }

            result["settings"] = serialized;

            // Stage detection
            var body = CinemachineAdapter.GetPipelineComponent(mb.gameObject, "Body");
            var aim = CinemachineAdapter.GetPipelineComponent(mb.gameObject, "Aim");
            if (mb == body) result["stage"] = "Body";
            else if (mb == aim) result["stage"] = "Aim";
            else if (t.Name.Contains("Perlin")) result["stage"] = "Noise";
            else if (typeof(CinemachineExtension).IsAssignableFrom(t)) result["stage"] = "Extension";

            return result;
        }

        private static object SanitizeShallow(object val, System.Type declaredType, int depth = 0)
        {
            if (val == null) return null;
            if (depth > 3) return val.ToString();

            var t = val.GetType();
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return val;
            if (val is Vector2 v2) return new { v2.x, v2.y };
            if (val is Vector3 v3) return new { v3.x, v3.y, v3.z };
            if (val is Vector4 v4) return new { v4.x, v4.y, v4.z, v4.w };
            if (val is Quaternion q) return new { q.x, q.y, q.z, q.w };
            if (val is Color c) return new { c.r, c.g, c.b, c.a };
            if (val is Object uo) return uo != null ? uo.name : "None";

            // For structs, recurse one level
            if (t.IsValueType && !t.IsPrimitive)
            {
                var dict = new Dictionary<string, object>();
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    try { dict[f.Name] = SanitizeShallow(f.GetValue(val), f.FieldType, depth + 1); } catch { }
                }
                return dict;
            }

            return val.ToString();
        }
#endif

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
                 try { if (member.GetCustomAttribute<System.ObsoleteAttribute>() != null) continue; } catch { continue; }
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

        [UnitySkill("cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "property", "vcam", "pipeline", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineSetVCamProperty(
            string vcamName = null,
            int instanceId = 0,
            string path = null,
            string componentType = null,
            string propertyName = null,
            object value = null,
            float? fov = null,
            float? nearClip = null,
            float? farClip = null,
            float? orthoSize = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            if (string.IsNullOrWhiteSpace(propertyName) && value == null &&
                (fov.HasValue || nearClip.HasValue || farClip.HasValue || orthoSize.HasValue))
            {
                return CinemachineSetLens(vcamName, instanceId, path, fov, nearClip, farClip, orthoSize);
            }

            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr2) return vcamErr2;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return new { error = "propertyName is required unless using shorthand lens parameters (fov/nearClip/farClip/orthoSize)." };
            }

            var normalizedComponentType = componentType?.Trim();

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            object target = null;
            bool isLens = false;

            if (string.IsNullOrEmpty(normalizedComponentType) ||
                normalizedComponentType.Equals("Main", System.StringComparison.OrdinalIgnoreCase) ||
                normalizedComponentType.Equals(CinemachineAdapter.VCamTypeName, System.StringComparison.OrdinalIgnoreCase))
            {
                target = vcam;
            }
            else if (normalizedComponentType.Equals("Lens", System.StringComparison.OrdinalIgnoreCase))
            {
                target = CinemachineAdapter.GetLens(vcam);
                isLens = true;
            }
            else
            {
                var comps = go.GetComponents<MonoBehaviour>();
                target = comps.FirstOrDefault(c => c.GetType().Name.Equals(normalizedComponentType, System.StringComparison.OrdinalIgnoreCase));

                if (target == null &&
                    !string.IsNullOrEmpty(normalizedComponentType) &&
                    !normalizedComponentType.StartsWith("Cinemachine", System.StringComparison.OrdinalIgnoreCase))
                {
                    target = comps.FirstOrDefault(c => c.GetType().Name.Equals("Cinemachine" + normalizedComponentType, System.StringComparison.OrdinalIgnoreCase));
                }
            }

            if (target == null) return new { error = "Component " + normalizedComponentType + " not found on Object." };

            if (isLens)
            {
                object boxedLens = CinemachineAdapter.GetLens(vcam);
                if (SetFieldOrProperty(boxedLens, propertyName, value))
                {
                   CinemachineAdapter.SetLens(vcam, (LensSettings)boxedLens);
                   return new { success = true, message = "Set Lens." + propertyName + " to " + value };
                }
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

        [UnitySkill("cinemachine_set_targets", "Set Follow and LookAt targets.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "follow", "lookAt", "target", "cinemachine" },
            Outputs = new[] { "success" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineSetTargets(string vcamName = null, int instanceId = 0, string path = null, string followName = null, string lookAtName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

            Undo.RecordObject(vcam, "Set Targets");
            if (followName != null)
                CinemachineAdapter.SetFollow(vcam, GameObjectFinder.Find(followName)?.transform);
            if (lookAtName != null)
                CinemachineAdapter.SetLookAt(vcam, GameObjectFinder.Find(lookAtName)?.transform);

            EditorUtility.SetDirty(go);
            return new { success = true };
#endif
        }

        [UnitySkill("cinemachine_add_component", "Add a Cinemachine component (e.g., OrbitalFollow).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "camera", "component", "add", "pipeline", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
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

        [UnitySkill("cinemachine_set_lens", "Quickly configure Lens settings (FOV, Near, Far, OrthoSize).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "lens", "fov", "clip", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineSetLens(string vcamName = null, int instanceId = 0, string path = null, float? fov = null, float? nearClip = null, float? farClip = null, float? orthoSize = null, string mode = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

            var lens = CinemachineAdapter.GetLens(vcam);
            bool changed = false;

            if (fov.HasValue) { lens.FieldOfView = fov.Value; changed = true; }
            if (nearClip.HasValue) { lens.NearClipPlane = nearClip.Value; changed = true; }
            if (farClip.HasValue) { lens.FarClipPlane = farClip.Value; changed = true; }
            if (orthoSize.HasValue) { lens.OrthographicSize = orthoSize.Value; changed = true; }

            if (changed)
            {
                CinemachineAdapter.SetLens(vcam, lens);
                EditorUtility.SetDirty(go);
                return new { success = true, message = "Updated Lens settings" };
            }

            return new { error = "No values provided to update." };
#endif
        }

        [UnitySkill("cinemachine_list_components", "List all available Cinemachine component names.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Query,
            Tags = new[] { "cinemachine", "component", "list", "pipeline" },
            Outputs = new[] { "count", "components" },
            ReadOnly = true)]
        public static object CinemachineListComponents()
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var cmAssembly = CinemachineAdapter.CmAssembly;
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

        [UnitySkill("cinemachine_set_component", "Switch VCam pipeline component (Body/Aim/Noise). CM3 only.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify | SkillOperation.Delete | SkillOperation.Create,
            Tags = new[] { "camera", "pipeline", "body", "aim", "noise" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
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

        [UnitySkill("cinemachine_impulse_generate", "Trigger an Impulse. Params: {velocity: {x,y,z}} or empty.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Execute,
            Tags = new[] { "camera", "impulse", "shake", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "impulseSource" })]
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
        
        [UnitySkill("cinemachine_get_brain_info", "Get info about the Active Camera and Blend.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Query,
            Tags = new[] { "camera", "brain", "blend", "active", "cinemachine" },
            Outputs = new[] { "activeCamera", "isBlending", "activeBlend", "updateMethod" },
            ReadOnly = true)]
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
            var updateMethod = CinemachineAdapter.GetBrainUpdateMethod(brain);
            return new {
                success = true,
                activeCamera = activeCam ? activeCam.name : "None",
                isBlending = brain.IsBlending,
                activeBlend = brain.ActiveBlend?.Description ?? "None",
                updateMethod
            };
#endif
        }

        [UnitySkill("cinemachine_set_active", "Force activation of a VCam (SOLO) by setting highest priority.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "active", "priority", "solo", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineSetActive(string vcamName = null, int instanceId = 0, string path = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            // 记录快照用于撤销
            WorkflowManager.SnapshotObject(go);

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

            int maxPrio = CinemachineAdapter.GetMaxPriority();
            CinemachineAdapter.SetPriority(vcam, maxPrio + 1);
            EditorUtility.SetDirty(vcam);

            return new { success = true, message = "Set Priority to " + CinemachineAdapter.GetPriority(vcam) + " (Highest)" };
#endif
        }

        [UnitySkill("cinemachine_set_noise", "Configure Noise settings (Basic Multi Channel Perlin).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "noise", "perlin", "shake", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
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

            CinemachineAdapter.SetNoiseGains(perlin, amplitudeGain, frequencyGain);
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
            return FindHelper.FindAll<T>();
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
                    var foundGo = GameObjectFinder.Find(name: nameStr);
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

#if CINEMACHINE_2 || CINEMACHINE_3
        private static System.Type FindCinemachineType(string name)
        {
            return CinemachineAdapter.FindCinemachineType(name);
        }
#endif
        [UnitySkill("cinemachine_create_target_group", "Create a CinemachineTargetGroup. Returns name.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "targetGroup", "group", "cinemachine" },
            Outputs = new[] { "success", "name" })]
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

        [UnitySkill("cinemachine_target_group_add_member", "Add/Update member in TargetGroup. Inputs: groupName, targetName, weight, radius.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "targetGroup", "member", "add", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "targetGroup", "gameObject" })]
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

        [UnitySkill("cinemachine_target_group_remove_member", "Remove member from TargetGroup. Inputs: groupName, targetName.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify | SkillOperation.Delete,
            Tags = new[] { "camera", "targetGroup", "member", "remove", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "targetGroup", "gameObject" })]
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

        [UnitySkill("cinemachine_set_spline", "Set Spline for VCam Body. CM3 + Splines only. Inputs: vcamName, splineName.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "spline", "dolly", "path", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam", "splineContainer" })]
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
        [UnitySkill("cinemachine_add_extension", "Add a CinemachineExtension. Inputs: vcamName, extensionName (e.g. CinemachineStoryboard).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "camera", "extension", "add", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineAddExtension(string vcamName = null, int instanceId = 0, string path = null, string extensionName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
             return NoCinemachine();
#else
             var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
             if (err != null) return err;

             var vcam = CinemachineAdapter.GetVCam(go);
             if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

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

        [UnitySkill("cinemachine_remove_extension", "Remove a CinemachineExtension. Inputs: vcamName, extensionName.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Delete,
            Tags = new[] { "camera", "extension", "remove", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "vcam" })]
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

        [UnitySkill("cinemachine_create_mixing_camera", "Create a Cinemachine Mixing Camera.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "mixing", "blend", "cinemachine" },
            Outputs = new[] { "success", "name" })]
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

        [UnitySkill("cinemachine_mixing_camera_set_weight", "Set weight of a child camera in a Mixing Camera. Inputs: mixerName, childName, weight.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "mixing", "weight", "blend", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "mixingCamera", "vcam" })]
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

        [UnitySkill("cinemachine_create_clear_shot", "Create a Cinemachine Clear Shot Camera.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "clearShot", "auto", "cinemachine" },
            Outputs = new[] { "success", "name" })]
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

        [UnitySkill("cinemachine_create_state_driven_camera", "Create a Cinemachine State Driven Camera. Optional: targetAnimatorName.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "stateDriven", "animator", "cinemachine" },
            Outputs = new[] { "success", "name" })]
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

        [UnitySkill("cinemachine_state_driven_camera_add_instruction", "Add instruction to State Driven Camera. Inputs: cameraName, stateName, childCameraName, minDuration, activateAfter.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "stateDriven", "instruction", "state", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "stateDrivenCamera", "vcam" })]
        public static object CinemachineStateDrivenCameraAddInstruction(string cameraName = null, int cameraInstanceId = 0, string cameraPath = null, string stateName = null, string childCameraName = null, int childInstanceId = 0, string childPath = null, float minDuration = 0, float activateAfter = 0)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
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

            CinemachineAdapter.AddStateDrivenInstruction(stateCam, hash, childVcam, minDuration, activateAfter);
            EditorUtility.SetDirty(stateCam);

            return new { success = true, message = $"Added instruction: {stateName} -> {childGo.name}" };
#endif
        }

        // ===================== Brain / Priority / Blend =====================

        [UnitySkill("cinemachine_set_brain", "Configure CinemachineBrain: update method, default blend, debug display.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "brain", "blend", "cinemachine", "update" },
            Outputs = new[] { "success", "settings" })]
        public static object CinemachineSetBrain(
            string updateMethod = null,
            string blendUpdateMethod = null,
            string defaultBlendStyle = null,
            float? defaultBlendTime = null,
            bool? showDebugText = null,
            bool? showCameraFrustum = null,
            bool? ignoreTimeScale = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var brain = CinemachineAdapter.FindBrain();
            if (brain == null) return new { error = "No CinemachineBrain found. Add one to the Main Camera first." };

            Undo.RecordObject(brain, "Set Brain");

            if (updateMethod != null)
                CinemachineAdapter.SetBrainUpdateMethod(brain, updateMethod);
            if (blendUpdateMethod != null)
                CinemachineAdapter.SetBrainBlendUpdateMethod(brain, blendUpdateMethod);

            if (defaultBlendStyle != null || defaultBlendTime.HasValue)
            {
                var current = CinemachineAdapter.GetBrainDefaultBlend(brain);
                string style = defaultBlendStyle ?? CinemachineAdapter.GetBlendStyle(current);
                float time = defaultBlendTime ?? CinemachineAdapter.GetBlendTime(current);
                CinemachineAdapter.SetBrainDefaultBlend(brain, CinemachineAdapter.CreateBlendDefinition(style, time));
            }

            if (showDebugText.HasValue) CinemachineAdapter.SetBrainBool(brain, "ShowDebugText", showDebugText.Value);
            if (showCameraFrustum.HasValue) CinemachineAdapter.SetBrainBool(brain, "ShowCameraFrustum", showCameraFrustum.Value);
            if (ignoreTimeScale.HasValue) CinemachineAdapter.SetBrainBool(brain, "IgnoreTimeScale", ignoreTimeScale.Value);

            EditorUtility.SetDirty(brain);

            var blend = CinemachineAdapter.GetBrainDefaultBlend(brain);
            return new
            {
                success = true,
                settings = new
                {
                    updateMethod = CinemachineAdapter.GetBrainUpdateMethod(brain),
                    blendUpdateMethod = CinemachineAdapter.GetBrainBlendUpdateMethod(brain),
                    defaultBlendStyle = CinemachineAdapter.GetBlendStyle(blend),
                    defaultBlendTime = CinemachineAdapter.GetBlendTime(blend),
                    showDebugText = CinemachineAdapter.GetBrainBool(brain, "ShowDebugText"),
                    showCameraFrustum = CinemachineAdapter.GetBrainBool(brain, "ShowCameraFrustum"),
                    ignoreTimeScale = CinemachineAdapter.GetBrainBool(brain, "IgnoreTimeScale")
                }
            };
#endif
        }

        [UnitySkill("cinemachine_set_priority", "Set explicit priority value for a Virtual Camera.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "priority", "cinemachine" },
            Outputs = new[] { "success", "priority" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineSetPriority(
            string vcamName = null, int instanceId = 0, string path = null,
            int priority = 10)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            var vcam = CinemachineAdapter.GetVCam(go);
            if (CinemachineAdapter.VCamOrError(vcam) is object vcamErr) return vcamErr;

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(vcam, "Set Priority");
            CinemachineAdapter.SetPriority(vcam, priority);
            EditorUtility.SetDirty(vcam);

            return new { success = true, name = go.name, priority };
#endif
        }

        [UnitySkill("cinemachine_set_blend", "Set default blend or per-camera-pair blend. Leave fromCamera/toCamera empty for default.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "blend", "transition", "cinemachine" },
            Outputs = new[] { "success", "message" })]
        public static object CinemachineSetBlend(
            string style = "EaseInOut",
            float time = 2f,
            string fromCamera = null,
            string toCamera = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var brain = CinemachineAdapter.FindBrain();
            if (brain == null) return new { error = "No CinemachineBrain found." };

            var blend = CinemachineAdapter.CreateBlendDefinition(style, time);

            if (string.IsNullOrEmpty(fromCamera) && string.IsNullOrEmpty(toCamera))
            {
                Undo.RecordObject(brain, "Set Default Blend");
                CinemachineAdapter.SetBrainDefaultBlend(brain, blend);
                EditorUtility.SetDirty(brain);
                return new { success = true, message = $"Set default blend: {style} {time}s" };
            }

            return new { success = true, message = $"Set default blend: {style} {time}s (per-camera-pair blends require CinemachineBlenderSettings asset — use set_brain + custom blends asset for advanced use)" };
#endif
        }

        // ===================== Sequencer =====================

        [UnitySkill("cinemachine_create_sequencer", "Create a Sequencer camera (CM3) or BlendList camera (CM2) that plays child cameras in sequence.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "sequencer", "blendlist", "sequence", "cinemachine" },
            Outputs = new[] { "gameObjectName", "instanceId" })]
        public static object CinemachineCreateSequencer(string name, bool loop = false)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = new GameObject(name);
            var type = CinemachineAdapter.FindCinemachineType(CinemachineAdapter.SequencerTypeName);
            if (type == null) return new { error = "Could not find Sequencer type: " + CinemachineAdapter.SequencerTypeName };

            var seq = go.AddComponent(type) as MonoBehaviour;
            CinemachineAdapter.SetSequencerLoop(seq, loop);

            // 确保 Brain 存在
            var mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.GetComponent<CinemachineBrain>() == null)
                Undo.AddComponent<CinemachineBrain>(mainCamera.gameObject);

            Undo.RegisterCreatedObjectUndo(go, "Create Sequencer Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, gameObjectName = go.name, instanceId = go.GetInstanceID(), type = CinemachineAdapter.SequencerTypeName, loop };
#endif
        }

        [UnitySkill("cinemachine_sequencer_add_instruction", "Add a child camera instruction to a Sequencer/BlendList camera.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "sequencer", "instruction", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "sequencer" })]
        public static object CinemachineSequencerAddInstruction(
            string sequencerName = null, int sequencerInstanceId = 0, string sequencerPath = null,
            string childCameraName = null, int childInstanceId = 0, string childPath = null,
            float hold = 2f,
            string blendStyle = "EaseInOut",
            float blendTime = 2f)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(sequencerName, sequencerInstanceId, sequencerPath);
            if (err != null) return err;

            var seq = CinemachineAdapter.GetSequencer(go);
            if (seq == null) return new { error = $"Not a {CinemachineAdapter.SequencerTypeName}" };

            var (childGo, childErr) = GameObjectFinder.FindOrError(childCameraName, childInstanceId, childPath);
            if (childErr != null) return childErr;
            var childVcam = childGo.GetComponent<CinemachineVirtualCameraBase>();
            if (childVcam == null) return new { error = "Child is not a Cinemachine Virtual Camera" };

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(seq, "Add Sequencer Instruction");

            var blend = CinemachineAdapter.CreateBlendDefinition(blendStyle, blendTime);
            CinemachineAdapter.AddSequencerInstruction(seq, childVcam, hold, blend);
            EditorUtility.SetDirty(seq);

            int count = CinemachineAdapter.GetSequencerInstructionCount(seq);
            return new { success = true, message = $"Added instruction #{count}: {childGo.name} (hold={hold}s, blend={blendStyle} {blendTime}s)" };
#endif
        }

        // ===================== FreeLook =====================

        [UnitySkill("cinemachine_create_freelook", "Create a FreeLook camera. CM2: CinemachineFreeLook. CM3: CinemachineCamera + OrbitalFollow(ThreeRing) + RotationComposer.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "freelook", "orbit", "third-person", "cinemachine" },
            Outputs = new[] { "gameObjectName", "instanceId" })]
        public static object CinemachineCreateFreeLook(string name, string followName = null, string lookAtName = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var go = CinemachineAdapter.CreateFreeLook(name);

            // 确保 Brain 存在
            var mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.GetComponent<CinemachineBrain>() == null)
                Undo.AddComponent<CinemachineBrain>(mainCamera.gameObject);

            // 设置目标
            var vcam = CinemachineAdapter.GetVCam(go);
            if (vcam != null)
            {
                if (!string.IsNullOrEmpty(followName))
                {
                    var followGo = GameObjectFinder.Find(followName);
                    if (followGo != null) CinemachineAdapter.SetFollow(vcam, followGo.transform);
                }
                if (!string.IsNullOrEmpty(lookAtName))
                {
                    var lookAtGo = GameObjectFinder.Find(lookAtName);
                    if (lookAtGo != null) CinemachineAdapter.SetLookAt(vcam, lookAtGo.transform);
                }
            }
#if CINEMACHINE_2
            // CM2 FreeLook 有独立的 Follow/LookAt
            var freeLook = go.GetComponent<CinemachineFreeLook>();
            if (freeLook != null)
            {
                if (!string.IsNullOrEmpty(followName))
                {
                    var followGo = GameObjectFinder.Find(followName);
                    if (followGo != null) freeLook.m_Follow = followGo.transform;
                }
                if (!string.IsNullOrEmpty(lookAtName))
                {
                    var lookAtGo = GameObjectFinder.Find(lookAtName);
                    if (lookAtGo != null) freeLook.m_LookAt = lookAtGo.transform;
                }
            }
#endif

            Undo.RegisterCreatedObjectUndo(go, "Create FreeLook Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new { success = true, gameObjectName = go.name, instanceId = go.GetInstanceID() };
#endif
        }

        // ===================== Camera Manager Configure =====================

        [UnitySkill("cinemachine_configure_camera_manager", "Configure ClearShot/StateDriven/Sequencer camera properties.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "clearshot", "statedriven", "sequencer", "configure", "cinemachine" },
            Outputs = new[] { "success", "message" },
            RequiresInput = new[] { "camera" })]
        public static object CinemachineConfigureCameraManager(
            string cameraName = null, int cameraInstanceId = 0, string cameraPath = null,
            // ClearShot
            float? activateAfter = null,
            float? minDuration = null,
            bool? randomizeChoice = null,
            // StateDriven
            string animatorName = null,
            int? layerIndex = null,
            // Common
            string defaultBlendStyle = null,
            float? defaultBlendTime = null,
            // Sequencer
            bool? loop = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(cameraName, cameraInstanceId, cameraPath);
            if (err != null) return err;

            WorkflowManager.SnapshotObject(go);
            var changes = new List<string>();

            // ClearShot
            var clearShot = go.GetComponent<CinemachineClearShot>();
            if (clearShot != null)
            {
                Undo.RecordObject(clearShot, "Configure ClearShot");
#if CINEMACHINE_3
                if (activateAfter.HasValue) { clearShot.ActivateAfter = activateAfter.Value; changes.Add($"activateAfter={activateAfter.Value}"); }
                if (minDuration.HasValue) { clearShot.MinDuration = minDuration.Value; changes.Add($"minDuration={minDuration.Value}"); }
                if (randomizeChoice.HasValue) { clearShot.RandomizeChoice = randomizeChoice.Value; changes.Add($"randomize={randomizeChoice.Value}"); }
                if (defaultBlendStyle != null || defaultBlendTime.HasValue)
                {
                    string style = defaultBlendStyle ?? CinemachineAdapter.GetBlendStyle(clearShot.DefaultBlend);
                    float time = defaultBlendTime ?? CinemachineAdapter.GetBlendTime(clearShot.DefaultBlend);
                    clearShot.DefaultBlend = CinemachineAdapter.CreateBlendDefinition(style, time);
                    changes.Add($"blend={style} {time}s");
                }
#else
                if (activateAfter.HasValue) { clearShot.m_ActivateAfter = activateAfter.Value; changes.Add($"activateAfter={activateAfter.Value}"); }
                if (minDuration.HasValue) { clearShot.m_MinDuration = minDuration.Value; changes.Add($"minDuration={minDuration.Value}"); }
                if (randomizeChoice.HasValue) { clearShot.m_RandomizeChoice = randomizeChoice.Value; changes.Add($"randomize={randomizeChoice.Value}"); }
                if (defaultBlendStyle != null || defaultBlendTime.HasValue)
                {
                    string style = defaultBlendStyle ?? CinemachineAdapter.GetBlendStyle(clearShot.m_DefaultBlend);
                    float time = defaultBlendTime ?? CinemachineAdapter.GetBlendTime(clearShot.m_DefaultBlend);
                    clearShot.m_DefaultBlend = CinemachineAdapter.CreateBlendDefinition(style, time);
                    changes.Add($"blend={style} {time}s");
                }
#endif
                EditorUtility.SetDirty(clearShot);
            }

            // StateDriven
            var stateDriven = go.GetComponent<CinemachineStateDrivenCamera>();
            if (stateDriven != null)
            {
                Undo.RecordObject(stateDriven, "Configure StateDriven");
                if (!string.IsNullOrEmpty(animatorName))
                {
                    var animGo = GameObjectFinder.Find(animatorName);
                    if (animGo != null)
                    {
                        var animator = animGo.GetComponent<Animator>();
                        if (animator != null)
                        {
#if CINEMACHINE_3
                            stateDriven.AnimatedTarget = animator;
#else
                            stateDriven.m_AnimatedTarget = animator;
#endif
                            changes.Add($"animator={animatorName}");
                        }
                    }
                }
                if (layerIndex.HasValue)
                {
#if CINEMACHINE_3
                    stateDriven.LayerIndex = layerIndex.Value;
#else
                    stateDriven.m_LayerIndex = layerIndex.Value;
#endif
                    changes.Add($"layerIndex={layerIndex.Value}");
                }
                if (defaultBlendStyle != null || defaultBlendTime.HasValue)
                {
#if CINEMACHINE_3
                    string style = defaultBlendStyle ?? CinemachineAdapter.GetBlendStyle(stateDriven.DefaultBlend);
                    float time = defaultBlendTime ?? CinemachineAdapter.GetBlendTime(stateDriven.DefaultBlend);
                    stateDriven.DefaultBlend = CinemachineAdapter.CreateBlendDefinition(style, time);
#else
                    string style = defaultBlendStyle ?? CinemachineAdapter.GetBlendStyle(stateDriven.m_DefaultBlend);
                    float time = defaultBlendTime ?? CinemachineAdapter.GetBlendTime(stateDriven.m_DefaultBlend);
                    stateDriven.m_DefaultBlend = CinemachineAdapter.CreateBlendDefinition(style, time);
#endif
                    changes.Add($"blend={style} {time}s");
                }
                EditorUtility.SetDirty(stateDriven);
            }

            // Sequencer
            var seq = CinemachineAdapter.GetSequencer(go);
            if (seq != null && loop.HasValue)
            {
                Undo.RecordObject(seq, "Configure Sequencer");
                CinemachineAdapter.SetSequencerLoop(seq, loop.Value);
                changes.Add($"loop={loop.Value}");
                EditorUtility.SetDirty(seq);
            }

            if (changes.Count == 0) return new { error = "No matching camera manager found or no properties to change." };
            return new { success = true, message = $"Configured {go.name}: {string.Join(", ", changes)}" };
#endif
        }

        // ===================== Body / Aim Configure =====================

        [UnitySkill("cinemachine_configure_body", "Configure Body stage component (Follow, OrbitalFollow, ThirdPersonFollow, PositionComposer, etc.) in one call.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "body", "follow", "orbital", "thirdperson", "cinemachine" },
            Outputs = new[] { "success", "componentType", "changes" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineConfigureBody(
            string vcamName = null, int instanceId = 0, string path = null,
            // Follow / Transposer offset
            float? offsetX = null, float? offsetY = null, float? offsetZ = null,
            string bindingMode = null,
            float? dampingX = null, float? dampingY = null, float? dampingZ = null,
            // OrbitalFollow / OrbitalTransposer
            string orbitStyle = null, float? radius = null,
            float? topHeight = null, float? topRadius = null,
            float? midHeight = null, float? midRadius = null,
            float? bottomHeight = null, float? bottomRadius = null,
            // ThirdPersonFollow
            float? shoulderX = null, float? shoulderY = null, float? shoulderZ = null,
            float? verticalArmLength = null, float? cameraSide = null,
            // PositionComposer / FramingTransposer
            float? cameraDistance = null,
            float? screenX = null, float? screenY = null,
            float? deadZoneWidth = null, float? deadZoneHeight = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            var body = CinemachineAdapter.GetPipelineComponent(go, "Body");
            if (body == null) return new { error = "No Body stage component found. Add one first (e.g. CinemachineFollow, CinemachineOrbitalFollow)." };

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(body, "Configure Body");
            var typeName = body.GetType().Name;
            var changes = new List<string>();

            void TrySet(string prop, object val, string label)
            {
                if (val == null) return;
                if (SetFieldOrProperty(body, prop, val)) changes.Add($"{label}={val}");
            }

            // --- Follow / Transposer ---
            if (typeName.Contains("Follow") && !typeName.Contains("ThirdPerson") && !typeName.Contains("Orbital")
                || typeName.Contains("Transposer") && !typeName.Contains("Orbital") && !typeName.Contains("Framing"))
            {
#if CINEMACHINE_3
                if (offsetX.HasValue || offsetY.HasValue || offsetZ.HasValue)
                {
                    var f = (CinemachineFollow)body;
                    var cur = f.FollowOffset;
                    f.FollowOffset = new Vector3(offsetX ?? cur.x, offsetY ?? cur.y, offsetZ ?? cur.z);
                    changes.Add($"offset=({f.FollowOffset.x},{f.FollowOffset.y},{f.FollowOffset.z})");
                }
                TrySet("TrackerSettings.BindingMode", bindingMode, "bindingMode");
                if (dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue)
                {
                    var f = (CinemachineFollow)body;
                    var cur = f.TrackerSettings.PositionDamping;
                    var ts = f.TrackerSettings;
                    ts.PositionDamping = new Vector3(dampingX ?? cur.x, dampingY ?? cur.y, dampingZ ?? cur.z);
                    f.TrackerSettings = ts;
                    changes.Add($"damping=({ts.PositionDamping.x},{ts.PositionDamping.y},{ts.PositionDamping.z})");
                }
#else
                TrySet("m_FollowOffset", offsetX.HasValue || offsetY.HasValue || offsetZ.HasValue ? (object)null : null, "");
                if (offsetX.HasValue || offsetY.HasValue || offsetZ.HasValue)
                {
                    var cur = (Vector3)body.GetType().GetField("m_FollowOffset")?.GetValue(body);
                    var v = new Vector3(offsetX ?? cur.x, offsetY ?? cur.y, offsetZ ?? cur.z);
                    body.GetType().GetField("m_FollowOffset")?.SetValue(body, v);
                    changes.Add($"offset=({v.x},{v.y},{v.z})");
                }
                TrySet("m_BindingMode", bindingMode, "bindingMode");
                TrySet("m_XDamping", dampingX, "dampingX");
                TrySet("m_YDamping", dampingY, "dampingY");
                TrySet("m_ZDamping", dampingZ, "dampingZ");
#endif
            }
            // --- OrbitalFollow / OrbitalTransposer ---
            else if (typeName.Contains("Orbital"))
            {
#if CINEMACHINE_3
                TrySet("OrbitStyle", orbitStyle, "orbitStyle");
                TrySet("Radius", radius, "radius");
                TrySet("TrackerSettings.BindingMode", bindingMode, "bindingMode");
                if (dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue)
                {
                    var o = (CinemachineOrbitalFollow)body;
                    var cur = o.TrackerSettings.PositionDamping;
                    var ts = o.TrackerSettings;
                    ts.PositionDamping = new Vector3(dampingX ?? cur.x, dampingY ?? cur.y, dampingZ ?? cur.z);
                    o.TrackerSettings = ts;
                    changes.Add($"damping=({ts.PositionDamping.x},{ts.PositionDamping.y},{ts.PositionDamping.z})");
                }
                if (topHeight.HasValue || topRadius.HasValue || midHeight.HasValue || midRadius.HasValue || bottomHeight.HasValue || bottomRadius.HasValue)
                {
                    var o = (CinemachineOrbitalFollow)body;
                    var orbits = o.Orbits;
                    if (topHeight.HasValue) { orbits.Top.Height = topHeight.Value; changes.Add($"topH={topHeight.Value}"); }
                    if (topRadius.HasValue) { orbits.Top.Radius = topRadius.Value; changes.Add($"topR={topRadius.Value}"); }
                    if (midHeight.HasValue) { orbits.Center.Height = midHeight.Value; changes.Add($"midH={midHeight.Value}"); }
                    if (midRadius.HasValue) { orbits.Center.Radius = midRadius.Value; changes.Add($"midR={midRadius.Value}"); }
                    if (bottomHeight.HasValue) { orbits.Bottom.Height = bottomHeight.Value; changes.Add($"botH={bottomHeight.Value}"); }
                    if (bottomRadius.HasValue) { orbits.Bottom.Radius = bottomRadius.Value; changes.Add($"botR={bottomRadius.Value}"); }
                    o.Orbits = orbits;
                }
#else
                TrySet("m_BindingMode", bindingMode, "bindingMode");
                TrySet("m_XDamping", dampingX, "dampingX");
                TrySet("m_YDamping", dampingY, "dampingY");
                TrySet("m_ZDamping", dampingZ, "dampingZ");
#endif
            }
            // --- ThirdPersonFollow ---
            else if (typeName.Contains("ThirdPerson") || typeName.Contains("3rdPerson"))
            {
#if CINEMACHINE_3
                if (shoulderX.HasValue || shoulderY.HasValue || shoulderZ.HasValue)
                {
                    var tp = (CinemachineThirdPersonFollow)body;
                    var cur = tp.ShoulderOffset;
                    tp.ShoulderOffset = new Vector3(shoulderX ?? cur.x, shoulderY ?? cur.y, shoulderZ ?? cur.z);
                    changes.Add($"shoulder=({tp.ShoulderOffset.x},{tp.ShoulderOffset.y},{tp.ShoulderOffset.z})");
                }
                TrySet("VerticalArmLength", verticalArmLength, "armLength");
                TrySet("CameraSide", cameraSide, "cameraSide");
                TrySet("CameraDistance", cameraDistance, "distance");
                if (dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue)
                {
                    var tp = (CinemachineThirdPersonFollow)body;
                    var cur = tp.Damping;
                    tp.Damping = new Vector3(dampingX ?? cur.x, dampingY ?? cur.y, dampingZ ?? cur.z);
                    changes.Add($"damping=({tp.Damping.x},{tp.Damping.y},{tp.Damping.z})");
                }
#else
                TrySet("ShoulderOffset", shoulderX.HasValue || shoulderY.HasValue || shoulderZ.HasValue ? (object)null : null, "");
                if (shoulderX.HasValue || shoulderY.HasValue || shoulderZ.HasValue)
                {
                    var cur = (Vector3)(body.GetType().GetField("ShoulderOffset")?.GetValue(body) ?? Vector3.zero);
                    var v = new Vector3(shoulderX ?? cur.x, shoulderY ?? cur.y, shoulderZ ?? cur.z);
                    body.GetType().GetField("ShoulderOffset")?.SetValue(body, v);
                    changes.Add($"shoulder=({v.x},{v.y},{v.z})");
                }
                TrySet("VerticalArmLength", verticalArmLength, "armLength");
                TrySet("CameraSide", cameraSide, "cameraSide");
                TrySet("CameraDistance", cameraDistance, "distance");
                TrySet("Damping", dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue ? (object)null : null, "");
                if (dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue)
                {
                    var cur = (Vector3)(body.GetType().GetField("Damping")?.GetValue(body) ?? Vector3.zero);
                    var v = new Vector3(dampingX ?? cur.x, dampingY ?? cur.y, dampingZ ?? cur.z);
                    body.GetType().GetField("Damping")?.SetValue(body, v);
                    changes.Add($"damping=({v.x},{v.y},{v.z})");
                }
#endif
            }
            // --- PositionComposer / FramingTransposer ---
            else if (typeName.Contains("PositionComposer") || typeName.Contains("FramingTransposer"))
            {
                TrySet("CameraDistance", cameraDistance, "distance");
#if CINEMACHINE_3
                TrySet("Composition.ScreenPosition", screenX.HasValue && screenY.HasValue ? (object)new Vector2(screenX.Value, screenY.Value) : null, "screen");
                if (screenX.HasValue && !screenY.HasValue) TrySet("Composition.ScreenPosition.x", screenX, "screenX");
                if (screenY.HasValue && !screenX.HasValue) TrySet("Composition.ScreenPosition.y", screenY, "screenY");
                TrySet("Composition.DeadZone.Size", deadZoneWidth.HasValue && deadZoneHeight.HasValue ? (object)new Vector2(deadZoneWidth.Value, deadZoneHeight.Value) : null, "deadZone");
                if (dampingX.HasValue || dampingY.HasValue || dampingZ.HasValue)
                {
                    var cur = (Vector3)(body.GetType().GetField("Damping")?.GetValue(body) ?? Vector3.zero);
                    var v = new Vector3(dampingX ?? cur.x, dampingY ?? cur.y, dampingZ ?? cur.z);
                    body.GetType().GetField("Damping")?.SetValue(body, v);
                    changes.Add($"damping=({v.x},{v.y},{v.z})");
                }
#else
                TrySet("m_ScreenX", screenX, "screenX");
                TrySet("m_ScreenY", screenY, "screenY");
                TrySet("m_DeadZoneWidth", deadZoneWidth, "deadZoneW");
                TrySet("m_DeadZoneHeight", deadZoneHeight, "deadZoneH");
                TrySet("m_XDamping", dampingX, "dampingX");
                TrySet("m_YDamping", dampingY, "dampingY");
                TrySet("m_ZDamping", dampingZ, "dampingZ");
#endif
            }
            else
            {
                // Generic fallback - try all offset/damping params
                TrySet("FollowOffset", offsetX.HasValue || offsetY.HasValue || offsetZ.HasValue ? (object)new Vector3(offsetX ?? 0, offsetY ?? 0, offsetZ ?? 0) : null, "offset");
            }

            EditorUtility.SetDirty(body);
            if (changes.Count == 0) return new { success = true, componentType = typeName, message = "No changes applied (parameters may not match this component type)." };
            return new { success = true, componentType = typeName, changes = string.Join(", ", changes) };
#endif
        }

        [UnitySkill("cinemachine_configure_aim", "Configure Aim stage component (RotationComposer, PanTilt, Composer, POV, etc.) in one call.",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "aim", "composer", "pantilt", "cinemachine" },
            Outputs = new[] { "success", "componentType", "changes" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineConfigureAim(
            string vcamName = null, int instanceId = 0, string path = null,
            // Composer / RotationComposer
            float? screenX = null, float? screenY = null,
            float? deadZoneWidth = null, float? deadZoneHeight = null,
            float? softZoneWidth = null, float? softZoneHeight = null,
            float? horizontalDamping = null, float? verticalDamping = null,
            float? lookaheadTime = null, float? lookaheadSmoothing = null,
            bool? centerOnActivate = null,
            // PanTilt / POV
            string referenceFrame = null,
            float? panValue = null, float? tiltValue = null,
            // Target offset
            float? targetOffsetX = null, float? targetOffsetY = null, float? targetOffsetZ = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            var aim = CinemachineAdapter.GetPipelineComponent(go, "Aim");
            if (aim == null) return new { error = "No Aim stage component found. Add one first (e.g. CinemachineRotationComposer, CinemachinePanTilt)." };

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(aim, "Configure Aim");
            var typeName = aim.GetType().Name;
            var changes = new List<string>();

            void TrySet(string prop, object val, string label)
            {
                if (val == null) return;
                if (SetFieldOrProperty(aim, prop, val)) changes.Add($"{label}={val}");
            }

            if (typeName.Contains("Composer") || typeName.Contains("GroupComposer"))
            {
#if CINEMACHINE_3
                TrySet("CenterOnActivate", centerOnActivate, "centerOnActivate");
                if (screenX.HasValue || screenY.HasValue)
                {
                    var rc = (CinemachineRotationComposer)aim;
                    var cur = rc.Composition.ScreenPosition;
                    var pos = new Vector2(screenX ?? cur.x, screenY ?? cur.y);
                    var comp = rc.Composition;
                    comp.ScreenPosition = pos;
                    rc.Composition = comp;
                    changes.Add($"screen=({pos.x},{pos.y})");
                }
                TrySet("Damping", horizontalDamping.HasValue && verticalDamping.HasValue ? (object)new Vector2(horizontalDamping.Value, verticalDamping.Value) : null, "damping");
                if (horizontalDamping.HasValue && !verticalDamping.HasValue) TrySet("Damping.x", horizontalDamping, "hDamping");
                if (verticalDamping.HasValue && !horizontalDamping.HasValue) TrySet("Damping.y", verticalDamping, "vDamping");
                TrySet("Lookahead.Time", lookaheadTime, "lookaheadTime");
                TrySet("Lookahead.Smoothing", lookaheadSmoothing, "lookaheadSmooth");
                if (targetOffsetX.HasValue || targetOffsetY.HasValue || targetOffsetZ.HasValue)
                {
                    var rc = (CinemachineRotationComposer)aim;
                    var cur = rc.TargetOffset;
                    rc.TargetOffset = new Vector3(targetOffsetX ?? cur.x, targetOffsetY ?? cur.y, targetOffsetZ ?? cur.z);
                    changes.Add($"targetOffset=({rc.TargetOffset.x},{rc.TargetOffset.y},{rc.TargetOffset.z})");
                }
#else
                TrySet("m_CenterOnActivate", centerOnActivate, "centerOnActivate");
                TrySet("m_ScreenX", screenX, "screenX");
                TrySet("m_ScreenY", screenY, "screenY");
                TrySet("m_DeadZoneWidth", deadZoneWidth, "deadZoneW");
                TrySet("m_DeadZoneHeight", deadZoneHeight, "deadZoneH");
                TrySet("m_SoftZoneWidth", softZoneWidth, "softZoneW");
                TrySet("m_SoftZoneHeight", softZoneHeight, "softZoneH");
                TrySet("m_HorizontalDamping", horizontalDamping, "hDamping");
                TrySet("m_VerticalDamping", verticalDamping, "vDamping");
                TrySet("m_LookaheadTime", lookaheadTime, "lookaheadTime");
                TrySet("m_LookaheadSmoothing", lookaheadSmoothing, "lookaheadSmooth");
                if (targetOffsetX.HasValue || targetOffsetY.HasValue || targetOffsetZ.HasValue)
                {
                    var cur = (Vector3)(aim.GetType().GetField("m_TrackedObjectOffset")?.GetValue(aim) ?? Vector3.zero);
                    var v = new Vector3(targetOffsetX ?? cur.x, targetOffsetY ?? cur.y, targetOffsetZ ?? cur.z);
                    aim.GetType().GetField("m_TrackedObjectOffset")?.SetValue(aim, v);
                    changes.Add($"targetOffset=({v.x},{v.y},{v.z})");
                }
#endif
            }
            else if (typeName.Contains("PanTilt") || typeName.Contains("POV"))
            {
#if CINEMACHINE_3
                TrySet("ReferenceFrame", referenceFrame, "referenceFrame");
                TrySet("PanAxis.Value", panValue, "pan");
                TrySet("TiltAxis.Value", tiltValue, "tilt");
#else
                TrySet("m_HorizontalAxis.Value", panValue, "pan");
                TrySet("m_VerticalAxis.Value", tiltValue, "tilt");
#endif
            }

            EditorUtility.SetDirty(aim);
            if (changes.Count == 0) return new { success = true, componentType = typeName, message = "No changes applied." };
            return new { success = true, componentType = typeName, changes = string.Join(", ", changes) };
#endif
        }

        // ===================== Extension / Impulse Configure =====================

        [UnitySkill("cinemachine_configure_extension", "Configure Cinemachine extension properties (Confiner, Deoccluder, FollowZoom, GroupFraming, etc.).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "extension", "confiner", "deoccluder", "cinemachine" },
            Outputs = new[] { "success", "extensionType", "changes" },
            RequiresInput = new[] { "vcam" })]
        public static object CinemachineConfigureExtension(
            string vcamName = null, int instanceId = 0, string path = null,
            string extensionName = null,
            // Confiner
            string boundingShapeName = null,
            float? damping = null,
            float? slowingDistance = null,
            // Deoccluder / Collider
            float? cameraRadius = null,
            string strategy = null,
            int? maximumEffort = null,
            float? smoothingTime = null,
            // FollowZoom
            float? width = null,
            float? fovMin = null,
            float? fovMax = null,
            // GroupFraming
            string framingMode = null,
            float? framingSize = null,
            string sizeAdjustment = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            var (go, err) = GameObjectFinder.FindOrError(vcamName, instanceId, path);
            if (err != null) return err;

            MonoBehaviour ext = null;
            if (!string.IsNullOrEmpty(extensionName))
            {
                var type = FindCinemachineType(extensionName);
                if (type != null) ext = go.GetComponent(type) as MonoBehaviour;
            }
            if (ext == null)
            {
                // Auto-detect: find first CinemachineExtension on the GO
                var exts = go.GetComponents<CinemachineExtension>();
                ext = exts.Length > 0 ? exts[0] : null;
            }
            if (ext == null) return new { error = "No Cinemachine extension found. Add one first with cinemachine_add_extension." };

            WorkflowManager.SnapshotObject(go);
            Undo.RecordObject(ext, "Configure Extension");
            var typeName = ext.GetType().Name;
            var changes = new List<string>();

            void TrySet(string prop, object val, string label)
            {
                if (val == null) return;
                if (SetFieldOrProperty(ext, prop, val)) changes.Add($"{label}={val}");
            }

            // Confiner (CM2: CinemachineConfiner, CM3: CinemachineConfiner2D/3D)
            if (typeName.Contains("Confiner"))
            {
                if (!string.IsNullOrEmpty(boundingShapeName))
                {
                    var shapeGo = GameObjectFinder.Find(boundingShapeName);
                    if (shapeGo != null)
                    {
                        // Try Collider2D, then Collider
                        var col2d = shapeGo.GetComponent<Collider2D>();
                        var col3d = shapeGo.GetComponent<Collider>();
                        if (col2d != null && SetFieldOrProperty(ext, "BoundingShape2D", col2d))
                            changes.Add($"boundingShape={boundingShapeName}(2D)");
                        else if (col2d != null && SetFieldOrProperty(ext, "m_BoundingShape2D", col2d))
                            changes.Add($"boundingShape={boundingShapeName}(2D)");
                        else if (col3d != null && SetFieldOrProperty(ext, "BoundingVolume", col3d))
                            changes.Add($"boundingVolume={boundingShapeName}(3D)");
                        else if (col3d != null && SetFieldOrProperty(ext, "m_BoundingVolume", col3d))
                            changes.Add($"boundingVolume={boundingShapeName}(3D)");
                    }
                }
                TrySet("Damping", damping, "damping");
                TrySet("m_Damping", damping, "damping");
                TrySet("SlowingDistance", slowingDistance, "slowingDist");
                TrySet("m_SlowingDistance", slowingDistance, "slowingDist");
            }
            // Deoccluder / Collider
            else if (typeName.Contains("Deoccluder") || typeName.Contains("Collider"))
            {
                TrySet("AvoidObstacles.CameraRadius", cameraRadius, "camRadius");
                TrySet("m_AvoidOcclusionRadius", cameraRadius, "camRadius");
                TrySet("AvoidObstacles.Strategy", strategy, "strategy");
                TrySet("m_Strategy", strategy, "strategy");
                TrySet("AvoidObstacles.MaximumEffort", maximumEffort, "maxEffort");
                TrySet("m_MaximumEffort", maximumEffort, "maxEffort");
                TrySet("AvoidObstacles.SmoothingTime", smoothingTime, "smoothTime");
                TrySet("m_SmoothingTime", smoothingTime, "smoothTime");
                TrySet("AvoidObstacles.Damping", damping, "damping");
                TrySet("m_Damping", damping, "damping");
            }
            // FollowZoom
            else if (typeName.Contains("FollowZoom"))
            {
                TrySet("Width", width, "width");
                TrySet("m_Width", width, "width");
                TrySet("Damping", damping, "damping");
                TrySet("m_Damping", damping, "damping");
                if (fovMin.HasValue && fovMax.HasValue)
                {
                    var range = new Vector2(fovMin.Value, fovMax.Value);
                    if (SetFieldOrProperty(ext, "FovRange", range) || SetFieldOrProperty(ext, "m_MinFOV", fovMin))
                    {
                        SetFieldOrProperty(ext, "m_MaxFOV", fovMax);
                        changes.Add($"fovRange=({fovMin},{fovMax})");
                    }
                }
            }
            // GroupFraming
            else if (typeName.Contains("GroupFraming"))
            {
                TrySet("FramingMode", framingMode, "framingMode");
                TrySet("FramingSize", framingSize, "framingSize");
                TrySet("SizeAdjustment", sizeAdjustment, "sizeAdjust");
                TrySet("Damping", damping, "damping");
            }
            else
            {
                // Generic: try all params
                TrySet("Damping", damping, "damping");
                TrySet("CameraRadius", cameraRadius, "camRadius");
            }

            EditorUtility.SetDirty(ext);
            if (changes.Count == 0) return new { success = true, extensionType = typeName, message = "No changes applied." };
            return new { success = true, extensionType = typeName, changes = string.Join(", ", changes) };
#endif
        }

        [UnitySkill("cinemachine_configure_impulse_source", "Configure CinemachineImpulseSource definition (shape, duration, gains).",
            Category = SkillCategory.Cinemachine, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "impulse", "shake", "configure", "cinemachine" },
            Outputs = new[] { "success", "changes" },
            RequiresInput = new[] { "source" })]
        public static object CinemachineConfigureImpulseSource(
            string sourceName = null, int sourceInstanceId = 0, string sourcePath = null,
            float? amplitudeGain = null,
            float? frequencyGain = null,
            float? impactRadius = null,
            float? duration = null,
            float? dissipationRate = null)
        {
#if !CINEMACHINE_2 && !CINEMACHINE_3
            return NoCinemachine();
#else
            MonoBehaviour source = null;
            if (!string.IsNullOrEmpty(sourceName) || sourceInstanceId != 0 || !string.IsNullOrEmpty(sourcePath))
            {
                var (go, err) = GameObjectFinder.FindOrError(sourceName, sourceInstanceId, sourcePath);
                if (err != null) return err;
                source = go.GetComponent<CinemachineImpulseSource>();
            }
            else
            {
                var sources = FindAllObjects<CinemachineImpulseSource>();
                source = sources.Length > 0 ? sources[0] : null;
            }
            if (source == null) return new { error = "No CinemachineImpulseSource found." };

            WorkflowManager.SnapshotObject(source.gameObject);
            Undo.RecordObject(source, "Configure Impulse Source");
            var changes = new List<string>();

            void TrySet(string prop, object val, string label)
            {
                if (val == null) return;
                if (SetFieldOrProperty(source, prop, val)) changes.Add($"{label}={val}");
            }

            // CM3: ImpulseDefinition is a direct field, CM2: m_ImpulseDefinition
#if CINEMACHINE_3
            TrySet("ImpulseDefinition.ImpactRadius", impactRadius, "impactRadius");
            TrySet("ImpulseDefinition.DissipationRate", dissipationRate, "dissipationRate");
            TrySet("ImpulseDefinition.AmplitudeGain", amplitudeGain, "amplitudeGain");
            TrySet("ImpulseDefinition.FrequencyGain", frequencyGain, "frequencyGain");
            TrySet("ImpulseDefinition.TimeEnvelope.Duration", duration, "duration");
#else
            TrySet("m_ImpulseDefinition.m_ImpactRadius", impactRadius, "impactRadius");
            TrySet("m_ImpulseDefinition.m_DissipationRate", dissipationRate, "dissipationRate");
            TrySet("m_ImpulseDefinition.m_AmplitudeGain", amplitudeGain, "amplitudeGain");
            TrySet("m_ImpulseDefinition.m_FrequencyGain", frequencyGain, "frequencyGain");
            TrySet("m_ImpulseDefinition.m_TimeEnvelope.m_Duration", duration, "duration");
#endif

            EditorUtility.SetDirty(source);
            if (changes.Count == 0) return new { success = true, message = "No changes applied." };
            return new { success = true, source = source.gameObject.name, changes = string.Join(", ", changes) };
#endif
        }
    }
}
