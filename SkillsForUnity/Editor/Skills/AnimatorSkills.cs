using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.IO;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Animator management skills - create controllers, manage parameters, control playback.
    /// </summary>
    public static class AnimatorSkills
    {
        [UnitySkill("animator_create_controller", "Create a new Animator Controller")]
        public static object AnimatorCreateController(string name, string folder = "Assets/Animations")
        {
            var folderErr = Validate.SafePath(folder, "folder");
            if (folderErr != null) return folderErr;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, name + ".controller");
            if (File.Exists(path))
                return new { error = $"Controller already exists: {path}" };

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            WorkflowManager.SnapshotObject(controller, SnapshotType.Created);
            AssetDatabase.SaveAssets();

            return new { success = true, name, path };
        }

        [UnitySkill("animator_add_parameter", "Add a parameter to an Animator Controller")]
        public static object AnimatorAddParameter(string controllerPath, string paramName, string paramType = "float", float defaultFloat = 0, int defaultInt = 0, bool defaultBool = false)
        {
            var pathErr = Validate.SafePath(controllerPath, "controllerPath");
            if (pathErr != null) return pathErr;

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                return new { error = $"Controller not found: {controllerPath}" };

            AnimatorControllerParameterType type;
            switch (paramType.ToLower())
            {
                case "float":
                    type = AnimatorControllerParameterType.Float;
                    break;
                case "int":
                    type = AnimatorControllerParameterType.Int;
                    break;
                case "bool":
                    type = AnimatorControllerParameterType.Bool;
                    break;
                case "trigger":
                    type = AnimatorControllerParameterType.Trigger;
                    break;
                default:
                    return new { error = $"Unknown parameter type: {paramType}. Use: float, int, bool, trigger" };
            }

            WorkflowManager.SnapshotObject(controller);
            controller.AddParameter(paramName, type);

            // Set default value
            var parameters = controller.parameters;
            var param = parameters.FirstOrDefault(p => p.name == paramName);
            if (param != null)
            {
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        param.defaultFloat = defaultFloat;
                        break;
                    case AnimatorControllerParameterType.Int:
                        param.defaultInt = defaultInt;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        param.defaultBool = defaultBool;
                        break;
                }
                controller.parameters = parameters;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return new { success = true, controller = controllerPath, parameter = paramName, type = paramType };
        }

        [UnitySkill("animator_get_parameters", "Get all parameters from an Animator Controller")]
        public static object AnimatorGetParameters(string controllerPath)
        {
            var pathErr = Validate.SafePath(controllerPath, "controllerPath");
            if (pathErr != null) return pathErr;

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                return new { error = $"Controller not found: {controllerPath}" };

            var parameters = controller.parameters.Select(p => new
            {
                name = p.name,
                type = p.type.ToString(),
                defaultFloat = p.defaultFloat,
                defaultInt = p.defaultInt,
                defaultBool = p.defaultBool
            }).ToArray();

            return new { controller = controllerPath, parameters };
        }

        [UnitySkill("animator_set_parameter", "Set a parameter value on a GameObject's Animator (supports name/instanceId/path)")]
        public static object AnimatorSetParameter(
            string name = null, int instanceId = 0, string path = null,
            string paramName = null, string paramType = "float",
            float floatValue = 0, int intValue = 0, bool boolValue = false)
        {
            if (Validate.Required(paramName, "paramName") is object err) return err;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new { error = $"No Animator component on {go.name}" };

            WorkflowManager.SnapshotObject(animator);
            Undo.RecordObject(animator, "Set Animator Parameter");

            switch (paramType.ToLower())
            {
                case "float":
                    animator.SetFloat(paramName, floatValue);
                    return new { success = true, gameObject = go.name, parameter = paramName, value = floatValue };
                case "int":
                    animator.SetInteger(paramName, intValue);
                    return new { success = true, gameObject = go.name, parameter = paramName, value = intValue };
                case "bool":
                    animator.SetBool(paramName, boolValue);
                    return new { success = true, gameObject = go.name, parameter = paramName, value = boolValue };
                case "trigger":
                    animator.SetTrigger(paramName);
                    return new { success = true, gameObject = go.name, parameter = paramName, triggered = true };
                default:
                    return new { error = $"Unknown parameter type: {paramType}" };
            }
        }

        [UnitySkill("animator_play", "Play an animation state on a GameObject (supports name/instanceId/path)")]
        public static object AnimatorPlay(string name = null, int instanceId = 0, string path = null, string stateName = null, int layer = 0, float normalizedTime = 0)
        {
            if (Validate.Required(stateName, "stateName") is object err1) return err1;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new { error = $"No Animator component on {go.name}" };

            animator.Play(stateName, layer, normalizedTime);

            return new { success = true, gameObject = go.name, state = stateName, layer };
        }

        [UnitySkill("animator_get_info", "Get Animator component information (supports name/instanceId/path)")]
        public static object AnimatorGetInfo(string name = null, int instanceId = 0, string path = null)
        {
            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new { error = $"No Animator component on {go.name}" };

            var controllerPath = animator.runtimeAnimatorController != null
                ? AssetDatabase.GetAssetPath(animator.runtimeAnimatorController)
                : null;

            return new
            {
                gameObject = go.name,
                instanceId = go.GetInstanceID(),
                hasController = animator.runtimeAnimatorController != null,
                controllerPath,
                speed = animator.speed,
                applyRootMotion = animator.applyRootMotion,
                updateMode = animator.updateMode.ToString(),
                cullingMode = animator.cullingMode.ToString(),
                layerCount = animator.layerCount,
                parameterCount = animator.parameterCount
            };
        }

        [UnitySkill("animator_assign_controller", "Assign an Animator Controller to a GameObject (supports name/instanceId/path)")]
        public static object AnimatorAssignController(string name = null, int instanceId = 0, string path = null, string controllerPath = null)
        {
            if (Validate.Required(controllerPath, "controllerPath") is object err2) return err2;
            var pathErr = Validate.SafePath(controllerPath, "controllerPath");
            if (pathErr != null) return pathErr;

            var (go, error) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (error != null) return error;

            var animator = go.GetComponent<Animator>();
            if (animator == null)
            {
                animator = Undo.AddComponent<Animator>(go);
                WorkflowManager.SnapshotCreatedComponent(animator);
            }

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            if (controller == null)
                return new { error = $"Controller not found: {controllerPath}" };

            Undo.RecordObject(animator, "Assign Animator Controller");
            animator.runtimeAnimatorController = controller;

            return new { success = true, gameObject = go.name, controller = controllerPath };
        }

        [UnitySkill("animator_list_states", "List all states in an Animator Controller layer")]
        public static object AnimatorListStates(string controllerPath, int layer = 0)
        {
            var pathErr = Validate.SafePath(controllerPath, "controllerPath");
            if (pathErr != null) return pathErr;

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                return new { error = $"Controller not found: {controllerPath}" };

            if (layer >= controller.layers.Length)
                return new { error = $"Layer {layer} does not exist. Controller has {controller.layers.Length} layers." };

            var stateMachine = controller.layers[layer].stateMachine;
            var states = stateMachine.states.Select(s => new
            {
                name = s.state.name,
                tag = s.state.tag,
                speed = s.state.speed,
                hasMotion = s.state.motion != null
            }).ToArray();

            return new
            {
                controller = controllerPath,
                layer,
                layerName = controller.layers[layer].name,
                stateCount = states.Length,
                states
            };
        }
        [UnitySkill("animator_add_state", "Add a state to an Animator Controller layer")]
        public static object AnimatorAddState(string controllerPath, string stateName, string clipPath = null, int layer = 0)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) return new { error = $"Controller not found: {controllerPath}" };
            if (layer < 0 || layer >= controller.layers.Length) return new { error = $"Invalid layer: {layer}" };

            var sm = controller.layers[layer].stateMachine;
            var state = sm.AddState(stateName);
            if (!string.IsNullOrEmpty(clipPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip != null) state.motion = clip;
            }
            AssetDatabase.SaveAssets();
            return new { success = true, controller = controllerPath, stateName, layer };
        }

        [UnitySkill("animator_add_transition", "Add a transition between two states in an Animator Controller")]
        public static object AnimatorAddTransition(string controllerPath, string fromState, string toState, int layer = 0, bool hasExitTime = true, float duration = 0.25f)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null) return new { error = $"Controller not found: {controllerPath}" };
            if (layer < 0 || layer >= controller.layers.Length) return new { error = $"Invalid layer: {layer}" };

            var sm = controller.layers[layer].stateMachine;
            var states = sm.states;
            AnimatorState src = null, dst = null;
            foreach (var s in states)
            {
                if (s.state.name == fromState) src = s.state;
                if (s.state.name == toState) dst = s.state;
            }
            if (src == null) return new { error = $"State not found: {fromState}" };
            if (dst == null) return new { error = $"State not found: {toState}" };

            var transition = src.AddTransition(dst);
            transition.hasExitTime = hasExitTime;
            transition.duration = duration;
            AssetDatabase.SaveAssets();
            return new { success = true, from = fromState, to = toState, layer, hasExitTime, duration };
        }
    }
}
