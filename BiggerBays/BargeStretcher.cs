using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BiggerBays
{
    // Makes the barge longer and wider to match the bay: longer from its end by the back wall, which stays where it is,
    // and wider about its own middle.
    //
    // The stock barge (root "PRF_Barge_SmallBay (1)") is 39.5 m wide and 83.9 m long, from z = -2.4 to z = 81.4. It is a
    // holo field top and bottom, a ring top and bottom, chevrons, plus signs and holo sprites, and three box volumes: the
    // entrance trigger that takes salvage, the blocker over a full barge, and the flow that pulls salvage in. Its rim
    // lights are a separate group, PRF_BayAnimatedEmmissiveLighting/BargeEmissiveLights.
    //
    // Everything is mapped along Z about the barge's back-wall end and across X about its middle: a point at distance d
    // from there goes to d x k, where k is new size / old size in that direction.
    //   long or wide pieces (fields, rings,     are stretched by k in that direction
    //   the volumes)
    //   small pieces (chevrons, plus signs,     keep their size and are moved to their mapped place
    //   sprites, lights)
    // Statically batched meshes are drawn stretched or moved by BatchedRedraw; everything else has its transform scaled
    // or moved. The children of a scaled object are stretched with it.
    internal static class BargeStretcher
    {
        const string MarkerName = "BiggerBays_BargeApplied";
        const string BargePrefix = "PRF_Barge_SmallBay";
        const string LightsRoot = "PRF_BayAnimatedEmmissiveLighting";
        const string LightsPath = "BargeEmissiveLights";
        // A piece at least this big in a direction is part of the barge's size that way and is stretched.
        const float Big = 30f;

        sealed class Node
        {
            public Transform T = null!;
            public Vector3 Stock, LocalStock;
            public Bounds Box;
            public MeshRenderer? Batched;
        }

        // The barge's start-up animation may write positions and sizes back; what was changed here is put back on top.
        sealed class Pin
        {
            public Transform T = null!;
            public Vector3 PosDelta, LastPos, LastScale;
            public Vector3 Factor = Vector3.one;        // per local axis
            public bool Scaled;
        }

        static readonly List<Pin> sPins = new List<Pin>();

        internal static void OnLevelLoadStart() => sPins.Clear();

        internal static void Apply()
        {
            if (!Plugin.BiggerBay.Value) return;
            // The barge follows the bay: as much longer, and as much wider on each side.
            float longer = BayReshaper.AppliedLength;
            float wider = BayReshaper.AppliedWidth;
            if (longer <= 0f && wider <= 0f) return;

            GameObject? barge = null, lightsTop = null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                {
                    if (!go.activeInHierarchy) continue;
                    if (barge == null && go.name.StartsWith(BargePrefix, StringComparison.Ordinal)) barge = go;
                    else if (lightsTop == null && go.name == LightsRoot) lightsTop = go;
                }
            }
            if (barge == null) return;                                          // no barge in this level
            if (barge.transform.Find(MarkerName) != null) return;               // this barge has been done already

            var roots = new List<Transform> { barge.transform };
            var lights = lightsTop != null ? lightsTop.transform.Find(LightsPath) : null;
            if (lights != null) roots.Add(lights);
            else Plugin.Log.LogWarning("[Barge] the barge's rim lights were not found; they stay where they are.");

            // Everything, parents before children, as the game placed it.
            var nodes = new List<Node>();
            int bargeNodes = 0;
            foreach (var root in roots)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var n = new Node { T = t, Stock = t.position, LocalStock = t.localPosition };
                    n.Box = BayReshaper.BoxOf(t, out n.Batched);
                    nodes.Add(n);
                }
                if (root == barge.transform) bargeNodes = nodes.Count;
            }

            // The barge's size is that of its holo field (failing that, of its longest piece).
            Bounds field = default;
            for (int i = 0; i < bargeNodes; i++)
                if (nodes[i].T.name.IndexOf("Holo Field", StringComparison.OrdinalIgnoreCase) >= 0 && nodes[i].T.GetComponent<MeshRenderer>() != null
                    && nodes[i].Box.size.z > field.size.z) field = nodes[i].Box;
            if (field.size.z < Big)
                for (int i = 0; i < bargeNodes; i++)
                    if (nodes[i].Box.size.z > field.size.z) field = nodes[i].Box;
            float length = field.size.z, width = field.size.x;
            if (length < Big || width < 1f)
            {
                Plugin.Log.LogWarning($"[Barge] could not work out the barge's size (largest piece is {width:0.0} x {length:0.0} m); it is left as it is.");
                return;
            }
            float z0 = field.min.z, x0 = field.center.x;
            float kz = (length + longer) / length;
            float kx = (width + 2f * wider) / width;
            Vector3 Map(Vector3 p) => new Vector3(x0 + (p.x - x0) * kx, p.y, z0 + (p.z - z0) * kz);

            var inherits = new HashSet<Transform>();     // scaled objects and everything under them
            var one = new List<Matrix4x4>(1);
            int stretched = 0, moved = 0, failed = 0;
            foreach (var n in nodes)
            {
                var t = n.T;
                if (t.parent != null && inherits.Contains(t.parent)) { inherits.Add(t); continue; }

                // Stretched in a direction if it is big that way; otherwise only moved to its mapped place.
                float sx = n.Box.size.x >= Big ? kx : 1f;
                float sz = n.Box.size.z >= Big ? kz : 1f;
                var center = n.Box.center;
                var shift = Map(center) - center;
                var target = n.Stock + shift;
                bool scale = sx != 1f || sz != 1f;

                if (n.Batched != null)
                {
                    one.Clear();
                    one.Add(Matrix4x4.Translate(center + shift) * Matrix4x4.Scale(new Vector3(sx, 1f, sz)) * Matrix4x4.Translate(-center));
                    if (!BatchedRedraw.Set(n.Batched, one)) failed++;
                    else if (scale) stretched++;
                    else if (shift != Vector3.zero) moved++;
                }
                else if (scale)
                {
                    // Scale along whichever of the object's own axes lies along the bay, and whichever lies across it.
                    var factor = Vector3.one;
                    if (sz != 1f) factor[AxisAlong(t, Vector3.forward)] *= sz;
                    if (sx != 1f) factor[AxisAlong(t, Vector3.right)] *= sx;
                    var s = Vector3.Scale(t.localScale, factor);
                    t.localScale = s;
                    inherits.Add(t);
                    stretched++;
                    sPins.Add(new Pin { T = t, Factor = factor, Scaled = true, LastScale = s });
                }
                else if (n.Box.size != Vector3.zero && shift != Vector3.zero) moved++;

                if ((t.position - target).sqrMagnitude > 1e-8f)
                {
                    t.position = target;
                    var rb = t.GetComponent<Rigidbody>();
                    if (rb != null) rb.position = target;
                }
            }

            // Positions, relative to each parent, as they now are.
            foreach (var n in nodes)
            {
                if (n.T.parent != null && inherits.Contains(n.T.parent)) continue;
                var delta = n.T.localPosition - n.LocalStock;
                if (delta.sqrMagnitude < 1e-8f) continue;
                Pin? pin = null;
                foreach (var p in sPins) if (p.T == n.T) { pin = p; break; }
                if (pin == null) sPins.Add(pin = new Pin { T = n.T });
                pin.PosDelta = delta;
                pin.LastPos = n.T.localPosition;
            }

            Physics.SyncTransforms();
            var marker = new GameObject(MarkerName);
            marker.transform.SetParent(barge.transform, false);

            Plugin.Log.LogInfo($"[Barge] barge made {longer:0.0} m longer and {wider:0.0} m wider on each side: {width:0.0} x {length:0.0} m -> " +
                               $"{width + 2f * wider:0.0} x {length + longer:0.0} m; {stretched} piece(s) stretched, {moved} moved" +
                               $"{(failed > 0 ? $", {failed} mesh(es) could not be matched and stay as they were" : "")}.");
        }

        // Which of an object's own axes (0 = x, 1 = y, 2 = z) lies closest to a world direction.
        static int AxisAlong(Transform t, Vector3 world)
        {
            int axis = 0; float best = -1f;
            for (int a = 0; a < 3; a++)
            {
                var dir = t.rotation * (a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward);
                float d = Mathf.Abs(Vector3.Dot(dir, world));
                if (d > best) { best = d; axis = a; }
            }
            return axis;
        }

        // Called every frame, after animations have run.
        internal static void Tick()
        {
            for (int i = sPins.Count - 1; i >= 0; i--)
            {
                var p = sPins[i];
                if (p.T == null) { sPins.RemoveAt(i); continue; }

                if (p.PosDelta != Vector3.zero)
                {
                    var lp = p.T.localPosition;
                    if ((lp - p.LastPos).sqrMagnitude > 1e-8f)
                    {
                        lp += p.PosDelta;
                        p.T.localPosition = lp;
                        p.LastPos = lp;
                    }
                }
                if (p.Scaled)
                {
                    var ls = p.T.localScale;
                    if ((ls - p.LastScale).sqrMagnitude > 1e-10f)
                    {
                        ls = Vector3.Scale(ls, p.Factor);
                        p.T.localScale = ls;
                        p.LastScale = ls;
                    }
                }
            }
        }
    }
}
