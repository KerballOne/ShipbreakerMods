using System.Collections.Generic;
using UnityEngine;

namespace TheExpanse
{
    // On each connected injector's handle console, set into its face like the game's breaker panels: three small lights
    // in a row just above the handle, which light red one by one as the core swells and are all dark at its low point
    // (the safe moment to pull the handle), and a plasma / fire hazard sign on the plate beside it. They are children of
    // the console, so they stay on it however it moves. Laid out from the console's own size: its face is the side of its
    // local X axis that looks away from the core, its Y axis runs from the handle up to the pipe, Z across. Without a
    // console found, they float above the handle instead. Hidden for injectors already disconnected, and while the
    // plasma is not burning.
    internal static class HandleLights
    {
        const int Lights = 3;
        const float Size = 0.07f, Spacing = 0.14f, Above = 0.3f;    // floating, without a console
        const float SignSize = 0.32f, SignAbove = 0.62f;
        static readonly GameObject?[,] sLights = new GameObject?[8, Lights];
        static readonly GameObject?[] sSigns = new GameObject?[8];
        static readonly Transform?[] sMountedOn = new Transform?[8];
        static Material? sOn, sOff, sYellow, sBlack, sYellowDim;
        static readonly bool?[] sSignsLive = new bool?[8];
        static Mesh? sOuter, sInner, sFlame;
        static GameObject? sRoot;
        static readonly Dictionary<string, List<(Vector3 Spot, float Side)>> sSpots = new Dictionary<string, List<(Vector3 Spot, float Side)>>();  // the consoles are alike

        internal static void Destroy()
        {
            foreach (var light in sLights) if (light != null) Object.Destroy(light);
            foreach (var sign in sSigns) if (sign != null) Object.Destroy(sign);
            if (sRoot != null) Object.Destroy(sRoot);
            sRoot = null;
            System.Array.Clear(sLights, 0, sLights.Length);
            System.Array.Clear(sSigns, 0, sSigns.Length);
            System.Array.Clear(sMountedOn, 0, sMountedOn.Length);
            System.Array.Clear(sSignsLive, 0, sSignsLive.Length);
        }

        // stage: 0 all dark (safe), 1..3 that many lit. live: the injector still feeds burning plasma; otherwise (already
        // disconnected, or the plasma out) the lights stay dark and the signs are unlit and subdued, but still there.
        // console: the handle's console, or null; handle, up, around, outward: the floating placement's world position
        // and directions (outward: away from the core's axis).
        internal static void Show(int injector, bool visible, Transform? console, Vector3 handle, Vector3 up, Vector3 around, Vector3 outward, int stage, bool live = true)
        {
            if (!visible)
            {
                for (int k = 0; k < Lights; k++) { var hidden = sLights[injector, k]; if (hidden != null) hidden.SetActive(false); }
                { var sign = sSigns[injector]; if (sign != null) sign.SetActive(false); }
                return;
            }
            if (sRoot == null) sRoot = new GameObject("TheExpanse_HandleLights");
            sOn ??= FxShape.GlowMaterial(new Color(1f, 0.08f, 0.05f), 6f);
            sOff ??= FxShape.GlowMaterial(new Color(0.05f, 0.015f, 0.015f), 0.15f);

            for (int k = 0; k < Lights; k++)
            {
                if (sLights[injector, k] == null)
                {
                    var made = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    made.name = $"TheExpanse_HandleLight_{injector}_{k}";
                    Object.Destroy(made.GetComponent<Collider>());
                    made.transform.SetParent(sRoot.transform, false);
                    sLights[injector, k] = made;
                    sMountedOn[injector] = null;
                }
                var light = sLights[injector, k]!;
                light.SetActive(true);
                var renderer = light.GetComponent<Renderer>();
                var material = live && k < stage ? sOn : sOff;
                if (renderer.sharedMaterial != material) renderer.sharedMaterial = material;
            }
            if (sSigns[injector] == null) { sSigns[injector] = MakeSign(injector); sMountedOn[injector] = null; }
            sSigns[injector]!.SetActive(true);
            if (sSignsLive[injector] != live)
            {
                sSignsLive[injector] = live;
                sYellowDim ??= FxShape.GlowMaterial(new Color(0.42f, 0.33f, 0.05f), 0.05f);
                foreach (var renderer in sSigns[injector]!.GetComponentsInChildren<MeshRenderer>(true))
                    if (renderer.sharedMaterial == sYellow || renderer.sharedMaterial == sYellowDim)
                        renderer.sharedMaterial = live ? sYellow : sYellowDim;
            }

            if (console != null)
            {
                if (sMountedOn[injector] != console) Mount(injector, console, outward);
                return;
            }
            // No console: floating above the handle.
            for (int k = 0; k < Lights; k++)
            {
                var light = sLights[injector, k]!;
                if (light.transform.parent != sRoot.transform) light.transform.SetParent(sRoot.transform, false);
                light.transform.localScale = Vector3.one * Size;
                light.transform.position = handle + up * Above + around * ((k - 1) * Spacing);
            }
            var floating = sSigns[injector]!.transform;
            if (floating.parent != sRoot.transform) floating.SetParent(sRoot.transform, false);
            floating.localScale = Vector3.one * SignSize;
            floating.SetPositionAndRotation(handle + up * SignAbove + outward * 0.02f, Quaternion.LookRotation(outward, up));
            for (int k = 0; k < floating.childCount; k++)
            {
                var one = floating.GetChild(k);
                one.gameObject.SetActive(true);
                one.localScale = Vector3.one;
                one.localRotation = Quaternion.identity;
                one.localPosition = new Vector3(k == 0 ? -0.6f : 0.6f, 0f, 0f);
            }
        }

