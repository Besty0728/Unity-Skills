using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;

#if PROBUILDER
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UnityEngine.ProBuilder.Shapes;
#endif

namespace UnitySkills
{
    /// <summary>
    /// ProBuilder mesh modeling skills - create shapes, extrude, bevel, subdivide, etc.
    /// Requires com.unity.probuilder package (5.x+).
    /// </summary>
    public static class ProBuilderSkills
    {
#if !PROBUILDER
        private static object NoProBuilder() =>
            new { error = "ProBuilder package (com.unity.probuilder) is not installed. Install via: Window > Package Manager > Unity Registry > ProBuilder" };
#endif

        // ==================================================================================
        // Shape Creation
        // ==================================================================================

        [UnitySkill("probuilder_create_shape", "Create a ProBuilder primitive shape (Cube/Sphere/Cylinder/Cone/Torus/Prism/Arch/Pipe/Stairs/Door/Plane)", TracksWorkflow = true)]
        public static object ProBuilderCreateShape(
            string shape = "Cube",
            string name = null,
            float x = 0, float y = 0, float z = 0,
            float sizeX = 1, float sizeY = 1, float sizeZ = 1,
            float rotX = 0, float rotY = 0, float rotZ = 0,
            string parent = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            if (!ShapeTypeMap.TryGetValue(shape, out var shapeType))
                return new { error = $"Unknown shape: {shape}. Available: {string.Join(", ", ShapeTypeMap.Keys)}" };

            var pbMesh = CreatePBShape(shapeType, name, new Vector3(x, y, z), new Vector3(sizeX, sizeY, sizeZ), new Vector3(rotX, rotY, rotZ), parent);
            if (pbMesh == null)
                return new { error = $"Failed to create ProBuilder shape: {shape}" };

            var go = pbMesh.gameObject;

            Undo.RegisterCreatedObjectUndo(go, "Create ProBuilder Shape");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                shape,
                position = new { x, y, z },
                size = new { x = sizeX, y = sizeY, z = sizeZ },
                vertexCount = pbMesh.vertexCount,
                faceCount = pbMesh.faceCount
            };
#endif
        }

        // ==================================================================================
        // Face Operations
        // ==================================================================================

        [UnitySkill("probuilder_extrude_faces", "Extrude faces on a ProBuilder mesh (method: IndividualFaces/FaceNormal/VertexNormal)", TracksWorkflow = true)]
        public static object ProBuilderExtrudeFaces(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null,
            float distance = 0.5f,
            string method = "FaceNormal")
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (!Enum.TryParse<ExtrudeMethod>(method, true, out var extrudeMethod))
                return new { error = $"Unknown extrude method: {method}. Available: IndividualFaces, FaceNormal, VertexNormal" };

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes as comma-separated indices (e.g. \"0,1,2\"), or omit to extrude all faces." };

            Undo.RecordObject(pbMesh, "Extrude Faces");
            WorkflowManager.SnapshotObject(pbMesh);

