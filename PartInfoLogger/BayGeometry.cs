using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BBI.Unity.Game;
using UnityEngine;

namespace PartInfoLogger
{
    // Answers "what does the bay actually look like, in the same coordinates as the ship?" - a full record of the bay
    // walls, the Master Jack, the floating jacks, the barge and the furnace / processor mouths, taken from the running game.
    //
    //   bay_geometry.csv        one row per collider and per mesh renderer that is NOT part of the ship, within
    //                           RadiusMeters of the ship, written once per ship load shortly after gameplay starts.
    //                           Columns: what it is, where (transform, world box, local box, local-to-world matrix)
    //                           and which mesh it uses (MeshIndex into the two files below, -1 = none / not recorded).
    //   bay_geometry_meshes.json index of the distinct meshes: name, vertex and triangle counts, byte offsets.
    //   bay_geometry_meshes.bin  the mesh data itself, little-endian: for each mesh its vertices (3 x float32, mesh-local)
    //                           followed by its triangle indices (3 x int32). World position = LocalToWorld x vertex.
    //
    // Box, sphere and capsule colliders are fully described by their row. A mesh the game keeps non-readable cannot be
    // read back, so it is listed with Readable=False and only its boxes.
    // Ship parts (anything under a StructurePart) are left out: joint_census_spawn.csv already lists those.
    // The jacks float, so this is where they were at spawn, not where they drift to later.
    internal static class BayGeometry
    {
        static bool s_Ran;

        internal static void OnGameplay()
        {
            if (s_Ran || !Plugin.BayGeometryEnabled.Value) return;
            s_Ran = true;
            Plugin.Instance.StartCoroutine(Run());
        }

        // Leaving the level re-arms the one-shot for the next ship.
        internal static void OnLeftLevel() => s_Ran = false;

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => F(v.x) + " " + F(v.y) + " " + F(v.z);

        static string M(Matrix4x4 m)
        {
            var sb = new StringBuilder();
            for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) { if (sb.Length > 0) sb.Append(' '); sb.Append(m[r, c].ToString("0.#####", CultureInfo.InvariantCulture)); }
            return sb.ToString();                                  // 3 rows x 4 columns, row-major (the 4th row is 0 0 0 1)
        }