        // Where the signs go: a grid of rays over the face marks every spot with room for a sign that is flat all round;
        // those are grouped into areas (neighbors at the same depth), and the two largest areas that stand proud of the
        // plate (the mounting blocks) each get one sign, at the member spot nearest their middle. Smaller signs are tried
        // if fewer than two areas fit. Each: (depth, y, z) in the console's space, and the sign's side.
        static List<(Vector3 Spot, float Side)> BlockSpots(List<Vector3> triangles, float face, float outer, Bounds bounds, Vector2 lightsAt, float signSide)
        {
            var result = new List<(Vector3, float)>();
            const int n = 28;
            for (int attempt = 0; attempt < 3 && result.Count < 2; attempt++, signSide *= 0.75f)
            {
                result.Clear();
                float reach = signSide * 0.45f;
                var depth = new float?[n + 1, n + 1];
                var fits = new bool[n + 1, n + 1];
                for (int a = 1; a < n; a++)
                    for (int b = 1; b < n; b++)
                    {
                        float y = bounds.min.y + bounds.size.y * a / n, z = bounds.min.z + bounds.size.z * b / n;
                        depth[a, b] = Depth(triangles, face, outer, new Vector2(y, z));
                        if (!(depth[a, b] is float d)) continue;
                        if (y - reach < bounds.min.y || y + reach > bounds.max.y || z - reach < bounds.min.z || z + reach > bounds.max.z) continue;
                        if ((new Vector2(y, z) - lightsAt).magnitude < signSide * 0.9f) continue;        // clear of the lights
                        bool flat = true;
                        for (int c = 0; c < 8 && flat; c++)
                        {
                            float ang = c * Mathf.PI / 4f;
                            flat = Depth(triangles, face, outer, new Vector2(y + Mathf.Cos(ang) * reach, z + Mathf.Sin(ang) * reach)) is float e
                                   && Mathf.Abs(e - d) < 0.003f;
                        }
                        fits[a, b] = flat;
                    }
                // Group the fitting spots into areas.
                var label = new int[n + 1, n + 1];
                var areas = new List<List<(int, int)>>();
                for (int a = 1; a < n; a++)
                    for (int b = 1; b < n; b++)
                    {
                        if (!fits[a, b] || label[a, b] != 0) continue;
                        var area = new List<(int, int)>();
                        var stack = new Stack<(int, int)>();
                        stack.Push((a, b)); label[a, b] = areas.Count + 1;
                        while (stack.Count > 0)
                        {
                            var (x, y) = stack.Pop();
                            area.Add((x, y));
                            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            {
                                int u = x + dx, v = y + dy;
                                if (u < 1 || v < 1 || u >= n || v >= n || !fits[u, v] || label[u, v] != 0) continue;
                                if (Mathf.Abs(depth[u, v]!.Value - depth[x, y]!.Value) > 0.004f) continue;
                                label[u, v] = areas.Count + 1;
                                stack.Push((u, v));
                            }
                        }
                        areas.Add(area);
                    }
                // The two largest of the proud areas (deeper than the plate's level are left for last).
                areas.Sort((p, q) =>
                {
                    float dp = face * depth[p[0].Item1, p[0].Item2]!.Value, dq = face * depth[q[0].Item1, q[0].Item2]!.Value;
                    if (Mathf.Abs(dp - dq) > 0.01f) return dq.CompareTo(dp);                              // prouder first
                    return q.Count.CompareTo(p.Count);                                                     // then bigger
                });
                foreach (var area in areas)
                {
                    if (area.Count < 2) continue;
                    float my = 0f, mz = 0f;
                    foreach (var (a, b) in area) { my += a; mz += b; }
                    my /= area.Count; mz /= area.Count;
                    var (ba, bb) = area[0];
                    float best = float.MaxValue;
                    foreach (var (a, b) in area) { float d2 = (a - my) * (a - my) + (b - mz) * (b - mz); if (d2 < best) { best = d2; ba = a; bb = b; } }
                    float y = bounds.min.y + bounds.size.y * ba / n, z = bounds.min.z + bounds.size.z * bb / n;
                    result.Add((new Vector3(depth[ba, bb]!.Value, y, z), signSide));
                    if (result.Count == 2) break;
                }
            }
            return result;
        }