            var newFaces = pbMesh.Extrude(faces, extrudeMethod, distance);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                extrudedFaceCount = newFaces?.Length ?? 0,
                method,
                distance,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_delete_faces", "Delete faces from a ProBuilder mesh by index", TracksWorkflow = true)]
        public static object ProBuilderDeleteFaces(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (string.IsNullOrEmpty(faceIndexes))
                return new { error = "faceIndexes is required (comma-separated, e.g. \"0,1,2\")" };

            var indices = ParseIntList(faceIndexes);
            if (indices == null || indices.Count == 0)
                return new { error = "Invalid faceIndexes format. Use comma-separated integers." };

            var allFaces = pbMesh.faces;
            var validIndices = indices.Where(i => i >= 0 && i < allFaces.Count).ToList();
            if (validIndices.Count == 0)
                return new { error = $"No valid face indices. Mesh has {allFaces.Count} faces (0-{allFaces.Count - 1})." };

            Undo.RecordObject(pbMesh, "Delete Faces");
            WorkflowManager.SnapshotObject(pbMesh);

            var facesToDelete = validIndices.Select(i => allFaces[i]).ToArray();
            pbMesh.DeleteFaces(facesToDelete);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                deletedCount = validIndices.Count,
                remainingFaces = pbMesh.faceCount,
                remainingVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_merge_faces", "Merge multiple faces into a single face on a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderMergeFaces(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count < 2)
                return new { error = "At least 2 faces are required to merge. Provide faceIndexes as comma-separated indices." };

            Undo.RecordObject(pbMesh, "Merge Faces");
            WorkflowManager.SnapshotObject(pbMesh);

            var merged = MergeElements.Merge(pbMesh, faces);
            if (merged == null)
                return new { error = "Failed to merge faces. Ensure the selected faces are valid." };

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                mergedFromCount = faces.Count,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_flip_normals", "Flip face normals on a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderFlipNormals(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes or omit to flip all." };

            Undo.RecordObject(pbMesh, "Flip Normals");
            WorkflowManager.SnapshotObject(pbMesh);

            foreach (var face in faces)
                face.Reverse();

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                flippedCount = faces.Count
            };
#endif
        }

        [UnitySkill("probuilder_detach_faces", "Detach faces from a ProBuilder mesh (creates independent faces or a new object)", TracksWorkflow = true)]
        public static object ProBuilderDetachFaces(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null,
            bool deleteSourceFaces = false)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes or omit to detach all." };

            Undo.RecordObject(pbMesh, "Detach Faces");
            WorkflowManager.SnapshotObject(pbMesh);

            var newFaces = pbMesh.DetachFaces(faces, deleteSourceFaces);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                detachedFaceCount = newFaces?.Count ?? 0,
                deleteSourceFaces,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        // ==================================================================================
        // Edge Operations
        // ==================================================================================

