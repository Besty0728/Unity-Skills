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
    /// XR Interaction Toolkit 版本兼容的反射辅助类。
    /// 支持 XRI 2.x（Unity 2022，类型在根命名空间）与 XRI 3.x（Unity 6，类型下沉到子命名空间）。
    /// 所有 XRI API 调用一律走反射——对 XRI 程序集没有编译期依赖。
    /// </summary>
    internal static class XRReflectionHelper
    {
        // ==================================================================================
        // 版本检测（带缓存）
        // ==================================================================================

        private static int? _majorVersion;
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();

        /// <summary>
        /// 探测到的 XRI 主版本：3 = XRI 3.x，2 = XRI 2.x，0 = 未安装。
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
            // XRI 3.x 把类型挪进了子命名空间（如 .Interactors.XRRayInteractor）
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor") != null)
            {
                _majorVersion = 3;
                return;
            }

            // XRI 2.x 的类型仍在根命名空间
            if (FindTypeInAssemblies("UnityEngine.XR.Interaction.Toolkit.XRRayInteractor") != null)
            {
                _majorVersion = 2;
                return;
            }

            _majorVersion = 0;
        }

        /// <summary>
        /// XRI 未安装时的标准错误响应。
        /// </summary>
        public static object NoXRI() => new
        {
            error = "XR Interaction Toolkit package (com.unity.xr.interaction.toolkit) is not installed. " +
                    "Install via: Window > Package Manager > Unity Registry > XR Interaction Toolkit"
        };

        // ==================================================================================
        // 类型映射——短名 -> 全限定名，按 [v3, v2] 的回退顺序排列
        // ==================================================================================

        private static readonly Dictionary<string, string[]> TypeMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 核心类型（两个版本命名空间相同）
            ["XRInteractionManager"] = new[] { "UnityEngine.XR.Interaction.Toolkit.XRInteractionManager" },

            // 交互器
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

            // 可交互物
            ["XRGrabInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable" },
            ["XRSimpleInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRSimpleInteractable" },
            ["XRBaseInteractable"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable",
                "UnityEngine.XR.Interaction.Toolkit.XRBaseInteractable" },

            // 移动——传送
            ["TeleportationProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationProvider",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationProvider" },
            ["TeleportationArea"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationArea",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationArea" },
            ["TeleportationAnchor"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation.TeleportationAnchor",
                "UnityEngine.XR.Interaction.Toolkit.TeleportationAnchor" },

            // 移动——位移
            ["ContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ContinuousMoveProvider" },
            ["ActionBasedContinuousMoveProvider"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement.ActionBasedContinuousMoveProvider",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedContinuousMoveProvider" },

            // 移动——转向
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

            // 移动——系统/调度
            ["LocomotionSystem"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.LocomotionSystem" },
            ["LocomotionMediator"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionMediator" },

            // UI
            ["TrackedDeviceGraphicRaycaster"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster" },
            ["XRUIInputModule"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule" },

            // 输入控制器
            ["ActionBasedController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.ActionBasedController",
                "UnityEngine.XR.Interaction.Toolkit.ActionBasedController" },
            ["XRController"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.XRController",
                "UnityEngine.XR.Interaction.Toolkit.XRController" },

            // XR Origin（来自 com.unity.xr.core-utils）
            ["XROrigin"] = new[] { "Unity.XR.CoreUtils.XROrigin" },

            // 射线可视化
            ["XRInteractorLineVisual"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals.XRInteractorLineVisual",
                "UnityEngine.XR.Interaction.Toolkit.XRInteractorLineVisual" },

            // 交互层
            ["InteractionLayerMask"] = new[] {
                "UnityEngine.XR.Interaction.Toolkit.InteractionLayerMask" },
        };

        // ==================================================================================
        // 类型解析
        // ==================================================================================

        /// <summary>
        /// 在所有已加载程序集中按全名查找类型。
        /// 先用 asm.GetType()，失败再退回全程序集扫描。
        /// </summary>
        public static Type FindTypeInAssemblies(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;
            if (_typeCache.TryGetValue(fullName, out var cached)) return cached;

            // 第一遍：快路径——asm.GetType(fullName)
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

            // 第二遍：回退——用 GetTypes() 全量扫描（覆盖程序集转发/加载的边角情形）
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
        /// 借助版本感知的映射表按短名解析 XR 类型。
        /// 先试 v3 命名空间，再回退到 v2。
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

            // 回退：按简单名扫描所有类型（策略同 ComponentSkills.FindComponentType）
            var fallback = FindTypeBySimpleName(shortName);
            _typeCache[cacheKey] = fallback;
            return fallback;
        }

        /// <summary>
        /// 按简单名扫描所有程序集查找 Component 类型。
        /// 这是最宽的搜索——较慢，但能覆盖程序集加载的边角情形。
        /// </summary>
        private static Type FindTypeBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;

            var cacheKey = $"__simple__{simpleName}";
            if (_typeCache.TryGetValue(cacheKey, out var cached)) return cached;

            Type result = null;

            // 在所有程序集的所有类型里按简单名匹配（忽略大小写）
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
        // 组件操作
        // ==================================================================================

        /// <summary>
        /// 用反射给 GameObject 添加 XR 组件，成功返回该组件，失败返回 null。
        /// 先走 ResolveXRType，失败再退回全程序集扫描。
        /// </summary>
        public static Component AddXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;

            var type = ResolveXRType(typeName);

            // 最后兜底：在所有程序集里按简单名扫描类型
            if (type == null)
                type = FindTypeBySimpleName(typeName);

            if (type == null) return null;

            var existing = go.GetComponent(type);
            if (existing != null) return existing;

            return go.AddComponent(type);
        }

        /// <summary>
        /// 用反射从 GameObject 上取 XR 组件。
        /// </summary>
        public static Component GetXRComponent(GameObject go, string typeName)
        {
            if (go == null) return null;
            var type = ResolveXRType(typeName) ?? FindTypeBySimpleName(typeName);
            if (type == null) return null;
            return go.GetComponent(type);
        }

        /// <summary>
        /// 判断 GameObject 是否挂有某个 XR 组件。
        /// </summary>
        public static bool HasXRComponent(GameObject go, string typeName)
        {
            return GetXRComponent(go, typeName) != null;
        }

        // ==================================================================================
        // 属性访问
        // ==================================================================================

        /// <summary>
        /// 用反射读取对象的属性值。
        /// </summary>
        public static object GetProperty(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            var prop = obj.GetType().GetProperty(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.CanRead)
                return prop.GetValue(obj);

            // 属性取不到时退回字段
            var field = obj.GetType().GetField(propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(obj);
        }

        /// <summary>
        /// 用反射设置对象的属性值，自动完成枚举转换。
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

            // 属性取不到时退回字段
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
        /// 设置枚举类型的属性，按名称解析字符串值。
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
        /// 取某属性可用的枚举值列表。
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
        // 方法调用
        // ==================================================================================

        /// <summary>
        /// 用反射调用对象上的方法。
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
        // 场景查询
        // ==================================================================================

        /// <summary>
        /// 查找场景中某 XR 类型的全部组件。
        /// </summary>
        public static Component[] FindComponentsOfXRType(string typeName)
        {
            var type = ResolveXRType(typeName);
            if (type == null) return Array.Empty<Component>();

            return FindHelper.FindAll(type, includeInactive: true).OfType<Component>().ToArray();
        }

        /// <summary>
        /// 查找场景中某 XR 类型的第一个组件。
        /// </summary>
        public static Component FindFirstOfXRType(string typeName)
        {
            var results = FindComponentsOfXRType(typeName);
            return results.Length > 0 ? results[0] : null;
        }

        /// <summary>
        /// 取某 XR 组件关键属性的可读摘要。
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

            // 读取常见 XR 属性（属性名已对照 XRI 源码核实）
            var commonProps = new[] {
                // 交互器属性
                "interactionLayers", "selectMode", "maxRaycastDistance", "lineType",
                "hitDetectionType", "enableUIInteraction", "useForceGrab", "anchorControl",
                "sphereCastRadius",
                // 可交互物属性
                "movementType", "throwOnDetach", "forceGravityOnDetach",
                "smoothPosition", "smoothPositionAmount", "smoothRotation", "smoothRotationAmount",
                "trackPosition", "trackRotation", "trackScale",
                "useDynamicAttach", "attachEaseInTime", "throwVelocityScale",
                // 移动相关属性
                "moveSpeed", "enableStrafe", "enableFly",
                "turnAmount", "turnSpeed", "enableTurnLeftRight", "enableTurnAround",
                // Socket 属性
                "showInteractableHoverMeshes", "socketActive", "recycleDelayTime",
                "socketSnappingRadius", "socketScaleMode",
                // 运行时状态（只读）
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
        // 值转换
        // ==================================================================================

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;

            // 字符串转枚举
            if (targetType.IsEnum && value is string s)
            {
                try { return Enum.Parse(targetType, s, ignoreCase: true); }
                catch { return null; }
            }

            // 数值类型转换
            try { return Convert.ChangeType(value, targetType); }
            catch { return null; }
        }

        /// <summary>
        /// 清空类型解析缓存（装包或域重载后有用）。
        /// </summary>
        public static void ClearCache()
        {
            _typeCache.Clear();
            _majorVersion = null;
        }
    }
}

// Producer:Betsy
