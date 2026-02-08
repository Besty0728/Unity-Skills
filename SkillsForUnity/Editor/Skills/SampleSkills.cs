using UnityEngine;
using UnityEditor;

namespace UnitySkills
{
    /// <summary>
    /// Sample skills for testing the REST API.
    /// </summary>
    public static class SampleSkills
    {
        [UnitySkill("create_cube", "Create a cube at the specified position")]
        public static string CreateCube(float x = 0, float y = 0, float z = 0, string name = "Cube")
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(cube, "Create " + name);
            WorkflowManager.SnapshotObject(cube, SnapshotType.Created);
            return $"Created {name} at ({x},{y},{z})";
        }

        [UnitySkill("create_sphere", "Create a sphere at the specified position")]
        public static string CreateSphere(float x = 0, float y = 0, float z = 0, string name = "Sphere")
        {
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(sphere, "Create " + name);
            WorkflowManager.SnapshotObject(sphere, SnapshotType.Created);
            return $"Created {name} at ({x},{y},{z})";
        }

        [UnitySkill("delete_object", "Delete a GameObject by name")]
        public static string DeleteObject(string objectName)
        {
            var obj = GameObject.Find(objectName);
            if (obj == null) return $"Not found: {objectName}";
            WorkflowManager.SnapshotObject(obj);
            Undo.DestroyObjectImmediate(obj);
            return $"Deleted {objectName}";
        }

        [UnitySkill("get_scene_info", "Get current scene information")]
        public static object GetSceneInfo()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            return new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                rootObjectCount = roots.Length,
                rootObjects = System.Array.ConvertAll(roots, go => go.name)
            };
        }

        [UnitySkill("set_object_position", "Set position of a GameObject")]
        public static string SetObjectPosition(string objectName, float x, float y, float z)
        {
            var obj = GameObject.Find(objectName);
            if (obj == null) return $"Not found: {objectName}";
            Undo.RecordObject(obj.transform, "Set Position");
            obj.transform.position = new Vector3(x, y, z);
            return $"Set {objectName} position to ({x},{y},{z})";
        }

        [UnitySkill("set_object_rotation", "Set rotation of a GameObject (Euler angles)")]
        public static string SetObjectRotation(string objectName, float x, float y, float z)
        {
            var obj = GameObject.Find(objectName);
            if (obj == null) return $"Not found: {objectName}";
            Undo.RecordObject(obj.transform, "Set Rotation");
            obj.transform.rotation = Quaternion.Euler(x, y, z);
            return $"Set {objectName} rotation to ({x},{y},{z})";
        }

        [UnitySkill("set_object_scale", "Set scale of a GameObject")]
        public static string SetObjectScale(string objectName, float x, float y, float z)
        {
            var obj = GameObject.Find(objectName);
            if (obj == null) return $"Not found: {objectName}";
            Undo.RecordObject(obj.transform, "Set Scale");
            obj.transform.localScale = new Vector3(x, y, z);
            return $"Set {objectName} scale to ({x},{y},{z})";
        }

        [UnitySkill("find_objects_by_name", "Find all GameObjects containing a name (param: nameContains)")]
        public static object FindObjectsByName(string nameContains)
        {
            var allObjects = Object.FindObjectsOfType<GameObject>();
            var matches = System.Array.FindAll(allObjects, go => go.name.Contains(nameContains));
            return new
            {
                query = nameContains,
                count = matches.Length,
                objects = System.Array.ConvertAll(matches, go => new
                {
                    name = go.name,
                    position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z }
                })
            };
        }
    }
}