        [UnitySkill("probuilder_bevel_edges", "Bevel (chamfer) edges on a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderBevelEdges(
            string name = null, int instanceId = 0, string path = null,
            string edgeIndexes = null,
            float amount = 0.2f)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (amount <= 0f || amount > 1f)
                return new { error = "amount must be between 0 (exclusive) and 1 (inclusive)" };

            IList<Edge> edges;
            if (string.IsNullOrEmpty(edgeIndexes))
            {
                // Bevel all edges
                var edgeSet = new HashSet<Edge>();
                foreach (var face in pbMesh.faces)
                    foreach (var edge in face.edges)
                        edgeSet.Add(edge);
                edges = edgeSet.ToList();
            }
            else
            {
                edges = ParseEdgeList(pbMesh, edgeIndexes);
                if (edges == null || edges.Count == 0)
                    return new { error = "Invalid edgeIndexes. Use pairs like \"0-1,2-3\" (vertex index pairs)." };
            }

            Undo.RecordObject(pbMesh, "Bevel Edges");
            WorkflowManager.SnapshotObject(pbMesh);

            var newFaces = Bevel.BevelEdges(pbMesh, edges, amount);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                beveledEdgeCount = edges.Count,
                newFaceCount = newFaces?.Count ?? 0,
                amount,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_extrude_edges", "Extrude edges outward on a ProBuilder mesh to create walls, rails, or flanges", TracksWorkflow = true)]
        public static object ProBuilderExtrudeEdges(
            string name = null, int instanceId = 0, string path = null,
            string edgeIndexes = null,
            float distance = 0.5f,
            bool extrudeAsGroup = true,
            bool enableManifoldExtrude = false)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (string.IsNullOrEmpty(edgeIndexes))
                return new { error = "edgeIndexes is required (vertex pairs, e.g. \"0-1,2-3\")" };

            var edges = ParseEdgeList(pbMesh, edgeIndexes);
            if (edges == null || edges.Count == 0)
                return new { error = "Invalid edgeIndexes. Use pairs like \"0-1,2-3\" (vertex index pairs)." };

            Undo.RecordObject(pbMesh, "Extrude Edges");
            WorkflowManager.SnapshotObject(pbMesh);

            var newEdges = pbMesh.Extrude(edges, distance, extrudeAsGroup, enableManifoldExtrude);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                extrudedEdgeCount = edges.Count,
                newEdgeCount = newEdges?.Length ?? 0,
                distance,
                extrudeAsGroup,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_bridge_edges", "Bridge two edges with a new face (create doorways, windows, connections)", TracksWorkflow = true)]
        public static object ProBuilderBridgeEdges(
            string name = null, int instanceId = 0, string path = null,
            string edgeA = null,
            string edgeB = null,
            bool allowNonManifold = false)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (string.IsNullOrEmpty(edgeA) || string.IsNullOrEmpty(edgeB))
                return new { error = "Both edgeA and edgeB are required (e.g. edgeA=\"0-1\", edgeB=\"4-5\")" };

            var edgesA = ParseEdgeList(pbMesh, edgeA);
            var edgesB = ParseEdgeList(pbMesh, edgeB);
            if (edgesA == null || edgesA.Count == 0 || edgesB == null || edgesB.Count == 0)
                return new { error = "Invalid edge format. Use \"vertexA-vertexB\" (e.g. \"0-1\")." };

            Undo.RecordObject(pbMesh, "Bridge Edges");
            WorkflowManager.SnapshotObject(pbMesh);

            var newFace = pbMesh.Bridge(edgesA[0], edgesB[0], allowNonManifold);
            if (newFace == null)
                return new { error = "Failed to bridge edges. Ensure both edges exist and can be connected." };

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                bridgedEdge = new { a = edgeA, b = edgeB },
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        // ==================================================================================
        // Mesh Operations
        // ==================================================================================

        [UnitySkill("probuilder_subdivide", "Subdivide a ProBuilder mesh or selected faces", TracksWorkflow = true)]
        public static object ProBuilderSubdivide(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (!string.IsNullOrEmpty(faceIndexes))
            {
                var faces = SelectFaces(pbMesh, faceIndexes);
                if (faces.Count == 0)
                    return new { error = "No valid face indices provided." };
            }

            Undo.RecordObject(pbMesh, "Subdivide");
            WorkflowManager.SnapshotObject(pbMesh);

            if (string.IsNullOrEmpty(faceIndexes))
            {
                var allFaces = pbMesh.faces.ToArray();
                ConnectElements.Connect(pbMesh, allFaces);
            }
            else
            {
                ConnectElements.Connect(pbMesh, SelectFaces(pbMesh, faceIndexes));
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_conform_normals", "Make all face normals on a ProBuilder mesh point consistently outward", TracksWorkflow = true)]
        public static object ProBuilderConformNormals(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes or omit to conform all." };

            Undo.RecordObject(pbMesh, "Conform Normals");
            WorkflowManager.SnapshotObject(pbMesh);

            var result = pbMesh.ConformNormals(faces);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                status = result.status.ToString(),
                notification = result.notification ?? "",
                faceCount = faces.Count
            };
#endif
        }

        [UnitySkill("probuilder_weld_vertices", "Weld (merge) nearby vertices within a radius on a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderWeldVertices(
            string name = null, int instanceId = 0, string path = null,
            string vertexIndexes = null,
            float radius = 0.01f)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (string.IsNullOrEmpty(vertexIndexes))
                return new { error = "vertexIndexes is required (comma-separated, e.g. \"0,1,2,3\")" };

            var indices = ParseIntList(vertexIndexes);
            if (indices == null || indices.Count == 0)
                return new { error = "Invalid vertexIndexes format" };

            if (radius <= 0f)
                return new { error = "radius must be greater than 0" };

            var positions = pbMesh.positions;
            var validIndices = indices.Where(i => i >= 0 && i < positions.Count).ToList();
            if (validIndices.Count == 0)
                return new { error = $"No valid vertex indices. Mesh has {positions.Count} vertices (0-{positions.Count - 1})." };

            Undo.RecordObject(pbMesh, "Weld Vertices");
            WorkflowManager.SnapshotObject(pbMesh);

            var weldedIndices = pbMesh.WeldVertices(validIndices, radius);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                inputVertexCount = validIndices.Count,
                weldedVertexCount = weldedIndices?.Length ?? 0,
                radius,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_set_face_material", "Set material on specific faces of a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderSetFaceMaterial(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null,
            string materialPath = null,
            int submeshIndex = -1)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes or omit to apply to all." };

            // Validate inputs BEFORE Undo/Snapshot
            if (!string.IsNullOrEmpty(materialPath))
            {
                if (Validate.SafePath(materialPath, "materialPath") is object pathErr) return pathErr;
            }
            else if (submeshIndex < 0)
            {
                return new { error = "Provide either materialPath or submeshIndex" };
            }

            var renderer = pbMesh.GetComponent<MeshRenderer>();
            if (renderer == null)
                return new { error = $"'{pbMesh.gameObject.name}' has no MeshRenderer component" };

            Undo.RecordObject(pbMesh, "Set Face Material");
            Undo.RecordObject(renderer, "Set Face Material");
            WorkflowManager.SnapshotObject(pbMesh);

            if (!string.IsNullOrEmpty(materialPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (mat == null)
                    return new { error = $"Material not found: {materialPath}" };

                // Determine submesh index for this material
                var sharedMats = renderer.sharedMaterials;
                int matIndex = Array.IndexOf(sharedMats, mat);

                if (matIndex < 0)
                {
                    // Add material to renderer
                    var newMats = new Material[sharedMats.Length + 1];
                    Array.Copy(sharedMats, newMats, sharedMats.Length);
                    newMats[sharedMats.Length] = mat;
                    renderer.sharedMaterials = newMats;
                    matIndex = sharedMats.Length;
                }

                foreach (var face in faces)
                    face.submeshIndex = matIndex;
            }
            else
            {
                foreach (var face in faces)
                    face.submeshIndex = submeshIndex;
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                affectedFaces = faces.Count,
                materialCount = pbMesh.GetComponent<MeshRenderer>().sharedMaterials.Length
            };
#endif
        }

        // ==================================================================================
        // Info & Transform
        // ==================================================================================

        [UnitySkill("probuilder_get_info", "Get ProBuilder mesh info (vertices, faces, edges, materials, bounds)")]
        public static object ProBuilderGetInfo(
            string name = null, int instanceId = 0, string path = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var go = pbMesh.gameObject;
            var renderer = pbMesh.GetComponent<MeshRenderer>();
            var bounds = pbMesh.GetComponent<MeshFilter>()?.sharedMesh?.bounds ?? new Bounds();
            var pbShape = go.GetComponent<ProBuilderShape>();

            // Collect submesh info
            var submeshes = new Dictionary<int, int>();
            foreach (var face in pbMesh.faces)
            {
                if (!submeshes.ContainsKey(face.submeshIndex))
                    submeshes[face.submeshIndex] = 0;
                submeshes[face.submeshIndex]++;
            }

            return new
            {
                success = true,
                name = go.name,
                instanceId = go.GetInstanceID(),
                isProBuilder = true,
                vertexCount = pbMesh.vertexCount,
                faceCount = pbMesh.faceCount,
                edgeCount = pbMesh.edgeCount,
                triangleCount = pbMesh.triangleCount,
                shapeType = pbShape?.shape?.GetType().Name ?? "Unknown",
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                bounds = new { center = new { x = bounds.center.x, y = bounds.center.y, z = bounds.center.z }, size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z } },
                materials = renderer?.sharedMaterials?.Select((m, i) => new { index = i, name = m != null ? m.name : "(null)" }).ToArray(),
                submeshFaceCounts = submeshes.Select(kv => new { submeshIndex = kv.Key, faceCount = kv.Value }).ToArray()
            };
#endif
        }

        [UnitySkill("probuilder_center_pivot", "Center pivot or set pivot to a world position on a ProBuilder mesh", TracksWorkflow = true)]
        public static object ProBuilderCenterPivot(
            string name = null, int instanceId = 0, string path = null,
            float? worldX = null, float? worldY = null, float? worldZ = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            Undo.RecordObject(pbMesh.transform, "Center Pivot");
            Undo.RecordObject(pbMesh, "Center Pivot");
            WorkflowManager.SnapshotObject(pbMesh.gameObject);

            if (worldX.HasValue || worldY.HasValue || worldZ.HasValue)
            {
                var pos = pbMesh.transform.position;
                var worldPos = new Vector3(worldX ?? pos.x, worldY ?? pos.y, worldZ ?? pos.z);
                pbMesh.SetPivot(worldPos);
            }
            else
            {
                pbMesh.CenterPivot(null);
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();

            var newPos = pbMesh.transform.position;
            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                pivot = new { x = newPos.x, y = newPos.y, z = newPos.z }
            };
#endif
        }

        // ==================================================================================
        // UV Operations
        // ==================================================================================

        [UnitySkill("probuilder_project_uv", "Project UVs onto ProBuilder mesh faces using box projection", TracksWorkflow = true)]
        public static object ProBuilderProjectUV(
            string name = null, int instanceId = 0, string path = null,
            string faceIndexes = null,
            int channel = 0)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var faces = SelectFaces(pbMesh, faceIndexes);
            if (faces.Count == 0)
                return new { error = "No faces selected. Provide faceIndexes or omit to project all." };

            if (channel < 0 || channel > 3)
                return new { error = "UV channel must be 0-3 (0=primary, 1=lightmap)" };

            Undo.RecordObject(pbMesh, "Project UV");
            WorkflowManager.SnapshotObject(pbMesh);

            UVEditing.ProjectFacesBox(pbMesh, faces.ToArray(), channel);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                projectedFaceCount = faces.Count,
                channel,
                method = "Box"
            };
#endif
        }

