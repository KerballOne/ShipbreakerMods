using UnityEngine;

namespace MagBoots
{
    internal static class SurfaceGeometry
    {
        // Caches each mesh's triangle/vertex arrays by Mesh instance. Mesh.triangles/Mesh.vertices are
        // Unity properties that allocate and copy the ENTIRE array on every access - calling them fresh
        // here was costing up to two full mesh array copies per raycast, and this runs up to 10 times per
        // FixedUpdate tick while walking (once per stride-probe step), against whatever ship part happens
        // to be underfoot. For any reasonably complex hull mesh that's a serious per-tick GC/CPU cost.
        // Keyed by the Mesh object itself (shared meshes are reused across many part instances, so this
        // stays small relative to the number of raycasts it serves).
        private static readonly System.Collections.Generic.Dictionary<Mesh, (int[] triangles, Vector3[] vertices)> sMeshCache = new();

        private static (int[] triangles, Vector3[] vertices) GetMeshData(Mesh mesh)
        {
            if (sMeshCache.TryGetValue(mesh, out var cached))
                return cached;

            var data = (mesh.triangles, mesh.vertices);
            sMeshCache[mesh] = data;
            return data;
        }

        // Estimates the world-space area of the specific triangle a raycast hit, since
        // Unity doesn't expose per-face area and the game has no "flat surface" query of its own.
        public static bool HasMinimumFaceArea(RaycastHit hit, float minAreaSqMeters)
        {
            if (!(hit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null)
                return true; // non-mesh colliders (box/sphere/capsule) are assumed large enough.

            Mesh mesh = meshCollider.sharedMesh;
            var (triangles, vertices) = GetMeshData(mesh);

            int triIndex = hit.triangleIndex;
            if (triIndex < 0 || triIndex * 3 + 2 >= triangles.Length)
                return true;

            Vector3 a = vertices[triangles[triIndex * 3]];
            Vector3 b = vertices[triangles[triIndex * 3 + 1]];
            Vector3 c = vertices[triangles[triIndex * 3 + 2]];

            Transform t = hit.collider.transform;
            Vector3 worldA = t.TransformPoint(a);
            Vector3 worldB = t.TransformPoint(b);
            Vector3 worldC = t.TransformPoint(c);

            float area = Vector3.Cross(worldB - worldA, worldC - worldA).magnitude * 0.5f;
            return area >= minAreaSqMeters;
        }
    }
}
