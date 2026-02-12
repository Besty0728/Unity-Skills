using UnityEngine;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Camera skills - Control the Scene View camera.
    /// </summary>
    public static class CameraSkills
    {
        [UnitySkill("camera_align_view_to_object", "Align Scene View camera to look at an object.")]
        public static object CameraAlignViewToObject(string objectName)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: objectName);
            if (findErr != null) return findErr;

            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.AlignViewToObject(go.transform);
                return new { success = true, message = $"Aligned view to {objectName}" };
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
    }
}
