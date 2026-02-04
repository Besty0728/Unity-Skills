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
            var vcam = go.AddComponent<CinemachineCamera>(); // CM 3.x: CinemachineVirtualCamera -> CinemachineCamera
            vcam.Priority = 10; // CM 3.x: m_Priority -> Priority

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
            
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            // Helper to scrape a component/object
            object InspectComponent(object component)
            {
                if (component == null) return null;
                var type = component.GetType();
                var fields = new List<object>();
                
                // Get all public fields and properties
                var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property);

                foreach(var member in members)
                {
                    // Skip obsolete
                    if (member.GetCustomAttribute<System.ObsoleteAttribute>() != null) continue;

                    var tooltipAttr = member.GetCustomAttribute<TooltipAttribute>();
                    object val = null;
                    string typeName = "";
                    bool canWrite = false;

                    try 
                    {
                        if (member is FieldInfo f) 
                        {
                            val = f.GetValue(component);
                            typeName = f.FieldType.Name;
                            canWrite = !f.IsInitOnly;
                        }
                        else if (member is PropertyInfo p)
                        {
                             if (!p.CanRead) continue;
                             val = p.GetValue(component);
                             typeName = p.PropertyType.Name;
                             canWrite = p.CanWrite;
                        }

                        // Handle Vectors specially for nicer JSON
                        if (val is Vector3 v3) val = new { v3.x, v3.y, v3.z };
                        if (val is Vector2 v2) val = new { v2.x, v2.y };
                    } 
                    catch { continue; }
                    
                    fields.Add(new 
                    {
                        name = member.Name,
                        type = typeName,
                        value = val,
                        tooltip = tooltipAttr?.tooltip ?? "",
                        readOnly = !canWrite
                    });
                }
                
                return new 
                { 
                    type = type.Name, 
                    fields 
                };
            }

            // In CM 3.x, procedural components are separate MonoBehaviours on the same GameObject
            // We just scrape all Cinemachine components found on the GO
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
                lens = InspectComponent(vcam.Lens), // Lens is usually property in 3.x too
                components = components
            };
        }

        [UnitySkill("cinemachine_set_vcam_property", "Set any property on VCam or its pipeline components.")]
        public static object CinemachineSetVCamProperty(string vcamName, string componentType, string propertyName, object value)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            object target = null;
            
            // 1. Check if targeting Main Camera component
            if (componentType.Equals("Main", System.StringComparison.OrdinalIgnoreCase) || 
                componentType.Equals("CinemachineCamera", System.StringComparison.OrdinalIgnoreCase))
            {
                target = vcam;
            }
            // 2. Check if targeting Lens (LensSettings struct typically)
            else if (componentType.Equals("Lens", System.StringComparison.OrdinalIgnoreCase))
            {
                // In 3.x, Lens is likely a property returning a struct or class.
                // Assuming LensSettings struct property "Lens"
                object boxedLens = vcam.Lens;
                if (!SetFieldOrProperty(boxedLens, propertyName, value)) 
                    return new { error = "Property " + propertyName + " not found on Lens" };
                
                vcam.Lens = (LensSettings)boxedLens; // Write back
                return new { success = true, message = "Set Lens." + propertyName + " to " + value };
            }
            else 
            {
                // 3. Try to find matching component
                var comps = go.GetComponents<MonoBehaviour>();
                target = comps.FirstOrDefault(c => c.GetType().Name.Equals(componentType, System.StringComparison.OrdinalIgnoreCase));
                
                // Try with "Cinemachine" prefix if not found
                if (target == null && !componentType.StartsWith("Cinemachine"))
                {
                    target = comps.FirstOrDefault(c => c.GetType().Name.Equals("Cinemachine" + componentType, System.StringComparison.OrdinalIgnoreCase));
                }
            }

            if (target == null) return new { error = "Component " + componentType + " not found on Object." };

            if (SetFieldOrProperty(target, propertyName, value))
            {
                if (target is Object unityObj) EditorUtility.SetDirty(unityObj);
                return new { success = true, message = "Set " + target.GetType().Name + "." + propertyName + " to " + value };
            }
            
            return new { error = "Property " + propertyName + " not found on " + target.GetType().Name };
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
                
                if (destType.IsEnum)
                {
                    try { return System.Enum.Parse(destType, val.ToString(), true); } catch { }
                }

                try {
                    return JToken.FromObject(val).ToObject(destType);
                } catch {}

                try {
                    return System.Convert.ChangeType(val, destType);
                } catch { return null; }
            }

            // Try Field
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
            var vcam = go.GetComponent<CinemachineCamera>();
            if (vcam == null) return new { error = "Not a CinemachineCamera" };

            if (followName != null) 
                vcam.Follow = GameObject.Find(followName)?.transform;
            if (lookAtName != null) 
                vcam.LookAt = GameObject.Find(lookAtName)?.transform;
                
            return new { success = true };
        }

        [UnitySkill("cinemachine_add_component", "Add a Cinemachine component (e.g., OrbitalFollow).")]
        public static object CinemachineAddComponent(string vcamName, string componentType)
        {
            var go = GameObject.Find(vcamName);
            if (go == null) return new { error = "GameObject not found" };
            
            if (string.IsNullOrEmpty(componentType)) return new { error = "Component Type is empty" };

            // Resolve Type
            var type = FindCinemachineType(componentType);
            if (type == null) return new { error = "Could not find Cinemachine component type: " + componentType };

            // In CM3, we just AddComponent
            var comp = Undo.AddComponent(go, type);
            if (comp != null)
            {
                return new { success = true, message = "Added " + type.Name + " to " + vcamName };
            }
            return new { error = "Failed to add component." };
        }

        private static System.Type FindCinemachineType(string name)
        {
             if (string.IsNullOrEmpty(name)) return null;
             
             // Common short names mapping for CM 3.x
             var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
             {
                 { "OrbitalFollow", "CinemachineOrbitalFollow" },
                 { "Transposer", "CinemachineTransposer" },
                 { "Composer", "CinemachineComposer" },
                 { "HardLockToTarget", "CinemachineHardLockToTarget" }
             };
             
             if (map.TryGetValue(name, out var fullName)) name = fullName;
             if (!name.StartsWith("Cinemachine")) name = "Cinemachine" + name;
             
             // Search in Unity.Cinemachine assembly
             var cmAssembly = typeof(CinemachineCamera).Assembly;
             // Trying with standard Namespace
             var type = cmAssembly.GetType("Unity.Cinemachine." + name, false, true);
             if (type != null) return type;

             return null;
        }
    }
}
