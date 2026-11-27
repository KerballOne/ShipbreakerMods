using System.Collections.Generic;
using BBI.Unity.Game;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.VFX;

namespace TheExpanse
{
    // Destructible stations, step 1: the nearby salvage bays (PRF_CampaingAct1_SupportStation, (1)-(3): low-detail 3D
    // bays 0.7-2.6 km out, each a bay frame, a green barge, six jacks and crates, with a sparks effect; no colliders).
    //   pieces:  every mesh under the station's PRF_SupportStation_01 is a piece. Each gets a box collider (from its
    //            mesh box) so the weapons' rays find it (out there nothing collides physically, but rays still hit),
    //            and health: StationHealth for a 50 m piece, more for bigger ones;
    //   damage:  PDC rounds StationPdcDamage each, a torpedo StationTorpedoDamage to every piece within
    //            StationTorpedoRadius of where it goes off (a big hit shows a small blast); the rail gun's slug breaks
    //            every piece on its line and goes on through;
    //   broken:  an explosion (the reactor core's effect, sized to the piece) and the piece breaks off and drifts away
    //            with the hit's push, turning slowly; when the bay frame goes, the station's sparks go out. Everything
    //            is back at the next level load.
    internal static class StationDamage
    {
        sealed class Piece
        {
            internal Transform T = null!;
            internal Transform Station = null!;
            internal Renderer Renderer = null!;
            internal float Health, Max, Size;
            internal bool Broken;
            internal Vector3 Velocity, Spin;
            internal bool Batched;
            internal bool Anchored;                     // a chunk of the bay frame left standing: it never drifts
        }

        const string ExplosionGuid = "c620190dd56b9b64badf8501cd8939ef";    // FX_Explosion_ReactorCore
        const float AnchorSize = 600f;                                     // m across: pieces this big don't move
        static readonly Dictionary<Collider, Piece> sPieces = new Dictionary<Collider, Piece>();
        static readonly List<Piece> sDrifting = new List<Piece>();
        static bool sSetUp;
        static float sNextLook;
        static GameObject? sExplosionPrefab;
        static bool sExplosionLoading;
        static readonly List<(Vector3 at, float scale)> sPendingBooms = new List<(Vector3, float)>();

        internal static void Reset()
        {
            sPieces.Clear();
            sDrifting.Clear();
            sSetUp = false;
            sNextLook = 0f;
            sDone.Clear();
            sFirstLook = -1f;
        }

        // Every frame (LateUpdate): set the stations up once they are there; move the broken pieces.
        internal static void Frame()
        {
            if (!Plugin.Enabled.Value || !Plugin.StationDamageEnabled.Value) return;
            if (!sSetUp && Time.unscaledTime >= sNextLook) { sNextLook = Time.unscaledTime + 5f; SetUp(); }
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            foreach (var p in sDrifting)
            {
                if (p.T == null) continue;
                p.T.position += p.Velocity * dt;
                p.T.rotation = Quaternion.Euler(p.Spin * dt) * p.T.rotation;
            }
        }

        // Each station once, as it turns up: looked for every 5 s until all four are set up (or for two minutes).
        static readonly HashSet<Transform> sDone = new HashSet<Transform>();
        static float sFirstLook = -1f;
        const int Expected = 4;

        static void SetUp()
        {
            if (sFirstLook < 0f) sFirstLook = Time.unscaledTime;
            int pieces = 0, batched = 0, shaped = 0;
            var names = new List<string>();
            foreach (var root in SceneLookup.Roots())
            {
                if (!root.name.StartsWith("PRF_CampaingAct1_SupportStation") || root.name.Contains("Hab")) continue;
                var station = root.Find("PRF_SupportStation_01");
                if (station == null || sDone.Contains(station)) continue;
                sDone.Add(station);
                names.Add(root.name);
                // the whole station: the bay with its barge, jacks and crates, and the stripped ship beside it
                // (Narrative_MackerelShelledOut_PREFAB)
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    var go = renderer.gameObject;
                    Collider collider;
                    // its real shape when the mesh can be read (shots inside an open bay pass through); else its box
                    if (!renderer.isPartOfStaticBatch && filter.sharedMesh.isReadable)
                    {
                        var mc = go.GetComponent<MeshCollider>();
                        if (mc == null) mc = go.AddComponent<MeshCollider>();
                        mc.sharedMesh = filter.sharedMesh;
                        collider = mc;
                        shaped++;
                    }
                    else
                    {
                        var box = go.GetComponent<BoxCollider>();
                        if (box == null) box = go.AddComponent<BoxCollider>();
                        var local = renderer.isPartOfStaticBatch ? LocalBox(renderer) : filter.sharedMesh.bounds;
                        box.center = local.center;
                        box.size = local.size;
                        collider = box;
                    }
                    float size = renderer.bounds.size.magnitude;
                    float health = Mathf.Max(1f, Plugin.StationHealth.Value) * Mathf.Clamp(size / 50f, 0.3f, 6f);
                    sPieces[collider] = new Piece { T = go.transform, Station = station, Renderer = renderer, Health = health, Max = health, Size = size, Batched = renderer.isPartOfStaticBatch };
                    pieces++;
                    if (renderer.isPartOfStaticBatch) batched++;
                }
            }
            if (names.Count > 0)
                Plugin.Log.LogInfo($"[Stations] {string.Join(", ", names)} made destructible ({sDone.Count} of {Expected} now): {pieces} piece(s), " +
                                   $"{shaped} with their real shape, {pieces - shaped} with a box" +
                                   (batched > 0 ? $", {batched} statically batched (those can only vanish, not drift)" : "") + ".");
            if (sDone.Count >= Expected || Time.unscaledTime - sFirstLook > 120f) sSetUp = true;
        }

