using UnityEngine;
using UnityEditor;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// Camera skills - Control Scene View and Game cameras.
    /// </summary>
    public static class CameraSkills
    {
        [UnitySkill("camera_align_view_to_object", "Align Scene View camera to look at an object.")]
        public static object CameraAlignViewToObject(string name = null, int instanceId = 0, string path = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.AlignViewToObject(go.transform);
                return new { success = true, message = $"Aligned view to {go.name}" };
            }
            
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_get_info", "Get Scene View camera position and rotation.")]
        public static object CameraGetInfo()
        {
            if (SceneView.lastActiveSceneView != null)
            {
                var cam = SceneView.lastActiveSceneView.camera;
                return new
                {
                    position = new { x = cam.transform.position.x, y = cam.transform.position.y, z = cam.transform.position.z },
                    rotation = new { x = cam.transform.eulerAngles.x, y = cam.transform.eulerAngles.y, z = cam.transform.eulerAngles.z },
                    pivot = new { x = SceneView.lastActiveSceneView.pivot.x, y = SceneView.lastActiveSceneView.pivot.y, z = SceneView.lastActiveSceneView.pivot.z },
                    size = SceneView.lastActiveSceneView.size,
                    orthographic = SceneView.lastActiveSceneView.orthographic
                };
            }
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_set_transform", "Set Scene View camera position/rotation manually.")]
        public static object CameraSetTransform(
            float posX, float posY, float posZ,
            float rotX, float rotY, float rotZ,
            float size = 5f,
            bool instant = true
        )
        {
            if (SceneView.lastActiveSceneView != null)
            {
                var sceneView = SceneView.lastActiveSceneView;
                var position = new Vector3(posX, posY, posZ);
                var rotation = Quaternion.Euler(rotX, rotY, rotZ);
                
                sceneView.LookAt(position, rotation, size);
                
                return new { success = true, message = "Scene View camera updated" };
            }
            return new { error = "No active Scene View found" };
        }
        
        [UnitySkill("camera_look_at", "Focus Scene View camera on a point.")]
        public static object CameraLookAt(float x, float y, float z)
        {
             if (SceneView.lastActiveSceneView != null)
            {
                var sceneView = SceneView.lastActiveSceneView;
                sceneView.LookAt(new Vector3(x, y, z), sceneView.rotation, sceneView.size);
                return new { success = true };
            }
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_create", "Create a new Game Camera", TracksWorkflow = true)]
        public static object CameraCreate(string name = "New Camera", float x = 0, float y = 1, float z = -10, bool addAudioListener = false)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            if (addAudioListener) go.AddComponent<AudioListener>();
            go.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(go, "Create Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            return new { success = true, name = go.name, instanceId = go.GetInstanceID() };
        }

        [UnitySkill("camera_get_properties", "Get Game Camera properties (supports name/instanceId/path)")]
        public static object CameraGetProperties(string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var cam = go.GetComponent<Camera>();
            if (cam == null) return new { error = $"No Camera component on {go.name}" };
            return new
            {
                success = true, name = go.name,
                fieldOfView = cam.fieldOfView, nearClipPlane = cam.nearClipPlane, farClipPlane = cam.farClipPlane,
                orthographic = cam.orthographic, orthographicSize = cam.orthographicSize,
                depth = cam.depth, cullingMask = cam.cullingMask,
                clearFlags = cam.clearFlags.ToString(),
                backgroundColor = new { r = cam.backgroundColor.r, g = cam.backgroundColor.g, b = cam.backgroundColor.b, a = cam.backgroundColor.a },
                rect = new { x = cam.rect.x, y = cam.rect.y, w = cam.rect.width, h = cam.rect.height }
            };
        }

        [UnitySkill("camera_set_properties", "Set Game Camera properties (FOV, clip planes, clear flags, background color, depth)", TracksWorkflow = true)]
        public static object CameraSetProperties(
            string name = null, int instanceId = 0, string path = null,
            float? fieldOfView = null, float? nearClipPlane = null, float? farClipPlane = null,
            float? depth = null, string clearFlags = null,
            float? bgR = null, float? bgG = null, float? bgB = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var cam = go.GetComponent<Camera>();
            if (cam == null) return new { error = $"No Camera component on {go.name}" };
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Camera Properties");
            if (fieldOfView.HasValue) cam.fieldOfView = fieldOfView.Value;
            if (nearClipPlane.HasValue) cam.nearClipPlane = nearClipPlane.Value;
            if (farClipPlane.HasValue) cam.farClipPlane = farClipPlane.Value;
            if (depth.HasValue) cam.depth = depth.Value;
            if (!string.IsNullOrEmpty(clearFlags) && System.Enum.TryParse<CameraClearFlags>(clearFlags, true, out var cf)) cam.clearFlags = cf;
            if (bgR.HasValue || bgG.HasValue || bgB.HasValue)
            {
                var c = cam.backgroundColor;
                cam.backgroundColor = new Color(bgR ?? c.r, bgG ?? c.g, bgB ?? c.b, c.a);
            }
            return new { success = true, name = go.name };
        }

        [UnitySkill("camera_set_culling_mask", "Set Game Camera culling mask by layer names (comma-separated)", TracksWorkflow = true)]
        public static object CameraSetCullingMask(string layerNames, string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var cam = go.GetComponent<Camera>();
            if (cam == null) return new { error = $"No Camera component on {go.name}" };
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Culling Mask");
            int mask = 0;
            foreach (var ln in layerNames.Split(','))
            {
                var layer = LayerMask.NameToLayer(ln.Trim());
                if (layer >= 0) mask |= 1 << layer;
            }
            cam.cullingMask = mask;
            return new { success = true, cullingMask = mask };
        }

        [UnitySkill("camera_screenshot", "Capture a screenshot from a Game Camera to file")]
        public static object CameraScreenshot(string savePath = "Assets/screenshot.png", int width = 1920, int height = 1080, string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var cam = go.GetComponent<Camera>();
            if (cam == null) return new { error = $"No Camera component on {go.name}" };
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (!savePath.EndsWith(".png")) savePath += ".png";
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var rt = new RenderTexture(width, height, 24);
            Texture2D tex = null;
            RenderTexture oldTarget = cam.targetTexture;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(savePath, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = oldTarget;
                RenderTexture.active = null;
                if (rt != null) Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
            AssetDatabase.ImportAsset(savePath);
            return new { success = true, path = savePath, width, height };
        }

        [UnitySkill("camera_set_orthographic", "Switch Game Camera between orthographic and perspective mode", TracksWorkflow = true)]
        public static object CameraSetOrthographic(bool orthographic, float? orthographicSize = null, string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var cam = go.GetComponent<Camera>();
            if (cam == null) return new { error = $"No Camera component on {go.name}" };
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Orthographic");
            cam.orthographic = orthographic;
            if (orthographicSize.HasValue) cam.orthographicSize = orthographicSize.Value;
            return new { success = true, orthographic, orthographicSize = cam.orthographicSize };
        }

        [UnitySkill("camera_list", "List all cameras in the scene")]
        public static object CameraList()
        {
            var cameras = FindHelper.FindAll<Camera>();
            var list = cameras.Select(c => new
            {
                name = c.gameObject.name, instanceId = c.gameObject.GetInstanceID(),
                path = GameObjectFinder.GetPath(c.gameObject),
                depth = c.depth, orthographic = c.orthographic, enabled = c.enabled
            }).OrderBy(c => c.depth).ToArray();
            return new { count = list.Length, cameras = list };
        }
    }
}
