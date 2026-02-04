using UnityEngine;
using UnityEditor;
using Cinemachine;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Cinemachine skills - Deep control & introspection.
    /// </summary>
    public static class CinemachineSkills
    {
        [UnitySkill("cinemachine_create_vcam", "Create a new Virtual Camera")]
        public static object CinemachineCreateVCam(string name, string folder = "Assets/Settings")
        {
            var go = new GameObject(name);
            var vcam = go.AddComponent<CinemachineVirtualCamera>();
            vcam.m_Priority = 10;

            Undo.RegisterCreatedObjectUndo(go, "Create Virtual Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            // Ensure CinemachineBrain exists on Main Camera
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
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a Virtual Camera" };

            // Helper to scrape a component/object
            object InspectPipelineComponent(object component)
            {
                if (component == null) return null;
                var type = component.GetType();
                var fields = new List<object>();
                
                // Get all public fields
                foreach(var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    // Skip hidden/obsolete if needed, but let's show all
                    var tooltipAttr = field.GetCustomAttribute<TooltipAttribute>();
                    var val = field.GetValue(component);
                    
                    // Handle Vectors specially for nicer JSON
                    if (val is Vector3 v3) val = new { v3.x, v3.y, v3.z };
                    if (val is Vector2 v2) val = new { v2.x, v2.y };
                    
                    fields.Add(new 
                    {
                        name = field.Name,
                        type = field.FieldType.Name,
                        value = val,
                        tooltip = tooltipAttr?.tooltip ?? ""
                    });
                }
                
                return new 
                { 
                    type = type.Name, 
                    fields 
                };
            }

            return new
            {
                name = vcam.Name,
                priority = vcam.m_Priority,
                follow = vcam.Follow ? vcam.Follow.name : "None",
                lookAt = vcam.LookAt ? vcam.LookAt.name : "None",
                lens = InspectPipelineComponent(vcam.m_Lens), // Lens is a struct, works fine
                bodyComponent = InspectPipelineComponent(vcam.GetCinemachineComponent(CinemachineCore.Stage.Body)),
                aimComponent = InspectPipelineComponent(vcam.GetCinemachineComponent(CinemachineCore.Stage.Aim)),
                noiseComponent = InspectPipelineComponent(vcam.GetCinemachineComponent(CinemachineCore.Stage.Noise))
            };
        }

        [UnitySkill("cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components.")]
        public static object CinemachineSetVCamProperty(string vcamName, string componentType, string propertyName, object value)
        {
            // componentType: "Body", "Aim", "Noise", "Main" (VCam itself), "Lens"
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a Virtual Camera" };

            object target = null;
            
            // Determine target object
            switch(componentType.ToLower())
            {
                case "main": target = vcam; break;
                
                // Lens is a struct field on VCam, not a Component. 
                // Setting it requires Getting, Modifying, Setting back.
                // For simplicity, we might treat Lens properties as "Main" properties accessed via "m_Lens.FieldOfView"?
                // Or handle separately. Let's try direct reflection support for nested paths later.
                // For this implementation, let's stick to component objects.
                
                // Special handling for Lens:
                case "lens": 
                    // LensSettings is a struct. To modify via reflection, we MUST box it first.
                    object boxedLens = vcam.m_Lens;
                    if (!SetFieldOrProperty(boxedLens, propertyName, value)) 
                        return new { error = $"Property {propertyName} not found on LensSettings" };
                    
                    vcam.m_Lens = (LensSettings)boxedLens; // Unbox modified value back
                    return new { success = true, message = $"Set Lens.{propertyName} to {value}" };

                case "body": target = vcam.GetCinemachineComponent(CinemachineCore.Stage.Body); break;
                case "aim": target = vcam.GetCinemachineComponent(CinemachineCore.Stage.Aim); break;
                case "noise": target = vcam.GetCinemachineComponent(CinemachineCore.Stage.Noise); break;
                default: return new { error = "Unknown component type. Use Main, Body, Aim, Noise, or Lens." };
            }

            if (target == null) return new { error = $"Component {componentType} not found on VCam." };

            if (SetFieldOrProperty(target, propertyName, value))
            {
                // If we modified a component, we might need to tell Editor it's dirty
                EditorUtility.SetDirty(vcam); 
                if (target is MonoBehaviour mb) EditorUtility.SetDirty(mb);
                return new { success = true, message = $"Set {componentType}.{propertyName} to {value}" };
            }
            
            return new { error = $"Property {propertyName} not found on {componentType} ({target.GetType().Name})" };
        }

        // Helper to set field OR property via reflection
        private static bool SetFieldOrProperty(object target, string name, object value)
        {
            var type = target.GetType();
            var flags = BindingFlags.Public | BindingFlags.Instance;

            // Helper for conversion
            object SafeConvert(object val, System.Type destType)
            {
                if (val == null) return null;
                if (destType.IsAssignableFrom(val.GetType())) return val;
                
                // 1. Handle string -> Unity Object (Transform/GameObject) lookup
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
                
                // 2. Handle Enums (from string or int)
                if (destType.IsEnum)
                {
                    try { return System.Enum.Parse(destType, val.ToString(), true); } catch { }
                }

                // 3. Try Newtonsoft conversion (handles Vectors {x,y,z}, Arrays, etc.)
                try {
                    return JToken.FromObject(val).ToObject(destType);
                } catch {}

                // 4. Fallback to simple conversion
                try {
                    return System.Convert.ChangeType(val, destType);
                } catch { return null; }
            }

            // Try Field first
            var field = type.GetField(name, flags);
            if (field != null)
            {
                try 
                {
                    object safeValue = SafeConvert(value, field.FieldType);
                    if (safeValue != null)
                    {
                        field.SetValue(target, safeValue);
                        return true;
                    }
                }
                catch { }
            }

            // Try Property
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    object safeValue = SafeConvert(value, prop.PropertyType);
                    if (safeValue != null)
                    {
                        prop.SetValue(target, safeValue);
                        return true;
                    }
                }
                catch { }
            }
            
            return false;
        }

        [UnitySkill("cinemachine_set_targets", "Set Follow and LookAt targets.")]
        public static object CinemachineSetTargets(string vcamName, string followName = null, string lookAtName = null)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            
            if (followName != null) 
                vcam.Follow = GameObject.Find(followName)?.transform;
            if (lookAtName != null) 
                vcam.LookAt = GameObject.Find(lookAtName)?.transform;
                
            return new { success = true };
        }
        [UnitySkill("cinemachine_set_component", "Switch VCam pipeline component (Body/Aim/Noise).")]
        public static object CinemachineSetComponent(string vcamName, string stage, string componentType)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) return new { error = "Not a Virtual Camera" };
            
            // Normalize stage
            CinemachineCore.Stage stageEnum;
            switch(stage.ToLower())
            {
                case "body": stageEnum = CinemachineCore.Stage.Body; break;
                case "aim": stageEnum = CinemachineCore.Stage.Aim; break;
                case "noise": stageEnum = CinemachineCore.Stage.Noise; break;
                default: return new { error = "Invalid stage. Use Body, Aim, or Noise." };
            }
            
            // Handle "None" / "Do Nothing"
            if (componentType.ToLower() == "none" || componentType.ToLower() == "donothing")
            {
                var existing = vcam.GetCinemachineComponent(stageEnum);
                if (existing != null)
                {
                    Undo.DestroyObjectImmediate(existing);
                    return new { success = true, message = $"Removed component at stage {stage}" };
                }
                return new { success = true, message = $"No component at stage {stage} to remove" };
            }
            
            // Resolve Type
            var type = FindCinemachineType(componentType);
            if (type == null) return new { error = $"Could not find Cinemachine component type: {componentType}" };
            
            // Verify stage compatibility (optional, but AddCinemachineComponent handles it)
            // But AddCinemachineComponent<T> is generic. We have a Type object.
            // We need to use reflection or the non-generic internal/inspector methods, 
            // OR just AddComponent and verify?
            // CinemachineVirtualCamera.AddCinemachineComponent<T>() implementation basically does:
            // Destroy existing at stage -> Undo.AddComponent<T> -> Invalidate pipeline
            
            // IMPORTANT: standard AddComponent might not handle the pipeline replacement and "Hidden" flags correctly if we don't use the VCam helper.
            // But the VCam helper is Generic only: public T AddCinemachineComponent<T>()
            // We can invoke it via reflection.
            
            var method = typeof(CinemachineVirtualCamera).GetMethod("AddCinemachineComponent", BindingFlags.Public | BindingFlags.Instance);
            var generic = method.MakeGenericMethod(type);
            var newComponent = generic.Invoke(vcam, null);
            
            return new { success = true, message = $"Set {stage} to {type.Name}" };
        }
        
        private static System.Type FindCinemachineType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            
            // Try explicit lookup for common short names
            var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "Transposer", "CinemachineTransposer" },
                { "FramingTransposer", "CinemachineFramingTransposer" },
                { "Composer", "CinemachineComposer" },
                { "Pov", "CinemachinePOV" },
                { "OrbitalTransposer", "CinemachineOrbitalTransposer" },
                { "TrackedDolly", "CinemachineTrackedDolly" },
                { "HardLockToTarget", "CinemachineHardLockToTarget" },
                { "SameAsFollowTarget", "CinemachineSameAsFollowTarget" },
                { "BasicMultiChannelPerlin", "CinemachineBasicMultiChannelPerlin" }
            };
            
            if (map.TryGetValue(name, out var fullName)) name = fullName;
            if (!name.StartsWith("Cinemachine")) name = "Cinemachine" + name;
            
            // Search in Cinemachine assembly
            var cinemachineAssembly = typeof(CinemachineVirtualCamera).Assembly;
            var type = cinemachineAssembly.GetType("Cinemachine." + name, false, true);
            if (type != null) return type;
            
            // Brute force search all assemblies? unlikely needed if it's a standard component
            return null;
        }
    }
}
