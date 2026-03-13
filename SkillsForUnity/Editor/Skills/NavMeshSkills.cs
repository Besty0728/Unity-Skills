using UnityEngine;
using UnityEngine.AI;
using UnityEditor;
using UnityEditor.AI;

namespace UnitySkills
{
    /// <summary>
    /// NavMesh skills - baking, pathfinding.
    /// </summary>
    public static class NavMeshSkills
    {
        [UnitySkill("navmesh_bake", "Bake the NavMesh (Synchronous). Warning: Can be slow.")]
        public static object NavMeshBake()
        {
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
            return new { success = true, message = "NavMesh baked successfully" };
        }

        [UnitySkill("navmesh_clear", "Clear the NavMesh data")]
        public static object NavMeshClear()
        {
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
            return new { success = true, warning = "NavMesh cleared. This operation cannot be undone." };
        }

        [UnitySkill("navmesh_calculate_path", "Calculate a path between two points. Returns: {status, distance, cornerCount, corners}")]
        public static object NavMeshCalculatePath(
            float startX, float startY, float startZ,
            float endX, float endY, float endZ,
            int areaMask = NavMesh.AllAreas
        )
        {
            var startPos = new Vector3(startX, startY, startZ);
            var endPos = new Vector3(endX, endY, endZ);
            
            NavMeshPath path = new NavMeshPath();
            bool hasPath = NavMesh.CalculatePath(startPos, endPos, areaMask, path);
            
            if (!hasPath)
                return new { status = "NoPath", valid = false };
            
            float distance = 0f;
            if (path.status == NavMeshPathStatus.PathComplete || path.status == NavMeshPathStatus.PathPartial)
            {
                for (int i = 0; i < path.corners.Length - 1; i++)
                    distance += Vector3.Distance(path.corners[i], path.corners[i + 1]);
            }
            
            var corners = new System.Collections.Generic.List<object>();
            foreach(var c in path.corners)
            {
                corners.Add(new { x = c.x, y = c.y, z = c.z });
            }

            return new
            {
                status = path.status.ToString(),
                valid = path.status == NavMeshPathStatus.PathComplete,
                distance,
                cornerCount = path.corners.Length,
                corners
            };
        }

        [UnitySkill("navmesh_add_agent", "Add NavMeshAgent component to an object", TracksWorkflow = true)]
        public static object NavMeshAddAgent(string name = null, int instanceId = 0, string path = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            if (go.GetComponent<NavMeshAgent>() != null) return new { error = $"{go.name} already has NavMeshAgent" };
            Undo.AddComponent<NavMeshAgent>(go);
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            return new { success = true, gameObject = go.name };
        }

        [UnitySkill("navmesh_set_agent", "Set NavMeshAgent properties (speed, acceleration, radius, height, stoppingDistance)", TracksWorkflow = true)]
        public static object NavMeshSetAgent(string name = null, int instanceId = 0, string path = null,
            float? speed = null, float? acceleration = null, float? angularSpeed = null,
            float? radius = null, float? height = null, float? stoppingDistance = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return new { error = $"No NavMeshAgent on {go.name}" };
            WorkflowManager.SnapshotObject(agent);
            Undo.RecordObject(agent, "Set NavMeshAgent");
            if (speed.HasValue) agent.speed = speed.Value;
            if (acceleration.HasValue) agent.acceleration = acceleration.Value;
            if (angularSpeed.HasValue) agent.angularSpeed = angularSpeed.Value;
            if (radius.HasValue) agent.radius = radius.Value;
            if (height.HasValue) agent.height = height.Value;
            if (stoppingDistance.HasValue) agent.stoppingDistance = stoppingDistance.Value;
            return new { success = true, gameObject = go.name, speed = agent.speed, radius = agent.radius };
        }

        [UnitySkill("navmesh_add_obstacle", "Add NavMeshObstacle component to an object", TracksWorkflow = true)]
        public static object NavMeshAddObstacle(string name = null, int instanceId = 0, string path = null, bool carve = true)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            if (go.GetComponent<NavMeshObstacle>() != null) return new { error = $"{go.name} already has NavMeshObstacle" };
            var obs = Undo.AddComponent<NavMeshObstacle>(go);
            obs.carving = carve;
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            return new { success = true, gameObject = go.name, carving = obs.carving };
        }

        [UnitySkill("navmesh_set_obstacle", "Set NavMeshObstacle properties (shape, size, carving)", TracksWorkflow = true)]
        public static object NavMeshSetObstacle(string name = null, int instanceId = 0, string path = null,
            string shape = null, float? sizeX = null, float? sizeY = null, float? sizeZ = null, bool? carving = null)
        {
            var (go, err) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (err != null) return err;
            var obs = go.GetComponent<NavMeshObstacle>();
            if (obs == null) return new { error = $"No NavMeshObstacle on {go.name}" };
            WorkflowManager.SnapshotObject(obs);
            Undo.RecordObject(obs, "Set NavMeshObstacle");
            if (!string.IsNullOrEmpty(shape) && System.Enum.TryParse<NavMeshObstacleShape>(shape, true, out var s)) obs.shape = s;
            if (sizeX.HasValue || sizeY.HasValue || sizeZ.HasValue)
            {
                var sz = obs.size;
                obs.size = new Vector3(sizeX ?? sz.x, sizeY ?? sz.y, sizeZ ?? sz.z);
            }
            if (carving.HasValue) obs.carving = carving.Value;
            return new { success = true, gameObject = go.name, shape = obs.shape.ToString(), carving = obs.carving };
        }

        [UnitySkill("navmesh_sample_position", "Find nearest point on NavMesh")]
        public static object NavMeshSamplePosition(float x, float y, float z, float maxDistance = 10f)
        {
            var sourcePos = new Vector3(x, y, z);
            if (NavMesh.SamplePosition(sourcePos, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
                return new { success = true, found = true, point = new { x = hit.position.x, y = hit.position.y, z = hit.position.z }, distance = hit.distance };
            return new { success = true, found = false };
        }

        [UnitySkill("navmesh_set_area_cost", "Set area traversal cost")]
        public static object NavMeshSetAreaCost(int areaIndex, float cost)
        {
            if (Validate.InRange(areaIndex, 0, 31, "areaIndex") is object err1) return err1;
            if (cost < 0) return new { error = "cost must be >= 0" };
            NavMesh.SetAreaCost(areaIndex, cost);
            return new { success = true, areaIndex, cost };
        }

        [UnitySkill("navmesh_get_settings", "Get NavMesh build settings")]
        public static object NavMeshGetSettings()
        {
            var settings = NavMesh.GetSettingsByIndex(0);
            return new
            {
                success = true, agentRadius = settings.agentRadius, agentHeight = settings.agentHeight,
                agentSlope = settings.agentSlope, agentClimb = settings.agentClimb
            };
        }
    }
}