        static string Csv(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string Json(string? s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 12; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static IEnumerator Run()
        {
            yield return new WaitForSeconds(Plugin.BayGeometryDelay.Value);
            try { Dump(); }
            catch (Exception ex) { Plugin.Log.LogError("[BayGeometry] " + ex); }
        }

        // ---- mesh store -------------------------------------------------------------------------------------------
        sealed class Store
        {
            public readonly Dictionary<int, int> Index = new Dictionary<int, int>();      // mesh instance id -> index (or -1)
            public readonly List<string> Entries = new List<string>();
            public readonly MemoryStream Bin = new MemoryStream();
            public long Verts, Budget;
            public int Unreadable, OverBudget;

            public int Add(Mesh? mesh, out bool readable)
            {
                readable = false;
                if (mesh == null) return -1;
                readable = mesh.isReadable;
                if (Index.TryGetValue(mesh.GetInstanceID(), out var known)) return known;
                int idx = -1;
                if (!readable) Unreadable++;
                else if (Verts + mesh.vertexCount > Budget) OverBudget++;
                else
                {
                    try
                    {
                        var v = mesh.vertices; var t = mesh.triangles;
                        long off = Bin.Position;
                        var buf = new byte[v.Length * 12 + t.Length * 4];
                        var fl = new float[v.Length * 3];
                        for (int i = 0; i < v.Length; i++) { fl[i * 3] = v[i].x; fl[i * 3 + 1] = v[i].y; fl[i * 3 + 2] = v[i].z; }
                        Buffer.BlockCopy(fl, 0, buf, 0, fl.Length * 4);
                        Buffer.BlockCopy(t, 0, buf, fl.Length * 4, t.Length * 4);
                        Bin.Write(buf, 0, buf.Length);
                        idx = Entries.Count; Verts += v.Length;
                        var b = mesh.bounds;
                        Entries.Add("{\"index\":" + idx + ",\"name\":" + Json(mesh.name) + ",\"verts\":" + v.Length + ",\"tris\":" + (t.Length / 3) +
                                    ",\"offset\":" + off + ",\"boundsCenter\":[" + F(b.center.x) + "," + F(b.center.y) + "," + F(b.center.z) + "]" +
                                    ",\"boundsSize\":[" + F(b.size.x) + "," + F(b.size.y) + "," + F(b.size.z) + "]}");
                    }
                    catch (Exception) { Unreadable++; idx = -1; readable = false; }
                }
                Index[mesh.GetInstanceID()] = idx;
                return idx;
            }
        }

        static void Dump()
        {
            // Centre of the ship = mean position of its structure parts; everything is filtered by distance from there.
            var sps = Resources.FindObjectsOfTypeAll<StructurePart>().Where(s => s != null && s.gameObject.scene.IsValid()).ToArray();
            var centre = Vector3.zero;
            if (sps.Length > 0) { foreach (var s in sps) centre += s.transform.position; centre /= sps.Length; }
            float radius = Plugin.BayGeometryRadius.Value;
            var store = new Store { Budget = Plugin.BayGeometryMaxVerts.Value };

            var sb = new StringBuilder();
            sb.AppendLine("Kind,Name,Path,Root,IsTrigger,Enabled,Active,Layer,Body,WorldPos,WorldEuler,LossyScale," +
                          "WorldBoundsCenter,WorldBoundsSize,LocalCenter,LocalSize,MeshName,MeshVerts,MeshIndex,Readable,LocalToWorld");
            int nCol = 0, nRen = 0, nSkipShip = 0, nSkipFar = 0;

            foreach (var c in Resources.FindObjectsOfTypeAll<Collider>())
            {
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                if (c.GetComponentInParent<StructurePart>() != null) { nSkipShip++; continue; }
                var b = c.bounds;
                // A disabled or inactive collider reports empty bounds; fall back to the transform for the distance test.
                var where = b.size == Vector3.zero ? c.transform.position : b.center;
                if ((where - centre).magnitude - b.extents.magnitude > radius) { nSkipFar++; continue; }

                Vector3 lc = Vector3.zero, ls = Vector3.zero; string meshName = ""; int verts = -1, mi = -1; bool readable = false;
                switch (c)
                {
                    case BoxCollider bc: lc = bc.center; ls = bc.size; break;
                    case SphereCollider sc: lc = sc.center; ls = Vector3.one * (sc.radius * 2f); break;
                    case CapsuleCollider cc:
                        lc = cc.center; ls = Vector3.one * (cc.radius * 2f);
                        if (cc.direction == 0) ls.x = cc.height; else if (cc.direction == 1) ls.y = cc.height; else ls.z = cc.height;
                        break;
                    case MeshCollider mc when mc.sharedMesh != null:
                        lc = mc.sharedMesh.bounds.center; ls = mc.sharedMesh.bounds.size; meshName = mc.sharedMesh.name; verts = mc.sharedMesh.vertexCount;
                        mi = store.Add(mc.sharedMesh, out readable);
                        break;
                }
                var rb = c.attachedRigidbody;
                Row(sb, c.GetType().Name, c.transform, c.isTrigger, c.enabled, rb == null ? "static" : rb.isKinematic ? "kinematic" : "dynamic",
                    b.center, b.size, lc, ls, meshName, verts, mi, readable);
                nCol++;
            }

            if (Plugin.BayGeometryRenderers.Value)
            {
                foreach (var r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
                {
                    if (r == null || !r.gameObject.scene.IsValid()) continue;
                    if (r.GetComponentInParent<StructurePart>() != null) continue;
                    var b = r.bounds;
                    if ((b.center - centre).magnitude - b.extents.magnitude > radius) continue;
                    var mf = r.GetComponent<MeshFilter>(); var m = mf != null ? mf.sharedMesh : null;
                    int mi = store.Add(m, out bool readable);
                    Row(sb, "MeshRenderer", r.transform, false, r.enabled, "none", b.center, b.size,
                        m != null ? m.bounds.center : Vector3.zero, m != null ? m.bounds.size : Vector3.zero, m != null ? m.name : "", m != null ? m.vertexCount : -1, mi, readable);
                    nRen++;
                }
            }

            var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
            File.WriteAllText(Path.Combine(outDir, "bay_geometry.csv"), sb.ToString());
            File.WriteAllBytes(Path.Combine(outDir, "bay_geometry_meshes.bin"), store.Bin.ToArray());
            File.WriteAllText(Path.Combine(outDir, "bay_geometry_meshes.json"),
                "{\"shipCentre\":[" + F(centre.x) + "," + F(centre.y) + "," + F(centre.z) + "],\"radius\":" + F(radius) +
                ",\"format\":\"per mesh at offset: verts x 3 float32 (mesh-local), then tris x 3 int32; little-endian\",\"meshes\":[\n" +
                string.Join(",\n", store.Entries) + "\n]}");
            Plugin.Log.LogInfo($"[BayGeometry] {nCol} collider(s) and {nRen} renderer(s) within {radius:0} m of the ship centre {V(centre)}; " +
                               $"{store.Entries.Count} mesh(es) recorded ({store.Verts} vertices, {store.Bin.Length / 1048576f:0.0} MB), " +
                               $"{store.Unreadable} not readable, {store.OverBudget} over the vertex budget; " +
                               $"{nSkipShip} ship collider(s) and {nSkipFar} distant one(s) left out -> {outDir}");
        }

        static void Row(StringBuilder sb, string kind, Transform t, bool trigger, bool enabled, string body,
                        Vector3 wc, Vector3 ws, Vector3 lc, Vector3 ls, string meshName, int verts, int meshIndex, bool readable)
        {
            sb.Append(kind).Append(',').Append(Csv(t.name)).Append(',').Append(Csv(PathOf(t))).Append(',').Append(Csv(t.root.name)).Append(',')
              .Append(trigger).Append(',').Append(enabled).Append(',').Append(t.gameObject.activeInHierarchy).Append(',')
              .Append(Csv(LayerMask.LayerToName(t.gameObject.layer))).Append(',').Append(body).Append(',')
              .Append(V(t.position)).Append(',').Append(V(t.eulerAngles)).Append(',').Append(V(t.lossyScale)).Append(',')
              .Append(V(wc)).Append(',').Append(V(ws)).Append(',').Append(V(lc)).Append(',').Append(V(ls)).Append(',')
              .Append(Csv(meshName)).Append(',').Append(verts).Append(',').Append(meshIndex).Append(',').Append(readable).Append(',')
              .Append(M(t.localToWorldMatrix)).AppendLine();
        }
    }
}
