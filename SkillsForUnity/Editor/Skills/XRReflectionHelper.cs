using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnitySkills.Internal;

namespace UnitySkills
{
    /// <summary>
    /// Version-compatible reflection helper for XR Interaction Toolkit.
    /// Supports XRI 2.x (Unity 2022, types in the root namespace) and XRI 3.x (Unity 6, types moved into sub-namespaces).
    /// Every XRI API call goes through reflection — no compile-time dependency on the XRI assembly.
    /// </summary>
    internal static class XRReflectionHelper
    {
        // ==================================================================================
        // Version detection (cached)
        // ==================================================================================

        private static int? _majorVersion;
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        /// <summary>
        /// The detected XRI major version: 3 = XRI 3.x, 2 = XRI 2.x, 0 = not installed.
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
            // XRI 3.x moved types into a sub-namespace (e.g. .Interactors.XRRayInteractor)
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor") != null)
            {
                _majorVersion = 3;
                return;
            }

            // XRI 2.x types are still in the root namespace
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.XRRayInteractor") != null)
            {
                _majorVersion = 2;
                return;
            }

            _majorVersion = 0;
        }

        /// <summary>
        /// The standard error response when XRI is not installed.
        /// </summary>
        public static object NoXRI() => new
        {
            error = "XR Interaction Toolkit package (com.unity.xr.interaction.toolkit) is not installed. " +
                    "Install via: Window > Package Manager > Unity Registry > XR Interaction Toolkit"
        };

        // ==================================================================================
        // Type map — short name -> fully-qualified names, ordered [v3, v2] as the fallback order
        // ==================================================================================

        private static readonly Dictionary<string, string[]> TypeMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Core types (same namespace across both versions)
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

