using System.Collections.Generic;
using UnityEngine;

namespace BiggerBays
{
    // Draws statically batched renderers somewhere other than where they were baked.
    //
    // A statically batched renderer is part of a combined mesh whose vertices are already in world space, so it does not
    // follow its transform, and the mesh it was made from is not in memory. The combined mesh is not readable either, but
    // it can still be drawn. Each batched renderer owns a run of consecutive submeshes in it (one per material); that run
    // is found by matching submesh bounds to the renderer's own bounds.
    //
    // Every renderer registered here has a list of placements, each a move (and possibly a turn) away from its baked
    // position. A placement that changes nothing is the original itself, left to the game to draw. Any other placement is
    // drawn here every frame. If no placement is the unchanged one the original is hidden, which is how a renderer is
    // moved; an extra placement next to the unchanged one is how it is copied.
    internal static class BatchedRedraw
    {
        sealed class Entry
        {
            public MeshRenderer Original = null!;
            public Mesh Mesh = null!;
            public int FirstSubMesh;
            public int Count;
            public readonly List<Matrix4x4> Placements = new List<Matrix4x4> { Matrix4x4.identity };
            public bool Hidden;
            public int Id;
        }

        static readonly Dictionary<int, Entry> sEntries = new Dictionary<int, Entry>();
        static readonly List<Entry> sList = new List<Entry>();
        static readonly Dictionary<int, Bounds[]> sSubMeshBounds = new Dictionary<int, Bounds[]>();
        static readonly HashSet<long> sTaken = new HashSet<long>();
        static readonly MaterialPropertyBlock sBlock = new MaterialPropertyBlock();

        internal static int Failures;      // renderers that could not be matched, since the count was last reset

        static bool IsUnchanged(Matrix4x4 m)
        {
            var id = Matrix4x4.identity;
            for (int i = 0; i < 16; i++) if (Mathf.Abs(m[i] - id[i]) > 1e-4f) return false;
            return true;
        }

        static Entry? Get(MeshRenderer r)
        {
            int id = r.GetInstanceID();
            if (sEntries.TryGetValue(id, out var have)) return have;

            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            int mats = r.sharedMaterials.Length;
            if (mesh == null || mats == 0) { Failures++; return null; }

            int meshId = mesh.GetInstanceID();
            if (!sSubMeshBounds.TryGetValue(meshId, out var subs))
            {
                subs = new Bounds[mesh.subMeshCount];
                for (int i = 0; i < subs.Length; i++) subs[i] = mesh.GetSubMesh(i).bounds;
                sSubMeshBounds[meshId] = subs;
            }

            // The closest run of submeshes; among equally good ones, the first that no other renderer has claimed (two
            // renderers can share the same bounds exactly).
            var want = r.bounds;
            int best = -1; float bestErr = float.MaxValue;
            for (int s = 0; s + mats <= subs.Length; s++)
            {
                var b = subs[s];
                for (int k = 1; k < mats; k++) b.Encapsulate(subs[s + k]);
                float err = (b.min - want.min).magnitude + (b.max - want.max).magnitude;
                if (err > 0.1f) continue;
                bool taken = sTaken.Contains(((long)meshId << 32) | (uint)s);
                if (taken) err += 0.05f;
                if (err < bestErr) { bestErr = err; best = s; }
            }
            if (best < 0)
            {
                Failures++;
                return null;
            }
            sTaken.Add(((long)meshId << 32) | (uint)best);

            var e = new Entry { Original = r, Mesh = mesh, FirstSubMesh = best, Count = mats, Id = id };
            sEntries[id] = e;
            sList.Add(e);
            return e;
        }

        // Replaces a renderer's placements. An empty list, or just the unchanged one, puts it back as the game had it.
        internal static bool Set(MeshRenderer r, IList<Matrix4x4> placements)
        {
            var e = Get(r);
            if (e == null) return false;
            e.Placements.Clear();
            foreach (var m in placements) e.Placements.Add(m);
            if (e.Placements.Count == 0) e.Placements.Add(Matrix4x4.identity);
            Refresh(e);
            return true;
        }

        // Moves everything drawn for a renderer (the original and any copies) by the same amount.
        internal static bool Shift(MeshRenderer r, Vector3 delta)
        {
            var e = Get(r);
            if (e == null) return false;
            var move = Matrix4x4.Translate(delta);
            for (int i = 0; i < e.Placements.Count; i++) e.Placements[i] = move * e.Placements[i];
            Refresh(e);
            return true;
        }

        // Hidden two ways. The scanner highlight swaps a renderer's materials for its outline material and switches the
        // renderer back ON, which would draw the outline where the object used to stand; forceRenderingOff keeps it
        // undrawn through that, and Draw picks the outline material up from it instead.
        static void Refresh(Entry e)
        {
            bool hide = true;
            foreach (var m in e.Placements) if (IsUnchanged(m)) { hide = false; break; }
            if (hide == e.Hidden || e.Original == null) return;
            e.Hidden = hide;
            e.Original.forceRenderingOff = hide;
            e.Original.enabled = !hide;
        }

        internal static int Count => sList.Count;

        // Called every frame.
        internal static void Draw()
        {
            for (int i = sList.Count - 1; i >= 0; i--)
            {
                var e = sList[i];
                if (e.Original == null || e.Mesh == null)       // level unloaded
                {
                    sList.RemoveAt(i);
                    sEntries.Remove(e.Id);
                    if (sList.Count == 0) { sSubMeshBounds.Clear(); sTaken.Clear(); }
                    continue;
                }
                if (e.Placements.Count == 1 && IsUnchanged(e.Placements[0])) continue;
                var go = e.Original.gameObject;
                if (!go.activeInHierarchy) continue;
                // A renderer the game itself has switched off stays off; one hidden here does not count.
                if (!e.Hidden && !e.Original.enabled) continue;

                // Whatever the original would be drawn with right now: its own materials, or the scanner's outline
                // material and settings while the scanner has it highlighted.
                var mats = e.Original.sharedMaterials;
                MaterialPropertyBlock? block = null;
                if (e.Original.HasPropertyBlock()) { e.Original.GetPropertyBlock(sBlock); block = sBlock; }
                foreach (var matrix in e.Placements)
                {
                    if (IsUnchanged(matrix)) continue;
                    for (int k = 0; k < e.Count && k < mats.Length; k++)
                        if (mats[k] != null)
                            Graphics.DrawMesh(e.Mesh, matrix, mats[k], go.layer, null, e.FirstSubMesh + k, block,
                                              e.Original.shadowCastingMode, e.Original.receiveShadows);
                }
            }
        }
    }
}
