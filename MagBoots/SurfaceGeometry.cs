using UnityEngine;

namespace MagBoots
{
    internal static class SurfaceGeometry
    {
        // Estimates the world-space area of the specific triangle a raycast hit, since
        // Unity doesn't expose per-face area and the game has no "flat surface" query of its own.
        public static bool HasMinimumFaceArea(RaycastHit hit, float minAreaSqMeters)
        {
            if (!(hit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null)
                return true; // non-mesh colliders (box/sphere/capsule) are assumed large enough.

            Mesh mesh = meshCollider.sharedMesh;
            int triIndex = hit.triangleIndex;
            if (triIndex < 0 || triIndex * 3 + 2 >= mesh.triangles.Length)
                return true;

            int[] triangles = mesh.triangles;
            Vector3[] vertices = mesh.vertices;

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
