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
            NavMeshBuilder.BuildNavMesh();
            return new { success = true, message = "NavMesh baked successfully" };
        }

        [UnitySkill("navmesh_clear", "Clear the NavMesh data")]
        public static object NavMeshClear()
        {
            NavMeshBuilder.ClearAllNavMeshes();
            return new { success = true, message = "NavMesh cleared" };
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
    }
}
