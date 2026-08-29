using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

#if PROBUILDER
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
using UnityEngine.ProBuilder.Shapes;
using UnityEditor.ProBuilder;
#endif

namespace UnitySkills
{
    /// <summary>
    /// ProBuilder modeling skills: create shapes, extrude, bevel, subdivide, etc.
    /// Depends on the com.unity.probuilder package (5.x and above).
    /// </summary>
    public static class ProBuilderSkills
    {
#if !PROBUILDER
        private static object NoProBuilder() =>
            new { error = "ProBuilder package (com.unity.probuilder) is not installed. Install via: Window > Package Manager > Unity Registry > ProBuilder" };
#endif

        // ==================================================================================
        // Shape creation
        // ==================================================================================

        [UnitySkill("probuilder_create_shape", "Create a ProBuilder primitive shape (Cube/Sphere/Cylinder/Cone/Torus/Prism/Arch/Pipe/Stairs/Door/Plane)", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Create,
            Tags = new[] { "probuilder", "shape", "primitive", "mesh", "modeling" },
            Outputs = new[] { "success", "name", "instanceId", "shape", "vertexCount", "faceCount" },
            MutatesScene = true, RiskLevel = "medium", RequiresPackages = new[] { "com.unity.probuilder" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                shape,
                position = new { x, y, z },
                size = new { x = sizeX, y = sizeY, z = sizeZ },
                vertexCount = pbMesh.vertexCount,
                faceCount = pbMesh.faceCount
            };
#endif
        }

        // ==================================================================================
        // Face operations
        // ==================================================================================

        [UnitySkill("probuilder_extrude_faces", "Extrude faces on a ProBuilder mesh (method: IndividualFaces/FaceNormal/VertexNormal)", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "extrude", "face", "modeling" },
            Outputs = new[] { "success", "extrudedFaceCount", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
            {
                // SelectFaces returns an empty set in exactly one situation: faceIndexes was indeed sent, but
                // every index in it either failed to parse or is out of the mesh's face range — an omitted or
                // empty faceIndexes returns *all* faces instead, never an empty list. So reaching here always
                // means an invalid value was sent, not a missing parameter. The old message started with
                // "No faces selected. Provide...", which the router's generic text classifier would read as
                // "the caller sent nothing at all" and report MISSING_PARAM, even though faceIndexes was sent — it just didn't match anything.
                return new
                {
                    error = $"No faces matched faceIndexes='{faceIndexes}'. Mesh '{pbMesh.gameObject.name}' has {pbMesh.faceCount} faces (valid range 0-{pbMesh.faceCount - 1}). Provide faceIndexes as comma-separated indices (e.g. \"0,1,2\"), or omit to extrude all faces.",
                    errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                    parameter = "faceIndexes"
                };
            }

            Undo.RecordObject(pbMesh, "Extrude Faces");
            WorkflowManager.SnapshotObject(pbMesh);

            var newFaces = pbMesh.Extrude(faces, extrudeMethod, distance);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                extrudedFaceCount = newFaces?.Length ?? 0,
                method,
                distance,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_delete_faces", "Delete faces from a ProBuilder mesh by index", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "delete", "face", "modeling" },
            Outputs = new[] { "success", "deletedCount", "remainingFaces", "remainingVertices" },
            RequiresInput = new[] { "proBuilderMesh" },
            RiskLevel = "medium")]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                deletedCount = validIndices.Count,
                remainingFaces = pbMesh.faceCount,
                remainingVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_merge_faces", "Merge multiple faces into a single face on a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "merge", "face", "combine" },
            Outputs = new[] { "success", "mergedFromCount", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                mergedFromCount = faces.Count,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_flip_normals", "Flip face normals on a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "normals", "flip", "face" },
            Outputs = new[] { "success", "flippedCount" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                flippedCount = faces.Count
            };
#endif
        }

