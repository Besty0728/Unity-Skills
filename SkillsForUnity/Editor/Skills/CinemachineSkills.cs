using UnityEngine;
using UnityEditor;
using Unity.Cinemachine;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Cinemachine skills - Deep control & introspection.
    /// Updated for Cinemachine 3.x
    /// </summary>
    public static class CinemachineSkills
    {
        [UnitySkill("cinemachine_create_vcam", "Create a new Virtual Camera")]
        public static object CinemachineCreateVCam(string name, string folder = "Assets/Settings")
        {
            var go = new GameObject(name);
            var vcam = go.AddComponent<CinemachineCamera>(); // CM 3.x
            vcam.Priority = 10; 

            Undo.RegisterCreatedObjectUndo(go, "Create Virtual Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            // Ensure CinemachineBrain exists
            if (Camera.main != null)
            {
                var brain = Camera.main.gameObject.GetComponent<CinemachineBrain>();
                if (brain == null)
                {
                    var brainComp = Undo.AddComponent<CinemachineBrain>(Camera.main.gameObject);
                    WorkflowManager.SnapshotCreatedComponent(brainComp);
                }
            }

            return new { success = true, gameObjectName = go.name, instanceId = go.GetInstanceID() };
        }

        [UnitySkill("cinemachine_inspect_vcam", "Deeply inspect a VCam, returning fields and tooltips.")]
        public static object CinemachineInspectVCam(string objectName)
        {
            var go = GameObject.Find(objectName);
            if (go == null) return new { error = "GameObject not found" };
            
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            // Helper to scrape a component
            object InspectComponent(object component)
            {
                if (component == null) return null;
                // Use Sanitize to deeply convert the component to a safe Dictionary
                return Sanitize(component);
            }

            // In CM 3.x, procedural components are separate MonoBehaviours
            var components = go.GetComponents<MonoBehaviour>()
                               .Where(mb => mb != null && mb.GetType().Namespace != null && mb.GetType().Namespace.Contains("Cinemachine"))
                               .Select(mb => InspectComponent(mb))
                               .ToList();

            return new
            {
                name = vcam.name,
                priority = vcam.Priority,
                follow = vcam.Follow ? vcam.Follow.name : "None",
                lookAt = vcam.LookAt ? vcam.LookAt.name : "None",
                lens = InspectComponent(vcam.Lens),
                components = components
            };
        }

        // --- Custom Sanitizer to break Loops ---
        private static object Sanitize(object obj, int depth = 0)
        {
            if (obj == null) return null;
            if (depth > 5) return obj.ToString(); // Limit recursion

            var t = obj.GetType();
            if (t.IsPrimitive || t == typeof(string) || t == typeof(bool) || t.IsEnum) return obj;

            // Handle Unity Structs manually
            if (obj is Vector2 v2) return new { v2.x, v2.y };
            if (obj is Vector3 v3) return new { v3.x, v3.y, v3.z };
            if (obj is Vector4 v4) return new { v4.x, v4.y, v4.z, v4.w };
            if (obj is Quaternion q) return new { q.x, q.y, q.z, q.w };
            if (obj is Color c) return new { c.r, c.g, c.b, c.a };
            if (obj is Rect r) return new { r.x, r.y, r.width, r.height };
            
            // Handle Arrays/Lists
            if (obj is System.Collections.IEnumerable list)
            {
                var result = new List<object>();
                foreach(var item in list) result.Add(Sanitize(item, depth + 1));
                return result;
            }

            // Deep Sanitization for complex Structs/Classes (like PhysicalSettings)
            var dict = new Dictionary<string, object>();
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

            foreach (var member in members)
            {
                 // Skip obsolete and problematic properties
                 if (member.GetCustomAttribute<System.ObsoleteAttribute>() != null) continue;
                 if (member.Name == "normalized" || member.Name == "magnitude" || member.Name == "sqrMagnitude") continue;

                 try 
                 {
                     object val = null;
                     if (member is FieldInfo f) val = f.GetValue(obj);
                     else if (member is PropertyInfo p && p.CanRead && p.GetIndexParameters().Length == 0) val = p.GetValue(obj);
                     
                     if (val != null)
                     {
                         dict[member.Name] = Sanitize(val, depth + 1);
                     }
                 }
                 catch {}
            }
            // Add type name specifically if depth 0 (top level component)
            if (depth == 0) dict["_type"] = t.Name; 
            
            return dict;
        }

        [UnitySkill("cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components.")]
        public static object CinemachineSetVCamProperty(string vcamName, string componentType, string propertyName, object value)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            object target = null;
            bool isLens = false;
            
            if (componentType.Equals("Main", System.StringComparison.OrdinalIgnoreCase) || 
                componentType.Equals("CinemachineCamera", System.StringComparison.OrdinalIgnoreCase))
            {
                target = vcam;
            }
            else if (componentType.Equals("Lens", System.StringComparison.OrdinalIgnoreCase))
            {
                target = vcam.Lens; 
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
                object boxedLens = vcam.Lens;
                if (SetFieldOrProperty(boxedLens, propertyName, value))
                {
                   vcam.Lens = (LensSettings)boxedLens;
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
        }

        [UnitySkill("cinemachine_set_targets", "Set Follow and LookAt targets.")]
        public static object CinemachineSetTargets(string vcamName, string followName = null, string lookAtName = null)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            if (followName != null) 
                vcam.Follow = GameObject.Find(followName)?.transform;
            if (lookAtName != null) 
                vcam.LookAt = GameObject.Find(lookAtName)?.transform;
                
            Action(go, "Set Targets");
            return new { success = true };
        }

        [UnitySkill("cinemachine_add_component", "Add a Cinemachine component (e.g., OrbitalFollow).")]
        public static object CinemachineAddComponent(string vcamName, string componentType)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            
            var type = FindCinemachineType(componentType);
            if (type == null) return new { error = "Could not find Cinemachine component type: " + componentType };

            var comp = Undo.AddComponent(go, type);
            if (comp != null)
            {
                return new { success = true, message = "Added " + type.Name + " to " + vcamName };
            }
            return new { error = "Failed to add component." };
        }

        // --- NEW SKILLS (v1.5/CM3) ---

        [UnitySkill("cinemachine_set_lens", "Quickly configure Lens settings (FOV, Near, Far, OrthoSize).")]
        public static object CinemachineSetLens(string vcamName, float? fov = null, float? nearClip = null, float? farClip = null, float? orthoSize = null, string mode = null)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
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

            return new { success = false, message = "No values provided to update." };
        }

        [UnitySkill("cinemachine_list_components", "List all available Cinemachine component names.")]
        public static object CinemachineListComponents()
        {
            var cmAssembly = typeof(CinemachineCamera).Assembly;
            var componentTypes = cmAssembly.GetTypes()
                .Where(t => t.IsSubclassOf(typeof(MonoBehaviour)) && !t.IsAbstract && t.IsPublic)
                .Select(t => t.Name)
                .Where(n => n.StartsWith("Cinemachine"))
                .OrderBy(n => n)
                .ToList();

            return new { success = true, count = componentTypes.Count, components = componentTypes };
        }

        [UnitySkill("cinemachine_set_component", "Switch VCam pipeline component (Body/Aim/Noise).")]
        public static object CinemachineSetComponent(string vcamName, string stage, string componentType)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            if (!System.Enum.TryParse<CinemachineCore.Stage>(stage, true, out var stageEnum))
            {
                return new { error = "Invalid stage. Use Body, Aim, or Noise." };
            }

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
            }

            EditorUtility.SetDirty(go);
            return new { success = true, message = "Set " + stage + " to " + (componentType ?? "None") };
        }

        [UnitySkill("cinemachine_impulse_generate", "Trigger an Impulse. Params: {velocity: {x,y,z}} or empty.")]
        public static object CinemachineImpulseGenerate(string sourceParams) 
        {
             var sources = Object.FindObjectsOfType<CinemachineImpulseSource>();
             if (sources.Length == 0) return new { success = false, error = "No CinemachineImpulseSource found in scene." };
             
             var source = sources[0]; // Default to first found
             Vector3 velocity = Vector3.down; // Default direction

             // Parse JSON params if provided
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
                      // Can expand for Position/Force later if needed
                 }
                 catch { /* If parsing fails, use defaults */ }
             }

             source.GenerateImpulse(velocity);
             return new { success = true, message = "Generated Impulse from " + source.name + " with velocity " + velocity };
        }
        
        [UnitySkill("cinemachine_get_brain_info", "Get info about the Active Camera and Blend.")]
        public static object CinemachineGetBrainInfo()
        {
            if (Camera.main == null) return new { error = "No Main Camera" };
            var brain = Camera.main.GetComponent<CinemachineBrain>();
            if (brain == null) return new { error = "No CinemachineBrain on Main Camera" };

            var activeCam = brain.ActiveVirtualCamera as Component;
            
            return new { 
                success = true,
                activeCamera = activeCam ? activeCam.name : "None",
                isBlending = brain.IsBlending,
                activeBlend = brain.ActiveBlend?.Description ?? "None",
                updateMethod = brain.UpdateMethod.ToString()
            };
        }

        [UnitySkill("cinemachine_set_active", "Force activation of a VCam (SOLO) by setting highest priority.")]
        public static object CinemachineSetActive(string vcamName)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            var allCams = Object.FindObjectsOfType<CinemachineCamera>();
            int maxPrio = 0;
            if (allCams.Length > 0) maxPrio = allCams.Max(c => c.Priority);

            vcam.Priority = maxPrio + 1;
            EditorUtility.SetDirty(vcam);

            return new { success = true, message = "Set Priority to " + vcam.Priority + " (Highest)" };
        }

        [UnitySkill("cinemachine_set_noise", "Configure Noise settings (Basic Multi Channel Perlin).")]
        public static object CinemachineSetNoise(string vcamName, float amplitudeGain, float frequencyGain)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            
            var perlin = go.GetComponent<CinemachineBasicMultiChannelPerlin>();
            if (perlin == null) perlin = Undo.AddComponent<CinemachineBasicMultiChannelPerlin>(go);

            perlin.AmplitudeGain = amplitudeGain;
            perlin.FrequencyGain = frequencyGain;
            EditorUtility.SetDirty(perlin);
            
            return new { success = true, message = "Set Noise profile." };
        }

        // --- Helpers ---
        
        private static void Action(Object target, string name)
        {
            Undo.RecordObject(target, name);
            EditorUtility.SetDirty(target);
        }

        private static bool SetFieldOrProperty(object target, string name, object value)
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
                if (destType.IsEnum) { try { return System.Enum.Parse(destType, val.ToString(), true); } catch { } }
                try { return JToken.FromObject(val).ToObject(destType); } catch {}
                try { return System.Convert.ChangeType(val, destType); } catch { return null; }
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                try {
                    object safeValue = SafeConvert(value, field.FieldType);
                    if (safeValue != null) { field.SetValue(target, safeValue); return true; }
                } catch { }
            }
            
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try {
                    object safeValue = SafeConvert(value, prop.PropertyType);
                    if (safeValue != null) { prop.SetValue(target, safeValue); return true; }
                } catch { }
            }
            return false;
        }

        private static System.Type FindCinemachineType(string name)
        {
             if (string.IsNullOrEmpty(name)) return null;
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
             if (map.TryGetValue(name, out var fullName)) name = fullName;
             if (!name.StartsWith("Cinemachine")) name = "Cinemachine" + name;
             
             var cmAssembly = typeof(CinemachineCamera).Assembly;
             var type = cmAssembly.GetType("Unity.Cinemachine." + name, false, true);
             if (type == null) type = cmAssembly.GetType(name, false, true);
             return type;
        }
    }
}
