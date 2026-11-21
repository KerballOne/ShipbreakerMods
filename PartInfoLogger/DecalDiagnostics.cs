using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBI.Unity.Game;
using Unity.Entities;
using UnityEngine;

namespace PartInfoLogger
{
    // Answers "why does a baked decal not show?" for decals spawned on a (modded) ship.
    //
    // The game bakes decals once, at ship spawn (DecalBakerSystem): every BakedDecalProjector whose box meets a
    // StructurePart writes planar UVs into that part's mesh UV channel 2 or 3 and sets _BakedDecalTexture01/02 on the
    // part's material, but only for materials with the keyword _BAKED_DECALS_ON, and only for entities that carry
    // Transform + MeshFilter + MeshRenderer + StructurePart as ECS component objects.
    //
    // Shortly after gameplay starts this logs, per projector: its pose, box, texture and whether its entity still
    // carries BakedDecalProjectorComponent (the baker removes it when it has run); then, per part near the projector:
    // material, shader, keyword, the two decal texture slots, the UV channels the baker writes, whether the part's
    // entity matches the baker's query, and what the baker's own tests would say (side test, how many vertices pass
    // the normal test, how many of those land inside the decal's 0..1 square).
    //
    // Output: lines tagged [DecalDiag] in BepInEx/LogOutput.log.  Polling only, no Harmony patches.
    internal static class DecalDiagnostics
    {
        const string Keyword = "_BAKED_DECALS_ON";
        static readonly string[] Slots = { "_BakedDecalTexture01", "_BakedDecalTexture02" };
        static bool s_Ran;

        internal static void OnGameplay()
        {
            if (s_Ran || !Plugin.DecalDiagnosticsEnabled.Value) return;
            s_Ran = true;
            Plugin.Instance.StartCoroutine(Run());
        }

        internal static void OnLeftLevel() => s_Ran = false;

        static void L(string s) => Plugin.Log.LogInfo("[DecalDiag] " + s);

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null && parts.Count < 6; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static IEnumerator Run()
        {
            yield return new WaitForSeconds(Plugin.DecalDiagnosticsDelay.Value);
            try { Dump(); }
            catch (Exception ex) { L("FAILED: " + ex); }
        }