        // ==================================================================================
        // Private Helpers
        // ==================================================================================

#if PROBUILDER
        private static readonly Dictionary<string, Type> ShapeTypeMap = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            { "Cube", typeof(Cube) }, { "Sphere", typeof(Sphere) }, { "Cylinder", typeof(Cylinder) },
            { "Cone", typeof(Cone) }, { "Torus", typeof(Torus) }, { "Prism", typeof(Prism) },
            { "Arch", typeof(Arch) }, { "Pipe", typeof(Pipe) }, { "Stairs", typeof(Stairs) },
            { "Door", typeof(Door) }, { "Plane", typeof(UnityEngine.ProBuilder.Shapes.Plane) },
        };

        private static ProBuilderMesh CreatePBShape(Type shapeType, string objName, Vector3 pos, Vector3 size, Vector3 rot, string parentName)
        {
            var pbMesh = ShapeFactory.Instantiate(shapeType);
            if (pbMesh == null) return null;

            var go = pbMesh.gameObject;
            if (!string.IsNullOrEmpty(objName)) go.name = objName;
            go.transform.position = pos;
            go.transform.eulerAngles = rot;

            var pbShape = go.GetComponent<ProBuilderShape>();
            if (pbShape != null) pbShape.size = size;

            if (!string.IsNullOrEmpty(parentName))
            {
                var parent = GameObjectFinder.Find(name: parentName);
                if (parent != null) go.transform.SetParent(parent.transform, true);
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();
            return pbMesh;
        }
#endif

        // ==================================================================================
        // Batch & Level Design
        // ==================================================================================

        [UnitySkill("probuilder_create_batch", "Batch create multiple ProBuilder shapes in one call. items: JSON array of {shape, name, x, y, z, sizeX, sizeY, sizeZ, rotX, rotY, rotZ, parent, materialPath}", TracksWorkflow = true)]
        public static object ProBuilderCreateBatch(string items, string defaultParent = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            return BatchExecutor.Execute<PBBatchItem>(items, item =>
            {
                if (!ShapeTypeMap.TryGetValue(item.shape ?? "Cube", out var shapeType))
                    return new { error = $"Unknown shape: {item.shape}" };

                var pos = new Vector3(item.x, item.y, item.z);
                var size = new Vector3(item.sizeX, item.sizeY, item.sizeZ);
                var rot = new Vector3(item.rotX, item.rotY, item.rotZ);
                var parent = item.parent ?? defaultParent;

                var pbMesh = CreatePBShape(shapeType, item.name, pos, size, rot, parent);
                if (pbMesh == null)
                    return new { error = $"Failed to create shape: {item.shape}" };

                var go = pbMesh.gameObject;

                // Apply material if specified
                if (!string.IsNullOrEmpty(item.materialPath))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(item.materialPath);
                    if (mat != null)
                        pbMesh.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }

                Undo.RegisterCreatedObjectUndo(go, "Create PB Shape");
                WorkflowManager.SnapshotObject(go, SnapshotType.Created);

                return new { success = true, name = go.name, instanceId = go.GetInstanceID(), shape = item.shape ?? "Cube" };
            }, item => item.name ?? item.shape);
#endif
        }

        private class PBBatchItem
        {
            public string shape { get; set; } = "Cube";
            public string name { get; set; }
            public float x { get; set; }
            public float y { get; set; }
            public float z { get; set; }
            public float sizeX { get; set; } = 1;
            public float sizeY { get; set; } = 1;
            public float sizeZ { get; set; } = 1;
            public float rotX { get; set; }
            public float rotY { get; set; }
            public float rotZ { get; set; }
            public string parent { get; set; }
            public string materialPath { get; set; }
        }

        [UnitySkill("probuilder_move_vertices", "Move vertices of a ProBuilder mesh by index. Use to create ramps, slopes, and custom shapes from primitives", TracksWorkflow = true)]
        public static object ProBuilderMoveVertices(
            string name = null, int instanceId = 0, string path = null,
            string vertexIndexes = null,
            float deltaX = 0, float deltaY = 0, float deltaZ = 0)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (string.IsNullOrEmpty(vertexIndexes))
                return new { error = "vertexIndexes is required (comma-separated, e.g. \"4,5,6,7\" for top vertices of a Cube)" };

            var indices = ParseIntList(vertexIndexes);
            if (indices == null || indices.Count == 0)
                return new { error = "Invalid vertexIndexes format" };

            var positions = pbMesh.positions;
            var validIndices = indices.Where(i => i >= 0 && i < positions.Count).ToList();
            if (validIndices.Count == 0)
                return new { error = $"No valid vertex indices. Mesh has {positions.Count} vertices (0-{positions.Count - 1})." };

            Undo.RecordObject(pbMesh, "Move Vertices");
            WorkflowManager.SnapshotObject(pbMesh);

            var delta = new Vector3(deltaX, deltaY, deltaZ);
            var newPositions = positions.ToArray();
            foreach (var idx in validIndices)
                newPositions[idx] += delta;
            pbMesh.positions = newPositions;

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                movedVertexCount = validIndices.Count,
                delta = new { x = deltaX, y = deltaY, z = deltaZ },
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_set_vertices", "Set absolute positions of specific vertices on a ProBuilder mesh. vertices: JSON array of {index, x, y, z}", TracksWorkflow = true)]
        public static object ProBuilderSetVertices(
            string name = null, int instanceId = 0, string path = null,
            string vertices = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            if (Validate.RequiredJsonArray(vertices, "vertices") is object jsonErr) return jsonErr;

            Undo.RecordObject(pbMesh, "Set Vertices");
            WorkflowManager.SnapshotObject(pbMesh);

            var positions = pbMesh.positions.ToArray();
            var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<VertexPosItem>>(vertices);
            int setCount = 0;

            foreach (var item in items)
            {
                if (item.index >= 0 && item.index < positions.Length)
                {
                    positions[item.index] = new Vector3(item.x, item.y, item.z);
                    setCount++;
                }
            }

            pbMesh.positions = positions;
            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                setVertexCount = setCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        private class VertexPosItem
        {
            public int index { get; set; }
            public float x { get; set; }
            public float y { get; set; }
            public float z { get; set; }
        }

        [UnitySkill("probuilder_get_vertices", "Get vertex positions of a ProBuilder mesh (all or by index). Essential for understanding mesh topology before vertex edits")]
        public static object ProBuilderGetVertices(
            string name = null, int instanceId = 0, string path = null,
            string vertexIndexes = null, bool verbose = true)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var positions = pbMesh.positions;
            List<object> result;

            if (!string.IsNullOrEmpty(vertexIndexes))
            {
                var indices = ParseIntList(vertexIndexes);
                result = (indices ?? new List<int>())
                    .Where(i => i >= 0 && i < positions.Count)
                    .Select(i => (object)new { index = i, x = positions[i].x, y = positions[i].y, z = positions[i].z })
                    .ToList();
            }
            else if (verbose || positions.Count <= 100)
            {
                result = new List<object>();
                for (int i = 0; i < positions.Count; i++)
                    result.Add(new { index = i, x = positions[i].x, y = positions[i].y, z = positions[i].z });
            }
            else
            {
                // Summary mode for large meshes
                var bounds = pbMesh.GetComponent<MeshFilter>()?.sharedMesh?.bounds ?? new Bounds();
                return new
                {
                    success = true,
                    name = pbMesh.gameObject.name,
                    vertexCount = positions.Count,
                    bounds = new { min = new { x = bounds.min.x, y = bounds.min.y, z = bounds.min.z }, max = new { x = bounds.max.x, y = bounds.max.y, z = bounds.max.z } },
                    note = $"Mesh has {positions.Count} vertices. Use vertexIndexes to query specific vertices, or verbose=true to get all."
                };
            }

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                vertexCount = positions.Count,
                faceCount = pbMesh.faceCount,
                vertices = result
            };
