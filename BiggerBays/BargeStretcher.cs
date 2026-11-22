using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BiggerBays
{
    // Makes the barge longer along the bay, keeping its end by the back wall where it is.
    //
    // The stock barge (root "PRF_Barge_SmallBay (1)") is 39.5 m wide and 83.9 m long, from z = -2.4 to z = 81.4. It is a
    // holo field top and bottom, a ring top and bottom, chevrons, plus signs and holo sprites, and three box volumes: the
    // entrance trigger that takes salvage, the blocker over a full barge, and the flow that pulls salvage in. Its rim
    // lights are a separate group, PRF_BayAnimatedEmmissiveLighting/BargeEmissiveLights.
    //
    // Everything is mapped along Z about the barge's back-wall end: a point at distance d from that end goes to d x k,
    // where k is new length / old length.
    //   long pieces (fields, rings, the volumes)   are stretched by k
    //   short pieces (chevrons, plus signs,        keep their size and are moved to their mapped place
    //   sprites, lights)
    // Statically batched meshes are drawn stretched or moved by BatchedRedraw; everything else has its transform scaled
    // or moved. The children of a scaled object are stretched with it.
    internal static class BargeStretcher
    {
        const string MarkerName = "BiggerBays_BargeApplied";
        const string BargePrefix = "PRF_Barge_SmallBay";
        const string LightsRoot = "PRF_BayAnimatedEmmissiveLighting";
        const string LightsPath = "BargeEmissiveLights";
        // A piece at least this long along the bay is part of the barge's length and is stretched.
        const float Long = 30f;

        sealed class Node
        {
            public Transform T = null!;
            public Vector3 Stock, LocalStock, LocalScaleStock;
            public Bounds Box;
            public MeshRenderer? Batched;
        }

        // The barge's start-up animation may write positions and sizes back; what was changed here is put back on top.
        sealed class Pin
        {
            public Transform T = null!;
            public Vector3 PosDelta, LastPos, LastScale;
            public int Axis = -1;
            public float K = 1f;
        }

        static readonly List<Pin> sPins = new List<Pin>();

        static string V(Vector3 v) => $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";

        internal static void OnLevelLoadStart()
        {
            sPins.Clear();
        }

        internal static void Apply()
        {
            float extra = Plugin.ExtraBargeLength.Value;
            if (extra < 0f) extra = BayReshaper.AppliedLength;
            extra = Mathf.Clamp(extra, 0f, 300f);
            if (!Plugin.BiggerBay.Value || extra <= 0f) return;

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
                    var n = new Node { T = t, Stock = t.position, LocalStock = t.localPosition, LocalScaleStock = t.localScale };
                    n.Box = BayReshaper.BoxOf(t, out n.Batched);
                    nodes.Add(n);
                }
                if (root == barge.transform) bargeNodes = nodes.Count;
            }

            // The barge's length is that of its holo field (failing that, of its longest piece).
            Bounds longest = default;
            for (int i = 0; i < bargeNodes; i++)
                if (nodes[i].T.name.IndexOf("Holo Field", StringComparison.OrdinalIgnoreCase) >= 0 && nodes[i].T.GetComponent<MeshRenderer>() != null
                    && nodes[i].Box.size.z > longest.size.z) longest = nodes[i].Box;
            if (longest.size.z < Long)
                for (int i = 0; i < bargeNodes; i++)
                    if (nodes[i].Box.size.z > longest.size.z) longest = nodes[i].Box;
            float length = longest.size.z;
            if (length < Long)
            {
                Plugin.Log.LogWarning($"[Barge] could not work out the barge's length (longest piece is {length:0.0} m); it is left as it is.");
                return;
            }
            float z0 = longest.min.z;
            float k = (length + extra) / length;
            float Map(float z) => z0 + (z - z0) * k;
            var stretch = Matrix4x4.Translate(new Vector3(0f, 0f, z0)) * Matrix4x4.Scale(new Vector3(1f, 1f, k)) * Matrix4x4.Translate(new Vector3(0f, 0f, -z0));

            var inherits = new HashSet<Transform>();     // scaled objects and everything under them
            var one = new List<Matrix4x4>(1);
            int stretched = 0, moved = 0, redrawn = 0, failed = 0;
            foreach (var n in nodes)
            {
                var t = n.T;
                if (t.parent != null && inherits.Contains(t.parent)) { inherits.Add(t); continue; }

                bool isLong = n.Box.size.z >= Long;
                float shift = Map(n.Box.center.z) - n.Box.center.z;
                var target = n.Stock + new Vector3(0f, 0f, shift);

                if (n.Batched != null)
                {
                    one.Clear();
                    one.Add(isLong ? stretch : Matrix4x4.Translate(new Vector3(0f, 0f, shift)));
                    if (BatchedRedraw.Set(n.Batched, one)) redrawn++; else failed++;
                    if (isLong) stretched++; else if (shift != 0f) moved++;
                }
                else if (isLong)
                {
                    // Scale along whichever of the object's own axes lies along the bay.
                    int axis = 0; float best = -1f;
                    for (int a = 0; a < 3; a++)
                    {
                        var dir = t.rotation * (a == 0 ? Vector3.right : a == 1 ? Vector3.up : Vector3.forward);
                        if (Mathf.Abs(dir.z) > best) { best = Mathf.Abs(dir.z); axis = a; }
                    }
                    var s = t.localScale;
                    s[axis] *= k;
                    t.localScale = s;
                    inherits.Add(t);
                    stretched++;
                    sPins.Add(new Pin { T = t, Axis = axis, K = k, LastScale = s });
                }
                else if (n.Box.size != Vector3.zero && shift != 0f) moved++;

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

            Plugin.Log.LogInfo($"[Barge] barge made {extra:0.0} m longer: {length:0.0} m -> {length + extra:0.0} m (z {z0:0.0} to {z0 + length + extra:0.0}), " +
                               $"{stretched} piece(s) stretched, {moved} moved, {redrawn} batched mesh(es) redrawn" +
                               $"{(failed > 0 ? $", {failed} batched NOT matched (left as they were)" : "")}.");
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
                if (p.Axis >= 0)
                {
                    var ls = p.T.localScale;
                    if ((ls - p.LastScale).sqrMagnitude > 1e-10f)
                    {
                        ls[p.Axis] *= p.K;
                        p.T.localScale = ls;
                        p.LastScale = ls;
                    }
                }
            }
        }
    }
}
