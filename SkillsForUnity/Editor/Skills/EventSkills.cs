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
    /// Event management skills - inspect and modify UnityEvents (e.g. Button.onClick).
    /// </summary>
    public static class EventSkills
    {
        [UnitySkill("event_get_listeners", "Get persistent listeners of a UnityEvent",
            Category = SkillCategory.Event, Operation = SkillOperation.Query,
            Tags = new[] { "event", "listeners", "unityevent", "inspect" },
            Outputs = new[] { "gameObject", "component", "eventName", "listenerCount", "listeners" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EventGetListeners(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            // Find component
            var component = FindComponentOnGameObject(go, componentName);
            if (component == null)
                return new { error = $"Component not found: {componentName} on {go.name}" };

            // Find event field via reflection
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

            // Inspect listeners
            int count = unityEvent.GetPersistentEventCount();
            var listeners = new List<object>();

            for (int i = 0; i < count; i++)
            {
                var target = unityEvent.GetPersistentTarget(i);
                var methodName = unityEvent.GetPersistentMethodName(i);
                var state = unityEvent.GetPersistentListenerState(i);

                listeners.Add(new
                {
                    index = i,
                    target = target != null ? target.name : "null",
                    targetType = target != null ? target.GetType().Name : "null",
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

        [UnitySkill("event_add_listener", "Add a persistent listener to a UnityEvent (Editor time). Supported args: void, int, float, string, bool, Object.", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "add", "callback" },
            Outputs = new[] { "message", "index" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "targetObjectName", "methodName" })]
        public static object EventAddListener(
            string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null,
            string targetObjectName = null, string targetComponentName = null, string methodName = null,
            string mode = "RuntimeOnly",
            string argType = "void", // void, int, float, string, bool, object
            float floatArg = 0, int intArg = 0, string stringArg = null, bool boolArg = false)
        {
            var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (goErr != null) return goErr;

            var component = FindComponentOnGameObject(go, componentName);
            if (component == null) return new { error = $"Source Component not found: {componentName}" };

            var (targetGo, tgtErr) = GameObjectFinder.FindOrError(name: targetObjectName);
            if (tgtErr != null) return tgtErr;

            // Resolve target: "GameObject" is not a Component, use GO itself as Object target
            Object targetObj = null;
            System.Type targetType = null;
            if (targetComponentName == "GameObject" || targetComponentName == "UnityEngine.GameObject")
            {
                targetObj = targetGo;
                targetType = typeof(GameObject);
            }
            else
            {
                var targetComponent = FindComponentOnGameObject(targetGo, targetComponentName);
                if (targetComponent == null) return new { error = $"Target Component not found: {targetComponentName}" };
                targetObj = targetComponent;
                targetType = targetComponent.GetType();
            }

            // Find UnityEvent
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

            // Record Undo
            WorkflowManager.SnapshotObject(component);
            Undo.RecordObject(component, "Add Event Listener");

            // Resolve Method - also handles property setters (set_XXX)
            MethodInfo methodInfo = null;

            MethodInfo FindMethodOnTarget(System.Type[] paramTypes)
            {
                var mi = targetType.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, paramTypes, null);
                if (mi != null) return mi;
                if (methodName.StartsWith("set_"))
                {
                    var prop2 = targetType.GetProperty(methodName.Substring(4), BindingFlags.Instance | BindingFlags.Public);
                    if (prop2 != null && prop2.CanWrite)
                    {
                        var setter = prop2.GetSetMethod();
                        if (setter != null && paramTypes.Length == 1 && setter.GetParameters()[0].ParameterType == paramTypes[0])
                            return setter;
                    }
                }
                return null;
            }

            switch (argType.ToLower())
            {
                case "void":
                    methodInfo = FindMethodOnTarget(System.Type.EmptyTypes);
                    if (methodInfo == null) return new { error = $"Method '{methodName}()' not found on {targetComponentName}" };
                    var voidDelegate = System.Delegate.CreateDelegate(typeof(UnityAction), targetObj, methodInfo) as UnityAction;
                    UnityEventTools.AddPersistentListener(unityEvent, voidDelegate);
                    break;

                case "float":
                    methodInfo = FindMethodOnTarget(new[] { typeof(float) });
                    if (methodInfo == null) return new { error = $"Method '{methodName}(float)' not found" };
                    var floatDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<float>), targetObj, methodInfo) as UnityAction<float>;
                    UnityEventTools.AddFloatPersistentListener(unityEvent, floatDelegate, floatArg);
                    break;

                case "int":
                    methodInfo = FindMethodOnTarget(new[] { typeof(int) });
                    if (methodInfo == null) return new { error = $"Method '{methodName}(int)' not found" };
                    var intDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<int>), targetObj, methodInfo) as UnityAction<int>;
                    UnityEventTools.AddIntPersistentListener(unityEvent, intDelegate, intArg);
                    break;

                case "string":
                    methodInfo = FindMethodOnTarget(new[] { typeof(string) });
                    if (methodInfo == null) return new { error = $"Method '{methodName}(string)' not found" };
                    var stringDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<string>), targetObj, methodInfo) as UnityAction<string>;
                    UnityEventTools.AddStringPersistentListener(unityEvent, stringDelegate, stringArg);
                    break;

                case "bool":
                    methodInfo = FindMethodOnTarget(new[] { typeof(bool) });
                    if (methodInfo == null) return new { error = $"Method '{methodName}(bool)' not found" };
                    var boolDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<bool>), targetObj, methodInfo) as UnityAction<bool>;
                    UnityEventTools.AddBoolPersistentListener(unityEvent, boolDelegate, boolArg);
                    break;

                default:
                    return new { error = $"Unsupported argType: {argType}" };
            }

            // Set Call State (Runtime/Editor)
            // The newly added listener is always the last one
            int index = unityEvent.GetPersistentEventCount() - 1;
            UnityEventCallState callState = UnityEventCallState.RuntimeOnly;
            if (mode.ToLower() == "editorandruntime") callState = UnityEventCallState.EditorAndRuntime;
            else if (mode.ToLower() == "off") callState = UnityEventCallState.Off;
            
            unityEvent.SetPersistentListenerState(index, callState);

            return new
            {
                success = true,
                message = $"Added listener {targetComponentName}.{methodName} to {componentName}.{eventName}",
                index
            };
        }

        [UnitySkill("event_remove_listener", "Remove a persistent listener by index", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Delete,
            Tags = new[] { "event", "listener", "remove", "delete" },
            Outputs = new[] { "remainingCount" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" })]
        public static object EventRemoveListener(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, int index = 0)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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
            RequiresInput = new[] { "gameObject", "componentName", "eventName" })]
        public static object EventInvoke(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null)
        {
             var (go, goErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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
            
            // Invoke via Reflection 'Invoke' method
            // UnityEvent.Invoke() is public
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

        // Helper to find UnityEventBase on a component
        private static (UnityEventBase evt, Component comp, object error) FindEvent(string name, int instanceId, string path, string componentName, string eventName)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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

        private static bool TryResolveListenerTarget(
            string targetName,
            int targetInstanceId,
            string targetPath,
            string targetComponentName,
            out Object targetObj,
            out System.Type targetType,
            out string error)
        {
            targetObj = null;
            targetType = null;
            error = null;

            var (targetGo, targetErr) = GameObjectFinder.FindOrError(name: targetName, instanceId: targetInstanceId, path: targetPath);
            if (targetErr != null)
            {
                error = SkillResultHelper.TryGetError(targetErr, out var findError) ? findError : "Target object not found";
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetComponentName) ||
                string.Equals(targetComponentName, "GameObject", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetComponentName, "UnityEngine.GameObject", System.StringComparison.OrdinalIgnoreCase))
            {
                targetObj = targetGo;
                targetType = typeof(GameObject);
                return true;
            }

            var targetComponent = FindComponentOnGameObject(targetGo, targetComponentName);
            if (targetComponent == null)
            {
                error = $"Target Component not found: {targetComponentName}";
                return false;
            }

            targetObj = targetComponent;
            targetType = targetComponent.GetType();
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

        private static MethodInfo FindObjectArgumentMethod(System.Type targetType, string methodName, Object argument)
        {
            return targetType
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => string.Equals(m.Name, methodName, System.StringComparison.Ordinal))
                .Select(m => new { Method = m, Parameters = m.GetParameters() })
                .Where(x => x.Parameters.Length == 1 && typeof(Object).IsAssignableFrom(x.Parameters[0].ParameterType))
                .Where(x => argument == null || x.Parameters[0].ParameterType.IsAssignableFrom(argument.GetType()))
                .Select(x => x.Method)
                .FirstOrDefault();
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

        [UnitySkill("event_clear_listeners", "Remove all persistent listeners from a UnityEvent", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Delete,
            Tags = new[] { "event", "clear", "listeners", "remove" },
            Outputs = new[] { "removed" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" })]
        public static object EventClearListeners(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName);
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
            RequiresInput = new[] { "gameObject", "componentName", "eventName" })]
        public static object EventSetListenerState(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, int index = 0, string state = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName);
            if (err != null) return err;
            if (index < 0 || index >= evt.GetPersistentEventCount()) return new { error = "Index out of range" };
            if (!System.Enum.TryParse<UnityEventCallState>(state, true, out var callState)) return new { error = $"Invalid state: {state}" };
            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Listener State");
            evt.SetPersistentListenerState(index, callState);
            return new { success = true, index, state = callState.ToString() };
        }

        [UnitySkill("event_set_listener", "Replace a persistent UnityEvent listener at a specific index. Supports void/int/float/string/bool/Object static arguments.",
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "set", "replace", "persistent" },
            Outputs = new[] { "index", "target", "method", "state", "argType" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "targetName", "methodName" },
            TracksWorkflow = true)]
        public static object EventSetListener(
            string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null,
            int index = 0,
            string targetName = null, int targetInstanceId = 0, string targetPath = null,
            string targetComponentName = null, string methodName = null,
            string mode = "RuntimeOnly",
            string argType = "void",
            float floatArg = 0, int intArg = 0, string stringArg = null, bool boolArg = false,
            string objectReferenceName = null, int objectReferenceInstanceId = 0, string objectReferencePath = null,
            string objectAssetPath = null, string objectType = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName);
            if (err != null) return err;
            if (index < 0 || index >= evt.GetPersistentEventCount()) return new { error = "Index out of range" };
            if (Validate.Required(methodName, "methodName") is object methodErr) return methodErr;

            if (!TryResolveListenerTarget(targetName, targetInstanceId, targetPath, targetComponentName, out var targetObj, out var targetType, out var targetError))
            {
                return new { error = targetError };
            }

            if (!System.Enum.TryParse<UnityEventCallState>(mode, true, out var callState))
            {
                return new { error = $"Invalid mode: {mode}" };
            }

            WorkflowManager.SnapshotObject(comp);
            Undo.RecordObject(comp, "Set Event Listener");

            var normalizedArgType = (argType ?? "void").Trim().ToLowerInvariant();
            switch (normalizedArgType)
            {
                case "void":
                    if (!(evt is UnityEvent unityEvent))
                        return new { error = "Void listener replacement requires a standard UnityEvent" };
                    var voidMethod = FindTargetMethod(targetType, methodName, System.Type.EmptyTypes);
                    if (voidMethod == null) return new { error = $"Method '{methodName}()' not found on {targetType.Name}" };
                    var voidDelegate = System.Delegate.CreateDelegate(typeof(UnityAction), targetObj, voidMethod) as UnityAction;
                    UnityEventTools.RegisterPersistentListener(unityEvent, index, voidDelegate);
                    break;

                case "float":
                    var floatMethod = FindTargetMethod(targetType, methodName, new[] { typeof(float) });
                    if (floatMethod == null) return new { error = $"Method '{methodName}(float)' not found on {targetType.Name}" };
                    var floatDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<float>), targetObj, floatMethod) as UnityAction<float>;
                    UnityEventTools.RegisterFloatPersistentListener(evt, index, floatDelegate, floatArg);
                    break;

                case "int":
                    var intMethod = FindTargetMethod(targetType, methodName, new[] { typeof(int) });
                    if (intMethod == null) return new { error = $"Method '{methodName}(int)' not found on {targetType.Name}" };
                    var intDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<int>), targetObj, intMethod) as UnityAction<int>;
                    UnityEventTools.RegisterIntPersistentListener(evt, index, intDelegate, intArg);
                    break;

                case "string":
                    var stringMethod = FindTargetMethod(targetType, methodName, new[] { typeof(string) });
                    if (stringMethod == null) return new { error = $"Method '{methodName}(string)' not found on {targetType.Name}" };
                    var stringDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<string>), targetObj, stringMethod) as UnityAction<string>;
                    UnityEventTools.RegisterStringPersistentListener(evt, index, stringDelegate, stringArg);
                    break;

                case "bool":
                    var boolMethod = FindTargetMethod(targetType, methodName, new[] { typeof(bool) });
                    if (boolMethod == null) return new { error = $"Method '{methodName}(bool)' not found on {targetType.Name}" };
                    var boolDelegate = System.Delegate.CreateDelegate(typeof(UnityAction<bool>), targetObj, boolMethod) as UnityAction<bool>;
                    UnityEventTools.RegisterBoolPersistentListener(evt, index, boolDelegate, boolArg);
                    break;

                case "object":
                    if (!SerializedPropertySkillUtility.TryResolveObjectReference(
                            objectReferenceName, objectReferenceInstanceId, objectReferencePath, objectAssetPath, objectType,
                            out var objectArg, out var objectArgError))
                    {
                        return new { error = objectArgError };
                    }

                    var objectMethod = FindObjectArgumentMethod(targetType, methodName, objectArg);
                    if (objectMethod == null) return new { error = $"Compatible object method '{methodName}(Object)' not found on {targetType.Name}" };
                    if (!TryRegisterObjectPersistentListener(evt, index, targetObj, objectMethod, objectArg, out var registerError))
                    {
                        return new { error = registerError };
                    }
                    break;

                default:
                    return new { error = $"Unsupported argType: {argType}" };
            }

            evt.SetPersistentListenerState(index, callState);
            EditorUtility.SetDirty(comp);

            return new
            {
                success = true,
                index,
                target = targetObj != null ? targetObj.name : "null",
                targetType = targetType.Name,
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
        public static object EventListEvents(string name = null, int instanceId = 0, string path = null, string componentName = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
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

        [UnitySkill("event_add_listener_batch", "Add multiple listeners at once. items: JSON array of {targetObjectName, targetComponentName, methodName}", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "listener", "batch", "bulk" },
            Outputs = new[] { "added", "total" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName", "items" })]
        public static object EventAddListenerBatch(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null, string items = null)
        {
            var list = Newtonsoft.Json.JsonConvert.DeserializeObject<List<BatchListenerItem>>(items);
            if (list == null || list.Count == 0) return new { error = "No items provided" };
            int added = 0;
            foreach (var item in list)
            {
                var result = EventAddListener(name, instanceId, path, componentName, eventName, item.targetObjectName, item.targetComponentName, item.methodName);
                if (!SkillResultHelper.TryGetError(result, out _))
                    added++;
            }
            return new { success = true, added, total = list.Count };
        }

        private class BatchListenerItem
        {
            public string targetObjectName { get; set; }
            public string targetComponentName { get; set; }
            public string methodName { get; set; }
        }

        [UnitySkill("event_copy_listeners", "Copy listeners from one event to another", TracksWorkflow = true,
            Category = SkillCategory.Event, Operation = SkillOperation.Modify,
            Tags = new[] { "event", "copy", "listeners", "duplicate" },
            Outputs = new[] { "copied" },
            RequiresInput = new[] { "sourceObject", "sourceComponent", "sourceEvent", "targetObject", "targetComponent", "targetEvent" })]
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
            for (int i = 0; i < srcEvt.GetPersistentEventCount(); i++)
            {
                var target = srcEvt.GetPersistentTarget(i);
                var method = srcEvt.GetPersistentMethodName(i);
                if (target == null) continue;
                var mi = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, System.Type.EmptyTypes, null);
                if (mi != null)
                {
                    var del = System.Delegate.CreateDelegate(typeof(UnityAction), target, mi) as UnityAction;
                    UnityEventTools.AddPersistentListener(tgtUnityEvent, del);
                    copied++;
                }
            }
            return new { success = true, copied };
        }

        [UnitySkill("event_get_listener_count", "Get the number of persistent listeners on a UnityEvent",
            Category = SkillCategory.Event, Operation = SkillOperation.Query,
            Tags = new[] { "event", "listener", "count" },
            Outputs = new[] { "count" },
            RequiresInput = new[] { "gameObject", "componentName", "eventName" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object EventGetListenerCount(string name = null, int instanceId = 0, string path = null, string componentName = null, string eventName = null)
        {
            var (evt, comp, err) = FindEvent(name, instanceId, path, componentName, eventName);
            if (err != null) return err;
            return new { success = true, count = evt.GetPersistentEventCount() };
        }
    }
}

// Producer:Betsy
