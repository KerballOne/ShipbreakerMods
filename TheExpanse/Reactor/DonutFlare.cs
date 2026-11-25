using System.Collections.Generic;
using UnityEngine;

namespace TheExpanse
{
    // The level 3 flare: a flat ring of plasma bursting out of the core in the plane of the injectors, expanding past
    // them and fading, over about a second. Plain glowing shapes; it animates and removes itself.
    internal sealed class DonutFlare : MonoBehaviour
    {
        const float Seconds = 1.1f;
        static Mesh? sRing;
        static Material? sMaterial;
        float mStarted, mFrom, mTo;
        Material? mOwn;

        internal static void Fire(Vector3 center, Vector3 normal, float fromRadius, float toRadius)
        {
            sRing ??= Ring();
            sMaterial ??= FxShape.GlowMaterial(new Color(0.8f, 0.35f, 1f), 12f);
            var flare = new GameObject("TheExpanse_DonutFlare");
            flare.transform.SetPositionAndRotation(center, Quaternion.FromToRotation(Vector3.up, normal));
            flare.AddComponent<MeshFilter>().sharedMesh = sRing;
            var renderer = flare.AddComponent<MeshRenderer>();
            var donut = flare.AddComponent<DonutFlare>();
            donut.mOwn = new Material(sMaterial);
            renderer.sharedMaterial = donut.mOwn;
            donut.mStarted = Time.time;
            donut.mFrom = Mathf.Max(0.1f, fromRadius);
            donut.mTo = Mathf.Max(donut.mFrom + 0.5f, toRadius);
            donut.Step();
        }

        void Update() => Step();

        void Step()
        {
            float t = (Time.time - mStarted) / Seconds;
            if (t >= 1f) { Destroy(gameObject); if (mOwn != null) Destroy(mOwn); return; }
            float eased = 1f - (1f - t) * (1f - t);
            float radius = Mathf.Lerp(mFrom, mTo, eased);
            // the ring mesh is unit radius in XZ; it thickens a little as it goes
            transform.localScale = new Vector3(radius, 1f + 2f * t, radius);
            if (mOwn != null) FxShape.PaintGlow(mOwn, new Color(0.8f, 0.35f, 1f), 12f * (1f - t) * (1f - t));
        }

        // A flat annulus, outer radius 1, inner 0.8, a little thickness, both faces.
        static Mesh Ring()
        {
            const int n = 64;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i <= n; i++)
            {
                float a = i / (float)n * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                vertices.Add(dir * 0.8f + Vector3.up * 0.05f);
                vertices.Add(dir * 1f);
                vertices.Add(dir * 0.8f - Vector3.up * 0.05f);
            }
            for (int i = 0; i < n; i++)
            {
                int a = i * 3, b = (i + 1) * 3;
                foreach (var (p, q) in new[] { (0, 1), (1, 2), (2, 0) })
                {
                    triangles.AddRange(new[] { a + p, b + p, b + q, a + p, b + q, a + q });
                    triangles.AddRange(new[] { a + p, b + q, b + p, a + p, a + q, b + q });
                }
            }
            var mesh = new Mesh { name = "TheExpanse_DonutRing" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