            // Locomotion — teleportation
            ["TeleportationProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationProvider",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationProvider" },
            ["TeleportationArea"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationArea" },
            ["TeleportationAnchor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationAnchor",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor" },

            // Locomotion — movement
            ["ContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ContinuousMoveProvider" },
            ["ActionBasedContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ActionBasedContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedContinuousMoveProvider" },

            // Locomotion — turning
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

            // Locomotion — system/mediator
            ["LocomotionSystem"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.LocomotionSystem" },
            ["LocomotionMediator"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionMediator" },

            // UI
            ["TrackedDeviceGraphicRaycaster"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster" },
            ["XRUIInputModule"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule" },

            // Input controllers
            ["ActionBasedController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.ActionBasedController",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedController" },
            ["XRController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRController",
                "UnityEngine.XR.Interaction.Toolkit.XRController" },

            // XR Origin (from com.unity.xr.core-utils)
            ["XROrigin"] = new[] { "Unity.XR.CoreUtils.XROrigin" },

            // Ray visualization
            ["XRInteractorLineVisual"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals.XRInteractorLineVisual",
                "UnityEngine.XR.Interaction.Toolkit.XRInteractorLineVisual" },

            // Interaction layers
            ["InteractionLayerMask"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask" },
        };

        // ==================================================================================
        // Type resolution
        // ==================================================================================

        /// <summary>
        /// Looks up a type by full name across all loaded assemblies.
        /// Tries asm.GetType() first, falls back to a full-assembly scan on failure.
        /// </summary>
        public static Type FindTypeInAssemblies(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_typeCache.TryGetValue(fullName, out var cached)) return cached;

            // Pass 1: fast path — asm.GetType(fullName)
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

            // Pass 2: fallback — full scan with GetTypes() (covers assembly-forwarding/loading edge cases)
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
        /// Resolves an XR type by short name using the version-aware type map.
        /// Tries the v3 namespace first, then falls back to v2.
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
        /// Scans all assemblies for a Component type by simple name.
        /// This is the widest search — slower, but covers assembly-loading edge cases.
        /// </summary>
        private static Type FindTypeBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            var cacheKey = $"__simple__{simpleName}";
            if (_typeCache.TryGetValue(cacheKey, out var cached)) return cached;

            Type result = null;

            // Match by simple name (case-insensitive) across every type in every assembly
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
        // Component operations
        // ==================================================================================

        /// <summary>
        /// Adds an XR component to a GameObject via reflection; returns the component on success, null on failure.
        /// Tries ResolveXRType first, falls back to a full-assembly scan on failure.
        /// </summary>
        public static Component AddXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;

            var type = ResolveXRType(typeName);

            // Final fallback: scan all assemblies for the type by simple name
            if (type == null)
                type = FindTypeBySimpleName(typeName);

            if (type == null) return null;

            var existing = go.GetComponent(type);
            if (existing != null) return existing;

            return go.AddComponent(type);
        }

        /// <summary>
        /// Gets an XR component from a GameObject via reflection.
        /// </summary>
        public static Component GetXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;
            var type = ResolveXRType(typeName) ?? FindTypeBySimpleName(typeName);
            if (type == null) return null;
            return go.GetComponent(type);
        }

        /// <summary>
        /// Determines whether a GameObject has a given XR component attached.
        /// </summary>
        public static bool HasXRComponent(GameObject go, string typeName)
        {
            return GetXRComponent(go, typeName) != null;
        }

        // ==================================================================================
        // Property access
        // ==================================================================================

        /// <summary>
        /// Reads an object's property value via reflection.
        /// </summary>
        public static object GetProperty(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(obj);

            // Fall back to a field if the property can't be found
            var field = obj.GetType().GetField(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        /// <summary>
        /// Sets an object's property value via reflection, converting enums automatically.
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

            // Fall back to a field if the property can't be found
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
        /// Sets an enum-typed property, resolving the string value by name.
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
        /// Gets the list of available enum values for a given property.
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
        // Method invocation
        // ==================================================================================

        /// <summary>
        /// Invokes a method on an object via reflection.
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
        // Scene queries
        // ==================================================================================

        /// <summary>
        /// Finds every component of a given XR type in the scene.
        /// </summary>
        public static Component[] FindComponentsOfXRType(string typeName)
        {
            var type = ResolveXRType(typeName);
            if (type == null) return Array.Empty<Component>();

            return FindHelper.FindAll(type, includeInactive: true).OfType<Component>().ToArray();
        }

        /// <summary>
        /// Finds the first component of a given XR type in the scene.
        /// </summary>
        public static Component FindFirstOfXRType(string typeName)
        {
            var results = FindComponentsOfXRType(typeName);
            return results.Length > 0 ? results[0] : null;
        }

        /// <summary>
        /// Gets a readable summary of a given XR component's key properties.
        /// </summary>
        public static Dictionary<string, object> GetComponentInfo(Component comp)
        {
            if (comp == null) return null;
            var info = new Dictionary<string, object>();
            var type = comp.GetType();

            info["type"] = type.Name;
            info["gameObject"] = comp.gameObject.name;
            info["entityId"] = UnityObjectIdUtility.GetEntityId(comp.gameObject);
            info["instanceId"] = UnityObjectIdUtility.GetObjectId(comp.gameObject);
            info["enabled"] = comp is Behaviour b ? b.enabled : true;

            // Read common XR properties (property names verified against the XRI source)
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
                // Locomotion-related properties
                "moveSpeed", "enableStrafe", "enableFly",
                "turnAmount", "turnSpeed", "enableTurnLeftRight", "enableTurnAround",
                // Socket properties
                "showInteractableHoverMeshes", "socketActive", "recycleDelayTime",
                "socketSnappingRadius", "socketScaleMode",
                // Runtime state (read-only)
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
        // Value conversion
        // ==================================================================================

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            // String to enum
            if (targetType.IsEnum && value is string s)
            {
                try { return Enum.Parse(targetType, s, ignoreCase: true); }
                catch { return null; }
            }

            // Numeric type conversion
            try { return Convert.ChangeType(value, targetType); }
            catch { return null; }
        }

        /// <summary>
        /// Clears the type resolution cache (useful after installing a package or a domain reload).
        /// </summary>
        public static void ClearCache()
        {
            _typeCache.Clear();
            _majorVersion = null;
        }
    }
}

// Producer:Betsy