        // The console's triangles in its own space (from its readable meshes, not ours).
        static List<Vector3> LocalTriangles(Transform console)
        {
            var result = new List<Vector3>();
            foreach (var filter in console.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null || Ours(filter.transform) || !mesh.isReadable) continue;
                var vertices = mesh.vertices;
                var indices = mesh.triangles;
                for (int i = 0; i < indices.Length; i++)
                    result.Add(console.InverseTransformPoint(filter.transform.TransformPoint(vertices[indices[i]])));
            }
            return result;
        }

        // How deep the face is at (y, z): the first triangle a ray from outside the face meets, going in along X.
        static float? Depth(List<Vector3> triangles, float face, float outer, Vector2 at)
        {
            var origin = new Vector3(outer + face * 0.05f, at.x, at.y);
            var direction = new Vector3(-face, 0f, 0f);
            float best = float.MaxValue;
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                Vector3 a = triangles[i], e1 = triangles[i + 1] - a, e2 = triangles[i + 2] - a;
                var p = Vector3.Cross(direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-9f) continue;
                var tv = origin - a;
                float u = Vector3.Dot(tv, p) / det;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(tv, e1);
                float v = Vector3.Dot(direction, q) / det;
                if (v < 0f || u + v > 1f) continue;
                float t = Vector3.Dot(e2, q) / det;
                if (t > 0f && t < best) best = t;
            }
            return best < float.MaxValue ? origin.x - face * best : (float?)null;
        }

        static bool Ours(Transform t)
        {
            for (var at = t; at != null; at = at.parent) if (at.name.StartsWith("TheExpanse_")) return true;
            return false;
        }

        // Set them into the console's face, once (they then move with it).
        static void Mount(int injector, Transform console, Vector3 outward)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { MountOn(injector, console, outward); }
            finally { Plugin.Log.LogInfo($"[Reactor] injector {injector}: console measured in {watch.Elapsed.TotalMilliseconds:0} ms."); }
        }

        static void MountOn(int injector, Transform console, Vector3 outward)
        {
            sMountedOn[injector] = console;
            // The console's size, in its own space, from its meshes (not ours).
            bool any = false;
            var bounds = new Bounds();
            foreach (var filter in console.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || Ours(filter.transform)) continue;
                var b = filter.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z);
                    var local = console.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(local, Vector3.zero); any = true; } else bounds.Encapsulate(local);
                }
            }
            if (!any) bounds = new Bounds(Vector3.zero, new Vector3(0.1f, 1f, 1f));

            // The face: the X side that looks away from the core; across the face, +Z is to the right seen from +X
            // (and -Z seen from -X).
            float face = Vector3.Dot(console.right, outward) >= 0f ? 1f : -1f;
            float outer = face > 0f ? bounds.max.x : bounds.min.x;
            var triangles = LocalTriangles(console);
            // The plate: the depth most of the face sits at (the handle and its trim stand proud of it), from a grid of rays.
            var depths = new Dictionary<int, int>();
            for (int a = 1; a < 10; a++)
                for (int b = 1; b < 10; b++)
                {
                    var at = new Vector2(bounds.min.y + bounds.size.y * a / 10f, bounds.min.z + bounds.size.z * b / 10f);
                    if (Depth(triangles, face, outer, at) is float hit)
                    {
                        int key = Mathf.RoundToInt(hit * 500f);       // 2 mm steps
                        depths[key] = depths.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                }
            float surface = outer;
            if (depths.Count > 0)
            {
                int best = 0, bestCount = -1;
                foreach (var kv in depths) if (kv.Value > bestCount || (kv.Value == bestCount && face * kv.Key < face * best)) { best = kv.Key; bestCount = kv.Value; }
                surface = best / 500f;
            }
            float right = face;
            var size = bounds.size;
            var center = bounds.center;
            float across = Mathf.Max(0.05f, size.z), along = Mathf.Max(0.05f, size.y);
            float unit = Mathf.Max(1e-4f, Mathf.Abs(console.lossyScale.x));   // the console is normally unscaled

            // Lights: domes set into the face in a row across, just above the handle (which sits low on the face).
            float dome = 0.045f * across;
            for (int k = 0; k < Lights; k++)
            {
                var t = sLights[injector, k]!.transform;
                t.SetParent(console, false);
                t.localRotation = Quaternion.identity;
                t.localScale = new Vector3(dome * 0.45f, dome, dome) / unit;
                float y = center.y - 0.24f * along, z = center.z + right * (0.13f + (k - 1) * 0.085f) * across;
                float under = Depth(triangles, face, outer, new Vector2(y, z)) is float d && Mathf.Abs(d - surface) < 0.03f ? d : surface;
                t.localPosition = new Vector3(under, y, z);
            }
            // Signs: one on each of the two grey mounting blocks (the two largest flat areas standing proud of the plate),
            // sitting on the surface found there, the triangle's base toward the handle.
            var signs = sSigns[injector]!.transform;
            signs.SetParent(console, false);
            signs.localPosition = Vector3.zero;
            signs.localRotation = Quaternion.identity;
            signs.localScale = Vector3.one / unit;
            var lightsAt = new Vector2(center.y - 0.24f * along, center.z + right * 0.13f * across);
            string spotKey = $"{console.name}|{size.x:0.000}|{size.y:0.000}|{size.z:0.000}|{face}";
            if (!sSpots.TryGetValue(spotKey, out var spots)) sSpots[spotKey] = spots = BlockSpots(triangles, face, outer, bounds, lightsAt, 0.2f * across);
            string placed = "";
            for (int k = 0; k < signs.childCount; k++)
            {
                var one = signs.GetChild(k);
                if (k >= spots.Count) { one.gameObject.SetActive(false); placed += " (no second block)"; continue; }
                var (spot, side_) = spots[k];
                // The sign's "up" (its flame's tip) points away from the handle along the console's length, so its base
                // lies square to the block's edge.
                var offset = new Vector2(spot.y - center.y, spot.z - center.z);
                var away = Mathf.Abs(offset.x) >= Mathf.Abs(offset.y) ? new Vector2(Mathf.Sign(offset.x), 0f) : new Vector2(0f, Mathf.Sign(offset.y));
                one.gameObject.SetActive(true);
                one.localScale = Vector3.one * side_;
                one.localRotation = Quaternion.LookRotation(new Vector3(face, 0f, 0f), new Vector3(0f, away.x, away.y));
                one.localPosition = new Vector3(spot.x + face * 0.001f, spot.y, spot.z);
                placed += $" {side_ * 100f:0.0} cm at ({spot.y:0.00}, {spot.z:0.00}), {Mathf.Abs(outer - spot.x) * 100f:0.0} cm below the top;";
            }
            Plugin.Log.LogInfo($"[Reactor] injector {injector}: lights and hazard sign set into its handle console '{console.name}' " +
                               $"(size {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m, face on its {(face > 0f ? "+" : "-")}X side; " +
                               $"{triangles.Count / 3} triangle(s) read, the plate {Mathf.Abs(outer - surface) * 100f:0.0} cm below its outermost point; signs:{placed}).");
        }

        // Two signs (under one holder): a black-edged yellow triangle with a black flame, each layer a little in front of the last, seen from
        // both sides. Built from plain shapes, so it needs no texture.
        static GameObject MakeSign(int injector)
        {
            sYellow ??= FxShape.GlowMaterial(new Color(1f, 0.78f, 0.05f), 1.2f);
            sBlack ??= FxShape.GlowMaterial(new Color(0.02f, 0.02f, 0.02f), 0f);
            sOuter ??= Triangle(1f);
            sInner ??= Triangle(0.78f);
            sFlame ??= Flame();
            var signs = new GameObject($"TheExpanse_HazardSigns_{injector}");
            signs.transform.SetParent(sRoot!.transform, false);
            for (int k = 0; k < 2; k++)
            {
                var sign = new GameObject($"TheExpanse_HazardSign_{injector}_{k}");
                sign.transform.SetParent(signs.transform, false);
                Layer(sign, "Edge", sOuter, sBlack, 0f);
                Layer(sign, "Field", sInner, sYellow, 0.004f);
                Layer(sign, "Flame", sFlame, sBlack, 0.008f);
            }
            return signs;
        }

        static void Layer(GameObject sign, string name, Mesh mesh, Material material, float forward)
        {
            var layer = new GameObject(name);
            layer.transform.SetParent(sign.transform, false);
            layer.transform.localPosition = new Vector3(0f, 0f, forward);
            layer.AddComponent<MeshFilter>().sharedMesh = mesh;
            layer.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        // An upright equilateral triangle of the given side (in sign units), centered on its middle, in the XY plane.
        static Mesh Triangle(float side)
        {
            float h = side * 0.866f;
            var points = new List<Vector2> { new Vector2(-side / 2f, -h / 3f), new Vector2(0f, h * 2f / 3f), new Vector2(side / 2f, -h / 3f) };
            return Fan(points, new Vector2(0f, 0f));
        }

        // A flame, as on the standard flammable warning sign: a round base rising to a tip leaning right, with a smaller
        // tongue on the left. Key points smoothed into a curve (Catmull-Rom).
        static readonly Vector2[] sFlameKeys =
        {
            new Vector2(0f, -0.17f), new Vector2(0.10f, -0.155f), new Vector2(0.15f, -0.09f), new Vector2(0.16f, 0f),
            new Vector2(0.13f, 0.09f), new Vector2(0.075f, 0.17f), new Vector2(0.045f, 0.24f), new Vector2(0.045f, 0.31f),
            new Vector2(0f, 0.25f), new Vector2(-0.035f, 0.17f), new Vector2(-0.045f, 0.10f), new Vector2(-0.075f, 0.13f),
            new Vector2(-0.085f, 0.19f), new Vector2(-0.13f, 0.09f), new Vector2(-0.16f, 0f), new Vector2(-0.15f, -0.09f),
            new Vector2(-0.10f, -0.155f),
        };

        static Mesh Flame()
        {
            var points = new List<Vector2>();
            int n = sFlameKeys.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = sFlameKeys[(i + n - 1) % n], p1 = sFlameKeys[i], p2 = sFlameKeys[(i + 1) % n], p3 = sFlameKeys[(i + 2) % n];
                for (int k = 0; k < 6; k++)
                {
                    float t = k / 6f, t2 = t * t, t3 = t2 * t;
                    points.Add(0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                }
            }
            return Filled(points);
        }

        // A filled outline of any (simple) shape, by ear clipping, both faces.
        static Mesh Filled(List<Vector2> outline)
        {
            var vertices = new List<Vector3>();
            foreach (var p in outline) vertices.Add(p);
            float area = 0f;
            for (int i = 0; i < outline.Count; i++) { var a = outline[i]; var b = outline[(i + 1) % outline.Count]; area += a.x * b.y - b.x * a.y; }
            var left = new List<int>();
            for (int i = 0; i < outline.Count; i++) left.Add(i);
            if (area < 0f) left.Reverse();                     // counter-clockwise
            var triangles = new List<int>();
            int guard = 0;
            while (left.Count > 3 && guard++ < 10000)
            {
                bool clipped = false;
                for (int i = 0; i < left.Count; i++)
                {
                    int ia = left[(i + left.Count - 1) % left.Count], ib = left[i], ic = left[(i + 1) % left.Count];
                    Vector2 a = outline[ia], b = outline[ib], c = outline[ic];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0f) continue;      // a reflex corner
                    bool inside = false;
                    foreach (int j in left)
                        if (j != ia && j != ib && j != ic && InTriangle(outline[j], a, b, c)) { inside = true; break; }
                    if (inside) continue;
                    triangles.Add(ia); triangles.Add(ib); triangles.Add(ic);
                    triangles.Add(ia); triangles.Add(ic); triangles.Add(ib);
                    left.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (left.Count == 3)
            {
                triangles.Add(left[0]); triangles.Add(left[1]); triangles.Add(left[2]);
                triangles.Add(left[0]); triangles.Add(left[2]); triangles.Add(left[1]);
            }
            var mesh = new Mesh { name = "TheExpanse_Sign" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        // A filled outline (seen from its center), both faces.
        static Mesh Fan(List<Vector2> outline, Vector2 center)
        {
            var vertices = new List<Vector3> { center };
            foreach (var p in outline) vertices.Add(p);
            var triangles = new List<int>();
            for (int i = 0; i < outline.Count; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % outline.Count;
                triangles.Add(0); triangles.Add(a); triangles.Add(b);
                triangles.Add(0); triangles.Add(b); triangles.Add(a);
            }
            var mesh = new Mesh { name = "TheExpanse_Sign" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