        static Bounds LocalBox(Renderer renderer)
        {
            var t = renderer.transform;
            var w = renderer.bounds;
            var b = new Bounds(t.InverseTransformPoint(w.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(t.InverseTransformPoint(w.center + Vector3.Scale(w.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return b;
        }

        // ---- hits from the weapons --------------------------------------------------------------------------------

        // A hit on a collider; true when it is a station piece (the caller then knows it hit a station).
        internal static bool Hit(Collider? c, float damage, Vector3 point, Vector3 dir)
        {
            if (c == null || !sPieces.TryGetValue(c, out var piece)) return false;
            Damage(piece, damage, point, dir);
            return true;
        }

        // The rail gun's slug: every station piece on its line breaks at once; true when it was a station piece.
        internal static bool Pierce(Collider? c, Vector3 point, Vector3 dir)
        {
            if (c == null || !sPieces.TryGetValue(c, out var piece)) return false;
            if (!piece.Broken && piece.T != null) Break(piece, point, dir);
            return true;
        }

        // A blast: every piece whose drawn box is within the radius takes the damage.
        internal static void Blast(Vector3 at, float radius, float damage)
        {
            var hit = new List<Piece>();
            foreach (var p in sPieces.Values)
                if (!p.Broken && p.Renderer != null && (p.Renderer.bounds.ClosestPoint(at) - at).sqrMagnitude <= radius * radius) hit.Add(p);
            foreach (var p in hit) Damage(p, damage, at, (p.T.position - at).normalized);
        }

        static void Damage(Piece p, float damage, Vector3 point, Vector3 dir)
        {
            if (p.Broken || p.T == null || damage <= 0f) return;
            p.Health -= damage;
            // a hit that takes a tenth or more of what it can take shows: a small blast where it struck
            if (damage >= p.Max * 0.1f && p.Health > 0f) Boom(point, Mathf.Clamp(p.Size / 150f, 0.3f, 2f));
            if (p.Health > 0f) return;
            Break(p, point, dir);
        }

        static void Break(Piece p, Vector3 point, Vector3 dir)
        {
            p.Broken = true;
            Boom(point, Mathf.Clamp(p.Size / 25f, 0.5f, 8f));
            try
            {
                // spilled on the hit side, drifting out of the wreck
                var lootMiddle = p.Renderer != null ? p.Renderer.bounds.center : p.T.position;
                var outOf = (point - lootMiddle).sqrMagnitude > 0.01f ? (point - lootMiddle).normalized : -dir.normalized;
                StationLoot.OnBroken(p.T.name, point - dir.normalized * 2f, outOf);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[Stations] loot: " + ex.Message); }
            // the bay frame going puts the station's sparks out
            if (p.T.name == "SM_Bay_GenericBackground_V01")
            {
                var sparks = p.Station.Find("FX_SupportStation");
                if (sparks != null) sparks.gameObject.SetActive(false);
            }
            if (p.Batched)
            {
                // drawn as part of a combined mesh: it can't be moved, only hidden
                var r = p.T.GetComponent<Renderer>();
                if (r != null) r.forceRenderingOff = true;
                foreach (var c in p.T.GetComponents<Collider>()) c.enabled = false;
                Plugin.Log.LogInfo($"[Stations] '{p.T.name}' of '{p.Station.parent.name}' destroyed (batched: hidden).");
                return;
            }
            // the bay's frame itself (the biggest piece by far) is too massive to be shoved: if it can't be cut, it stays
            bool massive = p.Size >= AnchorSize || p.Anchored;
            // shattered with the game's own cutter (the one that splits salvage-ship panels, cut faces filled): the
            // chunks fly apart from the hit
            if (Shatter(p, point, dir)) return;
            if (massive)
            {
                Plugin.Log.LogInfo($"[Stations] '{p.T.name}' of '{p.Station.parent.name}' destroyed ({p.Max:0} health, {p.Size:0} m): too massive to move, it stays.");
                return;
            }
            // else it breaks off whole and drifts: pushed away from the hit, turning slowly; the bigger, the slower
            p.T.SetParent(null, true);
            float speed = Mathf.Max(0f, Plugin.StationDriftSpeed.Value) * Mathf.Clamp(100f / Mathf.Max(1f, p.Size), 0.15f, 1.5f);
            var away = (p.T.position - point).normalized;
            if (away.sqrMagnitude < 0.01f) away = dir;
            p.Velocity = (away * 0.6f + dir.normalized * 0.4f).normalized * speed * Random.Range(0.7f, 1.3f);
            p.Spin = Random.onUnitSphere * Random.Range(8f, 25f) * Mathf.Clamp(50f / Mathf.Max(1f, p.Size), 0.3f, 2f);
            sDrifting.Add(p);
            Plugin.Log.LogInfo($"[Stations] '{p.T.name}' of '{p.Station.parent.name}' destroyed ({p.Max:0} health, {p.Size:0} m): breaks off at {p.Velocity.magnitude:0.0} m/s.");
        }

        // ---- shattering ------------------------------------------------------------------------------------------

        static UvMapperBase? sUv;
        static ColorMapperBase? sColor;
        static Material? sFill;
        const float StaySize = 150f;        // m across: a chunk of the bay frame this big stays where it is
        static bool sMappersLooked;

        // The game's own texture and color mapping for cut faces, from any shatter setup it has loaded.
        static bool Mappers()
        {
            if (!sMappersLooked)
            {
                sMappersLooked = true;
                // a setup whose cut faces get the game's own fill material, a metal one first (the hull panels')
                ShatterableComponentAsset? best = null;
                int bestScore = -1;
                var seen = new List<string>();
                foreach (var asset in Resources.FindObjectsOfTypeAll<ShatterableComponentAsset>())
                {
                    if (asset == null || asset.Data == null || asset.Data.UvMapper == null) continue;
                    var fill = asset.Data.UvMapper.FillMaterial;
                    string names = (asset.name + " " + (fill != null ? fill.name : "")).ToLowerInvariant();
                    int score = fill == null ? 0 : names.Contains("metal") || names.Contains("steel") || names.Contains("hull") ? 3 : names.Contains("panel") ? 2 : 1;
                    if (seen.Count < 12) seen.Add($"{asset.name} (fill '{(fill != null ? fill.name : "none")}')");
                    if (score > bestScore) { bestScore = score; best = asset; }
                }
                if (best != null)
                {
                    sUv = best.Data.UvMapper;
                    sColor = best.Data.ColorMapper;
                    sFill = sUv.FillMaterial;
                    Plugin.Log.LogInfo($"[Stations] cut faces as '{best.name}', fill material '{(sFill != null ? sFill.name : "none")}'; setups seen: {string.Join("; ", seen)}.");
                }
                if (sUv == null) Plugin.Log.LogWarning("[Stations] no shatter setup of the game's loaded: pieces break off whole.");
            }
            return sUv != null;
        }

        // Cuts the piece's mesh with ShatterCuts planes through the area around the hit (each cut splits every chunk
        // it crosses, so up to 2^cuts chunks) with the game's hull splitter (PooledFastHull, as CuttingService does for
        // salvage parts: the cut faces filled). The piece is switched off; each chunk is a new object drifting away from
        // the hit with its own spin. False when the mesh can't be read or cut.
        static bool Shatter(Piece p, Vector3 point, Vector3 dir)
        {
            var filter = p.T.GetComponent<MeshFilter>();
            var renderer = p.T.GetComponent<MeshRenderer>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || renderer == null || !mesh.isReadable || !Mappers()) return false;
            var hulls = new List<PooledFastHull>();
            // the bay's frame (and what is left of it): immovable. Only a box around the hit is broken out of it; the
            // rest stays, as the pieces outside that box
            bool anchored = p.Size >= AnchorSize || p.Anchored;
            int stayed = 0;
            var materials = renderer.sharedMaterials;
            var local = p.T.InverseTransformPoint(point);
            try
            {
                var first = PooledFastHull.Reserve();
                first.RefreshHullFromMesh(mesh, false);
                PooledFastHull? region = first;
                if (anchored)
                {
                    // six cuts, the faces of a box CarveSize across around the hit: each takes off what is outside that
                    // face (it stays, without the cut faces: those would cross the whole frame) and keeps carving the
                    // inside
                    var scale = p.T.lossyScale;
                    float carve = Mathf.Max(5f, Plugin.StationCarveSize.Value) * 0.5f;
                    var half = new Vector3(carve / Mathf.Max(1e-4f, Mathf.Abs(scale.x)), carve / Mathf.Max(1e-4f, Mathf.Abs(scale.y)), carve / Mathf.Max(1e-4f, Mathf.Abs(scale.z)));
                    for (int k = 0; k < 3 && region != null; k++)
                        for (int s = -1; s <= 1 && region != null; s += 2)
                        {
                            var normal = Vector3.zero; normal[k] = s;
                            var at = local + normal * half[k];
                            var a = PooledFastHull.Reserve();
                            var b = PooledFastHull.Reserve();
                            region.Split(at, normal, 0f, true, false, sUv!, sColor!, ref a, ref b);
                            region.Release();
                            region = null;
                            // which side is outside the box: the one whose middle lies past the face
                            PooledFastHull? outside = null, inside = null;
                            foreach (var h in new[] { a, b })
                            {
                                if (h.IsEmpty) { h.Release(); continue; }
                                var m = h.GetMesh();
                                bool past = m != null && Vector3.Dot(m.bounds.center - at, normal) > 0f;
                                if (past && outside == null)
                                {
                                    outside = h;
                                    if (m != null && Stay(p, m, materials)) stayed++;      // Stay keeps the mesh
                                    continue;
                                }
                                if (m != null) Object.Destroy(m);
                                if (inside == null) inside = h; else h.Release();
                            }
                            outside?.Release();
                            region = inside;
                        }
                    if (region == null)
                    {
                        // the box held nothing of the frame: what was cut off stays, nothing flies
                        p.T.gameObject.SetActive(false);
                        Plugin.Log.LogInfo($"[Stations] '{p.T.name}' hit: nothing of it inside the {carve * 2f:0} m box; {stayed} piece(s) left standing.");
                        return true;
                    }
                }
                hulls.Add(region!);
                // the part that breaks up (the whole piece, or the box carved out of the frame): cut at random angles
                var middle = anchored ? local : mesh.bounds.center;
                int cuts = Mathf.Clamp(Plugin.StationShatterCuts.Value - (anchored ? 1 : 0), 1, 4);
                for (int c = 0; c < cuts; c++)
                {
                    var at = Vector3.Lerp(local, middle, Random.Range(0.3f, 0.8f)) + Vector3.Scale(Random.insideUnitSphere * 0.15f, anchored ? Vector3.one * 5f : mesh.bounds.size);
                    var normal = Random.onUnitSphere;
                    int count = hulls.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var h = hulls[0];
                        hulls.RemoveAt(0);
                        var a = PooledFastHull.Reserve();
                        var b = PooledFastHull.Reserve();
                        h.Split(at, normal, 0f, true, false, sUv!, sColor!, ref a, ref b);
                        h.Release();
                        if (a.IsEmpty) a.Release(); else hulls.Add(a);
                        if (b.IsEmpty) b.Release(); else hulls.Add(b);
                    }
                }
                if (hulls.Count < 2 && !anchored) { foreach (var h in hulls) h.Release(); return false; }
                float speed = Mathf.Max(0f, Plugin.StationDriftSpeed.Value) * Mathf.Clamp(100f / Mathf.Max(1f, anchored ? 20f : p.Size), 0.3f, 1.5f);
                int flying = 0;
                foreach (var h in hulls)
                {
                    var chunkMesh = h.GetMesh();
                    h.Release();
                    if (chunkMesh == null) continue;
                    Fly(p, chunkMesh, materials, point, dir, speed);
                    flying++;
                }
                p.T.gameObject.SetActive(false);
                Plugin.Log.LogInfo($"[Stations] '{p.T.name}' of '{p.Station.parent.name}' destroyed ({p.Max:0} health, {p.Size:0} m): " +
                                   (anchored ? $"a {Plugin.StationCarveSize.Value:0} m piece broke out into {flying} chunk(s), {stayed} part(s) of it left standing (they can be shot again)."
                                             : $"shattered into {flying} chunk(s)."));
                return true;
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[Stations] could not shatter '{p.T.name}': {ex.Message}; it breaks off whole.");
                return false;
            }
        }

        // What is left of the frame after a carve: stays where it is, with its own materials only (the cut faces of
        // the frame-wide cuts dropped), a collider and health, so it can be shot again.
        static bool Stay(Piece p, Mesh m, Material[] materials)
        {
            if (m.subMeshCount > materials.Length) m.subMeshCount = materials.Length;
            if (m.vertexCount == 0) { Object.Destroy(m); return false; }
            var go = new GameObject(p.T.name.Replace("_Chunk", "").Replace("_Left", "") + "_Left");
            go.transform.SetPositionAndRotation(p.T.position, p.T.rotation);
            go.transform.localScale = p.T.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = m;
            float size = Vector3.Scale(m.bounds.size, go.transform.lossyScale).magnitude;
            float health = Mathf.Max(1f, Plugin.StationHealth.Value) * Mathf.Clamp(size / 50f, 0.3f, 6f);
            sPieces[mc] = new Piece { T = go.transform, Station = p.Station, Renderer = r, Health = health, Max = health, Size = size, Anchored = true };
            return true;
        }

        // A broken-off chunk: drifts away from the hit and tumbles about its own middle (its mesh hangs under a pivot
        // at its middle: turning the piece's own pivot made chunks swing round the old piece's origin).
        static void Fly(Piece p, Mesh chunkMesh, Material[] materials, Vector3 point, Vector3 dir, float speed)
        {
            var center = p.T.TransformPoint(chunkMesh.bounds.center);
            var pivot = new GameObject(p.T.name.Replace("_Left", "") + "_Chunk").transform;
            pivot.position = center;
            var go = new GameObject("Mesh");
            go.transform.SetParent(pivot, false);
            go.transform.SetPositionAndRotation(p.T.position, p.T.rotation);
            go.transform.localScale = p.T.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = chunkMesh;
            var r = go.AddComponent<MeshRenderer>();
            // the piece's own materials; the cut faces (the extra submesh at the end) the game's fill material
            var mats = new Material[Mathf.Max(1, chunkMesh.subMeshCount)];
            for (int i = 0; i < mats.Length; i++)
                mats[i] = i >= materials.Length && sFill != null ? sFill : materials.Length > 0 ? materials[Mathf.Min(i, materials.Length - 1)] : null!;
            r.sharedMaterials = mats;
            var away = (center - point).normalized;
            if (away.sqrMagnitude < 0.01f) away = Random.onUnitSphere;
            sDrifting.Add(new Piece
            {
                T = pivot, Station = p.Station, Renderer = r, Broken = true, Size = Vector3.Scale(chunkMesh.bounds.size, p.T.lossyScale).magnitude,
                Velocity = (away * 0.7f + dir.normalized * 0.3f).normalized * speed * Random.Range(0.6f, 1.4f),
                Spin = Random.onUnitSphere * Random.Range(10f, 40f),
            });
        }

        // ---- the explosion -----------------------------------------------------------------------------------------

        static void Boom(Vector3 at, float scale)
        {
            if (sExplosionPrefab != null) { Spawn(at, scale); return; }
            sPendingBooms.Add((at, scale));
            if (sExplosionLoading) return;
            sExplosionLoading = true;
            Addressables.LoadAssetAsync<GameObject>(ExplosionGuid).Completed += handle =>
            {
                sExplosionLoading = false;
                if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null) { sPendingBooms.Clear(); return; }
                sExplosionPrefab = handle.Result;
                foreach (var (a, s) in sPendingBooms) Spawn(a, s);
                sPendingBooms.Clear();
            };
        }

        static void Spawn(Vector3 at, float scale)
        {
            var boom = Object.Instantiate(sExplosionPrefab!, at, Quaternion.identity);
            boom.name = "TheExpanse_StationBlast";
            boom.transform.localScale = Vector3.one * scale;
            boom.SetActive(true);
            foreach (var effect in boom.GetComponentsInChildren<VisualEffect>(true))
            { effect.enabled = true; effect.Reinit(); effect.Play(); effect.SendEvent("Start"); effect.SendEvent("OnPlay"); }
            foreach (var system in boom.GetComponentsInChildren<ParticleSystem>(true)) system.Play(true);
            Object.Destroy(boom, 12f);
        }
    }
}
