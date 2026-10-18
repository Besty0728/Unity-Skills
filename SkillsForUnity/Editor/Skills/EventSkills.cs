using UnityEngine;
using UnityEngine.Events;
using UnityEditor;
using UnityEditor.Events;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Event management skills: inspect and modify UnityEvents (e.g. Button.onClick).
    /// </summary>
    public static class EventSkills
    {
        [UnitySkill("event_get_listeners", "Get persistent listeners of a UnityEvent. Each listener names its target by name, entityId, instanceId (0 on Unity 6000.4+), scene path or asset path, so same-name targets can be told apart.",
            Category = SkillCategory.Event, Operation = SkillOperation.Query,
            Tags = new[] { "event", "listeners", "unityevent", "inspect" },
            Outputs = new[] { "gameObject", "component", "eventName", "listenerCount", "listeners" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EventGetListeners(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;

            var component = FindComponentOnGameObject(go, componentName);
            if (component == null)
                return new { error = $"Component not found: {componentName} on {go.name}" };

            var type = component.GetType();
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            UnityEventBase unityEvent = null;

            if (field != null)
                unityEvent = field.GetValue(component) as UnityEventBase;
            else if (property != null)
                unityEvent = property.GetValue(component) as UnityEventBase;

            if (unityEvent == null)
                return new { error = $"UnityEvent field/property '{eventName}' not found or null on {componentName}" };

            int count = unityEvent.GetPersistentEventCount();
            var listeners = new List<object>();

            for (int i = 0; i < count; i++)
            {
                var target = unityEvent.GetPersistentTarget(i);
                var methodName = unityEvent.GetPersistentMethodName(i);
                var state = unityEvent.GetPersistentListenerState(i);

                var (targetPath, targetAssetPath) = LocateTarget(target);

                listeners.Add(new
                {
                    index = i,
                    target = target != null ? target.name : "null",
                    targetType = target != null ? target.GetType().Name : "null",
                    targetEntityId = UnityObjectIdUtility.GetEntityId(target),
                    targetInstanceId = UnityObjectIdUtility.GetObjectId(target),
                    targetPath,
                    targetAssetPath,
                    method = methodName,
                    state = state.ToString()
                });
            }

            return new
            {
                success = true,
                gameObject = go.name,
                component = componentName,
                eventName = eventName,
                listenerCount = count,
                listeners
            };
        }

        [UnitySkill("event_add_listener", "Add a persistent listener to a UnityEvent (Editor time). Supported args: void, int, float, string, bool, Object. mode: Off/EditorAndRuntime/RuntimeOnly. The target is located by the first of targetEntityId, targetInstanceId, targetPath, targetObjectName that is given; an Object argument by the objectArg* parameters.", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "add", "callback" },
            Outputs = new[] { "message", "index", "mode", "target", "targetType", "targetEntityId", "targetInstanceId", "targetPath", "targetAssetPath", "method", "argType" },
            // Any one of the four target locators in the compound token names the target (a _requiredInputGroups key),
            // so none of them is wire-required; validation refuses a call that gives none.
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "targetObjectName|targetEntityId|targetInstanceId|targetPath", "methodName" },
            MutatesScene = true)]
        public static object EventAddListener(
            string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null,
            string targetObjectName = null, string targetComponentName = null, string methodName = null,
            string mode = "RuntimeOnly",
            [SkillParam("void, int, float, string, bool or object. object takes its argument from objectArgAssetPath (an asset) or objectArgEntityId / objectArgInstanceId / objectArgPath / objectArgName (a scene object).")]
            string argType = "void",
            float floatArg = 0, int intArg = 0, string stringArg = null, bool boolArg = false,
            [SkillParam("Legacy instance id of the target (always 0 on Unity 6000.4+; use targetEntityId). The first target locator given decides, in this order: targetEntityId, targetInstanceId, targetPath, targetObjectName; one that finds nothing is an error, never a fallback to the next.")]
            int targetInstanceId = 0,
            [SkillParam("Entity id of the target: a GameObject, one of its components (targetComponentName naming its type then binds that exact component) or an asset such as a ScriptableObject. event_get_listeners reports it.")]
            string targetEntityId = null,
            [SkillParam("Hierarchy path of the target GameObject, e.g. Canvas/Menu; tells same-name objects apart. Used when no id is given.")]
            string targetPath = null,
            string objectArgName = null, int objectArgInstanceId = 0, string objectArgEntityId = null, string objectArgPath = null,
            [SkillParam("Project path of an asset to pass as the Object argument; when given, the scene locators objectArgName / objectArgInstanceId / objectArgEntityId / objectArgPath are ignored.")]
            string objectArgAssetPath = null,
            [SkillParam("Type bound for the Object argument: a scene reference is the GameObject unless this names a component (Transform, Rigidbody...), whose own id then binds that exact component; for objectArgAssetPath an asset type (Material...).")]
            string objectArgType = null, string entityId = null)
        {
            // mode must be strictly parsed up front: comparing it with two string.Equals calls would
            // let an unrecognized value fall through to RuntimeOnly after the listener is already
            // added, so "EditorOnly" would silently become a runtime listener while reporting success.
            if (!SkillParamUtil.TryParseRequiredEnum<UnityEventCallState>(mode, "mode", out var callState, out var modeError))
                return modeError;
            if (Validate.Required(methodName, "methodName") is object methodErr) return methodErr;

            var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (goErr != null) return goErr;

            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return new { error = $"Source Component not found: {componentName}" };

            // "GameObject" (or no component name) is not a component; in that case the GameObject itself is the target object.
            if (!TryResolveListenerTarget(targetObjectName, targetInstanceId, targetEntityId, targetPath, targetComponentName,
                    out var targetObj, out var targetType, out var targetError))
            {
                return targetError;
            }

            var type = component.GetType();
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            UnityEvent unityEvent = null;

            object rawEvent = null;
            if (field != null) rawEvent = field.GetValue(component);
            else if (property != null) rawEvent = property.GetValue(component);

            if (rawEvent == null)
                return new { error = $"UnityEvent '{eventName}' not found on {componentName}" };

            unityEvent = rawEvent as UnityEvent;
            if (unityEvent == null)
                return new { error = $"Field '{eventName}' is not a standard UnityEvent. Generic events (UnityEvent<T>) not yet supported in this version." };

            var normalizedArgType = (argType ?? "void").Trim().ToLowerInvariant();

            // Whatever can fail on the caller's input (the Object argument, the method, the argType) is settled before anything
            // is recorded, so a bad call leaves no snapshot or Undo entry behind.
            Object objectArg = null;
            if (normalizedArgType == "object" &&
                !TryResolveObjectArgument(objectArgName, objectArgInstanceId, objectArgEntityId, objectArgPath, objectArgAssetPath, objectArgType,
                    "objectArgAssetPath, objectArgEntityId, objectArgInstanceId, objectArgPath or objectArgName",
                    out objectArg, out var objectArgError))
            {
                return objectArgError;
            }

            if (!TryFindListenerMethod(targetType, methodName, normalizedArgType, argType, objectArg, out var methodInfo, out var methodError))
                return methodError;

            WorkflowManager.SnapshotObject(component);
            Undo.RecordObject(component, "Add Event Listener");

            switch (normalizedArgType)
            {
                case "void":
                    UnityEventTools.AddPersistentListener(unityEvent,
                        System.Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo) as UnityAction);
                    break;

                case "float":
                    UnityEventTools.AddFloatPersistentListener(unityEvent,
                        System.Delegate.CreateDelegate(typeof(UnityAction<float>), targetObj, methodInfo) as UnityAction<float>, floatArg);
                    break;

                case "int":
                    UnityEventTools.AddIntPersistentListener(unityEvent,
                        System.Delegate.CreateDelegate(typeof(UnityAction<int>), targetObj, methodInfo) as UnityAction<int>, intArg);
                    break;

                case "string":
                    UnityEventTools.AddStringPersistentListener(unityEvent,
                        System.Delegate.CreateDelegate(typeof(UnityAction<string>), targetObj, methodInfo) as UnityAction<string>, stringArg);
                    break;

                case "bool":
                    UnityEventTools.AddBoolPersistentListener(unityEvent,
                        System.Delegate.CreateDelegate(typeof(UnityAction<bool>), targetObj, methodInfo) as UnityAction<bool>, boolArg);
                    break;

                case "object":
                    if (!TryAddObjectPersistentListener(unityEvent, targetObj, methodInfo, objectArg, out var objectAddError))
                        return new { error = objectAddError };
                    break;
            }

            // The listener just added is guaranteed to be at the end of the list.
            int index = unityEvent.GetPersistentEventCount() - 1;
            unityEvent.SetPersistentListenerState(index, callState);
            EditorUtility.SetDirty(component);

            var (boundPath, boundAssetPath) = LocateTarget(targetObj);
            return new
            {
                success = true,
                message = $"Added listener {(string.IsNullOrWhiteSpace(targetComponentName) ? targetType.Name : targetComponentName)}.{methodName} to {componentName}.{eventName}",
                index,
                mode = callState.ToString(),
                target = targetObj.name,
                targetType = targetType.Name,
                targetEntityId = UnityObjectIdUtility.GetEntityId(targetObj),
                targetInstanceId = UnityObjectIdUtility.GetObjectId(targetObj),
                targetPath = boundPath,
                targetAssetPath = boundAssetPath,
                method = methodName,
                argType = normalizedArgType
            };
        }

        [UnitySkill("event_remove_listener", "Remove a persistent listener by index", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Delete,
            Tags = new[] { "event", "listener", "remove", "delete" },
            Outputs = new[] { "remainingCount" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object EventRemoveListener(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, int index = 0, string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;

            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return new { error = $"Component not found: {componentName}" };

            var type = component.GetType();
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            UnityEventBase unityEvent = null;
            if (field != null) unityEvent = field.GetValue(component) as UnityEventBase;
            else if (property != null) unityEvent = property.GetValue(component) as UnityEventBase;

            if (unityEvent == null) return new { error = "UnityEvent not found" };

            if (Validate.InRange(index, 0, unityEvent.GetPersistentEventCount() - 1, "index") is object rangeErr) return rangeErr;

            WorkflowManager.SnapshotObject(component);
            Undo.RecordObject(component, "Remove Event Listener");
            UnityEventTools.RemovePersistentListener(unityEvent, index);

            return new { success = true, remainingCount = unityEvent.GetPersistentEventCount() };
        }

        [UnitySkill("event_invoke", "Invoke a UnityEvent explicitly (Runtime only)",
            Category = SkillCategory.Event, Operation = SkillOperation.Execute,
            Tags = new[] { "event", "invoke", "trigger", "runtime" },
            Outputs = new[] { "message" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            // Firing itself writes nothing, but it executes whatever the persistent listeners do to
            // the scene, with no snapshot and no way to undo it.
            // Only by declaring this a scene mutation does the surface profile's "no scene edits" guarantee hold up.
            MutatesScene = true)]
        public static object EventInvoke(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, string entityId = null)
        {
             var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (goErr != null) return goErr;

            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return new { error = $"Component not found: {componentName}" };

            var type = component.GetType();
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            UnityEventBase unityEvent = null;
            if (field != null) unityEvent = field.GetValue(component) as UnityEventBase;
            else if (property != null) unityEvent = property.GetValue(component) as UnityEventBase;

             if (unityEvent == null) return new { error = "UnityEvent not found" };
            
            var invokeMethod = unityEvent.GetType().GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public);
            if (invokeMethod == null)
                return new { error = "Could not find Invoke method on event" };
                
            try
            {
                invokeMethod.Invoke(unityEvent, null);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                return new { error = $"Event invoke failed: {(ex.InnerException ?? ex).Message}" };
            }

            return new { success = true, message = "Event invoked" };
        }

        private static (UnityEventBase evt, Component comp, object error) FindEvent(string name, int instanceId, string path, string componentName, string eventName, string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return (null, null, findErr);
            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return (null, null, new { error = $"Component not found: {componentName}" });
            var type = component.GetType();
            var field = type.GetField(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var property = type.GetProperty(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            UnityEventBase evt = null;
            if (field != null) evt = field.GetValue(component) as UnityEventBase;
            else if (property != null) evt = property.GetValue(component) as UnityEventBase;
            if (evt == null) return (null, null, new { error = $"UnityEvent '{eventName}' not found" });
            return (evt, component, null);
        }

        /// <summary>
        /// Locates the listener's target with <see cref="SerializedPropertySkillUtility.TryFindObject"/>: the first of
        /// entityId, instance id, path and name that is given decides, and one that finds nothing is an error, never a fallback
        /// to the next (a stale id must not bind a same-name object). An id may name a component, which a
        /// <paramref name="targetComponentName"/> of its own type (or a base type) binds exactly, or an asset, which is bound as it
        /// is. A blank component name (or "GameObject") targets the GameObject itself. <paramref name="error"/> is a
        /// ready-to-return skill result.
        /// </summary>
        private static bool TryResolveListenerTarget(
            string targetName,
            int targetInstanceId,
            string targetEntityId,
            string targetPath,
            string targetComponentName,
            out Object targetObj,
            out System.Type targetType,
            out object error)
        {
            targetObj = null;
            targetType = null;
            error = null;

            // Validation already refuses a call with no locator; in-process callers such as batch items reach this check instead.
            if (string.IsNullOrWhiteSpace(targetName) && targetInstanceId == 0 && string.IsNullOrWhiteSpace(targetEntityId) && string.IsNullOrWhiteSpace(targetPath))
            {
                error = new
                {
                    error = "A target is required: pass one of the target name, targetEntityId, targetInstanceId or targetPath",
                    errorCode = SkillErrorCode.MissingParam.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry,
                    suggestedFixes = new[] { new { action = "fix_param", reason = "Name the GameObject that holds the method; targetEntityId or targetPath tells same-name objects apart." } }
                };
                return false;
            }

            if (!SerializedPropertySkillUtility.TryFindObject(targetName, targetInstanceId, targetEntityId, targetPath, "Target object", out var found, out error))
                return false;

            var namedComponent = found as Component;
            var targetGo = found as GameObject ?? namedComponent?.gameObject;
            var wantsGameObject = string.IsNullOrWhiteSpace(targetComponentName) || IsGameObjectTypeName(targetComponentName);

            if (targetGo == null)
            {
                // An id named an asset (a ScriptableObject, a Material...): the listener binds it as it is.
                if (!string.IsNullOrWhiteSpace(targetComponentName) && !MatchesTypeName(found.GetType(), targetComponentName))
                {
                    error = new { error = $"Target '{found.name}' is a {found.GetType().Name}, not a {targetComponentName}" };
                    return false;
                }

                targetObj = found;
                targetType = found.GetType();
                return true;
            }

            if (wantsGameObject)
            {
                targetObj = targetGo;
                targetType = typeof(GameObject);
                return true;
            }

            // A component's own id names that component, even when its GameObject carries several of the type.
            var targetComponent = namedComponent != null && MatchesTypeName(namedComponent.GetType(), targetComponentName)
                ? namedComponent
                : FindComponentOnGameObject(targetGo, targetComponentName);
            if (targetComponent == null)
            {
                error = new { error = $"Target Component not found: {targetComponentName}" };
                return false;
            }

            targetObj = targetComponent;
            targetType = targetComponent.GetType();
            return true;
        }

        private static bool IsGameObjectTypeName(string typeName) =>
            string.Equals(typeName, "GameObject", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(typeName, "UnityEngine.GameObject", System.StringComparison.OrdinalIgnoreCase);

        // Whether a type name (a simple or full name) names the type or one of its base types.
        private static bool MatchesTypeName(System.Type actual, string typeName)
        {
            var named = SerializedPropertySkillUtility.ResolveUnityObjectType(typeName);
            return named != null
                ? named.IsAssignableFrom(actual)
                : string.Equals(actual.Name, typeName, System.StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(actual.FullName, typeName, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Where a listener's target lives: the hierarchy path of a scene object, or the asset path of an asset
        /// (a ScriptableObject, a prefab asset...); the one that does not apply is null, and so are both for a destroyed target.
        /// </summary>
        private static (string path, string assetPath) LocateTarget(Object target)
        {
            // A destroyed target compares equal to null but is not a null reference, so it is checked before .gameObject is read.
            if (target == null)
                return (null, null);

            var assetPath = AssetDatabase.GetAssetPath(target);
            if (!string.IsNullOrEmpty(assetPath))
                return (null, assetPath);

            var go = target as GameObject ?? (target as Component)?.gameObject;
            return (go != null ? GameObjectFinder.GetPath(go) : null, null);
        }

        /// <summary>
        /// Resolves the Object argument of an argType "object" listener with
        /// <see cref="SerializedPropertySkillUtility.TryResolveObjectReferenceDetailed"/>, so it is located as strictly as the
        /// target is. Asking for one without any locator is its own error, naming <paramref name="locatorHint"/>.
        /// </summary>
        private static bool TryResolveObjectArgument(
            string name, int instanceId, string entityId, string path, string assetPath, string objectType,
            string locatorHint, out Object argument, out object error)
        {
            argument = null;
            error = null;
            if (string.IsNullOrWhiteSpace(name) && instanceId == 0 && string.IsNullOrWhiteSpace(entityId) &&
                string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(assetPath))
            {
                error = new
                {
                    error = $"argType 'object' needs the argument: pass {locatorHint}",
                    errorCode = SkillErrorCode.MissingParam.ToWireString(),
                    retryStrategy = SkillErrorResponse.RetryFixAndRetry
                };
                return false;
            }

            return SerializedPropertySkillUtility.TryResolveObjectReferenceDetailed(
                name, instanceId, path, assetPath, objectType, entityId, out argument, out error);
        }

        /// <summary>
        /// The method a listener of this argType calls on <paramref name="targetType"/>, settled before anything is recorded.
        /// <paramref name="argType"/> is the lower-cased kind, <paramref name="requestedArgType"/> what the caller wrote.
        /// </summary>
        private static bool TryFindListenerMethod(System.Type targetType, string methodName, string argType, string requestedArgType,
            Object objectArg, out MethodInfo method, out object error)
        {
            method = null;
            error = null;

            System.Type[] parameterTypes;
            string signature;
            switch (argType)
            {
                case "void": parameterTypes = System.Type.EmptyTypes; signature = string.Empty; break;
                case "float": parameterTypes = new[] { typeof(float) }; signature = "float"; break;
                case "int": parameterTypes = new[] { typeof(int) }; signature = "int"; break;
                case "string": parameterTypes = new[] { typeof(string) }; signature = "string"; break;
                case "bool": parameterTypes = new[] { typeof(bool) }; signature = "bool"; break;
                case "object":
                    method = FindObjectArgumentMethod(targetType, methodName, objectArg);
                    if (method == null)
                    {
                        error = new { error = $"Compatible object method '{methodName}(Object)' not found on {targetType.Name}" };
                        return false;
                    }
                    return true;
                default:
                    error = new { error = $"Unsupported argType: {requestedArgType}" };
                    return false;
            }

            method = FindTargetMethod(targetType, methodName, parameterTypes);
            if (method == null)
            {
                error = new { error = $"Method '{methodName}({signature})' not found on {targetType.Name}" };
                return false;
            }

            return true;
        }

        private static Component FindComponentOnGameObject(GameObject go, string componentName)
        {
            if (go == null || string.IsNullOrWhiteSpace(componentName))
                return null;

            var componentType = ComponentSkills.FindComponentType(componentName);
            if (componentType != null)
            {
                var component = go.GetComponent(componentType);
                if (component != null)
                    return component;
            }

            var byName = go.GetComponent(componentName);
            if (byName != null)
                return byName;

            return go.GetComponents<Component>()
                .FirstOrDefault(component =>
                    component != null &&
                    (string.Equals(component.GetType().Name, componentName, System.StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(component.GetType().FullName, componentName, System.StringComparison.OrdinalIgnoreCase)));
        }

        private static MethodInfo FindTargetMethod(System.Type targetType, string methodName, System.Type[] parameterTypes)
        {
            var method = targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, parameterTypes, null);
            if (method != null) return method;

            if (methodName != null && methodName.StartsWith("set_", System.StringComparison.Ordinal))
            {
                var property = targetType.GetProperty(methodName.Substring(4), BindingFlags.Instance | BindingFlags.Public);
                var setter = property?.GetSetMethod();
                if (setter != null)
                {
                    var parameters = setter.GetParameters();
                    if (parameters.Length == parameterTypes.Length &&
                        parameters.Select(p => p.ParameterType).SequenceEqual(parameterTypes))
                    {
                        return setter;
                    }
                }
            }

            return null;
        }

        // Of the one-argument overloads that accept the argument, the one whose parameter type is nearest to the argument's own type
        // (Foo(Transform) over Foo(Component) over Foo(Object) for a Transform). With no argument, the first in reflection order.
        internal static MethodInfo FindObjectArgumentMethod(System.Type targetType, string methodName, Object argument)
        {
            var argumentType = argument != null ? argument.GetType() : null;
            return targetType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, methodName, System.StringComparison.Ordinal))
                .Select(m => new { Method = m, Parameters = m.GetParameters() })
                .Where(x => x.Parameters.Length == 1 && typeof(Object).IsAssignableFrom(x.Parameters[0].ParameterType))
                .Where(x => argumentType == null || x.Parameters[0].ParameterType.IsAssignableFrom(argumentType))
                .OrderBy(x => InheritanceDistance(argumentType, x.Parameters[0].ParameterType))
                .Select(x => x.Method)
                .FirstOrDefault();
        }

        // How many base classes above argumentType the parameter type sits (0 = exactly it).
        private static int InheritanceDistance(System.Type argumentType, System.Type parameterType)
        {
            var distance = 0;
            for (var type = argumentType; type != null; type = type.BaseType, distance++)
            {
                if (type == parameterType)
                    return distance;
            }

            return int.MaxValue;
        }

        private static bool TryRegisterObjectPersistentListener(UnityEventBase evt, int index, Object targetObj, MethodInfo method, Object argument, out string error)
        {
            error = null;
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                var delegateType = typeof(UnityAction<>).MakeGenericType(parameterType);
                var listener = System.Delegate.CreateDelegate(delegateType, targetObj, method);
                var registerMethod = typeof(UnityEventTools)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "RegisterObjectPersistentListener" &&
                        m.IsGenericMethodDefinition &&
                        m.GetParameters().Length == 4);
                if (registerMethod == null)
                {
                    error = "UnityEventTools.RegisterObjectPersistentListener<T> not found";
                    return false;
                }

                registerMethod.MakeGenericMethod(parameterType)
                    .Invoke(null, new object[] { evt, index, listener, argument });
                return true;
            }
            catch (System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Reflects into UnityEventBase's internal PersistentCallGroup/PersistentCall/ArgumentCache classes
        /// to read a given persistent call's argument type and value. Uses the same field names that
        /// UnityEventDrawer itself relies on, which stay stable across Unity versions. Every lookup here
        /// goes by string name, so a version mismatch can never cause a compile failure: the worst case
        /// is just returning "unknown", and the caller falls back to looking up the parameterless overload
        /// instead of throwing.
        /// </summary>
        private static (string argType, Object objectArg, int intArg, float floatArg, string stringArg, bool boolArg) ReadPersistentCallArguments(UnityEventBase evt, int index)
        {
            try
            {
                var callsField = typeof(UnityEventBase).GetField("m_PersistentCalls", BindingFlags.Instance | BindingFlags.NonPublic);
                var callGroup = callsField?.GetValue(evt);
                if (callGroup == null) return ("unknown", null, 0, 0f, null, false);

                var callsListField = callGroup.GetType().GetField("m_Calls", BindingFlags.Instance | BindingFlags.NonPublic);
                if (!(callsListField?.GetValue(callGroup) is System.Collections.IList callsList) || index < 0 || index >= callsList.Count)
                    return ("unknown", null, 0, 0f, null, false);

                var call = callsList[index];
                if (call == null) return ("unknown", null, 0, 0f, null, false);

                var callType = call.GetType();
                var mode = callType.GetField("m_Mode", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(call);
                var args = callType.GetField("m_Arguments", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(call);
                if (mode == null || args == null) return ("unknown", null, 0, 0f, null, false);

                var argsType = args.GetType();
                object ReadRaw(string field) => argsType.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(args);

                switch (mode.ToString())
                {
                    case "Void": return ("void", null, 0, 0f, null, false);
                    case "Object": return ("object", ReadRaw("m_ObjectArgument") as Object, 0, 0f, null, false);
                    case "Int": return ("int", null, (int)(ReadRaw("m_IntArgument") ?? 0), 0f, null, false);
                    case "Float": return ("float", null, 0, (float)(ReadRaw("m_FloatArgument") ?? 0f), null, false);
                    case "String": return ("string", null, 0, 0f, ReadRaw("m_StringArgument") as string, false);
                    case "Bool": return ("bool", null, 0, 0f, null, (bool)(ReadRaw("m_BoolArgument") ?? false));
                    default: return ("unknown", null, 0, 0f, null, false); // EventDefined or an unrecognized mode
                }
            }
            catch
            {
                return ("unknown", null, 0, 0f, null, false);
            }
        }

        /// <summary>
        /// Appends a persistent listener of Object type. Prefers UnityEventTools.AddObjectPersistentListener&lt;T&gt;;
        /// when that's unavailable, falls back to the same two-step approach as the Inspector's "+" button: first use
        /// AddPersistentListener(UnityEventBase) to append an empty persistent call, then fill it in at that
        /// index with RegisterObjectPersistentListener&lt;T&gt; (implemented by
        /// <see cref="TryRegisterObjectPersistentListener"/>). Both lookups go through reflection,
        /// so on Unity versions missing the overload this only reports an error rather than failing to compile.
        /// </summary>
        private static bool TryAddObjectPersistentListener(UnityEventBase evt, Object targetObj, MethodInfo method, Object argument, out string error)
        {
            error = null;
            try
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                var delegateType = typeof(UnityAction<>).MakeGenericType(parameterType);
                var listener = System.Delegate.CreateDelegate(delegateType, targetObj, method);

                var addObjectMethod = typeof(UnityEventTools)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m =>
                        m.Name == "AddObjectPersistentListener" &&
                        m.IsGenericMethodDefinition &&
                        m.GetParameters().Length == 3);
                if (addObjectMethod != null)
                {
                    addObjectMethod.MakeGenericMethod(parameterType).Invoke(null, new object[] { evt, listener, argument });
                    return true;
                }

                var addBlank = typeof(UnityEventTools)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "AddPersistentListener" && m.GetParameters().Length == 1);
                if (addBlank == null)
                {
                    error = "Neither UnityEventTools.AddObjectPersistentListener<T> nor AddPersistentListener(UnityEventBase) was found";
                    return false;
                }

                addBlank.Invoke(null, new object[] { evt });
                int newIndex = evt.GetPersistentEventCount() - 1;
                if (!TryRegisterObjectPersistentListener(evt, newIndex, targetObj, method, argument, out error))
                {
                    UnityEventTools.RemovePersistentListener(evt, newIndex);
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                error = (ex.InnerException ?? ex).Message;
                return false;
            }
        }

        [UnitySkill("event_clear_listeners", "Remove all persistent listeners from a UnityEvent", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Delete,
            Tags = new[] { "event", "clear", "listeners", "remove" },
            Outputs = new[] { "removed" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            MutatesScene = true,
            RiskLevel = "medium")]
        public static object EventClearListeners(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, string entityId = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName, entityId);
            if (err != null) return err;
            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Clear Listeners");
            int count = evt.GetPersistentEventCount();
            for (int i = count - 1; i >= 0; i--)
                UnityEventTools.RemovePersistentListener(evt, i);
            return new { success = true, removed = count };
        }

        [UnitySkill("event_set_listener_state", "Set a listener's call state (Off, RuntimeOnly, EditorAndRuntime)", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "state", "callstate" },
            Outputs = new[] { "index", "state" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            MutatesScene = true)]
        public static object EventSetListenerState(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, int index = 0, string state = null, string entityId = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName, entityId);
            if (err != null) return err;
            if (index < 0 || index >= evt.GetPersistentEventCount()) return new { error = "Index out of range" };
            if (!SkillParamUtil.TryParseRequiredEnum<UnityEventCallState>(state, "state", out var callState, out var stateError)) return stateError;
            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Listener State");
            evt.SetPersistentListenerState(index, callState);
            return new { success = true, index, state = callState.ToString() };
        }

        [UnitySkill("event_set_listener", "Replace a persistent UnityEvent listener at a specific index. Supports void/int/float/string/bool/Object static arguments. The target is located by the first of targetEntityId, targetInstanceId, targetPath, targetName that is given.",
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "set", "replace", "persistent" },
            Outputs = new[] { "index", "target", "targetEntityId", "targetInstanceId", "targetPath", "targetAssetPath", "method", "state", "argType" },
            // As for event_add_listener, the target locators are alternatives (a _requiredInputGroups key) and none is wire-required.
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "targetName|targetEntityId|targetInstanceId|targetPath", "methodName" },
            TracksWorkflow = true, MutatesScene = true)]
        public static object EventSetListener(
            string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null,
            int index = 0,
            string targetName = null, int targetInstanceId = 0, string targetPath = null,
            string targetComponentName = null, string methodName = null,
            string mode = "RuntimeOnly",
            string argType = "void",
            float floatArg = 0, int intArg = 0, string stringArg = null, bool boolArg = false,
            string objectReferenceName = null, int objectReferenceInstanceId = 0, string objectReferencePath = null,
            string objectAssetPath = null, string objectType = null,
            [SkillParam("Entity id of the target: a GameObject, one of its components (targetComponentName naming its type then binds that exact component) or an asset such as a ScriptableObject. The first target locator given decides, in this order: targetEntityId, targetInstanceId, targetPath, targetName; one that finds nothing is an error, never a fallback to the next.")]
            string targetEntityId = null,
            [SkillParam("Entity id of the scene object passed as the Object argument (a component's id with objectType naming its type binds that exact component); used when objectAssetPath is empty, and an id that names nothing is an error.")]
            string objectReferenceEntityId = null, string entityId = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName, entityId);
            if (err != null) return err;
            if (index < 0 || index >= evt.GetPersistentEventCount()) return new { error = "Index out of range" };
            if (Validate.Required(methodName, "methodName") is object methodErr) return methodErr;

            if (!TryResolveListenerTarget(targetName, targetInstanceId, targetEntityId, targetPath, targetComponentName,
                    out var targetObj, out var targetType, out var targetError))
            {
                return targetError;
            }

            if (!SkillParamUtil.TryParseRequiredEnum<UnityEventCallState>(mode, "mode", out var callState, out var modeError))
            {
                return modeError;
            }

            var normalizedArgType = (argType ?? "void").Trim().ToLowerInvariant();

            // Whatever can fail on the caller's input (the Object argument, the method, the argType) is settled before anything
            // is recorded, so a bad call leaves no snapshot or Undo entry behind.
            Object objectArg = null;
            if (normalizedArgType == "object" &&
                !TryResolveObjectArgument(
                    objectReferenceName, objectReferenceInstanceId, objectReferenceEntityId, objectReferencePath, objectAssetPath, objectType,
                    "objectAssetPath, objectReferenceEntityId, objectReferenceInstanceId, objectReferencePath or objectReferenceName",
                    out objectArg, out var objectArgError))
            {
                return objectArgError;
            }

            if (normalizedArgType == "void" && !(evt is UnityEvent))
                return new { error = "Void listener replacement requires a standard UnityEvent" };

            if (!TryFindListenerMethod(targetType, methodName, normalizedArgType, argType, objectArg, out var methodInfo, out var methodError))
                return methodError;

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Event Listener");

            switch (normalizedArgType)
            {
                case "void":
                    UnityEventTools.RegisterPersistentListener((UnityEvent)evt, index,
                        System.Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo) as UnityAction);
                    break;

                case "float":
                    UnityEventTools.RegisterFloatPersistentListener(evt, index,
                        System.Delegate.CreateDelegate(typeof(UnityAction<float>), targetObj, methodInfo) as UnityAction<float>, floatArg);
                    break;

                case "int":
                    UnityEventTools.RegisterIntPersistentListener(evt, index,
                        System.Delegate.CreateDelegate(typeof(UnityAction<int>), targetObj, methodInfo) as UnityAction<int>, intArg);
                    break;

                case "string":
                    UnityEventTools.RegisterStringPersistentListener(evt, index,
                        System.Delegate.CreateDelegate(typeof(UnityAction<string>), targetObj, methodInfo) as UnityAction<string>, stringArg);
                    break;

                case "bool":
                    UnityEventTools.RegisterBoolPersistentListener(evt, index,
                        System.Delegate.CreateDelegate(typeof(UnityAction<bool>), targetObj, methodInfo) as UnityAction<bool>, boolArg);
                    break;

                case "object":
                    if (!TryRegisterObjectPersistentListener(evt, index, targetObj, methodInfo, objectArg, out var registerError))
                        return new { error = registerError };
                    break;
            }

            evt.SetPersistentListenerState(index, callState);
            EditorUtility.SetDirty(comp);

            var (boundPath, boundAssetPath) = LocateTarget(targetObj);
            return new
            {
                success = true,
                index,
                target = targetObj != null ? targetObj.name : "null",
                targetType = targetType.Name,
                targetEntityId = UnityObjectIdUtility.GetEntityId(targetObj),
                targetInstanceId = UnityObjectIdUtility.GetObjectId(targetObj),
                targetPath = boundPath,
                targetAssetPath = boundAssetPath,
                method = methodName,
                state = callState.ToString(),
                argType = normalizedArgType
            };
        }

        [UnitySkill("event_list_events", "List all UnityEvent fields on a component",
            Category = SkillCategory.Event, Operation = SkillOperation.Query,
            Tags = new[] { "event", "list", "fields", "component" },
            Outputs = new[] { "component", "count", "events" },
            RequiresInput = new[] { "gameObject", "componentName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EventListEvents(string name = null, int instanceId = 0, string path = null, string componentName = null, string entityId = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path, entityId: entityId);
            if (findErr != null) return findErr;
            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return new { error = $"Component not found: {componentName}" };
            var type = component.GetType();
            var events = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(UnityEventBase).IsAssignableFrom(f.FieldType))
                .Select(f => { var e = f.GetValue(component) as UnityEventBase; return new { name = f.Name, type = f.FieldType.Name, listenerCount = e?.GetPersistentEventCount() ?? 0 }; })
                .ToArray();
            return new { success = true, component = componentName, count = events.Length, events };
        }

        [UnitySkill("event_add_listener_batch", "Add multiple listeners at once. items: JSON array of {targetObjectName, targetComponentName, methodName, mode, argType, floatArg, intArg, stringArg, boolArg, targetEntityId, targetInstanceId, targetPath, objectArgName, objectArgEntityId, objectArgInstanceId, objectArgPath, objectArgAssetPath, objectArgType}", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "batch", "bulk" },
            Outputs = new[] { "added", "total", "failed", "results" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "items" },
            MutatesScene = true)]
        public static object EventAddListenerBatch(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null,
            [SkillParam("JSON array of {targetObjectName|targetEntityId|targetInstanceId|targetPath, targetComponentName?, methodName, mode? (default RuntimeOnly), argType? (default void), floatArg?, intArg?, stringArg?, boolArg?, objectArgName|objectArgEntityId|objectArgInstanceId|objectArgPath|objectArgAssetPath?, objectArgType?}, each as in event_add_listener.")]
            string items = null, string entityId = null)
        {
            var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchListenerItem>>(items);
            if (list == null || list.Count == 0) return new { error = "No items provided" };
            int added = 0;
            int failed = 0;
            var results = new List<object>();
            foreach (var item in list)
            {
                // Each item must go through the single-item overload with its full signature: forwarding
                // only targetObjectName/targetComponentName/methodName would, whenever argType isn't "void"
                // (e.g. GameObject.SetActive(bool)), match against a parameterless overload that doesn't
                // exist, then silently drop the failure below — added doesn't increment, the failure reason
                // is lost, and the batch still reports success:true.
                var result = EventAddListener(
                    name, instanceId, path, componentName, eventName,
                    item.targetObjectName, item.targetComponentName, item.methodName,
                    string.IsNullOrEmpty(item.mode) ? "RuntimeOnly" : item.mode,
                    string.IsNullOrEmpty(item.argType) ? "void" : item.argType,
                    item.floatArg, item.intArg, item.stringArg, item.boolArg,
                    targetInstanceId: item.targetInstanceId, targetEntityId: item.targetEntityId, targetPath: item.targetPath,
                    objectArgName: item.objectArgName, objectArgInstanceId: item.objectArgInstanceId, objectArgEntityId: item.objectArgEntityId,
                    objectArgPath: item.objectArgPath, objectArgAssetPath: item.objectArgAssetPath, objectArgType: item.objectArgType,
                    entityId: entityId);

                var label = item.targetObjectName ?? item.targetPath ?? item.targetEntityId ?? (item.targetInstanceId != 0 ? item.targetInstanceId.ToString() : null);
                if (SkillResultHelper.TryGetError(result, out var itemError))
                {
                    failed++;
                    results.Add(new { target = label, method = item.methodName, success = false, error = itemError });
                }
                else
                {
                    added++;
                    results.Add(new { target = label, method = item.methodName, success = true });
                }
            }
            return new { success = failed == 0, added, failed, total = list.Count, results };
        }

        private class BatchListenerItem
        {
            public string targetObjectName { get; set; }
            public string targetComponentName { get; set; }
            public string methodName { get; set; }
            public string mode { get; set; }
            public string argType { get; set; }
            public float floatArg { get; set; }
            public int intArg { get; set; }
            public string stringArg { get; set; }
            public bool boolArg { get; set; }
            public int targetInstanceId { get; set; }
            public string targetEntityId { get; set; }
            public string targetPath { get; set; }
            public string objectArgName { get; set; }
            public int objectArgInstanceId { get; set; }
            public string objectArgEntityId { get; set; }
            public string objectArgPath { get; set; }
            public string objectArgAssetPath { get; set; }
            public string objectArgType { get; set; }
        }

        [UnitySkill("event_copy_listeners", "Copy listeners from one event to another, preserving each listener's argument type and value (void/int/float/string/bool/Object)", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "copy", "listeners", "duplicate" },
            Outputs = new[] { "copied", "skipped", "failures" },
            RequiresInput = new[] { "sourceObject", "sourceComponent", "sourceEvent", "targetObject", "targetComponent", "targetEvent" },
            MutatesScene = true)]
        public static object EventCopyListeners(string sourceObject, string sourceComponent, string sourceEvent,
            string targetObject, string targetComponent, string targetEvent)
        {
            var (srcEvt, srcComp, srcErr) = FindEvent(sourceObject, 0, null, sourceComponent, sourceEvent);
            if (srcErr != null) return srcErr;
            var (tgtEvt, tgtComp, tgtErr) = FindEvent(targetObject, 0, null, targetComponent, targetEvent);
            if (tgtErr != null) return tgtErr;
            if (!(tgtEvt is UnityEvent tgtUnityEvent)) return new { error = "Target must be a standard UnityEvent" };
            WorkflowManager.SnapshotObject(tgtComp);
            Undo.RecordObject(tgtComp, "Copy Listeners");
            int copied = 0;
            var failures = new List<object>();
            for (int i = 0; i < srcEvt.GetPersistentEventCount(); i++)
            {
                var target = srcEvt.GetPersistentTarget(i);
                var method = srcEvt.GetPersistentMethodName(i);
                if (target == null)
                {
                    failures.Add(new { index = i, method, reason = "Source listener target is missing/destroyed" });
                    continue;
                }

                // Must look up the overload by the actual argument type: looking up only the parameterless
                // overload (Type.EmptyTypes) would resolve a listener with a stored argument (a common case,
                // e.g. GameObject.SetActive(bool)) to null and silently skip it, with copied not incrementing
                // and no explanation given.
                var (argType, objectArg, intArg, floatArg, stringArg, boolArg) = ReadPersistentCallArguments(srcEvt, i);
                var targetType = target.GetType();
                string failReason = null;

                try
                {
                    switch (argType)
                    {
                        case "void":
                        case "unknown": // Internal structure couldn't be read; fall back to treating it as parameterless
                        {
                            var mi = FindTargetMethod(targetType, method, System.Type.EmptyTypes);
                            if (mi == null) { failReason = $"Method '{method}()' not found on {targetType.Name}"; break; }
                            var del = System.Delegate.CreateDelegate(typeof(UnityAction), target, mi) as UnityAction;
                            UnityEventTools.AddPersistentListener(tgtUnityEvent, del);
                            break;
                        }
                        case "int":
                        {
                            var mi = FindTargetMethod(targetType, method, new[] { typeof(int) });
                            if (mi == null) { failReason = $"Method '{method}(int)' not found on {targetType.Name}"; break; }
                            var del = System.Delegate.CreateDelegate(typeof(UnityAction<int>), target, mi) as UnityAction<int>;
                            UnityEventTools.AddIntPersistentListener(tgtUnityEvent, del, intArg);
                            break;
                        }
                        case "float":
                        {
                            var mi = FindTargetMethod(targetType, method, new[] { typeof(float) });
                            if (mi == null) { failReason = $"Method '{method}(float)' not found on {targetType.Name}"; break; }
                            var del = System.Delegate.CreateDelegate(typeof(UnityAction<float>), target, mi) as UnityAction<float>;
                            UnityEventTools.AddFloatPersistentListener(tgtUnityEvent, del, floatArg);
                            break;
                        }
                        case "string":
                        {
                            var mi = FindTargetMethod(targetType, method, new[] { typeof(string) });
                            if (mi == null) { failReason = $"Method '{method}(string)' not found on {targetType.Name}"; break; }
                            var del = System.Delegate.CreateDelegate(typeof(UnityAction<string>), target, mi) as UnityAction<string>;
                            UnityEventTools.AddStringPersistentListener(tgtUnityEvent, del, stringArg);
                            break;
                        }
                        case "bool":
                        {
                            var mi = FindTargetMethod(targetType, method, new[] { typeof(bool) });
                            if (mi == null) { failReason = $"Method '{method}(bool)' not found on {targetType.Name}"; break; }
                            var del = System.Delegate.CreateDelegate(typeof(UnityAction<bool>), target, mi) as UnityAction<bool>;
                            UnityEventTools.AddBoolPersistentListener(tgtUnityEvent, del, boolArg);
                            break;
                        }
                        case "object":
                        {
                            var mi = FindObjectArgumentMethod(targetType, method, objectArg);
                            if (mi == null) { failReason = $"Compatible object method '{method}(Object)' not found on {targetType.Name}"; break; }
                            if (!TryAddObjectPersistentListener(tgtUnityEvent, target, mi, objectArg, out var addError))
                                failReason = addError;
                            break;
                        }
                        default:
                            failReason = $"Unsupported source listener argument mode for '{method}'";
                            break;
                    }
                }
                catch (System.Exception ex)
                {
                    failReason = ex.Message;
                }

                if (failReason != null)
                {
                    failures.Add(new { index = i, method, reason = failReason });
                    continue;
                }

                int newIndex = tgtUnityEvent.GetPersistentEventCount() - 1;
                tgtUnityEvent.SetPersistentListenerState(newIndex, srcEvt.GetPersistentListenerState(i));
                copied++;
            }
            return new { success = failures.Count == 0, copied, skipped = failures.Count, failures };
        }

        [UnitySkill("event_get_listener_count", "Get the number of persistent listeners on a UnityEvent",
            Category = SkillCategory.Event, Operation = SkillOperation.Query,
            Tags = new[] { "event", "listener", "count" },
            Outputs = new[] { "count" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EventGetListenerCount(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, string entityId = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName, entityId);
            if (err != null) return err;
            return new { success = true, count = evt.GetPersistentEventCount() };
        }
    }
}

// Producer:Betsy
