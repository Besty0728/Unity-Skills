using UnityEngine;
using UnityEditor;
using Cinemachine;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;

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
            
            // Ensure CinemachineBrain exists on Main Camera
            if (Camera.main != null)
            {
                var brain = Camera.main.gameObject.GetComponent<CinemachineBrain>();
                if (brain == null)
                    Camera.main.gameObject.AddComponent<CinemachineBrain>();
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

            // Try Field first
            var field = type.GetField(name, flags);
            if (field != null)
            {
                try 
                {
                    object safeValue = System.Convert.ChangeType(value, field.FieldType);
                    field.SetValue(target, safeValue);
                    return true;
                }
                catch { return false; }
            }

            // Try Property
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    object safeValue = System.Convert.ChangeType(value, prop.PropertyType);
                    prop.SetValue(target, safeValue);
                    return true;
                }
                catch { return false; }
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
    }
}
