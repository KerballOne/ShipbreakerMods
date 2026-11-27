using System.Collections.Generic;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using UnityEngine;

namespace TheExpanse
{
    // The ship's weapons, found by the Corvette's part names: PDC turrets and barrels, torpedo tubes, their doors and the
    // torpedoes in the feed trays, the rail gun. Step 1 only lists what is there, once per level, to fit the turning
    // pieces to the real model.
    internal static class WeaponMounts
    {
        static bool sLogged;
        static readonly Regex Wanted = new Regex(@"pdc|torp|railgun|rail_gun|tube|door", RegexOptions.IgnoreCase);
        static readonly Regex NotWanted = new Regex(@"_New_|NestPanel|Ammo|Poster|PowerGenerator|Chock|TubeSeg|TubeCollar|railing|crawl|airlock|cockpit|hatch_basic|PRF_Door_Basic|SM_Door_Gate|Door_Hatch", RegexOptions.IgnoreCase);
        static readonly Regex Copy = new Regex(@"\s*\(\d+\)$");

        static readonly Regex PdcPart = new Regex(@"^PDC_(.+?)_(Body_01|Barrel_01|New_Truss)$", RegexOptions.IgnoreCase);

        internal static void Reset() { sLogged = false; Pdc.Reset(); }

        // The ship's structure was rebuilt under the turrets (they were found too early): find everything again,
        // keeping torpedoes already in flight.
        internal static void Rescan()
        {
            Plugin.Log.LogInfo("[Weapons] weapon mounts moved; looking for them again.");
            Pdc.Lost = false;
            Pdc.Reset();
            Torpedo.ResetTubes();
            sLogged = false;
        }

        internal static void LogOnce()
        {
            if (sLogged) return;
            sLogged = true;
            var groups = new SortedDictionary<string, List<Transform>>(System.StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<Transform>();
            var bodies = new Dictionary<string, Transform>();
            var barrels = new Dictionary<string, Transform>();
            var trusses = new Dictionary<string, Transform>();
            var shipParts = new List<StructurePart>();
            foreach (var part in Object.FindObjectsOfType<StructurePart>())
            {
                if (!FlightModel.IsShipPart(part)) continue;
                shipParts.Add(part);
                StationLoot.NoteShipPart(part);
                foreach (var t in part.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || !seen.Add(t)) continue;
                    if (t.name == "Railgun_Muzzle") RailGun.SetMuzzle(t);
                    Torpedo.Note(t);
                    Ammo.Note(t);
                    var pdc = PdcPart.Match(t.name);
                    if (pdc.Success)
                    {
                        string piece = pdc.Groups[2].Value.ToLowerInvariant();
                        (piece == "body_01" ? bodies : piece == "barrel_01" ? barrels : trusses)[pdc.Groups[1].Value] = t;
                    }
                    if (!Wanted.IsMatch(t.name) || NotWanted.IsMatch(t.name)) continue;
                    string key = Copy.Replace(t.name, "");
                    if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Transform>();
                    list.Add(t);
                }
            }
            foreach (var pair in bodies)
                if (barrels.TryGetValue(pair.Key, out var barrel) && trusses.TryGetValue(pair.Key, out var truss)) Pdc.Add(pair.Key, truss, pair.Value, barrel);
                else Plugin.Log.LogWarning($"[Weapons] PDC '{pair.Key}': body without {(barrels.ContainsKey(pair.Key) ? "" : "a barrel ")}{(trusses.ContainsKey(pair.Key) ? "" : "a truss")}; not driven.");
            Pdc.LogNoneOnce();
            Torpedo.Setup();
            var pdcNames = new List<string>();
            foreach (var turret in Pdc.Turrets) pdcNames.Add(turret.Name);
            Ammo.Setup(pdcNames, shipParts);
            StationLoot.ShipScanned();
            int total = 0;
            foreach (var list in groups.Values) total += list.Count;
            Plugin.Log.LogInfo($"[Weapons] {total} weapon object(s) on the ship in {groups.Count} name group(s):");
            int lines = 0;
            foreach (var pair in groups)
            {
                if (++lines > 80) { Plugin.Log.LogInfo("[Weapons]  ... (more not listed)"); break; }
                var first = pair.Value[0];
                var part = first.GetComponent<StructurePart>();
                var body = first.GetComponentInParent<Rigidbody>();
                var mesh = first.GetComponent<MeshFilter>();
                string size = mesh != null && mesh.sharedMesh != null ? $" mesh {Vector3.Scale(mesh.sharedMesh.bounds.size, first.lossyScale)}" : "";
                Plugin.Log.LogInfo($"[Weapons]  '{pair.Key}' x{pair.Value.Count}: e.g. '{Path(first, 5)}' at {first.position}" +
                                   $"{(part != null ? " (structure part)" : "")}{size}; body '{(body != null ? body.name : "none")}'" +
                                   $"{(body != null ? $" ({body.GetComponents<Joint>().Length} joint(s))" : "")}; children {first.childCount}");
            }
        }

        static string Path(Transform t, int depth)
        {
            var names = new List<string>();
            for (int i = 0; t != null && i < depth; t = t.parent, i++) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