#endif
        }

        [UnitySkill("probuilder_combine_meshes", "Combine multiple ProBuilder meshes into one (for optimization). Provide comma-separated names or 'selected' for Selection", TracksWorkflow = true)]
        public static object ProBuilderCombineMeshes(string names = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            List<ProBuilderMesh> meshes;

            if (!string.IsNullOrEmpty(names) && !names.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                meshes = new List<ProBuilderMesh>();
                foreach (var n in names.Split(','))
                {
                    var go = GameObjectFinder.Find(name: n.Trim());
                    if (go == null) return new { error = $"GameObject not found: {n.Trim()}" };
                    var pb = go.GetComponent<ProBuilderMesh>();
                    if (pb == null) return new { error = $"'{n.Trim()}' has no ProBuilderMesh" };
                    meshes.Add(pb);
                }
            }
            else
            {
                meshes = Selection.gameObjects
                    .Select(g => g.GetComponent<ProBuilderMesh>())
                    .Where(pb => pb != null)
                    .ToList();
            }

            if (meshes.Count < 2)
                return new { error = "At least 2 ProBuilder meshes are required to combine" };

            // Record undo for all source meshes
            foreach (var m in meshes)
            {
                Undo.RecordObject(m.gameObject, "Combine Meshes");
                WorkflowManager.SnapshotObject(m.gameObject);
            }

            var target = meshes[0];
            var result = CombineMeshes.Combine(meshes, target);

            // Destroy source meshes (except target)
            for (int i = 1; i < meshes.Count; i++)
                Undo.DestroyObjectImmediate(meshes[i].gameObject);

            target.ToMesh();
            target.Refresh();

            return new
            {
                success = true,
                name = target.gameObject.name,
                instanceId = target.gameObject.GetInstanceID(),
                combinedCount = meshes.Count,
                resultMeshCount = result?.Count ?? 1,
                vertexCount = target.vertexCount,
                faceCount = target.faceCount
            };
#endif
        }

        [UnitySkill("probuilder_set_material", "Set material on an entire ProBuilder mesh (all faces). Quick way to color objects", TracksWorkflow = true)]
        public static object ProBuilderSetMaterial(
            string name = null, int instanceId = 0, string path = null,
            string materialPath = null,
            float? r = null, float? g = null, float? b = null, float? a = null)
        {
#if !PROBUILDER
            return NoProBuilder();
#else
            var (pbMesh, err) = FindProBuilderMesh(name, instanceId, path);
            if (err != null) return err;

            var renderer = pbMesh.GetComponent<MeshRenderer>();
            if (renderer == null)
                return new { error = $"'{pbMesh.gameObject.name}' has no MeshRenderer component" };

            Undo.RecordObject(renderer, "Set Material");
            WorkflowManager.SnapshotObject(pbMesh.gameObject);

            if (!string.IsNullOrEmpty(materialPath))
            {
                if (Validate.SafePath(materialPath, "materialPath") is object pathErr) return pathErr;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (mat == null)
                    return new { error = $"Material not found: {materialPath}" };
                renderer.sharedMaterial = mat;
            }
            else if (r.HasValue || g.HasValue || b.HasValue)
            {
                // Create a temporary colored material for quick prototyping
                var color = new Color(r ?? 0.5f, g ?? 0.5f, b ?? 0.5f, a ?? 1f);
                var mat = new Material(Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("HDRP/Lit"));
                mat.color = color;
                mat.name = $"PB_{pbMesh.gameObject.name}_{ColorUtility.ToHtmlStringRGB(color)}";
                renderer.sharedMaterial = mat;

                return new
                {
                    success = true,
                    name = pbMesh.gameObject.name,
                    instanceId = pbMesh.gameObject.GetInstanceID(),
                    materialName = mat.name,
                    color = new { r = color.r, g = color.g, b = color.b, a = color.a },
                    note = "Runtime material created. Use material_create + materialPath for persistent materials."
                };
            }
            else
            {
                return new { error = "Provide materialPath or color (r,g,b)" };
            }

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                instanceId = pbMesh.gameObject.GetInstanceID(),
                material = renderer.sharedMaterial.name
            };