        [UnitySkill("probuilder_detach_faces", "Detach faces from a ProBuilder mesh (creates independent faces or a new object)", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "detach", "face", "separate" },
            Outputs = new[] { "success", "detachedFaceCount", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // DetachFaces(faces, true) both appends independent copies of the requested faces and removes the
            // originals, so totalFaces/totalVertices end up close to the starting numbers (N removed, then N
            // added back as copies) — looking at the counts alone would make a perfectly normal
            // "detach and delete" call read like a no-op. So deleteSourceFaces is instead verified with a
            // count-independent reference-identity check (ProBuilder's Face doesn't override value equality, so
            // Contains here genuinely means "is the same object still alive"), and any survivors found are force-removed —
            // this also guards against DetachFaces' internal delete step failing to match the source faces by reference.
            int sourceFacesDeleted = 0;
            if (deleteSourceFaces)
            {
                var stillPresent = faces.Where(f => pbMesh.faces.Contains(f)).ToList();
                if (stillPresent.Count > 0)
                    pbMesh.DeleteFaces(stillPresent);
                sourceFacesDeleted = faces.Count(f => !pbMesh.faces.Contains(f));
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                detachedFaceCount = newFaces?.Count ?? 0,
                deleteSourceFaces,
                sourceFacesDeleted,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        // ==================================================================================
        // Edge operations
        // ==================================================================================

        [UnitySkill("probuilder_bevel_edges", "Bevel (chamfer) edges on a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "bevel", "chamfer", "edge" },
            Outputs = new[] { "success", "beveledEdgeCount", "newFaceCount", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                // edgeIndexes not specified: bevel all edges
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                beveledEdgeCount = edges.Count,
                newFaceCount = newFaces?.Count ?? 0,
                amount,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_extrude_edges", "Extrude edges outward on a ProBuilder mesh to create walls, rails, or flanges", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "extrude", "edge", "wall" },
            Outputs = new[] { "success", "extrudedEdgeCount", "newEdgeCount", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // Unless enableManifoldExtrude is true, Extrude() silently drops edges shared by more than 2 faces
            // (ProBuilder's own editor action gates this the same way, hidden behind the global "Allow
            // non-manifold actions" preference), and returns null once every requested edge has been filtered
            // out this way — which, by default, is every single edge on a closed/watertight mesh. So
            // extrudedEdgeCount can't just report the requested count: that would make a completely no-op call
            // on a closed mesh read as success with a plausible-looking count.
            if (newEdges == null || newEdges.Length == 0)
            {
                return new
                {
                    error = enableManifoldExtrude
                        ? "Extrude produced no new edges. None of the requested edges could be extruded."
                        : $"None of the {edges.Count} requested edge(s) were extruded: they are all manifold (shared by 2 faces), and enableManifoldExtrude=false only extrudes boundary/open edges. Pass enableManifoldExtrude=true to allow extruding manifold edges too.",
                    errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                    parameter = "enableManifoldExtrude"
                };
            }

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                extrudedEdgeCount = newEdges.Length,
                requestedEdgeCount = edges.Count,
                newEdgeCount = newEdges.Length,
                distance,
                extrudeAsGroup,
                enableManifoldExtrude,
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_bridge_edges", "Bridge two edges with a new face (create doorways, windows, connections)", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Create | SkillOperation.Modify,
            Tags = new[] { "probuilder", "bridge", "edge", "connect" },
            Outputs = new[] { "success", "bridgedEdge", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                bridgedEdge = new { a = edgeA, b = edgeB },
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        // ==================================================================================
        // Mesh operations
        // ==================================================================================

        [UnitySkill("probuilder_subdivide", "Subdivide a ProBuilder mesh or selected faces", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "subdivide", "mesh", "detail" },
            Outputs = new[] { "success", "totalFaces", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                totalFaces = pbMesh.faceCount,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_conform_normals", "Make all face normals on a ProBuilder mesh point consistently outward", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "normals", "conform", "consistency" },
            Outputs = new[] { "success", "status", "faceCount" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                status = result.status.ToString(),
                notification = result.notification ?? "",
                faceCount = faces.Count
            };
#endif
        }

        [UnitySkill("probuilder_weld_vertices", "Weld (merge) nearby vertices within a radius on a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "weld", "vertex", "merge" },
            Outputs = new[] { "success", "inputVertexCount", "weldedVertexCount", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            var vertexCountBeforeWeld = pbMesh.vertexCount;

            // Reproduces the editor's built-in "Weld Vertices" action (WeldVertices.cs): first ToMesh() to put
            // the mesh into a known-consistent state, then weld shared-vertex groups, then — a step this skill
            // used to be missing — remove triangles that degenerate to zero area from welding before writing
            // back the render mesh. Without this step, vertexCount would never decrease even when
            // spatially-coincident vertices were genuinely merged into the same shared-vertex group: welding
            // only updates the topology layer (which original vertices share a position), not the per-face
            // corner-position array that vertexCount reflects — compacting that array is exactly what
            // RemoveDegenerateTriangles does.
            pbMesh.ToMesh();
            var weldedIndices = pbMesh.WeldVertices(validIndices, radius);

            var removedVertices = new List<int>();
            if (MeshValidation.ContainsDegenerateTriangles(pbMesh))
                MeshValidation.RemoveDegenerateTriangles(pbMesh, removedVertices);

            pbMesh.ToMesh();
            pbMesh.Refresh();
            pbMesh.Optimize();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                inputVertexCount = validIndices.Count,
                weldedVertexCount = weldedIndices?.Length ?? 0,
                degenerateVerticesRemoved = removedVertices.Count,
                radius,
                vertexCountBeforeWeld,
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_set_face_material", "Set material on specific faces of a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "material", "face", "submesh" },
            Outputs = new[] { "success", "affectedFaces", "materialCount" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // Input validation must happen before Undo/Snapshot
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

                var sharedMats = renderer.sharedMaterials;
                int matIndex = Array.IndexOf(sharedMats, mat);

                if (matIndex < 0)
                {
                    // Renderer doesn't have this material yet, so append a new slot
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                affectedFaces = faces.Count,
                materialCount = pbMesh.GetComponent<MeshRenderer>().sharedMaterials.Length
            };
#endif
        }

        // ==================================================================================
        // Info and transform
        // ==================================================================================

        [UnitySkill("probuilder_get_info", "Get ProBuilder mesh info (vertices, faces, edges, materials, bounds)",
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Query,
            Tags = new[] { "probuilder", "info", "mesh", "topology" },
            Outputs = new[] { "vertexCount", "faceCount", "edgeCount", "triangleCount", "shapeType", "bounds" },
            RequiresInput = new[] { "proBuilderMesh" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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

            // ProBuilderShape is internal, so the shape type name can only be obtained via reflection
            var shapeTypeName = GetShapeTypeName(go);

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
                entityId = UnityObjectIdUtility.GetEntityId(go),
                instanceId = UnityObjectIdUtility.GetObjectId(go),
                isProBuilder = true,
                vertexCount = pbMesh.vertexCount,
                faceCount = pbMesh.faceCount,
                edgeCount = pbMesh.edgeCount,
                triangleCount = pbMesh.triangleCount,
                shapeType = shapeTypeName,
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                bounds = new { center = new { x = bounds.center.x, y = bounds.center.y, z = bounds.center.z }, size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z } },
                materials = renderer?.sharedMaterials?.Select((m, i) => new { index = i, name = m != null ? m.name : "(null)" }).ToArray(),
                submeshFaceCounts = submeshes.Select(kv => new { submeshIndex = kv.Key, faceCount = kv.Value }).ToArray()
            };
#endif
        }

        [UnitySkill("probuilder_center_pivot", "Center pivot or set pivot to a world position on a ProBuilder mesh", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "pivot", "center", "transform" },
            Outputs = new[] { "success", "pivot" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                pivot = new { x = newPos.x, y = newPos.y, z = newPos.z }
            };
#endif
        }

        // ==================================================================================
        // UV operations
        // ==================================================================================

        [UnitySkill("probuilder_project_uv", "Project UVs onto ProBuilder mesh faces using box projection", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "uv", "projection", "texture" },
            Outputs = new[] { "success", "projectedFaceCount", "channel", "method" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // UVEditing is internal, so this can only be reached via reflection
            if (!InvokeProjectFacesBox(pbMesh, faces.ToArray(), channel))
                return new { error = "Failed to project UVs. UVEditing.ProjectFacesBox is not accessible in this ProBuilder version." };

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                projectedFaceCount = faces.Count,
                channel,
                method = "Box"
            };
#endif
        }

        // ==================================================================================
        // Private helpers
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

            // Apply size via localScale then freeze it (baked into vertices), to avoid reflecting into the
            // internal ProBuilderShape API
            go.transform.localScale = size;
            pbMesh.FreezeScaleTransform();
            pbMesh.ToMesh();
            pbMesh.Refresh();

            // Position/rotation must be set after freeze
            go.transform.position = pos;
            go.transform.eulerAngles = rot;

            if (!string.IsNullOrEmpty(parentName))
            {
                var parent = GameObjectFinder.Find(name: parentName);
                if (parent != null) go.transform.SetParent(parent.transform, true);
            }

            return pbMesh;
        }

        // ProBuilderShape is internal in ProBuilder 5.x, so read-only queries go through reflection

        private static Type _pbShapeType;
        private static PropertyInfo _pbShapeShapeProp;

        private static string GetShapeTypeName(GameObject go)
        {
            if (_pbShapeType == null)
                _pbShapeType = typeof(ProBuilderMesh).Assembly.GetType("UnityEngine.ProBuilder.Shapes.ProBuilderShape");
            if (_pbShapeType == null) return "Unknown";

            var comp = go.GetComponent(_pbShapeType);
            if (comp == null) return "Unknown";

            if (_pbShapeShapeProp == null)
                _pbShapeShapeProp = _pbShapeType.GetProperty("shape", BindingFlags.Public | BindingFlags.Instance);
            var shape = _pbShapeShapeProp?.GetValue(comp);
            return shape?.GetType().Name ?? "Unknown";
        }

        // UVEditing is internal in ProBuilder 5.x; this is a reflection helper for it

        private static MethodInfo _projectFacesBoxMethod;

        private static bool InvokeProjectFacesBox(ProBuilderMesh mesh, Face[] faces, int channel)
        {
            if (_projectFacesBoxMethod == null)
            {
                var uvType = typeof(ProBuilderMesh).Assembly.GetType("UnityEngine.ProBuilder.MeshOperations.UVEditing");
                if (uvType == null) return false;
                _projectFacesBoxMethod = uvType.GetMethod("ProjectFacesBox",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ProBuilderMesh), typeof(Face[]), typeof(int) }, null);
            }
            if (_projectFacesBoxMethod == null) return false;
            _projectFacesBoxMethod.Invoke(null, new object[] { mesh, faces, channel });
            return true;
        }
#endif

        // ==================================================================================
        // Batching and level building
        // ==================================================================================

        [UnitySkill("probuilder_create_batch", "Batch create multiple ProBuilder shapes in one call. items: JSON array of {shape, name, x, y, z, sizeX, sizeY, sizeZ, rotX, rotY, rotZ, parent, materialPath}", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Create,
            Tags = new[] { "probuilder", "batch", "create", "level-design" },
            Outputs = new[] { "success", "results" },
            MutatesScene = true,
            RiskLevel = "medium")]
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

                if (!string.IsNullOrEmpty(item.materialPath))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(item.materialPath);
                    if (mat != null)
                        pbMesh.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }

                Undo.RegisterCreatedObjectUndo(go, "Create PB Shape");
                WorkflowManager.SnapshotObject(go, SnapshotType.Created);

                return new { success = true, name = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go), shape = item.shape ?? "Cube" };
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

        [UnitySkill("probuilder_move_vertices", "Move vertices of a ProBuilder mesh by index. Use to create ramps, slopes, and custom shapes from primitives", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "vertex", "move", "deform" },
            Outputs = new[] { "success", "movedVertexCount", "delta", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // Writing directly into a copy of positions[] only moves the requested per-face corner slots —
            // each face owns its own vertices exclusively, even at corners that visually appear shared with a
            // neighboring face — so a shared corner (or the other side of a weld) doesn't move along with it,
            // and the mesh tears open exactly at the seam the caller expected to stay connected.
            // TranslateVertices first resolves each index to the full SharedVertex group it belongs to, then
            // moves the whole group together.
            pbMesh.TranslateVertices(validIndices, delta);

            pbMesh.ToMesh();
            pbMesh.Refresh();

            return new
            {
                success = true,
                name = pbMesh.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                movedVertexCount = validIndices.Count,
                delta = new { x = deltaX, y = deltaY, z = deltaZ },
                totalVertices = pbMesh.vertexCount
            };
#endif
        }

        [UnitySkill("probuilder_set_vertices", "Set absolute positions of specific vertices on a ProBuilder mesh. vertices: JSON array of {index, x, y, z}", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "vertex", "position", "absolute" },
            Outputs = new[] { "success", "setVertexCount", "totalVertices" },
            RequiresInput = new[] { "proBuilderMesh" })]
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

            // Validate.RequiredJsonArray only rejects null/empty/"[]" — it doesn't parse the string. So
            // malformed JSON (a trailing comma, unquoted keys, mismatched brackets, an array of wrongly-shaped
            // elements) reaches DeserializeObject completely unguarded and throws a raw
            // JsonReaderException/JsonSerializationException, aborting the whole request outright instead of
            // presenting a normal structured error that explains the expected shape.
            List<VertexPosItem> items;
            try
            {
                items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<VertexPosItem>>(vertices);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                return new
                {
                    error = $"'vertices' is not valid JSON: {ex.Message}. Expected a JSON array of {{\"index\": int, \"x\": float, \"y\": float, \"z\": float}}, e.g. [{{\"index\":0,\"x\":1,\"y\":0,\"z\":0}}].",
                    errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                    parameter = "vertices"
                };
            }
            if (items == null || items.Count == 0)
            {
                return new
                {
                    error = "'vertices' must be a non-empty JSON array of {\"index\": int, \"x\": float, \"y\": float, \"z\": float}.",
                    errorCode = SkillErrorCode.SemanticInvalid.ToWireString(),
                    parameter = "vertices"
                };
            }

            Undo.RecordObject(pbMesh, "Set Vertices");
            WorkflowManager.SnapshotObject(pbMesh);

            var positions = pbMesh.positions.ToArray();
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
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

        [UnitySkill("probuilder_get_vertices", "Get vertex positions of a ProBuilder mesh (all or by index). Essential for understanding mesh topology before vertex edits",
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Query,
            Tags = new[] { "probuilder", "vertex", "position", "topology" },
            Outputs = new[] { "vertexCount", "faceCount", "vertices" },
            RequiresInput = new[] { "proBuilderMesh" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
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
                // Large meshes fall back to summary mode
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

        [UnitySkill("probuilder_combine_meshes", "Combine multiple ProBuilder meshes into one (for optimization). Provide comma-separated names or 'selected' for Selection", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify | SkillOperation.Delete,
            Tags = new[] { "probuilder", "combine", "merge", "optimization" },
            Outputs = new[] { "success", "combinedCount", "vertexCount", "faceCount" },
            RequiresInput = new[] { "proBuilderMesh" },
            RiskLevel = "medium")]
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

            foreach (var m in meshes)
            {
                Undo.RecordObject(m.gameObject, "Combine Meshes");
                WorkflowManager.SnapshotObject(m.gameObject);
            }

            var target = meshes[0];
            var result = CombineMeshes.Combine(meshes, target);

            // Destroy the source meshes (keep target)
            for (int i = 1; i < meshes.Count; i++)
                Undo.DestroyObjectImmediate(meshes[i].gameObject);

            target.ToMesh();
            target.Refresh();

            return new
            {
                success = true,
                name = target.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(target.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(target.gameObject),
                combinedCount = meshes.Count,
                resultMeshCount = result?.Count ?? 1,
                vertexCount = target.vertexCount,
                faceCount = target.faceCount
            };
#endif
        }

        [UnitySkill("probuilder_set_material", "Set material on an entire ProBuilder mesh (all faces). Quick way to color objects", TracksWorkflow = true,
            Category = SkillCategory.ProBuilder, Operation = SkillOperation.Modify,
            Tags = new[] { "probuilder", "material", "color", "appearance" },
            Outputs = new[] { "success", "material", "color" },
            RequiresInput = new[] { "proBuilderMesh" })]
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
                // Build a temporary shaded material using the current render pipeline's shader
                var color = new Color(r ?? 0.5f, g ?? 0.5f, b ?? 0.5f, a ?? 1f);
                var shaderName = ProjectSkills.GetDefaultShaderName();
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return new { error = $"Cannot find shader '{shaderName}' for current render pipeline" };
                var mat = new Material(shader);
                var colorProp = ProjectSkills.GetColorPropertyName();
                if (mat.HasProperty(colorProp))
                    mat.SetColor(colorProp, color);
                else
                    mat.color = color;
                mat.name = $"PB_{pbMesh.gameObject.name}_{ColorUtility.ToHtmlStringRGB(color)}";
                renderer.sharedMaterial = mat;

                return new
                {
                    success = true,
                    name = pbMesh.gameObject.name,
                    entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                    instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
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
                entityId = UnityObjectIdUtility.GetEntityId(pbMesh.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(pbMesh.gameObject),
                material = renderer.sharedMaterial.name
            };
#endif
        }

#if PROBUILDER
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

// Producer:Betsy
