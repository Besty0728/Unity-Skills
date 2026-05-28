using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnitySkills
{
    /// <summary>
    /// Reflection helper for XR Interaction Toolkit version compatibility.
    /// Supports XRI 2.x (Unity 2022, root namespace) and XRI 3.x (Unity 6, sub-namespaces).
    /// All XRI API calls go through reflection — no compile-time dependency on XRI assemblies.
    /// </summary>
    internal static class XRReflectionHelper
    {
        // ==================================================================================
        // Version Detection (cached)
        // ==================================================================================

        private static int? _majorVersion;
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        /// <summary>
        /// Detected XRI major version: 3 = XRI 3.x, 2 = XRI 2.x, 0 = not installed.
        /// </summary>
        public static int XRIMajorVersion
        {
            get
            {
                if (!_majorVersion.HasValue) DetectVersion();
                return _majorVersion.Value;
            }
        }

        public static bool IsXRIInstalled => XRIMajorVersion > 0;

        private static void DetectVersion()
        {
            // XRI 3.x moved types into sub-namespaces (e.g. .Interactors.XRRayInteractor)
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor") != null)
            {
                _majorVersion = 3;
                return;
            }

            // XRI 2.x keeps types in root namespace
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.XRRayInteractor") != null)
            {
                _majorVersion = 2;
                return;
            }

            _majorVersion = 0;
        }

        /// <summary>
        /// Standard error response when XRI is not installed.
        /// </summary>
        public static object NoXRI() => new
        {
            error = "XR Interaction Toolkit package (com.unity.xr.interaction.toolkit) is not installed. " +
                    "Install via: Window > Package Manager > Unity Registry > XR Interaction Toolkit"
        };

        // ==================================================================================
        // Type Mapping — maps short names to full qualified names [v3, v2] fallback order
        // ==================================================================================

        private static readonly Dictionary<string, string[]> TypeMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Core (same namespace in both versions)
            ["XRInteractionManager"] = new[] { "UnityEngine.XR.Interaction.Toolkit.XRInteractionManager" },

            // Interactors
            ["XRRayInteractor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor",
                "UnityEngine.XR.Interaction.Toolkit.XRRayInteractor" },
            ["XRDirectInteractor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRDirectInteractor",
                "UnityEngine.XR.Interaction.Toolkit.XRDirectInteractor" },
            ["XRSocketInteractor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRSocketInteractor",
                "UnityEngine.XR.Interaction.Toolkit.XRSocketInteractor" },
            ["NearFarInteractor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.NearFarInteractor" },
            ["XRBaseInteractor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor",
                "UnityEngine.XR.Interaction.Toolkit.XRBaseInteractor" },

            // Interactables
            ["XRGrabInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable" },
            ["XRSimpleInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable" },
            ["XRBaseInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRBaseInteractable" },

            // Locomotion - Teleportation
            ["TeleportationProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationProvider",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationProvider" },
            ["TeleportationArea"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationArea" },
            ["TeleportationAnchor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationAnchor",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor" },

            // Locomotion - Movement
            ["ContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ContinuousMoveProvider" },
            ["ActionBasedContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ActionBasedContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedContinuousMoveProvider" },

            // Locomotion - Turning
            ["SnapTurnProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.SnapTurnProvider",
                "UnityEngine.XR.Interaction.Toolkit.SnapTurnProvider" },
            ["ActionBasedSnapTurnProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.ActionBasedSnapTurnProvider",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedSnapTurnProvider" },
            ["ContinuousTurnProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.ContinuousTurnProvider",
                "UnityEngine.XR.Interaction.Toolkit.ContinuousTurnProvider" },
            ["ActionBasedContinuousTurnProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning.ActionBasedContinuousTurnProvider",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedContinuousTurnProvider" },

            // Locomotion - System/Mediator
            ["LocomotionSystem"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.LocomotionSystem" },
            ["LocomotionMediator"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionMediator" },

            // UI
            ["TrackedDeviceGraphicRaycaster"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster" },
            ["XRUIInputModule"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule" },

            // Input Controllers
            ["ActionBasedController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.ActionBasedController",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedController" },
            ["XRController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRController",
                "UnityEngine.XR.Interaction.Toolkit.XRController" },

            // XR Origin (from com.unity.xr.core-utils)
            ["XROrigin"] = new[] { "Unity.XR.CoreUtils.XROrigin" },

            // Line Visual
            ["XRInteractorLineVisual"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals.XRInteractorLineVisual",
                "UnityEngine.XR.Interaction.Toolkit.XRInteractorLineVisual" },

            // Interaction Layers
            ["InteractionLayerMask"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask" },
        };

        // ==================================================================================
        // Type Resolution
        // ==================================================================================

        /// <summary>
        /// Find a type by full name across all loaded assemblies.
        /// Uses asm.GetType() first, then falls back to full assembly scan.
        /// </summary>
        public static Type FindTypeInAssemblies(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_typeCache.TryGetValue(fullName, out var cached)) return cached;

            // Pass 1: Fast path — asm.GetType(fullName)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null)
                    {
                        _typeCache[fullName] = t;
                        return t;
                    }
                }
                catch { /* ignore assemblies that fail to enumerate */ }
            }

            // Pass 2: Fallback — full scan with GetTypes() (handles assembly forwarding/loading edge cases)
            var shortName = fullName.Contains(".") ? fullName.Substring(fullName.LastIndexOf('.') + 1) : fullName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.FullName == fullName)
                        {
                            _typeCache[fullName] = t;
                            return t;
                        }
                    }
                }
                catch { /* ignore assemblies that fail to enumerate */ }
            }

            _typeCache[fullName] = null;
            return null;
        }

        /// <summary>
        /// Resolve an XR type by short name using the version-aware mapping table.
        /// Tries v3 namespace first, then falls back to v2.
        /// </summary>
        public static Type ResolveXRType(string shortName)
        {
            if (string.IsNullOrEmpty(shortName)) return null;

            var cacheKey = $"__resolve__{shortName}";
            if (_typeCache.TryGetValue(cacheKey, out var cached)) return cached;

            if (TypeMap.TryGetValue(shortName, out var candidates))
            {
                foreach (var fullName in candidates)
                {
                    var t = FindTypeInAssemblies(fullName);
                    if (t != null)
                    {
                        _typeCache[cacheKey] = t;
                        return t;
                    }
                }
            }

            // Fallback: scan all types by simple name (same strategy as ComponentSkills.FindComponentType)
            var fallback = FindTypeBySimpleName(shortName);
            _typeCache[cacheKey] = fallback;
            return fallback;
        }

        /// <summary>
        /// Find a Component type by simple name, scanning all assemblies.
        /// This is the broadest search — slower but handles assembly loading edge cases.
        /// </summary>
        private static Type FindTypeBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            var cacheKey = $"__simple__{simpleName}";
            if (_typeCache.TryGetValue(cacheKey, out var cached)) return cached;

            Type result = null;

            // Search all types in all assemblies by simple name (case-insensitive)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.Equals(simpleName, StringComparison.OrdinalIgnoreCase) &&
                            typeof(Component).IsAssignableFrom(t))
                        {
                            result = t;
                            break;
                        }
                    }
                    if (result != null) break;
                }
                catch { /* ignore assemblies that fail to enumerate */ }
            }

            _typeCache[cacheKey] = result;
            return result;
        }

        // ==================================================================================
        // Component Operations
        // ==================================================================================

        /// <summary>
        /// Add an XR component to a GameObject using reflection.
        /// Returns the added component, or null on failure.
        /// Uses ResolveXRType first, then falls back to full assembly scan.
        /// </summary>
        public static Component AddXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;

            var type = ResolveXRType(typeName);

            // Ultimate fallback: scan all types in all assemblies by simple name
            if (type == null)
                type = FindTypeBySimpleName(typeName);

            if (type == null) return null;

            // Check if component already exists
            var existing = go.GetComponent(type);
            if (existing != null) return existing;

            return go.AddComponent(type);
        }

        /// <summary>
        /// Get an XR component from a GameObject using reflection.
        /// </summary>
        public static Component GetXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;
            var type = ResolveXRType(typeName) ?? FindTypeBySimpleName(typeName);
            if (type == null) return null;
            return go.GetComponent(type);
        }

        /// <summary>
        /// Check if a GameObject has an XR component.
        /// </summary>
        public static bool HasXRComponent(GameObject go, string typeName)
        {
            return GetXRComponent(go, typeName) != null;
        }

        // ==================================================================================
        // Property Access
        // ==================================================================================

        /// <summary>
        /// Get a property value from an object via reflection.
        /// </summary>
        public static object GetProperty(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(obj);

            // Try field as fallback
            var field = obj.GetType().GetField(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        /// <summary>
        /// Set a property value on an object via reflection.
        /// Handles enum conversion automatically.
        /// </summary>
        public static bool SetProperty(object obj, string propName, object value)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return false;

            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                var converted = ConvertValue(value, prop.PropertyType);
                if (converted != null || value == null)
                {
                    prop.SetValue(obj, converted);
                    return true;
                }
            }

            // Try field as fallback
            var field = obj.GetType().GetField(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var converted = ConvertValue(value, field.FieldType);
                if (converted != null || value == null)
                {
                    field.SetValue(obj, converted);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Set a property that is an enum, parsing the string value by name.
        /// </summary>
        public static bool SetEnumProperty(object obj, string propName, string enumValueName)
        {
            if (obj == null || string.IsNullOrEmpty(propName) || string.IsNullOrEmpty(enumValueName))
                return false;

            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return false;

            var enumType = prop.PropertyType;
            if (!enumType.IsEnum) return false;

            try
            {
                var enumValue = Enum.Parse(enumType, enumValueName, ignoreCase: true);
                prop.SetValue(obj, enumValue);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get available enum values for a property.
        /// </summary>
        public static string[] GetEnumValues(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return Array.Empty<string>();

            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop == null || !prop.PropertyType.IsEnum) return Array.Empty<string>();

            return Enum.GetNames(prop.PropertyType);
        }

        // ==================================================================================
        // Method Invocation
        // ==================================================================================

        /// <summary>
        /// Invoke a method on an object via reflection.
        /// </summary>
        public static object InvokeMethod(object obj, string methodName, params object[] args)
        {
            if (obj == null || string.IsNullOrEmpty(methodName)) return null;

            var method = obj.GetType().GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return null;

            return method.Invoke(obj, args);
        }

        // ==================================================================================
        // Scene Query
        // ==================================================================================

        /// <summary>
        /// Find all components of an XR type in the scene.
        /// </summary>
        public static Component[] FindComponentsOfXRType(string typeName)
        {
            var type = ResolveXRType(typeName);
            if (type == null) return Array.Empty<Component>();

#if UNITY_6000_0_OR_NEWER
            return UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OfType<Component>().ToArray();
#else
            return UnityEngine.Object.FindObjectsOfType(type).OfType<Component>().ToArray();
#endif
        }

        /// <summary>
        /// Find the first component of an XR type in the scene.
        /// </summary>
        public static Component FindFirstOfXRType(string typeName)
        {
            var results = FindComponentsOfXRType(typeName);
            return results.Length > 0 ? results[0] : null;
        }

        /// <summary>
        /// Get a readable summary of an XR component's key properties.
        /// </summary>
        public static Dictionary<string, object> GetComponentInfo(Component comp)
        {
            if (comp == null) return null;
            var info = new Dictionary<string, object>();
            var type = comp.GetType();

            info["type"] = type.Name;
            info["gameObject"] = comp.gameObject.name;
            info["instanceId"] = (int)comp.gameObject.GetEntityId();
            info["enabled"] = comp is Behaviour b ? b.enabled : true;

            // Read common XR properties (verified from XRI source code)
            var commonProps = new[] {
                // Interactor properties
                "interactionLayers", "selectMode", "maxRaycastDistance", "lineType",
                "hitDetectionType", "enableUIInteraction", "useForceGrab", "anchorControl",
                "sphereCastRadius",
                // Interactable properties
                "movementType", "throwOnDetach", "forceGravityOnDetach",
                "smoothPosition", "smoothPositionAmount", "smoothRotation", "smoothRotationAmount",
                "trackPosition", "trackRotation", "trackScale",
                "useDynamicAttach", "attachEaseInTime", "throwVelocityScale",
                // Locomotion properties
                "moveSpeed", "enableStrafe", "enableFly",
                "turnAmount", "turnSpeed", "enableTurnLeftRight", "enableTurnAround",
                // Socket properties
                "showInteractableHoverMeshes", "socketActive", "recycleDelayTime",
                "socketSnappingRadius", "socketScaleMode",
                // State (readonly)
                "isSelected", "isHovered"
            };

            foreach (var propName in commonProps)
            {
                try
                {
                    var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanRead)
                    {
                        var val = prop.GetValue(comp);
                        info[propName] = val?.ToString();
                    }
                }
                catch { /* skip inaccessible properties */ }
            }

            return info;
        }

        // ==================================================================================
        // Value Conversion
        // ==================================================================================

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            // Enum conversion from string
            if (targetType.IsEnum && value is string s)
            {
                try { return Enum.Parse(targetType, s, ignoreCase: true); }
                catch { return null; }
            }

            // Numeric conversions
            try { return Convert.ChangeType(value, targetType); }
            catch { return null; }
        }

        /// <summary>
        /// Clear the type resolution cache (useful after package install/domain reload).
        /// </summary>
        public static void ClearCache()
        {
            _typeCache.Clear();
            _majorVersion = null;
        }
    }
}