        static void Dump()
        {
            var projectors = UnityEngine.Object.FindObjectsOfType<BakedDecalProjector>();
            var filter = Plugin.DecalDiagnosticsNameFilter.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            L($"projectors in scene: {projectors.Length}; name filter: [{string.Join(", ", filter)}]");

            var world = World.DefaultGameObjectInjectionWorld;
            var baker = world?.GetExistingSystem<DecalBakerSystem>();
            if (baker == null) L("DecalBakerSystem: NOT FOUND in the default world");
            else L($"DecalBakerSystem: Enabled={baker.Enabled}, textures registered={baker.DecalTextureIdMap?.Count ?? -1}" +
                   (baker.DecalTextureIdMap != null ? " [" + string.Join(", ", baker.DecalTextureIdMap.Values.Where(t => t != null).Select(t => t.name).Distinct().Take(20)) + "]" : ""));

            var texField = typeof(BakedDecalProjector).GetField("m_DecalTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            var biasField = typeof(BakedDecalProjector).GetField("m_ProjectionSurfaceBias", BindingFlags.Instance | BindingFlags.NonPublic);
            var parts = UnityEngine.Object.FindObjectsOfType<StructurePart>();
            L($"structure parts in scene: {parts.Length}");

            int shown = 0;
            foreach (var proj in projectors)
            {
                var path = PathOf(proj.transform);
                if (filter.Length > 0 && !filter.Any(f => path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (++shown > Plugin.DecalDiagnosticsMaxProjectors.Value) { L("... more projectors not shown (MaxProjectors)"); break; }

                var tex = texField?.GetValue(proj) as Texture2D;
                float bias = biasField != null ? (float)biasField.GetValue(proj) : -0.25f;
                var tf = proj.transform;
                var ebc = proj.GetComponent<EntityBlueprintComponent>();
                string ent = "no EntityBlueprintComponent";
                if (ebc != null)
                {
                    bool valid = EntityBlueprintComponent.IsValid(ebc);
                    ent = $"EBC valid={valid}";
                    if (valid)
                    {
                        var em = ebc.EntityManager;
                        ent += $", entity={ebc.Entity.Index}, stillHasProjectorComponent={em.HasComponent<BakedDecalProjectorComponent>(ebc.Entity)}" +
                               $", hasTransformObject={em.HasComponent<Transform>(ebc.Entity)}";
                    }
                }
                L($"PROJECTOR {path} | active={proj.gameObject.activeInHierarchy} enabled={proj.enabled} | pos={tf.position:F2} up={tf.up:F2} " +
                  $"size={proj.Size:F2} bias={bias} | texture={(tex != null ? $"{tex.name} {tex.width}x{tex.height} {tex.format} wrap={tex.wrapMode}" : "NULL")} | {ent}");

                // Parts whose renderer bounds meet the projector's box (world AABB of the box, a little generous).
                var half = proj.Size * 0.5f;
                var box = new Bounds(tf.position, Vector3.zero);
                for (int i = 0; i < 8; i++)
                    box.Encapsulate(tf.TransformPoint(new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z)));
                int near = 0;
                foreach (var part in parts)
                {
                    if (part == null) continue;
                    var mr = part.GetComponent<MeshRenderer>();
                    var mf = part.GetComponent<MeshFilter>();
                    if (mr == null || mf == null || !mr.bounds.Intersects(box)) continue;
                    if (++near > Plugin.DecalDiagnosticsMaxParts.Value) { L("   ... more parts not shown (MaxPartsPerProjector)"); break; }
                    DumpPart(part, mr, mf, tf, proj.Size, bias);
                }
                if (near == 0) L("   no structure part's renderer bounds meet this projector's box");
            }
        }

        static void DumpPart(StructurePart part, MeshRenderer mr, MeshFilter mf, Transform proj, Vector3 size, float bias)
        {
            var mesh = mf.sharedMesh;
            string query = "no entity";
            if (EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent))
            {
                var em = part.EntityBlueprintComponent.EntityManager;
                var e = part.Entity;
                query = $"entity={e.Index} bakerQuery(Transform={em.HasComponent<Transform>(e)}, MeshFilter={em.HasComponent<MeshFilter>(e)}, " +
                        $"MeshRenderer={em.HasComponent<MeshRenderer>(e)}, StructurePart={em.HasComponent<StructurePart>(e)})";
            }
            // The baker's side test: a part is skipped when the projector lies mostly "below" the part's pivot.
            float side = Vector3.Dot((proj.position - part.transform.position).normalized, -proj.up);
            L($"   PART {PathOf(part.transform)} | {query} | sideTest={side:F2} (must be < 0.5) | mesh={(mesh != null ? $"{mesh.name} verts={mesh.vertexCount} readable={mesh.isReadable} subMeshes={mesh.subMeshCount}" : "NULL")}");

            var mats = mr.sharedMaterials;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null) { L($"      material[{m}] NULL"); continue; }
                var slots = Slots.Select(s => mat.HasProperty(s) ? $"{s}={(mat.GetTexture(s) != null ? mat.GetTexture(s).name : "none")}" : $"{s}=NO SUCH PROPERTY");
                L($"      material[{m}] {mat.name} | shader={mat.shader.name} | {Keyword}={mat.IsKeywordEnabled(Keyword)} | {string.Join(" | ", slots)}");
            }
            if (mesh == null || !mesh.isReadable) return;

            // What the baker would compute for this mesh: vertices whose normal faces the projector get
            // uv = (x / size.x + 0.5, z / size.z + 0.5) in projector space.
            var verts = new List<Vector3>(); var norms = new List<Vector3>();
            mesh.GetVertices(verts); mesh.GetNormals(norms);
            var toProj = proj.worldToLocalMatrix * part.transform.localToWorldMatrix;
            int facing = 0, inside = 0;
            for (int i = 0; i < verts.Count && i < norms.Count; i++)
            {
                if (Vector3.Dot(toProj.MultiplyVector(norms[i]), Vector3.down) >= bias) continue;
                facing++;
                var p = toProj.MultiplyPoint(verts[i]);
                float u = p.x / size.x + 0.5f, v = p.z / size.z + 0.5f;
                if (u >= 0f && u <= 1f && v >= 0f && v <= 1f) inside++;
            }
            L($"      baker preview: {facing} of {verts.Count} vertices face the projector, {inside} of those fall inside the decal square");
            for (int ch = 2; ch <= 3; ch++)
            {
                var uv = new List<Vector2>();
                mesh.GetUVs(ch, uv);
                if (uv.Count == 0) { L($"      uv channel {ch}: empty (the baker never wrote it, or the mesh in the MeshFilter is not the baked copy)"); continue; }
                int in01 = uv.Count(q => q.x >= 0f && q.x <= 1f && q.y >= 0f && q.y <= 1f && (q.x != 0f || q.y != 0f));
                L($"      uv channel {ch}: {uv.Count} values, x {uv.Min(q => q.x):F2}..{uv.Max(q => q.x):F2}, y {uv.Min(q => q.y):F2}..{uv.Max(q => q.y):F2}, non-zero inside 0..1: {in01}");
            }
        }
    }
}
