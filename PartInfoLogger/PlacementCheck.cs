using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace PartInfoLogger
{
    // Verifies editor-placed addressable parts actually spawned in-game with the renderer meshes and
    // colliders their game prefab is expected to contain.
    //
    // Input:  placement_manifest.json (next to OutputPath), written by the Shipbuilder editor's ClaudeBridge
    //         "manifest" command: one entry per placed AddressableLoader, with the meshes/colliders of the
    //         prefab it points at.
    // Output: placement_check.csv (one row per entry), placement_check_debug.txt (hierarchy samples), and a
    //         one-line summary in the log.
    //
    // Matching: each entry is matched to its runtime AddressableLoader stub by assetGUID + GO name
    // (disambiguated by parent/grandparent names). ModdedShipLoader instantiates the content as a child of
    // the stub, but the game then moves the mesh-bearing part GOs out from under it (first in-game run:
    // every stub had exactly one child and zero renderers beneath it, while the joint census showed the
    // parts alive). So expected meshes are looked up under the stub first, then scene-wide by mesh name,
    // taking the nearest unclaimed instance to the stub; the distance is reported as a drift indicator.
    // Extra runtime renderers/colliders (hardpoint spawns) are never flagged.
    internal static class PlacementCheck
    {
        class Manifest { public string? ship; public List<Entry>? entries; }
        class Entry
        {
            public string? key, name, group, deck, guid, asset;
            public float[]? shipLocalPos;
            public List<R>? renderers;
            public List<C>? colliders;
        }
        class R { public string? p, m; public int v; }
        class C { public string? p, t, m; public bool convex, trigger; }

        const float DisplacedMeters = 6f;

        static string Clean(string s) => s.Replace("(Clone)", "").Trim();
        static string MeshName(Mesh? m) => m == null ? "" : m.name.Replace(" Instance", "");
        static string ColKey(Collider c) => c.GetType().Name + ":" + (c is MeshCollider mc ? MeshName(mc.sharedMesh) : "");

        static string PathOf(Transform t)
        {
            var p = t.name;
            for (var c = t.parent; c != null; c = c.parent) p = c.name + "/" + p;
            return p;
        }

        internal static IEnumerator Run(float delaySeconds)
        {
            yield return new WaitForSeconds(delaySeconds);

            var outDir = Path.GetDirectoryName(Plugin.OutputPath.Value)!;
            var manifestPath = Path.Combine(outDir, "placement_manifest.json");
            if (!File.Exists(manifestPath))
            {
                Plugin.Log.LogInfo($"[PlacementCheck] No manifest at {manifestPath} — skipping.");
                yield break;
            }

            try { Check(manifestPath, outDir); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[PlacementCheck] Failed: {ex}"); }
        }

        // Nearest unclaimed item to `from` in the bucket for `key`; claims it.
        static T? Take<T>(Dictionary<string, List<T>> index, HashSet<T> used, string key, Vector3 from, out float dist)
            where T : Component
        {
            dist = -1f;
            if (!index.TryGetValue(key, out var bucket)) return null;
            T? best = null; float bestD = float.MaxValue;
            foreach (var c in bucket)
            {
                if (c == null || used.Contains(c)) continue;
                var d = (c.transform.position - from).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best != null) { used.Add(best); dist = Mathf.Sqrt(bestD); }
            return best;
        }

        static void Check(string manifestPath, string outDir)
        {
            var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath));
            var entries = manifest?.entries ?? new List<Entry>();

            var loaderType = AccessTools.TypeByName("BBI.Unity.Game.AddressableLoader");
            var guidField = loaderType?.GetField("assetGUID");
            if (loaderType == null || guidField == null)
            {
                Plugin.Log.LogWarning("[PlacementCheck] AddressableLoader type not found — is ModdedShipLoader installed?");
                return;
            }

            var loaders = Resources.FindObjectsOfTypeAll(loaderType)
                .OfType<Component>()
                .Where(c => c != null && c.gameObject.scene.IsValid())
                .ToList();
            var byGuid = new Dictionary<string, List<Component>>();
            foreach (var l in loaders)
            {
                var g = guidField.GetValue(l) as string ?? "";
                if (!byGuid.TryGetValue(g, out var list)) byGuid[g] = list = new List<Component>();
                list.Add(l);
            }

            // Scene-wide indexes by mesh name / collider key.
            var rIndex = new Dictionary<string, List<MeshRenderer>>();
            foreach (var mr in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (mr == null || !mr.gameObject.scene.IsValid()) continue;
                var mf = mr.GetComponent<MeshFilter>();
                var key = MeshName(mf != null ? mf.sharedMesh : null);
                if (key.Length == 0) continue;
                if (!rIndex.TryGetValue(key, out var b)) rIndex[key] = b = new List<MeshRenderer>();
                b.Add(mr);
            }
            var cIndex = new Dictionary<string, List<Collider>>();
            foreach (var col in Resources.FindObjectsOfTypeAll<Collider>())
            {
                if (col == null || !col.gameObject.scene.IsValid()) continue;
                var key = ColKey(col);
                if (!cIndex.TryGetValue(key, out var b)) cIndex[key] = b = new List<Collider>();
                b.Add(col);
            }
            var usedR = new HashSet<MeshRenderer>(); var usedC = new HashSet<Collider>();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Status,Deck,Group,Name,Asset,ExpRenderers,FoundRenderers,VisibleRenderers,UnderStub,MissingRenderMeshes," +
                          "BadMaterials,ExpColliders,FoundColliders,EnabledColliders,MissingColliders,MaxDistFromStub,StubChildren,StubWorldPos,Notes");
            var dbg = new System.Text.StringBuilder();
            var dbgDone = new HashSet<string>();
            var counts = new Dictionary<string, int>();
            int matched = 0;
            var usedStubs = new HashSet<Component>();

            // Pass 1: resolve stubs and claim everything still parented under them, so the scene-wide
            // pass can't hand one entry's renderers to a neighbouring identical part.
            var stubs = new Component?[entries.Count];
            var notesArr = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i]; notesArr[i] = "";
                if (e.guid == null || !byGuid.TryGetValue(e.guid, out var cands)) continue;
                var c = cands.Where(l => !usedStubs.Contains(l) && Clean(l.gameObject.name) == e.name).ToList();
                if (c.Count > 1)
                {
                    var c2 = c.Where(l => l.transform.parent != null && Clean(l.transform.parent.name) == e.group).ToList();
                    if (c2.Count > 0) c = c2;
                }
                if (c.Count > 1)
                {
                    var c3 = c.Where(l => l.transform.parent?.parent != null && Clean(l.transform.parent.parent.name) == e.deck).ToList();
                    if (c3.Count > 0) c = c3;
                }
                if (c.Count > 1) notesArr[i] = $"{c.Count} candidate stubs; took first";
                stubs[i] = c.FirstOrDefault();
                if (stubs[i] != null) { usedStubs.Add(stubs[i]!); matched++; }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var stub = stubs[i];
                string status, notes = notesArr[i];
                int foundR = 0, visR = 0, under = 0, foundC = 0, enC = 0, childCount = 0;
                float maxDist = 0f;
                var missingR = new List<string>(); var missingC = new List<string>(); var bad = new List<string>();
                string worldPos = "";
                int expR = e.renderers?.Count ?? 0, expC = e.colliders?.Count ?? 0;

                if (stub == null)
                {
                    status = "MISSING_STUB";
                    notes = e.guid != null && byGuid.ContainsKey(e.guid)
                        ? "guid present in scene but no stub with this name" : "no AddressableLoader with this guid in scene";
                }
                else
                {
                    var t = stub.transform;
                    childCount = t.childCount;
                    worldPos = t.position.ToString("F2");

                    // Under-stub candidates first (distance 0), then scene-wide nearest.
                    var local = new Dictionary<string, List<MeshRenderer>>();
                    foreach (var mr in t.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        var key = MeshName(mf != null ? mf.sharedMesh : null);
                        if (!local.TryGetValue(key, out var b)) local[key] = b = new List<MeshRenderer>();
                        b.Add(mr);
                    }
                    MeshRenderer? firstGlobal = null; float firstGlobalDist = 0f;
                    foreach (var r in e.renderers ?? new List<R>())
                    {
                        var key = r.m ?? "";
                        var mr = Take(local, usedR, key, t.position, out var d);
                        if (mr != null) under++;
                        else
                        {
                            mr = Take(rIndex, usedR, key, t.position, out d);
                            if (mr != null && firstGlobal == null) { firstGlobal = mr; firstGlobalDist = d; }
                        }
                        if (mr == null) { missingR.Add(key); continue; }
                        foundR++;
                        if (d > maxDist) maxDist = d;
                        if (mr.enabled && mr.gameObject.activeInHierarchy) visR++;
                        foreach (var mat in mr.sharedMaterials)
                        {
                            if (mat == null) bad.Add($"{key}:null");
                            else if (mat.shader == null || mat.shader.name.Contains("InternalErrorShader")) bad.Add($"{key}:{mat.name}");
                        }
                    }

                    var localC = new Dictionary<string, List<Collider>>();
                    foreach (var col in t.GetComponentsInChildren<Collider>(true))
                    {
                        var key = ColKey(col);
                        if (!localC.TryGetValue(key, out var b)) localC[key] = b = new List<Collider>();
                        b.Add(col);
                    }
                    foreach (var c in e.colliders ?? new List<C>())
                    {
                        var key = (c.t ?? "") + ":" + (c.m ?? "");
                        var col = Take(localC, usedC, key, t.position, out var d) ?? Take(cIndex, usedC, key, t.position, out d);
                        if (col == null) { missingC.Add(key); continue; }
                        foundC++;
                        if (d > maxDist) maxDist = d;
                        if (col.enabled && col.gameObject.activeInHierarchy) enC++;
                    }

                    if (expR == 0 && expC == 0) status = "NO_EXPECTATIONS";
                    else if (childCount == 0 && foundR == 0) status = "NOT_LOADED";
                    else if (missingR.Count > 0) status = "RENDER_MISSING";
                    else if (bad.Count > 0) status = "BAD_MATERIAL";
                    else if (visR < expR) status = "HIDDEN";
                    else if (missingC.Count > 0) status = "COLLIDER_MISSING";
                    else if (enC < expC) status = "COLLIDER_DISABLED";
                    else if (maxDist > DisplacedMeters) status = "DISPLACED";
                    else status = under == expR ? "OK" : "OK_REPARENTED";

                    // One hierarchy sample per asset so the matching assumptions can be audited.
                    if (e.asset != null && dbgDone.Add(e.asset))
                    {
                        dbg.AppendLine($"=== {e.asset}  [{status}]  stub: {PathOf(t)}");
                        DumpTree(t, dbg, 1, 3);
                        if (firstGlobal != null)
                            dbg.AppendLine($"  first scene-wide match ({firstGlobalDist:F2} m): {PathOf(firstGlobal.transform)}  " +
                                           $"[{string.Join(",", firstGlobal.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))}]");
                        dbg.AppendLine();
                    }
                }

                counts[status] = counts.TryGetValue(status, out var n) ? n + 1 : 1;
                sb.AppendLine(string.Join(",", new[]
                {
                    status, Csv(e.deck), Csv(e.group), Csv(e.name), Csv(e.asset),
                    expR.ToString(), foundR.ToString(), visR.ToString(), under.ToString(), Csv(string.Join(" | ", missingR.Distinct())),
                    Csv(string.Join(" | ", bad.Distinct())),
                    expC.ToString(), foundC.ToString(), enC.ToString(), Csv(string.Join(" | ", missingC.Distinct())),
                    maxDist.ToString("F2"), childCount.ToString(), Csv(worldPos), Csv(notes),
                }));
            }

            if (matched == 0)
            {
                Plugin.Log.LogInfo($"[PlacementCheck] None of {entries.Count} manifest entries found among {loaders.Count} " +
                                   $"loader stubs — ship '{manifest?.ship}' is probably not the one loaded. Nothing written.");
                return;
            }

            var path = Path.Combine(outDir, "placement_check.csv");
            File.WriteAllText(path, sb.ToString());
            File.WriteAllText(Path.Combine(outDir, "placement_check_debug.txt"), dbg.ToString());
            var summary = string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
            Plugin.Log.LogInfo($"[PlacementCheck] {entries.Count} entries: {summary}. Wrote {path}");
        }

        static void DumpTree(Transform t, System.Text.StringBuilder sb, int depth, int maxDepth)
        {
            foreach (Transform c in t)
            {
                var comps = string.Join(",", c.GetComponents<Component>().Where(x => x != null && !(x is Transform)).Select(x => x.GetType().Name));
                sb.AppendLine($"{new string(' ', depth * 2)}{c.name} [{comps}] children={c.childCount}{(c.gameObject.activeInHierarchy ? "" : " INACTIVE")}");
                if (depth < maxDepth) DumpTree(c, sb, depth + 1, maxDepth);
            }
        }

        static string Csv(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