#endif
        }

        private static (ProBuilderMesh mesh, object error) FindProBuilderMesh(string name, int instanceId, string path)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path);
            if (findErr != null) return (null, findErr);

            var pbMesh = go.GetComponent<ProBuilderMesh>();
            if (pbMesh == null)
                return (null, new { error = $"GameObject '{go.name}' does not have a ProBuilderMesh component" });

            return (pbMesh, null);
        }

        private static List<Face> SelectFaces(ProBuilderMesh mesh, string faceIndexes)
        {
            var allFaces = mesh.faces;
            if (string.IsNullOrEmpty(faceIndexes))
                return allFaces.ToList();

            var indices = ParseIntList(faceIndexes);
            if (indices == null) return new List<Face>();

            return indices
                .Where(i => i >= 0 && i < allFaces.Count)
                .Select(i => allFaces[i])
                .ToList();
        }

        private static List<int> ParseIntList(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return null;
            var result = new List<int>();
            foreach (var part in csv.Split(','))
            {
                if (int.TryParse(part.Trim(), out var val))
                    result.Add(val);
            }
            return result.Count > 0 ? result : null;
        }

        private static IList<Edge> ParseEdgeList(ProBuilderMesh mesh, string edgeIndexes)
        {
            if (string.IsNullOrEmpty(edgeIndexes)) return null;
            var edges = new List<Edge>();
            foreach (var pair in edgeIndexes.Split(','))
            {
                var parts = pair.Trim().Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var a) && int.TryParse(parts[1].Trim(), out var b))
                    edges.Add(new Edge(a, b));
            }
            return edges.Count > 0 ? edges : null;
        }
#endif
    }
}
