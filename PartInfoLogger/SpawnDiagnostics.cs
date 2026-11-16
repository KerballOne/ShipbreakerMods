using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace PartInfoLogger
{
    // Answers "what was jointed at spawn, and what caught fire / leaked / got damaged / moved first?"
    //
    //   joint_census_spawn.csv  one row per StructurePart, written ONCE per ship load shortly after gameplay starts
    //                           (joint_census.csv is rewritten on every return to gameplay, so it only ever shows the
    //                           end-of-session state).
    //   fire_watch.csv          one row the first time a part is seen BURNING / ELECTRIFIED / BURNT / LEAKING /
    //                           DAMAGED (integrity below its spawn value) / MOVED / GONE, polled on a timer for the
    //                           first FireWatchSeconds. The first rows are the origin of whatever went wrong.
    //
    // Pure polling of ECS component presence — no Harmony patches on the game's elemental systems.
    internal static class SpawnDiagnostics
    {
        class Rec
        {
            public StructurePart? Part;
            public Entity Entity;
            public bool HasEntity;
            public string Name = "", Jsa = "", Neighbours = "";
            public int NeighbourCount;
            public Vector3 Pos0;
            public float Integrity0 = -1f, IntegrityMax = -1f, MaxMoved;
            public bool Burning, Electrified, Burnt, Leaking, Damaged, Moved, Gone;
        }

        const float MovedMeters = 0.15f;
        static bool s_Ran;
        static float s_GameplayStart;

        internal static void OnGameplay()
        {
            if (s_Ran || !Plugin.SpawnDiagnosticsEnabled.Value) return;
            s_Ran = true;
            s_GameplayStart = Time.time;
            Plugin.Instance.StartCoroutine(Run());
        }

        // Leaving the level re-arms the one-shot for the next ship.
        internal static void OnLeftLevel() => s_Ran = false;

        static string Csv(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        static string P(Vector3 v) => $"{v.x:F2} {v.y:F2} {v.z:F2}";

        static IEnumerator Run()
        {
            yield return new WaitForSeconds(Plugin.SpawnSnapshotDelay.Value);

            var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
            List<Rec>? recs = null;
            try { recs = Snapshot(outDir); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[SpawnDiagnostics] Snapshot failed: {ex}"); }
            if (recs == null || Plugin.FireWatchSeconds.Value <= 0f) yield break;

            var watchPath = Path.Combine(outDir, "fire_watch.csv");
            try
            {
                File.WriteAllText(watchPath,
                    "SecondsSinceGameplay,Event,PartName,Jsa,WorldPos,Integrity,IntegrityAtSpawn,IntegrityMax,MovedMeters,NeighboursAtSpawn,NeighbourNames\n");
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[SpawnDiagnostics] Cannot write {watchPath}: {ex.Message}"); yield break; }

            int total = 0;
            var interval = Mathf.Max(0.1f, Plugin.FireWatchInterval.Value);
            while (Time.time - s_GameplayStart < Plugin.FireWatchSeconds.Value)
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    int n = Poll(recs, sb);
                    if (n > 0) { File.AppendAllText(watchPath, sb.ToString()); total += n; }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[SpawnDiagnostics] Poll failed: {ex.Message}"); }
                yield return new WaitForSeconds(interval);
            }
            Plugin.Log.LogInfo($"[SpawnDiagnostics] Fire watch finished: {total} event(s) over {Plugin.FireWatchSeconds.Value:F0}s -> {watchPath}");
        }

        static List<Rec> Snapshot(string outDir)
        {
            var parts = Resources.FindObjectsOfTypeAll<StructurePart>()
                .Where(p => p != null && p.gameObject.scene.IsValid()).ToArray();
            var recs = new List<Rec>(parts.Length);
            using var connected = new NativeList<Entity>(Allocator.Temp);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("PartName,Jsa,WorldPos,Integrity,IntegrityMax,JointedNeighbours,NeighbourNames,Flags");
            int isolated = 0;
            foreach (var part in parts)
            {
                var r = new Rec { Part = part, Name = part.gameObject.name.Replace("(Clone)", "").Trim(), Pos0 = part.transform.position };
                r.Jsa = part.StructurePartAsset?.Data?.JointSetupAsset?.name ?? "";
                var flags = new List<string>();
                if (EntityBlueprintComponent.IsValid(part.EntityBlueprintComponent))
                {
                    r.HasEntity = true; r.Entity = part.Entity;
                    var em = part.EntityBlueprintComponent.EntityManager;
                    connected.Clear();
                    Plugin.GetConnectedEntities(em, r.Entity, connected);
                    var names = new List<string>();
                    foreach (var e in connected)
                    {
                        if (e == r.Entity || !em.HasComponent<StructurePart>(e)) continue;
                        r.NeighbourCount++;
                        if (names.Count < 8)
                        {
                            var np = em.GetComponentObject<StructurePart>(e);
                            if (np != null) names.Add(np.gameObject.name.Replace("(Clone)", "").Trim());
                        }
                    }
                    r.Neighbours = string.Join(" | ", names);
                    if (em.HasComponent<ObjectIntegrityComponent>(r.Entity))
                    {
                        var oi = em.GetComponentData<ObjectIntegrityComponent>(r.Entity);
                        r.Integrity0 = oi.Current; r.IntegrityMax = oi.Max;
                    }
                    if (em.HasComponent<BurningComponent>(r.Entity)) { r.Burning = true; flags.Add("BURNING"); }
                    if (em.HasComponent<ElectrifiedComponent>(r.Entity)) { r.Electrified = true; flags.Add("ELECTRIFIED"); }
                    if (em.HasComponent<BurntComponent>(r.Entity)) { r.Burnt = true; flags.Add("BURNT"); }
                    if (em.HasComponent<FlammableComponent>(r.Entity)) flags.Add("flammable");
                    if (em.HasComponent<IgnitesFromElectricity>(r.Entity)) flags.Add("ignitesFromElectricity");
                    if (em.HasComponent<LeakableComponent>(r.Entity)) flags.Add("leakable");
                    if (r.NeighbourCount == 0) isolated++;
                }
                else flags.Add("noEntity");
                recs.Add(r);
                sb.AppendLine($"{Csv(r.Name)},{Csv(r.Jsa)},{P(r.Pos0)},{r.Integrity0:F3},{r.IntegrityMax:F3},{r.NeighbourCount},{Csv(r.Neighbours)},{string.Join(" ", flags)}");
            }
            var path = Path.Combine(outDir, "joint_census_spawn.csv");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo($"[SpawnDiagnostics] Spawn snapshot: {recs.Count} part(s), {isolated} with no jointed neighbour, " +
                               $"{recs.Count(x => x.Burning)} already burning, {recs.Count(x => x.Electrified)} electrified -> {path}");
            return recs;
        }

        static int Poll(List<Rec> recs, System.Text.StringBuilder sb)
        {
            float t = Time.time - s_GameplayStart;
            int n = 0;
            HashSet<Entity>? leakOwners = null;
            EntityManager em = default;
            bool haveEm = false;

            void Emit(Rec r, string ev, float integrity)
            {
                var pos = r.Part != null ? r.Part.transform.position : r.Pos0;
                sb.AppendLine($"{t:F1},{ev},{Csv(r.Name)},{Csv(r.Jsa)},{P(pos)},{integrity:F3},{r.Integrity0:F3},{r.IntegrityMax:F3},{r.MaxMoved:F2},{r.NeighbourCount},{Csv(r.Neighbours)}");
                n++;
            }

            foreach (var r in recs)
            {
                if (r.Gone) continue;
                if (r.Part == null) { r.Gone = true; Emit(r, "GONE", -1f); continue; }
                if (!r.HasEntity) continue;
                if (!haveEm)
                {
                    if (!EntityBlueprintComponent.IsValid(r.Part.EntityBlueprintComponent)) continue;
                    em = r.Part.EntityBlueprintComponent.EntityManager; haveEm = true;
                    leakOwners = new HashSet<Entity>();
                    using var q = em.CreateEntityQuery(ComponentType.ReadOnly<LeakComponent>());
                    using var leaks = q.ToComponentDataArray<LeakComponent>(Allocator.Temp);
                    foreach (var l in leaks) leakOwners.Add(l.Owner);
                }
                if (!em.Exists(r.Entity)) { r.Gone = true; Emit(r, "GONE", -1f); continue; }

                float integ = -1f;
                if (em.HasComponent<ObjectIntegrityComponent>(r.Entity)) integ = em.GetComponentData<ObjectIntegrityComponent>(r.Entity).Current;

                float moved = (r.Part.transform.position - r.Pos0).magnitude;
                if (moved > r.MaxMoved) r.MaxMoved = moved;

                if (!r.Burning && em.HasComponent<BurningComponent>(r.Entity)) { r.Burning = true; Emit(r, "BURNING", integ); }
                if (!r.Electrified && em.HasComponent<ElectrifiedComponent>(r.Entity)) { r.Electrified = true; Emit(r, "ELECTRIFIED", integ); }
                if (!r.Burnt && em.HasComponent<BurntComponent>(r.Entity)) { r.Burnt = true; Emit(r, "BURNT", integ); }
                if (!r.Leaking && leakOwners != null && leakOwners.Contains(r.Entity)) { r.Leaking = true; Emit(r, "LEAKING", integ); }
                if (!r.Damaged && r.Integrity0 >= 0f && integ >= 0f && integ < r.Integrity0 - 0.001f) { r.Damaged = true; Emit(r, "DAMAGED", integ); }
                if (!r.Moved && moved > MovedMeters) { r.Moved = true; Emit(r, "MOVED", integ); }
            }
            return n;
        }
    }
}
